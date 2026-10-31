using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The strip above the panes. One bar at a time, in this order: this PC released (v7), a message, the project check (U3),
    /// a VCC install to move to the Manager (or its undo), a new purchase, a Manager update, products with a newer version.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private struct BarButton
        {
            public string Label;
            public string Tooltip;
            public SRButtonKind Kind;
            public bool Enabled;
            public System.Action Click;
        }

        /// <summary>Draws the bar that applies and returns the height it took (0 when there is none).</summary>
        private float DrawBars(Rect body)
        {
            var width = body.width;
            var notice = DeviceLimitNotice.Current;
            if (notice != null && notice.Kind == NoticeKind.DeviceReleased)
            {
                var lang = ManagerText.Language;
                return DrawBar(body, SRTone.Warning, DeviceReleasedText.Title(lang) + "\n" + DeviceReleasedText.Body(lang),
                    new BarButton { Label = DeviceReleasedText.Button(lang), Kind = SRButtonKind.Primary, Enabled = !_removing, Click = ReactivateThisPc });
            }
            if (!string.IsNullOrEmpty(_message))
            {
                var close = new BarButton { Label = ManagerText.Close, Kind = SRButtonKind.Ghost, Enabled = true, Click = () => { _message = null; } };
                if (_messageType != MessageType.Error) return DrawBar(body, Tone(_messageType), _message, close);
                var report = new BarButton { Label = ManagerText.ErBtn, Kind = SRButtonKind.Secondary, Enabled = true, Click = ReportCurrentMessage };
                return DrawBar(body, Tone(_messageType), _message, report, close);
            }
            // Conflicts can stop scripts from compiling: before purchases and updates.
            if (_conflicts.Count > 0 || _checkMoves.Count + _checkRemovals.Count > 0)
            {
                var text = _conflicts.Count > 0 ? ManagerText.CfBar.Replace("{n}", _conflicts.Count.ToString()) : ManagerText.CfUndoBar.Replace("{n}", (_checkMoves.Count + _checkRemovals.Count).ToString());
                var view = new BarButton { Label = ManagerText.CfView, Kind = SRButtonKind.Secondary, Enabled = true, Click = OpenProjectCheck };
                return DrawBar(body, _conflicts.Count > 0 ? SRTone.Warning : SRTone.Info, text, view);
            }
            // Before purchases and updates: an update the Manager installs could be put back to VCC's version.
            var vcc = DrawVccBar(body);
            if (vcc > 0f) return vcc;
            var unseen = PurchaseWatcher.UnseenCount;
            if (unseen > 0 && _session != null)
            {
                var first = FirstUnseen();
                var getNow = new BarButton
                {
                    Label = ManagerText.GetNow, Kind = SRButtonKind.Primary, Enabled = !Busy && first != null && first.Kind == FavoriteKind.Purchase && first.Purchase.CanDownload,
                    Tooltip = Busy ? ManagerText.WaitTip : "",
                    Click = () => { if (first != null) { Select(first, true); DoDownload(first); } }
                };
                var view = new BarButton { Label = ManagerText.ViewNew, Kind = SRButtonKind.Secondary, Enabled = first != null, Click = () => { if (first != null) ShowEntry(first); } };
                return DrawBar(body, SRTone.Brand, ManagerText.NewBuyBar.Replace("{n}", unseen.ToString()), view, getNow);
            }
            if (_update != null && _update.UpdateAvailable && !_update.MustUpdate)
            {
                var canApply = _update.CanApply || _update.UseEmbedded;
                var apply = new BarButton
                {
                    Label = ManagerText.UpdBtn, Kind = SRButtonKind.Primary, Enabled = canApply && !Busy,
                    Tooltip = Busy ? ManagerText.UpdWhy : (_update.ApplyBlockedReason ?? ""), Click = ApplyUpdate
                };
                return DrawBar(body, SRTone.Info, ManagerText.UpdNew.Replace("{v}", _update.Response.latest.version), apply);
            }
            var updates = UpdateCheckService.Count;
            if (updates > 0 && _session != null)
            {
                var show = new BarButton { Label = ManagerText.ViewNew, Kind = SRButtonKind.Secondary, Enabled = true, Click = () => { _filter = (int)FilterMode.New; _search = ""; _sellerFilter = ""; _linesDirty = true; } };
                var all = new BarButton { Label = ManagerText.UpdAll, Kind = SRButtonKind.Primary, Enabled = !Busy, Tooltip = Busy ? ManagerText.WaitTip : "", Click = StartUpdateAll };
                return DrawBar(body, SRTone.Info, ManagerText.AutoNew.Replace("{n}", updates.ToString()), show, all);
            }
            return 0f;
        }

        private CatalogEntry FirstUnseen()
        {
            if (_entries == null) return null;
            foreach (var entry in _entries) if (PurchaseWatcher.IsUnseen(entry.Key)) return entry;
            return null;
        }

        /// <summary>Clears search and filter so the entry is in the list, then selects it.</summary>
        private void ShowEntry(CatalogEntry entry)
        {
            _search = "";
            _filter = (int)FilterMode.All;
            _sellerFilter = "";
            _linesDirty = true;
            Select(entry, true);
        }

        private float DrawBar(Rect body, SRTone tone, string text, params BarButton[] buttons)
        {
            var pad = SRMetrics.Space8;
            var colors = SRTheme.Tone(tone);
            var buttonWidths = new float[buttons.Length];
            var total = 0f;
            for (var i = 0; i < buttons.Length; i++)
            {
                buttonWidths[i] = Mathf.Max(64f, SRStyles.ButtonText.CalcSize(new GUIContent(buttons[i].Label)).x + 2f * SRMetrics.Space12);
                total += buttonWidths[i] + SRMetrics.Space6;
            }
            var textWidth = Mathf.Max(80f, body.width - 2f * pad - total);
            var textHeight = SRStyles.Small.CalcHeight(new GUIContent(text), textWidth);
            var height = Mathf.Max(SRMetrics.ButtonLarge + 2f * SRMetrics.Space6, textHeight + 2f * SRMetrics.Space6);
            var bar = new Rect(body.x, body.y, body.width, height);

            if (Event.current.type == EventType.Repaint)
            {
                SRTheme.Fill(bar, colors.Bg);
                SRTheme.Fill(new Rect(bar.x, bar.yMax - 1f, bar.width, 1f), colors.Border);
            }
            var label = new Rect(bar.x + pad, bar.y + SRMetrics.Space6, textWidth, textHeight);
            var style = new GUIStyle(SRStyles.Small) { wordWrap = true };
            style.normal.textColor = colors.Text;
            GUI.Label(label, text, style);

            var x = bar.xMax - pad;
            for (var i = buttons.Length - 1; i >= 0; i--)
            {
                x -= buttonWidths[i];
                var r = new Rect(x, bar.y + (bar.height - SRMetrics.Button) * 0.5f, buttonWidths[i], SRMetrics.Button);
                using (new GUIEnabledScope(buttons[i].Enabled))
                {
                    if (SRGui.Button(r, new GUIContent(buttons[i].Label, buttons[i].Tooltip ?? ""), buttons[i].Kind, SRButtonSize.Default))
                    {
                        var click = buttons[i].Click;
                        Defer(click);
                    }
                }
                x -= SRMetrics.Space6;
            }
            return height;
        }
    }
}
