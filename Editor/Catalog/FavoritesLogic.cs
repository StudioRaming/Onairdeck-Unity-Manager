using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    internal enum FavoriteKind { Purchase, License }

    /// <summary>
    /// The pure part of the favorites (no UnityEngine, headless-checked): which EditorPrefs key a signed-in user's list
    /// lives under, how an entry is keyed, and the list's text form. <see cref="FavoritesStore"/> does the EditorPrefs part.
    /// </summary>
    internal static class FavoritesLogic
    {
        public const string PrefsPrefix = "OnAirDeck.UnityManager.Favorites.";

        /// <summary>One EditorPrefs key per account on this PC, or null without a user id (nothing is stored then).</summary>
        public static string PrefsKey(string userId)
        {
            return string.IsNullOrEmpty(userId) ? null : PrefsPrefix + userId;
        }

        /// <summary>"p:&lt;product_id&gt;" for a purchase, "l:&lt;license_id&gt;" for a license; null for an empty id.</summary>
        public static string EntryKey(FavoriteKind kind, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            return (kind == FavoriteKind.Purchase ? "p:" : "l:") + id;
        }

        /// <summary>
        /// The purchase rows of a list and their keys, in list order: one row per line item shown in Unity. A product bought once is
        /// "p:&lt;product_id&gt;" as before. A product bought in several options (one line item each) lists every item: the oldest keeps
        /// "p:&lt;product_id&gt;" (its seen state stays), each later one is "p:&lt;product_id&gt;/&lt;item_id&gt;", so a newly bought option
        /// is new and the older rows keep their keys. Until 2026-11-03 only the first item of a product was listed. An item without an
        /// item id, or one listed twice, cannot be told apart and is skipped when its product already has a row.
        /// </summary>
        public static List<KeyValuePair<PurchaseItem, string>> PurchaseRows(PurchaseItem[] purchases)
        {
            var rows = new List<KeyValuePair<PurchaseItem, string>>();
            if (purchases == null) return rows;
            var oldest = new Dictionary<string, PurchaseItem>(StringComparer.Ordinal);
            foreach (var item in purchases)
            {
                if (item == null || !item.ShownInUnity || string.IsNullOrEmpty(item.product_id) || string.IsNullOrEmpty(item.item_id)) continue;
                PurchaseItem seen;
                if (!oldest.TryGetValue(item.product_id, out seen) || Older(item, seen)) oldest[item.product_id] = item;
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in purchases)
            {
                if (item == null || !item.ShownInUnity) continue;
                var id = !string.IsNullOrEmpty(item.product_id) ? item.product_id : item.item_id;
                PurchaseItem first;
                var later = !string.IsNullOrEmpty(item.product_id) && !string.IsNullOrEmpty(item.item_id)
                    && oldest.TryGetValue(item.product_id, out first) && !ReferenceEquals(first, item);
                var key = later ? EntryKey(FavoriteKind.Purchase, item.product_id + "/" + item.item_id) : EntryKey(FavoriteKind.Purchase, id);
                if (key == null || !keys.Add(key)) continue;
                rows.Add(new KeyValuePair<PurchaseItem, string>(item, key));
            }
            return rows;
        }

        /// <summary>Bought earlier (ISO dates compare as text), then the lower item id, so the choice never depends on list order.</summary>
        private static bool Older(PurchaseItem a, PurchaseItem b)
        {
            var at = !string.IsNullOrEmpty(a.purchased_at) ? a.purchased_at : a.date ?? "";
            var bt = !string.IsNullOrEmpty(b.purchased_at) ? b.purchased_at : b.date ?? "";
            var byDate = string.CompareOrdinal(at, bt);
            return byDate != 0 ? byDate < 0 : string.CompareOrdinal(a.item_id, b.item_id) < 0;
        }

        public static HashSet<string> Parse(string raw)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(raw)) return set;
            foreach (var line in raw.Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("p:", StringComparison.Ordinal) || t.StartsWith("l:", StringComparison.Ordinal))
                    if (t.Length > 2) set.Add(t);
            }
            return set;
        }

        /// <summary>Sorted, one entry per line, so the stored text is stable.</summary>
        public static string Serialize(IEnumerable<string> entries)
        {
            var list = new List<string>(entries);
            list.Sort(StringComparer.Ordinal);
            return string.Join("\n", list.ToArray());
        }

        /// <summary>Flips the entry in the set. Returns the new state (true = now a favorite). A null key changes nothing and returns false.</summary>
        public static bool Toggle(HashSet<string> set, string entryKey)
        {
            if (entryKey == null) return false;
            if (set.Remove(entryKey)) return false;
            set.Add(entryKey);
            return true;
        }
    }
}
