using System;
using System.Collections.Generic;
using System.Threading;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Multi-select in the list, the batch bar (selected / running), and the batch runs (selection and "update all").</summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private HashSet<string> _selected = new HashSet<string>();
        [NonSerialized] private BatchQueue _batch;
        [NonSerialized] private List<string> _batchKeys = new List<string>();
        [NonSerialized] private CancellationTokenSource _batchCancel;

        /// <summary>A product a batch can handle: a downloadable purchase or a license with an installable package.</summary>
        private static bool CanBatch(CatalogEntry entry)
        {
            if (entry.Kind == FavoriteKind.Purchase) return entry.Purchase != null && entry.Purchase.CanDownload;
            if (OfferedPackage(entry) != null) return true;
            // A file add-on of a core (Ming Light Controller): selectable with its core, so one run installs the core, then the add-on.
            return entry.License != null && CoreAddons.CoreOf(entry).Length > 0 && OfferedFileVersion(entry) != null;
        }

        private float BatchBarHeightNow()
        {
            if (_batch != null && _batch.IsRunning) return ManagerLayout.BatchBarRunningHeight;
            return _selected.Count > 0 ? ManagerLayout.BatchBarHeight : 0f;
        }

        private void DrawBatchBar(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                SRTheme.Fill(r, SRTheme.P.SurfaceSection);
                SRTheme.Fill(new Rect(r.x, r.y, r.width, 1f), SRTheme.P.LineSubtle);
            }
            var pad = SRMetrics.Space8;
            if (_batch != null && _batch.IsRunning)
            {
                var label = ManagerText.BatchRunning.Replace("{p}", _batch.ProgressText);
                var bar = new Rect(r.x + pad, r.y + pad, r.width - 2f * pad, SRMetrics.Line);
                GUILayout.BeginArea(bar);
                SRGui.ProgressBar(_batch.CurrentProgress, label, SRTone.Info);
                GUILayout.EndArea();
                var cancel = new Rect(r.xMax - pad - 80f, bar.yMax + SRMetrics.Space4, 80f, SRMetrics.ButtonMini);
                if (SRGui.Button(cancel, new GUIContent(ManagerText.Cancel), SRButtonKind.Ghost, SRButtonSize.Mini))
                    Defer(() => { if (_batch != null) _batch.Cancel(); });
                return;
            }

            var clear = new Rect(r.xMax - pad - 90f, r.y + (r.height - SRMetrics.ButtonMini) * 0.5f, 90f, SRMetrics.ButtonMini);
            var run = new Rect(clear.x - SRMetrics.Space6 - 110f, clear.y, 110f, SRMetrics.ButtonMini);
            var text = new Rect(r.x + pad, r.y, Mathf.Max(0f, run.x - r.x - 2f * pad), r.height);
            SRGui.Ink(text, SRGui.Fit(ManagerText.SelectedN.Replace("{n}", _selected.Count.ToString()), SRStyles.Strong, text.width), SRStyles.Strong);
            // After a batch with failures only the failed items stay selected: the button says it retries them.
            var retry = _retryKeys.Count > 0 && _retryKeys.SetEquals(_selected);
            var runLabel = retry ? ManagerText.RetryFailed.Replace("{n}", _selected.Count.ToString()) : ManagerText.BatchGet;
            using (new GUIEnabledScope(!Busy))
            {
                if (SRGui.Button(run, new GUIContent(SRGui.Fit(runLabel, SRStyles.Small, run.width - 8f), runLabel), SRButtonKind.Primary, SRButtonSize.Mini))
                    Defer(StartSelectedBatch);
            }
            if (SRGui.Button(clear, new GUIContent(ManagerText.ClearSel), SRButtonKind.Ghost, SRButtonSize.Mini))
                Defer(() => { _selected.Clear(); _retryKeys.Clear(); });
        }

        private void StartSelectedBatch()
        {
            if (_session == null || Busy || _selected.Count == 0) return;
            var picked = new List<CatalogEntry>();
            foreach (var entry in _visible) if (_selected.Contains(entry.Key) && CanBatch(entry)) picked.Add(entry);
            if (picked.Count == 0) return;
            if (!EditorUtility.DisplayDialog(ManagerText.DlgBatchT, ManagerText.DlgBatch.Replace("{n}", picked.Count.ToString()), ManagerText.BatchGet, ManagerText.Cancel)) return;
            RunBatch(picked, true);
        }

        /// <summary>Every product that has a newer version (the "update all" button of the update bar).</summary>
        private void StartUpdateAll()
        {
            if (_session == null || Busy || _entries == null) return;
            var picked = new List<CatalogEntry>();
            foreach (var entry in _entries) if (entry.State == EntryState.New && CanBatch(entry)) picked.Add(entry);
            if (picked.Count == 0) return;
            if (!EditorUtility.DisplayDialog(ManagerText.DlgBatchT, ManagerText.DlgUpdAll.Replace("{n}", picked.Count.ToString()), ManagerText.UpdAll, ManagerText.Cancel)) return;
            RunBatch(picked, false);
        }

        private async void RunBatch(List<CatalogEntry> picked, bool clearSelection)
        {
            var token = _session.token;
            _batchCancel = new CancellationTokenSource();
            var cancel = _batchCancel.Token;
            var items = new List<BatchItem>();
            var fileItems = new List<BatchItem>();
            var keyOf = new Dictionary<string, string>();
            var nameOf = new Dictionary<string, string>();
            var versions = new List<LicenseVersionEntry>();
            var waiting = new List<PendingAddon>();
            // MingToon Core before its add-ons (owner rule 2026-11-01); everything else keeps the list order.
            picked = CoreAddons.OrderCoreFirst(picked, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId);
            foreach (var entry in picked)
            {
                if (entry.Kind == FavoriteKind.Purchase)
                {
                    var item = BatchActions.Download(token, entry.Purchase, cancel);
                    items.Add(item);
                    keyOf[item.Name] = entry.Key;
                    nameOf[item.Name] = entry.Name;
                    continue;
                }
                var version = OfferedPackage(entry);
                if (version != null)
                {
                    versions.Add(version);
                    keyOf[version.package_name + " " + version.package_version] = entry.Key;
                    nameOf[version.package_name + " " + version.package_version] = entry.Name;
                    continue;
                }
                // A file add-on of a core (MLC): after every package, so the core of this run is in when it starts.
                var file = OfferedFileVersion(entry);
                if (file == null || entry.License == null) continue;
                var fileItem = BatchActions.InstallFile(token, entry.License, file, _licenses, cancel);
                fileItems.Add(fileItem);
                keyOf[fileItem.Name] = entry.Key;
                nameOf[fileItem.Name] = entry.Name;
                waiting.Add(new PendingAddon { AddonKey = entry.Key, LicenseId = entry.LicenseId, Core = CoreAddons.CoreOf(entry), Name = entry.Name, SinceUnix = NowUnix() });
            }
            // MingToon from VCC or the BOOTH installer: one question for the whole batch (undone below when none of them installed).
            VpmMove vccMove = null;
            if (versions.Count > 0 && !SwitchFromVcc(versions.ConvertAll(v => v.package_name), out vccMove))
            {
                _batchCancel.Dispose();
                _batchCancel = null;
                return;
            }
            if (versions.Count > 0) items.AddRange(BatchActions.InstallAll(token, versions, _licenses, cancel, LegacyInstallDialog.ConfirmBatch));
            items.AddRange(fileItems);
            if (items.Count == 0)
            {
                UndoSwitch(vccMove);
                return;
            }

            _batchKeys = new List<string>();
            var names = new List<string>();
            foreach (var item in items)
            {
                string key, name;
                _batchKeys.Add(keyOf.TryGetValue(item.Name, out key) ? key : "");
                names.Add(nameOf.TryGetValue(item.Name, out name) ? name : item.Name);
            }
            // The file add-ons wait for their core in SessionState too: when a reload ends this run before they ran, the window installs
            // them once the core is in (ResumePendingAddons). This run removes them when it ends.
            foreach (var addon in waiting) { _addonLive.Add(addon.AddonKey); SavePendingAddon(addon); }
            SessionState.EraseString(BatchRowsKey);
            _batch = new BatchQueue(items);
            _batch.Changed += Repaint;
            SetMessage(null, MessageType.None);
            var finished = false;
            // Runs while reloads are still held (the code after the await may never run: the reload can come first).
            Action finish = () =>
            {
                if (finished) return;
                finished = true;
                FinishBatch(_batch, items, names, vccMove, clearSelection, waiting);
            };
            try
            {
                await BatchActions.RunAsync(_batch, finish);
            }
            catch (Exception e)
            {
                FailedInstall("com.studioraming.manager", "Atelier Manager", UpdateService.CurrentVersion, e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Batch failed: " + e.Message);
            }
            finally
            {
                finish();
            }
        }

        /// <summary>
        /// After a batch: each item finished, or why not and what to do next ("0 finished, 2 failed" alone told nothing); the failed items
        /// stay selected for a retry; the VCC switch is kept or undone. Row reasons and the retry selection also go to SessionState, because
        /// the reload that follows a batch clears the window's dictionaries.
        /// </summary>
        private void FinishBatch(BatchQueue queue, List<BatchItem> items, List<string> names, VpmMove vccMove, bool clearSelection, List<PendingAddon> waiting)
        {
            if (queue == null) return;
            queue.Changed -= Repaint;
            foreach (var addon in waiting) { _addonLive.Remove(addon.AddonKey); RemovePendingAddon(addon.AddonKey); }
            ReportBatchFailures(queue, _batchKeys);
            // Something it moved got the Manager's copy: the switch is final. Nothing did: the entries go back.
            if (vccMove != null)
            {
                if (AnyDone(queue, items, vccMove.Packages)) KeepSwitch(vccMove);
                else UndoSwitch(vccMove);
            }
            var lines = InstallResultText.Lines(ManagerText.Language, queue.Results, names);
            var failedKeys = new HashSet<string>();
            var carried = new List<CarriedRow>();
            for (var i = 0; i < queue.Results.Count; i++)
            {
                var key = i < _batchKeys.Count ? _batchKeys[i] : "";
                if (key.Length == 0) continue;
                var result = queue.Results[i];
                if (result.Status == BatchStatus.Done) { _lastFailure.Remove(key); _cleanupRequired.Remove(key); continue; }
                if (result.Status != BatchStatus.Failed) continue;
                failedKeys.Add(key);
                if (result.Error is LegacyCleanupRequiredException)
                {
                    _cleanupRequired[key] = result.Error.Message;
                    var manual = ((LegacyCleanupRequiredException)result.Error).ManualFolder;
                    if (manual.Length > 0) _manualFolder[key] = manual;
                    else _manualFolder.Remove(key);
                    carried.Add(new CarriedRow { Key = key, Cleanup = true, Text = result.Error.Message });
                }
                else
                {
                    RememberFailure(key, result.Error);
                    string text;
                    if (_lastFailure.TryGetValue(key, out text)) carried.Add(new CarriedRow { Key = key, Text = text });
                }
            }
            if (queue.SessionExpired) OnSessionExpired("");
            else SetMessage(InstallResultText.Summary(ManagerText.Language, lines), queue.Results.Count == queue.DoneCount ? MessageType.Info : MessageType.Warning);
            foreach (var line in lines)
                if (line.Status == BatchStatus.Failed) Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Batch item " + line.Name + " failed: " + line.Reason);
            // A second press retries only the failed items: they stay selected, the rest is cleared.
            if (clearSelection || failedKeys.Count > 0) _selected.Clear();
            _retryKeys = new HashSet<string>();
            foreach (var key in failedKeys)
            {
                var entry = _entries != null ? _entries.Find(x => x.Key == key) : null;
                if (entry != null && CanBatch(entry)) { _selected.Add(key); _retryKeys.Add(key); }
            }
            if (carried.Count > 0 || _retryKeys.Count > 0) SessionState.SetString(BatchRowsKey, ReloadCarry.FormatRows(NowUnix(), _retryKeys, carried));
            if (_batchCancel != null) { _batchCancel.Dispose(); _batchCancel = null; }
            _batch = null;
            RefreshInstalledState();
        }

        /// <summary>True when an item installing one of <paramref name="packages"/> finished (item names are "&lt;package&gt; &lt;version&gt;").</summary>
        private static bool AnyDone(BatchQueue queue, List<BatchItem> items, List<string> packages)
        {
            for (var i = 0; i < queue.Results.Count && i < items.Count; i++)
            {
                if (queue.Results[i].Status != BatchStatus.Done) continue;
                foreach (var package in packages)
                    if (BatchQueue.ItemIsPackage(items[i].Name, package)) return true;
            }
            return false;
        }
    }
}
