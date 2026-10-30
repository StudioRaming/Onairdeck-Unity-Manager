using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The error reports waiting for the person's answer. A report comes from a failed install (<see cref="FromFailure"/>),
    /// from the watch of an import (<see cref="InstallErrorWatch"/>) or from the button next to a red message; the window shows
    /// the first one as a card. Nothing is sent without a click: <see cref="SendAsync"/> goes through <see cref="FeedbackClient"/>,
    /// and a sent or dismissed signature is remembered per project (Library/OnAirDeck/InstallErrorReports.txt).
    /// The waiting reports are kept in SessionState, so a script reload does not lose them.
    /// </summary>
    internal static class InstallErrorReporter
    {
        private const string PendingKey = "OnAirDeck.UnityManager.InstallErrorPending";

        [Serializable]
        private sealed class PendingFile
        {
            public List<InstallErrorReport> Items = new List<InstallErrorReport>();
        }

        private static List<InstallErrorReport> _pending;
        private static InstallErrorLedger _ledger;

        /// <summary>The waiting list changed (a report arrived, was sent or dismissed).</summary>
        public static event Action Changed;

        /// <summary>The report the card shows, or null.</summary>
        public static InstallErrorReport Current
        {
            get
            {
                Load();
                return _pending.Count > 0 ? _pending[0] : null;
            }
        }

        /// <summary>How many reports wait, the one on the card included.</summary>
        public static int PendingCount
        {
            get
            {
                Load();
                return _pending.Count;
            }
        }

        /// <summary>
        /// Adds a report to the waiting list. The ledger is asked first, line by line, so an error that was already sent or
        /// dismissed in this project never rides along in another card: an automatic offer drops every known line; a
        /// <paramref name="manual"/> offer (the button next to a red message) drops only sent lines, and a report that is all sent
        /// is <see cref="OfferResult.Duplicate"/>. What is left joins the waiting card of the same product, or becomes one. A merge
        /// that adds no new line changes nothing (no save, no repaint, no window), so an exception thrown every frame costs little.
        /// </summary>
        public static OfferResult Offer(InstallErrorReport report, bool manual = false)
        {
            if (report == null) return OfferResult.Ignored;
            Load();
            var fresh = new List<InstallErrorEntry>();
            var sentLines = 0;
            foreach (var entry in report.Entries())
            {
                var state = Ledger.State(entry.Signature);
                if (state == InstallErrorLedger.Sent) sentLines++;
                if (state == null || (manual && state != InstallErrorLedger.Sent)) fresh.Add(entry);
            }
            if (fresh.Count == 0) return manual && sentLines > 0 ? OfferResult.Duplicate : OfferResult.Ignored;
            // A copy with a card id of its own, so no caller's report is ever a waiting card.
            var incoming = report.OnlyEntries(fresh);

            var existing = _pending.Find(r => r.SameProduct(incoming));
            if (existing != null)
            {
                if (!existing.Merge(incoming)) return OfferResult.Ignored;
                Save();
                Raise();
                return OfferResult.Shown;
            }
            _pending.Add(incoming);
            Save();
            Raise();
            EditorApplication.delayCall += ManagerWindow.ShowForErrorReport;
            return OfferResult.Shown;
        }

        /// <summary>"Don't send": remembers the signature of every error in the card so none is offered again in this project.</summary>
        public static void Dismiss(InstallErrorReport report)
        {
            Load();
            foreach (var entry in report.Entries()) Ledger.Add(entry.Signature, InstallErrorLedger.Dismissed);
            Remove(report);
        }

        /// <summary>
        /// Sends the frozen <paramref name="shot"/> as a bug through <see cref="FeedbackClient"/> (which redacts every field) and
        /// returns the report id. 401 → <see cref="SessionExpiredException"/>. On success the signature of every entry that was in the
        /// excerpt is recorded as sent, and every waiting card drops each entry with such a signature; entries the excerpt cap cut, and
        /// errors with another signature that joined while it was sending, stay.
        /// </summary>
        public static async Task<string> SendAsync(string sessionToken, InstallErrorSnapshot shot, string note)
        {
            var message = (note ?? "").Trim();
            if (message.Length == 0) message = shot.Summary;
            var body = FeedbackClient.Build(FeedbackKind.Bug, message, shot.ErrorCode, shot.Excerpt,
                FeedbackEnvironment.Collect(new[] { shot.PackageLabel }));
            var id = await FeedbackClient.SendAsync(sessionToken, body);
            Load();
            foreach (var entry in shot.Included) Ledger.Add(entry.Signature, InstallErrorLedger.Sent);
            _pending.RemoveAll(card => card.RemoveSigned(IsSent));
            Save();
            Raise();
            return id;
        }

        private static bool IsSent(string signature)
        {
            return Ledger.State(signature) == InstallErrorLedger.Sent;
        }

        /// <summary>
        /// The report for a failed install or import, or null when the failure is not a bug: a cancel, a declined or pending
        /// clean-up, an expired sign-in, a device-limit or update-required refusal (they have their own screens) and a missing
        /// dependency the person has to get first. A handled refusal is install.manager_failed; any other exception is install.exception.
        /// </summary>
        public static InstallErrorReport FromFailure(string productId, string product, string version, Exception e)
        {
            if (e == null) return null;
            if (e is OperationCanceledException || e is SessionExpiredException || e is LegacyCleanupRequiredException || e is LegacyHandoffPendingException) return null;
            if (e is DeviceLimitException || e is DeviceReleasedException || e is ManagerUpdateRequiredException) return null;
            var dependency = e as DependencyUnavailableException;
            if (dependency != null && dependency.Kind == DependencyErrorKind.GetOnAtelierFirst) return null;

            var handled = e is PackageInstallException || e is ApiException;
            var report = new InstallErrorReport
            {
                Product = product ?? "",
                ProductId = productId ?? "",
                Version = version ?? "",
                BaseCode = handled ? InstallErrorCodes.ManagerFailed : InstallErrorCodes.Exception,
                Reason = e.Message ?? "",
            };
            var detail = handled ? e.GetType().Name + ": " + e.Message : ConsoleErrorBuffer.FirstLines(e.ToString(), 20);
            report.AddOther(detail);
            return report;
        }

        /// <summary>A report for a red message of the window that no install produced (the button next to it).</summary>
        public static InstallErrorReport FromMessage(string message)
        {
            var report = new InstallErrorReport { Product = "Atelier Manager", ProductId = "com.studioraming.manager", Version = UpdateService.CurrentVersion ?? "", Reason = message ?? "" };
            report.AddOther(message ?? "");
            return report;
        }

        private static InstallErrorLedger Ledger
        {
            get
            {
                if (_ledger == null)
                    _ledger = new InstallErrorLedger(Path.Combine(PackageInstallService.ProjectRoot, "Library", "OnAirDeck", "InstallErrorReports.txt"));
                return _ledger;
            }
        }

        private static void Remove(InstallErrorReport report)
        {
            _pending.RemoveAll(r => r.SameProduct(report));
            Save();
            Raise();
        }

        private static void Raise()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        private static void Load()
        {
            if (_pending != null) return;
            _pending = new List<InstallErrorReport>();
            var raw = SessionState.GetString(PendingKey, "");
            if (raw.Length == 0) return;
            try
            {
                var file = JsonUtility.FromJson<PendingFile>(raw);
                if (file != null && file.Items != null) _pending = file.Items;
            }
            catch (Exception)
            {
                // An unreadable list is the same as an empty one.
            }
        }

        private static void Save()
        {
            SessionState.SetString(PendingKey, JsonUtility.ToJson(new PendingFile { Items = _pending }));
        }
    }

    /// <summary>What <see cref="InstallErrorReporter.Offer"/> did with a report.</summary>
    internal enum OfferResult { Shown, Ignored, Duplicate }
}
