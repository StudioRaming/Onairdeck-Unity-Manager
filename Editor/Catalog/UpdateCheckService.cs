using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// "New versions are available" without touching the project. On editor load (delayCall) it asks the server for
    /// the purchase list when someone is signed in, at most once per editor session and every 6 hours, and keeps
    /// the items that have a newer version. It never downloads or installs anything and fails quietly.
    /// The window calls <see cref="Evaluate"/> with each purchase list it loads so <see cref="Count"/> stays current.
    /// </summary>
    [InitializeOnLoad]
    internal static class UpdateCheckService
    {
        private const string LastCheckKey = "OnAirDeck.UnityManager.UpdateCheckLast";
        private const string SessionKey = "OnAirDeck.UnityManager.UpdateCheckStarted";

        public static event Action Changed;

        private static List<UpdateCandidate> _items = new List<UpdateCandidate>();

        /// <summary>Entries with update_available plus installed packages with a newer version offered.</summary>
        public static int Count { get { return _items.Count; } }
        public static IList<UpdateCandidate> Items { get { return _items; } }

        static UpdateCheckService()
        {
            EditorApplication.delayCall += AutoCheck;
        }

        private static async void AutoCheck()
        {
            try
            {
                var session = SessionStore.Load();
                var signedIn = session != null && !session.IsExpired && !string.IsNullOrEmpty(session.token);
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var last = long.Parse(EditorPrefs.GetString(LastCheckKey, "0"));
                if (!UpdateCheckLogic.ShouldRun(signedIn, SessionState.GetBool(SessionKey, false), now, last)) return;
                SessionState.SetBool(SessionKey, true); // a failed fetch is not retried until the next editor session
                await RefreshAsync(session.token);
                if (Count > 0)
                    Debug.Log(OnAirDeckConfig.LogPrefix + Count + " update" + (Count == 1 ? " is" : "s are") + " available. Open Atelier Manager (Window > Atelier Manager) to update.");
            }
            catch (Exception)
            {
                // Quiet on purpose: an offline editor start must not print errors.
            }
        }

        /// <summary>Fetches the purchase list now (ignores the throttle) and updates <see cref="Items"/>. Returns false on any failure.</summary>
        public static async Task<bool> RefreshAsync(string sessionToken)
        {
            try
            {
                // The account is fixed before the request: an account switch while it runs must not record A's list for B.
                var before = SessionStore.Load();
                var requestUserId = before != null && before.token == sessionToken ? before.userId : null;
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(sessionToken);
                var after = SessionStore.Load();
                if (!UpdateCheckLogic.ShouldApply(requestUserId, after != null ? after.userId : null)) return false;
                Evaluate(response);
                EditorPrefs.SetString(LastCheckKey, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
                PurchaseWatcher.Observe(requestUserId, response);
                try { await LicenseService.EnsureLicenseFilesAsync(sessionToken, response.licenses); }
                catch (Exception) { }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Recomputes the items from a purchase list the window already has. Raises <see cref="Changed"/> when the result differs.</summary>
        public static void Evaluate(PurchasesResponse response)
        {
            var root = PackageInstallService.ProjectRoot;
            var next = UpdateCheckLogic.Find(response == null ? null : response.purchases, response == null ? null : response.licenses,
                name => InstalledPackages.InstalledVersion(root, name));
            var changed = next.Count != _items.Count;
            for (var i = 0; !changed && i < next.Count; i++)
                changed = next[i].Kind != _items[i].Kind || next[i].Name != _items[i].Name || next[i].Latest != _items[i].Latest;
            _items = next;
            if (!changed) return;
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
