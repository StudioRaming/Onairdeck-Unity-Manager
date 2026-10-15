using System;
using System.IO;
using System.Threading.Tasks;
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
        public static async Task<string> IssueAsync(string sessionToken, PurchaseItem item)
        {
            var response = await OnAirDeckApi.RequestLicenseAsync(sessionToken, item.item_id);
            var folder = Installer.ProductFolder(item.name);
            LicenseFile.Write(folder, response.license);
            AssetDatabase.Refresh();
            return folder + "/" + LicenseFile.FileName;
        }

        /// <summary>
        /// Best-effort refresh for items installed in this project: only where the product folder
        /// exists, and only when the license is missing or within the refresh window. Returns the
        /// number refreshed. A rejected session propagates; other failures are logged per item.
        /// </summary>
        public static async Task<int> RefreshInstalledAsync(string sessionToken, PurchaseItem[] purchases)
        {
            if (purchases == null) return 0;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var refreshed = 0;
            foreach (var item in purchases)
            {
                if (item == null || string.IsNullOrEmpty(item.item_id) || !item.downloaded) continue;
                var folder = Installer.ProductFolder(item.name);
                if (!Directory.Exists(Path.GetDirectoryName(LicenseFile.PathFor(folder)))) continue;
                if (!LicenseFile.NeedsRefresh(folder, now)) continue;
                try
                {
                    await IssueAsync(sessionToken, item);
                    refreshed++;
                }
                catch (SessionExpiredException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not refresh the license for " + item.name + ": " + e.Message);
                }
            }
            return refreshed;
        }
    }
}
