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
        /// <summary>This release failed here before: only the Update button installs it.</summary>
        SkipFailed
    }

    /// <summary>What the editor is doing now; any of it makes this a bad moment to replace the Manager.</summary>
    internal struct AutoUpdateMoment
    {
        public bool WindowBusy;       // an install, batch, self-update or hand-off of the window
        public bool Compiling;        // EditorApplication.isCompiling or isUpdating
        public bool Playing;          // play mode, or entering it
        public bool ImportsQueued;    // PackageImportQueue has .unitypackage imports to run
        public bool ImportRequest;    // the one-button import holds a request

        public bool IsSafe { get { return !WindowBusy && !Compiling && !Playing && !ImportsQueued && !ImportRequest; } }
    }

    /// <summary>
    /// The automatic self-update (owner decision 2026-10-31: the Managers update themselves). This record, written before an
    /// automatic install starts, lives in Library/OnAirDeck/self-update.json: the version that runs after the next script reload
    /// reads it to show "업데이트됨 a → b", and the version still loaded when the new code fails to compile reads it to put the old
    /// folder back. No UnityEngine here.
    /// </summary>
    internal sealed class SelfUpdateRecord
    {
        public const string PhaseApplying = "applying";
        public const string PhaseApplied = "applied";
        public const string PhaseRolledBack = "rolled-back";
        /// <summary>Failed before the swap, or a rolled-back release whose note was seen: only a click installs it again.</summary>
        public const string PhaseFailed = "failed";
        public const string PhaseShown = "shown";

        public string From = "";
        public string To = "";
        /// <summary>"embedded" (folder swap from the release zip) or "git" (Package Manager switched to the release's git URL).</summary>
        public string Route = "";
        public string Phase = "";
        public string PackageFolder = "";
        /// <summary>Embedded: where the old folder's contents went (the install transaction's backup).</summary>
        public string Backup = "";
        /// <summary>Git: the package id to add back (name@url#tag) when the new version does not compile.</summary>
        public string OldPackageId = "";
        public string Error = "";
        public string Updated = "";
    }

    internal static class AutoUpdateLogic
    {
        public const string RecordName = "self-update.json";
        /// <summary>A window opened again within this time does not ask the server again.</summary>
        public const long OpenRecheckSeconds = 10 * 60;
        /// <summary>While a window stays open, the server is asked again after this long.</summary>
        public const long DailySeconds = 24 * 60 * 60;

        /// <summary>True when the window should ask plugin-version now: on open unless asked within 10 minutes, else once a day.</summary>
        public static bool CheckDue(bool onOpen, long now, long lastCheck)
        {
            if (lastCheck <= 0 || now < lastCheck) return true;
            return now - lastCheck >= (onOpen ? OpenRecheckSeconds : DailySeconds);
        }

        /// <summary>
        /// What to do with the last answer. A release that failed here before is installed again only by a click (a broken build must
        /// not loop); a required update (must_update) is the same rule, the blocking screen offers the click.
        /// </summary>
        public static AutoUpdateAction Decide(bool updateAvailable, bool canApply, string latestVersion, string failedVersion, AutoUpdateMoment moment)
        {
            if (!updateAvailable || !canApply || string.IsNullOrEmpty(latestVersion)) return AutoUpdateAction.None;
            if (!string.IsNullOrEmpty(failedVersion) && failedVersion == latestVersion) return AutoUpdateAction.SkipFailed;
            return moment.IsSafe ? AutoUpdateAction.Apply : AutoUpdateAction.Wait;
        }

        /// <summary>The note the new version shows once after the reload; null when there is none to show for <paramref name="current"/>.</summary>
        public static string NoteFor(SelfUpdateRecord record, string current)
        {
            if (record == null) return null;
            if (record.Phase == SelfUpdateRecord.PhaseApplied && record.To == current) return "updated";
            if (record.Phase == SelfUpdateRecord.PhaseRolledBack && record.From == current) return "rolled-back";
            return null;
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

        public static SelfUpdateRecord Load(string projectRoot)
        {
            try
            {
                var path = RecordPath(projectRoot);
                if (!File.Exists(path)) return null;
                var root = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                if (root == null) return null;
                return new SelfUpdateRecord
                {
                    From = MiniJson.GetString(root, "from") ?? "",
                    To = MiniJson.GetString(root, "to") ?? "",
                    Route = MiniJson.GetString(root, "route") ?? "",
                    Phase = MiniJson.GetString(root, "phase") ?? "",
                    PackageFolder = MiniJson.GetString(root, "packageFolder") ?? "",
                    Backup = MiniJson.GetString(root, "backup") ?? "",
                    OldPackageId = MiniJson.GetString(root, "oldPackageId") ?? "",
                    Error = MiniJson.GetString(root, "error") ?? "",
                    Updated = MiniJson.GetString(root, "updated") ?? "",
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void Save(string projectRoot, SelfUpdateRecord record)
        {
            var root = new Dictionary<string, object>();
            root["format"] = 1L;
            root["from"] = record.From;
            root["to"] = record.To;
            root["route"] = record.Route;
            root["phase"] = record.Phase;
            root["packageFolder"] = record.PackageFolder;
            root["backup"] = record.Backup;
            root["oldPackageId"] = record.OldPackageId;
            root["error"] = record.Error;
            record.Updated = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
            root["updated"] = record.Updated;
            AtomicFile.Write(RecordPath(projectRoot), JsonWriter.Write(root, "\n"));
        }

        // ------------------------------------------------------------ rollback of an embedded update

        /// <summary>
        /// Puts the old Manager back after an embedded update whose code does not compile: the new contents of
        /// <paramref name="packageFolder"/> go to "&lt;backup&gt;-failed" and the old ones come back from <paramref name="backup"/>. The
        /// folder itself stays (Unity keeps watching an embedded package folder; see PackageTransaction). Throws when the backup is
        /// missing or empty, before anything moves.
        /// </summary>
        public static void RestoreBackup(string packageFolder, string backup)
        {
            if (string.IsNullOrEmpty(backup) || !Directory.Exists(backup) || Directory.GetFileSystemEntries(backup).Length == 0)
                throw new PackageInstallException("The backup of the previous Manager is gone; it cannot be put back.");
            if (string.IsNullOrEmpty(packageFolder) || !Directory.Exists(packageFolder))
                throw new PackageInstallException("Cannot find the folder the Manager is installed in.");
            var failed = backup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-failed";
            for (var n = 2; Directory.Exists(failed); n++) failed = backup.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + "-failed-" + n.ToString(CultureInfo.InvariantCulture);
            Directory.CreateDirectory(failed);
            foreach (var child in Directory.GetFileSystemEntries(packageFolder))
                PackageTransaction.MoveEntry(child, Path.Combine(failed, Path.GetFileName(child)));
            foreach (var child in Directory.GetFileSystemEntries(backup))
                PackageTransaction.MoveEntry(child, Path.Combine(packageFolder, Path.GetFileName(child)));
        }
    }
}
