using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>A .meta file in the project that already uses a GUID the incoming packages bring.</summary>
    internal sealed class GuidCollision
    {
        public string Guid = "";
        /// <summary>Project-relative path of the colliding .meta, '/' separators.</summary>
        public string Path = "";
        /// <summary>Top-level Packages/&lt;folder&gt; the file is in; "" when it is under Assets/.</summary>
        public string Container = "";
        /// <summary>package.json name and version of the container; "" when there is none or it cannot be read.</summary>
        public string PackageName = "";
        public string PackageVersion = "";
        /// <summary>
        /// Under Assets/: project-relative path of the nearest ancestor folder that holds a package.json (an external package imported into
        /// Assets, never the old product install); "" when the file is in plain Assets content. PackageName/PackageVersion describe it.
        /// </summary>
        public string AssetsPackage = "";
    }

    /// <summary>An installed package that holds colliding files and could be put back in place by reinstalling it.</summary>
    internal sealed class ReinstallCandidate
    {
        public string Name = "";
        public string Version = "";
        public string Folder = "";
        public int Files;
    }

    /// <summary>
    /// Two assets with one GUID break references (materials, renderer features). This happens when an older
    /// install put files inside another package's folder (a .unitypackage import into Packages/&lt;core&gt;) and a
    /// new package brings the same files under its own folder. The scan runs on the .meta files only.
    /// </summary>
    internal static class GuidCollisions
    {
        private static readonly Regex GuidLine = new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private const int MaxMetaChars = 1024;
        private const int ShownPaths = 10;

        /// <summary>Every GUID in the .meta entries of a zip, read without extracting it.</summary>
        public static HashSet<string> ReadZipGuids(string zipPath)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            using (var stream = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (!entry.FullName.EndsWith(".meta", StringComparison.Ordinal)) continue;
                    using (var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), true))
                    {
                        var guid = FirstGuid(reader);
                        if (guid != null) guids.Add(guid);
                    }
                }
            }
            return guids;
        }

        /// <summary>
        /// Looks in Assets/ and in every top-level folder of Packages/ (embedded packages; Library/PackageCache is
        /// not touched) for .meta files that use one of <paramref name="guids"/>. Folders in
        /// <paramref name="excludedFolders"/> (the ones the transaction replaces) are skipped, as are folders
        /// Unity ignores (a name ending in "~" or starting with ".").
        /// </summary>
        public static List<GuidCollision> Scan(string projectRoot, ISet<string> guids, IEnumerable<string> excludedFolders)
        {
            var found = new List<GuidCollision>();
            if (guids == null || guids.Count == 0) return found;
            var excluded = new List<string>();
            foreach (var folder in excludedFolders) excluded.Add(Normalize(folder));

            var root = Path.GetFullPath(projectRoot);
            ScanFolder(root, Path.Combine(root, "Assets"), "", guids, excluded, found);
            var packages = Path.Combine(root, "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var folder in Directory.GetDirectories(packages))
                {
                    if (Ignored(Path.GetFileName(folder)) || excluded.Contains(Normalize(folder))) continue;
                    ScanFolder(root, folder, folder, guids, excluded, found);
                }
            }
            return found;
        }

        /// <summary>
        /// Splits collisions into packages that can be reinstalled (they have a readable package.json) and
        /// <paramref name="blocking"/> ones (Assets/, or a folder that is no package).
        /// </summary>
        public static List<ReinstallCandidate> Candidates(IEnumerable<GuidCollision> collisions, List<GuidCollision> blocking)
        {
            var candidates = new List<ReinstallCandidate>();
            foreach (var collision in collisions)
            {
                if (collision.Container.Length == 0 || collision.PackageName.Length == 0 || collision.PackageVersion.Length == 0)
                {
                    blocking.Add(collision);
                    continue;
                }
                var candidate = candidates.Find(c => c.Folder == collision.Container);
                if (candidate == null)
                {
                    candidate = new ReinstallCandidate { Name = collision.PackageName, Version = collision.PackageVersion, Folder = collision.Container };
                    candidates.Add(candidate);
                }
                candidate.Files++;
            }
            return candidates;
        }

        /// <summary>
        /// Why the install is refused. Files inside a package folder (another vendor's, or a stub) get one line per
        /// package, in the user's language: they are never offered for moving. Leftover files under Assets/ keep the
        /// path list.
        /// </summary>
        public static string Refusal(IList<GuidCollision> collisions)
        {
            var language = LegacyInstallText.Language();
            var perPackage = new List<string>();
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var assets = new List<GuidCollision>();
            foreach (var collision in collisions)
            {
                if (collision.Container.Length == 0 && collision.AssetsPackage.Length == 0) { assets.Add(collision); continue; }
                var folderPath = collision.Container.Length > 0 ? collision.Container : collision.AssetsPackage;
                var label = collision.PackageName.Length > 0 ? collision.PackageName : Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, '/'));
                int n;
                counts.TryGetValue(label, out n);
                if (n == 0) perPackage.Add(label);
                counts[label] = n + 1;
            }
            var sb = new StringBuilder();
            foreach (var label in perPackage)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(LegacyInstallText.ForeignRefusal(language, label, counts[label]));
            }
            if (assets.Count > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append("Cannot install: ").Append(assets.Count).Append(" file(s) under Assets/ still use GUIDs that the new package brings, which would break references. Up to ")
                  .Append(ShownPaths).Append(" shown:");
                for (var i = 0; i < Math.Min(ShownPaths, assets.Count); i++)
                    sb.Append("\n  ").Append(assets[i].Path).Append(" (").Append(assets[i].Guid).Append(")");
                if (assets.Count > ShownPaths) sb.Append("\n  and ").Append(assets.Count - ShownPaths).Append(" more");
            }
            return sb.ToString();
        }

        // ---------- helpers ----------

        private static void ScanFolder(string root, string folder, string container, ISet<string> guids, List<string> excluded, List<GuidCollision> found)
        {
            if (!Directory.Exists(folder)) return;
            var name = "";
            var version = "";
            if (container.Length > 0)
            {
                var manifest = ReadManifest(folder);
                if (manifest != null) { name = manifest.Name; version = manifest.Version; }
            }
            // Each folder carries the nearest package boundary above it (an ancestor with a package.json under Assets).
            var pending = new Stack<KeyValuePair<string, string>>();
            pending.Push(new KeyValuePair<string, string>(folder, ""));
            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                var current = entry.Key;
                var boundary = entry.Value;
                if (container.Length == 0 && boundary.Length == 0 && !SamePath(current, folder) && File.Exists(Path.Combine(current, "package.json")))
                    boundary = Path.GetFullPath(current).Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
                var boundaryName = name;
                var boundaryVersion = version;
                if (boundary.Length > 0)
                {
                    var inner = ReadManifest(Path.Combine(root, boundary.Replace('/', Path.DirectorySeparatorChar)));
                    boundaryName = inner != null ? inner.Name : "";
                    boundaryVersion = inner != null ? inner.Version : "";
                }
                foreach (var meta in Directory.EnumerateFiles(current, "*.meta"))
                {
                    var guid = ReadGuid(meta);
                    if (guid == null || !guids.Contains(guid)) continue;
                    found.Add(new GuidCollision
                    {
                        Guid = guid,
                        Path = Path.GetFullPath(meta).Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/'),
                        Container = container,
                        PackageName = boundaryName,
                        PackageVersion = boundaryVersion,
                        AssetsPackage = boundary
                    });
                }
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    if (Ignored(Path.GetFileName(child)) || excluded.Contains(Normalize(child))) continue;
                    pending.Push(new KeyValuePair<string, string>(child, boundary));
                }
            }
        }

        private static bool SamePath(string a, string b) { return Normalize(a) == Normalize(b); }

        /// <summary>Every GUID in the .meta files of an extracted package folder.</summary>
        public static HashSet<string> ReadFolderGuids(string folder)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var meta in Directory.EnumerateFiles(folder, "*.meta", SearchOption.AllDirectories))
            {
                var guid = ReadGuid(meta);
                if (guid != null) guids.Add(guid);
            }
            return guids;
        }

        private static PackageManifest ReadManifest(string folder)
        {
            try
            {
                var path = Path.Combine(folder, "package.json");
                return File.Exists(path) ? PackageZip.ParseManifest(File.ReadAllText(path, Encoding.UTF8), false) : null;
            }
            catch (PackageInstallException) { return null; }
            catch (IOException) { return null; }
        }

        private static bool Ignored(string folderName)
        {
            return folderName.StartsWith(".", StringComparison.Ordinal) || folderName.EndsWith("~", StringComparison.Ordinal);
        }

        private static string Normalize(string folder)
        {
            return Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        }

        /// <summary>The GUID of one .meta file, null when unreadable.</summary>
        internal static string GuidOfMeta(string metaPath) { return ReadGuid(metaPath); }

        private static string ReadGuid(string metaPath)
        {
            try
            {
                using (var reader = new StreamReader(metaPath, Encoding.UTF8, true))
                    return FirstGuid(reader);
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        private static string FirstGuid(StreamReader reader)
        {
            var buffer = new char[MaxMetaChars];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return null;
            var match = GuidLine.Match(new string(buffer, 0, read));
            return match.Success ? match.Groups[1].Value : null;
        }
    }
}
