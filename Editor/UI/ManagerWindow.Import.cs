using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The window's half of the one-button import: <see cref="ImportRequestWatcher"/> claims a request and asks the person; this
    /// runs the same install path a click would (download a purchase, install the offered package or file version) and says how
    /// it ended. The refund lock, the device gate and the license issue stay where they are.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private static bool AnyWindowBusy()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ManagerWindow>())
                if (window != null && window.Busy) return true;
            return false;
        }

        private static Task<ImportOutcome> RunImportInWindow(ImportRequest request, Action<string, string> willInstall)
        {
            Open();
            var windows = Resources.FindObjectsOfTypeAll<ManagerWindow>();
            if (windows.Length == 0 || windows[0] == null) return Task.FromResult(ImportOutcome.Release());
            return windows[0].RunImportAsync(request, willInstall);
        }

        private async Task<ImportOutcome> RunImportAsync(ImportRequest request, Action<string, string> willInstall)
        {
            if (_session == null || Busy) return ImportOutcome.Release();
            var token = _session.token;
            // The list the window has may be older than the purchase the page was on.
            try
            {
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(token);
                if (_session == null || _session.token != token) return ImportOutcome.Release();
                ApplyResponse(response);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
                return ImportOutcome.Nothing();
            }
            catch (Exception e)
            {
                // Offline or the server failed: the request goes back to pending, the page keeps waiting.
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Loading the list for an import failed: " + e.Message);
                return ImportOutcome.Release();
            }
            RebuildIfNeeded();

            var entries = FindImportEntries(request);
            if (entries.Count == 0) return ImportOutcome.Failed("not_available");
            _view = (int)View.Main;
            _selectedKey = entries[0].Key;
            Repaint();

            if (entries[0].Kind == FavoriteKind.Purchase) return await DoDownloadAsync(entries[0], true);
            if (entries.Count == 1) return await InstallLicenseEntry(entries[0], willInstall);

            // An item that grants several licenses (a MingToon Founders edition: Core, the URP add-on, MLC) installs all of them under
            // the one confirm. Every install ends with a refresh, and a reload in the middle would drop the rest: reloads wait until
            // the last step, as in a batch. A step that does not end here and now (failed, declined, or waiting for an import or a
            // hand-off) ends the run with its outcome; what was installed before it stays, and the rest can be installed from the list.
            var root = PackageInstallService.ProjectRoot;
            var before = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries)
                if (entry.PackageName.Length > 0) before[entry.PackageName] = InstalledPackages.InstalledVersion(root, entry.PackageName);
            ImportOutcome last = null;
            EditorApplication.LockReloadAssemblies();
            try
            {
                foreach (var index in ImportRequestLogic.InstallOrder(entries.ConvertAll(e => e.PackageName)))
                {
                    var entry = entries[index];
                    // An add-on installed before it brought the exact version it needs.
                    if (last != null && entry.PackageName.Length > 0 && InstalledPackages.InstalledVersion(root, entry.PackageName) != before[entry.PackageName]) continue;
                    _selectedKey = entry.Key;
                    var outcome = await InstallLicenseEntry(entry, willInstall);
                    if (outcome.Report != ImportReport.Done) return outcome;
                    last = outcome;
                }
            }
            finally
            {
                EditorApplication.UnlockReloadAssemblies();
            }
            return last ?? ImportOutcome.Failed("not_available");
        }

        private async Task<ImportOutcome> InstallLicenseEntry(CatalogEntry entry, Action<string, string> willInstall)
        {
            var package = OfferedPackage(entry);
            if (package != null) return await DoInstallAsync(entry, package, true, willInstall);
            var file = OfferedFileVersion(entry);
            if (file != null) return await DoInstallFileAsync(entry, file, true);
            return ImportOutcome.Failed("not_available");
        }

        /// <summary>
        /// The list entries a request is for: the purchase of exactly that item, else every license the item grants (or the license of a
        /// license request) that has something to install here, each row once. The list shows one row per product, so a second line
        /// item of the same product gets an entry of its own here: another item of the product must never be downloaded in its
        /// place (the first download of an item ends its refund).
        /// </summary>
        private List<CatalogEntry> FindImportEntries(ImportRequest request)
        {
            var found = new List<CatalogEntry>();
            if (_entries == null) return found;
            if (request.kind == "item" && _purchases != null)
            {
                foreach (var item in _purchases)
                {
                    if (item == null || item.item_id != request.item_id || !item.ShownInUnity || !item.HasUnityFiles) continue;
                    foreach (var entry in _entries)
                        if (entry.Purchase != null && ReferenceEquals(entry.Purchase, item)) { found.Add(entry); return found; }
                    found.Add(new CatalogEntry { Key = "import:" + item.item_id, Kind = FavoriteKind.Purchase, Purchase = item, Name = item.name ?? "" });
                    return found;
                }
            }
            var licenseIds = new List<string>();
            if (!string.IsNullOrEmpty(request.license_id)) licenseIds.Add(request.license_id);
            if (request.licenses != null)
                foreach (var license in request.licenses)
                    if (license != null && !string.IsNullOrEmpty(license.license_id)) licenseIds.Add(license.license_id);
            foreach (var id in licenseIds)
                foreach (var entry in _entries)
                    if (entry.HoldsLicense(id) && !found.Contains(entry) && (OfferedPackage(entry) != null || OfferedFileVersion(entry) != null)) found.Add(entry);
            return found;
        }
    }
}
