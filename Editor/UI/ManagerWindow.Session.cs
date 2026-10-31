using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Sign in and out, loading the product list, the Manager's own update.</summary>
    internal sealed partial class ManagerWindow
    {
        // ---------- sign in / out ----------

        private async void SignIn()
        {
            CancelSignIn();
            _signInCancel = new CancellationTokenSource();
            var cancel = _signInCancel.Token;
            _signingIn = true;
            _signInUrl = null;
            _timeoutText = null;
            _signInStarted = EditorApplication.timeSinceStartup;
            _signInLastTick = _signInStarted;
            EditorApplication.update -= SignInTick;
            EditorApplication.update += SignInTick;
            SetMessage(null, MessageType.None);
            try
            {
                var session = await AuthService.SignInAsync(url => { _signInUrl = url; Repaint(); }, cancel);
                _session = session;
                _purchases = null;
                _licenses = null;
                _entries = null;
                ClearDevices();
                _loadFailed = false;
                _licensesRefreshed = false;
                SessionMarker.Write(session.userId);
                _entriesDirty = true;
            }
            catch (OperationCanceledException)
            {
                // The person pressed cancel: back to the welcome screen without a message.
            }
            catch (TimeoutException)
            {
                _timeoutText = ManagerText.ToBody;
            }
            catch (Exception e)
            {
                _timeoutText = null;
                SetMessage(e.Message, MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Sign-in", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sign-in failed: " + e.Message);
            }
            finally
            {
                _signingIn = false;
                _signInUrl = null;
                EditorApplication.update -= SignInTick;
                Repaint();
            }
        }

        /// <summary>Once a second while the browser sign-in waits, so the "time left" line stays true. Not an animation.</summary>
        private void SignInTick()
        {
            var now = EditorApplication.timeSinceStartup;
            if (now - _signInLastTick < 1.0) return;
            _signInLastTick = now;
            Repaint();
        }

        private int SignInSecondsLeft()
        {
            var total = AuthService.SignInTimeoutMs / 1000;
            var left = total - (int)(EditorApplication.timeSinceStartup - _signInStarted);
            return Mathf.Clamp(left, 0, total);
        }

        private void CancelSignIn()
        {
            if (_signInCancel == null) return;
            _signInCancel.Cancel();
            _signInCancel.Dispose();
            _signInCancel = null;
        }

        private async void SignOut()
        {
            if (_session == null || _signingOut) return;
            _signingOut = true;
            var session = _session;
            try
            {
                await AuthService.SignOutAsync(session);
            }
            catch (Exception e)
            {
                ManagerErrorWatch.OfferCaught("Sign-out", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sign-out failed: " + e.Message);
            }
            finally
            {
                ClearSessionState();
                _signingOut = false;
                Repaint();
            }
        }

        private void ClearSessionState()
        {
            _session = null;
            _purchases = null;
            _licenses = null;
            _entries = null;
            ClearDevices();
            _visible = new List<CatalogEntry>();
            _lines = new List<ListLine>();
            _selectedKey = "";
            _view = (int)View.Main;
            _showDetail = false;
            SessionMarker.Clear();
        }

        private void OnSessionExpired(string message)
        {
            SessionStore.Clear();
            ClearSessionState();
            _timeoutText = string.IsNullOrEmpty(message) ? ManagerText.ToBody : message;
            Repaint();
        }

        // ---------- the list ----------

        /// <summary>
        /// Loads purchases and licenses. <paramref name="userInitiated"/> is true for a click on Refresh: its license renewal may take
        /// a released seat back (X-Atelier-Reactivate). The automatic load when the window opens is background work and never does.
        /// </summary>
        private async void LoadEntries(bool userInitiated)
        {
            if (_session == null || _loading) return;
            _loading = true;
            _loadFailed = false;
            Repaint();
            var token = _session.token;
            var userId = _session.userId;
            try
            {
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(token);
                if (_session == null || _session.token != token) return;
                ApplyResponse(response);
                PurchaseWatcher.Observe(userId, response);
                UpdateCheckService.Evaluate(response);
                await RefreshLicenseFiles(userInitiated);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                if (_entries == null) { _loadFailed = true; _loadError = e.Message; }
                else SetMessage(e.Message, MessageType.Error);
                var caught = ManagerErrorWatch.OfferCaught("Loading the product list", e);
                if (_entries != null) _messageReport = caught;
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Loading the product list failed: " + e.Message);
            }
            finally
            {
                _loading = false;
                Repaint();
            }
        }

        /// <summary>Keeps onairdeck.license files fresh for what is installed here. Once per window open in the background; every Refresh click as a user action.</summary>
        private async Task RefreshLicenseFiles(bool userInitiated)
        {
            if (_session == null || _purchases == null) return;
            if (!userInitiated && _licensesRefreshed) return;
            _licensesRefreshed = true;
            var token = _session.token;
            try
            {
                var refreshed = await LicenseService.RefreshInstalledAsync(token, _purchases, userInitiated);
                refreshed += await LicenseService.EnsureLicenseFilesAsync(token, _licenses, userInitiated);
                if (refreshed > 0) Debug.Log(OnAirDeckConfig.LogPrefix + "Refreshed " + refreshed + " license file" + (refreshed == 1 ? "" : "s") + ".");
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                if (!IsNoticeRefusal(e)) ManagerErrorWatch.OfferCaught("Refreshing license files", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "License refresh failed: " + e.Message);
            }
        }

        private void RefreshClicked()
        {
            if (_session == null || _loading || Busy) return;
            // A click on Refresh also re-reads what is installed, so the chips never show a stale state.
            RefreshInstalledState();
            LoadEntries(true);
            StartConflictScan();
        }

        // ---------- the Manager's own update ----------

        /// <summary>
        /// Asks plugin-version when the window opens (not again within 10 minutes) and once a day while it stays open (the session's
        /// answer is reused in between), then lets the automatic update decide.
        /// </summary>
        private async void CheckForUpdate(bool onOpen)
        {
            if (_checkingUpdate) return;
            _checkingUpdate = true;
            try
            {
                UpdateState state;
                var now = AutoUpdater.Now();
                if (AutoUpdateLogic.CheckDue(onOpen, now, AutoUpdater.LastCheck))
                {
                    state = await UpdateService.CheckAsync();
                    if (state != null && state.Response != null) AutoUpdater.LastCheck = now;
                    else _nextCheckAttempt = now + AutoUpdateLogic.RetrySeconds;   // offline or a server error: not again on every tick
                }
                else state = await UpdateService.CheckOncePerSessionAsync();
                if (state != null && (state.Response != null || _update == null)) _update = state;
            }
            catch (Exception e)
            {
                ManagerErrorWatch.OfferCaught("Checking for a Manager update", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Update check failed: " + e.Message);
            }
            finally
            {
                _checkingUpdate = false;
            }
            TryAutoUpdate();
            Repaint();
        }

        /// <summary>
        /// The Update button and the required-update screen: the same guarded path as the automatic update (one update at a time, the
        /// record first, the rollback when the new code does not compile), and it may retry a release that failed here before.
        /// </summary>
        private async void ApplyUpdate()
        {
            var state = _update;
            if (state == null || !state.HasLatest || AutoUpdater.Running || Busy) return;
            _applyingSelfUpdate = true;
            SetMessage(ManagerText.SelfUpdating, MessageType.Info);
            Repaint();
            try
            {
                var error = await AutoUpdater.ApplyAsync(state, false, true);
                if (error == "deferred") SetMessage(ManagerText.AuWait, MessageType.Info);
                else if (error != null) SetMessage(ManagerText.SelfUpdateFailed.Replace("{m}", error), MessageType.Error);
                else SetMessage(ManagerText.SelfUpdated.Replace("{v}", state.Response.latest.version), MessageType.Info);
            }
            catch (Exception e)
            {
                SetMessage(ManagerText.SelfUpdateFailed.Replace("{m}", e.Message), MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Updating the Manager", e);
            }
            finally
            {
                _applyingSelfUpdate = false;
                Repaint();
            }
        }
    }
}
