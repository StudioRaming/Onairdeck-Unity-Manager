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
