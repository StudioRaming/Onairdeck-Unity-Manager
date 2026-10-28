using System;
using System.Collections.Generic;
using System.IO;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Contract v8: every DRM product, whatever its install form (VPM package, BOOTH .unitypackage), ships
    /// atelier.product.json in its own folder: {"license_id":"...","name":"..."}. A license counts as installed
    /// when a marker with its id exists anywhere under Assets/ or Packages/. No UnityEngine, headless-checked.
    /// </summary>
    internal static class ProductMarkers
    {
        public const string FileName = "atelier.product.json";

        /// <summary>
        /// The license ids of all markers under projectRoot/Assets and projectRoot/Packages. Hidden folders (dot-prefixed)
        /// and folders ending in "~" are skipped, as Unity does not import them. Never throws: unreadable folders and
        /// markers without a usable license_id are ignored. Call once per refresh and reuse the set.
        /// </summary>
        public static HashSet<string> Scan(string projectRoot)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(projectRoot)) return ids;
            Walk(Path.Combine(projectRoot, "Assets"), ids);
            Walk(Path.Combine(projectRoot, "Packages"), ids);
            return ids;
        }

        /// <summary>
        /// Absolute folders under Assets/ that hold a marker with this license id (contract v9 uses them as old-install
        /// locations). Same skip rules as <see cref="Scan"/>; never throws.
        /// </summary>
        public static List<string> FoldersFor(string projectRoot, string licenseId)
        {
            var folders = new List<string>();
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(licenseId)) return folders;
            WalkFolders(Path.Combine(projectRoot, "Assets"), licenseId, folders);
            return folders;
        }

        private static void WalkFolders(string directory, string licenseId, List<string> folders)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (var file in Directory.GetFiles(directory, FileName))
                {
                    try
                    {
                        string id;
                        if (TryReadLicenseId(File.ReadAllText(file), out id) && string.Equals(id, licenseId, StringComparison.OrdinalIgnoreCase))
                            folders.Add(directory);
                    }
                    catch (Exception) { }
                }
                foreach (var sub in Directory.GetDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                    WalkFolders(sub, licenseId, folders);
                }
            }
            catch (Exception) { }
        }

        /// <summary>The license_id of one marker file's text; false for malformed JSON or a missing or empty id.</summary>
        public static bool TryReadLicenseId(string json, out string licenseId)
        {
            licenseId = null;
            if (string.IsNullOrEmpty(json)) return false;
            var text = json.Trim();
            if (text.Length < 2 || text[0] != '{' || text[text.Length - 1] != '}') return false;
            string value;
            if (!LicenseFile.TryReadString(text, "license_id", out value)) return false;
            value = value.Trim();
            if (value.Length == 0) return false;
            licenseId = value;
            return true;
        }

        private static void Walk(string directory, HashSet<string> ids)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                foreach (var file in Directory.GetFiles(directory, FileName))
                {
                    try
                    {
                        string id;
                        if (TryReadLicenseId(File.ReadAllText(file), out id)) ids.Add(id);
                    }
                    catch (Exception)
                    {
                        // An unreadable marker is the same as no marker.
                    }
                }
                foreach (var sub in Directory.GetDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal)) continue;
                    Walk(sub, ids);
                }
            }
            catch (Exception)
            {
                // A folder that cannot be listed (permissions, a race with a delete) is skipped.
            }
        }
    }
}
