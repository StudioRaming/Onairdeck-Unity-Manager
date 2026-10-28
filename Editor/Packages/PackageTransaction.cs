using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>One package zip to install, already downloaded and checked against its listing.</summary>
    internal sealed class InstallItem
    {
        public string Name = "";
        public string Version = "";
        public string ZipPath = "";
        /// <summary>Absolute folder to replace. Empty means Packages/&lt;Name&gt; (self-update of an embedded package uses its real folder).</summary>
        public string TargetFolder = "";
        /// <summary>Why this item is in the install when it is not the requested package (shown in the report).</summary>
        public string Note = "";
    }

    internal sealed class InstalledEntry
    {
        public string Name = "";
        public string OldVersion = "";
        public string NewVersion = "";
        public string Target = "";
        public string Staged = "";
        /// <summary>Where the old folder was moved to; "" when nothing was installed before.</summary>
        public string Backup = "";
        /// <summary>Files of the old folder that the new package does not have (relative, '/').</summary>
        public readonly List<string> NotCarriedOver = new List<string>();
        public string Note = "";
        /// <summary>Journal only: which files the package folder holds while its contents are being swapped (see PackageTransaction).</summary>
        public string Phase = "";
    }

    internal sealed class InstallReport
    {
        public readonly List<InstalledEntry> Installed = new List<InstalledEntry>();
        public bool ManifestUpdated;
        public string ManifestBackup = "";
        /// <summary>Set when the product's own installer took over (legacy hand-off). Not an install success: the installer finishes the job.</summary>
        public string HandedOff = "";

        public string Describe()
        {
            if (HandedOff.Length > 0) return HandedOff;
            var sb = new StringBuilder();
            foreach (var entry in Installed)
            {
                sb.Append(entry.Name).Append(' ');
                sb.Append(entry.OldVersion.Length == 0 ? "installed " : entry.OldVersion + " -> ").Append(entry.NewVersion).Append('\n');
                if (entry.Note.Length > 0) sb.Append("  ").Append(entry.Note).Append('\n');
                if (entry.Backup.Length > 0) sb.Append("  Old version kept at ").Append(entry.Backup).Append('\n');
                if (entry.NotCarriedOver.Count > 0)
                {
                    sb.Append("  ").Append(entry.NotCarriedOver.Count).Append(" file(s) of the old version are not in the new one and were not carried over");
                    sb.Append(" (they are in the backup): ");
                    var shown = Math.Min(entry.NotCarriedOver.Count, 8);
                    sb.Append(string.Join(", ", entry.NotCarriedOver.GetRange(0, shown).ToArray()));
                    if (entry.NotCarriedOver.Count > shown) sb.Append(", and ").Append(entry.NotCarriedOver.Count - shown).Append(" more");
                    sb.Append('\n');
                }
            }
            if (ManifestUpdated) sb.Append("Packages/vpm-manifest.json updated (backup: ").Append(ManifestBackup).Append(")\n");
            return sb.ToString().TrimEnd('\n');
        }
    }

    /// <summary>Raised by a test hook to stop a transaction as a process crash would: no undo runs.</summary>
    internal sealed class TransactionCrash : Exception
    {
        public TransactionCrash(string point) : base("simulated crash at " + point) { }
    }

    /// <summary>
    /// One install transaction for a set of packages: stage, journal, swap folders, update vpm-manifest.json,
    /// and undo everything in reverse order on failure. All file-system work takes the project root as a
    /// parameter, so it runs on temp folders without Unity.
    /// </summary>
    /// <remarks>
    /// An installed package folder is never renamed. Unity watches every embedded package folder and keeps watching
    /// it after a rename, so a renamed-away folder with a new one in its place leaves every later AssetDatabase.Refresh
    /// waiting forever in DirectoryMonitorWindows::Sync (reproduced on Unity 2021.3.45f2, 2026-10-26; Unity's own
    /// refresh after Client.Resolve hangs the same way). Deleting or renaming a folder away with nothing in its place
    /// does not hang. So an update moves what is inside the folder: the old contents out to the backup, then the new
    /// contents in. The journal records which of the two the folder holds (Phase), so a crash between any two moves
    /// can be undone from disk.
    /// </remarks>
    internal static class PackageTransaction
    {
        private const string StateApplying = "applying";
        private const string StateComplete = "complete";
        private const string StateRolledBack = "rolledback";

        // Format 1 moved whole package folders; its unfinished journals cannot be undone with the contents moves below.
        private const long JournalFormat = 2;

        // What an updated package's folder holds. OldOut: only old files, the rest already in the backup. NewIn: only
        // new files, all old ones in the backup. OldBack: a rollback cleared the new files and is moving the old back.
        private const string PhaseOldOut = "old-out";
        private const string PhaseNewIn = "new-in";
        private const string PhaseOldBack = "old-back";

        private static readonly Regex PackageName = new Regex(@"^[a-z0-9]+(\.[a-z0-9-]+)+\z", RegexOptions.CultureInvariant);

        /// <summary>Test seam: called with a named point; may throw to fail the step or TransactionCrash to simulate a crash.</summary>
        internal static Action<string> FaultHook;

        /// <summary>How long a folder move keeps retrying while a file inside it is held open (see MoveFolder).</summary>
        internal static int MoveRetryBudgetMs = 5000;

        public static string JournalPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "OnAirDeck", "install-journal.json");
        }

        public static string BackupRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "OnAirDeck", "Backup");
        }

        private static string StageRoot(string projectRoot)
        {
            return Path.Combine(projectRoot, "Temp", "OnAirDeck");
        }

        // ---------- install ----------

        public static InstallReport Execute(string projectRoot, IList<InstallItem> items, DateTime now)
        {
            if (items == null || items.Count == 0) throw new PackageInstallException("Nothing to install.");
            projectRoot = Path.GetFullPath(projectRoot);

            var pending = Recover(projectRoot);
            if (pending != null)
                throw new PackageInstallException("A previous install was interrupted and has been rolled back. Review it, then try again.\n" + pending);

            foreach (var item in items)
            {
                if (!PackageName.IsMatch(item.Name)) throw new PackageInstallException("Not a valid package name: \"" + item.Name + "\".");
                var refusal = InstalledPackages.LegacyRefusal(projectRoot, item.Name);
                if (refusal != null) throw new PackageInstallException(refusal);
            }

            // Duplicate GUIDs break references. The folders being replaced do not count: their old files go away.
            var targets = new List<string>();
            var guids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                targets.Add(TargetOf(projectRoot, item));
                foreach (var guid in GuidCollisions.ReadZipGuids(item.ZipPath))
                {
                    // Two packages of one install that share a GUID would collide with each other after the swap.
                    if (!guids.Add(guid))
                        throw new PackageInstallException("Cannot install: " + item.Name + " and another package in this install both use GUID " + guid + ".");
                }
            }
            var collisions = GuidCollisions.Scan(projectRoot, guids, targets);
            if (collisions.Count > 0) throw new PackageInstallException(GuidCollisions.Refusal(collisions));

            var stageFolder = Path.Combine(StageRoot(projectRoot), "stage-" + Guid.NewGuid().ToString("N"));
            var journal = new Journal { StageFolder = stageFolder };
            var journalWritten = false;
            try
            {
                var report = new InstallReport();
                var manifests = new List<PackageManifest>();
                var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

                foreach (var item in items)
                {
                    Fault("stage:" + item.Name);
                    var staged = Path.Combine(stageFolder, item.Name);
                    manifests.Add(PackageZip.Extract(item.ZipPath, staged, item.Name, item.Version));

                    var target = TargetOf(projectRoot, item);
                    if (!PathSafety.IsInside(Path.Combine(projectRoot, "Packages"), target))
                        throw new PackageInstallException("Refusing to install " + item.Name + " outside the project's Packages folder.");

                    var entry = new InstalledEntry { Name = item.Name, NewVersion = item.Version, Target = target, Staged = staged, Note = item.Note };
                    if (Directory.Exists(target))
                    {
                        entry.OldVersion = ReadFolderVersion(target);
                        entry.Backup = UniqueBackup(projectRoot, item.Name, entry.OldVersion.Length == 0 ? "unknown" : entry.OldVersion, stamp);
                        entry.NotCarriedOver.AddRange(FilesMissingFrom(target, staged));
                    }
                    else if (File.Exists(target))
                    {
                        throw new PackageInstallException("A file is in the way of " + target + ".");
                    }
                    report.Installed.Add(entry);
                }

                // Compute the manifest edit before anything moves: an unreadable file must fail the install, not the undo.
                var manifestPath = Path.Combine(projectRoot, "Packages", "vpm-manifest.json");
                string newManifestText = null;
                if (File.Exists(manifestPath))
                    newManifestText = VpmManifestUpdater.Update(File.ReadAllText(manifestPath, new UTF8Encoding(false)), manifests);
                if (newManifestText != null)
                {
                    // Not inside a package backup: that folder moves back into Packages on undo.
                    var folder = Path.Combine(BackupRoot(projectRoot), "vpm-manifest-" + stamp + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                    journal.ManifestPath = manifestPath;
                    journal.ManifestBackup = Path.Combine(folder, "vpm-manifest.json");
                    report.ManifestUpdated = true;
                    report.ManifestBackup = journal.ManifestBackup;
                }
                journal.Entries.AddRange(report.Installed);

                Directory.CreateDirectory(BackupRoot(projectRoot));
                Fault("journal");
                journal.State = StateApplying;
                journal.Write(projectRoot, now, "journal-write:applying");
                journalWritten = true;

                if (newManifestText != null)
                {
                    Fault("manifest-backup");
                    Directory.CreateDirectory(Path.GetDirectoryName(journal.ManifestBackup));
                    File.Copy(manifestPath, journal.ManifestBackup, false);
                }

                foreach (var entry in report.Installed)
                {
                    Fault("before-backup:" + entry.Name);
                    if (entry.Backup.Length > 0)
                    {
                        SetPhase(projectRoot, journal, entry, PhaseOldOut, now);
                        MoveContents(entry.Target, entry.Backup, "moved-old:" + entry.Name);
                        Fault("after-backup:" + entry.Name);
                        if (Directory.GetFileSystemEntries(entry.Target).Length > 0)
                            throw new IOException("Files appeared in " + entry.Target + " during the install.");
                        SetPhase(projectRoot, journal, entry, PhaseNewIn, now);
                        MoveContents(entry.Staged, entry.Target, "moved-new:" + entry.Name);
                    }
                    else
                    {
                        // Nothing to keep watching yet: the staged folder becomes the package folder.
                        Fault("after-backup:" + entry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(entry.Target));
                        MoveEntry(entry.Staged, entry.Target);
                    }
                    Fault("after-install:" + entry.Name);
                }

                if (newManifestText != null)
                {
                    Fault("before-manifest");
                    WriteManifest(manifestPath, newManifestText);
                    Fault("after-manifest");
                }

                journal.State = StateComplete;
                journal.Write(projectRoot, now, "journal-write:complete");
                TryDeleteStage(projectRoot, stageFolder);
                return report;
            }
            catch (TransactionCrash)
            {
                throw;
            }
            catch (Exception e)
            {
                if (!journalWritten)
                {
                    TryDeleteStage(projectRoot, stageFolder);
                    if (e is PackageInstallException) throw;
                    throw new PackageInstallException("Install failed before any change was made: " + e.Message, e);
                }
                List<string> notes;
                try
                {
                    notes = Undo(projectRoot, journal, now);
                    journal.State = StateRolledBack;
                    journal.Write(projectRoot, now, "journal-write:rolledback");
                    TryDeleteStage(projectRoot, stageFolder);
                }
                catch (Exception undoFailure)
                {
                    // The journal stays "applying", so the next editor load tries the rollback again.
                    throw new PackageInstallException("Install failed (" + e.Message + ") and the rollback could not finish: " + undoFailure.Message
                        + "\nOld versions are under " + BackupRoot(projectRoot) + ". The rollback is retried when the editor loads again.", e);
                }
                throw new PackageInstallException("Install failed and was rolled back: " + e.Message + (notes.Count > 0 ? "\n" + string.Join("\n", notes.ToArray()) : ""), e);
            }
        }

        // ---------- recovery ----------

        /// <summary>
        /// Rolls back an install that never completed (editor closed or crashed mid-swap). Returns a
        /// description of what was undone, or null when there is nothing to recover.
        /// </summary>
        public static string Recover(string projectRoot)
        {
            projectRoot = Path.GetFullPath(projectRoot);
            var path = JournalPath(projectRoot);
            if (!File.Exists(path)) return null;

            Journal journal;
            try
            {
                journal = Journal.Read(projectRoot, path);
            }
            catch (PackageInstallException e)
            {
                var aside = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                File.Move(path, aside);
                return "The install journal could not be read (" + e.Message + ") and was moved to " + aside + ". Check Packages/ and " + BackupRoot(projectRoot) + " by hand.";
            }
            if (journal.State == StateComplete || journal.State == StateRolledBack) return null;

            var notes = Undo(projectRoot, journal, DateTime.Now);
            journal.State = StateRolledBack;
            journal.Write(projectRoot, DateTime.Now, "journal-write:rolledback");
            TryDeleteStage(projectRoot, journal.StageFolder);

            var sb = new StringBuilder("An interrupted package install was rolled back:");
            foreach (var entry in journal.Entries) sb.Append("\n  ").Append(entry.Name).Append(entry.Backup.Length > 0 ? " restored to " + entry.OldVersion : " removed");
            foreach (var note in notes) sb.Append("\n  ").Append(note);
            return sb.ToString();
        }

        // Works from the journal's phase and what is on disk: a crash can fall between any two moves, here too.
        private static List<string> Undo(string projectRoot, Journal journal, DateTime now)
        {
            // Old-out is journaled before the first move, so a package without a phase was never touched. A backup that
            // holds files says otherwise: the journal is wrong, so nothing moves and it stays unfinished for a person.
            foreach (var entry in journal.Entries)
            {
                if (entry.Backup.Length > 0 && entry.Phase.Length == 0 && Directory.Exists(entry.Backup) && Directory.GetFileSystemEntries(entry.Backup).Length > 0)
                    throw new PackageInstallException("The install journal says " + entry.Name + " was not changed, but its backup " + entry.Backup
                        + " holds files. Nothing was rolled back; check Packages/ and the backup by hand.");
            }

            var notes = new List<string>();
            var rollbackFolder = Path.Combine(journal.StageFolder, "rollback");
            for (var i = journal.Entries.Count - 1; i >= 0; i--)
            {
                var entry = journal.Entries[i];
                Fault("undo:" + entry.Name);
                if (entry.Backup.Length > 0)
                {
                    if (entry.Phase == PhaseNewIn)
                    {
                        // The folder holds only new files (some may still be staged): set them aside, keep the folder.
                        if (Directory.Exists(entry.Target)) MoveContents(entry.Target, AsideFolder(rollbackFolder, entry.Name), "set-aside:" + entry.Name);
                        SetPhase(projectRoot, journal, entry, PhaseOldBack, now);
                    }
                    if ((entry.Phase == PhaseOldOut || entry.Phase == PhaseOldBack) && Directory.Exists(entry.Backup))
                    {
                        // The folder holds only old files; the rest of them come back from the backup.
                        MoveContents(entry.Backup, entry.Target, "restored:" + entry.Name);
                        TryDeleteEmptyFolder(entry.Backup);
                    }
                    // No phase: this package's folder was never touched.
                }
                else if (!Directory.Exists(entry.Staged) && Directory.Exists(entry.Target))
                {
                    // Nothing was installed before and the staged folder is gone: the target is our new folder.
                    MoveAside(entry.Target, rollbackFolder, entry.Name);
                }
            }
            if (journal.ManifestBackup.Length > 0 && File.Exists(journal.ManifestBackup))
            {
                File.Copy(journal.ManifestBackup, journal.ManifestPath, true);
                notes.Add("Packages/vpm-manifest.json restored from " + journal.ManifestBackup);
            }
            return notes;
        }

        // A newly installed package is removed whole: renaming a folder away with nothing in its place does not upset Unity's watcher.
        private static void MoveAside(string folder, string rollbackFolder, string name)
        {
            MoveEntry(folder, AsideFolder(rollbackFolder, name));
        }

        private static string AsideFolder(string rollbackFolder, string name)
        {
            var destination = Path.Combine(rollbackFolder, name);
            if (Directory.Exists(destination)) destination += "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            if (!PathSafety.IsInside(rollbackFolder, destination))
                throw new PackageInstallException("Refusing to set " + name + " aside outside " + rollbackFolder + ".");
            Directory.CreateDirectory(rollbackFolder);
            return destination;
        }

        // ---------- helpers ----------

        private static void SetPhase(string projectRoot, Journal journal, InstalledEntry entry, string phase, DateTime now)
        {
            entry.Phase = phase;
            journal.Write(projectRoot, now, "journal-write:" + phase + ":" + entry.Name);
        }

        /// <summary>Moves everything inside <paramref name="from"/> into <paramref name="to"/> (created when missing), one entry at a time; <paramref name="from"/> itself stays.</summary>
        private static void MoveContents(string from, string to, string faultPrefix)
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from)) return;
            var children = Directory.GetFileSystemEntries(from);
            Array.Sort(children, StringComparer.Ordinal);
            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                MoveEntry(child, Path.Combine(to, name));
                Fault(faultPrefix + "/" + name);
            }
        }

        // On Windows a folder cannot be renamed while a file inside it is open, and antivirus or the search indexer
        // opens freshly written files for a moment after they are closed. Measured on 2026-10-26 with Defender on:
        // 8 of 80 add-on extractions failed the first move with "access denied", and every one moved on a retry
        // within 135 ms. So a move is retried for a short while; anything that is not a transient lock fails at once.
        internal static void MoveEntry(string source, string destination)
        {
            var isFolder = Directory.Exists(source);
            var waited = 0;
            var delay = 25;
            while (true)
            {
                try
                {
                    if (isFolder) Directory.Move(source, destination);
                    else File.Move(source, destination);
                    return;
                }
                catch (Exception e)
                {
                    if (!(e is IOException || e is UnauthorizedAccessException) || waited >= MoveRetryBudgetMs) throw;
                    // A missing source, a destination in the way or another volume will not clear up by waiting.
                    if (!(isFolder ? Directory.Exists(source) : File.Exists(source)) || Directory.Exists(destination) || File.Exists(destination)) throw;
                    if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(source)), Path.GetPathRoot(Path.GetFullPath(destination)), StringComparison.OrdinalIgnoreCase)) throw;
                }
                Thread.Sleep(delay);
                waited += delay;
                delay = Math.Min(delay * 2, 500);
            }
        }

        private static void TryDeleteEmptyFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder, false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static string TargetOf(string projectRoot, InstallItem item)
        {
            return item.TargetFolder.Length > 0 ? Path.GetFullPath(item.TargetFolder) : Path.GetFullPath(InstalledPackages.FolderOf(projectRoot, item.Name));
        }

        private static void Fault(string point)
        {
            var hook = FaultHook;
            if (hook != null) hook(point);
        }

        private static string ReadFolderVersion(string folder)
        {
            try
            {
                var path = Path.Combine(folder, "package.json");
                return File.Exists(path) ? PackageZip.ParseManifest(File.ReadAllText(path, Encoding.UTF8), false).Version : "";
            }
            catch (PackageInstallException)
            {
                return "";
            }
        }

        private static string UniqueBackup(string projectRoot, string name, string version, string stamp)
        {
            var baseName = Path.Combine(BackupRoot(projectRoot), name + "-" + version + "-" + stamp);
            var path = baseName;
            for (var n = 2; Directory.Exists(path) || File.Exists(path); n++) path = baseName + "-" + n.ToString(CultureInfo.InvariantCulture);
            return path;
        }

        /// <summary>Files of <paramref name="oldFolder"/> that <paramref name="newFolder"/> lacks; a .meta only counts when its asset is gone too.</summary>
        internal static List<string> FilesMissingFrom(string oldFolder, string newFolder)
        {
            var missing = new List<string>();
            var oldFull = Path.GetFullPath(oldFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var file in Directory.GetFiles(oldFull, "*", SearchOption.AllDirectories))
            {
                var relative = file.Substring(oldFull.Length + 1).Replace('\\', '/');
                if (ExistsIn(newFolder, relative)) continue;
                if (relative.EndsWith(".meta", StringComparison.Ordinal) && ExistsIn(newFolder, relative.Substring(0, relative.Length - 5))) continue;
                missing.Add(relative);
            }
            missing.Sort(StringComparer.Ordinal);
            return missing;
        }

        private static bool ExistsIn(string folder, string relative)
        {
            var path = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(path) || Directory.Exists(path);
        }

        private static void WriteManifest(string path, string text)
        {
            var temp = path + ".onairdeck-tmp";
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            ReplaceFile(temp, path);
        }

        // Puts temp in place of path in one rename, so a crash leaves the old file or the new one, never part of one.
        private static void ReplaceFile(string temp, string path)
        {
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        private static void TryDeleteStage(string projectRoot, string stageFolder)
        {
            try
            {
                if (string.IsNullOrEmpty(stageFolder) || !Directory.Exists(stageFolder)) return;
                // Only ever delete a folder this transaction owns, under Temp/OnAirDeck.
                if (!PathSafety.IsInside(StageRoot(projectRoot), stageFolder)) return;
                Directory.Delete(stageFolder, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------- journal ----------

        private sealed class Journal
        {
            public string State = StateApplying;
            public string StageFolder = "";
            public string ManifestPath = "";
            public string ManifestBackup = "";
            public readonly List<InstalledEntry> Entries = new List<InstalledEntry>();

            // Recovery trusts this file between any two moves, so it is replaced whole, never overwritten in place.
            public void Write(string projectRoot, DateTime now, string faultPoint)
            {
                var entries = new List<object>();
                foreach (var entry in Entries)
                {
                    var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                    fields["name"] = entry.Name;
                    fields["oldVersion"] = entry.OldVersion;
                    fields["newVersion"] = entry.NewVersion;
                    fields["target"] = entry.Target;
                    fields["staged"] = entry.Staged;
                    fields["backup"] = entry.Backup;
                    fields["phase"] = entry.Phase;
                    entries.Add(fields);
                }
                var root = new Dictionary<string, object>(StringComparer.Ordinal);
                root["format"] = JournalFormat;
                root["state"] = State;
                root["updatedAt"] = now.ToString("o", CultureInfo.InvariantCulture);
                root["stageFolder"] = StageFolder;
                root["manifestPath"] = ManifestPath;
                root["manifestBackup"] = ManifestBackup;
                root["entries"] = entries;

                var path = JournalPath(projectRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temp = path + ".tmp";
                File.WriteAllText(temp, JsonWriter.Write(root, "\n") + "\n", new UTF8Encoding(false));
                Fault(faultPoint);
                ReplaceFile(temp, path);
            }

            public static Journal Read(string projectRoot, string path)
            {
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) throw new PackageInstallException("not valid JSON");
                var journal = new Journal
                {
                    State = MiniJson.GetString(root, "state") ?? "",
                    StageFolder = MiniJson.GetString(root, "stageFolder") ?? "",
                    ManifestPath = MiniJson.GetString(root, "manifestPath") ?? "",
                    ManifestBackup = MiniJson.GetString(root, "manifestBackup") ?? ""
                };
                long format;
                var finished = journal.State == StateComplete || journal.State == StateRolledBack;
                if (!finished && (!MiniJson.TryGetInt(root, "format", out format) || format != JournalFormat))
                    throw new PackageInstallException("written by an older Manager build that moved whole package folders");
                var packages = Path.Combine(projectRoot, "Packages");
                var backups = BackupRoot(projectRoot);
                var stage = StageRoot(projectRoot);
                // The journal drives moves and overwrites, so a path outside the places the Manager uses is refused.
                if (journal.StageFolder.Length > 0 && !PathSafety.IsInside(stage, journal.StageFolder)) throw new PackageInstallException("stage folder outside Temp/OnAirDeck");
                // An undo sets files aside under the stage folder; an empty one would resolve against the working directory.
                if (!finished && journal.StageFolder.Length == 0) throw new PackageInstallException("no stage folder");
                if (journal.ManifestBackup.Length > 0 && (!PathSafety.IsInside(backups, journal.ManifestBackup) || !PathSafety.IsInside(packages, journal.ManifestPath)))
                    throw new PackageInstallException("manifest paths outside the project");

                object list;
                var raw = root.TryGetValue("entries", out list) ? list as List<object> : null;
                if (raw == null) throw new PackageInstallException("no entries");
                foreach (var item in raw)
                {
                    var fields = item as Dictionary<string, object>;
                    if (fields == null) throw new PackageInstallException("bad entry");
                    var entry = new InstalledEntry
                    {
                        Name = MiniJson.GetString(fields, "name") ?? "",
                        OldVersion = MiniJson.GetString(fields, "oldVersion") ?? "",
                        NewVersion = MiniJson.GetString(fields, "newVersion") ?? "",
                        Target = MiniJson.GetString(fields, "target") ?? "",
                        Staged = MiniJson.GetString(fields, "staged") ?? "",
                        Backup = MiniJson.GetString(fields, "backup") ?? "",
                        Phase = MiniJson.GetString(fields, "phase") ?? ""
                    };
                    // The name becomes a path under the rollback folder, and a missing phase would read as "never touched".
                    if (!PackageName.IsMatch(entry.Name)) throw new PackageInstallException("not a valid package name");
                    object phase;
                    if (!finished && (!fields.TryGetValue("phase", out phase) || !(phase is string))) throw new PackageInstallException("no phase");
                    if (entry.Phase.Length > 0 && entry.Phase != PhaseOldOut && entry.Phase != PhaseNewIn && entry.Phase != PhaseOldBack)
                        throw new PackageInstallException("unknown phase \"" + entry.Phase + "\"");
                    if (entry.Target.Length == 0 || !PathSafety.IsInside(packages, entry.Target)) throw new PackageInstallException("target outside Packages");
                    if (entry.Staged.Length == 0 || !PathSafety.IsInside(stage, entry.Staged)) throw new PackageInstallException("staged folder outside Temp/OnAirDeck");
                    if (entry.Backup.Length > 0 && !PathSafety.IsInside(backups, entry.Backup)) throw new PackageInstallException("backup outside Library/OnAirDeck/Backup");
                    journal.Entries.Add(entry);
                }
                return journal;
            }
        }
    }
}
