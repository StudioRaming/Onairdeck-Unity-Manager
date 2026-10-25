using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Window ▸ OnAirDeck Unity Manager — sign in, purchases, download into the project.</summary>
    internal sealed partial class ManagerWindow : EditorWindow
    {
        // Unity serializes an EditorWindow's private fields across script reloads. Everything that
        // describes in-flight work is marked [NonSerialized]: the awaiting code is gone after a
        // reload, so a restored "downloading" / "signing in" flag would leave the window stuck
        // with disabled buttons and no way out.
        private SavedSession _session;
        [NonSerialized] private bool _signingIn;
        [NonSerialized] private bool _signingOut;
        [NonSerialized] private string _signInUrl;
        [NonSerialized] private CancellationTokenSource _signInCancel;

        [NonSerialized] private PurchaseItem[] _purchases;
        [NonSerialized] private bool _loadingPurchases;
        private string _search = "";
        private Vector2 _scroll;

        [NonSerialized] private string _downloadingItemId;
        [NonSerialized] private string _progressLabel = "";
        [NonSerialized] private float _progress;
        [NonSerialized] private CancellationTokenSource _downloadCancel;

        private string _message;
        private MessageType _messageType = MessageType.None;

        private UpdateState _update;
        private UnityEditor.PackageManager.Requests.AddRequest _applyingUpdate;
        private bool _licensesRefreshed;

        private GUIStyle _purchaseTitleStyle;
        private GUIStyle _detailsStyle;

        [MenuItem("Window/OnAirDeck Unity Manager")]
        public static void Open()
        {
            var window = GetWindow<ManagerWindow>();
            window.titleContent = new GUIContent("OnAirDeck");
            window.minSize = new Vector2(380, 360);
            window.Show();
        }

        private void OnEnable()
        {
            _session = SessionStore.Load();
            // The runtime license check reads this marker as proof of a signed-in Manager.
            if (_session != null) SessionMarker.Write(_session.userId); else SessionMarker.Clear();
            _licensesRefreshed = false;
            _purchaseTitleStyle = null;
            _detailsStyle = null;
            OnEnablePackages();
            CheckForUpdate();
        }

        private void OnFocus()
        {
            // A package folder can change outside the window (VCC, another editor, a rollback).
            RefreshInstalledPackages();
        }

        private void OnDisable()
        {
            // Closing the window (or a script reload) abandons in-progress work.
            CancelSignIn();
            CancelDownload();
            CancelPackageInstall();
            EditorApplication.update -= PollApplyingUpdate;
        }

        private void OnGUI()
        {
            // Create styles once, after Unity has made the current editor skin available.
            if (_purchaseTitleStyle == null)
                _purchaseTitleStyle = new GUIStyle(EditorStyles.boldLabel) { wordWrap = true };
            if (_detailsStyle == null)
                _detailsStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("OnAirDeck Unity Manager", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Download the assets you bought on OnAirDeck.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            DrawUpdateBanner();

            if (_update != null && _update.MustUpdate)
            {
                // Below the server's minimum: the endpoints may refuse this version, so stop here.
                EditorGUILayout.HelpBox("This version of the Manager is no longer supported. Update to continue.", MessageType.Warning);
            }
            else if (_signingIn) DrawSigningIn();
            else if (_session != null) DrawSignedIn();
            else DrawSignedOut();

            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_message, _messageType);
            }

            if (_session == null || _signingIn) GUILayout.FlexibleSpace();
            if (ActionButton("Open onairdeck.com", true))
                Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
        }

        private static bool ActionButton(string label, bool expand = false)
        {
            var style = EditorStyles.miniButton;
            var height = Mathf.Max(24f, EditorGUIUtility.singleLineHeight + 6f);
            if (expand) return GUILayout.Button(label, style, GUILayout.Height(height));

            // Measure the same style that draws the text; fixed pixel widths clip longer states.
            var width = Mathf.Ceil(style.CalcSize(new GUIContent(label)).x) + 12f;
            return GUILayout.Button(label, style, GUILayout.Width(width), GUILayout.Height(height));
        }

        // ---------- self-update ----------

        private async void CheckForUpdate()
        {
            try
            {
                var state = await UpdateService.CheckOncePerSessionAsync();
                if (state != null) _update = state;
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Update check failed: " + e.Message);
            }
            Repaint();
        }

        private void DrawUpdateBanner()
        {
            if (_update == null || !_update.UpdateAvailable) return;
            var latest = _update.Response.latest;
            var text = "Version " + latest.version + " is available (you have " + _update.CurrentVersion + ").";
            if (!string.IsNullOrEmpty(latest.notes)) text += "\n" + latest.notes;
            EditorGUILayout.HelpBox(text, _update.MustUpdate ? MessageType.Warning : MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                var updating = _applyingUpdate != null || _applyingSelfUpdate;
                GUI.enabled = _update.CanApply && !updating && !_installingPackage;
                if (ActionButton(updating ? "Updating…" : "Update")) ApplyUpdate();
                GUI.enabled = true;
                if (!_update.CanApply && !string.IsNullOrEmpty(_update.ApplyBlockedReason))
                    GUILayout.Label(_update.ApplyBlockedReason, _detailsStyle, GUILayout.ExpandWidth(true));
            }
            EditorGUILayout.Space();
        }

        private void ApplyUpdate()
        {
            if (_update != null && _update.UseEmbedded)
            {
                ApplyEmbeddedUpdate();
                return;
            }
            _applyingUpdate = UpdateService.Apply(_update);
            if (_applyingUpdate == null) return;
            SetMessage("Updating the OnAirDeck Unity Manager… Unity will reload scripts when it finishes.", MessageType.Info);
            EditorApplication.update += PollApplyingUpdate;
        }

        private void PollApplyingUpdate()
        {
            if (_applyingUpdate == null || !_applyingUpdate.IsCompleted) return;
            EditorApplication.update -= PollApplyingUpdate;
            var request = _applyingUpdate;
            _applyingUpdate = null;
            if (request.Status == UnityEditor.PackageManager.StatusCode.Success)
                SetMessage("Updated to " + request.Result.version + ".", MessageType.Info);
            else
                SetMessage("Update failed: " + (request.Error != null ? request.Error.message : "unknown error"), MessageType.Error);
        }

        // ---------- sign in / out ----------

        private void DrawSignedOut()
        {
            EditorGUILayout.HelpBox("Sign in with your OnAirDeck account to see your purchases.", MessageType.Info);
            if (ActionButton("Sign in with browser", true)) SignIn();
        }

        private void DrawSigningIn()
        {
            EditorGUILayout.HelpBox(
                "Approve the request in your browser, then come back to Unity.\nWaiting up to 3 minutes…",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !string.IsNullOrEmpty(_signInUrl);
                if (ActionButton("Open sign-in page again")) Application.OpenURL(_signInUrl);
                GUI.enabled = true;
                if (ActionButton("Cancel")) CancelSignIn();
            }
        }

        private void DrawSignedIn()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var who = string.IsNullOrEmpty(_session.email) ? "your OnAirDeck account" : _session.email;
                GUILayout.Label("Signed in as " + who, _detailsStyle, GUILayout.ExpandWidth(true));
                GUI.enabled = !_signingOut && _downloadingItemId == null;
                if (ActionButton(_signingOut ? "Signing out…" : "Sign out")) SignOut();
                GUI.enabled = true;
            }

            EditorGUILayout.Space();
            DrawPackages();
            DrawPurchases();
        }

        private async void SignIn()
        {
            CancelSignIn();
            _signInCancel = new CancellationTokenSource();
            var cancel = _signInCancel.Token;
            _signingIn = true;
            _signInUrl = null;
            SetMessage(null, MessageType.None);

            try
            {
                var session = await AuthService.SignInAsync(url => { _signInUrl = url; Repaint(); }, cancel);
                _session = session;
                _purchases = null;
                _licenses = null;
                _licensesRefreshed = false;
                SessionMarker.Write(session.userId);
                SetMessage("Signed in.", MessageType.Info);
            }
            catch (OperationCanceledException)
            {
                SetMessage("Sign-in was cancelled.", MessageType.None);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sign-in failed: " + e.Message);
            }
            finally
            {
                _signingIn = false;
                _signInUrl = null;
                Repaint();
            }
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
            _signingOut = true;
            var session = _session;
            try
            {
                await AuthService.SignOutAsync(session);
                SetMessage("Signed out.", MessageType.Info);
            }
            finally
            {
                _session = null;
                _purchases = null;
                _licenses = null;
                _signingOut = false;
                SessionMarker.Clear();
                Repaint();
            }
        }

        private void OnSessionExpired(string message)
        {
            SessionStore.Clear();
            SessionMarker.Clear();
            _session = null;
            _purchases = null;
            _licenses = null;
            SetMessage(message, MessageType.Warning);
        }

        // ---------- purchases ----------

        private void DrawPurchases()
        {
            if (_purchases == null && !_loadingPurchases) LoadPurchases();

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Purchases", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(40));
                GUI.enabled = !_loadingPurchases && _downloadingItemId == null;
                if (ActionButton(_loadingPurchases ? "Loading…" : "Refresh")) LoadPurchases();
                GUI.enabled = true;
            }

            if (_downloadingItemId != null)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(rect, _progress, _progressLabel);
                if (ActionButton("Cancel download", true)) CancelDownload();
            }

            if (_purchases == null)
            {
                EditorGUILayout.HelpBox(_loadingPurchases ? "Loading your purchases…" : "Could not load purchases.", MessageType.None);
                return;
            }
            if (_purchases.Length == 0)
            {
                EditorGUILayout.HelpBox("No purchases yet. Assets you buy on OnAirDeck will appear here.", MessageType.Info);
                return;
            }

            var filter = (_search ?? "").Trim();
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var shown = 0;
            foreach (var item in _purchases)
            {
                if (filter.Length > 0 && (item.name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                shown++;
                DrawPurchaseRow(item);
            }
            if (shown == 0) EditorGUILayout.LabelField("No purchases match \"" + filter + "\".", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.EndScrollView();
        }

        private void DrawPurchaseRow(PurchaseItem item)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(item.name ?? "", _purchaseTitleStyle, GUILayout.ExpandWidth(true));

                var details = item.date ?? "";
                if (item.files != null && item.files.Length > 0)
                    details += "  ·  " + item.files.Length + " file" + (item.files.Length == 1 ? "" : "s");
                if (item.update_available) details += "  ·  Update available";
                else if (item.downloaded) details += "  ·  Downloaded";

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(details, _detailsStyle, GUILayout.ExpandWidth(true));
                    if (item.CanDownload)
                    {
                        var busy = _downloadingItemId != null;
                        var thisOne = _downloadingItemId == item.item_id;
                        GUI.enabled = !busy;
                        var label = thisOne ? "Downloading…" : item.downloaded ? "Download again" : "Download";
                        if (ActionButton(label)) Download(item);
                        GUI.enabled = true;
                    }
                    else
                    {
                        GUILayout.Label("Not available in Unity", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                    }
                }
            }
        }

        private async void LoadPurchases()
        {
            if (_session == null) return;
            _loadingPurchases = true;
            Repaint();
            try
            {
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(_session.token);
                _purchases = response.purchases;
                SetLicenses(response.licenses);
                RefreshLicenses();
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                _purchases = null;
                SetMessage(e.Message, MessageType.Error);
            }
            finally
            {
                _loadingPurchases = false;
                Repaint();
            }
        }

        /// <summary>Once per window open: re-issue licenses for items installed here that are missing one or expire soon.</summary>
        private async void RefreshLicenses()
        {
            if (_licensesRefreshed || _session == null || _purchases == null) return;
            _licensesRefreshed = true;
            try
            {
                var refreshed = await LicenseService.RefreshInstalledAsync(_session.token, _purchases);
                if (refreshed > 0) Debug.Log(OnAirDeckConfig.LogPrefix + "Refreshed " + refreshed + " license file" + (refreshed == 1 ? "" : "s") + ".");
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "License refresh failed: " + e.Message);
            }
        }

        // ---------- download ----------

        private async void Download(PurchaseItem item)
        {
            if (_session == null || _downloadingItemId != null) return;

            // The first download makes the purchase non-refundable (same rule as the website).
            if (!item.downloaded && !EditorUtility.DisplayDialog(
                    "Download " + item.name,
                    "Downloading makes this purchase non-refundable, the same as downloading on onairdeck.com.\n\nLoose files and ZIPs will be placed in " + Installer.ProductFolder(item.name) + ".\nUnity packages open Unity's import dialog and use the paths stored in the package.",
                    "Download", "Cancel"))
            {
                return;
            }

            CancelDownload();
            _downloadCancel = new CancellationTokenSource();
            var cancel = _downloadCancel.Token;
            _downloadingItemId = item.item_id;
            _progress = 0f;
            _progressLabel = "Preparing…";
            SetMessage(null, MessageType.None);

            try
            {
                var summary = await DownloadService.DownloadItemAsync(_session.token, item,
                    (name, p) => { _progressLabel = name + "  " + Mathf.RoundToInt(p * 100) + "%"; _progress = p; Repaint(); }, cancel);
                item.downloaded = true;
                item.update_available = false;
                SetMessage(item.name + ": " + summary, MessageType.Info);
            }
            catch (OperationCanceledException)
            {
                SetMessage("Download cancelled.", MessageType.None);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Download failed: " + e.Message);
            }
            finally
            {
                _downloadingItemId = null;
                if (_downloadCancel != null) { _downloadCancel.Dispose(); _downloadCancel = null; }
                Repaint();
            }
        }

        private void CancelDownload()
        {
            if (_downloadCancel == null) return;
            _downloadCancel.Cancel();
        }

        private void SetMessage(string text, MessageType type)
        {
            _message = text;
            _messageType = type;
            Repaint();
        }
    }
}
