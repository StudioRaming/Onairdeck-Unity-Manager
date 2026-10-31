using System;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The window's half of the automatic self-update (<see cref="AutoUpdater"/>): asks on open and once a day while open, installs a
    /// newer release at a safe moment, and shows once what happened ("업데이트됨 a → b", or the rollback).
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private const double AutoTickSeconds = 5;
        [NonSerialized] private double _autoNextTick;
        [NonSerialized] private bool _checkingUpdate;
        [NonSerialized] private SelfUpdateRecord _autoNote;
        [NonSerialized] private string _autoNoteKind;

        private void OnEnableAutoUpdate()
        {
            _autoNote = AutoUpdater.PendingNote(out _autoNoteKind);
            EditorApplication.update -= AutoUpdateTick;
            EditorApplication.update += AutoUpdateTick;
        }

        private void OnDisableAutoUpdate()
        {
            EditorApplication.update -= AutoUpdateTick;
        }

        private void AutoUpdateTick()
        {
            if (EditorApplication.timeSinceStartup < _autoNextTick) return;
            _autoNextTick = EditorApplication.timeSinceStartup + AutoTickSeconds;
            if (!_checkingUpdate && AutoUpdateLogic.CheckDue(false, AutoUpdater.Now(), AutoUpdater.LastCheck))
            {
                CheckForUpdate(false);
                return;
            }
            TryAutoUpdate();
        }

        /// <summary>Installs the newer release by itself when this install can take it, it did not fail here before, and nothing is running.</summary>
        private async void TryAutoUpdate()
        {
            var state = _update;
            if (state == null || !state.HasLatest || AutoUpdater.Running) return;
            var action = AutoUpdateLogic.Decide(state.UpdateAvailable || state.MustUpdate, state.CanApply, state.Response.latest.version,
                AutoUpdater.FailedVersion(), AutoUpdater.Moment(Busy));
            if (action != AutoUpdateAction.Apply) return;
            _applyingSelfUpdate = true;
            SetMessage(ManagerText.AuRunning.Replace("{v}", state.Response.latest.version), MessageType.Info);
            Repaint();
            try
            {
                var error = await AutoUpdater.ApplyAsync(state);
                if (error != null) SetMessage(ManagerText.AuFailed.Replace("{v}", state.Response.latest.version).Replace("{m}", error), MessageType.Warning);
            }
            catch (Exception e)
            {
                SetMessage(ManagerText.AuFailed.Replace("{v}", state.Response.latest.version).Replace("{m}", e.Message), MessageType.Warning);
                _messageReport = ManagerErrorWatch.OfferCaught("Updating the Manager automatically", e);
            }
            finally
            {
                _applyingSelfUpdate = false;
                Repaint();
            }
        }

        /// <summary>The note of the last automatic update, shown once; 0 when there is none.</summary>
        private float DrawAutoUpdateNote(Rect body)
        {
            if (_autoNote == null || _autoNoteKind == null) return 0f;
            var close = new BarButton { Label = ManagerText.Close, Kind = SRButtonKind.Ghost, Enabled = true, Click = CloseAutoUpdateNote };
            if (_autoNoteKind == "updated")
                return DrawBar(body, SRTone.Brand, ManagerText.AuDone.Replace("{a}", _autoNote.From).Replace("{b}", _autoNote.To), close);
            return DrawBar(body, SRTone.Warning, ManagerText.AuBack.Replace("{a}", _autoNote.From).Replace("{b}", _autoNote.To), close);
        }

        private void CloseAutoUpdateNote()
        {
            AutoUpdater.NoteSeen(_autoNote);
            _autoNote = null;
            _autoNoteKind = null;
        }
    }
}
