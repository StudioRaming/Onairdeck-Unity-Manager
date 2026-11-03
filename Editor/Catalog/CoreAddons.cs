using System;
using System.Collections.Generic;
using System.IO;

namespace OnAirDeck.UnityManager
{
    /// <summary>What an add-on's install button does while its core is missing.</summary>
    internal enum AddonState
    {
        /// <summary>Not an add-on, or its core is installed: a normal install.</summary>
        Ready,
        /// <summary>The core is missing and this account can install it: the core goes first, then the add-on.</summary>
        InstallCoreFirst,
        /// <summary>The core is missing and this account has no core to install: the button explains instead.</summary>
        CoreMissing
    }

    /// <summary>The add-on option of a product sold with its core (<see cref="OnAirDeckConfig.CoreAddonPurchases"/>).</summary>
    internal sealed class PurchaseAddon
    {
        /// <summary>The core package ("com.studioraming.vmodular").</summary>
        public string Core = "";
        /// <summary>The asset file ids of the add-on option (plugin-purchases files[].file_id).</summary>
        public string[] FileIds = new string[0];
    }

    /// <summary>
    /// A core sold as a .unitypackage instead of a package (<see cref="OnAirDeckConfig.FileCores"/>, VModular 1.0.0): it never has a
    /// Packages/ folder, so a file it imports tells that it is here, and the license rows that ship it install it before an add-on.
    /// </summary>
    internal sealed class FileCore
    {
        /// <summary>Licenses whose file versions are the core (an add-on's "install the core first" installs one of them).</summary>
        public string[] LicenseIds = new string[0];
        /// <summary>Project-relative files the core's import writes: any of them present means the core is here.</summary>
        public string[] Markers = new string[0];
        /// <summary>The product whose purchase rows can carry the core's file (a purchase of the base product).</summary>
        public string PurchaseProductId = "";
        /// <summary>The asset file ids of the core in that product (plugin-purchases files[].file_id).</summary>
        public string[] PurchaseFileIds = new string[0];
    }

    /// <summary>
    /// Owner rule 2026-11-01: MingToon add-ons (URP add-on, Ming Light Controller) work only with MingToon Core, so they install only when the
    /// core is installed or goes first in the same run, the list shows them under the core, and a run orders the core first. Sources, in
    /// order: a package named "&lt;core&gt;.&lt;suffix&gt;" (the URP add-on also pins its core in vpmDependencies, which the install follows), then
    /// <see cref="OnAirDeckConfig.CoreAddonLicenses"/> for add-ons that declare nothing (MLC). A purchase row is an add-on through
    /// <see cref="OnAirDeckConfig.CoreAddonPurchases"/> (VModular Plus, an option of the VModular product). No UnityEngine: checked headless.
    /// </summary>
    internal static class CoreAddons
    {
        /// <summary>The core package this product is an add-on of, or "".</summary>
        public static string CoreOf(string packageName, string licenseId)
        {
            if (!string.IsNullOrEmpty(packageName))
                foreach (var core in OnAirDeckConfig.CorePackages.Keys)
                    if (packageName.StartsWith(core + ".", StringComparison.Ordinal)) return core;
            string mapped;
            if (!string.IsNullOrEmpty(licenseId) && OnAirDeckConfig.CoreAddonLicenses.TryGetValue(licenseId, out mapped)) return mapped;
            return "";
        }

        public static string CoreOf(CatalogEntry entry)
        {
            if (entry == null) return "";
            return entry.IsLicense ? CoreOf(entry.PackageName, entry.LicenseId) : CoreOfPurchase(entry.Purchase);
        }

        public static string CoreOfPurchase(PurchaseItem item)
        {
            return CoreOfPurchase(item, OnAirDeckConfig.CoreAddonPurchases);
        }

        /// <summary>
        /// The core a purchase row is an add-on of, or "": its product is in <paramref name="addons"/> and every file the row lists is one
        /// of the add-on's files. A row with no files, or with any other file (the base product), is not an add-on.
        /// </summary>
        public static string CoreOfPurchase(PurchaseItem item, IDictionary<string, PurchaseAddon> addons)
        {
            PurchaseAddon addon;
            if (item == null || addons == null || string.IsNullOrEmpty(item.product_id) || !addons.TryGetValue(item.product_id, out addon)) return "";
            if (addon == null || string.IsNullOrEmpty(addon.Core) || addon.FileIds == null || item.files == null || item.files.Length == 0) return "";
            foreach (var file in item.files)
                if (file == null || string.IsNullOrEmpty(file.file_id) || Array.IndexOf(addon.FileIds, file.file_id) < 0) return "";
            return addon.Core;
        }

        public static bool IsCore(string packageName)
        {
            return !string.IsNullOrEmpty(packageName) && OnAirDeckConfig.CorePackages.ContainsKey(packageName);
        }

        /// <summary>The file core (<see cref="OnAirDeckConfig.FileCores"/>) a license ships, or "".</summary>
        public static string FileCoreOfLicense(string licenseId)
        {
            return FileCoreOfLicense(licenseId, OnAirDeckConfig.FileCores);
        }

        public static string FileCoreOfLicense(string licenseId, IDictionary<string, FileCore> fileCores)
        {
            if (string.IsNullOrEmpty(licenseId) || fileCores == null) return "";
            foreach (var pair in fileCores)
                if (pair.Value != null && pair.Value.LicenseIds != null && Array.IndexOf(pair.Value.LicenseIds, licenseId) >= 0) return pair.Key;
            return "";
        }

        /// <summary>The file core a purchase row carries (it lists one of the core's files in the core's product), or "".</summary>
        public static string FileCoreOfPurchase(PurchaseItem item, IDictionary<string, FileCore> fileCores)
        {
            if (item == null || item.files == null || string.IsNullOrEmpty(item.product_id) || fileCores == null) return "";
            foreach (var pair in fileCores)
            {
                var core = pair.Value;
                if (core == null || core.PurchaseFileIds == null || core.PurchaseProductId != item.product_id) continue;
                foreach (var file in item.files)
                    if (file != null && !string.IsNullOrEmpty(file.file_id) && Array.IndexOf(core.PurchaseFileIds, file.file_id) >= 0) return pair.Key;
            }
            return "";
        }

        /// <summary>
        /// The core this row installs, or "": a core package, a license of a file core, or a purchase row that carries a file core's file
        /// (and is no add-on itself).
        /// </summary>
        public static string CoreProvidedBy(CatalogEntry entry)
        {
            if (entry == null) return "";
            if (entry.IsLicense)
            {
                if (IsCore(entry.PackageName)) return entry.PackageName;
                return FileCoreOfLicense(entry.LicenseId);
            }
            return CoreOfPurchase(entry.Purchase).Length > 0 ? "" : FileCoreOfPurchase(entry.Purchase, OnAirDeckConfig.FileCores);
        }

        /// <summary>True when the core is a file core whose marker is one of <paramref name="paths"/> (project-relative).</summary>
        public static bool MarkerAmong(string core, ICollection<string> paths)
        {
            FileCore fileCore;
            if (string.IsNullOrEmpty(core) || paths == null || !OnAirDeckConfig.FileCores.TryGetValue(core, out fileCore) || fileCore == null) return false;
            foreach (var marker in fileCore.Markers ?? new string[0])
                foreach (var path in paths)
                    if (string.Equals((path ?? "").Replace('\\', '/'), marker, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The display name of a core package ("MingToon Core").</summary>
        public static string CoreName(string core)
        {
            string name;
            return core != null && OnAirDeckConfig.CorePackages.TryGetValue(core, out name) ? name : core ?? "";
        }

        /// <summary>
        /// The core is in Packages/, its old Assets folder is there (an old install the add-on also works with), or a file core's marker
        /// file is (VModular 1.0.0 imports into Assets/; a later package form in Packages/ counts through the first check).
        /// </summary>
        public static bool CoreIsHere(string projectRoot, string core)
        {
            if (string.IsNullOrEmpty(core)) return true;
            if (!string.IsNullOrEmpty(InstalledPackages.InstalledVersion(projectRoot, core))) return true;
            FileCore fileCore;
            if (OnAirDeckConfig.FileCores.TryGetValue(core, out fileCore) && fileCore != null)
                foreach (var marker in fileCore.Markers ?? new string[0])
                    if (!string.IsNullOrEmpty(marker) && File.Exists(Path.Combine(projectRoot, marker.Replace('/', Path.DirectorySeparatorChar)))) return true;
            string legacy;
            return OnAirDeckConfig.LegacyAssetFolders.TryGetValue(core, out legacy) && Directory.Exists(Path.Combine(projectRoot, legacy.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>The button state of an add-on. <paramref name="coreInstalled"/>: the core is in Packages/ (or its old Assets folder is there).</summary>
        public static AddonState StateOf(string core, bool coreInstalled, bool coreOffered)
        {
            if (string.IsNullOrEmpty(core) || coreInstalled) return AddonState.Ready;
            return coreOffered ? AddonState.InstallCoreFirst : AddonState.CoreMissing;
        }

        /// <summary>
        /// A stable order for a run: cores first, then everything else, add-ons of a core last, each group in its given order. An add-on
        /// that pins its core (URP) then finds the core at the pin; with a newer core offered, the add-on's pin wins at the end of the run
        /// instead of the core moving past the add-on's pin after it.
        /// </summary>
        public static List<T> OrderCoreFirst<T>(IList<T> items, Func<T, string> packageOf, Func<T, string> licenseOf)
        {
            return OrderCoreFirst(items, packageOf, licenseOf, null);
        }

        /// <param name="purchaseCoreOf">The core of an item that is a purchase add-on (<see cref="CoreOfPurchase(PurchaseItem)"/>), or null.</param>
        public static List<T> OrderCoreFirst<T>(IList<T> items, Func<T, string> packageOf, Func<T, string> licenseOf, Func<T, string> purchaseCoreOf)
        {
            var cores = new List<T>();
            var middle = new List<T>();
            var addons = new List<T>();
            foreach (var item in items)
            {
                var package = packageOf(item) ?? "";
                if (IsCore(package) || FileCoreOfLicense(licenseOf(item)).Length > 0) cores.Add(item);
                else if (CoreOf(package, licenseOf(item)).Length > 0 || (purchaseCoreOf != null && (purchaseCoreOf(item) ?? "").Length > 0)) addons.Add(item);
                else middle.Add(item);
            }
            var result = new List<T>(cores);
            result.AddRange(middle);
            result.AddRange(addons);
            return result;
        }

        /// <summary>
        /// Puts each add-on row right under its core row within the same group (the core keeps its place) and marks it as a child. An
        /// add-on whose core is not in that group keeps its place, unmarked. Headers are kept.
        /// </summary>
        public static List<ListLine> Nest(List<ListLine> lines)
        {
            var result = new List<ListLine>();
            var start = 0;
            while (start < lines.Count)
            {
                if (lines[start].Header) { result.Add(lines[start]); start++; continue; }
                var end = start;
                while (end < lines.Count && !lines[end].Header) end++;
                result.AddRange(NestGroup(lines.GetRange(start, end - start)));
                start = end;
            }
            return result;
        }

        private static List<ListLine> NestGroup(List<ListLine> rows)
        {
            var corePackages = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var provided = row.Entry != null && row.Entry.IsLicense ? CoreProvidedBy(row.Entry) : "";
                if (provided.Length > 0) corePackages.Add(provided);
            }
            var children = new Dictionary<string, List<ListLine>>(StringComparer.Ordinal);
            var kept = new List<ListLine>();
            foreach (var row in rows)
            {
                row.Child = false;
                var core = CoreOf(row.Entry);
                if (core.Length > 0 && corePackages.Contains(core))
                {
                    List<ListLine> list;
                    if (!children.TryGetValue(core, out list)) children[core] = list = new List<ListLine>();
                    row.Child = true;
                    list.Add(row);
                }
                else kept.Add(row);
            }
            var result = new List<ListLine>();
            var placed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in kept)
            {
                result.Add(row);
                var package = row.Entry != null && row.Entry.IsLicense ? CoreProvidedBy(row.Entry) : "";
                List<ListLine> list;
                // Two rows of the same core (a free and a paid license that did not merge): the add-ons go under the first.
                if (package.Length > 0 && placed.Add(package) && children.TryGetValue(package, out list)) result.AddRange(list);
            }
            return result;
        }
    }
}
