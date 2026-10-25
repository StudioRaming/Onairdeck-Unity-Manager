using System.Collections.Generic;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Keeps Packages/vpm-manifest.json (the VRChat Creator Companion's project file) in step with
    /// installed packages. Only entries that already exist are updated; nothing is ever added, and
    /// every key the Manager does not know is written back unchanged.
    /// </summary>
    internal static class VpmManifestUpdater
    {
        /// <summary>The new file text, or null when no installed package has an entry to update.</summary>
        public static string Update(string existingText, IEnumerable<PackageManifest> installed)
        {
            var text = existingText ?? "";
            var root = MiniJson.ParseObject(text.TrimStart('﻿'));
            if (root == null) throw new PackageInstallException("Packages/vpm-manifest.json is not a valid JSON object. Fix or remove it, then install again.");

            var dependencies = Child(root, "dependencies");
            var locked = Child(root, "locked");
            var changed = false;

            foreach (var package in installed)
            {
                object value;
                if (dependencies != null && dependencies.TryGetValue(package.Name, out value))
                {
                    var entry = value as Dictionary<string, object>;
                    if (entry != null && SetVersion(entry, package.Version)) changed = true;
                }
                if (locked != null && locked.TryGetValue(package.Name, out value))
                {
                    var entry = value as Dictionary<string, object>;
                    if (entry == null) continue;
                    if (SetVersion(entry, package.Version)) changed = true;
                    if (SetDependencies(entry, package)) changed = true;
                }
            }
            if (!changed) return null;

            var newline = text.IndexOf("\r\n", System.StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var result = JsonWriter.Write(root, newline);
            var endsWithNewline = text.EndsWith("\n", System.StringComparison.Ordinal);
            return endsWithNewline ? result + newline : result;
        }

        private static bool SetVersion(Dictionary<string, object> entry, string version)
        {
            object current;
            if (entry.TryGetValue("version", out current) && (current as string) == version) return false;
            entry["version"] = version;
            return true;
        }

        private static bool SetDependencies(Dictionary<string, object> entry, PackageManifest package)
        {
            object current;
            var hasKey = entry.TryGetValue("dependencies", out current);
            if (!hasKey && package.VpmDependencies.Count == 0) return false;

            var existing = current as Dictionary<string, object>;
            if (existing != null && existing.Count == package.VpmDependencies.Count)
            {
                var same = true;
                foreach (var dependency in package.VpmDependencies)
                {
                    object old;
                    if (!existing.TryGetValue(dependency.Key, out old) || (old as string) != dependency.Value) { same = false; break; }
                }
                if (same) return false;
            }

            var desired = new Dictionary<string, object>(System.StringComparer.Ordinal);
            foreach (var dependency in package.VpmDependencies) desired[dependency.Key] = dependency.Value;
            entry["dependencies"] = desired;
            return true;
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
    }
}
