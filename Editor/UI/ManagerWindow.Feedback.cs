using System;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The ask screen: a bug report, a feature suggestion or a question to OnAirDeck (signed in only).</summary>
    internal sealed partial class ManagerWindow
    {
        private const float FeedbackMessageMinHeight = 72f;
        private const float FeedbackMessageMaxHeight = 180f;

        // The draft survives a script reload (plain serialized fields); in-flight state does not.
        private int _feedbackKind;
        private string _feedbackMessage = "";
        private string _feedbackErrorCode = "";
        private bool _feedbackAttachLog = true;
        private bool _askErrorOpen;
        private bool _askSentOpen;
        private Vector2 _feedbackMessageScroll;
        [NonSerialized] private bool _sendingFeedback;
        [NonSerialized] private string _feedbackSentId;
        [NonSerialized] private string _feedbackError;
        [NonSerialized] private FeedbackEnvironment _feedbackEnv;
        [NonSerialized] private GUIStyle _feedbackAreaStyle;
        [NonSerialized] private bool _feedbackAreaDark;

        private void OpenAsk()
        {
            _view = (int)View.Ask;
            _feedbackSentId = null;
            _feedbackError = null;
            _feedbackEnv = FeedbackEnvironment.Collect();
            Repaint();
        }

        private void DrawAsk(Rect body)
        {
            if (_feedbackEnv == null) _feedbackEnv = FeedbackEnvironment.Collect();
            BeginScreen(body);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ManagerText.AskTitle, SRStyles.Title, GUILayout.ExpandWidth(true));
            if (SRGui.Button(new GUIContent(ManagerText.Back), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => { _view = (int)View.Main; });
            GUILayout.EndHorizontal();
            GUILayout.Space(SRMetrics.Space8);

            if (_feedbackSentId != null)
            {
                var reference = _feedbackSentId.Length > 8 ? _feedbackSentId.Substring(0, 8) : _feedbackSentId;
                SRGui.HelpBox(ManagerText.Sent.Replace("{id}", reference), SRTone.Success);
                GUILayout.Space(SRMetrics.Space8);
                if (SRGui.Button(new GUIContent(ManagerText.Back), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(() => { _view = (int)View.Main; });
                EndScreen();
                return;
            }

            using (new GUIEnabledScope(!_sendingFeedback))
            {
                GUILayout.BeginHorizontal();
                var kinds = new[] { ManagerText.CBug, ManagerText.CIdea, ManagerText.CQ };
                for (var i = 0; i < kinds.Length; i++)
                {
                    if (SRGui.ToggleButton(new GUIContent(kinds[i]), _feedbackKind == i, SRButtonSize.Default, GUILayout.ExpandWidth(true)) && _feedbackKind != i)
                    {
                        var index = i;
                        Defer(() => { _feedbackKind = index; });
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(SRMetrics.Space8);

                GUILayout.BeginHorizontal();
                GUILayout.Label(ManagerText.Msg, SRStyles.Strong, GUILayout.ExpandWidth(true));
                var length = (_feedbackMessage ?? "").Trim().Length;
                GUILayout.Label(length + " / " + FeedbackClient.MaxMessage, length > FeedbackClient.MaxMessage ? SRStyles.Strong : SRStyles.Mini, GUILayout.ExpandWidth(false));
                GUILayout.EndHorizontal();
                _feedbackMessage = FeedbackTextArea(_feedbackMessage, body.width);

                GUILayout.Space(SRMetrics.Space6);
                bool reset;
                if (SRGui.Section(ref _askErrorOpen, new GUIContent(ManagerText.ErrCode), null, out reset))
                    _feedbackErrorCode = SRGui.TextField(new GUIContent(ManagerText.ErrCode), _feedbackErrorCode ?? "");
                if (SRGui.Section(ref _askSentOpen, new GUIContent(ManagerText.WhatSent), null, out reset))
                    DrawWhatIsSent();
            }

            GUILayout.Space(SRMetrics.Space8);
            var problem = FeedbackProblem();
            var empty = (_feedbackMessage ?? "").Trim().Length == 0;
            GUILayout.BeginHorizontal();
            GUILayout.Label(empty ? ManagerText.SendWhy : (problem ?? ""), SRStyles.MutedWrap, GUILayout.ExpandWidth(true));
            using (new GUIEnabledScope(!_sendingFeedback && problem == null))
            {
                if (SRGui.Button(new GUIContent(_sendingFeedback ? ManagerText.Sending : ManagerText.Send), SRButtonKind.Primary, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                    Defer(SendFeedback);
            }
            GUILayout.EndHorizontal();
            if (!string.IsNullOrEmpty(_feedbackError))
            {
                GUILayout.Space(SRMetrics.Space4);
                SRGui.HelpBox(_feedbackError, SRTone.Danger);
            }
            EndScreen();
        }

        private void DrawWhatIsSent()
        {
            var env = _feedbackEnv;
            InfoRow(ManagerText.KMgr, Or(env.ManagerVersion));
            InfoRow(ManagerText.KUnity, Or(env.UnityVersion));
            InfoRow(ManagerText.KOs, Or(env.Os));
            InfoRow(ManagerText.KAcc, _session != null ? _session.email : "");
            InfoRow(ManagerText.KDev, DeviceIdentity.DisplayName);
            InfoRow(ManagerText.KPackages, env.Packages.Length == 0 ? ManagerText.None : FeedbackText.Redact(string.Join(", ", env.Packages), FeedbackClient.LocalContext()));
            _feedbackAttachLog = SRGui.ToggleLeft(new GUIContent(ManagerText.AttachLog.Replace("{n}", ConsoleErrorBuffer.Count.ToString())), _feedbackAttachLog);
        }

        /// <summary>Word-wrapped multi-line field in the theme's field face; grows with the text up to a cap, then scrolls.</summary>
        private string FeedbackTextArea(string value, float screenWidth)
        {
            var p = SRTheme.P;
            EnsureFeedbackAreaStyle();
            value = value ?? "";
            var width = Mathf.Max(60f, Mathf.Min(ManagerLayout.FormMaxWidth, screenWidth - 2f * SRMetrics.Space16) - 12f);
            var contentHeight = _feedbackAreaStyle.CalcHeight(new GUIContent(value + "\n"), width);
            var height = Mathf.Clamp(contentHeight, FeedbackMessageMinHeight, FeedbackMessageMaxHeight);

            var frame = GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true));
            SRTheme.Box(frame, p.SurfaceSunken, p.LineControl, SRMetrics.RadiusControl);
            var inner = new Rect(frame.x + 1f, frame.y + 1f, frame.width - 2f, frame.height - 2f);
            if (contentHeight <= height) return EditorGUI.TextArea(inner, value, _feedbackAreaStyle);

            // Longer than the cap: scroll inside the frame.
            var view = new Rect(0f, 0f, inner.width - 14f, _feedbackAreaStyle.CalcHeight(new GUIContent(value + "\n"), inner.width - 14f));
            _feedbackMessageScroll = GUI.BeginScrollView(inner, _feedbackMessageScroll, view, false, true);
            value = EditorGUI.TextArea(view, value, _feedbackAreaStyle);
            GUI.EndScrollView();
            return value;
        }

        /// <summary>The wrapped multi-line field face, themed for the current palette and enabled state.</summary>
        private GUIStyle EnsureFeedbackAreaStyle()
        {
            var p = SRTheme.P;
            if (_feedbackAreaStyle == null || _feedbackAreaDark != SRTheme.Dark)
            {
                _feedbackAreaDark = SRTheme.Dark;
                _feedbackAreaStyle = new GUIStyle(SRStyles.FieldText) { wordWrap = true, padding = new RectOffset(5, 5, 4, 4) };
            }
            _feedbackAreaStyle.normal.textColor = GUI.enabled ? p.Text : SRTheme.DisabledInk(p.SurfaceSunken);
            _feedbackAreaStyle.focused.textColor = _feedbackAreaStyle.normal.textColor;
            return _feedbackAreaStyle;
        }

        /// <summary>The same limits as FeedbackClient.Problem, with texts in the chosen language. Null when the message can be sent.</summary>
        private string FeedbackProblem()
        {
            var text = (_feedbackMessage ?? "").Trim();
            if (text.Length == 0) return ManagerText.FbEmpty;
            if (text.Length > FeedbackClient.MaxMessage) return ManagerText.FbTooLong.Replace("{n}", FeedbackClient.MaxMessage.ToString());
            if ((_feedbackErrorCode ?? "").Trim().Length > FeedbackClient.MaxErrorCode) return ManagerText.FbCodeTooLong.Replace("{n}", FeedbackClient.MaxErrorCode.ToString());
            return null;
        }

        private static string Or(string value)
        {
            return string.IsNullOrEmpty(value) ? "-" : value;
        }

        private async void SendFeedback()
        {
            if (_session == null || _sendingFeedback) return;
            if (FeedbackProblem() != null) return;
            _sendingFeedback = true;
            _feedbackError = null;
            Repaint();
            try
            {
                var body = FeedbackClient.Build((FeedbackKind)Mathf.Clamp(_feedbackKind, 0, 2), _feedbackMessage, _feedbackErrorCode,
                    _feedbackAttachLog ? ConsoleErrorBuffer.Format(FeedbackClient.MaxConsoleLogBytes) : "", _feedbackEnv);
                var id = await FeedbackClient.SendAsync(_session.token, body);
                _feedbackMessage = "";
                _feedbackErrorCode = "";
                _feedbackMessageScroll = Vector2.zero;
                GUIUtility.keyboardControl = 0; // a focused field would keep showing the old text
                _feedbackSentId = id ?? "";
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                _feedbackError = FeedbackText.Redact(e.Message, FeedbackClient.LocalContext());
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sending feedback failed: " + _feedbackError);
            }
            finally
            {
                _sendingFeedback = false;
                Repaint();
            }
        }
    }
}
