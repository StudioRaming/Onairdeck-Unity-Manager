using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The seam where a product installer takes over the old-install move (for example the MingToon installer, which
    /// already knows every shipped version of its Assets folder). Register on <see cref="LegacyInstall.Handlers"/>.
    /// </summary>
    internal interface ILegacyHandler
    {
        /// <summary>True when this handler owns the item (a known product folder found by the config table, a marker or the package).</summary>
        bool Handles(string projectRoot, LegacyProbe probe, LegacyItem item);

        /// <summary>
        /// True when the product's own installer does the whole clean-up: the Manager never moves or deletes the item, it imports the
        /// installer (<see cref="LegacyInstallerFile"/>) and waits (<see cref="LegacyHandoff"/>). <see cref="Validate"/> and
        /// <see cref="Detach"/> are not used for such an item.
        /// </summary>
        bool HandsOff(LegacyProbe probe, LegacyItem item);

        /// <summary>
        /// Project-relative paths that stop the move (user-added or changed files). Empty means the folder is exactly the
        /// old install and may be detached. Must not throw for ordinary problems: return the path that is wrong.
        /// </summary>
        IList<string> Validate(string projectRoot, LegacyProbe probe, LegacyItem item);

        /// <summary>
        /// Detaches the item itself into <paramref name="backupFolder"/> (the Manager's own backup folder for this move).
        /// Return true when done; the Manager then does not move it again. Return false to let the Manager move it.
        /// </summary>
        bool Detach(string projectRoot, LegacyItem item, string backupFolder);
    }

    /// <summary>
    /// The hand-off handler of OnAirDeckConfig.LegacyInstallers (MingToon): owns the configured old folder while the package itself is
    /// not in Packages/ yet. With the package present the item is not owned, so the inventory rule decides (fail closed).
    /// </summary>
    internal sealed class InstallerHandoffHandler : ILegacyHandler
    {
        public bool Handles(string projectRoot, LegacyProbe probe, LegacyItem item)
        {
            LegacyInstallerSpec spec;
            if (probe == null || item == null || item.Source == "guid" || !OnAirDeckConfig.LegacyInstallers.TryGetValue(probe.Name, out spec)) return false;
            if (!string.Equals(item.Path, spec.Folder, StringComparison.OrdinalIgnoreCase)) return false;
            return IsLegacyCase(projectRoot, spec, probe.Name);
        }

        /// <summary>The legacy case: the old Assets folder exists and the package is not resolved in Packages/.</summary>
        public static bool IsLegacyCase(string projectRoot, LegacyInstallerSpec spec, string package)
        {
            if (spec == null || string.IsNullOrEmpty(spec.Folder)) return false;
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(projectRoot, spec.Folder.Replace('/', System.IO.Path.DirectorySeparatorChar)))) return false;
            return InstalledPackages.Read(projectRoot, package) == null;
        }

        public bool HandsOff(LegacyProbe probe, LegacyItem item) { return true; }
        public IList<string> Validate(string projectRoot, LegacyProbe probe, LegacyItem item) { return new string[0]; }
        public bool Detach(string projectRoot, LegacyItem item, string backupFolder) { return false; }
    }

    /// <summary>Reads the inventory file a package declares in "atelier": {"legacyInventory": "&lt;path in zip&gt;"}. The format is open: swap <see cref="LegacyInventory.Reader"/>.</summary>
    internal interface ILegacyInventoryReader
    {
        /// <summary>
        /// Folder-relative path ('/' separators) to the lowercase SHA-256 hex (64 characters) of the file. Every entry must carry one:
        /// False when the data cannot be read or any entry has no valid hash; the move is then refused.
        /// </summary>
        bool TryRead(byte[] data, out Dictionary<string, string> files);
    }

    /// <summary>Provisional format: {"files": {"Scripts/A.cs": "&lt;sha256 hex&gt;", ...}}. Used until the product installer's real format is agreed.</summary>
    internal sealed class JsonInventoryReader : ILegacyInventoryReader
    {
        public bool TryRead(byte[] data, out Dictionary<string, string> files)
        {
            files = null;
            try
            {
                var fields = MiniJson.ParseObject(new UTF8Encoding(false).GetString(data));
                object node;
                var map = fields != null && fields.TryGetValue("files", out node) ? node as Dictionary<string, object> : null;
                if (map == null) return false;
                var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in map)
                {
                    // A null, a number, an empty or a malformed hash makes the whole inventory invalid: it must never turn into "any content".
                    var hash = pair.Value as string;
                    if (!IsSha256(hash) || pair.Key.Replace('\\', '/').Trim('/').Length == 0) return false;
                    result[pair.Key.Replace('\\', '/').Trim('/')] = hash.ToLowerInvariant();
                }
                files = result;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Exactly 64 hexadecimal characters.</summary>
        public static bool IsSha256(string text)
        {
            if (text == null || text.Length != 64) return false;
            foreach (var ch in text)
                if (!((ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F'))) return false;
            return true;
        }
    }

    internal static class LegacyInventory
    {
        public static ILegacyInventoryReader Reader = new JsonInventoryReader();

        /// <summary>
        /// Project-relative paths under <paramref name="item"/> that the inventory does not list, or whose content differs.
        /// Files the inventory lists but the folder lacks are fine (the user deleted them).
        /// </summary>
        public static List<string> Unmatched(string projectRoot, LegacyItem item, IDictionary<string, string> inventory)
        {
            var result = new List<string>();
            var full = Path.Combine(projectRoot, item.Path.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (File.Exists(full))
                {
                    if (!Matches(full, Path.GetFileName(full), inventory)) result.Add(item.Path);
                    return result;
                }
                foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(full.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    if (!Matches(file, relative, inventory)) result.Add(item.Path + "/" + relative);
                }
            }
            catch (Exception)
            {
                result.Add(item.Path);   // unreadable: not provably the old install
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        private static bool Matches(string file, string relative, IDictionary<string, string> inventory)
        {
            string hash;
            if (!inventory.TryGetValue(relative, out hash)) return false;
            if (hash.Length != 64) return false;   // a reader that lets an empty hash through still never skips the content check
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                var bytes = sha.ComputeHash(stream);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return string.Equals(sb.ToString(), hash, StringComparison.Ordinal);
            }
        }
    }
}
