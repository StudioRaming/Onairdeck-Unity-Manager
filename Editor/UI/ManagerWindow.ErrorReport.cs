using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The install error report card at the top of the window and the button next to a red message. The card is drawn with plain
    /// rects (no GUILayout), so it can come and go between two events without disturbing the control list of the screen below.
    /// The logic (what is a report, redaction, dedupe, sending) is in Editor/Feedback; nothing is sent without the Send click.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private const float ReportNoteHeight = 40f;
        private const float ReportLogMaxHeight = 140f;
        private const string ReportNoteControl = "AtelierInstallReportNote";

        // The draft of the card survives a script reload; in-flight state does not.
        private string _reportKey = "";
        private string _reportNote = "";
        private bool _reportLogOpen;
        private Vector2 _reportLogScroll;
        [NonSerialized] private bool _sendingReport;
        [NonSerialized] private string _reportError;
        [NonSerialized] private InstallErrorReport _messageReport;
        // What the card shows is frozen per revision of the report; Send sends exactly this snapshot.
        [NonSerialized] private string _excerptKey;
        [NonSerialized] private InstallErrorSnapshot _shot;
        [NonSerialized] private string _excerpt = "";

        /// <summary>Opens the window without taking focus when it is closed; an open window shows the card by itself.</summary>
        internal static void ShowForErrorReport()
        {
            if (InstallErrorReporter.Current == null) return;
            if (Resources.FindObjectsOfTypeAll<ManagerWindow>().Length > 0) return;
            var window = GetWindow<ManagerWindow>(false, ManagerText.AppTitle, false);
            window.minSize = new Vector2(ManagerLayout.WindowMinWidth, ManagerLayout.WindowMinHeight);
        }

        /// <summary>A failed install or import: the red message, and a report for the card unless the failure is not a bug.</summary>
        private void FailedInstall(string productId, string product, string version, Exception e)
        {
            if (IsNoticeRefusal(e)) return;
            SetMessage(e.Message, MessageType.Error);
            _messageReport = InstallErrorReporter.FromFailure(productId, product, version, e);
            InstallErrorReporter.Offer(_messageReport);
        }

        /// <summary>The id the error report signature is keyed on: the license id, else the purchase's product id.</summary>
        private static string ProductIdOf(CatalogEntry entry)
        {
            if (entry.IsLicense) return entry.LicenseId;
            return entry.Purchase != null ? DownloadService.ProductIdOf(entry.Purchase) : entry.Name;
        }

        /// <summary>The button next to a red message: opens the card for it again, even if it was dismissed before; an already sent one says so.</summary>
        private void ReportCurrentMessage()
        {
            if (InstallErrorReporter.Offer(_messageReport ?? InstallErrorReporter.FromMessage(_message), true) == OfferResult.Duplicate)
                SetMessage(ManagerText.ErDuplicate, MessageType.Info);
        }

        /// <summary>The failed items of a finished batch, one report per product.</summary>
        private void ReportBatchFailures(BatchQueue queue, List<string> keys)
        {
            for (var i = 0; i < queue.Results.Count; i++)
            {
                var result = queue.Results[i];
                if (result.Status != BatchStatus.Failed || result.Error == null) continue;
                var key = i < keys.Count ? keys[i] : "";
                var name = result.Name;
                var productId = result.Name;
                if (_entries != null)
                {
                    var entry = _entries.Find(x => x.Key == key);
                    if (entry != null) { name = entry.Name; productId = ProductIdOf(entry); }
                }
                InstallErrorReporter.Offer(InstallErrorReporter.FromFailure(productId, name, result.Version, result.Error));
            }
        }

        /// <summary>Draws the card and returns the height it took.</summary>
        private float DrawReportCard(Rect body, InstallErrorReport report)
        {
            var key = report.Product + "@" + report.Version;
            if (key != _reportKey)
            {
                _reportKey = key;
                _reportNote = "";
                _reportLogOpen = false;
                _reportLogScroll = Vector2.zero;
                _reportError = null;
                GUIUtility.keyboardControl = 0;
            }
            var excerptKey = key + "|" + report.Revision;
            if (excerptKey != _excerptKey || _shot == null)
            {
                _excerptKey = excerptKey;
                _shot = report.TakeSnapshot(FeedbackClient.MaxConsoleLogBytes, FeedbackClient.MaxMessage);
                _excerpt = _shot.Excerpt;
            }

            var pad = SRMetrics.Space8;
            var gap = SRMetrics.Space4;
            var width = Mathf.Max(80f, body.width - 2f * pad);
            var colors = SRTheme.Tone(SRTone.Warning);
            var title = new GUIStyle(SRStyles.Strong) { wordWrap = true };
            title.normal.textColor = colors.Text;
            var text = new GUIStyle(SRStyles.Small) { wordWrap = true };
            text.normal.textColor = colors.Text;

            var titleText = ManagerText.ErTitle.Replace("{n}", report.Product);
            var reason = report.Headline;
            var status = !string.IsNullOrEmpty(_reportError) ? _reportError : _session == null ? ManagerText.ErSignIn : null;
            var titleHeight = title.CalcHeight(new GUIContent(titleText), width);
            var reasonHeight = reason.Length > 0 ? text.CalcHeight(new GUIContent(reason), width) : 0f;
            var statusHeight = status != null ? text.CalcHeight(new GUIContent(status), width) : 0f;
            var noteLabelHeight = text.CalcHeight(new GUIContent(ManagerText.ErNoteLabel), width);
            var logNoteHeight = text.CalcHeight(new GUIContent(ManagerText.ErLogNote), width);

            var height = pad + titleHeight + reasonHeight + gap + SRMetrics.ButtonMini + gap
                + noteLabelHeight + ReportNoteHeight + gap + (statusHeight > 0f ? statusHeight + gap : 0f) + SRMetrics.ButtonLarge + pad;
            var logHeight = 0f;
            if (_reportLogOpen)
            {
                // Leave room for the screen below at the window's minimum size.
                logHeight = Mathf.Clamp(body.height - height - logNoteHeight - 150f, 48f, ReportLogMaxHeight);
                height += logNoteHeight + logHeight + gap;
            }

            var card = new Rect(body.x, body.y, body.width, height);
            if (Event.current.type == EventType.Repaint)
            {
                SRTheme.Fill(card, colors.Bg);
                SRTheme.Fill(new Rect(card.x, card.yMax - 1f, card.width, 1f), colors.Border);
            }
            var x = card.x + pad;
            var y = card.y + pad;

            GUI.Label(new Rect(x, y, width, titleHeight), titleText, title);
            y += titleHeight;
            if (reasonHeight > 0f)
            {
                GUI.Label(new Rect(x, y, width, reasonHeight), reason, text);
                y += reasonHeight;
            }
            y += gap;

            // Expander for the exact text that will be sent, and how many more reports wait.
            var toggleLabel = ManagerText.ErShowLog;
            var toggleWidth = Mathf.Min(width, SRStyles.ButtonText.CalcSize(new GUIContent(toggleLabel)).x + 2f * SRMetrics.Space12);
            if (SRGui.Button(new Rect(x, y, toggleWidth, SRMetrics.ButtonMini), new GUIContent(toggleLabel), SRButtonKind.Ghost, SRButtonSize.Mini))
                Defer(() => { _reportLogOpen = !_reportLogOpen; });
            var waiting = InstallErrorReporter.PendingCount - 1;
            if (waiting > 0 && width - toggleWidth > 70f)
            {
                var more = new GUIStyle(SRStyles.Mini) { alignment = TextAnchor.MiddleRight };
                more.normal.textColor = colors.Text;
                GUI.Label(new Rect(x + toggleWidth, y, width - toggleWidth, SRMetrics.ButtonMini), ManagerText.ErMore.Replace("{n}", waiting.ToString()), more);
            }
            y += SRMetrics.ButtonMini + gap;

            if (_reportLogOpen)
            {
                var p = SRTheme.P;
                GUI.Label(new Rect(x, y, width, logNoteHeight), ManagerText.ErLogNote, text);
                y += logNoteHeight;
                var box = new Rect(x, y, width, logHeight);
                SRTheme.Box(box, p.SurfaceSunken, p.LineControl, SRMetrics.RadiusControl);
                var log = new GUIStyle(SRStyles.Mini) { wordWrap = true };
                log.normal.textColor = p.Text;
                var view = new Rect(0f, 0f, box.width - 18f, Mathf.Max(log.CalcHeight(new GUIContent(_excerpt), box.width - 18f) + 4f, box.height));
                _reportLogScroll = GUI.BeginScrollView(box, _reportLogScroll, view);
                GUI.Label(new Rect(4f, 2f, view.width - 8f, view.height), _excerpt, log);
                GUI.EndScrollView();
                y += logHeight + gap;
            }

            // The optional note.
            GUI.Label(new Rect(x, y, width, noteLabelHeight), ManagerText.ErNoteLabel, text);
            y += noteLabelHeight;
            var noteFrame = new Rect(x, y, width, ReportNoteHeight);
            SRTheme.Box(noteFrame, SRTheme.P.SurfaceSunken, SRTheme.P.LineControl, SRMetrics.RadiusControl);
            var inner = new Rect(noteFrame.x + 1f, noteFrame.y + 1f, noteFrame.width - 2f, noteFrame.height - 2f);
            using (new GUIEnabledScope(!_sendingReport))
            {
                var area = EnsureFeedbackAreaStyle();
                GUI.SetNextControlName(ReportNoteControl);
                _reportNote = EditorGUI.TextArea(inner, _reportNote ?? "", area);
                if (_reportNote.Length > FeedbackClient.MaxMessage) _reportNote = _reportNote.Substring(0, FeedbackClient.MaxMessage);
                if (_reportNote.Length == 0 && GUI.GetNameOfFocusedControl() != ReportNoteControl)
                    SRGui.Ink(new Rect(inner.x + 5f, inner.y + 4f, inner.width - 10f, inner.height - 8f), ManagerText.ErNoteHint, SRStyles.Muted);
            }
            y += ReportNoteHeight + gap;

            if (statusHeight > 0f)
            {
                GUI.Label(new Rect(x, y, width, statusHeight), status, text);
                y += statusHeight + gap;
            }

            // Buttons, right to left: Send, Don't send.
            var sendLabel = _sendingReport ? ManagerText.ErSending : ManagerText.ErSend;
            var sendWidth = Mathf.Max(64f, SRStyles.ButtonText.CalcSize(new GUIContent(sendLabel)).x + 2f * SRMetrics.Space12);
            var skipWidth = Mathf.Max(64f, SRStyles.ButtonText.CalcSize(new GUIContent(ManagerText.ErSkip)).x + 2f * SRMetrics.Space12);
            var sendRect = new Rect(card.xMax - pad - sendWidth, y, sendWidth, SRMetrics.ButtonLarge);
            var skipRect = new Rect(sendRect.x - SRMetrics.Space6 - skipWidth, y, skipWidth, SRMetrics.ButtonLarge);
            using (new GUIEnabledScope(!_sendingReport && _session != null))
            {
                if (SRGui.Button(sendRect, new GUIContent(sendLabel, _session == null ? ManagerText.ErSignIn : ""), SRButtonKind.Primary, SRButtonSize.Large))
                    Defer(SendReport);
            }
            using (new GUIEnabledScope(!_sendingReport))
            {
                if (SRGui.Button(skipRect, new GUIContent(ManagerText.ErSkip), SRButtonKind.Secondary, SRButtonSize.Default))
                    Defer(() => { GUIUtility.keyboardControl = 0; InstallErrorReporter.Dismiss(report); });
            }
            return height;
        }

        private async void SendReport()
        {
            var shot = _shot;
            if (InstallErrorReporter.Current == null || shot == null || _session == null || _sendingReport) return;
            _sendingReport = true;
            _reportError = null;
            Repaint();
            try
            {
                var id = await InstallErrorReporter.SendAsync(_session.token, shot, _reportNote);
                _reportNote = "";
                GUIUtility.keyboardControl = 0; // a focused field would keep showing the old text
                SetMessage(string.IsNullOrEmpty(id) ? ManagerText.ErThanks : ManagerText.ErSent.Replace("{id}", id.Length > 8 ? id.Substring(0, 8) : id), MessageType.Info);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                _reportError = e.Message;
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Sending the install error report failed: " + e.Message);
            }
            finally
            {
                _sendingReport = false;
                Repaint();
            }
        }
    }
}
