using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>What one product placed: the files it created and the folders it created for them.</summary>
    internal sealed class PlacedEntry
    {
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Folders = new List<string>();
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
    /// (<see cref="LicenseKey"/>, <see cref="PurchaseKey(PurchaseItem)"/>): the files an install created (copied, extracted from a zip,
    /// listed by a .unitypackage) and the folders it created for them. A file or folder that was already there before the install is
    /// never recorded: it may be the user's or another product's, and an import that overwrites it does not make it this product's.
    /// The packages the Manager installed in Packages/&lt;name&gt; are listed by name. Installs from before 2026-11-03 have no entry.
    /// No UnityEngine.
    /// </summary>
    internal static class PlacedFiles
    {
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

        /// <summary>The record. A missing or unreadable file reads as empty (then nothing can be removed by record).</summary>
        public static PlacedRecords Load(string projectRoot)
        {
            var result = new PlacedRecords();
            try
            {
                var path = PathFor(projectRoot);
                if (string.IsNullOrEmpty(projectRoot) || !File.Exists(path)) return result;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                if (root == null) return result;
                object raw;
                if (root.TryGetValue("products", out raw) && raw is List<object>)
                {
                    foreach (var item in (List<object>)raw)
                    {
                        var obj = item as Dictionary<string, object>;
                        var key = obj != null ? (MiniJson.GetString(obj, "key") ?? "") : "";
                        if (key.Length == 0) continue;
                        var entry = new PlacedEntry();
                        foreach (var p in Strings(obj, "paths")) if (RemovalPlanner.IsAssetFile(p)) entry.Paths.Add(p);
                        foreach (var f in Strings(obj, "folders")) if (RemovalPlanner.IsAssetFolder(f)) entry.Folders.Add(f);
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

        /// <summary>
        /// Adds what one install created to <paramref name="key"/>: <paramref name="paths"/> are files that were not there before it,
        /// <paramref name="folders"/> folders it created. Kept unique and sorted. Throws when the file cannot be written.
        /// </summary>
        public static void Add(string projectRoot, string key, IEnumerable<string> paths, IEnumerable<string> folders)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(key)) return;
            var all = Load(projectRoot);
            PlacedEntry entry;
            if (!all.Products.TryGetValue(key, out entry)) all.Products[key] = entry = new PlacedEntry();
            var added = AddUnique(entry.Paths, paths, RemovalPlanner.IsAssetFile, false) | AddUnique(entry.Folders, folders, RemovalPlanner.IsAssetFolder, true);
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
        /// After a removal: <paramref name="key"/> keeps only the files still on disk that it did not give up (<paramref name="released"/>,
        /// the files another product also recorded, so their last owner can take them later) and the folders still on disk. The key goes
        /// when nothing is left.
        /// </summary>
        public static void Prune(string projectRoot, string key, ICollection<string> released)
        {
            var all = Load(projectRoot);
            PlacedEntry entry;
            if (!all.Products.TryGetValue(key, out entry)) return;
            entry.Paths.RemoveAll(p => !File.Exists(RemovalPlanner.Absolute(projectRoot, p)) || (released != null && released.Contains(p)));
            entry.Folders.RemoveAll(f => !Directory.Exists(RemovalPlanner.Absolute(projectRoot, f)));
            if (entry.Paths.Count == 0) all.Products.Remove(key);
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

        private static void Save(string projectRoot, PlacedRecords all)
        {
            var keys = new List<string>(all.Products.Keys);
            keys.Sort(StringComparer.Ordinal);
            var products = new List<object>();
            foreach (var key in keys)
            {
                var entry = all.Products[key];
                var paths = new List<string>(entry.Paths);
                paths.Sort(StringComparer.Ordinal);
                var folders = new List<string>(entry.Folders);
                folders.Sort(StringComparer.Ordinal);
                products.Add(new Dictionary<string, object>
                {
                    { "key", key }, { "paths", paths.ConvertAll(p => (object)p) }, { "folders", folders.ConvertAll(f => (object)f) }
                });
            }
            var packages = new List<string>(all.Packages);
            packages.Sort(StringComparer.Ordinal);
            var root = new Dictionary<string, object> { { "format", 2L }, { "products", products }, { "packages", packages.ConvertAll(p => (object)p) } };
            AtomicFile.Write(PathFor(projectRoot), JsonWriter.Write(root, "\n") + "\n");
        }
    }

    /// <summary>
    /// The files and folders an install is about to create, taken before it writes anything: a target that already exists is left out
    /// (it is the user's or another product's), and so is every folder that exists. No UnityEngine.
    /// </summary>
    internal sealed class PlacementTracker
    {
        private readonly string _projectRoot;
        private readonly HashSet<string> _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Paths = new List<string>();
        public readonly List<string> Folders = new List<string>();

        public PlacementTracker(string projectRoot) { _projectRoot = projectRoot; }

        /// <summary>Call before the file is written. Records it only when it does not exist yet, and every parent folder that does not.</summary>
        public void BeforeWrite(string assetPath)
        {
            var clean = (assetPath ?? "").Replace('\\', '/');
            if (!RemovalPlanner.IsAssetFile(clean) || string.IsNullOrEmpty(_projectRoot)) return;
            if (File.Exists(RemovalPlanner.Absolute(_projectRoot, clean))) return;
            if (_paths.Add(clean)) Paths.Add(clean);
            var folder = clean.Substring(0, clean.LastIndexOf('/'));
            while (RemovalPlanner.IsAssetFolder(folder) && !Directory.Exists(RemovalPlanner.Absolute(_projectRoot, folder)))
            {
                if (_folders.Add(folder)) Folders.Add(folder);
                folder = folder.Substring(0, folder.LastIndexOf('/'));
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
        /// <summary>Folders this product created that are empty once <see cref="Files"/> are gone, deepest first: they go too.</summary>
        public readonly List<string> Folders = new List<string>();
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
            var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in placed.Products)
            {
                if (keys.Contains(pair.Key)) { mine.AddRange(pair.Value.Paths); foreach (var f in pair.Value.Folders) myFolders.Add(f); }
                else foreach (var p in pair.Value.Paths) others.Add(p);
            }
            if (mine.Count == 0)
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
                // A license file is someone's license: only the one issued for this product (by the id inside it) goes.
                if (IsLicenseFile(path) && !own.Contains(path)) { plan.Shared.Add(path); continue; }
                plan.Files.Add(path);
            }
            foreach (var path in own)
                if (!others.Contains(path) && seen.Add(path) && File.Exists(Absolute(projectRoot, path))) plan.Files.Add(path);
            plan.Files.Sort(StringComparer.Ordinal);
            if (plan.Files.Count == 0) { plan.Refusal = RemovalRefusal.NothingHere; return plan; }
            // A license file's own folder counts as created by this product when the record has none (the license step makes it).
            foreach (var path in own) myFolders.Add(path.Substring(0, path.LastIndexOf('/')));
            plan.Folders.AddRange(EmptiedFolders(projectRoot, plan.Files, myFolders));
            return plan;
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
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (ownFiles != null)
                foreach (var path in ownFiles)
                    if (IsAssetFile(path) && File.Exists(Absolute(projectRoot, path)) && !plan.Files.Contains(path))
                    {
                        plan.Files.Add(path);
                        folders.Add(path.Substring(0, path.LastIndexOf('/')));
                    }
            plan.Folders.AddRange(EmptiedFolders(projectRoot, plan.Files, folders));
            return plan;
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
        /// install root or a special Unity folder.
        /// </summary>
        public static List<string> EmptiedFolders(string projectRoot, IEnumerable<string> removed, ICollection<string> created)
        {
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in removed)
            {
                if (!IsAssetFile(path)) continue;
                gone.Add(path);
                gone.Add(path + ".meta");
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
