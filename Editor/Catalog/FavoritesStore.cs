using System;
using System.Collections.Generic;
using UnityEditor;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Star marks on products and licenses, kept per signed-in account on this PC in EditorPrefs (no server).
    /// A purchase is keyed by its product_id, a license by its license_id. Pass <c>SavedSession.userId</c>.
    /// </summary>
    internal static class FavoritesStore
    {
        /// <summary>Raised after a toggle. The window repaints and re-sorts.</summary>
        public static event Action Changed;

        private static readonly Dictionary<string, HashSet<string>> Cache = new Dictionary<string, HashSet<string>>();

        public static bool IsFavorite(string userId, FavoriteKind kind, string id)
        {
            var key = FavoritesLogic.EntryKey(kind, id);
            var set = Load(userId);
            return key != null && set != null && set.Contains(key);
        }

        public static bool IsFavorite(string userId, PurchaseItem item)
        {
            return item != null && IsFavorite(userId, FavoriteKind.Purchase, item.product_id);
        }

        public static bool IsFavorite(string userId, LicenseEntry license)
        {
            return license != null && IsFavorite(userId, FavoriteKind.License, license.license_id);
        }

        /// <summary>Flips the mark and returns the new state; false and no change when there is no user or id.</summary>
        public static bool Toggle(string userId, FavoriteKind kind, string id)
        {
            var key = FavoritesLogic.EntryKey(kind, id);
            var set = Load(userId);
            if (key == null || set == null) return false;
            var now = FavoritesLogic.Toggle(set, key);
            EditorPrefs.SetString(FavoritesLogic.PrefsKey(userId), FavoritesLogic.Serialize(set));
            var handler = Changed;
            if (handler != null) handler();
            return now;
        }

        public static bool Toggle(string userId, PurchaseItem item)
        {
            return item != null && Toggle(userId, FavoriteKind.Purchase, item.product_id);
        }

        public static bool Toggle(string userId, LicenseEntry license)
        {
            return license != null && Toggle(userId, FavoriteKind.License, license.license_id);
        }

        private static HashSet<string> Load(string userId)
        {
            var prefsKey = FavoritesLogic.PrefsKey(userId);
            if (prefsKey == null) return null;
            HashSet<string> set;
            if (!Cache.TryGetValue(prefsKey, out set))
            {
                set = FavoritesLogic.Parse(EditorPrefs.GetString(prefsKey, ""));
                Cache[prefsKey] = set;
            }
            return set;
        }
    }
}
