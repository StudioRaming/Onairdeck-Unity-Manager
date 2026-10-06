using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Window ▸ OnAirDeck Unity Manager — sign in, purchases, download into the project.</summary>
    internal sealed class ManagerWindow : EditorWindow
    {
        private SavedSession _session;
        private bool _signingIn;
        private bool _signingOut;
        private string _signInUrl;
        private CancellationTokenSource _signInCancel;

        private PurchaseItem[] _purchases;
        private bool _loadingPurchases;
        private string _search = "";
        private Vector2 _scroll;

        private string _downloadingItemId;
        private string _progressLabel = "";
        private float _progress;
        private CancellationTokenSource _downloadCancel;

        private string _message;
        private MessageType _messageType = MessageType.None;

        private static readonly GUILayoutOption RowButtonWidth = GUILayout.Width(92);

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
        }

        private void OnDisable()
        {
            // Closing the window (or a script reload) abandons in-progress work.
            CancelSignIn();
            CancelDownload();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("OnAirDeck Unity Manager", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Download the assets you bought on OnAirDeck.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            if (_signingIn) DrawSigningIn();
            else if (_session != null) DrawSignedIn();
            else DrawSignedOut();

            if (!string.IsNullOrEmpty(_message))
            {
                EditorGUILayout.Space();
                EditorGUILayout.HelpBox(_message, _messageType);
            }

            if (_session == null || _signingIn) GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open onairdeck.com", EditorStyles.miniButton))
                Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
        }

        // ---------- sign in / out ----------

        private void DrawSignedOut()
        {
            EditorGUILayout.HelpBox("Sign in with your OnAirDeck account to see your purchases.", MessageType.Info);
            if (GUILayout.Button("Sign in with browser", GUILayout.Height(28))) SignIn();
        }

        private void DrawSigningIn()
        {
            EditorGUILayout.HelpBox(
                "Approve the request in your browser, then come back to Unity.\nWaiting up to 3 minutes…",
                MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = !string.IsNullOrEmpty(_signInUrl);
                if (GUILayout.Button("Open sign-in page again")) Application.OpenURL(_signInUrl);
                GUI.enabled = true;
                if (GUILayout.Button("Cancel")) CancelSignIn();
            }
        }

        private void DrawSignedIn()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var who = string.IsNullOrEmpty(_session.email) ? "your OnAirDeck account" : _session.email;
                EditorGUILayout.LabelField("Signed in as " + who, EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                GUI.enabled = !_signingOut && _downloadingItemId == null;
                if (GUILayout.Button(_signingOut ? "Signing out…" : "Sign out", EditorStyles.miniButton, GUILayout.Width(90))) SignOut();
                GUI.enabled = true;
            }

            EditorGUILayout.Space();
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
                _signingOut = false;
                Repaint();
            }
        }

        private void OnSessionExpired(string message)
        {
            SessionStore.Clear();
            _session = null;
            _purchases = null;
            SetMessage(message, MessageType.Warning);
        }

        // ---------- purchases ----------

        private void DrawPurchases()
        {
            if (_purchases == null && !_loadingPurchases) LoadPurchases();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Purchases", EditorStyles.boldLabel, GUILayout.Width(80));
                _search = EditorGUILayout.TextField(_search, EditorStyles.toolbarSearchField);
                GUI.enabled = !_loadingPurchases && _downloadingItemId == null;
                if (GUILayout.Button(_loadingPurchases ? "Loading…" : "Refresh", EditorStyles.miniButton, GUILayout.Width(70))) LoadPurchases();
                GUI.enabled = true;
            }

            if (_downloadingItemId != null)
            {
                var rect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(rect, _progress, _progressLabel);
                if (GUILayout.Button("Cancel download", EditorStyles.miniButton)) CancelDownload();
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(item.name ?? "", EditorStyles.boldLabel);
                    GUILayout.FlexibleSpace();
                    if (item.CanDownload)
                    {
                        var busy = _downloadingItemId != null;
                        var thisOne = _downloadingItemId == item.item_id;
                        GUI.enabled = !busy;
                        var label = thisOne ? "Downloading…" : item.downloaded ? "Download again" : "Download";
                        if (GUILayout.Button(label, RowButtonWidth)) Download(item);
                        GUI.enabled = true;
                    }
                    else
                    {
                        // Auto-sized: a fixed-width LabelField clipped this text on both sides.
                        GUILayout.Label("Not available in Unity", EditorStyles.miniLabel);
                    }
                }

                var details = item.date ?? "";
                if (item.files != null && item.files.Length > 0)
                    details += "  ·  " + item.files.Length + " file" + (item.files.Length == 1 ? "" : "s");
                if (item.update_available) details += "  ·  Update available";
                else if (item.downloaded) details += "  ·  Downloaded";
                EditorGUILayout.LabelField(details, EditorStyles.miniLabel);
            }
        }

        private async void LoadPurchases()
        {
            if (_session == null) return;
            _loadingPurchases = true;
            Repaint();
            try
            {
                _purchases = await OnAirDeckApi.GetPurchasesAsync(_session.token);
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

        // ---------- download ----------

        private async void Download(PurchaseItem item)
        {
            if (_session == null || _downloadingItemId != null) return;

            // The first download makes the purchase non-refundable (same rule as the website).
            if (!item.downloaded && !EditorUtility.DisplayDialog(
                    "Download " + item.name,
                    "Downloading makes this purchase non-refundable, the same as downloading on onairdeck.com.\n\nFiles will be placed in " + Installer.ProductFolder(item.name) + ".",
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
