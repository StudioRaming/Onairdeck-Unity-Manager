using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>What a fix was about to touch changed after the check: nothing was changed, the check runs again.</summary>
    internal sealed class ConflictChangedException : PackageInstallException
    {
        public ConflictChangedException(string path) : base(path + " changed after the project check, so nothing was changed. The check runs again; look at the result first.") { }
    }

    /// <summary>A manifest.json entry the project check took out, with its backup (Library/OnAirDeck/Backup/manifest-&lt;stamp&gt;/).</summary>
    internal sealed class ManifestRemoval
    {
        public string Folder = "";
        public string Package = "";
        /// <summary>The entry's value as it was written (JSON-escaped), put back verbatim by an undo.</summary>
        public string Value = "";
        public string State = "";
        public string Created = "";
    }

    /// <summary>
    /// The fixes of the project check. Every one can be undone: files go to a backup through <see cref="LegacyMove"/> (the same
    /// backup-manifest.json and restore as an install's old-copy move), and a manifest.json entry is kept in a backup folder of its
    /// own. "Keep" ends an undo offer without touching the files. No UnityEngine here; the window refreshes Unity afterwards.
    /// </summary>
    internal static class ProjectConflictFixes
    {
        /// <summary>The package label of a project-check move in its backup-manifest.json (an install uses the package name).</summary>
        public const string MoveLabel = "project-check";
        public const string RemovalName = "removal.json";
        public const string StateRemoved = "removed";
        public const string StateRestored = "restored";
        public const string StateKept = "kept";

        /// <summary>
        /// Checks the project again and returns the same conflict as it is now. Throws <see cref="ConflictChangedException"/> when it is
        /// gone or no longer the one that was shown (another partner, other paths to move, other contents, another manifest value):
        /// a result shown minutes ago, or kept across a script reload, never decides what a fix touches. Worker thread safe.
        /// </summary>
        public static ProjectConflict Recheck(ConflictScanInput input, ProjectConflict shown, System.Threading.CancellationToken cancel)
        {
            foreach (var now in ProjectConflictScan.Run(input, cancel))
            {
                if (now.Key != shown.Key) continue;
                if (now.ManifestValue != shown.ManifestValue || now.EmbeddedFolder != shown.EmbeddedFolder) break;
                if (now.Movable.Count != shown.Movable.Count || now.MovablePrints.Count != shown.MovablePrints.Count) break;
                var same = true;
                for (var i = 0; i < now.Movable.Count && same; i++)
                    same = now.Movable[i] == shown.Movable[i] && now.MovablePrints[i] == shown.MovablePrints[i];
                if (!same) break;
                return now;
            }
            throw new ConflictChangedException(shown.Movable.Count > 0 ? shown.Movable[0] : shown.Paths.Count > 0 ? shown.Paths[0] : shown.Subject);
        }

        // ------------------------------------------------------------ move to backup

        /// <summary>
        /// Moves <see cref="ProjectConflict.Movable"/> (each with its .meta) to Library/OnAirDeck/Backup/legacy-&lt;stamp&gt;/ under
        /// the old-install guard rails (below Assets/, never a package folder, never a protected folder). Everything moved goes back
        /// when one move fails. The backup is listed by <see cref="PendingMoves"/> until it is restored or kept.
        /// </summary>
        public static LegacyMove MoveToBackup(string projectRoot, ProjectConflict conflict, DateTime now)
        {
            if (conflict == null || conflict.Movable.Count == 0) throw new PackageInstallException("Nothing here can be moved to the backup.");
            // The check may be minutes old: move only what still holds exactly what was checked (no user file added since).
            for (var i = 0; i < conflict.Movable.Count; i++)
            {
                var print = i < conflict.MovablePrints.Count ? conflict.MovablePrints[i] : "";
                if (print.Length == 0 || print != ProjectConflictScan.Fingerprint(projectRoot, conflict.Movable[i]))
                    throw new ConflictChangedException(conflict.Movable[i]);
            }
            var plan = new LegacyPlan();
            foreach (var path in conflict.Movable)
                plan.Items.Add(new LegacyItem { Path = path, Files = CountFiles(projectRoot, path), Product = conflict.Package, Source = "project-check" });
            return LegacyInstall.Move(projectRoot, plan, now, MoveLabel, conflict.Key);
        }

        /// <summary>Moves of the project check that can still be undone.</summary>
        public static List<PendingRestore> PendingMoves(string projectRoot)
        {
            return LegacyMove.PendingRestores(projectRoot).FindAll(p => p.Package == MoveLabel);
        }

        /// <summary>"Keep it this way": the backup stays as a backup and is no longer offered for undo. False when the manifest cannot be read.</summary>
        public static bool KeepMove(string manifestPath)
        {
            try
            {
                var root = MiniJson.ParseObject(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (root == null) return false;
                root["state"] = LegacyMove.StateInstalled;
                root["updated"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                AtomicFile.Write(manifestPath, JsonWriter.Write(root, "\n"), true);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static int CountFiles(string root, string relative)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                if (!Directory.Exists(full)) return 1;
                var n = 0;
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    if (!file.EndsWith(".meta", StringComparison.Ordinal)) n++;
                return n;
            }
            catch (Exception)
            {
                return 1;
            }
        }

        // ------------------------------------------------------------ manifest.json entry

        /// <summary>
        /// Takes <paramref name="package"/> out of Packages/manifest.json (Unity already uses the folder under Packages/), after copying
        /// the file to a backup folder with a removal.json. Only that entry changes: order, other entries, indentation and line ends
        /// stay. Refuses (throws, nothing changed) when the result would not read back as the same manifest without that entry.
        /// </summary>
        public static ManifestRemoval RemoveManifestEntry(string projectRoot, string package, DateTime now)
        {
            return RemoveManifestEntry(projectRoot, package, null, null, now);
        }

        /// <param name="expectedValue">The entry's value the check showed (null: any). Another value now refuses.</param>
        /// <param name="embeddedFolder">"Packages/&lt;folder&gt;" that must still hold <paramref name="package"/> (null: not checked).</param>
        public static ManifestRemoval RemoveManifestEntry(string projectRoot, string package, string expectedValue, string embeddedFolder, DateTime now)
        {
            var manifest = Path.Combine(projectRoot, "Packages", "manifest.json");
            var text = File.ReadAllText(manifest, Encoding.UTF8);
            string value;
            var edited = RemoveDependency(text, package, out value);
            if (edited == null) throw new ConflictChangedException("Packages/manifest.json");
            // Unity still has to use the folder, and the entry must be the one that was shown: else the check is out of date.
            if (expectedValue != null && UnescapeJson(value) != expectedValue) throw new ConflictChangedException("Packages/manifest.json");
            if (embeddedFolder != null && !EmbeddedHolds(projectRoot, embeddedFolder, package)) throw new ConflictChangedException(embeddedFolder);
            Verify(text, edited, package, false);

            var stamp = "manifest-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.Combine(PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot)), stamp);
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot)), stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            File.Copy(manifest, Path.Combine(folder, "manifest.json"));
            var removal = new ManifestRemoval { Folder = folder, Package = package, Value = value, State = StateRemoved, Created = now.ToString("o", CultureInfo.InvariantCulture) };
            WriteRemoval(removal);
            AtomicFile.Write(manifest, edited);
            return removal;
        }

        /// <summary>Puts a removed entry back (first in "dependencies"); refuses when the manifest has an entry of that name again.</summary>
        public static void RestoreManifestEntry(string projectRoot, ManifestRemoval removal)
        {
            var manifest = Path.Combine(projectRoot, "Packages", "manifest.json");
            var text = File.ReadAllText(manifest, Encoding.UTF8);
            string existing;
            if (RemoveDependency(text, removal.Package, out existing) != null)
                throw new PackageInstallException("Packages/manifest.json already has an entry for " + removal.Package + " again; nothing was changed.");
            var edited = InsertDependency(text, removal.Package, removal.Value);
            if (edited == null) throw new PackageInstallException("Packages/manifest.json has no \"dependencies\" block; nothing was changed.");
            Verify(edited, text, removal.Package, true);
            AtomicFile.Write(manifest, edited);
            removal.State = StateRestored;
            WriteRemoval(removal);
        }

        private static bool EmbeddedHolds(string projectRoot, string folder, string package)
        {
            try
            {
                var json = Path.Combine(projectRoot, folder.Replace('/', Path.DirectorySeparatorChar), "package.json");
                return File.Exists(json) && MiniJson.GetString(MiniJson.ParseObject(File.ReadAllText(json, Encoding.UTF8)), "name") == package;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static string UnescapeJson(string raw)
        {
            var parsed = MiniJson.ParseObject("{\"v\":\"" + raw + "\"}");
            return parsed != null ? MiniJson.GetString(parsed, "v") ?? raw : raw;
        }

        public static void KeepManifestRemoval(ManifestRemoval removal)
        {
            removal.State = StateKept;
            WriteRemoval(removal);
        }

        /// <summary>Removals that can still be undone, oldest first.</summary>
        public static List<ManifestRemoval> PendingRemovals(string projectRoot)
        {
            var found = new List<ManifestRemoval>();
            try
            {
                var backups = PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot));
                if (!Directory.Exists(backups)) return found;
                var folders = Directory.GetDirectories(backups, "manifest-*");
                Array.Sort(folders, StringComparer.Ordinal);
                foreach (var folder in folders)
                {
                    var removal = ReadRemoval(folder);
                    if (removal != null && removal.State == StateRemoved) found.Add(removal);
                }
            }
            catch (Exception)
            {
            }
            return found;
        }

        private static void WriteRemoval(ManifestRemoval removal)
        {
            var root = new Dictionary<string, object>();
            root["format"] = 1L;
            root["package"] = removal.Package;
            root["value"] = removal.Value;
            root["state"] = removal.State;
            root["created"] = removal.Created;
            root["updated"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            AtomicFile.Write(Path.Combine(removal.Folder, RemovalName), JsonWriter.Write(root, "\n"));
        }

        private static ManifestRemoval ReadRemoval(string folder)
        {
            try
            {
                var root = MiniJson.ParseObject(File.ReadAllText(Path.Combine(folder, RemovalName), Encoding.UTF8));
                if (root == null) return null;
                return new ManifestRemoval
                {
                    Folder = folder,
                    Package = MiniJson.GetString(root, "package") ?? "",
                    Value = MiniJson.GetString(root, "value") ?? "",
                    State = MiniJson.GetString(root, "state") ?? "",
                    Created = MiniJson.GetString(root, "created") ?? "",
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// <paramref name="with"/> and <paramref name="without"/> must read as the same manifest except that <paramref name="package"/>
        /// is in the first and not in the second (every other top-level key and dependency the same).
        /// </summary>
        private static void Verify(string with, string without, string package, bool restoring)
        {
            var a = MiniJson.ParseObject(with);
            var b = MiniJson.ParseObject(without);
            var what = restoring ? "putting back" : "removing";
            if (a == null || b == null) throw new PackageInstallException("Packages/manifest.json would not read back after " + what + " " + package + "; nothing was changed.");
            var depsA = a.ContainsKey("dependencies") ? a["dependencies"] as Dictionary<string, object> : null;
            var depsB = b.ContainsKey("dependencies") ? b["dependencies"] as Dictionary<string, object> : null;
            var same = depsA != null && depsB != null && depsA.ContainsKey(package) && !depsB.ContainsKey(package) && depsA.Count == depsB.Count + 1 && a.Count == b.Count;
            if (same)
                foreach (var pair in depsB)
                {
                    object other;
                    if (!depsA.TryGetValue(pair.Key, out other) || !Equals(other, pair.Value)) { same = false; break; }
                }
            if (!same) throw new PackageInstallException("Packages/manifest.json did not change as expected while " + what + " " + package + "; nothing was changed.");
        }

        // ------------------------------------------------------------ text edits of "dependencies"

        /// <summary>
        /// <paramref name="text"/> without the "dependencies" entry <paramref name="package"/> (its line, and the comma that joined it),
        /// or null when there is none. <paramref name="value"/> is the entry's raw (escaped) string value.
        /// </summary>
        internal static string RemoveDependency(string text, string package, out string value)
        {
            value = "";
            int open, close;
            if (!DependenciesSpan(text, out open, out close)) return null;
            var entry = new Regex("\"" + Regex.Escape(package) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            var match = entry.Match(text, open + 1, close - open - 1);
            // The key must be a key of this object, not text inside another value.
            while (match.Success && !IsKeyPosition(text, open, match.Index)) match = match.NextMatch();
            if (!match.Success || match.Index >= close) return null;
            value = match.Groups[1].Value;

            var start = match.Index;
            var end = match.Index + match.Length;
            var after = SkipSpace(text, end, close);
            if (after < close && text[after] == ',')
            {
                // Not the last entry: take the line from its indentation through the comma and the line end.
                var lineStart = LineStart(text, start, open);
                var cut = after + 1;
                while (cut < close && (text[cut] == ' ' || text[cut] == '\t')) cut++;
                if (cut < close && text[cut] == '\r') cut++;
                if (cut < close && text[cut] == '\n') cut++;
                return text.Substring(0, lineStart) + text.Substring(cut);
            }
            // The last entry: take the comma before it, keep the line end before the closing brace.
            var before = start - 1;
            while (before > open && char.IsWhiteSpace(text[before])) before--;
            if (text[before] == ',') return text.Substring(0, before) + text.Substring(end);
            // The only entry.
            return text.Substring(0, open + 1) + text.Substring(SkipSpaceBack(text, close, end));
        }

        /// <summary><paramref name="text"/> with "<paramref name="package"/>": "<paramref name="rawValue"/>" first in "dependencies", in the file's indentation and line ends.</summary>
        internal static string InsertDependency(string text, string package, string rawValue)
        {
            int open, close;
            if (!DependenciesSpan(text, out open, out close)) return null;
            var newline = text.Contains("\r\n") ? "\r\n" : "\n";
            var entry = "\"" + package.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\": \"" + rawValue + "\"";
            var first = SkipSpace(text, open + 1, close);
            if (first >= close)
            {
                var outer = Indentation(text, LineStart(text, open, 0));
                return text.Substring(0, open + 1) + newline + outer + "  " + entry + newline + outer + text.Substring(close);
            }
            var indent = Indentation(text, LineStart(text, first, open));
            if (LineStart(text, first, open) <= open) return text.Substring(0, first) + entry + ", " + text.Substring(first);
            return text.Substring(0, LineStart(text, first, open)) + indent + entry + "," + newline + text.Substring(LineStart(text, first, open));
        }

        /// <summary>The braces of the top-level "dependencies" object.</summary>
        private static bool DependenciesSpan(string text, out int open, out int close)
        {
            open = close = -1;
            var depth = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    var endString = EndOfString(text, i);
                    if (depth == 1 && string.CompareOrdinal(text, i, "\"dependencies\"", 0, 14) == 0 && endString == i + 13)
                    {
                        var colon = SkipSpace(text, endString + 1, text.Length);
                        if (colon < text.Length && text[colon] == ':')
                        {
                            var brace = SkipSpace(text, colon + 1, text.Length);
                            if (brace < text.Length && text[brace] == '{')
                            {
                                open = brace;
                                close = MatchingBrace(text, brace);
                                return close > open;
                            }
                        }
                    }
                    i = endString;
                }
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return false;
        }

        private static int MatchingBrace(string text, int open)
        {
            var depth = 0;
            for (var i = open; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"') i = EndOfString(text, i);
                else if (c == '{' || c == '[') depth++;
                else if ((c == '}' || c == ']') && --depth == 0) return i;
            }
            return -1;
        }

        private static int EndOfString(string text, int quote)
        {
            for (var i = quote + 1; i < text.Length; i++)
            {
                if (text[i] == '\\') { i++; continue; }
                if (text[i] == '"') return i;
            }
            return text.Length - 1;
        }

        // A key starts right after the object's '{' or a ',' at the object's own depth.
        private static bool IsKeyPosition(string text, int open, int index)
        {
            var i = index - 1;
            while (i > open && char.IsWhiteSpace(text[i])) i--;
            if (i == open) return true;
            if (text[i] != ',') return false;
            // The comma must belong to this object, not to a nested value.
            var depth = 0;
            for (var j = open + 1; j < i; j++)
            {
                var c = text[j];
                if (c == '"') j = EndOfString(text, j);
                else if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return depth == 0;
        }

        private static int SkipSpace(string text, int from, int limit)
        {
            while (from < limit && char.IsWhiteSpace(text[from])) from++;
            return from;
        }

        private static int SkipSpaceBack(string text, int close, int floor)
        {
            var i = close;
            while (i - 1 >= floor && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            if (i - 1 >= floor && text[i - 1] == '\n') { i--; if (i - 1 >= floor && text[i - 1] == '\r') i--; }
            return i;
        }

        private static int LineStart(string text, int index, int floor)
        {
            var i = index;
            while (i > floor + 1 && (text[i - 1] == ' ' || text[i - 1] == '\t')) i--;
            if (i > 0 && (text[i - 1] == '\n')) return i;
            return index;
        }

        private static string Indentation(string text, int lineStart)
        {
            var i = lineStart;
            while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
            return text.Substring(lineStart, i - lineStart);
        }
    }
}
