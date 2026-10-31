using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OnAirDeck.License;
using System.Threading;

namespace OnAirDeck.UnityManager
{
    /// <summary>The kinds of trouble the standing project check finds.</summary>
    internal enum ConflictKind
    {
        /// <summary>Two .meta files hold one GUID: Unity keeps one of them and references to the other break.</summary>
        DuplicateGuid,
        /// <summary>An installed product package and an old copy of the same product under Assets/.</summary>
        OldCopyBesidePackage,
        /// <summary>Two assembly definitions or managed DLLs with one assembly name: scripts stop compiling.</summary>
        DuplicateAssembly,
        /// <summary>A package in Packages/&lt;folder&gt; and also in Packages/manifest.json: Unity uses the folder and ignores the entry.</summary>
        EmbeddedAndManifest,
    }

    /// <summary>One problem of the project, with what a fix may touch.</summary>
    internal sealed class ProjectConflict
    {
        public ConflictKind Kind;
        /// <summary>A GUID, an assembly name, a package name or a group of folders: with <see cref="Kind"/> it names the conflict.</summary>
        public string Subject = "";
        /// <summary>Project-relative paths ('/' separators) of every copy, sorted.</summary>
        public readonly List<string> Paths = new List<string>();
        /// <summary>How many GUIDs a grouped duplicate-GUID conflict holds.</summary>
        public int Count = 1;
        /// <summary>The installed package this is about ("" when none): the one a reinstall would put back.</summary>
        public string Package = "";
        public string Version = "";
        /// <summary>Paths under Assets/ that "move to backup" may take (each passes the old-install guard rails).</summary>
        public readonly List<string> Movable = new List<string>();
        /// <summary>Only a reinstall of <see cref="Package"/> cleans this up (its own installer, or files inside a package folder).</summary>
        public bool NeedsReinstall;
        /// <summary>The value of the manifest.json entry (<see cref="ConflictKind.EmbeddedAndManifest"/>).</summary>
        public string ManifestValue = "";

        public string Key { get { return Kind + ":" + Subject; } }
    }

    /// <summary>What the check needs to know beyond the files: which packages the account's licenses install (name → license id).</summary>
    internal sealed class ConflictScanInput
    {
        public string ProjectRoot = "";
        public Dictionary<string, string> LicenseOfPackage = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Look at the asmdef and DLL names in Library/PackageCache too (registry and git packages).</summary>
        public bool IncludePackageCache = true;
    }

    /// <summary>
    /// The standing project check (U3): reads Assets/, the embedded packages under Packages/, Packages/manifest.json and the
    /// assembly names in Library/PackageCache, and lists what conflicts. Files only, no UnityEngine, so it runs on a worker thread
    /// and in the headless checks; it never changes anything (<see cref="ProjectConflictFixes"/> does, on a click).
    /// </summary>
    internal static class ProjectConflictScan
    {
        private static readonly Regex GuidLine = new Regex(@"^guid:\s*([0-9a-f]{32})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        private static readonly Regex AsmdefName = new Regex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.CultureInvariant);
        private static readonly Regex EditorPlatform = new Regex(@"first:\s*\n\s*Editor:\s*Editor\s*\n\s*second:\s*\n\s*enabled:\s*(\d)", RegexOptions.CultureInvariant);
        private static readonly Regex AnyPlatform = new Regex(@"first:\s*\n\s*Any:\s*\n\s*second:\s*\n\s*enabled:\s*(\d)", RegexOptions.CultureInvariant);
        private static readonly Regex ExcludeEditor = new Regex(@"Exclude Editor:\s*1", RegexOptions.CultureInvariant);

        /// <summary>One copy found on disk: its project-relative path and the root it came from.</summary>
        private sealed class Found
        {
            public string Path = "";
            /// <summary>"Packages/&lt;folder&gt;" or "Library/PackageCache/&lt;folder&gt;" for a package; "" for Assets/.</summary>
            public string Container = "";
            public string PackageName = "";
            public string PackageVersion = "";
            public bool IsFolder;
        }

        private sealed class Inventory
        {
            public readonly List<KeyValuePair<string, Found>> Metas = new List<KeyValuePair<string, Found>>();   // guid → copy
            public readonly List<KeyValuePair<string, Found>> Assemblies = new List<KeyValuePair<string, Found>>(); // name → copy
            public readonly List<Found> Packages = new List<Found>();   // embedded and cached package roots
        }

        public static List<ProjectConflict> Run(ConflictScanInput input, CancellationToken cancel)
        {
            var root = Path.GetFullPath(input.ProjectRoot);
            var inventory = Collect(root, input.IncludePackageCache, cancel);
            var list = new List<ProjectConflict>();
            list.AddRange(OldCopies(root, inventory, input.LicenseOfPackage));
            list.AddRange(DuplicateGuids(root, inventory, list));
            list.AddRange(DuplicateAssemblies(inventory));
            list.AddRange(EmbeddedAndManifest(root, inventory));
            foreach (var conflict in list) conflict.Paths.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // ------------------------------------------------------------ inventory

        private static Inventory Collect(string root, bool withCache, CancellationToken cancel)
        {
            var inventory = new Inventory();
            Walk(root, Path.Combine(root, "Assets"), new Found(), true, inventory, cancel);
            var packages = Path.Combine(root, "Packages");
            if (Directory.Exists(packages))
                foreach (var folder in Directory.GetDirectories(packages))
                {
                    if (Ignored(Path.GetFileName(folder))) continue;
                    var owner = PackageRoot(root, folder);
                    if (owner.PackageName.Length > 0) inventory.Packages.Add(owner);
                    Walk(root, folder, owner, true, inventory, cancel);
                }
            var cache = Path.Combine(root, "Library", "PackageCache");
            if (withCache && Directory.Exists(cache))
                foreach (var folder in Directory.GetDirectories(cache))
                {
                    var owner = PackageRoot(root, folder);
                    if (owner.PackageName.Length > 0) inventory.Packages.Add(owner);
                    // Cached packages are read only for their assembly names: Unity rewrites a GUID that clashes with one of them.
                    Walk(root, folder, owner, false, inventory, cancel);
                }
            return inventory;
        }

        private static Found PackageRoot(string root, string folder)
        {
            var found = new Found { Container = Relative(root, folder) };
            try
            {
                var manifest = Path.Combine(folder, "package.json");
                if (File.Exists(manifest))
                {
                    var json = MiniJson.ParseObject(File.ReadAllText(manifest, Encoding.UTF8));
                    found.PackageName = MiniJson.GetString(json, "name") ?? "";
                    found.PackageVersion = MiniJson.GetString(json, "version") ?? "";
                }
            }
            catch (Exception)
            {
                // An unreadable package.json names no package.
            }
            return found;
        }

        private static void Walk(string root, string start, Found owner, bool withMetas, Inventory inventory, CancellationToken cancel)
        {
            if (!Directory.Exists(start)) return;
            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                cancel.ThrowIfCancellationRequested();
                var current = pending.Pop();
                string[] files;
                try { files = Directory.GetFiles(current); }
                catch (Exception) { continue; }
                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (withMetas && name.EndsWith(".meta", StringComparison.Ordinal))
                    {
                        var asset = file.Substring(0, file.Length - 5);
                        // A .meta without its asset is gone at the next import; it holds nothing.
                        if (!File.Exists(asset) && !Directory.Exists(asset)) continue;
                        var guid = GuidOf(file);
                        if (guid != null)
                        {
                            var copy = Copy(root, asset, owner);
                            copy.IsFolder = Directory.Exists(asset);
                            inventory.Metas.Add(new KeyValuePair<string, Found>(guid, copy));
                        }
                    }
                    else if (name.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                    {
                        var assembly = AsmdefNameOf(file);
                        if (assembly != null) inventory.Assemblies.Add(new KeyValuePair<string, Found>(assembly, Copy(root, file, owner)));
                    }
                    else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && IsManagedAssembly(file) && EditorEnabled(ReadMeta(file)))
                        inventory.Assemblies.Add(new KeyValuePair<string, Found>(Path.GetFileNameWithoutExtension(file), Copy(root, file, owner)));
                }
                string[] folders;
                try { folders = Directory.GetDirectories(current); }
                catch (Exception) { continue; }
                foreach (var folder in folders)
                    if (!Ignored(Path.GetFileName(folder))) pending.Push(folder);
            }
        }

        private static Found Copy(string root, string path, Found owner)
        {
            return new Found { Path = Relative(root, path), Container = owner.Container, PackageName = owner.PackageName, PackageVersion = owner.PackageVersion };
        }

        // ------------------------------------------------------------ the four checks

        /// <summary>
        /// Every installed package the account's licenses (or the Manager's own config) know, checked with the install's own
        /// old-copy rules (<see cref="LegacyInstall.DetectKnown"/>: the config table and atelier.product.json markers). Items the
        /// guard rails accept are movable; a blocked item or one the product's installer must take is fixed by a reinstall.
        /// </summary>
        private static List<ProjectConflict> OldCopies(string root, Inventory inventory, Dictionary<string, string> licenseOfPackage)
        {
            var list = new List<ProjectConflict>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var package in inventory.Packages)
            {
                if (!seen.Add(package.PackageName)) continue;
                string licenseId;
                licenseOfPackage.TryGetValue(package.PackageName, out licenseId);
                if (string.IsNullOrEmpty(licenseId) && !OnAirDeckConfig.LegacyAssetFolders.ContainsKey(package.PackageName)) continue;
                LegacyPlan plan;
                try
                {
                    var probe = new LegacyProbe { Name = package.PackageName, Version = package.PackageVersion, LicenseId = licenseId ?? "" };
                    plan = LegacyInstall.DetectKnown(root, new List<LegacyProbe> { probe });
                }
                catch (Exception)
                {
                    continue;
                }
                if (plan.IsEmpty) continue;
                var conflict = new ProjectConflict
                {
                    Kind = ConflictKind.OldCopyBesidePackage, Subject = package.PackageName,
                    Package = package.PackageName, Version = package.PackageVersion,
                };
                conflict.Paths.Add(package.Container);
                foreach (var item in plan.Items)
                {
                    conflict.Paths.Add(item.Path);
                    if (item.Blocked || item.HandOff) conflict.NeedsReinstall = true;
                    else conflict.Movable.Add(item.Path);
                }
                list.Add(conflict);
            }
            return list;
        }

        /// <summary>
        /// GUIDs held by more than one .meta under Assets/ and the embedded packages, grouped by where the copies live (an old copy
        /// gives hundreds of GUIDs, but one card). Copies already covered by an old-copy conflict are left to it. A copy under Assets/
        /// whose twin is in a package is movable; the whole Assets folder moves when every asset in it is such a copy.
        /// </summary>
        private static List<ProjectConflict> DuplicateGuids(string root, Inventory inventory, List<ProjectConflict> oldCopies)
        {
            var byGuid = new Dictionary<string, List<Found>>(StringComparer.Ordinal);
            foreach (var pair in inventory.Metas)
            {
                List<Found> copies;
                if (!byGuid.TryGetValue(pair.Key, out copies)) byGuid[pair.Key] = copies = new List<Found>();
                copies.Add(pair.Value);
            }
            var covered = new List<string>();
            foreach (var conflict in oldCopies) foreach (var path in conflict.Paths) if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) covered.Add(path);

            var groups = new Dictionary<string, ProjectConflict>(StringComparer.Ordinal);
            var strayGuids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in byGuid)
            {
                if (pair.Value.Count < 2) continue;
                var homes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var inPackage = false;
                var allCovered = true;
                foreach (var copy in pair.Value)
                {
                    homes.Add(Home(copy));
                    if (copy.Container.Length > 0) inPackage = true;
                    else if (!IsUnderAny(copy.Path, covered)) allCovered = false;
                }
                if (allCovered && pair.Value.Exists(c => c.Container.Length == 0)) continue;
                var key = string.Join(" | ", new List<string>(homes).ToArray());
                ProjectConflict group;
                if (!groups.TryGetValue(key, out group))
                {
                    group = new ProjectConflict { Kind = ConflictKind.DuplicateGuid, Subject = key, Count = 0 };
                    groups[key] = group;
                }
                group.Count++;
                foreach (var copy in pair.Value)
                {
                    if (!group.Paths.Contains(copy.Path)) group.Paths.Add(copy.Path);
                    if (copy.Container.Length > 0 && group.Package.Length == 0 && copy.PackageName.Length > 0)
                    {
                        group.Package = copy.PackageName;
                        group.Version = copy.PackageVersion;
                    }
                }
                // Copies inside two package folders: only reinstalling the package that should own them helps.
                var assetsCopies = pair.Value.FindAll(c => c.Container.Length == 0);
                if (assetsCopies.Count == 0) group.NeedsReinstall = true;
                if (inPackage) foreach (var copy in assetsCopies) strayGuids.Add(pair.Key);
            }

            // Movable: the highest Assets folder whose every asset is a stray copy, else the stray files themselves.
            var strayPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in inventory.Metas)
                if (pair.Value.Container.Length == 0 && strayGuids.Contains(pair.Key)) strayPaths.Add(pair.Value.Path);
            foreach (var group in groups.Values)
            {
                foreach (var path in group.Paths)
                {
                    if (!strayPaths.Contains(path)) continue;
                    // A copied folder moves whole only when everything in it is a copy; else its copied files move one by one.
                    if (Directory.Exists(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))) && !FolderIsPure(root, path, strayPaths)) continue;
                    var target = HighestPure(root, path, strayPaths);
                    if (!IsUnderAny(target, group.Movable)) group.Movable.Add(target);
                }
                group.Movable.RemoveAll(m => group.Movable.Exists(o => !ReferenceEquals(o, m) && o != m && m.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase)));
            }
            return new List<ProjectConflict>(groups.Values);
        }

        /// <summary>Assembly names that two asmdef files or Editor-enabled managed DLLs share. A DLL under Assets/ beside a package copy is movable.</summary>
        private static List<ProjectConflict> DuplicateAssemblies(Inventory inventory)
        {
            var byName = new Dictionary<string, List<Found>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in inventory.Assemblies)
            {
                List<Found> copies;
                if (!byName.TryGetValue(pair.Key, out copies)) byName[pair.Key] = copies = new List<Found>();
                copies.Add(pair.Value);
            }
            var list = new List<ProjectConflict>();
            foreach (var pair in byName)
            {
                if (pair.Value.Count < 2) continue;
                var conflict = new ProjectConflict { Kind = ConflictKind.DuplicateAssembly, Subject = pair.Key };
                var inPackage = pair.Value.Exists(c => c.Container.Length > 0);
                foreach (var copy in pair.Value)
                {
                    conflict.Paths.Add(copy.Path);
                    if (copy.Container.Length > 0 && conflict.Package.Length == 0 && copy.PackageName.Length > 0)
                    {
                        conflict.Package = copy.PackageName;
                        conflict.Version = copy.PackageVersion;
                    }
                    if (inPackage && copy.Container.Length == 0 && copy.Path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) conflict.Movable.Add(copy.Path);
                }
                list.Add(conflict);
            }
            return list;
        }

        /// <summary>A package in Packages/&lt;folder&gt; that Packages/manifest.json also lists (other than as "file:" that same folder).</summary>
        private static List<ProjectConflict> EmbeddedAndManifest(string root, Inventory inventory)
        {
            var list = new List<ProjectConflict>();
            Dictionary<string, object> dependencies;
            var manifest = Path.Combine(root, "Packages", "manifest.json");
            try
            {
                if (!File.Exists(manifest)) return list;
                var json = MiniJson.ParseObject(File.ReadAllText(manifest, Encoding.UTF8));
                object value;
                if (json == null || !json.TryGetValue("dependencies", out value)) return list;
                dependencies = value as Dictionary<string, object>;
                if (dependencies == null) return list;
            }
            catch (Exception)
            {
                return list;
            }
            foreach (var package in inventory.Packages)
            {
                if (!package.Container.StartsWith("Packages/", StringComparison.Ordinal)) continue;
                object entry;
                if (!dependencies.TryGetValue(package.PackageName, out entry)) continue;
                var text = entry as string ?? "";
                var folder = package.Container.Substring("Packages/".Length);
                if (text.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && string.Equals(text.Substring(5).Trim().TrimEnd('/'), folder, StringComparison.OrdinalIgnoreCase)) continue;
                var conflict = new ProjectConflict
                {
                    Kind = ConflictKind.EmbeddedAndManifest, Subject = package.PackageName,
                    Package = package.PackageName, Version = package.PackageVersion, ManifestValue = text,
                };
                conflict.Paths.Add(package.Container);
                conflict.Paths.Add("Packages/manifest.json");
                list.Add(conflict);
            }
            return list;
        }

        // ------------------------------------------------------------ helpers

        /// <summary>Where a copy lives for grouping: its package folder, or its folder cut to the first two levels under Assets/ (a folder's own .meta counts as inside it).</summary>
        private static string Home(Found copy)
        {
            if (copy.Container.Length > 0) return copy.Container;
            var folder = copy.IsFolder ? copy.Path : ParentOf(copy.Path);
            var segments = folder.Split('/');
            return string.Join("/", segments, 0, Math.Min(3, segments.Length));
        }

        /// <summary>The highest folder above <paramref name="path"/> (below Assets/) whose every asset is in <paramref name="stray"/>; the path itself when none.</summary>
        private static string HighestPure(string root, string path, HashSet<string> stray)
        {
            var best = path;
            var parent = ParentOf(path);
            while (parent.Contains("/") && stray.Contains(parent) && FolderIsPure(root, parent, stray))
            {
                best = parent;
                parent = ParentOf(parent);
            }
            return best;
        }

        private static bool FolderIsPure(string root, string folder, HashSet<string> stray)
        {
            try
            {
                var full = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
                foreach (var entry in Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories))
                {
                    if (entry.EndsWith(".meta", StringComparison.Ordinal)) continue;
                    if (!stray.Contains(Relative(root, entry))) return false;
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string ParentOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash > 0 ? path.Substring(0, slash) : "";
        }

        private static bool IsUnderAny(string path, List<string> folders)
        {
            foreach (var folder in folders)
                if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool Ignored(string name)
        {
            return name.StartsWith(".", StringComparison.Ordinal) || name.EndsWith("~", StringComparison.Ordinal);
        }

        private static string Relative(string root, string full)
        {
            var rootFull = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(full).Substring(rootFull.Length + 1).Replace('\\', '/');
        }

        private static string GuidOf(string metaPath)
        {
            try
            {
                using (var reader = new StreamReader(metaPath, Encoding.UTF8, true))
                {
                    var buffer = new char[1024];
                    var read = reader.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return null;
                    var match = GuidLine.Match(new string(buffer, 0, read));
                    return match.Success ? match.Groups[1].Value : null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string AsmdefNameOf(string path)
        {
            try
            {
                var match = AsmdefName.Match(File.ReadAllText(path, Encoding.UTF8));
                return match.Success ? match.Groups[1].Value : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string ReadMeta(string assetPath)
        {
            try { return File.Exists(assetPath + ".meta") ? File.ReadAllText(assetPath + ".meta", Encoding.UTF8) : ""; }
            catch (Exception) { return ""; }
        }

        /// <summary>
        /// True when a plugin's .meta lets the Editor load it: an explicit Editor setting wins, else the "Any platform" setting, an
        /// "Exclude Editor" turns it off, and a missing or older .meta counts as enabled (Unity's default for a managed DLL).
        /// </summary>
        internal static bool EditorEnabled(string metaText)
        {
            if (string.IsNullOrEmpty(metaText)) return true;
            var text = metaText.Replace("\r\n", "\n");
            if (ExcludeEditor.IsMatch(text)) return false;
            var editor = EditorPlatform.Match(text);
            if (editor.Success) return editor.Groups[1].Value == "1";
            var any = AnyPlatform.Match(text);
            if (any.Success) return any.Groups[1].Value == "1";
            return true;
        }

        /// <summary>True for a .NET assembly (a PE file with a CLR runtime header); native plugins share names across platform folders on purpose.</summary>
        internal static bool IsManagedAssembly(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (stream.Length < 0x40 || reader.ReadUInt16() != 0x5A4D) return false;   // "MZ"
                    stream.Position = 0x3C;
                    var pe = reader.ReadInt32();
                    if (pe <= 0 || pe > stream.Length - 24) return false;
                    stream.Position = pe;
                    if (reader.ReadUInt32() != 0x00004550) return false;                      // "PE\0\0"
                    stream.Position = pe + 4 + 16;
                    var optionalSize = reader.ReadUInt16();
                    var optional = pe + 24;
                    stream.Position = optional;
                    var magic = reader.ReadUInt16();
                    var directories = magic == 0x20b ? optional + 112 : optional + 96;
                    var clr = directories + 14 * 8;
                    if (clr + 8 > optional + optionalSize || clr + 8 > stream.Length) return false;
                    stream.Position = clr;
                    return reader.ReadUInt32() != 0;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
