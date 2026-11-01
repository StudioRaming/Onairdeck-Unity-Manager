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

    /// <summary>
    /// Owner rule 2026-11-01: MingToon add-ons (URP add-on, Ming Light Controller) work only with MingToon Core, so they install only when the
    /// core is installed or goes first in the same run, the list shows them under the core, and a run orders the core first. Sources, in
    /// order: a package named "&lt;core&gt;.&lt;suffix&gt;" (the URP add-on also pins its core in vpmDependencies, which the install follows), then
    /// <see cref="OnAirDeckConfig.CoreAddonLicenses"/> for add-ons that declare nothing (MLC). No UnityEngine: checked headless.
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
            return entry == null || !entry.IsLicense ? "" : CoreOf(entry.PackageName, entry.LicenseId);
        }

        public static bool IsCore(string packageName)
        {
            return !string.IsNullOrEmpty(packageName) && OnAirDeckConfig.CorePackages.ContainsKey(packageName);
        }

        /// <summary>The display name of a core package ("MingToon Core").</summary>
        public static string CoreName(string core)
        {
            string name;
            return core != null && OnAirDeckConfig.CorePackages.TryGetValue(core, out name) ? name : core ?? "";
        }

        /// <summary>The core is in Packages/, or its old Assets folder is there (an old install the add-on also works with).</summary>
        public static bool CoreIsHere(string projectRoot, string core)
        {
            if (string.IsNullOrEmpty(core)) return true;
            if (!string.IsNullOrEmpty(InstalledPackages.InstalledVersion(projectRoot, core))) return true;
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
            var cores = new List<T>();
            var middle = new List<T>();
            var addons = new List<T>();
            foreach (var item in items)
            {
                var package = packageOf(item) ?? "";
                if (IsCore(package)) cores.Add(item);
                else if (CoreOf(package, licenseOf(item)).Length > 0) addons.Add(item);
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
                if (row.Entry != null && IsCore(row.Entry.PackageName)) corePackages.Add(row.Entry.PackageName);
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
                var package = row.Entry != null ? row.Entry.PackageName : "";
                List<ListLine> list;
                // Two rows of the same core (a free and a paid license that did not merge): the add-ons go under the first.
                if (IsCore(package) && placed.Add(package) && children.TryGetValue(package, out list)) result.AddRange(list);
            }
            return result;
        }
    }
}
