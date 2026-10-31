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
    /// The Manager's self-update (owner decision 2026-10-31: it updates itself), for the automatic install and the Update button alike.
    /// One update at a time in the editor. The record is written before anything changes (<see cref="SelfUpdateRecord"/>): the new
    /// version reads it to show "업데이트됨 a → b", and this code, which Unity keeps loaded when the new code fails to compile, reads it
    /// to put the old version back.
    /// </summary>
    [InitializeOnLoad]
    internal static class AutoUpdater
    {
        private const string LastCheckKey = "OnAirDeck.UnityManager.AutoUpdate.LastCheck.";

        /// <summary>
        /// The version of the code that is loaded, read when this domain loaded. Package Manager's registration follows the folder
        /// (after a swap it already says the new version while this old code still runs), so it never tells which code runs.
        /// </summary>
        public static readonly string LoadedVersion;

        /// <summary>EditMode tests replace the install step; null runs the real one.</summary>
        internal static Func<UpdateState, SelfUpdateRecord, Task> ApplySeam;

        /// <summary>A window's own installs, batches and hand-offs (not its self-update), for the check right before the swap.</summary>
        internal static Func<bool> WindowWorking;

        private static int _running;

        static AutoUpdater()
        {
            LoadedVersion = UpdateService.CurrentVersion;
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompiled;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompiled;
            EditorApplication.delayCall += SettleAfterReload;
        }

        /// <summary>An update (automatic or clicked) is running in this editor.</summary>
        public static bool Running { get { return Volatile.Read(ref _running) != 0; } }

        private static string Root { get { return PackageInstallService.ProjectRoot; } }

        private static void SettleAfterReload()
        {
            try
            {
                var record = AutoUpdateLogic.Load(Root);
                if (record == null) return;
                if (record.Phase == SelfUpdateRecord.PhaseRestoring && record.From == LoadedVersion)
                {
                    // A rollback was cut (the editor quit or reloaded mid-way): finish it.
                    RollBack();
                    return;
                }
                if (AutoUpdateLogic.Settle(record, LoadedVersion)) AutoUpdateLogic.Save(Root, record);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not read the self-update record: " + e.Message);
            }
        }

        // ------------------------------------------------------------ timing and state

        public static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); }

        private static string LastCheckPref { get { return LastCheckKey + AutoUpdateLogic.StableHash(Root); } }

        public static long LastCheck
        {
            get { long v; return long.TryParse(EditorPrefs.GetString(LastCheckPref, "0"), out v) ? v : 0; }
            set { EditorPrefs.SetString(LastCheckPref, value.ToString()); }
        }

        /// <summary>The release no automatic install may take now (being installed or rolled back, or failed here), or "".</summary>
        public static string BlockedVersion()
        {
            return AutoUpdateLogic.BlockedVersion(AutoUpdateLogic.Load(Root));
        }

        /// <summary>The moment as an automatic update sees it: <paramref name="windowBusy"/> and any update already running count as busy.</summary>
        public static AutoUpdateMoment Moment(bool windowBusy)
        {
            var moment = EditorMoment(windowBusy);
            moment.WindowBusy = moment.WindowBusy || Running;
            return moment;
        }

        private static AutoUpdateMoment EditorMoment(bool windowBusy)
        {
            return new AutoUpdateMoment
            {
                WindowBusy = windowBusy,
                Compiling = EditorApplication.isCompiling || EditorApplication.isUpdating,
                Playing = EditorApplication.isPlayingOrWillChangePlaymode,
                ImportsQueued = PackageImportQueue.PendingCount > 0,
                ImportRequest = ImportRequestWatcher.Busy,
            };
        }

        /// <summary>Why right now, before the swap, is not safe (everything but this update itself), or null.</summary>
        private static string UnsafeNow()
        {
            var working = WindowWorking;
            return EditorMoment(working != null && working()).Reason();
        }

        // ------------------------------------------------------------ install

        /// <summary>
        /// Installs <paramref name="state"/>'s release. Returns null when it is in (a reload follows), "deferred" when the moment turned
        /// unsafe before anything changed (tried again later), or the error text. Refuses a release that is not newer than the running
        /// code, and a second update while one runs. A failure before the swap undoes itself in the install transaction and marks the
        /// release failed, so only a click tries it again.
        /// </summary>
        public static async Task<string> ApplyAsync(UpdateState state, bool firstRun = false, bool manual = false)
        {
            if (state == null || !state.CanApply || !state.HasLatest) return "This install cannot take the release by itself.";
            var to = state.Response.latest.version;
            if (!AutoUpdateLogic.IsNewer(to, LoadedVersion)) return "The release (" + to + ") is not newer than the running version (" + LoadedVersion + ").";
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return "Another update is running.";
            var record = new SelfUpdateRecord
            {
                From = LoadedVersion, To = to, Route = state.UseEmbedded ? "embedded" : "git", Phase = SelfUpdateRecord.PhaseApplying,
                PackageFolder = state.PackageFolder ?? "", OldPackageId = state.PackageId ?? "", FirstRun = firstRun, Manual = manual,
            };
            try
            {
                AutoUpdateLogic.Save(Root, record);
                var seam = ApplySeam;
                if (seam != null) await seam(state, record);
                else if (state.UseEmbedded)
                {
                    await PackageInstallService.SelfUpdateAsync(state.DownloadUrl, state.PackageName, to, state.PackageFolder, null, CancellationToken.None,
                        state.Response.latest.sha256, UnsafeNow,
                        report =>
                        {
                            // Before Package Manager and the asset database hear of it: a refresh may compile at once.
                            AutoUpdateLogic.TakeSwaps(record, report, state.PackageName, Root);
                            AutoUpdateLogic.Save(Root, record);
                        });
                }
                else
                {
                    var reason = UnsafeNow();
                    if (reason != null) throw new SelfUpdateDeferredException(reason);
                    var add = UpdateService.Apply(state);
                    if (add == null) throw new PackageInstallException("This install cannot switch to the release's git URL.");
                    while (!add.IsCompleted) await Task.Delay(200);
                    if (add.Status != StatusCode.Success) throw new PackageInstallException(add.Error != null ? add.Error.message : "Package Manager refused the update.");
                }
                return null;
            }
            catch (SelfUpdateDeferredException e)
            {
                record.Phase = SelfUpdateRecord.PhaseDeferred;
                record.Error = e.Message;
                try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
                return "deferred";
            }
            catch (Exception e)
            {
                record.Phase = SelfUpdateRecord.PhaseFailed;
                record.Error = e.Message;
                try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Update to " + to + " failed: " + e.Message);
                return e.Message;
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }
        }

        // ------------------------------------------------------------ rollback

        /// <summary>
        /// Runs in the version that started the update: a compile error in a file inside a folder the update swapped (any assembly there)
        /// means the new code cannot load; Unity keeps this code loaded, and it puts the old version back.
        /// </summary>
        private static void OnAssemblyCompiled(string assemblyPath, CompilerMessage[] messages)
        {
            if (messages == null || messages.Length == 0) return;
            var record = AutoUpdateLogic.Load(Root);
            if (record == null || record.Phase != SelfUpdateRecord.PhaseApplying || record.From != LoadedVersion) return;
            foreach (var message in messages)
            {
                if (message.type != CompilerMessageType.Error || !AutoUpdateLogic.ErrorInUpdate(record, Root, message.file)) continue;
                // After the compile ends: no file moves while Unity still reads the sources. The record is read again then.
                EditorApplication.delayCall -= RollBack;
                EditorApplication.delayCall += RollBack;
                return;
            }
        }

        internal static void RollBack()
        {
            var record = AutoUpdateLogic.Load(Root);
            if (record == null || (record.Phase != SelfUpdateRecord.PhaseApplying && record.Phase != SelfUpdateRecord.PhaseRestoring) || record.From != LoadedVersion) return;
            try
            {
                record.Phase = SelfUpdateRecord.PhaseRestoring;
                AutoUpdateLogic.Save(Root, record);
                if (record.Route == "embedded")
                {
                    AutoUpdateLogic.RestoreAll(record);
                    Finish(record, null);
                    AssetDatabase.Refresh();
                }
                else if (record.OldPackageId.Length > 0)
                {
                    var add = Client.Add(record.OldPackageId);
                    EditorApplication.CallbackFunction poll = null;
                    poll = () =>
                    {
                        if (!add.IsCompleted) return;
                        EditorApplication.update -= poll;
                        Finish(record, add.Status == StatusCode.Success ? null : (add.Error != null ? add.Error.message : "Package Manager refused it."));
                    };
                    EditorApplication.update += poll;
                }
                else throw new PackageInstallException("There is no earlier package id to go back to.");
            }
            catch (Exception e)
            {
                Finish(record, e.Message);
            }
        }

        private static void Finish(SelfUpdateRecord record, string error)
        {
            record.Phase = error == null ? SelfUpdateRecord.PhaseRolledBack : SelfUpdateRecord.PhaseFailed;
            record.Error = error == null ? "The new version did not compile in this Unity." : "Rollback failed: " + error;
            try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
            if (error != null)
                Debug.LogError(OnAirDeckConfig.LogPrefix + "The update to " + record.To + " did not compile and the previous version could not be put back: " + error);
        }

        // ------------------------------------------------------------ the note

        /// <summary>The record with a note to show once ("updated" or "rolled-back" in <paramref name="kind"/>), else null.</summary>
        public static SelfUpdateRecord PendingNote(out string kind)
        {
            var record = AutoUpdateLogic.Load(Root);
            // The window may open before the delayed settle ran. Only the new version may settle here: the old one opening a window
            // while its update still runs must not call that update failed.
            if (record != null && record.Phase == SelfUpdateRecord.PhaseApplying && record.To == LoadedVersion && AutoUpdateLogic.Settle(record, LoadedVersion))
            {
                try { AutoUpdateLogic.Save(Root, record); } catch (Exception) { }
            }
            kind = AutoUpdateLogic.NoteFor(record, LoadedVersion);
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
