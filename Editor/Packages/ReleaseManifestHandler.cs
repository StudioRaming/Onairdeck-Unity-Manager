using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The old Assets folder of a product that lists its own files (<see cref="OnAirDeckConfig.LegacyReleaseManifestFolders"/>: MingToon
    /// 0.1.7 and older carry Release/MingToon_ReleaseManifest.json with the path and SHA-256 of every shipped file). When every file in the
    /// folder is one of them and unchanged, the Manager moves the folder to Library/OnAirDeck/Backup (undoable) and installs the package; the
    /// package keeps the same GUIDs, so materials keep their shader. A file that is not listed or was changed (the user's own preset, an
    /// edit), or a git working copy, blocks the move and is named in the message. A .meta of a listed asset counts as listed whatever its
    /// content: Unity rewrites importer settings in .meta files, which are not user data.
    /// Owner request 2026-11-01: installing over an old BOOTH install must work even when the license version has no installer file.
    /// </summary>
    internal sealed class ReleaseManifestHandler : ILegacyHandler
    {
        public const string ReasonGit = "git";
        private static readonly string[] Junk = { "Thumbs.db", "desktop.ini", ".DS_Store" };

        public bool Handles(string projectRoot, LegacyProbe probe, LegacyItem item)
        {
            if (probe == null || item == null || item.Source == "guid") return false;
            string folder, manifests;
            if (!OnAirDeckConfig.LegacyAssetFolders.TryGetValue(probe.Name, out folder) || !OnAirDeckConfig.LegacyReleaseManifestFolders.TryGetValue(probe.Name, out manifests)) return false;
            if (!string.Equals(item.Path, folder, StringComparison.OrdinalIgnoreCase)) return false;
            return ManifestFiles(projectRoot, item.Path, manifests).Count > 0;
        }

        public bool HandsOff(LegacyProbe probe, LegacyItem item) { return false; }

        public IList<string> Validate(string projectRoot, LegacyProbe probe, LegacyItem item)
        {
            var root = Path.GetFullPath(projectRoot);
            var folder = Path.Combine(root, item.Path.Replace('/', Path.DirectorySeparatorChar));
            // A developer's checkout is never moved, even when its files match.
            if (Directory.Exists(Path.Combine(folder, ".git")) || File.Exists(Path.Combine(folder, ".git")))
                return new List<string> { item.Path + "/.git" };
            string manifests;
            OnAirDeckConfig.LegacyReleaseManifestFolders.TryGetValue(probe.Name, out manifests);
            manifests = manifests ?? "";
            var listed = ReadListed(root, item.Path, manifests);
            if (listed.Count == 0) return new List<string> { item.Path };
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ManifestFiles(root, item.Path, manifests))
            {
                own.Add(file);
                own.Add(file + ".meta");
            }
            var bad = new List<string>();
            foreach (var file in Directory.GetFiles(folder, "*", SearchOption.AllDirectories))
            {
                var relative = item.Path + "/" + file.Substring(folder.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                if (own.Contains(relative) || Array.IndexOf(Junk, Path.GetFileName(file)) >= 0) continue;
                if (relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    var asset = relative.Substring(0, relative.Length - 5);
                    if (listed.ContainsKey(relative) || listed.ContainsKey(asset) || HasListedChild(listed, asset)) continue;
                    bad.Add(relative);
                    continue;
                }
                try
                {
                    if (!LegacyInventory.Matches(file, relative, listed)) bad.Add(relative);
                }
                catch (Exception)
                {
                    bad.Add(relative);
                }
            }
            bad.Sort(StringComparer.Ordinal);
            return bad;
        }

        public bool Detach(string projectRoot, LegacyItem item, string backupFolder) { return false; }

        // A folder's .meta when the manifest lists only what is inside the folder.
        private static bool HasListedChild(Dictionary<string, string> listed, string folder)
        {
            var prefix = folder + "/";
            foreach (var key in listed.Keys) if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Project-relative paths of the *Manifest.json files in the folder's manifest folder, sorted.</summary>
        internal static List<string> ManifestFiles(string projectRoot, string itemPath, string manifests)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(manifests)) return result;
            try
            {
                var dir = Path.Combine(projectRoot, (itemPath + "/" + manifests).Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(dir)) return result;
                foreach (var file in Directory.GetFiles(dir, "*Manifest.json", SearchOption.TopDirectoryOnly))
                    result.Add(itemPath + "/" + manifests.Trim('/') + "/" + Path.GetFileName(file));
            }
            catch (Exception) { }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>Project-relative path to SHA-256 from every manifest, only paths inside the folder; an entry without a valid hash is left out.</summary>
        internal static Dictionary<string, string> ReadListed(string projectRoot, string itemPath, string manifests)
        {
            var listed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relative in ManifestFiles(projectRoot, itemPath, manifests))
            {
                try
                {
                    var text = File.ReadAllText(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)), new UTF8Encoding(false)).TrimStart('﻿');
                    var fields = MiniJson.ParseObject(text);
                    object node;
                    var files = fields != null && fields.TryGetValue("files", out node) ? node as List<object> : null;
                    if (files == null) continue;
                    foreach (var entry in files)
                    {
                        var map = entry as Dictionary<string, object>;
                        if (map == null) continue;
                        var path = (MiniJson.GetString(map, "path") ?? "").Replace('\\', '/').Trim('/');
                        var hash = (MiniJson.GetString(map, "sha256") ?? "").ToLowerInvariant();
                        if (path.Length == 0 || !JsonInventoryReader.IsSha256(hash)) continue;
                        if (!path.StartsWith(itemPath + "/", StringComparison.OrdinalIgnoreCase)) continue;
                        listed[path] = hash;
                    }
                }
                catch (Exception) { }
            }
            return listed;
        }
    }
}
