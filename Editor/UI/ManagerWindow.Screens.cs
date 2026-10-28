using System;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Full-window screens: signed out, signing in, timeout, required update, loading, load error, empty; and the header tools.</summary>
    internal sealed partial class ManagerWindow
    {
        private void BeginScreen(Rect body)
        {
            var width = Mathf.Min(ManagerLayout.FormMaxWidth, body.width - 2f * SRMetrics.Space16);
            var rect = new Rect(body.x + (body.width - width) * 0.5f, body.y + SRMetrics.Space16, width,
                Mathf.Max(0f, body.height - 2f * SRMetrics.Space16));
            GUILayout.BeginArea(rect);
        }

        private static void EndScreen()
        {
            GUILayout.EndArea();
        }

        private static void OpenWeb(string url)
        {
            if (!string.IsNullOrEmpty(url) && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) Application.OpenURL(url);
            else Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
        }

        // ------------------------------------------------------------ header tools

        private void DrawHeaderTools(Rect tools)
        {
            if (_session == null)
            {
                if (SRGui.Button(tools, new GUIContent(ManagerText.LangName, ManagerText.MAcctLang), SRButtonKind.Ghost, SRButtonSize.Mini))
                    ShowLanguageMenu();
                return;
            }

            var refresh = new Rect(tools.x, tools.y, tools.height, tools.height);
            var account = new Rect(refresh.xMax + SRMetrics.Space4, tools.y, tools.xMax - refresh.xMax - SRMetrics.Space4, tools.height);
            using (new GUIEnabledScope(!_loading && !Busy))
            {
                if (SRGui.Button(refresh, new GUIContent("", ManagerText.RefreshTip), SRButtonKind.Ghost, SRButtonSize.Mini))
                    Defer(RefreshClicked);
            }
            DrawRefreshGlyph(refresh);
            if (SRGui.Button(account, new GUIContent(ManagerText.LoggedIn, _session.email), SRButtonKind.Ghost, SRButtonSize.Mini))
                ShowAccountMenu();
        }

        /// <summary>Two arc segments with an arrow head, in the text color; the theme has no refresh glyph.</summary>
        private static void DrawRefreshGlyph(Rect r)
        {
            if (Event.current.type != EventType.Repaint) return;
            var color = GUI.enabled ? SRTheme.P.Text : SRTheme.P.TextMuted;
            var center = r.center;
            var radius = Mathf.Min(r.width, r.height) * 0.28f;
            var points = new Vector3[14];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = Mathf.Deg2Rad * Mathf.Lerp(40f, 330f, i / (float)(points.Length - 1));
                points[i] = new Vector3(center.x + Mathf.Cos(angle) * radius, center.y - Mathf.Sin(angle) * radius, 0f);
            }
            Handles.color = color;
            Handles.DrawAAPolyLine(1.6f, points);
            var tip = points[points.Length - 1];
            var back = points[points.Length - 3];
            var direction = (tip - back).normalized;
            var side = new Vector3(-direction.y, direction.x, 0f);
            Handles.DrawAAConvexPolygon(tip + direction * 2.6f, tip - direction * 1.2f + side * 2.6f, tip - direction * 1.2f - side * 2.6f);
        }

        private void ShowLanguageMenu()
        {
            var menu = new GenericMenu();
            var current = (int)SRLanguage.Get(ManagerText.LanguageKey);
            var names = new[] { "한국어", "English", "日本語" };
            for (var i = 0; i < names.Length; i++)
            {
                var lang = (SRLang)i;
                menu.AddItem(new GUIContent(names[i]), current == i, () => SRLanguage.Set(lang, ManagerText.LanguageKey));
            }
            menu.ShowAsContext();
        }

        private void ShowAccountMenu()
        {
            var menu = new GenericMenu();
            menu.AddDisabledItem(new GUIContent(_session.email ?? ""));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(ManagerText.MAcctDev), _view == (int)View.Devices, () => Defer(OpenDeviceManager));
            var current = (int)SRLanguage.Get(ManagerText.LanguageKey);
            var names = new[] { "한국어", "English", "日本語" };
            for (var i = 0; i < names.Length; i++)
            {
                var lang = (SRLang)i;
                menu.AddItem(new GUIContent(ManagerText.MAcctLang + "/" + names[i]), current == i, () => SRLanguage.Set(lang, ManagerText.LanguageKey));
            }
            menu.AddItem(new GUIContent(ManagerText.MAcctWeb), false, () => Application.OpenURL(OnAirDeckConfig.WebsiteUrl));
            menu.AddSeparator("");
            menu.AddItem(new GUIContent(ManagerText.MAcctOut), false, () => Defer(SignOut));
            menu.ShowAsContext();
        }

        private struct GUIEnabledScope : IDisposable
        {
            private readonly bool _previous;

            public GUIEnabledScope(bool enabled)
            {
                _previous = GUI.enabled;
                GUI.enabled = _previous && enabled;
            }

            public void Dispose()
            {
                GUI.enabled = _previous;
            }
        }

        // ------------------------------------------------------------ screens

        private void DrawSignedOut(Rect body)
        {
            BeginScreen(body);
            GUILayout.Label(ManagerText.Welcome, SRStyles.Title);
            GUILayout.Space(SRMetrics.Space12);
            DrawStep("1", ManagerText.S1, ManagerText.S1d);
            DrawStep("2", ManagerText.S2, ManagerText.S2d);
            DrawStep("3", ManagerText.S3, ManagerText.S3d);
            GUILayout.Space(SRMetrics.Space12);
            if (!string.IsNullOrEmpty(_message)) SRGui.HelpBox(_message, Tone(_messageType));
            if (SRGui.Button(new GUIContent(ManagerText.Login), SRButtonKind.Primary, SRButtonSize.Hero, GUILayout.ExpandWidth(true)))
                Defer(SignIn);
            GUILayout.Space(SRMetrics.Space4);
            if (SRGui.Button(new GUIContent(ManagerText.Signup), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(true)))
                Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
            EndScreen();
        }

        private static void DrawStep(string number, string title, string description)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                SRGui.BadgeLayout(number, SRTone.Brand);
                using (new EditorGUILayout.VerticalScope())
                {
                    GUILayout.Label(title, SRStyles.Strong);
                    GUILayout.Label(description, SRStyles.MutedWrap);
                }
            }
            GUILayout.Space(SRMetrics.Space6);
        }

        private void DrawSigningIn(Rect body)
        {
            BeginScreen(body);
            GUILayout.Label(ManagerText.SiTitle, SRStyles.Title);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.Label(ManagerText.SiHint, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space12);
            // A static bar: it is redrawn once a second, not animated.
            var total = Mathf.Max(1, AuthService.SignInTimeoutMs / 1000);
            var left = SignInSecondsLeft();
            SRGui.ProgressBar(left / (float)total, ManagerText.SiLeft.Replace("{t}", FormatSeconds(left)), SRTone.Brand);
            GUILayout.Space(SRMetrics.Space12);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new GUIEnabledScope(!string.IsNullOrEmpty(_signInUrl)))
                {
                    if (SRGui.Button(new GUIContent(ManagerText.Reopen), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                        Application.OpenURL(_signInUrl);
                }
                GUILayout.FlexibleSpace();
                if (SRGui.Button(new GUIContent(ManagerText.Cancel), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(CancelSignIn);
            }
            EndScreen();
        }

        private static string FormatSeconds(int seconds)
        {
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        private void DrawTimeout(Rect body)
        {
            BeginScreen(body);
            GUILayout.Label(ManagerText.ToTitle, SRStyles.Title);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.Label(_timeoutText, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space12);
            if (SRGui.Button(new GUIContent(ManagerText.ToBtn), SRButtonKind.Primary, SRButtonSize.Large, GUILayout.ExpandWidth(true)))
                Defer(() => { _timeoutText = null; SignIn(); });
            GUILayout.Space(SRMetrics.Space4);
            if (SRGui.Button(new GUIContent(ManagerText.Back), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(true)))
                Defer(() => { _timeoutText = null; });
            EndScreen();
        }

        private void DrawRequiredUpdate(Rect body)
        {
            BeginScreen(body);
            var version = _update != null && _update.HasLatest ? _update.Response.latest.version : "";
            GUILayout.Label(ManagerText.ReqTitle, SRStyles.Title);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.Label(version.Length > 0 ? ManagerText.ReqBody.Replace("{v}", version) : ManagerText.ReqBodyPlain, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space12);
            var canApply = _update != null && (_update.CanApply || _update.UseEmbedded);
            var busy = Busy;
            using (new GUIEnabledScope(canApply && !busy))
            {
                if (SRGui.Button(new GUIContent(ManagerText.ReqBtn, busy ? ManagerText.UpdWhy : ""), SRButtonKind.Primary, SRButtonSize.Hero, GUILayout.ExpandWidth(true)))
                    Defer(ApplyUpdate);
            }
            if (!canApply)
            {
                GUILayout.Space(SRMetrics.Space4);
                if (SRGui.Button(new GUIContent(ManagerText.ViewAt), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(true)))
                    Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
            }
            if (!string.IsNullOrEmpty(_message))
            {
                GUILayout.Space(SRMetrics.Space8);
                SRGui.HelpBox(_message, Tone(_messageType));
            }
            EndScreen();
        }

        private void DrawLoading(Rect body)
        {
            BeginScreen(body);
            GUILayout.Space(SRMetrics.Space16);
            GUILayout.Label(ManagerText.Loading, SRStyles.Center);
            EndScreen();
        }

        private void DrawLoadError(Rect body)
        {
            BeginScreen(body);
            if (SRGui.HelpBox(ManagerText.PLoad, SRTone.Danger, ManagerText.PLoadB))
                Defer(() => LoadEntries(true));
            if (!string.IsNullOrEmpty(_loadError))
            {
                GUILayout.Space(SRMetrics.Space4);
                GUILayout.Label(_loadError, SRStyles.MutedWrap);
            }
            GUILayout.Space(SRMetrics.Space8);
            if (SRGui.Button(new GUIContent(ManagerText.Contact), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                Defer(OpenAsk);
            EndScreen();
        }

        private void DrawEmpty(Rect body)
        {
            BeginScreen(body);
            if (SRGui.EmptyState(ManagerText.EmptyT, ManagerText.EmptyM, ManagerText.EmptyB, body.height * 0.5f))
                Application.OpenURL(OnAirDeckConfig.WebsiteUrl);
            EndScreen();
        }
    }
}
