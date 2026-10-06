using System;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Window ▸ OnAirDeck Unity Manager — sign in / sign out (Phase 1B).</summary>
    internal sealed class ManagerWindow : EditorWindow
    {
        private SavedSession _session;
        private bool _signingIn;
        private bool _signingOut;
        private string _signInUrl;
        private string _message;
        private MessageType _messageType = MessageType.None;
        private CancellationTokenSource _signInCancel;

        [MenuItem("Window/OnAirDeck Unity Manager")]
        public static void Open()
        {
            var window = GetWindow<ManagerWindow>();
            window.titleContent = new GUIContent("OnAirDeck");
            window.minSize = new Vector2(320, 220);
            window.Show();
        }

        private void OnEnable()
        {
            _session = SessionStore.Load();
        }

        private void OnDisable()
        {
            // Closing the window (or a script reload) abandons an in-progress sign-in.
            CancelSignIn();
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

            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Open onairdeck.com", EditorStyles.miniButton))
                Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
        }

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
            var who = string.IsNullOrEmpty(_session.email) ? "your OnAirDeck account" : _session.email;
            EditorGUILayout.HelpBox("Signed in as " + who, MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Purchase history is coming in the next update.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.Space();

            GUI.enabled = !_signingOut;
            if (GUILayout.Button(_signingOut ? "Signing out…" : "Sign out")) SignOut();
            GUI.enabled = true;
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
                _signingOut = false;
                Repaint();
            }
        }

        /// <summary>Called by later features when the server rejects the saved session.</summary>
        internal void OnSessionExpired(string message)
        {
            SessionStore.Clear();
            _session = null;
            SetMessage(message, MessageType.Warning);
        }

        private void SetMessage(string text, MessageType type)
        {
            _message = text;
            _messageType = type;
            Repaint();
        }
    }
}
