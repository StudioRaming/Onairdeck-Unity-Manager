using System;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The "Feedback / 문의" section: a bug report, feature request or question to OnAirDeck (signed in only).</summary>
    internal sealed partial class ManagerWindow
    {
        private const string FeedbackOpenKey = "OnAirDeck.UnityManager.FeedbackOpen";
        private const float FeedbackMessageMinHeight = 72f;
        private const float FeedbackMessageMaxHeight = 180f;

        private static readonly GUIContent[] FeedbackKinds =
        {
            new GUIContent("Bug"),
            new GUIContent("Feature request"),
            new GUIContent("Question"),
        };

        // The draft survives a script reload (plain serialized fields); in-flight state does not.
        private int _feedbackKind;
        private string _feedbackMessage = "";
        private string _feedbackErrorCode = "";
        private bool _feedbackAttachLog = true;
        private Vector2 _feedbackMessageScroll;
        [NonSerialized] private bool _sendingFeedback;
        [NonSerialized] private string _feedbackResult;
        [NonSerialized] private SRTone _feedbackResultTone;
        [NonSerialized] private FeedbackEnvironment _feedbackEnv;
        [NonSerialized] private GUIStyle _feedbackAreaStyle;
        [NonSerialized] private bool _feedbackAreaDark;

        private void DrawFeedback()
        {
            if (_session == null) return;
            GUILayout.Space(SRMetrics.Space8);
            bool resetClicked;
            var label = new GUIContent("Feedback / 문의", "Send a bug report, a feature request or a question to OnAirDeck.");
            if (!SRGui.Section(FeedbackOpenKey, label, false, null, out resetClicked))
            {
                _feedbackEnv = null; // collected again on the next open (packages may have changed)
                return;
            }
            if (_feedbackEnv == null) _feedbackEnv = FeedbackEnvironment.Collect();

            SRGui.BeginCard();
            {
                GUI.enabled = !_sendingFeedback;
                _feedbackKind = SRGui.Popup(new GUIContent("Kind"), Mathf.Clamp(_feedbackKind, 0, FeedbackKinds.Length - 1), FeedbackKinds);

                GUILayout.Space(SRMetrics.Space4);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Message", SRStyles.Small, GUILayout.ExpandWidth(true));
                    var length = (_feedbackMessage ?? "").Trim().Length;
                    GUILayout.Label(length + " / " + FeedbackClient.MaxMessage, length > FeedbackClient.MaxMessage ? SRStyles.Strong : SRStyles.Mini, GUILayout.ExpandWidth(false));
                }
                _feedbackMessage = FeedbackTextArea(_feedbackMessage);

                GUILayout.Space(SRMetrics.Space4);
                _feedbackErrorCode = SRGui.TextField(
                    new GUIContent("Error code", "Optional: an error code or the first line of the error you saw."),
                    _feedbackErrorCode ?? "");

                var errors = ConsoleErrorBuffer.Count;
                _feedbackAttachLog = SRGui.ToggleLeft(
                    new GUIContent("Attach recent Console errors (" + errors + ")",
                        "The last " + ConsoleErrorBuffer.Capacity + " errors and exceptions since the last script reload, with up to "
                        + ConsoleErrorBuffer.MaxStackLines + " stack lines each. Your home folder is shown as ~."),
                    _feedbackAttachLog);
                GUI.enabled = true;

                GUILayout.Space(SRMetrics.Space4);
                GUILayout.Label("Sent with your message", SRStyles.Small);
                GUILayout.Label(FeedbackEnvironmentText(_feedbackEnv), _detailsStyle);

                GUILayout.Space(SRMetrics.Space4);
                var problem = FeedbackClient.Problem(_feedbackMessage, _feedbackErrorCode);
                using (new EditorGUILayout.HorizontalScope())
                {
                    var hint = problem != null && (_feedbackMessage ?? "").Trim().Length > 0 ? problem : "Your OnAirDeck account is sent with the message so we can reply.";
                    GUILayout.Label(hint, _detailsStyle, GUILayout.ExpandWidth(true));
                    GUI.enabled = !_sendingFeedback && problem == null;
                    if (ActionButton(_sendingFeedback ? "Sending…" : "Send", false, true)) SendFeedback();
                    GUI.enabled = true;
                }

                if (!string.IsNullOrEmpty(_feedbackResult))
                {
                    GUILayout.Space(SRMetrics.Space4);
                    SRGui.HelpBox(_feedbackResult, _feedbackResultTone);
                }
            }
            SRGui.EndCard();
        }

        /// <summary>Word-wrapped multi-line field in the theme's field face; grows with the text up to a cap, then scrolls.</summary>
        private string FeedbackTextArea(string value)
        {
            var p = SRTheme.P;
            if (_feedbackAreaStyle == null || _feedbackAreaDark != SRTheme.Dark)
            {
                _feedbackAreaDark = SRTheme.Dark;
                _feedbackAreaStyle = new GUIStyle(SRStyles.FieldText) { wordWrap = true, padding = new RectOffset(5, 5, 4, 4) };
            }
            _feedbackAreaStyle.normal.textColor = GUI.enabled ? p.Text : SRTheme.DisabledInk(p.SurfaceSunken);
            _feedbackAreaStyle.focused.textColor = _feedbackAreaStyle.normal.textColor;

            value = value ?? "";
            var width = Mathf.Max(60f, EditorGUIUtility.currentViewWidth - 2f * SRMetrics.Space12 - 2f * SRMetrics.Space8 - 24f);
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

        private static string FeedbackEnvironmentText(FeedbackEnvironment env)
        {
            if (env == null) return "";
            var text = "Unity " + Or(env.UnityVersion) + "  ·  Manager " + Or(env.ManagerVersion) + "\n" + Or(env.Os);
            text += "\nOnAirDeck packages: " + (env.Packages.Length == 0 ? "none" : string.Join(", ", env.Packages));
            return text;
        }

        private static string Or(string value)
        {
            return string.IsNullOrEmpty(value) ? "unknown" : value;
        }

        private async void SendFeedback()
        {
            if (_session == null || _sendingFeedback) return;
            if (FeedbackClient.Problem(_feedbackMessage, _feedbackErrorCode) != null) return;
            _sendingFeedback = true;
            _feedbackResult = null;
            Repaint();
            try
            {
                var body = FeedbackClient.Build((FeedbackKind)Mathf.Clamp(_feedbackKind, 0, FeedbackKinds.Length - 1),
                    _feedbackMessage, _feedbackErrorCode, _feedbackAttachLog, _feedbackEnv);
                var id = await FeedbackClient.SendAsync(_session.token, body);
                _feedbackMessage = "";
                _feedbackErrorCode = "";
                _feedbackMessageScroll = Vector2.zero;
                GUIUtility.keyboardControl = 0; // a focused field would keep showing the old text
                var reference = string.IsNullOrEmpty(id) ? "" : "\nReference: " + (id.Length > 8 ? id.Substring(0, 8) : id);
                SetFeedbackResult("Thank you. Your message was sent to OnAirDeck." + reference, SRTone.Success);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                SetFeedbackResult(e.Message, SRTone.Danger);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sending feedback failed: " + e.Message);
            }
            finally
            {
                _sendingFeedback = false;
                Repaint();
            }
        }

        private void SetFeedbackResult(string text, SRTone tone)
        {
            _feedbackResult = text;
            _feedbackResultTone = tone;
            Repaint();
        }
    }
}
