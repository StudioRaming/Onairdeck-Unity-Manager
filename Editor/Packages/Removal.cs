using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// What the Manager placed in this project for each product, in ProjectSettings/OnAirDeck/placed-files.json, so a removal
    /// takes away exactly that and nothing a guess would add (owner rule 2026-11-03: remove from the Manager's own records only).
    /// Keyed by <see cref="LicenseKey"/> or <see cref="PurchaseKey"/>; each key holds project-relative file paths (forward slashes):
    /// copied files, files extracted from a zip, the files a .unitypackage lists. A reinstall adds to the list (an update can leave an
    /// old file in place, and that file is still this product's). Installs from before 2026-11-03 have no entry. No UnityEngine.
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

        /// <summary>Every key with its paths. A missing or unreadable file reads as no records (nothing can then be removed by record).</summary>
        public static Dictionary<string, List<string>> Load(string projectRoot)
        {
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            try
            {
                var path = PathFor(projectRoot);
                if (string.IsNullOrEmpty(projectRoot) || !File.Exists(path)) return result;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                object raw;
                if (root == null || !root.TryGetValue("products", out raw)) return result;
                var list = raw as List<object>;
                if (list == null) return result;
                foreach (var item in list)
                {
                    var obj = item as Dictionary<string, object>;
                    var key = obj != null ? (MiniJson.GetString(obj, "key") ?? "") : "";
                    object rawPaths;
                    if (key.Length == 0 || !obj.TryGetValue("paths", out rawPaths) || !(rawPaths is List<object>)) continue;
                    var paths = new List<string>();
                    foreach (var p in (List<object>)rawPaths)
                    {
                        var text = p as string;
                        if (RemovalPlanner.IsAssetFile(text)) paths.Add(text);
                    }
                    result[key] = paths;
                }
            }
            catch (Exception)
            {
                result.Clear();
            }
            return result;
        }

        /// <summary>Adds <paramref name="paths"/> to the record of <paramref name="key"/> (kept unique and sorted). Throws when it cannot be written.</summary>
        public static void Add(string projectRoot, string key, IEnumerable<string> paths)
        {
            if (string.IsNullOrEmpty(projectRoot) || string.IsNullOrEmpty(key) || paths == null) return;
            var all = Load(projectRoot);
            List<string> list;
            if (!all.TryGetValue(key, out list)) all[key] = list = new List<string>();
            var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
            var added = false;
            foreach (var path in paths)
            {
                var clean = (path ?? "").Replace('\\', '/');
                if (RemovalPlanner.IsAssetFile(clean) && seen.Add(clean)) { list.Add(clean); added = true; }
            }
            if (added) Save(projectRoot, all);
        }

        /// <summary>Keeps only the paths of <paramref name="key"/> that are still on disk; drops the key when none are.</summary>
        public static void Prune(string projectRoot, string key)
        {
            var all = Load(projectRoot);
            List<string> list;
            if (!all.TryGetValue(key, out list)) return;
            list.RemoveAll(p => !File.Exists(RemovalPlanner.Absolute(projectRoot, p)));
            if (list.Count == 0) all.Remove(key);
            Save(projectRoot, all);
        }

        private static void Save(string projectRoot, Dictionary<string, List<string>> all)
        {
            var keys = new List<string>(all.Keys);
            keys.Sort(StringComparer.Ordinal);
            var products = new List<object>();
            foreach (var key in keys)
            {
                var paths = new List<string>(all[key]);
                paths.Sort(StringComparer.Ordinal);
                products.Add(new Dictionary<string, object> { { "key", key }, { "paths", paths.ConvertAll(p => (object)p) } });
            }
            var root = new Dictionary<string, object> { { "format", 1L }, { "products", products } };
            AtomicFile.Write(PathFor(projectRoot), JsonWriter.Write(root, "\n") + "\n");
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
        NeededByOthers
    }

    /// <summary>What one removal moves: files to the trash, a package folder to the backup.</summary>
    internal sealed class RemovalPlan
    {
        public RemovalRefusal Refusal = RemovalRefusal.None;
        /// <summary>Project-relative files that exist and that no other product recorded: they go to the trash.</summary>
        public readonly List<string> Files = new List<string>();
        /// <summary>Files this product recorded that another product recorded too: they stay.</summary>
        public readonly List<string> Shared = new List<string>();
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
        /// <summary>Folders a removal never takes away, also when they end up empty.</summary>
        private static readonly string[] KeptFolders =
        {
            "Assets", Installer.InstallRoot, "Assets/Editor", "Assets/Plugins", "Assets/Resources", "Assets/StreamingAssets", "Assets/Gizmos",
            "Assets/Editor Default Resources", "Assets/StudioRaming"
        };

        /// <summary>A project-relative file path under Assets/ with no "..", no root and no empty part.</summary>
        public static bool IsAssetFile(string path)
        {
            if (string.IsNullOrEmpty(path) || path.IndexOf('\\') >= 0 || !path.StartsWith("Assets/", StringComparison.Ordinal) || path.EndsWith("/", StringComparison.Ordinal)) return false;
            foreach (var part in path.Split('/'))
                if (part.Length == 0 || part == "." || part == ".." || part.IndexOf(':') >= 0) return false;
            return !path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase);
        }

        public static string Absolute(string projectRoot, string relative)
        {
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        /// <summary>
        /// The files of <paramref name="keys"/> (a row and the licenses it stands for) that are here and that no other key recorded,
        /// plus <paramref name="ownFiles"/> (the product's own onairdeck.license, found by the id inside it). No record at all: NoRecord,
        /// with <paramref name="manualFolder"/> to open when it exists.
        /// </summary>
        public static RemovalPlan ForFiles(string projectRoot, ICollection<string> keys, IDictionary<string, List<string>> placed, IEnumerable<string> ownFiles, string manualFolder)
        {
            var plan = new RemovalPlan();
            var mine = new List<string>();
            var others = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in placed)
            {
                if (keys.Contains(pair.Key)) mine.AddRange(pair.Value);
                else foreach (var p in pair.Value) others.Add(p);
            }
            if (mine.Count == 0)
            {
                plan.Refusal = RemovalRefusal.NoRecord;
                if (!string.IsNullOrEmpty(manualFolder) && Directory.Exists(Absolute(projectRoot, manualFolder))) plan.ManualFolder = manualFolder;
                return plan;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in mine)
            {
                if (!IsAssetFile(path) || !seen.Add(path)) continue;
                if (others.Contains(path)) { if (File.Exists(Absolute(projectRoot, path))) plan.Shared.Add(path); continue; }
                if (File.Exists(Absolute(projectRoot, path))) plan.Files.Add(path);
            }
            if (ownFiles != null)
                foreach (var path in ownFiles)
                    if (IsAssetFile(path) && !others.Contains(path) && seen.Add(path) && File.Exists(Absolute(projectRoot, path))) plan.Files.Add(path);
            plan.Files.Sort(StringComparer.Ordinal);
            if (plan.Files.Count == 0) plan.Refusal = RemovalRefusal.NothingHere;
            return plan;
        }

        /// <summary>
        /// A package in Packages/&lt;name&gt; (the form the Manager installs): moved to the backup as a whole. Refused when VCC lists it, or
        /// while another installed package names it in vpmDependencies (the URP add-on pins MingToon Core), unless that package is in
        /// <paramref name="goingFirst"/> (removed earlier in the same run).
        /// </summary>
        public static RemovalPlan ForPackage(string projectRoot, string packageName, IEnumerable<string> ownFiles, ICollection<string> goingFirst = null)
        {
            var plan = new RemovalPlan();
            var installed = InstalledPackages.Read(projectRoot, packageName);
            if (installed == null || installed.Name != packageName) { plan.Refusal = RemovalRefusal.NothingHere; return plan; }
            if (ListedByVcc(projectRoot, packageName)) { plan.Refusal = RemovalRefusal.HeldByVcc; return plan; }
            foreach (var dependent in Dependents(projectRoot, packageName))
                if (goingFirst == null || !goingFirst.Contains(dependent)) plan.NeededBy.Add(dependent);
            if (plan.NeededBy.Count > 0) { plan.Refusal = RemovalRefusal.NeededByOthers; return plan; }
            plan.PackageFolder = "Packages/" + packageName;
            if (ownFiles != null)
                foreach (var path in ownFiles)
                    if (IsAssetFile(path) && File.Exists(Absolute(projectRoot, path)) && !plan.Files.Contains(path)) plan.Files.Add(path);
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
        /// The folders that are left empty once <paramref name="removed"/> are gone, deepest first: a folder whose every file is a removed
        /// file or its .meta (or the .meta of an emptied subfolder) and whose every subfolder empties too. Never Assets, the install root
        /// or a special Unity folder (<see cref="KeptFolders"/>).
        /// </summary>
        public static List<string> EmptiedFolders(string projectRoot, IEnumerable<string> removed)
        {
            var gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in removed)
            {
                if (!IsAssetFile(path)) continue;
                gone.Add(path);
                gone.Add(path + ".meta");
                var folder = ParentOf(path);
                while (folder.Length > 0 && !IsKept(folder)) { candidates.Add(folder); folder = ParentOf(folder); }
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
