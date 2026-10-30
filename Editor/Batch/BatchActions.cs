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
        /// Contract v9 for a batch: one combined dialog before it starts lists every item that has an old Assets install
        /// (config table and markers; the zip-based rules need the download, so they cannot be listed yet). "Do not
        /// install" leaves only those items out. An item that turns up another old install after its download is not
        /// moved without consent: it fails with the "old install needs clean-up" text and waits for a click.
        /// </summary>
        public static List<BatchItem> InstallAll(string sessionToken, IList<LicenseVersionEntry> versions, LicenseEntry[] licenses,
            CancellationToken cancel = default(CancellationToken), Func<LegacyPlan, bool> confirmBatch = null)
        {
            var root = PackageInstallService.ProjectRoot;
            var asked = confirmBatch ?? LegacyInstallDialog.ConfirmBatch;
            var flagged = new Dictionary<LicenseVersionEntry, LegacyPlan>();
            var combined = new LegacyPlan();
            foreach (var version in versions)
            {
                var probes = PackageInstallService.ProbesFor(root, new List<InstallItem> { new InstallItem { Name = version.package_name, Version = version.package_version } }, version, licenses);
                var plan = LegacyInstall.DetectKnown(root, probes);
                if (plan.IsEmpty) continue;
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
        /// </summary>
        public static async Task RunAsync(BatchQueue queue)
        {
            EditorApplication.LockReloadAssemblies();
            try
            {
                await queue.RunAsync();
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }
        }
    }
}
