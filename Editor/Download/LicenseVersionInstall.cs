using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Installs one non-VPM version of a seller license (contract v8): a .unitypackage (DRM add-ons shipped in their BOOTH
    /// form) or a zip of loose files. VPM zips (<see cref="LicenseVersionEntry.IsPackage"/>) go through
    /// <see cref="PackageInstallService"/> instead.
    /// </summary>
    internal static class LicenseVersionInstall
    {
        /// <summary>
        /// Downloads the chosen version by its id (plugin-download { license_version_id }, an active license grant is required)
        /// and places it:
        /// <list type="bullet">
        /// <item>.unitypackage: queued in <see cref="PackageImportQueue"/>; Unity keeps the package's paths and GUIDs and overwrites
        /// files in place, nothing is deleted. <paramref name="interactiveImport"/> false (default) imports without Unity's import
        /// window, so the update overwrites the old install the way contract v8 describes.</item>
        /// <item>zip or loose files: extracted or copied into Assets/StudioRaming_Onairdeck/&lt;license name&gt;.</item>
        /// <item>the signed onairdeck.license is issued right after (a user click when <paramref name="userInitiated"/>); a failure
        /// there does not undo the install (<see cref="DownloadSummary.LicenseRefused"/>).</item>
        /// </list>
        /// The marker atelier.product.json is part of the product's own package (the shared lock template adds it); the Manager does
        /// not write one. A device-limit, device-released or update-required refusal goes to <see cref="DeviceLimitNotice"/> (its
        /// retry installs the version again) and is thrown; any server refusal is recorded in <see cref="LicenseRenewalStatus"/>.
        /// </summary>
        public static async Task<DownloadSummary> InstallAsync(
            string sessionToken, LicenseEntry license, LicenseVersionEntry version, LicenseEntry[] licenses,
            Action<string, float> onProgress, CancellationToken cancel, bool interactiveImport = false, bool userInitiated = true)
        {
            if (license == null || string.IsNullOrEmpty(license.license_id)) throw new ArgumentException("A license is required.", "license");
            if (version == null || string.IsNullOrEmpty(version.version_id)) throw new ArgumentException("A license version id is required.", "version");
            LegacyHandoff.ThrowIfWaiting();
            if (version.IsPackage)
                throw new PackageInstallException("This version is a package (" + version.package_name + " " + version.package_version + "); install it as a package.");

            DownloadFile[] files;
            try
            {
                files = await OnAirDeckApi.RequestLicenseVersionDownloadAsync(sessionToken, version.version_id, userInitiated);
            }
            catch (Exception e)
            {
                DeviceLimitNotice.Report(e, license.name,
                    async () => (object)await InstallAsync(sessionToken, license, version, licenses, onProgress, cancel, interactiveImport, true),
                    "download-version:" + version.version_id);
                throw;
            }

            var record = new InstallRecord
            {
                LicenseId = license.license_id,
                VersionId = version.version_id,
                Label = string.IsNullOrEmpty(version.version_label) ? (version.file_name ?? "") : version.version_label,
                ReleasedAt = version.released_at ?? "",
            };
            // The license file is written before a .unitypackage import starts (an import with scripts reloads the domain).
            var placed = await DownloadService.PlaceFilesAsync(files, Installer.ProductFolder(license.name), onProgress, cancel, interactiveImport, record,
                license.name, version.version_label, license.license_id,
                async summary =>
                {
                    try
                    {
                        var path = await LicenseService.IssueSellerLicenseAsync(sessionToken, licenses ?? new[] { license }, license.license_id, userInitiated);
                        summary.LicenseWritten = path != null;
                    }
                    catch (Exception e)
                    {
                        summary.LicenseRefused = e is DeviceLimitException || e is DeviceReleasedException || e is ManagerUpdateRequiredException;
                        Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Installed, but the license file could not be written: " + e.Message);
                    }
                }, PlacedFiles.LicenseKey(license.license_id));
            // A file core (VModular 1.0.0) is "here" only after its import ran; until then its add-on may follow it in the same queue.
            // The queue drops that add-on's import when the core's import does not go in (CoreImports.AddonWithoutCore).
            CoreImports.Note(CoreAddons.FileCoreOfLicense(license.license_id), placed.QueuedPackages);
            return placed;
        }
    }

    /// <summary>
    /// The queued imports of a file core (<see cref="OnAirDeckConfig.FileCores"/>): its .unitypackage is imported one editor tick later
    /// (<see cref="PackageImportQueue"/>), so an add-on that runs right after it (the next batch item, or "install the core first") would
    /// not find its marker yet. While the core's import is still queued the add-on goes into the same queue behind it, which imports in
    /// order. Kept in SessionState, like the queue.
    /// </summary>
    internal static class CoreImports
    {
        // "core<TAB>package path" per queued import of a file core.
        private const string Key = "OnAirDeck.UnityManager.CoreImports";

        public static void Note(string core, IList<string> packagePaths)
        {
            if (string.IsNullOrEmpty(core) || packagePaths == null || packagePaths.Count == 0) return;
            var lines = new List<string>();
            // Lines of imports that already ended are dropped: only waiting ones decide anything.
            foreach (var line in UnityEditor.SessionState.GetString(Key, "").Split('\n'))
                if (line.IndexOf('\t') > 0 && PackageImportQueue.ResultOf(line.Substring(line.IndexOf('\t') + 1)) == PackageImportQueue.ResultPending) lines.Add(line);
            foreach (var path in packagePaths) lines.Add(core + "\t" + path);
            UnityEditor.SessionState.SetString(Key, string.Join("\n", lines.ToArray()));
        }

        /// <summary>
        /// The queue is about to import <paramref name="packagePath"/> (the head of <paramref name="queue"/>): true when its license (the
        /// queue context's product id) is an add-on of a core that is not here and has no import still waiting in <paramref name="queue"/>
        /// (it failed, was cancelled or went missing, also earlier in the same pass). Decided from what the queue itself holds, so an
        /// add-on whose core failed while the add-on was still downloading is caught as well (review G4 r2 R1, R5).
        /// </summary>
        public static bool AddonWithoutCore(string packagePath, IDictionary<string, string[]> contexts, IList<string> queue)
        {
            string[] context;
            if (contexts == null || !contexts.TryGetValue(packagePath, out context) || context == null || context.Length < 3) return false;
            var core = CoreAddons.CoreOf("", context[2]);
            if (core.Length == 0) return false;
            return CoreAddons.AddonImportBlocked(core, CoreAddons.CoreIsHere(PackageInstallService.ProjectRoot, core), packagePath,
                UnityEditor.SessionState.GetString(Key, ""), queue);
        }

        /// <summary>A .unitypackage of <paramref name="core"/> is still waiting in the import queue.</summary>
        public static bool Coming(string core)
        {
            if (string.IsNullOrEmpty(core)) return false;
            foreach (var line in UnityEditor.SessionState.GetString(Key, "").Split('\n'))
            {
                var tab = line.IndexOf('\t');
                if (tab > 0 && line.Substring(0, tab) == core && PackageImportQueue.ResultOf(line.Substring(tab + 1)) == PackageImportQueue.ResultPending) return true;
            }
            return false;
        }

        /// <summary>The core is here, or its import is queued ahead of what asks.</summary>
        public static bool HereOrComing(string projectRoot, string core)
        {
            return CoreAddons.CoreIsHere(projectRoot, core) || Coming(core);
        }
    }
}
