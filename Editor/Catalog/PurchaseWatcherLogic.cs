using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>An entry the account has that this PC has not shown yet.</summary>
    internal sealed class WatchedEntry
    {
        public FavoriteKind Kind;
        public string Id = "";          // product_id (item_id when a legacy order has none) or license_id
        public string Key = "";         // "p:<id>" / "l:<id>", what is stored
        public PurchaseItem Purchase;   // Kind == Purchase
        public LicenseEntry License;    // Kind == License
    }

    internal sealed class SeenDiff
    {
        /// <summary>No seen list existed for this account: everything is marked seen and nothing is new.</summary>
        public bool IsFirstLoad;
        public List<WatchedEntry> Unseen = new List<WatchedEntry>();
        /// <summary>The text to store when <see cref="IsFirstLoad"/> (every current key).</summary>
        public string FirstLoadText = "";
    }

    /// <summary>The pure rules of <see cref="PurchaseWatcher"/> (no UnityEngine, headless-checked).</summary>
    internal static class PurchaseWatcherLogic
    {
        public const string SeenPrefix = "OnAirDeck.UnityManager.SeenEntries.";
        /// <summary>The seen list keeps the newest this many keys per account (oldest dropped), so the EditorPrefs text cannot grow forever.</summary>
        public const int MaxSeen = 1000;
        public const long MinIntervalSeconds = 60;
        public const long PeriodSeconds = 5 * 60;

        public static string SeenKey(string userId)
        {
            return string.IsNullOrEmpty(userId) ? null : SeenPrefix + userId;
        }

        /// <summary>Signed in, and the last attempt (success or not) is at least 60 s old (or the clock moved back).</summary>
        public static bool CanRefresh(bool signedIn, long nowUnix, long lastAttemptUnix)
        {
            if (!signedIn) return false;
            if (lastAttemptUnix <= 0 || lastAttemptUnix > nowUnix) return true;
            return nowUnix - lastAttemptUnix >= MinIntervalSeconds;
        }

        /// <summary>The 5-minute tick while the window is open (still subject to <see cref="CanRefresh"/>).</summary>
        public static bool PeriodicDue(bool windowOpen, long nowUnix, long lastAttemptUnix)
        {
            if (!windowOpen) return false;
            if (lastAttemptUnix <= 0 || lastAttemptUnix > nowUnix) return true;
            return nowUnix - lastAttemptUnix >= PeriodSeconds;
        }

        /// <summary>
        /// Compares the list with the stored seen text. <paramref name="storedOrNull"/> null means this account was never
        /// seen on this PC: that first load marks everything seen.
        /// </summary>
        public static SeenDiff Diff(string storedOrNull, PurchasesResponse response)
        {
            var all = Entries(response);
            var diff = new SeenDiff();
            if (storedOrNull == null)
            {
                diff.IsFirstLoad = true;
                var keys = new List<string>();
                foreach (var entry in all) keys.Add(entry.Key);
                keys.Sort(StringComparer.Ordinal);   // no "newest" among a first load: a stable order, then the cap
                diff.FirstLoadText = Join(Capped(keys));
                return diff;
            }
            var seen = FavoritesLogic.Parse(storedOrNull);
            foreach (var entry in all)
                if (!seen.Contains(entry.Key)) diff.Unseen.Add(entry);
            return diff;
        }

        /// <summary>Adds keys after the stored ones (oldest first), no duplicates, then keeps the newest <see cref="MaxSeen"/>.</summary>
        public static string MarkSeen(string storedOrNull, IEnumerable<string> keys)
        {
            var ordered = ParseOrdered(storedOrNull);
            var present = new HashSet<string>(ordered, StringComparer.Ordinal);
            foreach (var key in keys)
                if (!string.IsNullOrEmpty(key) && present.Add(key)) ordered.Add(key);
            return Join(Capped(ordered));
        }

        private static List<string> ParseOrdered(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.Length > 2 && (t.StartsWith("p:", StringComparison.Ordinal) || t.StartsWith("l:", StringComparison.Ordinal)) && present.Add(t)) list.Add(t);
            }
            return list;
        }

        private static List<string> Capped(List<string> ordered)
        {
            return ordered.Count <= MaxSeen ? ordered : ordered.GetRange(ordered.Count - MaxSeen, MaxSeen);
        }

        private static string Join(List<string> ordered)
        {
            return string.Join("\n", ordered.ToArray());
        }

        public static List<WatchedEntry> Entries(PurchasesResponse response)
        {
            var list = new List<WatchedEntry>();
            if (response == null) return list;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in response.purchases ?? new PurchaseItem[0])
            {
                if (item == null) continue;
                var id = !string.IsNullOrEmpty(item.product_id) ? item.product_id : item.item_id;
                var key = FavoritesLogic.EntryKey(FavoriteKind.Purchase, id);
                if (key == null || !keys.Add(key)) continue;
                list.Add(new WatchedEntry { Kind = FavoriteKind.Purchase, Id = id, Key = key, Purchase = item });
            }
            foreach (var license in response.licenses ?? new LicenseEntry[0])
            {
                if (license == null) continue;
                var key = FavoritesLogic.EntryKey(FavoriteKind.License, license.license_id);
                if (key == null || !keys.Add(key)) continue;
                list.Add(new WatchedEntry { Kind = FavoriteKind.License, Id = license.license_id, Key = key, License = license });
            }
            return list;
        }
    }
}
