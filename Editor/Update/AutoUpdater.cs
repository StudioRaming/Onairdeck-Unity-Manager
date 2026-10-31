using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The automatic self-update (owner decision 2026-10-31). The window asks plugin-version when it opens and once a day while it
    /// stays open (<see cref="AutoUpdateLogic.CheckDue"/>); a newer release is installed at a safe moment by the same routes as the
    /// Update button (embedded: the release zip replaces the package folder in one transaction; git: Package Manager switches to the
    /// release's git URL). The record written first (<see cref="SelfUpdateRecord"/>) lets the new version show "업데이트됨 a → b"
    /// after the reload, and lets this (old) code, still loaded when the new code fails to compile, put the old version back.
    /// </summary>
    [InitializeOnLoad]
    internal static class AutoUpdater
    {
        private const string LastCheckKey = "OnAirDeck.UnityManager.AutoUpdate.LastCheck.";
        private const string ManagerAssembly = "OnAirDeck.UnityManager.Editor";
        private const string LicenseAssembly = "OnAirDeck.License";

        /// <summary>EditMode tests replace the install step; null runs the real one.</summary>
        internal static Func<UpdateState, SelfUpdateRecord, Task> ApplySeam;

        private static bool _running;

        static AutoUpdater()
        {
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.delayCall += SettleAfterReload;
        }

        public static bool Running { get { return _running; } }

        private static string Root { get { return PackageInstallService.ProjectRoot; } }

        /// <summary>
        /// After a reload the record is settled: the new version running means the install is done; the old one running means the install
        /// never took (the editor quit mid-way and the journal was rolled back, or the step was cut by a reload), so the release counts
        /// as failed and the rollback watch, armed only while "applying", cannot fire later on an unrelated compile error.
        /// </summary>
        private static void SettleAfterReload()
        {
            try
            {
                var record = AutoUpdateLogic.Load(Root);
                if (record == null || record.Phase != SelfUpdateRecord.PhaseApplying) return;
                var current = UpdateService.CurrentVersion;
                if (current == record.To) record.Phase = SelfUpdateRecord.PhaseApplied;
                else
                {
                    record.Phase = SelfUpdateRecord.PhaseFailed;
                    record.Error = "The install did not finish.";
                }
                AutoUpdateLogic.Save(Root, record);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not read the self-update record: " + e.Message);
            }
        }

        // ------------------------------------------------------------ timing

        public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        private static string LastCheckPref { get { return LastCheckKey + AutoUpdateLogic.StableHash(Root); } }

        public static long LastCheck
        {
            get { long v; return long.TryParse(EditorPrefs.GetString(LastCheckPref, "0"), out v) ? v : 0; }
            set { EditorPrefs.SetString(LastCheckPref, value.ToString()); }
        }

        /// <summary>The release this project failed to take automatically (only a click installs it again), or "".</summary>
        public static string FailedVersion()
        {
            var record = AutoUpdateLogic.Load(Root);
            return record != null && (record.Phase == SelfUpdateRecord.PhaseRolledBack || record.Phase == SelfUpdateRecord.PhaseFailed) ? record.To : "";
        }

        public static AutoUpdateMoment Moment(bool windowBusy)
        {
            return new AutoUpdateMoment
            {
                WindowBusy = windowBusy || _running,
                Compiling = EditorApplication.isCompiling || EditorApplication.isUpdating,
                Playing = EditorApplication.isPlayingOrWillChangePlaymode,
                ImportsQueued = PackageImportQueue.PendingCount > 0,
                ImportRequest = ImportRequestWatcher.Busy,
            };
        }

        // ------------------------------------------------------------ install

        /// <summary>
        /// Installs <paramref name="state"/>'s release without a click. Writes the record first; a failure before the swap (download,
        /// checks) undoes itself in the install transaction and marks the release failed, so it is not tried again automatically.
        /// Returns null when it started (the reload follows), or the error text.
        /// </summary>
        public static async Task<string> ApplyAsync(UpdateState state)
        {
            if (_running || state == null || !state.CanApply || !state.HasLatest) return "not available";
            _running = true;
            var record = new SelfUpdateRecord
            {
                From = state.CurrentVersion, To = state.Response.latest.version, Route = state.UseEmbedded ? "embedded" : "git",
                Phase = SelfUpdateRecord.PhaseApplying, PackageFolder = state.PackageFolder ?? "", OldPackageId = state.PackageId ?? "",
            };
            try
            {
                AutoUpdateLogic.Save(Root, record);
                var seam = ApplySeam;
                if (seam != null) await seam(state, record);
                else if (state.UseEmbedded)
                {
                    var report = await PackageInstallService.SelfUpdateAsync(state.DownloadUrl, state.PackageName, record.To, state.PackageFolder, null, CancellationToken.None);
                    foreach (var entry in report.Installed)
                        if (entry.Name == state.PackageName) record.Backup = entry.Backup;
                    AutoUpdateLogic.Save(Root, record);
                }
                else
                {
                    var add = UpdateService.Apply(state);
                    if (add == null) throw new PackageInstallException("This install cannot switch to the release's git URL.");
                    while (!add.IsCompleted) await Task.Delay(200);
                    if (add.Status != StatusCode.Success) throw new PackageInstallException(add.Error != null ? add.Error.message : "Package Manager refused the update.");
                }
                return null;
            }
            catch (Exception e)
            {
                record.Phase = SelfUpdateRecord.PhaseFailed;
                record.Error = e.Message;
                try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Automatic update to " + record.To + " failed: " + e.Message);
                return e.Message;
            }
            finally
            {
                _running = false;
            }
        }

        // ------------------------------------------------------------ rollback

        /// <summary>
        /// Runs in the version that started the update: when the new Manager code does not compile, Unity keeps this code loaded, and it
        /// puts the old version back (embedded: from the transaction's backup; git: the old package id).
        /// </summary>
        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(assemblyPath ?? "");
            if (name != ManagerAssembly && name != LicenseAssembly) return;
            var failed = false;
            foreach (var message in messages ?? new CompilerMessage[0]) if (message.type == CompilerMessageType.Error) { failed = true; break; }
            if (!failed) return;
            var record = AutoUpdateLogic.Load(Root);
            if (record == null || record.Phase != SelfUpdateRecord.PhaseApplying || record.From != UpdateService.CurrentVersion) return;
            // After the compile ends: no file moves while Unity still reads the sources.
            EditorApplication.delayCall += () => RollBack(record);
        }

        internal static void RollBack(SelfUpdateRecord record)
        {
            try
            {
                if (record.Route == "embedded") AutoUpdateLogic.RestoreBackup(record.PackageFolder, record.Backup);
                else if (record.OldPackageId.Length > 0) Client.Add(record.OldPackageId);
                else throw new PackageInstallException("There is no earlier package id to go back to.");
                record.Phase = SelfUpdateRecord.PhaseRolledBack;
                record.Error = "The new version did not compile in this Unity.";
                AutoUpdateLogic.Save(Root, record);
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                record.Phase = SelfUpdateRecord.PhaseFailed;
                record.Error = "Rollback failed: " + e.Message;
                try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
                Debug.LogError(OnAirDeckConfig.LogPrefix + "The automatic update to " + record.To + " did not compile and the previous version could not be put back: " + e.Message);
            }
        }

        // ------------------------------------------------------------ the note

        /// <summary>"updated" or "rolled-back" when the window has a note to show once, else null.</summary>
        public static SelfUpdateRecord PendingNote(out string kind)
        {
            kind = null;
            var record = AutoUpdateLogic.Load(Root);
            kind = AutoUpdateLogic.NoteFor(record, UpdateService.CurrentVersion);
            return kind != null ? record : null;
        }

        public static void NoteSeen(SelfUpdateRecord record)
        {
            if (record == null) return;
            // A shown update is done; a rolled-back release stays failed (no automatic retry) without showing its note again.
            record.Phase = record.Phase == SelfUpdateRecord.PhaseApplied ? SelfUpdateRecord.PhaseShown : SelfUpdateRecord.PhaseFailed;
            try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
        }
    }
}
