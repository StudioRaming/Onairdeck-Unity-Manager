using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    internal enum AutoUpdateAction
    {
        /// <summary>Nothing to install (no newer release, or this install cannot take it by itself).</summary>
        None,
        /// <summary>Install now.</summary>
        Apply,
        /// <summary>A newer release waits for a safe moment (an install, import, compile or play mode is running).</summary>
        Wait,
        /// <summary>This release failed here, or its install is still settling: only the Update button installs it (a failed one).</summary>
        Blocked
    }

    /// <summary>What the editor is doing now; any of it makes this a bad moment to replace the Manager.</summary>
    internal struct AutoUpdateMoment
    {
        public bool WindowBusy;       // an install, batch, self-update or hand-off of a window, or another self-update
        public bool Compiling;        // EditorApplication.isCompiling or isUpdating
        public bool Playing;          // play mode, or entering it
        public bool ImportsQueued;    // PackageImportQueue has .unitypackage imports to run
        public bool ImportRequest;    // the one-button import holds a request

        public bool IsSafe { get { return !WindowBusy && !Compiling && !Playing && !ImportsQueued && !ImportRequest; } }

        /// <summary>Why this is not a safe moment, or null.</summary>
        public string Reason()
        {
            if (Playing) return "Play mode is running.";
            if (Compiling) return "Scripts are compiling.";
            if (WindowBusy) return "Another install is running.";
            if (ImportsQueued || ImportRequest) return "An import is running.";
            return null;
        }
    }

    /// <summary>One folder an update swapped and where its previous contents went (the install transaction's backup).</summary>
    internal sealed class SelfUpdateSwap
    {
        public string Name = "";
        public string Target = "";
        public string Backup = "";
        /// <summary>The rollback has put this folder back (saved after each folder, so a cut rollback resumes after it).</summary>
        public bool Restored;
        /// <summary>Set (and saved) once every new file left the folder for this place: a cut restore then only brings the rest of the
        /// backup in, never moves the old files that already came back.</summary>
        public string FailedDir = "";
    }

    /// <summary>
    /// The self-update record (owner decision 2026-10-31: the Managers update themselves). Written before any update starts, by a click
    /// or automatically, to Library/OnAirDeck/self-update.json: the version that runs after the next script reload reads it to show
    /// "업데이트됨 a → b"; the version still loaded when the new code fails to compile reads it to put every swapped folder (and
    /// vpm-manifest.json) back. No UnityEngine here.
    /// </summary>
    internal sealed class SelfUpdateRecord
    {
        public const string PhaseApplying = "applying";
        public const string PhaseApplied = "applied";
        /// <summary>The rollback is moving files; a load that finds this phase finishes it.</summary>
        public const string PhaseRestoring = "restoring";
        public const string PhaseRolledBack = "rolled-back";
        /// <summary>Failed before the swap, or a rolled-back release whose note was seen: only a click installs it again.</summary>
        public const string PhaseFailed = "failed";
        /// <summary>Stopped before anything changed (the moment turned unsafe): tried again later.</summary>
        public const string PhaseDeferred = "deferred";
        public const string PhaseShown = "shown";

        public string From = "";
        public string To = "";
        /// <summary>"embedded" (folder swap from the release zip) or "git" (Package Manager switched to the release's git URL).</summary>
        public string Route = "";
        public string Phase = "";
        public string PackageFolder = "";
        /// <summary>Embedded: the Manager folder's backup (also in <see cref="Swaps"/>).</summary>
        public string Backup = "";
        /// <summary>Embedded: every folder the transaction swapped (the Manager and any dependency it brought), in order.</summary>
        public readonly List<SelfUpdateSwap> Swaps = new List<SelfUpdateSwap>();
        /// <summary>Embedded: vpm-manifest.json and its copy from before the update, when the transaction changed it.</summary>
        public string ManifestPath = "";
        public string ManifestBackup = "";
        /// <summary>Git: the package id to add back (name@url#tag) when the new version does not compile.</summary>
        public string OldPackageId = "";
        /// <summary>The Manager's package name (its logical path Packages/&lt;name&gt;/ is what the compiler reports, whatever the route).</summary>
        public string PackageName = "";
        public string Error = "";
        public string Updated = "";
        /// <summary>The first check of this project found the release (an old .unitypackage or a pinned git URL was installed): "최신 버전으로 업데이트했어요".</summary>
        public bool FirstRun;
        /// <summary>A click started it (the Update button or the required-update screen).</summary>
        public bool Manual;
    }

    internal static class AutoUpdateLogic
    {
        public const string RecordName = "self-update.json";
        /// <summary>A window opened again within this time does not ask the server again.</summary>
        public const long OpenRecheckSeconds = 10 * 60;
        /// <summary>While a window stays open, the server is asked again after this long.</summary>
        public const long DailySeconds = 24 * 60 * 60;
        /// <summary>A failed check (offline, server error) is tried again after this long, not on every tick.</summary>
        public const long RetrySeconds = 10 * 60;

        /// <summary>True when the window should ask plugin-version now: on open unless asked within 10 minutes, else once a day.</summary>
        public static bool CheckDue(bool onOpen, long now, long lastCheck)
        {
            if (lastCheck <= 0 || now < lastCheck) return true;
            return now - lastCheck >= (onOpen ? OpenRecheckSeconds : DailySeconds);
        }

        /// <summary>The release in the answer is newer than the code that runs (a server answer can never move the Manager back).</summary>
        public static bool IsNewer(string latest, string running)
        {
            return !string.IsNullOrEmpty(latest) && PackageVersion.Compare(latest, running ?? "") > 0;
        }

        /// <summary>
        /// What to do with the last answer. <paramref name="blockedVersion"/> is a release whose install is still settling or failed here
        /// (<see cref="BlockedVersion"/>): never installed again by itself, so a broken build cannot loop. A required update follows the
        /// same rule; the blocking screen offers the click.
        /// </summary>
        public static AutoUpdateAction Decide(bool updateAvailable, bool canApply, string latestVersion, string runningVersion, string blockedVersion, AutoUpdateMoment moment)
        {
            if (!updateAvailable || !canApply || !IsNewer(latestVersion, runningVersion)) return AutoUpdateAction.None;
            if (!string.IsNullOrEmpty(blockedVersion) && blockedVersion == latestVersion) return AutoUpdateAction.Blocked;
            return moment.IsSafe ? AutoUpdateAction.Apply : AutoUpdateAction.Wait;
        }

        /// <summary>The release no automatic install may take now: one being installed or rolled back, or one that failed. "" when none.</summary>
        /// <summary>An update is in flight or being undone: no other update, by a click either, may start (it would overwrite the record and the backup).</summary>
        public static bool InFlight(SelfUpdateRecord record)
        {
            return record != null && (record.Phase == SelfUpdateRecord.PhaseApplying || record.Phase == SelfUpdateRecord.PhaseRestoring);
        }

        public static string BlockedVersion(SelfUpdateRecord record)
        {
            if (record == null) return "";
            switch (record.Phase)
            {
                case SelfUpdateRecord.PhaseApplying:
                case SelfUpdateRecord.PhaseRestoring:
                case SelfUpdateRecord.PhaseRolledBack:
                case SelfUpdateRecord.PhaseFailed:
                    return record.To;
                default:
                    return "";
            }
        }

        /// <summary>
        /// Settles a record after a script reload, given the version of the code that now runs: the new version means the update is in.
        /// The old one means it never took: cut before the swap (an embedded update with no swap recorded) it is tried again later; cut
        /// after it (the journal rolled it back, or the git route), it counts as failed. Returns true when it changed.
        /// </summary>
        public static bool Settle(SelfUpdateRecord record, string running)
        {
            if (record == null || record.Phase != SelfUpdateRecord.PhaseApplying) return false;
            if (running == record.To) record.Phase = SelfUpdateRecord.PhaseApplied;
            else if (record.Route == "embedded" && record.Swaps.Count == 0)
            {
                record.Phase = SelfUpdateRecord.PhaseDeferred;
                record.Error = "Cut before anything changed.";
            }
            else
            {
                record.Phase = SelfUpdateRecord.PhaseFailed;
                record.Error = "The install did not finish.";
            }
            return true;
        }

        /// <summary>The note the running version shows once; null when there is none for <paramref name="running"/>.</summary>
        public static string NoteFor(SelfUpdateRecord record, string running)
        {
            if (record == null) return null;
            if (record.Phase == SelfUpdateRecord.PhaseApplied && record.To == running) return "updated";
            if (record.Phase == SelfUpdateRecord.PhaseRolledBack && record.From == running) return "rolled-back";
            return null;
        }

        /// <summary>
        /// True when a compile error belongs to the update: its file is inside one of the folders the update swapped (any assembly there,
        /// also one the new release added). <paramref name="file"/> is the compiler's path, project-relative or absolute.
        /// </summary>
        public static bool ErrorInUpdate(SelfUpdateRecord record, string projectRoot, string file)
        {
            if (record == null || string.IsNullOrEmpty(file)) return false;
            // Embedded: nothing of the update is on disk until the swap is recorded; an error before it is the old code's own.
            if (record.Route == "embedded" && record.Swaps.Count == 0) return false;
            var logical = file.Replace('\\', '/');
            var names = new List<string>();
            foreach (var swap in record.Swaps) if (!string.IsNullOrEmpty(swap.Name)) names.Add(swap.Name);
            if (record.PackageName.Length > 0) names.Add(record.PackageName);
            // The compiler names a package's file by its logical path, the same for an embedded folder and a git copy in the cache.
            foreach (var name in names)
                if (logical.StartsWith("Packages/" + name + "/", StringComparison.OrdinalIgnoreCase)) return true;
            var full = Normalize(Path.IsPathRooted(file) ? file : Path.Combine(projectRoot, file));
            var folders = new List<string>();
            foreach (var swap in record.Swaps) folders.Add(swap.Target);
            if (record.Route == "embedded" && record.PackageFolder.Length > 0) folders.Add(record.PackageFolder);
            foreach (var folder in folders)
            {
                if (string.IsNullOrEmpty(folder)) continue;
                var root = Normalize(Path.IsPathRooted(folder) ? folder : Path.Combine(projectRoot, folder)).TrimEnd('/') + "/";
                if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string Normalize(string path)
        {
            try { return Path.GetFullPath(path).Replace('\\', '/'); }
            catch (Exception) { return path.Replace('\\', '/'); }
        }

        /// <summary>FNV-1a of the lower-cased text, as 8 hex digits: the same in every run (string.GetHashCode is not guaranteed to be).</summary>
        public static string StableHash(string text)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var c in (text ?? "").ToLowerInvariant()) { hash ^= c; hash *= 16777619u; }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        // ------------------------------------------------------------ record file

        public static string RecordPath(string projectRoot)
        {
            return Path.Combine(projectRoot, "Library", "OnAirDeck", RecordName);
        }

        /// <summary>True when a record file exists but cannot be read or parsed: nothing may start over it.</summary>
        public static bool Unreadable(string projectRoot)
        {
            SelfUpdateRecord record;
            return !TryLoad(projectRoot, out record);
        }

        /// <summary>
        /// One read of the record: false when a record file is there but cannot be read or parsed (held by another process, broken), true
        /// with the record, or with null when there is none. A caller that decides from it decides from this one read.
        /// </summary>
        public static bool TryLoad(string projectRoot, out SelfUpdateRecord record)
        {
            record = Load(projectRoot);
            return record != null || !File.Exists(RecordPath(projectRoot));
        }

        public static SelfUpdateRecord Load(string projectRoot)
        {
            try
            {
                var path = RecordPath(projectRoot);
                if (!File.Exists(path)) return null;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return null;
                var record = new SelfUpdateRecord
                {
                    From = MiniJson.GetString(root, "from") ?? "",
                    To = MiniJson.GetString(root, "to") ?? "",
                    Route = MiniJson.GetString(root, "route") ?? "",
                    Phase = MiniJson.GetString(root, "phase") ?? "",
                    PackageFolder = MiniJson.GetString(root, "packageFolder") ?? "",
                    Backup = MiniJson.GetString(root, "backup") ?? "",
                    ManifestPath = MiniJson.GetString(root, "manifestPath") ?? "",
                    ManifestBackup = MiniJson.GetString(root, "manifestBackup") ?? "",
                    OldPackageId = MiniJson.GetString(root, "oldPackageId") ?? "",
                    PackageName = MiniJson.GetString(root, "packageName") ?? "",
                    Error = MiniJson.GetString(root, "error") ?? "",
                    Updated = MiniJson.GetString(root, "updated") ?? "",
                    FirstRun = MiniJson.GetString(root, "firstRun") == "1",
                    Manual = MiniJson.GetString(root, "manual") == "1",
                };
                object swaps;
                if (root.TryGetValue("swaps", out swaps) && swaps is List<object>)
                    foreach (var item in (List<object>)swaps)
                    {
                        var swap = item as Dictionary<string, object>;
                        if (swap != null) record.Swaps.Add(new SelfUpdateSwap
                        {
                            Name = MiniJson.GetString(swap, "name") ?? "", Target = MiniJson.GetString(swap, "target") ?? "",
                            Backup = MiniJson.GetString(swap, "backup") ?? "", Restored = MiniJson.GetString(swap, "restored") == "1",
                            FailedDir = MiniJson.GetString(swap, "failedDir") ?? "",
                        });
                    }
                return record;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Save(string projectRoot, SelfUpdateRecord record)
        {
            var root = new Dictionary<string, object>();
            root["format"] = 2L;
            root["from"] = record.From;
            root["to"] = record.To;
            root["route"] = record.Route;
            root["phase"] = record.Phase;
            root["packageFolder"] = record.PackageFolder;
            root["backup"] = record.Backup;
            var swaps = new List<object>();
            foreach (var swap in record.Swaps)
                swaps.Add(new Dictionary<string, object> { { "name", swap.Name }, { "target", swap.Target }, { "backup", swap.Backup }, { "restored", swap.Restored ? "1" : "" }, { "failedDir", swap.FailedDir } });
            root["swaps"] = swaps;
            root["manifestPath"] = record.ManifestPath;
            root["manifestBackup"] = record.ManifestBackup;
            root["oldPackageId"] = record.OldPackageId;
            root["packageName"] = record.PackageName;
            root["error"] = record.Error;
            root["firstRun"] = record.FirstRun ? "1" : "";
            root["manual"] = record.Manual ? "1" : "";
            record.Updated = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            root["updated"] = record.Updated;
            AtomicFile.Write(RecordPath(projectRoot), JsonWriter.Write(root, "\n"));
        }

        /// <summary>Takes the swapped folders and the vpm-manifest.json copy from the install transaction's report into the record.</summary>
        public static void TakeSwaps(SelfUpdateRecord record, InstallReport report, string packageName, string projectRoot)
        {
            record.Swaps.Clear();
            foreach (var entry in report.Installed)
            {
                record.Swaps.Add(new SelfUpdateSwap { Name = entry.Name, Target = entry.Target, Backup = entry.Backup });
                if (entry.Name == packageName) record.Backup = entry.Backup;
            }
            if (report.ManifestUpdated && report.ManifestBackup.Length > 0)
            {
                record.ManifestBackup = report.ManifestBackup;
                record.ManifestPath = Path.Combine(projectRoot, "Packages", "vpm-manifest.json");
            }
        }

        // ------------------------------------------------------------ rollback of an embedded update

        /// <summary>
        /// Puts every folder the update swapped back, last swap first, then vpm-manifest.json. Each folder: its new contents go to
        /// "&lt;backup&gt;-failed" and the old ones come back from the backup; a folder the update created (no backup) is emptied into
        /// "-failed". A folder whose restore fails mid-way is put back the way it was before the restore started, and the error goes up.
        /// </summary>
        /// <param name="failedRoot">Where the new contents of a folder the update created go (outside Packages/, so Unity no longer sees
        /// that package): Library/OnAirDeck/Backup.</param>
        /// <param name="saveProgress">Called after each folder is back (<see cref="SelfUpdateSwap.Restored"/> set), so a cut rollback resumes
        /// after it instead of finding that folder's backup empty.</param>
        public static void RestoreAll(SelfUpdateRecord record, string failedRoot, Action saveProgress)
        {
            if (record.Swaps.Count == 0) throw new PackageInstallException("There is nothing recorded to put back.");
            for (var i = record.Swaps.Count - 1; i >= 0; i--)
            {
                var swap = record.Swaps[i];
                if (swap.Restored) continue;
                RestoreBackup(swap.Target, swap.Backup, failedRoot, swap, saveProgress);
                swap.Restored = true;
                if (saveProgress != null) saveProgress();
            }
            if (record.ManifestBackup.Length > 0 && File.Exists(record.ManifestBackup) && record.ManifestPath.Length > 0)
                AtomicFile.Write(record.ManifestPath, File.ReadAllText(record.ManifestBackup, Encoding.UTF8));
        }

        /// <summary>
        /// One folder back from its backup. Throws before anything moves when the folder is gone or a backup is named but missing or
        /// empty; a move that fails puts back what this call already moved, then rethrows.
        /// </summary>
        public static void RestoreBackup(string packageFolder, string backup, string failedRoot, SelfUpdateSwap swap = null, Action saveProgress = null)
        {
            var created = string.IsNullOrEmpty(backup);
            // Resumed after every new file left: only the rest of the backup comes in (the old files already back stay where they are).
            // A "-failed" folder that is empty again means a failed restore moved the new files back and could not clear the mark: the
            // folder is as it was before that restore, so the whole restore runs again.
            if (swap != null && swap.FailedDir.Length > 0 && Directory.Exists(swap.FailedDir) && Directory.GetFileSystemEntries(swap.FailedDir).Length > 0)
            {
                if (!created)
                {
                    if (!Directory.Exists(packageFolder)) Directory.CreateDirectory(packageFolder);
                    if (Directory.Exists(backup))
                        foreach (var child in Directory.GetFileSystemEntries(backup))
                        {
                            var to = Path.Combine(packageFolder, Path.GetFileName(child));
                            if (File.Exists(to) || Directory.Exists(to)) throw new PackageInstallException("Both the backup and the folder hold " + Path.GetFileName(child) + "; the rollback stops.");
                            PackageTransaction.MoveEntry(child, to);
                        }
                }
                else if (Directory.Exists(packageFolder))
                {
                    try { Directory.Delete(packageFolder); }
                    catch (IOException) { }
                }
                return;
            }
            // A folder the update created that is already gone needs nothing.
            if (created && (string.IsNullOrEmpty(packageFolder) || !Directory.Exists(packageFolder))) return;
            if (string.IsNullOrEmpty(packageFolder) || !Directory.Exists(packageFolder))
                throw new PackageInstallException("Cannot find the folder the Manager is installed in.");
            if (!created && (!Directory.Exists(backup) || Directory.GetFileSystemEntries(backup).Length == 0))
                throw new PackageInstallException("The backup of the previous Manager is gone; it cannot be put back.");
            if (created && string.IsNullOrEmpty(failedRoot))
                throw new PackageInstallException("There is no place outside Packages/ for the files of a package the update added.");
            var baseName = created ? Path.Combine(failedRoot, Path.GetFileName(packageFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) + "-new")
                                   : backup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var failed = baseName + "-failed";
            for (var n = 2; Directory.Exists(failed); n++) failed = baseName + "-failed-" + n.ToString(CultureInfo.InvariantCulture);
            Directory.CreateDirectory(failed);

            var done = new List<KeyValuePair<string, string>>();   // from, to (in the order moved)
            try
            {
                foreach (var child in Directory.GetFileSystemEntries(packageFolder))
                {
                    var to = Path.Combine(failed, Path.GetFileName(child));
                    PackageTransaction.MoveEntry(child, to);
                    done.Add(new KeyValuePair<string, string>(child, to));
                }
                if (swap != null)
                {
                    swap.FailedDir = failed;
                    if (saveProgress != null) saveProgress();
                }
                if (!created)
                    foreach (var child in Directory.GetFileSystemEntries(backup))
                    {
                        var to = Path.Combine(packageFolder, Path.GetFileName(child));
                        PackageTransaction.MoveEntry(child, to);
                        done.Add(new KeyValuePair<string, string>(child, to));
                    }
            }
            catch (Exception)
            {
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    try { PackageTransaction.MoveEntry(done[i].Value, done[i].Key); }
                    catch (Exception) { }
                }
                if (swap != null)
                {
                    swap.FailedDir = "";
                    try { if (saveProgress != null) saveProgress(); } catch (Exception) { }
                }
                throw;
            }
            // A package the update added leaves Packages/ entirely: an empty folder there would still be a folder Unity looks at.
            if (created)
            {
                try { Directory.Delete(packageFolder); }
                catch (IOException) { }
            }
        }
    }
}
