using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>A package only the Manager updates now that VCC still lists in Packages/vpm-manifest.json, while its folder is here.</summary>
    internal sealed class VpmHeldPackage
    {
        public string Package = "";
        /// <summary>The version in Packages/&lt;name&gt;/package.json.</summary>
        public string FolderVersion = "";
        /// <summary>The version VCC lists ("dependencies" first, else "locked"); "" when the entry has none.</summary>
        public string ListedVersion = "";
    }

    /// <summary>One move to the Manager and its backup folder (Library/OnAirDeck/Backup/vcc-move-&lt;stamp&gt;/).</summary>
    internal sealed class VpmMove
    {
        public string Folder = "";
        public List<string> Packages = new List<string>();
        public string State = "";
        public string Created = "";
    }

    /// <summary>
    /// Moves packages that VCC installed to the Manager (owner 2026-10-31: MingToon is no longer updated through VCC). The move takes the
    /// packages' entries out of both "dependencies" and "locked" of Packages/vpm-manifest.json; the package folders, the user's Assets
    /// folders and every other entry stay. Both entries must go: the VRChat resolver (vpm-core-lib 0.1.29, checked 2026-10-31) treats
    /// any entry left in either object whose version differs from the folder as missing, asks on the next project load and puts the
    /// listed version back over the folder. With no entry it leaves the folder alone, also when another VCC package still names the
    /// core in its vpmDependencies. The file is copied to the backup first; Undo puts the entries back (at the folder's version, so
    /// the resolver does not downgrade what the Manager installed meanwhile). No UnityEngine here.
    /// </summary>
    internal static class VpmMigration
    {
        public const string RecordName = "migration.json";
        public const string ManifestName = "vpm-manifest.json";
        public const string FolderPrefix = "vcc-move-";
        public const string StateMoved = "moved";
        public const string StateRestored = "restored";
        public const string StateKept = "kept";

        private static string ManifestPath(string projectRoot) { return Path.Combine(projectRoot, "Packages", ManifestName); }

        /// <summary>The packages of <see cref="OnAirDeckConfig.ManagerOnlyVpmPackages"/> that VCC still lists and whose folder is here. Never throws.</summary>
        public static List<VpmHeldPackage> Find(string projectRoot)
        {
            var found = new List<VpmHeldPackage>();
            try
            {
                var path = ManifestPath(projectRoot);
                if (!File.Exists(path)) return found;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8).TrimStart('﻿'));
                if (root == null) return found;
                var dependencies = Child(root, "dependencies");
                var locked = Child(root, "locked");
                foreach (var package in OnAirDeckConfig.ManagerOnlyVpmPackages)
                {
                    var listed = VersionOf(dependencies, package) ?? VersionOf(locked, package);
                    if (listed == null) continue;
                    var installed = InstalledPackages.Read(projectRoot, package);
                    if (installed == null || installed.Name != package) continue;
                    found.Add(new VpmHeldPackage { Package = package, FolderVersion = installed.Version ?? "", ListedVersion = listed });
                }
            }
            catch (Exception)
            {
            }
            return found;
        }

        /// <summary>
        /// Takes <paramref name="packages"/> out of "dependencies" and "locked", after copying vpm-manifest.json to a new backup folder with
        /// its migration.json. Throws (nothing changed) when the file cannot be read, none of the packages is listed, or the result would
        /// not read back as the same file without exactly those entries.
        /// </summary>
        public static VpmMove Move(string projectRoot, IList<string> packages, DateTime now)
        {
            var path = ManifestPath(projectRoot);
            if (!File.Exists(path)) throw new PackageInstallException("Packages/" + ManifestName + " is gone; nothing was changed.");
            var text = File.ReadAllText(path, Encoding.UTF8);
            var removed = new List<string>();
            var edited = Without(text, packages, removed);
            if (edited == null) throw new PackageInstallException("Packages/" + ManifestName + " no longer lists these packages; nothing was changed.");

            var backups = PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot));
            var stamp = FolderPrefix + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.Combine(backups, stamp);
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(backups, stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            File.Copy(path, Path.Combine(folder, ManifestName));
            var move = new VpmMove { Folder = folder, Packages = removed, State = StateMoved, Created = now.ToString("o", CultureInfo.InvariantCulture) };
            WriteRecord(move);
            AtomicFile.Write(path, edited);
            return move;
        }

        /// <summary>
        /// Puts the moved entries back from the backup copy, with "version" set to what the package folder holds now. Refuses (throws,
        /// nothing changed) when vpm-manifest.json lists one of them again or the backup cannot be read.
        /// </summary>
        public static void Undo(string projectRoot, VpmMove move)
        {
            var path = ManifestPath(projectRoot);
            var saved = MiniJson.ParseObject(File.ReadAllText(Path.Combine(move.Folder, ManifestName), Encoding.UTF8).TrimStart('﻿'));
            if (saved == null) throw new PackageInstallException("The backup of " + ManifestName + " cannot be read; nothing was changed.");
            var text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "{\n  \"dependencies\": {},\n  \"locked\": {}\n}\n";
            var root = MiniJson.ParseObject(text.TrimStart('﻿'));
            if (root == null) throw new PackageInstallException("Packages/" + ManifestName + " is not a valid JSON object; nothing was changed.");
            foreach (var section in new[] { "dependencies", "locked" })
            {
                var from = Child(saved, section);
                if (from == null) continue;
                var to = Child(root, section);
                if (to == null) { to = new Dictionary<string, object>(StringComparer.Ordinal); root[section] = to; }
                foreach (var package in move.Packages)
                {
                    object entry;
                    if (!from.TryGetValue(package, out entry)) continue;
                    if (to.ContainsKey(package)) throw new PackageInstallException("Packages/" + ManifestName + " lists " + package + " again; nothing was changed.");
                    var copy = entry as Dictionary<string, object>;
                    var installed = InstalledPackages.Read(projectRoot, package);
                    if (copy != null && installed != null && !string.IsNullOrEmpty(installed.Version))
                    {
                        copy = new Dictionary<string, object>(copy, StringComparer.Ordinal);
                        copy["version"] = installed.Version;
                    }
                    to[package] = (object)copy ?? entry;
                }
            }
            AtomicFile.Write(path, Serialize(root, text));
            move.State = StateRestored;
            WriteRecord(move);
        }

        /// <summary>"Keep it this way": the backup stays, the undo offer goes.</summary>
        public static void Keep(VpmMove move)
        {
            move.State = StateKept;
            WriteRecord(move);
        }

        /// <summary>Moves that can still be undone, oldest first. Never throws.</summary>
        public static List<VpmMove> Pending(string projectRoot)
        {
            var found = new List<VpmMove>();
            try
            {
                var backups = PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot));
                if (!Directory.Exists(backups)) return found;
                var folders = Directory.GetDirectories(backups, FolderPrefix + "*");
                Array.Sort(folders, StringComparer.Ordinal);
                foreach (var folder in folders)
                {
                    var move = ReadRecord(folder);
                    if (move != null && move.State == StateMoved) found.Add(move);
                }
            }
            catch (Exception)
            {
            }
            return found;
        }

        /// <summary>
        /// <paramref name="text"/> without the entries of <paramref name="packages"/> in "dependencies" and "locked" (every other key kept,
        /// line ends and a final newline kept), or null when none is listed. <paramref name="removed"/> gets the packages that were listed.
        /// Throws when the file is not a JSON object or the result does not read back as expected.
        /// </summary>
        internal static string Without(string text, ICollection<string> packages, List<string> removed)
        {
            var root = MiniJson.ParseObject((text ?? "").TrimStart('﻿'));
            if (root == null) throw new PackageInstallException("Packages/" + ManifestName + " is not a valid JSON object. Fix or remove it, then try again.");
            var dependencies = Child(root, "dependencies");
            var locked = Child(root, "locked");
            foreach (var package in packages)
            {
                var listed = false;
                if (dependencies != null && dependencies.Remove(package)) listed = true;
                if (locked != null && locked.Remove(package)) listed = true;
                if (listed && !removed.Contains(package)) removed.Add(package);
            }
            if (removed.Count == 0) return null;
            var result = Serialize(root, text);
            Verify(text, result, removed);
            return result;
        }

        /// <summary>The result must be the original minus exactly the removed entries: same top-level keys, same other entries.</summary>
        private static void Verify(string before, string after, List<string> removed)
        {
            var a = MiniJson.ParseObject(before.TrimStart('﻿'));
            var b = MiniJson.ParseObject(after);
            var ok = a != null && b != null && a.Count == b.Count;
            if (ok)
                foreach (var pair in a)
                {
                    object other;
                    if (!b.TryGetValue(pair.Key, out other)) { ok = false; break; }
                    if (pair.Key == "dependencies" || pair.Key == "locked")
                    {
                        var x = pair.Value as Dictionary<string, object>;
                        var y = other as Dictionary<string, object>;
                        if (x == null || y == null) { ok = Same(pair.Value, other); if (!ok) break; continue; }
                        foreach (var entry in x)
                        {
                            object kept;
                            var gone = removed.Contains(entry.Key);
                            if (gone ? y.ContainsKey(entry.Key) : !y.TryGetValue(entry.Key, out kept) || !Same(entry.Value, kept)) { ok = false; break; }
                        }
                        if (ok && y.Count > x.Count) ok = false;
                    }
                    else ok = Same(pair.Value, other);
                    if (!ok) break;
                }
            if (!ok) throw new PackageInstallException("Packages/" + ManifestName + " did not change as expected; nothing was changed.");
        }

        private static bool Same(object a, object b)
        {
            var x = a as Dictionary<string, object>;
            var y = b as Dictionary<string, object>;
            if (x != null || y != null) return x != null && y != null && JsonWriter.Write(x, "\n") == JsonWriter.Write(y, "\n");
            var p = a as List<object>;
            var q = b as List<object>;
            if (p != null || q != null)
            {
                if (p == null || q == null || p.Count != q.Count) return false;
                for (var i = 0; i < p.Count; i++) if (!Same(p[i], q[i])) return false;
                return true;
            }
            return Equals(a, b);
        }

        private static string Serialize(Dictionary<string, object> root, string original)
        {
            var newline = original.IndexOf("\r\n", StringComparison.Ordinal) >= 0 ? "\r\n" : "\n";
            var result = JsonWriter.Write(root, newline);
            return original.EndsWith("\n", StringComparison.Ordinal) ? result + newline : result;
        }

        private static string VersionOf(Dictionary<string, object> section, string package)
        {
            object value;
            if (section == null || !section.TryGetValue(package, out value)) return null;
            var entry = value as Dictionary<string, object>;
            return entry != null ? MiniJson.GetString(entry, "version") ?? "" : "";
        }

        private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }

        private static void WriteRecord(VpmMove move)
        {
            var root = new Dictionary<string, object>();
            root["format"] = 1L;
            root["packages"] = new List<object>(move.Packages.ConvertAll(p => (object)p));
            root["state"] = move.State;
            root["created"] = move.Created;
            root["updated"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            AtomicFile.Write(Path.Combine(move.Folder, RecordName), JsonWriter.Write(root, "\n"));
        }

        private static VpmMove ReadRecord(string folder)
        {
            try
            {
                var root = MiniJson.ParseObject(File.ReadAllText(Path.Combine(folder, RecordName), Encoding.UTF8));
                if (root == null) return null;
                var move = new VpmMove
                {
                    Folder = folder,
                    State = MiniJson.GetString(root, "state") ?? "",
                    Created = MiniJson.GetString(root, "created") ?? "",
                };
                object list;
                if (root.TryGetValue("packages", out list) && list is List<object>)
                    foreach (var item in (List<object>)list) if (item is string) move.Packages.Add((string)item);
                return move.Packages.Count > 0 ? move : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
