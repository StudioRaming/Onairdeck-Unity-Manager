using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>What the Manager tells the server about a claimed import request.</summary>
    internal enum ImportReport
    {
        /// <summary>Nothing to post (the sign-in expired: the lease runs out and the request is listed again).</summary>
        None,
        Done,
        Failed,
        /// <summary>The person said no in the Manager's confirm.</summary>
        Declined,
        /// <summary>Not for this project, or the Manager could not start: the request is pending again.</summary>
        Release,
        /// <summary>Not over yet: a queued .unitypackage import or a hand-off install decides it later (<see cref="ImportRequestLogic.Settle"/>).</summary>
        Waiting,
    }

    /// <summary>The end of one claimed request: the report and, for a failure, its code (<c>[a-z0-9_]{1,40}</c>).</summary>
    internal sealed class ImportOutcome
    {
        public ImportReport Report;
        public string Code = "";

        public static ImportOutcome Done() { return new ImportOutcome { Report = ImportReport.Done }; }
        public static ImportOutcome Declined() { return new ImportOutcome { Report = ImportReport.Declined }; }
        public static ImportOutcome Release() { return new ImportOutcome { Report = ImportReport.Release }; }
        public static ImportOutcome Nothing() { return new ImportOutcome { Report = ImportReport.None }; }

        /// <summary>The .unitypackage files of <paramref name="packagePaths"/> are queued; their imports decide.</summary>
        public string[] WaitImports = new string[0];
        /// <summary>A hand-off install: done once <see cref="WaitPackageName"/> is in at <see cref="WaitPackageVersion"/>.</summary>
        public string WaitPackageName = "";
        public string WaitPackageVersion = "";

        public static ImportOutcome WaitForImports(IList<string> packagePaths)
        {
            var paths = new string[packagePaths.Count];
            packagePaths.CopyTo(paths, 0);
            return new ImportOutcome { Report = ImportReport.Waiting, WaitImports = paths };
        }

        public static ImportOutcome WaitForPackage(string name, string version)
        {
            return new ImportOutcome { Report = ImportReport.Waiting, WaitPackageName = name ?? "", WaitPackageVersion = version ?? "" };
        }

        public static ImportOutcome StillWaiting() { return new ImportOutcome { Report = ImportReport.Waiting }; }

        public static ImportOutcome Failed(string code)
        {
            return new ImportOutcome { Report = ImportReport.Failed, Code = ImportRequestLogic.IsValidCode(code) ? code : "install_failed" };
        }
    }

    /// <summary>What to do after a POST of the contract answered with an error.</summary>
    internal enum ImportErrorHandling
    {
        /// <summary>The request is gone, taken or finished elsewhere: forget it quietly.</summary>
        Drop,
        /// <summary>The server or the network did not answer: keep the record and try on a later poll.</summary>
        RetryLater,
    }

    /// <summary>The choice in the import confirm (<c>EditorUtility.DisplayDialogComplex</c>: 0 ok, 1 cancel, 2 alt).</summary>
    internal enum ImportChoice { Import = 0, Cancel = 1, NotThisProject = 2 }

    /// <summary>
    /// Pure rules of the one-button import (contract v1): when to poll, which request to take, what the confirm's answer and the
    /// install's end report, and what a held request becomes after a reload. No UnityEngine, so the rules are checked headless.
    /// </summary>
    internal static class ImportRequestLogic
    {
        /// <summary>The contract's floor: never list more often than every 15 s.</summary>
        public const long MinPollGapSeconds = 15;
        /// <summary>The list is polled every 60 s while the window is open.</summary>
        public const long PeriodSeconds = 60;
        /// <summary>The lease is 10 min; an install that runs longer renews it every 5 min.</summary>
        public const long RenewEverySeconds = 300;
        /// <summary>A record older than a day is not posted any more (the server expired it long ago).</summary>
        public const long MaxRecordAgeSeconds = 24 * 3600;
        /// <summary>A request that answered not_claimable is not tried again for the length of a lease.</summary>
        public const long BusySkipSeconds = 600;

        public const string PhaseClaimed = "claimed";
        public const string PhaseRunning = "running";
        public const string PhaseReport = "report";
        public const string PhaseWaiting = "waiting";
        /// <summary>A queued import or a hand-off that has not ended after this long is reported as interrupted.</summary>
        public const long MaxWaitSeconds = 3600;

        private static readonly Regex Code = new Regex("^[a-z0-9_]{1,40}$", RegexOptions.Compiled);

        internal enum Trigger { EditorFocus, WindowFocus, Periodic, Load }

        /// <summary>True when a poll may start now. Every trigger respects the 15 s floor; the periodic one runs only with the window open.</summary>
        public static bool Due(Trigger trigger, bool windowOpen, long now, long lastPoll)
        {
            var gap = now - lastPoll;
            if (lastPoll > 0 && gap < MinPollGapSeconds) return false;
            if (trigger == Trigger.Periodic) return windowOpen && (lastPoll <= 0 || gap >= PeriodSeconds);
            return true;
        }

        /// <summary>Why no request is taken now (null: go): signed out, compiling or importing, play mode, a legacy hand-off, the window busy.</summary>
        public static string Blocked(bool signedIn, bool compiling, bool playing, bool handoff, bool windowBusy)
        {
            if (!signedIn) return "signed_out";
            if (compiling) return "compiling";
            if (playing) return "play_mode";
            if (handoff) return "legacy_handoff";
            if (windowBusy) return "busy";
            return null;
        }

        public static bool IsValidCode(string code)
        {
            return !string.IsNullOrEmpty(code) && Code.IsMatch(code);
        }

        /// <summary>
        /// A request this Manager can act on: for Unity, with an id, of a known kind with its subject, not expired at
        /// <paramref name="nowUtc"/> (an unreadable expiry is left to the server).
        /// </summary>
        public static bool IsUsable(ImportRequest request, DateTime nowUtc)
        {
            if (request == null || string.IsNullOrEmpty(request.request_id)) return false;
            if (!string.IsNullOrEmpty(request.target) && request.target != "unity") return false;
            if (request.kind == "item") { if (string.IsNullOrEmpty(request.item_id)) return false; }
            else if (request.kind == "license") { if (string.IsNullOrEmpty(request.license_id)) return false; }
            else return false;
            DateTime expires;
            if (TryParseUtc(request.expires_at, out expires) && expires <= nowUtc) return false;
            return true;
        }

        /// <summary>The newest usable request (the server lists newest first) that is not in <paramref name="skipped"/>, or null.</summary>
        public static ImportRequest Pick(ImportRequest[] requests, DateTime nowUtc, ICollection<string> skipped)
        {
            if (requests == null) return null;
            foreach (var request in requests)
            {
                if (!IsUsable(request, nowUtc)) continue;
                if (skipped != null && skipped.Contains(request.request_id)) continue;
                return request;
            }
            return null;
        }

        /// <summary>The product name the confirm shows: the purchase's, else the license's, else "".</summary>
        public static string ProductName(ImportRequest request)
        {
            if (request == null) return "";
            if (HasPurchase(request) && !string.IsNullOrEmpty(request.purchase.name)) return request.purchase.name;
            if (request.licenses != null)
                foreach (var license in request.licenses)
                    if (license != null && !string.IsNullOrEmpty(license.name)) return license.name;
            return "";
        }

        /// <summary>
        /// True when the confirm must say that the first download makes the purchase non-refundable: an item whose Unity files are
        /// downloaded by the purchase (not through a license) and that was never downloaded.
        /// </summary>
        public static bool WarnsRefund(ImportRequest request)
        {
            return request != null && request.kind == "item" && HasPurchase(request) && !request.purchase.downloaded && request.purchase.HasUnityFiles;
        }

        /// <summary>The purchase row is present (JsonUtility turns an absent object into an empty one).</summary>
        public static bool HasPurchase(ImportRequest request)
        {
            return request != null && request.purchase != null && !string.IsNullOrEmpty(request.purchase.item_id);
        }

        /// <summary>The confirm's answer: Import goes on (null), Cancel is declined, Not this project gives the request back.</summary>
        public static ImportOutcome FromChoice(int choice)
        {
            switch ((ImportChoice)choice)
            {
                case ImportChoice.Import: return null;
                case ImportChoice.NotThisProject: return ImportOutcome.Release();
                default: return ImportOutcome.Declined();
            }
        }

        /// <summary>The POST action of a report ("" for none).</summary>
        public static string ActionOf(ImportReport report)
        {
            switch (report)
            {
                case ImportReport.Done: return "done";
                case ImportReport.Failed: return "failed";
                case ImportReport.Declined: return "declined";
                case ImportReport.Release: return "release";
                default: return "";
            }
        }

        public static ImportReport ReportOf(string action)
        {
            switch (action)
            {
                case "done": return ImportReport.Done;
                case "failed": return ImportReport.Failed;
                case "declined": return ImportReport.Declined;
                case "release": return ImportReport.Release;
                default: return ImportReport.None;
            }
        }

        /// <summary>
        /// What a held request becomes when the Manager loads and finds its record. "report": post what was decided. "claimed": the
        /// confirm never answered (the editor closed), give it back. "running": a package install whose package is now in at its
        /// version is done (the reload a package install causes ended the code that awaited it); anything else was interrupted.
        /// "waiting": <see cref="Settle"/> (possibly still waiting). A record older than <see cref="MaxRecordAgeSeconds"/> or without
        /// ids is forgotten (null).
        /// </summary>
        public static ImportOutcome Resume(ImportClaim claim, long now, Func<string, string> installedVersion, Func<string, string> importResult = null,
            Func<string, string> handoffState = null)
        {
            if (claim == null || string.IsNullOrEmpty(claim.request_id) || string.IsNullOrEmpty(claim.claim_id)) return null;
            if (claim.claimed_at > 0 && now - claim.claimed_at > MaxRecordAgeSeconds) return null;
            switch (claim.phase)
            {
                case PhaseReport:
                    var report = ReportOf(claim.action);
                    if (report == ImportReport.None) return null;
                    return report == ImportReport.Failed ? ImportOutcome.Failed(claim.code) : new ImportOutcome { Report = report };
                case PhaseClaimed:
                    return ImportOutcome.Release();
                case PhaseWaiting:
                    return Settle(claim, now, importResult, installedVersion, handoffState);
                case PhaseRunning:
                    // A package install: imports queued meanwhile are a hand-off installer's, so its status and the version decide.
                    if (!string.IsNullOrEmpty(claim.package_name) && !string.IsNullOrEmpty(claim.package_version))
                    {
                        var state = handoffState != null ? handoffState(claim.package_name) ?? "" : "";
                        if (state.Length > 0)
                        {
                            claim.wait_package = true;
                            return Settle(claim, now, importResult, installedVersion, handoffState);
                        }
                    }
                    // Imports were queued before the reload ended the install path (its license step ran before them): they decide.
                    else if (claim.wait_imports != null && claim.wait_imports.Length > 0) return Settle(claim, now, importResult, installedVersion, handoffState);
                    if (!string.IsNullOrEmpty(claim.package_name) && !string.IsNullOrEmpty(claim.package_version) && installedVersion != null
                        && string.Equals(installedVersion(claim.package_name) ?? "", claim.package_version, StringComparison.OrdinalIgnoreCase))
                        return ImportOutcome.Done();
                    return ImportOutcome.Failed("interrupted");
                default:
                    return null;
            }
        }

        /// <summary>
        /// A waiting request's result, or <see cref="ImportOutcome.StillWaiting"/>. Imports (<paramref name="importResult"/> gives
        /// pending, imported, cancelled, failed, missing or "" for unknown): all imported is done; one cancelled in Unity's import
        /// window is failed "cancelled"; one failed or missing is failed "import_failed"; one this editor session no longer knows (an
        /// editor restart) is failed "interrupted". A hand-off: done once the package is in at the version. Anything still open after
        /// <see cref="MaxWaitSeconds"/> is failed "interrupted".
        /// </summary>
        /// <param name="handoffState">The hand-off of a package: "done", "stopped", "pending" or "" when there is no hand-off record.</param>
        public static ImportOutcome Settle(ImportClaim claim, long now, Func<string, string> importResult, Func<string, string> installedVersion,
            Func<string, string> handoffState = null)
        {
            var since = claim.wait_since > 0 ? claim.wait_since : claim.claimed_at;
            var timedOut = since > 0 && now - since > MaxWaitSeconds;
            if (claim.wait_package)
            {
                // The installer's own result decides; the version only confirms a finished one (a package can be in place while the
                // installer still checks it, or fail after it was put there).
                var state = handoffState != null ? handoffState(claim.package_name) ?? "" : "";
                var versionIn = installedVersion != null && !string.IsNullOrEmpty(claim.package_name)
                    && string.Equals(installedVersion(claim.package_name) ?? "", claim.package_version, StringComparison.OrdinalIgnoreCase);
                if (state == "stopped") return ImportOutcome.Failed("install_failed");
                if (state == "done") return versionIn ? ImportOutcome.Done() : ImportOutcome.Failed("install_failed");
                if (state.Length == 0 && versionIn) return ImportOutcome.Done();
                return timedOut ? ImportOutcome.Failed("interrupted") : ImportOutcome.StillWaiting();
            }
            var imports = claim.wait_imports ?? new string[0];
            if (imports.Length == 0) return ImportOutcome.Done();
            var pending = false;
            foreach (var path in imports)
            {
                var result = importResult != null ? importResult(path) ?? "" : "";
                if (result == "pending") { pending = true; continue; }
                if (result == "imported") continue;
                if (result == "cancelled") return ImportOutcome.Failed("cancelled");
                if (result == "failed" || result == "missing") return ImportOutcome.Failed("import_failed");
                return ImportOutcome.Failed("interrupted");
            }
            if (!pending) return ImportOutcome.Done();
            return timedOut ? ImportOutcome.Failed("interrupted") : ImportOutcome.StillWaiting();
        }

        /// <summary>
        /// An error answer of the contract: 401 (sign-in; the lease runs out by itself), 404, 409 (not_claimable, lease_lost,
        /// not_available) and 410 drop the request; 400 is a bug of this side and drops it too (it would fail again); 429, 5xx and
        /// no answer at all are tried again later.
        /// </summary>
        public static ImportErrorHandling OnError(long status)
        {
            if (status == 0 || status == 429 || status >= 500) return ImportErrorHandling.RetryLater;
            return ImportErrorHandling.Drop;
        }

        public static bool TryParseUtc(string text, out DateTime utc)
        {
            return DateTime.TryParse(text ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out utc);
        }
    }
}
