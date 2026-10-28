using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using OnAirDeck.License;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Asks plugin-license for a signed license and keeps onairdeck.license files fresh.</summary>
    internal static class LicenseService
    {
        /// <summary>
        /// Issues a license for the item and writes it into the product folder (created if the
        /// item only installed a .unitypackage). Issuing never counts as a download on the server.
        /// Returns the project-relative asset path.
        /// </summary>
        public static async Task<string> IssueAsync(string sessionToken, PurchaseItem item, bool userInitiated = true)
        {
            try
            {
                return await IssueCoreAsync(sessionToken, item, userInitiated);
            }
            catch (Exception e)
            {
                // A device-limit, device-released or update-required refusal reaches the window; the caller still gets the exception.
                // The retry runs after a click, so it may take the seat back.
                DeviceLimitNotice.Report(e, item.name, () => IssueAsync(sessionToken, item, true), "issue:" + item.name);
                throw;
            }
        }

        private static async Task<string> IssueCoreAsync(string sessionToken, PurchaseItem item, bool reactivate)
        {
            var response = item.IsOwn
                ? await OnAirDeckApi.RequestOwnProductLicenseAsync(sessionToken, item.product_id, reactivate)
                : await OnAirDeckApi.RequestLicenseAsync(sessionToken, item.item_id, reactivate);
            var folder = Installer.ProductFolder(item.name);
            var path = LicenseFile.Write(folder, response.license);
            LegacyHandoffRunner.RefreshUnlessWaiting();
            // The same check sellers' assets run (OnAirDeck.License): embedded public key, product, signed-in user, expiry.
            var check = OnAirDeckLicense.Check(item.product_id, path);
            if (!check.IsValid)
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The license for " + item.name + " was saved but fails the local check (" + check.Status + "): " + check.Message);
            return folder + "/" + LicenseFile.FileName;
        }

        /// <summary>
        /// Lock-screen request (contract v8): issues one license of the account as a user click (X-Atelier-Reactivate),
        /// into the folder its file already has or the name-based folder. An id the account does not own is left alone
        /// (the window shows its normal "not owned" state). Refusals go to <see cref="DeviceLimitNotice"/>.
        /// </summary>
        public static async Task<string> IssueSellerLicenseAsync(string sessionToken, LicenseEntry[] licenses, string licenseId, bool userInitiated = true)
        {
            LicenseEntry license = null;
            if (licenses != null)
                foreach (var candidate in licenses)
                    if (candidate != null && string.Equals(candidate.license_id, licenseId, StringComparison.OrdinalIgnoreCase)) license = candidate;
            if (license == null) return null;
            try
            {
                var folder = LicenseFilePlan.FolderOf(licenses, license, LicenseFilePlan.ExistingFiles(PackageInstallService.ProjectRoot));
                var response = await OnAirDeckApi.RequestSellerLicenseAsync(sessionToken, license.license_id, userInitiated);
                var path = LicenseFile.Write(folder, response.license);
                LicenseRenewalStatus.Succeed(license.license_id);
                LegacyHandoffRunner.RefreshUnlessWaiting();
                var check = OnAirDeckLicense.Check(license.license_id, path);
                if (!check.IsValid)
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The license for " + license.name + " was saved but fails the local check (" + check.Status + "): " + check.Message);
                return folder + "/" + LicenseFile.FileName;
            }
            catch (Exception e)
            {
                LicenseRenewalStatus.Refuse(license.license_id, license.name, e);
                DeviceLimitNotice.Report(e, license.name, () => IssueSellerLicenseAsync(sessionToken, licenses, licenseId, true), "issue-seller:" + license.license_id);
                throw;
            }
        }

        private static bool _ensuring;

        private static string CurrentUserId(string sessionToken)
        {
            var session = SessionStore.Load();
            return session != null && session.token == sessionToken ? session.userId : null;
        }

        /// <summary>
        /// Contract v4. For every license of the account that has a package installed in this project (by any route),
        /// issues the license, or renews it 2 days before expiry, into
        /// Assets/StudioRaming_Onairdeck/&lt;safe license name&gt;/onairdeck.license. plugin-license gets { license_id, client }; the signed
        /// payload carries product_id = license_id, so OnAirDeckLicense.Check(license_id) passes. Call it after sign-in, after a list
        /// refresh and after a license-version install. A file issued to another account (or PC) is reissued, not kept.
        /// <paramref name="userInitiated"/> true sends X-Atelier-Reactivate (and reissues every installed license, since a
        /// released PC's valid files still need the seat); focus/timer refresh and PurchaseWatcher pass false (the default).
        /// Returns the number of files written. A rejected session propagates;
        /// device-limit and update-required refusals go to <see cref="DeviceLimitNotice"/> (its retry runs this again);
        /// other failures are logged per license. A second call while one runs returns 0.
        /// A server refusal is also kept per license in <see cref="LicenseRenewalStatus"/> (code and message) until that
        /// license is issued or renewed again, so a refusal in a background renewal reaches the window too.
        /// </summary>
        public static async Task<int> EnsureLicenseFilesAsync(string sessionToken, LicenseEntry[] licenses, bool userInitiated = false)
        {
            if (licenses == null || licenses.Length == 0 || string.IsNullOrEmpty(sessionToken) || _ensuring) return 0;
            _ensuring = true;
            var written = 0;
            try
            {
                var root = PackageInstallService.ProjectRoot;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var userId = CurrentUserId(sessionToken);
                var deviceHash = DeviceIdentity.Hash;
                // One marker scan and one folder listing per refresh (contract v8), not per license.
                var markers = ProductMarkers.Scan(root);
                var accountIds = new List<string>();
                foreach (var entry in licenses) if (entry != null) accountIds.Add(entry.license_id);
                LicenseRenewalStatus.Prune(accountIds);
                var existing = LicenseFilePlan.ExistingFiles(root);
                var jobs = LicenseFilePlan.Select(licenses, name => InstalledPackages.IsInstalled(root, name),
                    folder => userInitiated || LicenseFile.NeedsRefresh(folder, now, userId, deviceHash),
                    markers.Contains, existing);
                foreach (var job in jobs)
                {
                    try
                    {
                        var response = await OnAirDeckApi.RequestSellerLicenseAsync(sessionToken, job.License.license_id, userInitiated);
                        var path = LicenseFile.Write(job.Folder, response.license);
                        written++;
                        LicenseRenewalStatus.Succeed(job.License.license_id);
                        var check = OnAirDeckLicense.Check(job.License.license_id, path);
                        if (!check.IsValid)
                            Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The license for " + job.License.name + " was saved but fails the local check (" + check.Status + "): " + check.Message);
                    }
                    catch (SessionExpiredException)
                    {
                        throw;
                    }
                    catch (Exception e)
                    {
                        LicenseRenewalStatus.Refuse(job.License.license_id, job.License.name, e);
                        if (DeviceLimitNotice.Report(e, job.License.name, async () => (object)await EnsureLicenseFilesAsync(sessionToken, licenses, true), "ensure")) continue;
                        Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not issue the license for " + job.License.name + ": " + e.Message);
                    }
                }
                if (written > 0) LegacyHandoffRunner.RefreshUnlessWaiting();
                return written;
            }
            finally
            {
                _ensuring = false;
            }
        }

        /// <summary>
        /// Best-effort refresh for items installed in this project: only where the product folder
        /// exists, and only when the license is missing or within the refresh window. Returns the
        /// number refreshed. A rejected session propagates; device-limit and update-required refusals are collected in
        /// <see cref="DeviceLimitNotice"/> (one retry that renews everything still due); other failures are logged per item.
        /// </summary>
        public static async Task<int> RefreshInstalledAsync(string sessionToken, PurchaseItem[] purchases, bool userInitiated = false)
        {
            if (purchases == null) return 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var userId = CurrentUserId(sessionToken);
            var deviceHash = DeviceIdentity.Hash;
            var refreshed = 0;
            foreach (var item in purchases)
            {
                if (item == null || (item.IsOwn ? string.IsNullOrEmpty(item.product_id) : (string.IsNullOrEmpty(item.item_id) || !item.downloaded))) continue;
                var folder = Installer.ProductFolder(item.name);
                if (!Directory.Exists(Path.GetDirectoryName(LicenseFile.PathFor(folder)))) continue;
                if (!userInitiated && !LicenseFile.NeedsRefresh(folder, now, userId, deviceHash)) continue;
                try
                {
                    await IssueCoreAsync(sessionToken, item, userInitiated);
                    refreshed++;
                }
                catch (SessionExpiredException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (DeviceLimitNotice.Report(e, item.name, async () => (object)await RefreshInstalledAsync(sessionToken, purchases, true), "refresh")) continue;
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not refresh the license for " + item.name + ": " + e.Message);
                }
            }
            return refreshed;
        }
    }
}
