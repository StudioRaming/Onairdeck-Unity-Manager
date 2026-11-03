using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>What one product placed: the files it created (with the GUID a .unitypackage gave them), the files it touched that were already there, and the folders it created.</summary>
    internal sealed class PlacedEntry
    {
        /// <summary>Files this product created: only these can go with it.</summary>
        public readonly List<string> Paths = new List<string>();
        /// <summary>Files it wrote over that were already there (the user's, or another product's): never removed with it, and they keep another product's file in place.</summary>
        public readonly List<string> Touched = new List<string>();
        public readonly List<string> Folders = new List<string>();
        /// <summary>Path to the GUID a .unitypackage gave it: a file at that path whose .meta has another GUID is not this product's.</summary>
        public readonly Dictionary<string, string> Guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>Path to the SHA-256 of a copied or extracted file as it was written: a file at that path with other contents (the user's own, or one they changed) stays.</summary>
        public readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The whole record: per product key what it placed, and the packages the Manager installed in Packages/.</summary>
    internal sealed class PlacedRecords
    {
        public readonly Dictionary<string, PlacedEntry> Products = new Dictionary<string, PlacedEntry>(StringComparer.Ordinal);
        public readonly HashSet<string> Packages = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// What the Manager placed in this project, in ProjectSettings/OnAirDeck/placed-files.json, so a removal takes away exactly that and
    /// nothing a guess would add (owner rule 2026-11-03: remove from the Manager's own records only). Per product key
    /// (<see cref="LicenseKey"/>, <see cref="PurchaseKey(PurchaseItem)"/>): the files an install created, the files it wrote over that
    /// were already there, and the folders it created. A file that was already there never becomes removable by the install that wrote
    /// over it; when the product that created a file is removed while another product also wrote it, the file stays and passes to that
    /// product (<see cref="Release"/>), so the last of them removes it. The packages the Manager installed in Packages/&lt;name&gt; are
    /// listed by name. Installs from before 2026-11-03 have no entry. No UnityEngine.
    /// </summary>
    internal static class PlacedFiles
    {
        /// <summary>3: every created file carries its proof (GUID or SHA-256). Older drafts (1, 2) were never released and read as empty.</summary>
        private const long Format = 3;

        public static string PathFor(string projectRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "OnAirDeck"), "placed-files.json");
        }

        public static string LicenseKey(string licenseId) { return "license:" + (licenseId ?? ""); }
        public static string PurchaseKey(string itemId) { return "purchase:" + (itemId ?? ""); }

        /// <summary>A purchase by its line item; an own product (no line item) by its product id.</summary>
        public static string PurchaseKey(PurchaseItem item)
        {
            if (item == null) return "";
            return !string.IsNullOrEmpty(item.item_id) ? PurchaseKey(item.item_id) : "own:" + (item.product_id ?? "");
        }

        /// <summary>The record. A missing, unreadable or other-format file reads as empty (then nothing can be removed by record).</summary>
        public static PlacedRecords Load(string projectRoot)
        {
            var result = new PlacedRecords();
            try
            {
                var path = PathFor(projectRoot);
                if (string.IsNullOrEmpty(projectRoot) || !File.Exists(path)) return result;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                object raw;
                if (root == null || !root.TryGetValue("format", out raw) || !(raw is long) || (long)raw != Format) return result;
                if (root.TryGetValue("products", out raw) && raw is List<object>)
                {
                    foreach (var item in (List<object>)raw)
                    {
                        var obj = item as Dictionary<string, object>;
                        var key = obj != null ? (MiniJson.GetString(obj, "key") ?? "") : "";
                        if (key.Length == 0) continue;
                        var entry = new PlacedEntry();
                        foreach (var p in Strings(obj, "paths")) if (RemovalPlanner.IsAssetFile(p)) entry.Paths.Add(p);
                        foreach (var p in Strings(obj, "touched")) if (RemovalPlanner.IsAssetFile(p)) entry.Touched.Add(p);
                        foreach (var f in Strings(obj, "folders")) if (RemovalPlanner.IsAssetFolder(f)) entry.Folders.Add(f);
                        object guids;
                        if (obj.TryGetValue("guids", out guids) && guids is Dictionary<string, object>)
                            foreach (var pair in (Dictionary<string, object>)guids)
                                if (pair.Value is string && RemovalPlanner.IsGuid((string)pair.Value)) entry.Guids[pair.Key] = (string)pair.Value;
                        object hashes;
                        if (obj.TryGetValue("hashes", out hashes) && hashes is Dictionary<string, object>)
                            foreach (var pair in (Dictionary<string, object>)hashes)
                                if (pair.Value is string && RemovalPlanner.IsSha256((string)pair.Value)) entry.Hashes[pair.Key] = (string)pair.Value;
                        result.Products[key] = entry;
                    }
                }
                if (root.TryGetValue("packages", out raw) && raw is List<object>)
                    foreach (var name in (List<object>)raw)
                        if (name is string && ((string)name).Length > 0) result.Packages.Add((string)name);
            }
            catch (Exception)
            {
                return new PlacedRecords();
            }
            return result;
        }

        private static IEnumerable<string> Strings(Dictionary<string, object> obj, string field)
        {
            object raw;
            if (!obj.TryGetValue(field, out raw) || !(raw is List<object>)) yield break;
            foreach (var item in (List<object>)raw) if (item is string) yield return (string)item;
        }

        /// <summary>Adds what one install did to <paramref name="key"/> (<see cref="PlacementTracker"/>). Throws when the file cannot be written.</summary>
        public static void Add(string projectRoot, string key, PlacementTracker tracker)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(key) || tracker == null) return;
            var all = Load(projectRoot);
            PlacedEntry entry;
            if (!all.Products.TryGetValue(key, out entry)) all.Products[key] = entry = new PlacedEntry();
            var added = AddUnique(entry.Paths, tracker.Paths, RemovalPlanner.IsAssetFile, false);
            // A file this product created before stays created by it when a reinstall writes it again.
            var touched = new List<string>();
            foreach (var path in tracker.Touched) if (!entry.Paths.Exists(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase))) touched.Add(path);
            added |= AddUnique(entry.Touched, touched, RemovalPlanner.IsAssetFile, false);
            added |= AddUnique(entry.Folders, tracker.Folders, RemovalPlanner.IsAssetFolder, true);
            // Proofs only for files this product owns (created now or by an earlier install of it): a file it only wrote over gets none.
            var owned = new HashSet<string>(entry.Paths, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in tracker.Guids)
            {
                string old;
                if (owned.Contains(pair.Key) && (!entry.Guids.TryGetValue(pair.Key, out old) || old != pair.Value)) { entry.Guids[pair.Key] = pair.Value; added = true; }
            }
            foreach (var pair in tracker.Hashes)
            {
                string old;
                if (owned.Contains(pair.Key) && (!entry.Hashes.TryGetValue(pair.Key, out old) || old != pair.Value)) { entry.Hashes[pair.Key] = pair.Value; added = true; }
            }
            if (added) Save(projectRoot, all);
        }

        /// <summary>Records that the Manager installed Packages/&lt;name&gt; (after a successful package transaction).</summary>
        public static void AddPackage(string projectRoot, string packageName)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(packageName)) return;
            var all = Load(projectRoot);
            if (all.Packages.Add(packageName)) Save(projectRoot, all);
        }

        public static void RemovePackage(string projectRoot, string packageName)
        {
            var all = Load(projectRoot);
            if (all.Packages.Remove(packageName ?? "")) Save(projectRoot, all);
        }

        /// <summary>
        /// After a removal of <paramref name="key"/> that went through: each file it created that is still here (another product also
        /// wrote it) passes to a product that touched it, with its GUID; then the key goes. Nothing passes to a product that did not write
        /// the file, so a file only the user also has never becomes removable.
        /// </summary>
        public static void Release(string projectRoot, string key)
        {
            var all = Load(projectRoot);
            PlacedEntry entry;
            if (!all.Products.TryGetValue(key, out entry)) return;
            all.Products.Remove(key);
            foreach (var path in entry.Paths)
            {
                if (!File.Exists(RemovalPlanner.Absolute(projectRoot, path))) continue;
                foreach (var other in all.Products.Values)
                {
                    var at = other.Touched.FindIndex(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
                    if (at < 0) continue;
                    other.Touched.RemoveAt(at);
                    other.Paths.Add(path);
                    string guid, hash;
                    if (entry.Guids.TryGetValue(path, out guid)) other.Guids[path] = guid;
                    if (entry.Hashes.TryGetValue(path, out hash)) other.Hashes[path] = hash;
                    break;
                }
            }
            Save(projectRoot, all);
        }

        private static bool AddUnique(List<string> list, IEnumerable<string> items, Func<string, bool> valid, bool folders)
        {
            if (items == null) return false;
            var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            var added = false;
            foreach (var item in items)
            {
                var clean = (item ?? "").Replace('\\', '/');
                if (folders) clean = clean.TrimEnd('/');
                if (valid(clean) && seen.Add(clean)) { list.Add(clean); added = true; }
            }
            return added;
        }

        private static List<object> Sorted(IEnumerable<string> items)
        {
            var list = new List<string>(items);
            list.Sort(StringComparer.Ordinal);
            return list.ConvertAll(p => (object)p);
        }

        private static void Save(string projectRoot, PlacedRecords all)
        {
            var keys = new List<string>(all.Products.Keys);
            keys.Sort(StringComparer.Ordinal);
            var products = new List<object>();
            foreach (var key in keys)
            {
                var entry = all.Products[key];
                var guidKeys = new List<string>(entry.Guids.Keys);
                guidKeys.Sort(StringComparer.Ordinal);
                var guids = new Dictionary<string, object>();
                foreach (var path in guidKeys) guids[path] = entry.Guids[path];
                var hashKeys = new List<string>(entry.Hashes.Keys);
                hashKeys.Sort(StringComparer.Ordinal);
                var hashes = new Dictionary<string, object>();
                foreach (var path in hashKeys) hashes[path] = entry.Hashes[path];
                products.Add(new Dictionary<string, object>
                {
                    { "key", key }, { "paths", Sorted(entry.Paths) }, { "touched", Sorted(entry.Touched) }, { "folders", Sorted(entry.Folders) }, { "guids", guids }, { "hashes", hashes }
                });
            }
            var root = new Dictionary<string, object> { { "format", Format }, { "products", products }, { "packages", Sorted(all.Packages) } };
            AtomicFile.Write(PathFor(projectRoot), JsonWriter.Write(root, "\n") + "\n");
        }
    }

    /// <summary>
    /// What an install does, taken before it writes anything: a file that does not exist yet is created by it (with the GUID a
    /// .unitypackage gives it), one that exists is only touched, and every folder that does not exist yet is created by it. No UnityEngine.
    /// </summary>
    internal sealed class PlacementTracker
    {
        private readonly string _projectRoot;
        private readonly HashSet<string> _seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Touched = new List<string>();
        public readonly List<string> Folders = new List<string>();
        public readonly Dictionary<string, string> Guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public PlacementTracker(string projectRoot) { _projectRoot = projectRoot; }

        /// <summary>Call right after a copied or extracted file was written: a file this install created keeps the SHA-256 it was written with.</summary>
        public void AfterWrite(string assetPath)
        {
            var clean = (assetPath ?? "").Replace('\\', '/');
            // A touched file too: when it is this product's own (a reinstall or an update), PlacedFiles.Add takes its new proof.
            if (!_seen.Contains(clean)) return;
            var hash = RemovalPlanner.FileSha256(_projectRoot, clean);
            if (hash.Length > 0) Hashes[clean] = hash;
        }

        /// <summary>Call before the file is written (a .unitypackage: before its import, with the asset's GUID).</summary>
        public void BeforeWrite(string assetPath, string guid = null)
        {
            var clean = (assetPath ?? "").Replace('\\', '/');
            if (!RemovalPlanner.IsAssetFile(clean) || string.IsNullOrEmpty(_projectRoot) || !_seen.Add(clean)) return;
            if (RemovalPlanner.IsGuid(guid)) Guids[clean] = guid;
            if (File.Exists(RemovalPlanner.Absolute(_projectRoot, clean))) { Touched.Add(clean); return; }
            Paths.Add(clean);
            BeforeCreateFolder(clean.Substring(0, clean.LastIndexOf('/')));
        }

        /// <summary>Call before a folder is created: it and every parent that does not exist yet are created by this install.</summary>
        public void BeforeCreateFolder(string folder)
        {
            var clean = (folder ?? "").Replace('\\', '/').TrimEnd('/');
            if (string.IsNullOrEmpty(_projectRoot)) return;
            while (RemovalPlanner.IsAssetFolder(clean) && !Directory.Exists(RemovalPlanner.Absolute(_projectRoot, clean)))
            {
                if (_folders.Add(clean)) Folders.Add(clean);
                clean = clean.Substring(0, clean.LastIndexOf('/'));
            }
        }
    }

    /// <summary>Why a product cannot be removed by the Manager right now.</summary>
    internal enum RemovalRefusal
    {
        None,
        /// <summary>Installed before the Manager kept records, or by hand: nothing is guessed, the person removes it (the folder is shown).</summary>
        NoRecord,
        /// <summary>Nothing it recorded is left in the project.</summary>
        NothingHere,
        /// <summary>A package VCC lists in Packages/vpm-manifest.json: VCC would put it back, so VCC removes it.</summary>
        HeldByVcc,
        /// <summary>Other installed packages or add-ons need it (MingToon Core under its URP add-on): those go first.</summary>
        NeededByOthers,
        /// <summary>A core whose add-ons the Manager cannot list yet (OnAirDeckConfig.CoreAddonsPending): it is removed by hand for now.</summary>
        AddonsUnknown
    }

    /// <summary>What one removal moves: files to the trash, a package folder to the backup.</summary>
    internal sealed class RemovalPlan
    {
        public RemovalRefusal Refusal = RemovalRefusal.None;
        /// <summary>Project-relative files that exist and that no other product recorded: they go to the trash.</summary>
        public readonly List<string> Files = new List<string>();
        /// <summary>Files this product recorded that another product recorded too: they stay, and this product gives them up.</summary>
        public readonly List<string> Shared = new List<string>();
        /// <summary>Files this product created that are no longer what it wrote (changed, or replaced by the user's own): they stay.</summary>
        public readonly List<string> Changed = new List<string>();
        /// <summary>Folders this product created that are empty once <see cref="Files"/> are gone, deepest first: they go too.</summary>
        public readonly List<string> Folders = new List<string>();
        /// <summary>Every folder this product's installs created (the run checks again, after the files went, which of them are empty).</summary>
        public readonly List<string> CreatedFolders = new List<string>();
        /// <summary>"Packages/&lt;name&gt;" to move to Library/OnAirDeck/Backup, or "".</summary>
        public string PackageFolder = "";
        /// <summary>Installed packages that list this package in their vpmDependencies (with <see cref="RemovalRefusal.NeededByOthers"/>).</summary>
        public readonly List<string> NeededBy = new List<string>();
        /// <summary>With <see cref="RemovalRefusal.NoRecord"/>: the folder the person can open, or "".</summary>
        public string ManualFolder = "";

        public bool CanRun { get { return Refusal == RemovalRefusal.None && (Files.Count > 0 || PackageFolder.Length > 0); } }
    }

    /// <summary>
    /// Plans a removal from the records alone (no UnityEngine, headless-checked). The window moves the files with
    /// AssetDatabase.MoveAssetToTrash (the system trash can undo it) and a package folder to Library/OnAirDeck/Backup.
    /// </summary>
    internal static class RemovalPlanner
    {
        /// <summary>Folders a removal never takes away, also when a product created them and they end up empty.</summary>
        private static readonly string[] KeptFolders =
        {
            "Assets", Installer.InstallRoot, "Assets/Editor", "Assets/Plugins", "Assets/Resources", "Assets/StreamingAssets", "Assets/Gizmos",
            "Assets/Editor Default Resources", "Assets/StudioRaming"
        };

        /// <summary>A project-relative path under Assets/ with no "..", no root, no drive and no empty part.</summary>
        private static bool IsUnderAssets(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)) return false;
            foreach (var part in path.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0) return false;
            return true;
        }

        /// <summary>A file path under Assets/ (not a .meta) that a record may hold.</summary>
        public static bool IsAssetFile(string path)
        {
            return IsUnderAssets(path) && !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A folder under Assets/ that a record may hold (never Assets itself or a kept folder).</summary>
        public static bool IsAssetFolder(string path)
        {
            return IsUnderAssets(path) && !IsKept(path);
        }

        public static string Absolute(string projectRoot, string relative)
        {
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The files of <paramref name="keys"/> (a row and the licenses it stands for) that are here and that no other key recorded,
        /// plus <paramref name="ownFiles"/> (the product's own onairdeck.license, found by the id inside it), and the recorded folders
        /// they leave empty. A recorded onairdeck.license goes only when it is one of <paramref name="ownFiles"/>. No record at all:
        /// NoRecord, with <paramref name="manualFolder"/> to open when it exists.
        /// </summary>
        public static RemovalPlan ForFiles(string projectRoot, ICollection<string> keys, PlacedRecords placed, IEnumerable<string> ownFiles, string manualFolder)
        {
            var plan = new RemovalPlan();
            var mine = new List<string>();
            var myFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recorded = false;
            foreach (var pair in placed.Products)
            {
                if (keys.Contains(pair.Key))
                {
                    recorded = true;
                    mine.AddRange(pair.Value.Paths);
                    foreach (var f in pair.Value.Folders) myFolders.Add(f);
                    foreach (var g in pair.Value.Guids) guids[g.Key] = g.Value;
                    foreach (var h in pair.Value.Hashes) hashes[h.Key] = h.Value;
                }
                else
                {
                    // Another product created it, or wrote over it: it stays.
                    foreach (var p in pair.Value.Paths) others.Add(p);
                    foreach (var p in pair.Value.Touched) others.Add(p);
                }
            }
            if (!recorded)
            {
                plan.Refusal = RemovalRefusal.NoRecord;
                if (!string.IsNullOrEmpty(manualFolder) && Directory.Exists(Absolute(projectRoot, manualFolder))) plan.ManualFolder = manualFolder;
                return plan;
            }
            var own = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ownFiles != null) foreach (var path in ownFiles) if (IsAssetFile(path)) own.Add(path);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in mine)
            {
                if (!IsAssetFile(path) || !seen.Add(path)) continue;
                if (!File.Exists(Absolute(projectRoot, path))) continue;
                if (others.Contains(path)) { plan.Shared.Add(path); continue; }
                // A .unitypackage file is this product's only while its .meta keeps the GUID the package gave it (an import that never
                // ran, then a file of the user at the same path, has another one).
                // The file must still be the one this product wrote: a .unitypackage file by the GUID in its .meta (an import that never
                // ran, then a file of the user at the same path, has another one), a copied or extracted file by its contents (a file the
                // user put there after deleting the product's, or changed, stays). A recorded file without a proof stays too.
                string guid, hash;
                if (guids.TryGetValue(path, out guid)) { if (!string.Equals(MetaGuid(projectRoot, path), guid, StringComparison.OrdinalIgnoreCase)) { plan.Changed.Add(path); continue; } }
                else if (hashes.TryGetValue(path, out hash)) { if (!string.Equals(FileSha256(projectRoot, path), hash, StringComparison.OrdinalIgnoreCase)) { plan.Changed.Add(path); continue; } }
                else { plan.Changed.Add(path); continue; }
                // A license file is someone's license: only the one issued for this product (by the id inside it) goes.
                if (IsLicenseFile(path) && !own.Contains(path)) { plan.Shared.Add(path); continue; }
                plan.Files.Add(path);
            }
            foreach (var path in own)
                if (!others.Contains(path) && seen.Add(path) && File.Exists(Absolute(projectRoot, path))) plan.Files.Add(path);
            plan.Files.Sort(StringComparer.Ordinal);
            if (plan.Files.Count == 0) { plan.Refusal = RemovalRefusal.NothingHere; return plan; }
            plan.CreatedFolders.AddRange(myFolders);
            plan.Folders.AddRange(EmptiedFolders(projectRoot, plan.Files, myFolders));
            return plan;
        }

        private static readonly Regex GuidLine = new Regex(@"^guid:\s*([0-9a-fA-F]{32})\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);

        public static bool IsSha256(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 64) return false;
            foreach (var c in text) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>The SHA-256 (lowercase hex) of a project file, or "" when it cannot be read.</summary>
        public static string FileSha256(string projectRoot, string path)
        {
            try
            {
                using (var sha = SHA256.Create())
                using (var stream = File.OpenRead(Absolute(projectRoot, path)))
                {
                    var sb = new StringBuilder(64);
                    foreach (var b in sha.ComputeHash(stream)) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception)
            {
                return "";
            }
        }

        public static bool IsGuid(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length != 32) return false;
            foreach (var c in text) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>The GUID in &lt;path&gt;.meta, or "" when there is none.</summary>
        public static string MetaGuid(string projectRoot, string path)
        {
            try
            {
                var meta = Absolute(projectRoot, path) + ".meta";
                if (!File.Exists(meta)) return "";
                var match = GuidLine.Match(File.ReadAllText(meta));
                return match.Success ? match.Groups[1].Value : "";
            }
            catch (Exception)
            {
                return "";
            }
        }

        private static bool IsLicenseFile(string path)
        {
            return path.EndsWith("/" + LicenseFile.FileName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A package the Manager installed in Packages/&lt;name&gt; (<see cref="PlacedRecords.Packages"/>): moved to the backup as a whole.
        /// Not in the record: NoRecord (a hand-copied or older install is removed by hand). Refused when VCC lists it, or while another
        /// installed package names it in vpmDependencies (the URP add-on pins MingToon Core), unless that package is in
        /// <paramref name="goingFirst"/> (removed earlier in the same run).
        /// </summary>
        public static RemovalPlan ForPackage(string projectRoot, string packageName, PlacedRecords placed, IEnumerable<string> ownFiles, ICollection<string> goingFirst = null)
        {
            var plan = new RemovalPlan();
            var installed = InstalledPackages.Read(projectRoot, packageName);
            if (installed == null || installed.Name != packageName) { plan.Refusal = RemovalRefusal.NothingHere; return plan; }
            if (placed == null || !placed.Packages.Contains(packageName))
            {
                plan.Refusal = RemovalRefusal.NoRecord;
                plan.ManualFolder = "Packages/" + packageName;
                return plan;
            }
            if (ListedByVcc(projectRoot, packageName)) { plan.Refusal = RemovalRefusal.HeldByVcc; return plan; }
            foreach (var dependent in Dependents(projectRoot, packageName))
                if (goingFirst == null || !goingFirst.Contains(dependent)) plan.NeededBy.Add(dependent);
            if (plan.NeededBy.Count > 0) { plan.Refusal = RemovalRefusal.NeededByOthers; return plan; }
            if (Array.IndexOf(OnAirDeckConfig.CoreAddonsPending, packageName) >= 0) { plan.Refusal = RemovalRefusal.AddonsUnknown; return plan; }
            plan.PackageFolder = "Packages/" + packageName;
            if (ownFiles != null)
                foreach (var path in ownFiles)
                    if (IsAssetFile(path) && File.Exists(Absolute(projectRoot, path)) && !plan.Files.Contains(path)) plan.Files.Add(path);
            return plan;
        }

        /// <summary>
        /// Another option (line item) of the same purchased product still has a file of its record in this project. Every option of a
        /// product shares its product folder and that folder's onairdeck.license, which is checked by product id, so the license stays
        /// until the last option goes. <paramref name="otherOptionKeys"/> are the other options' PlacedFiles keys. Never throws.
        /// </summary>
        public static bool OtherOptionHere(string projectRoot, PlacedRecords placed, IEnumerable<string> otherOptionKeys)
        {
            if (placed == null || otherOptionKeys == null) return false;
            try
            {
                foreach (var key in otherOptionKeys)
                {
                    PlacedEntry recorded;
                    if (string.IsNullOrEmpty(key) || !placed.Products.TryGetValue(key, out recorded)) continue;
                    foreach (var path in recorded.Paths) if (File.Exists(Absolute(projectRoot, path))) return true;
                    foreach (var path in recorded.Touched) if (File.Exists(Absolute(projectRoot, path))) return true;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        /// <summary>
        /// The onairdeck.license files under the install root issued for this product: for one of <paramref name="licenseIds"/>, or for the
        /// purchase line item <paramref name="itemId"/> (the id inside the file decides, never the folder name). Never throws.
        /// </summary>
        public static List<string> OwnLicenseFiles(string projectRoot, ICollection<string> licenseIds, string itemId)
        {
            var result = new List<string>();
            try
            {
                var root = Absolute(projectRoot, Installer.InstallRoot);
                if (!Directory.Exists(root)) return result;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var file = Path.Combine(dir, LicenseFile.FileName);
                    if (!File.Exists(file)) continue;
                    var text = File.ReadAllText(file);
                    string id;
                    var mine = (licenseIds != null && LicenseFile.TryReadPayloadString(text, "license_id", out id) && id.Length > 0 && licenseIds.Contains(id))
                        || (!string.IsNullOrEmpty(itemId) && LicenseFile.TryReadPayloadString(text, "item_id", out id) && id == itemId);
                    if (mine) result.Add(Installer.InstallRoot + "/" + Path.GetFileName(dir) + "/" + LicenseFile.FileName);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        /// <summary>Installed packages (folders under Packages/) whose vpmDependencies name <paramref name="packageName"/>, sorted.</summary>
        public static List<string> Dependents(string projectRoot, string packageName)
        {
            var result = new List<string>();
            try
            {
                var packages = Path.Combine(projectRoot, "Packages");
                if (!Directory.Exists(packages)) return result;
                foreach (var dir in Directory.GetDirectories(packages))
                {
                    var name = Path.GetFileName(dir);
                    if (name == packageName) continue;
                    var manifest = InstalledPackages.Read(projectRoot, name);
                    if (manifest != null && manifest.VpmDependencyNames.Contains(packageName)) result.Add(manifest.Name);
                }
            }
            catch (Exception)
            {
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>True when Packages/vpm-manifest.json lists the package under "dependencies" or "locked". An unreadable file counts as listed.</summary>
        public static bool ListedByVcc(string projectRoot, string packageName)
        {
            var path = Path.Combine(Path.Combine(projectRoot, "Packages"), "vpm-manifest.json");
            if (!File.Exists(path)) return false;
            try
            {
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                if (root == null) return true;
                foreach (var section in new[] { "dependencies", "locked" })
                {
                    object raw;
                    var child = root.TryGetValue(section, out raw) ? raw as Dictionary<string, object> : null;
                    if (child != null && child.ContainsKey(packageName)) return true;
                }
                return false;
            }
            catch (Exception)
            {
                return true;
            }
        }

        /// <summary>
        /// The folders of <paramref name="created"/> (the ones this product's installs made) that are left empty once
        /// <paramref name="removed"/> are gone, deepest first: every file in one is a removed file or its .meta (or the .meta of an
        /// emptied subfolder) and every subfolder empties too. A folder that was there before the install never goes, nor Assets, the
        /// install root or a special Unity folder. <paramref name="afterRemoval"/>: the files already went, so a file found at one of
        /// their paths is a new one (saved meanwhile) and keeps its folder.
        /// </summary>
        public static List<string> EmptiedFolders(string projectRoot, IEnumerable<string> removed, ICollection<string> created, bool afterRemoval = false)
        {
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in removed)
            {
                if (!IsAssetFile(path)) continue;
                if (!afterRemoval || !File.Exists(Absolute(projectRoot, path))) gone.Add(path);
                if (!afterRemoval || !File.Exists(Absolute(projectRoot, path + ".meta"))) gone.Add(path + ".meta");
                var folder = ParentOf(path);
                while (folder.Length > 0 && IsAssetFolder(folder) && created != null && created.Contains(folder)) { candidates.Add(folder); folder = ParentOf(folder); }
            }
            var ordered = new List<string>(candidates);
            // Deepest first, so a parent sees its emptied children.
            ordered.Sort((a, b) => { var c = b.Split('/').Length.CompareTo(a.Split('/').Length); return c != 0 ? c : string.CompareOrdinal(a, b); });
            var emptied = new List<string>();
            var emptiedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in ordered)
            {
                var abs = Absolute(projectRoot, folder);
                if (!Directory.Exists(abs)) continue;
                var empty = true;
                foreach (var file in Directory.GetFiles(abs))
                {
                    var relative = folder + "/" + Path.GetFileName(file);
                    var subMeta = relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) && emptiedSet.Contains(relative.Substring(0, relative.Length - 5));
                    if (!gone.Contains(relative) && !subMeta) { empty = false; break; }
                }
                if (empty)
                    foreach (var dir in Directory.GetDirectories(abs))
                        if (!emptiedSet.Contains(folder + "/" + Path.GetFileName(dir))) { empty = false; break; }
                if (!empty) continue;
                emptied.Add(folder);
                emptiedSet.Add(folder);
            }
            return emptied;
        }

        private static bool IsKept(string folder)
        {
            foreach (var kept in KeptFolders) if (string.Equals(kept, folder, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string ParentOf(string path)
        {
            var slash = path.LastIndexOf('/');
            return slash <= 0 ? "" : path.Substring(0, slash);
        }

        /// <summary>
        /// The order a removal runs in: the reverse of the install order (<see cref="CoreAddons.OrderCoreFirst{T}(IList{T}, Func{T, string}, Func{T, string}, Func{T, string})"/>),
        /// so add-ons go before everything else and cores last. MingToon Core is never removed before its add-ons.
        /// </summary>
        public static List<T> RemovalOrder<T>(IList<T> items, Func<T, string> packageOf, Func<T, string> licenseOf, Func<T, string> purchaseCoreOf)
        {
            var order = CoreAddons.OrderCoreFirst(items, packageOf, licenseOf, purchaseCoreOf);
            order.Reverse();
            return order;
        }
    }
}
