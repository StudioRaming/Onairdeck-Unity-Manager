using System;
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

            var entry = FindImportEntry(request);
            if (entry == null) return ImportOutcome.Failed("not_available");
            _view = (int)View.Main;
            _selectedKey = entry.Key;
            Repaint();

            if (entry.Kind == FavoriteKind.Purchase) return await DoDownloadAsync(entry, true);
            var package = OfferedPackage(entry);
            if (package != null) return await DoInstallAsync(entry, package, true, willInstall);
            var file = OfferedFileVersion(entry);
            if (file != null) return await DoInstallFileAsync(entry, file, true);
            return ImportOutcome.Failed("not_available");
        }

        /// <summary>
        /// The list entry a request is for: the purchase of the item (by item id, else its product), else the license the item
        /// grants or the license of a license request, when that license has something to install here.
        /// </summary>
        private CatalogEntry FindImportEntry(ImportRequest request)
        {
            if (_entries == null) return null;
            if (request.kind == "item")
            {
                foreach (var entry in _entries)
                    if (entry.Purchase != null && entry.Purchase.item_id == request.item_id) return entry;
                if (!string.IsNullOrEmpty(request.product_id))
                    foreach (var entry in _entries)
                        if (entry.Purchase != null && entry.Purchase.product_id == request.product_id) return entry;
            }
            var licenseIds = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrEmpty(request.license_id)) licenseIds.Add(request.license_id);
            if (request.licenses != null)
                foreach (var license in request.licenses)
                    if (license != null && !string.IsNullOrEmpty(license.license_id)) licenseIds.Add(license.license_id);
            foreach (var id in licenseIds)
                foreach (var entry in _entries)
                    if (entry.IsLicense && entry.LicenseId == id && (OfferedPackage(entry) != null || OfferedFileVersion(entry) != null)) return entry;
            return null;
        }
    }
}
