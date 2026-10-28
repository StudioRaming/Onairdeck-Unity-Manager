using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>The identity and dependencies a package.json declares.</summary>
    internal sealed class PackageManifest
    {
        public string Name = "";
        public string Version = "";
        /// <summary>Exact versions only (see <see cref="PackageVersion.IsExact"/>), in declaration order.</summary>
        public readonly List<KeyValuePair<string, string>> VpmDependencies = new List<KeyValuePair<string, string>>();
        /// <summary>The same dependencies with the value exactly as package.json wrote it ("=0.1.16" or "0.1.16"), same order.</summary>
        public readonly List<KeyValuePair<string, string>> VpmDependenciesRaw = new List<KeyValuePair<string, string>>();
        /// <summary>Contract v9: "atelier": {"replacesAssetFolders": [...]} as written; unchecked, LegacyInstall applies the guard rails.</summary>
        public readonly List<string> ReplacesAssetFolders = new List<string>();
        /// <summary>"atelier": {"legacyInventory": "&lt;path in zip&gt;"}: the file listing exactly what the old Assets install held; "" when absent.</summary>
        public string LegacyInventory = "";
    }

    /// <summary>
    /// Validation and extraction of a package zip. A zip comes from a server (seller-authored for the
    /// add-on), so every entry is checked before anything is written, and one bad entry fails the
    /// whole package instead of being skipped.
    /// </summary>
    internal static class PackageZip
    {
        public const int MaxEntries = 50000;
        public const long MaxUncompressedBytes = 2L * 1024 * 1024 * 1024;
        public const long MaxPackageJsonBytes = 1024 * 1024;

        /// <summary>
        /// Checks every entry and reads the root package.json. <paramref name="expectedName"/> and
        /// <paramref name="expectedVersion"/> must equal what the listing promised.
        /// </summary>
        public static PackageManifest Validate(string zipPath, string expectedName, string expectedVersion)
        {
            using (var stream = File.OpenRead(zipPath))
            using (var archive = OpenArchive(stream))
            {
                PackageManifest manifest = null;
                foreach (var entry in ValidatedEntries(archive))
                {
                    if (entry.Relative == "package.json" && !entry.IsDirectory) manifest = ReadManifest(entry.Source);
                }
                if (manifest == null) throw new PackageInstallException("The package has no package.json at its root.");
                CheckIdentity(manifest, expectedName, expectedVersion);
                return manifest;
            }
        }

        /// <summary>
        /// Validates like <see cref="Validate"/> and writes the files into <paramref name="destination"/>,
        /// which must not exist yet. Returns the manifest.
        /// </summary>
        public static PackageManifest Extract(string zipPath, string destination, string expectedName, string expectedVersion)
        {
            var manifest = Validate(zipPath, expectedName, expectedVersion);
            if (Directory.Exists(destination) || File.Exists(destination))
                throw new PackageInstallException("Staging folder already exists: " + destination);
            Directory.CreateDirectory(destination);
            using (var stream = File.OpenRead(zipPath))
            using (var archive = OpenArchive(stream))
            {
                foreach (var entry in ValidatedEntries(archive))
                {
                    var target = Path.GetFullPath(Path.Combine(destination, entry.Relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!PathSafety.IsInside(destination, target))
                        throw new PackageInstallException("Unsafe entry in package: " + entry.Source.FullName);
                    if (entry.IsDirectory)
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = entry.Source.Open())
                    using (var output = File.Create(target))
                    {
                        input.CopyTo(output);
                    }
                }
            }
            return manifest;
        }

        /// <summary>
        /// Parses package.json text. Throws PackageInstallException when it is unusable. A dependency
        /// range throws too when <paramref name="strict"/>; otherwise it is left out (an installed package
        /// the Manager did not validate).
        /// </summary>
        public static PackageManifest ParseManifest(string json, bool strict = true)
        {
            var fields = MiniJson.ParseObject(json);
            if (fields == null) throw new PackageInstallException("package.json is not a valid JSON object.");
            var manifest = new PackageManifest
            {
                Name = MiniJson.GetString(fields, "name") ?? "",
                Version = MiniJson.GetString(fields, "version") ?? ""
            };
            object deps;
            if (fields.TryGetValue("vpmDependencies", out deps) && deps != null)
            {
                var map = deps as Dictionary<string, object>;
                if (map == null) throw new PackageInstallException("package.json vpmDependencies must be an object.");
                foreach (var pair in map)
                {
                    var raw = pair.Value as string;
                    string version;
                    if (!PackageVersion.TryParsePin(raw, out version))
                    {
                        if (!strict) continue;
                        throw new PackageInstallException("Dependency " + pair.Key + " must name an exact version, not \"" + (raw ?? "") + "\".");
                    }
                    manifest.VpmDependencies.Add(new KeyValuePair<string, string>(pair.Key, version));
                    manifest.VpmDependenciesRaw.Add(new KeyValuePair<string, string>(pair.Key, raw));
                }
            }
            object atelier;
            var atelierMap = fields.TryGetValue("atelier", out atelier) ? atelier as Dictionary<string, object> : null;
            if (atelierMap != null) manifest.LegacyInventory = MiniJson.GetString(atelierMap, "legacyInventory") ?? "";
            object replaces;
            var list = atelierMap != null && atelierMap.TryGetValue("replacesAssetFolders", out replaces) ? replaces as List<object> : null;
            if (list != null)
                foreach (var entry in list)
                {
                    var text = entry as string;
                    if (!string.IsNullOrEmpty(text)) manifest.ReplacesAssetFolders.Add(text);
                }
            return manifest;
        }

        public const long MaxInventoryBytes = 8L * 1024 * 1024;

        /// <summary>Reads one file of the zip (a validated entry, at most <paramref name="maxBytes"/>). Throws when it is missing or too large.</summary>
        public static byte[] ReadEntry(string zipPath, string relative, long maxBytes)
        {
            using (var stream = File.OpenRead(zipPath))
            using (var archive = OpenArchive(stream))
            {
                var wanted = (relative ?? "").Replace('\\', '/').Trim('/');
                foreach (var entry in ValidatedEntries(archive))
                {
                    if (entry.IsDirectory || !string.Equals(entry.Relative, wanted, StringComparison.Ordinal)) continue;
                    if (entry.Source.Length > maxBytes) throw new PackageInstallException("The package file " + wanted + " is unreasonably large.");
                    using (var input = entry.Source.Open())
                    using (var memory = new MemoryStream())
                    {
                        input.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            throw new PackageInstallException("The package has no file " + NormalizeEntry(relative) + ".");
        }

        private static string NormalizeEntry(string relative) { return (relative ?? "").Replace('\\', '/').Trim('/'); }

        private sealed class ValidEntry
        {
            public ZipArchiveEntry Source;
            public string Relative;
            public bool IsDirectory;
        }

        private static ZipArchive OpenArchive(Stream stream)
        {
            try { return new ZipArchive(stream, ZipArchiveMode.Read); }
            catch (InvalidDataException e) { throw new PackageInstallException("The package is not a valid zip file.", e); }
        }

        // Validates every entry up front, so extraction never starts on a bad zip.
        private static List<ValidEntry> ValidatedEntries(ZipArchive archive)
        {
            if (archive.Entries.Count > MaxEntries) throw new PackageInstallException("The package has too many files.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<ValidEntry>();
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                var isDirectory = name.EndsWith("/", StringComparison.Ordinal) || name.EndsWith("\\", StringComparison.Ordinal);
                string relative;
                if (!IsPlainRelative(name, out relative))
                    throw new PackageInstallException("Unsafe entry in package: \"" + name + "\".");
                if (relative.StartsWith("__MACOSX/", StringComparison.Ordinal)) continue; // Finder resource forks
                // Names that differ only in case would collide on Windows and macOS.
                if (!seen.Add(relative)) throw new PackageInstallException("Duplicate entry in package: \"" + name + "\".");
                total += entry.Length;
                if (total > MaxUncompressedBytes) throw new PackageInstallException("The package is too large when unpacked.");
                result.Add(new ValidEntry { Source = entry, Relative = relative, IsDirectory = isDirectory });
            }
            return result;
        }

        /// <summary>
        /// PathSafety.SafeRelativePath cleans a name (invalid characters become '_', trailing dots go).
        /// For a package that cleaning is itself evidence of a hostile or broken zip, so any
        /// difference from the plain normalized path rejects the entry.
        /// </summary>
        private static bool IsPlainRelative(string entryName, out string relative)
        {
            relative = PathSafety.SafeRelativePath(entryName);
            if (relative == null) return false;
            var plain = new StringBuilder();
            foreach (var part in entryName.Replace('\\', '/').Split(new[] { '/' }))
            {
                if (part.Length == 0 || part == ".") continue;
                if (plain.Length > 0) plain.Append('/');
                plain.Append(part);
            }
            return string.Equals(plain.ToString(), relative, StringComparison.Ordinal);
        }

        private static PackageManifest ReadManifest(ZipArchiveEntry entry)
        {
            if (entry.Length > MaxPackageJsonBytes) throw new PackageInstallException("package.json is unreasonably large.");
            using (var reader = new StreamReader(entry.Open(), new UTF8Encoding(false), true))
            {
                return ParseManifest(reader.ReadToEnd());
            }
        }

        private static void CheckIdentity(PackageManifest manifest, string expectedName, string expectedVersion)
        {
            if (!string.Equals(manifest.Name, expectedName, StringComparison.Ordinal))
                throw new PackageInstallException("The package declares the name \"" + manifest.Name + "\" but \"" + expectedName + "\" was expected.");
            if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
                throw new PackageInstallException("The package declares version \"" + manifest.Version + "\" but \"" + expectedVersion + "\" was expected.");
        }
    }
}
