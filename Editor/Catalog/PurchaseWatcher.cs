using System;
using System.Collections.Generic;
using UnityEditor;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// A purchase made on the website shows up without a manual refresh. It refreshes plugin-purchases when the editor
    /// regains focus (polled in the update tick), when the window is focused (<see cref="NotifyWindowFocused"/>) and every 5 minutes while the window
    /// is open (<see cref="SetWindowOpen"/>), at most once per 60 s, signed in only, silently on failure.
    /// "Seen" ids are kept per account in EditorPrefs; the first load for an account marks everything seen.
    /// No server change, no URL scheme and no local listener.
    /// </summary>
    [InitializeOnLoad]
    internal static class PurchaseWatcher
    {
        /// <summary>Raised after every successful refresh with the fresh list (the window can show it instead of loading again).</summary>
        public static event Action<PurchasesResponse> Refreshed;
        /// <summary>Raised when entries appear that this PC has not seen (argument: only the entries new in this refresh).</summary>
        public static event Action<IList<WatchedEntry>> NewEntriesFound;
        /// <summary>Raised whenever <see cref="Unseen"/> changed (found or marked seen).</summary>
        public static event Action Changed;

        private static List<WatchedEntry> _unseen = new List<WatchedEntry>();
        private static bool _windowOpen;
        private static long _lastAttempt;
        private static bool _busy;
        private static string _userId = "";
        private static bool _wasActive;

        /// <summary>Entries not seen on this PC yet (the "new" chip and the bar).</summary>
        public static IList<WatchedEntry> Unseen { get { return _unseen; } }
        public static int UnseenCount { get { return _unseen.Count; } }
        public static PurchasesResponse LastResponse { get; private set; }

        static PurchaseWatcher()
        {
            // EditorApplication.focusChanged does not exist in 2021.3, so the editor focus is polled in the update tick.
            _wasActive = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
            EditorApplication.update += Tick;
        }

        /// <summary>The window tells when it is open (true in OnEnable, false in OnDisable) so the 5-minute tick runs only then.</summary>
        public static void SetWindowOpen(bool open)
        {
            _windowOpen = open;
        }

        /// <summary>Call when the window gets focus.</summary>
        public static void NotifyWindowFocused()
        {
            RequestRefresh();
        }

        private static void Tick()
        {
            var active = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
            var regainedFocus = active && !_wasActive;
            _wasActive = active;
            if (regainedFocus) { RequestRefresh(); return; }
            if (!PurchaseWatcherLogic.PeriodicDue(_windowOpen, Now(), _lastAttempt)) return;
            RequestRefresh();
        }

        /// <summary>Refreshes now when signed in and the 60 s limit allows. Returns whether a refresh started.</summary>
        public static bool RequestRefresh()
        {
            if (_busy) return false;
            var session = SessionStore.Load();
            var signedIn = session != null && !session.IsExpired && !string.IsNullOrEmpty(session.token);
            if (!PurchaseWatcherLogic.CanRefresh(signedIn, Now(), _lastAttempt)) return false;
            RefreshAsync(session);
            return true;
        }

        private static async void RefreshAsync(SavedSession session)
        {
            _busy = true;
            _lastAttempt = Now();
            try
            {
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(session.token);
                // An account switch while the request ran: this list belongs to the old account, drop it.
                var now = SessionStore.Load();
                if (!UpdateCheckLogic.ShouldApply(session.userId, now != null ? now.userId : null)) return;
                Observe(session.userId, response);
                UpdateCheckService.Evaluate(response);
                var handler = Refreshed;
                if (handler != null) handler(response);
                await LicenseService.EnsureLicenseFilesAsync(session.token, response.licenses);
            }
            catch (Exception e)
            {
                // Silent on purpose: no popup and no Console line for a failed background refresh. A bug of the Manager still
                // opens the report card (a server or network failure does not).
                ManagerErrorWatch.OfferCaught("Background purchase refresh", e);
            }
            finally
            {
                _busy = false;
            }
        }

        /// <summary>
        /// Feeds a purchase list (also the one the window loaded itself) into the seen logic. The first list of an account
        /// is stored as seen; later lists put anything not stored into <see cref="Unseen"/>.
        /// </summary>
        public static void Observe(string userId, PurchasesResponse response)
        {
            var key = PurchaseWatcherLogic.SeenKey(userId);
            if (key == null || response == null) return;
            LastResponse = response;
            if (userId != _userId) { _unseen = new List<WatchedEntry>(); _userId = userId; }
            var stored = EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key, "") : null;
            var diff = PurchaseWatcherLogic.Diff(stored, response);
            if (diff.IsFirstLoad)
            {
                EditorPrefs.SetString(key, diff.FirstLoadText);
                SetUnseen(diff.Unseen, new List<WatchedEntry>());
                return;
            }
            var known = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in _unseen) known.Add(entry.Key);
            var added = new List<WatchedEntry>();
            foreach (var entry in diff.Unseen) if (!known.Contains(entry.Key)) added.Add(entry);
            SetUnseen(diff.Unseen, added);
        }

        /// <summary>The person looked at or took the entries: they are not new any more (stored per account).</summary>
        public static void MarkSeen(string userId, IEnumerable<string> entryKeys)
        {
            var key = PurchaseWatcherLogic.SeenKey(userId);
            if (key == null) return;
            var keys = new List<string>(entryKeys);
            EditorPrefs.SetString(key, PurchaseWatcherLogic.MarkSeen(EditorPrefs.HasKey(key) ? EditorPrefs.GetString(key, "") : null, keys));
            var left = new List<WatchedEntry>();
            foreach (var entry in _unseen) if (!keys.Contains(entry.Key)) left.Add(entry);
            SetUnseen(left, new List<WatchedEntry>());
        }

        public static void MarkAllSeen(string userId)
        {
            var keys = new List<string>();
            foreach (var entry in _unseen) keys.Add(entry.Key);
            MarkSeen(userId, keys);
        }

        public static bool IsUnseen(string entryKey)
        {
            foreach (var entry in _unseen) if (entry.Key == entryKey) return true;
            return false;
        }

        private static void SetUnseen(List<WatchedEntry> next, List<WatchedEntry> added)
        {
            var changed = next.Count != _unseen.Count;
            for (var i = 0; !changed && i < next.Count; i++) changed = next[i].Key != _unseen[i].Key;
            _unseen = next;
            if (added.Count > 0)
            {
                var found = NewEntriesFound;
                if (found != null) found(added);
            }
            if (!changed) return;
            var handler = Changed;
            if (handler != null) handler();
        }

        private static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
