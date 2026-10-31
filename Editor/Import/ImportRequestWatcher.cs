using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The Unity half of the website's "Unity로 가져오기" button (contract v1, plugin-import-requests). There is no wake-up link
    /// for Unity, so the Manager polls: when the editor regains focus, when the window is focused and every 60 s while it is open,
    /// never more often than every 15 s, signed in only, and not while scripts compile, play mode runs, a legacy hand-off waits or
    /// the window is busy. It claims the newest request, always asks in a dialog that names this project (Import / Not this project
    /// / Cancel), runs the window's own install path through <see cref="AtelierManagerApi.RunImport"/> and reports done, failed,
    /// declined or release. The held request is kept in Library/OnAirDeck/import-claim.json, so the reload a package install causes,
    /// or an editor restart, still ends in a report.
    /// </summary>
    [InitializeOnLoad]
    internal static class ImportRequestWatcher
    {
        private const string LastPollKey = "OnAirDeck.UnityManager.ImportLastPoll";
        private const string SkipKey = "OnAirDeck.UnityManager.ImportSkipped";

        private static bool _windowOpen;
        private static bool _wasActive;
        private static bool _busy;
        private static bool _renewing;
        private static bool _loadChecked;
        private static bool _focusRequested;
        // The request whose install runs now (renewed from the update tick without reading the file every frame).
        private static ImportClaim _running;

        /// <summary>The confirm (title, message, ok, cancel, alt → 0, 1, 2). Tests replace it; everything else calls the real dialog.</summary>
        internal static Func<string, string, string, string, string, int> ConfirmDialog = EditorUtility.DisplayDialogComplex;

        static ImportRequestWatcher()
        {
            _wasActive = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;
        }

        /// <summary>The window tells when it is open, so the 60 s poll runs only then.</summary>
        public static void SetWindowOpen(bool open)
        {
            _windowOpen = open;
        }

        /// <summary>Call when the window gets focus.</summary>
        public static void NotifyWindowFocused()
        {
            _focusRequested = true;
        }

        private static string ClaimPath
        {
            get { return Path.Combine(PackageInstallService.ProjectRoot, "Library", "OnAirDeck", "import-claim.json"); }
        }

        private static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        // ------------------------------------------------------------ when to poll

        /// <summary>EditMode tests stop the automatic polls while they drive <see cref="PollNowAsync"/> against a fake server.</summary>
        internal static bool Paused;

        private static void Tick()
        {
            if (Paused) return;
            var active = UnityEditorInternal.InternalEditorUtility.isApplicationActive;
            var regained = active && !_wasActive;
            _wasActive = active;

            RenewIfDue();
            if (_busy) return;

            ImportRequestLogic.Trigger trigger;
            if (!_loadChecked) trigger = ImportRequestLogic.Trigger.Load;
            else if (regained) trigger = ImportRequestLogic.Trigger.EditorFocus;
            else if (_focusRequested) trigger = ImportRequestLogic.Trigger.WindowFocus;
            else trigger = ImportRequestLogic.Trigger.Periodic;

            var now = Now();
            long last;
            if (!long.TryParse(SessionState.GetString(LastPollKey, "0"), out last)) last = 0;
            if (!ImportRequestLogic.Due(trigger, _windowOpen, now, last)) return;

            var session = SessionStore.Load();
            var signedIn = session != null && !session.IsExpired && !string.IsNullOrEmpty(session.token);
            var busy = AtelierManagerApi.WindowBusy;
            var blocked = ImportRequestLogic.Blocked(signedIn, EditorApplication.isCompiling || EditorApplication.isUpdating,
                EditorApplication.isPlayingOrWillChangePlaymode, LegacyHandoffWaiting(), busy != null && busy());
            if (blocked != null) return;

            _loadChecked = true;
            _focusRequested = false;
            SessionState.SetString(LastPollKey, now.ToString());
            _busy = true;
            PollAsync(session.token, trigger == ImportRequestLogic.Trigger.Load).ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static bool LegacyHandoffWaiting()
        {
            try { return LegacyHandoff.IsWaiting(PackageInstallService.ProjectRoot); }
            catch (Exception) { return false; }
        }

        // ------------------------------------------------------------ the flow

        /// <summary>One poll now, without the timing and busy gates (EditMode tests). Returns when the request it took is reported.</summary>
        internal static Task PollNowAsync(string token)
        {
            _busy = true;
            return PollAsync(token, false);
        }

        private static async Task PollAsync(string token, bool onLoad)
        {
            try
            {
                // A request this project still holds is finished first, and nothing new is taken meanwhile.
                var held = LoadClaim();
                if (held != null)
                {
                    var outcome = ImportRequestLogic.Resume(held, Now(), name => InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, name));
                    if (outcome == null) DeleteClaim();
                    else await FinishAsync(token, held, outcome);
                    return;
                }
                if (onLoad && !_windowOpen) return;   // the load check only finishes a held request; focus and the window poll for new ones
                var list = await ImportRequestApi.ListAsync(token);
                var request = ImportRequestLogic.Pick(list, DateTime.UtcNow, Skipped());
                if (request != null) await HandleAsync(token, request);
            }
            catch (SessionExpiredException)
            {
                // The window shows the signed-out state; a held record waits for the next sign-in.
            }
            catch (ApiException)
            {
                // 429, 5xx or offline: quiet, the next poll tries again.
            }
            catch (Exception e)
            {
                ManagerErrorWatch.OfferCaught("Import request", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Import request failed: " + e.Message);
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task HandleAsync(string token, ImportRequest request)
        {
            ImportActionResponse claimed;
            try
            {
                claimed = await ImportRequestApi.PostAsync(token, "claim", request.request_id, "", "");
            }
            catch (ImportRequestException e)
            {
                // Another Manager holds it, it expired, or it can no longer be served (refunded, revoked): leave it alone for a while.
                if (ImportRequestLogic.OnError(e.Status) == ImportErrorHandling.Drop) Skip(request.request_id, ImportRequestLogic.BusySkipSeconds);
                return;
            }
            if (claimed == null || string.IsNullOrEmpty(claimed.claim_id)) return;

            var now = Now();
            var product = ImportRequestLogic.ProductName(request);
            var claim = new ImportClaim
            {
                request_id = request.request_id, claim_id = claimed.claim_id, phase = ImportRequestLogic.PhaseClaimed,
                product = product, claimed_at = now, renewed_at = now,
            };
            SaveClaim(claim);

            var open = AtelierManagerApi.OpenWindow;
            if (open != null) open();
            var choice = ConfirmDialog(ManagerText.ImpTitle.Replace("{n}", product), ConfirmText(request, product),
                ManagerText.ImpImport, ManagerText.ImpCancel, ManagerText.ImpNotHere);
            var outcome = ImportRequestLogic.FromChoice(choice);
            if (choice == (int)ImportChoice.NotThisProject) Skip(request.request_id, long.MaxValue / 2);
            if (outcome == null)
            {
                claim.phase = ImportRequestLogic.PhaseRunning;
                SaveClaim(claim);
                var run = AtelierManagerApi.RunImport;
                _running = claim;
                try
                {
                    outcome = run == null
                        ? ImportOutcome.Release()
                        : await run(request, (package, version) =>
                        {
                            claim.package_name = package ?? "";
                            claim.package_version = version ?? "";
                            SaveClaim(claim);
                        });
                }
                finally
                {
                    _running = null;
                }
                if (outcome == null) outcome = ImportOutcome.Failed("install_failed");
            }
            await FinishAsync(token, claim, outcome);
        }

        /// <summary>Records the result first (a reload may end the request), then posts it; the record goes once the server took it or refused it for good.</summary>
        private static async Task FinishAsync(string token, ImportClaim claim, ImportOutcome outcome)
        {
            var action = ImportRequestLogic.ActionOf(outcome.Report);
            if (action.Length == 0)
            {
                DeleteClaim();
                return;
            }
            claim.phase = ImportRequestLogic.PhaseReport;
            claim.action = action;
            claim.code = outcome.Report == ImportReport.Failed ? outcome.Code : "";
            SaveClaim(claim);
            try
            {
                await ImportRequestApi.PostAsync(token, action, claim.request_id, claim.claim_id, claim.code);
                DeleteClaim();
            }
            catch (ImportRequestException e)
            {
                if (ImportRequestLogic.OnError(e.Status) == ImportErrorHandling.Drop) DeleteClaim();
            }
        }

        /// <summary>An install that runs longer than the lease keeps it: renew every 5 min while the record says running.</summary>
        private static void RenewIfDue()
        {
            var claim = _running;
            if (_renewing || claim == null) return;
            var now = Now();
            if (now - claim.renewed_at < ImportRequestLogic.RenewEverySeconds) return;
            var session = SessionStore.Load();
            if (session == null || string.IsNullOrEmpty(session.token)) return;
            _renewing = true;
            claim.renewed_at = now;
            SaveClaim(claim);
            RenewAsync(session.token, claim).ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task RenewAsync(string token, ImportClaim claim)
        {
            try { await ImportRequestApi.PostAsync(token, "renew", claim.request_id, claim.claim_id, ""); }
            catch (Exception) { /* lease_lost or offline: the report at the end tells what the server still accepts */ }
            finally { _renewing = false; }
        }

        private static string ConfirmText(ImportRequest request, string product)
        {
            var root = PackageInstallService.ProjectRoot;
            var text = ManagerText.ImpBody.Replace("{n}", product).Replace("{p}", Path.GetFileName(root)).Replace("{path}", root);
            if (ImportRequestLogic.WarnsRefund(request)) text += "\n\n" + ManagerText.ImpRefund;
            return text;
        }

        // ------------------------------------------------------------ the held request and the skip list

        internal static ImportClaim LoadClaim()
        {
            try
            {
                var path = ClaimPath;
                if (!File.Exists(path)) return null;
                var claim = JsonUtility.FromJson<ImportClaim>(File.ReadAllText(path));
                return claim != null && !string.IsNullOrEmpty(claim.request_id) ? claim : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void SaveClaim(ImportClaim claim)
        {
            AtomicFile.Write(ClaimPath, JsonUtility.ToJson(claim, true));
        }

        private static void DeleteClaim()
        {
            try { if (File.Exists(ClaimPath)) File.Delete(ClaimPath); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not remove the import record: " + e.Message); }
        }

        /// <summary>Ids not to take now: "not this project" for the rest of the editor session, a busy or gone one for a lease.</summary>
        private static HashSet<string> Skipped()
        {
            var now = Now();
            var set = new HashSet<string>();
            foreach (var pair in ReadSkips()) if (pair.Value > now) set.Add(pair.Key);
            return set;
        }

        private static void Skip(string requestId, long seconds)
        {
            var skips = ReadSkips();
            var now = Now();
            skips[requestId] = now + seconds;
            var parts = new List<string>();
            foreach (var pair in skips) if (pair.Value > now) parts.Add(pair.Key + "=" + pair.Value);
            SessionState.SetString(SkipKey, string.Join(";", parts.ToArray()));
        }

        private static Dictionary<string, long> ReadSkips()
        {
            var result = new Dictionary<string, long>();
            foreach (var part in SessionState.GetString(SkipKey, "").Split(';'))
            {
                var eq = part.IndexOf('=');
                long until;
                if (eq > 0 && long.TryParse(part.Substring(eq + 1), out until)) result[part.Substring(0, eq)] = until;
            }
            return result;
        }
    }
}
