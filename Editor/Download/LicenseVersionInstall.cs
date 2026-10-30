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
            var summary = await DownloadService.PlaceFilesAsync(files, Installer.ProductFolder(license.name), onProgress, cancel, interactiveImport, record);
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
            return summary;
        }
    }
}
