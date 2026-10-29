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
            return OfferedPackage(entry) != null;
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
            using (new GUIEnabledScope(!Busy))
            {
                if (SRGui.Button(run, new GUIContent(ManagerText.BatchGet), SRButtonKind.Primary, SRButtonSize.Mini))
                    Defer(StartSelectedBatch);
            }
            if (SRGui.Button(clear, new GUIContent(ManagerText.ClearSel), SRButtonKind.Ghost, SRButtonSize.Mini))
                Defer(() => _selected.Clear());
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
            var keyOf = new Dictionary<string, string>();
            var versions = new List<LicenseVersionEntry>();
            foreach (var entry in picked)
            {
                if (entry.Kind == FavoriteKind.Purchase)
                {
                    var item = BatchActions.Download(token, entry.Purchase, cancel);
                    items.Add(item);
                    keyOf[item.Name] = entry.Key;
                }
                else
                {
                    var version = OfferedPackage(entry);
                    versions.Add(version);
                    keyOf[version.package_name + " " + version.package_version] = entry.Key;
                }
            }
            if (versions.Count > 0) items.AddRange(BatchActions.InstallAll(token, versions, _licenses, cancel, LegacyInstallDialog.ConfirmBatch));
            if (items.Count == 0) return;

            _batchKeys = new List<string>();
            foreach (var item in items)
            {
                string key;
                _batchKeys.Add(keyOf.TryGetValue(item.Name, out key) ? key : "");
            }
            _batch = new BatchQueue(items);
            _batch.Changed += Repaint;
            SetMessage(null, MessageType.None);
            try
            {
                await BatchActions.RunAsync(_batch);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Batch failed: " + e.Message);
            }
            finally
            {
                var queue = _batch;
                queue.Changed -= Repaint;
                if (queue.SessionExpired) OnSessionExpired("");
                else if (queue.FailedCount > 0)
                    SetMessage(ManagerText.BatchDoneFailed.Replace("{d}", queue.DoneCount.ToString()).Replace("{f}", queue.FailedCount.ToString()), MessageType.Warning);
                else
                    SetMessage(ManagerText.BatchDone.Replace("{d}", queue.DoneCount.ToString()), MessageType.Info);
                if (clearSelection) _selected.Clear();
                if (_batchCancel != null) { _batchCancel.Dispose(); _batchCancel = null; }
                _batch = null;
                RefreshInstalledState();
            }
        }
    }
}
