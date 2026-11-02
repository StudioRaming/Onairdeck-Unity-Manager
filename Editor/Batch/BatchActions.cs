using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace OnAirDeck.UnityManager
{
    /// <summary>Unity-side actions for <see cref="BatchQueue"/>: downloads and package installs.</summary>
    internal static class BatchActions
    {
        /// <summary>
        /// A purchase or own-product download. Unity packages are imported without the import window, because a batch
        /// must not stop at one dialog per package (a single download from the window keeps the dialog).
        /// </summary>
        public static BatchItem Download(string sessionToken, PurchaseItem item, CancellationToken cancel = default(CancellationToken))
        {
            return new BatchItem
            {
                Name = item.name,
                Version = DownloadService.VersionOf(item),
                Run = async progress => (object)await DownloadService.DownloadItemAsync(sessionToken, item, progress, cancel, false)
            };
        }

        /// <summary>Installs or updates one license package version (with its exact dependencies, in one transaction).</summary>
        public static BatchItem Install(string sessionToken, LicenseVersionEntry version, CancellationToken cancel = default(CancellationToken), LicenseEntry[] licenses = null,
            Func<LegacyPlan, bool> confirmLegacy = null)
        {
            return new BatchItem
            {
                Name = version.package_name + " " + version.package_version,
                Version = version.package_version,
                Run = async progress => (object)await PackageInstallService.InstallAsync(sessionToken, version, progress, cancel, licenses, true, confirmLegacy)
            };
        }

        /// <summary>
        /// A license version that is a file, not a package (Ming Light Controller's .unitypackage), placed the way a click places it. An
        /// add-on of a core (CoreAddons) runs only when its core is here by then (installed earlier in the run or before): otherwise it fails
        /// with the reason (AddonNeedsCoreException) and nothing is downloaded.
        /// </summary>
        public static BatchItem InstallFile(string sessionToken, LicenseEntry license, LicenseVersionEntry version, LicenseEntry[] licenses,
            CancellationToken cancel = default(CancellationToken))
        {
            return new BatchItem
            {
                Name = FileItemName(version),
                Version = version.version_label ?? "",
                Run = async progress =>
                {
                    var core = CoreAddons.CoreOf("", license != null ? license.license_id : "");
                    if (core.Length > 0 && !CoreAddons.CoreIsHere(PackageInstallService.ProjectRoot, core))
                        throw new AddonNeedsCoreException(core, "Needs " + CoreAddons.CoreName(core) + ", which is not installed in this project; nothing was installed.");
                    return (object)await LicenseVersionInstall.InstallAsync(sessionToken, license, version, licenses, progress, cancel, false, true);
                }
            };
        }

        /// <summary>The item name of <see cref="InstallFile"/> ("file:&lt;version id&gt;"): never a package name, so package lookups do not match it.</summary>
        public static string FileItemName(LicenseVersionEntry version)
        {
            return "file:" + (version != null ? version.version_id : "");
        }

        /// <summary>
        /// Contract v9 for a batch: one combined dialog before it starts lists every item that has an old Assets install
        /// (config table and markers; the zip-based rules need the download, so they cannot be listed yet). "Do not
        /// install" leaves only those items out. An item that turns up another old install after its download is not
        /// moved without consent: it fails with the "old install needs clean-up" text and waits for a click. An item whose own old folder
        /// nobody can clean up (<see cref="LegacyInstall.RefusalBeforeConfirm"/>) is not in the dialog: it fails at once with what to do.
        /// </summary>
        public static List<BatchItem> InstallAll(string sessionToken, IList<LicenseVersionEntry> versions, LicenseEntry[] licenses,
            CancellationToken cancel = default(CancellationToken), Func<LegacyPlan, bool> confirmBatch = null)
        {
            var root = PackageInstallService.ProjectRoot;
            var asked = confirmBatch ?? LegacyInstallDialog.ConfirmBatch;
            var flagged = new Dictionary<LicenseVersionEntry, LegacyPlan>();
            var refused = new Dictionary<LicenseVersionEntry, LegacyCleanupRequiredException>();
            var combined = new LegacyPlan();
            foreach (var version in versions)
            {
                var probeItems = new List<InstallItem> { new InstallItem { Name = version.package_name, Version = version.package_version } };
                // An add-on brings its core (the URP add-on pins it), so the core's old Assets folder is asked about here too: otherwise
                // it turns up only after the download, is not in the approved set and the item ends as "declined".
                var core = CoreAddons.CoreOf(version.package_name, "");
                if (core.Length > 0) probeItems.Insert(0, new InstallItem { Name = core, Version = "" });
                var probes = PackageInstallService.ProbesFor(root, probeItems, version, licenses);
                foreach (var probe in probes) if (probe.Name == core) probe.Display = CoreAddons.CoreName(core);
                var plan = LegacyInstall.DetectKnown(root, probes);
                if (plan.IsEmpty) continue;
                // The item's own old folder can never be cleaned up (a working copy, or unlisted files with no installer): it fails with
                // that reason at once, downloads nothing and is not part of the question.
                var own = new LegacyPlan();
                own.Items.AddRange(plan.Items.FindAll(x => x.Owner != null && x.Owner.Name == version.package_name));
                var refusal = LegacyInstall.RefusalBeforeConfirm(own);
                if (refusal != null)
                {
                    refused[version] = refusal;
                    continue;
                }
                flagged[version] = plan;
                foreach (var item in plan.Items)
                    if (!item.Blocked && !item.HandOff && !combined.Items.Exists(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase))) combined.Items.Add(item);
            }
            var approved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var move = combined.IsEmpty || asked(combined);
            if (move) foreach (var item in combined.Items) approved.Add(item.Path);
            var items = new List<BatchItem>();
            foreach (var version in versions)
            {
                LegacyCleanupRequiredException refusal;
                if (refused.TryGetValue(version, out refusal))
                {
                    var reason = refusal;
                    items.Add(new BatchItem
                    {
                        Name = version.package_name + " " + version.package_version,
                        Version = version.package_version,
                        Run = progress => { throw reason; }
                    });
                    continue;
                }
                if (!move && flagged.ContainsKey(version)) continue;   // "Do not install": this item is skipped, nothing changes
                // A product installer hand-off (import, reload, wait) is one click at a time: a batch never starts one.
                items.Add(Install(sessionToken, version, cancel, licenses, plan =>
                {
                    var handoff = plan.HandoffItem();
                    if (handoff != null)
                        throw new LegacyCleanupRequiredException(LegacyInstallText.HandoffUnavailable(LegacyInstallText.Language(), handoff.Product, handoff.Path, handoff.Owner != null && handoff.Owner.Installer != null));
                    return plan.IsCoveredBy(approved);
                }));
            }
            return items;
        }

        /// <summary>
        /// Runs the queue with script reloads held back. Every install ends with a refresh, and a reload in the middle
        /// would drop the rest of the queue; the reload happens once when the batch ends (or is cancelled).
        /// <paramref name="beforeUnlock"/> runs while reloads are still held: the code awaiting this task may never resume (the reload can
        /// come first), so whatever must happen after the batch (its summary, the per-item reasons) happens there.
        /// </summary>
        public static async Task RunAsync(BatchQueue queue, Action beforeUnlock = null)
        {
            EditorApplication.LockReloadAssemblies();
            try
            {
                await queue.RunAsync();
            }
            finally
            {
                try { if (beforeUnlock != null) beforeUnlock(); }
                catch (Exception e) { UnityEngine.Debug.LogWarning(OnAirDeckConfig.LogPrefix + "After the batch: " + e.Message); }
                EditorApplication.UnlockReloadAssemblies();
            }
        }
    }
}
