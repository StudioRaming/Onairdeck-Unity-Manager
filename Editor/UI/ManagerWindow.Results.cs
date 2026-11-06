using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Owner report 2026-11-01 ("0개를 마쳤고 2개는 못 했어요" with no reason; the clean-up banner's install did nothing): every failed install
    /// keeps its reason and next step on its row, a batch says per item what finished and what did not, a second press retries only the
    /// failed items, a VCC/BOOTH install of MingToon is switched to the Manager with one question, and MingToon add-ons wait for the core.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        /// <summary>Entry key to the reason (and next step) of its last failed install; cleared when it installs.</summary>
        [NonSerialized] private readonly Dictionary<string, string> _lastFailure = new Dictionary<string, string>();
        /// <summary>The failed entries of the last batch, selected again so the batch button retries only them.</summary>
        [NonSerialized] private HashSet<string> _retryKeys = new HashSet<string>();
        /// <summary>The "core first?" question (title, message, ok, cancel); EditMode tests answer it.</summary>
        internal static Func<string, string, string, string, bool> CoreFirstDialog = EditorUtility.DisplayDialog;

        private void RememberFailure(string key, Exception e)
        {
            if (string.IsNullOrEmpty(key) || e == null) return;
            var language = ManagerText.Language;
            var kind = InstallResultText.Classify(e);
            if (kind == InstallFailureKind.Cancelled) { _lastFailure.Remove(key); return; }
            var next = InstallResultText.Next(language, kind);
            _lastFailure[key] = InstallResultText.Reason(language, e) + (next.Length > 0 ? "\n" + next : "");
        }

        // ---------- VCC / BOOTH installer -> Manager ----------

        /// <summary>
        /// Before installing a Manager-only package (MingToon core or URP add-on): when VCC (or the BOOTH installer, which installs the same
        /// way) still lists MingToon in Packages/vpm-manifest.json, asks once and takes those entries out (backup + undo, VpmMigration), so the
        /// install that follows is the Manager's and VCC cannot put the old version back. Returns false when the user said no or the move
        /// failed (the reason is shown); <paramref name="move"/> is null when nothing needed moving.
        /// </summary>
        private bool SwitchFromVcc(ICollection<string> packages, out VpmMove move)
        {
            move = null;
            var wanted = false;
            foreach (var package in packages)
                if (Array.IndexOf(OnAirDeckConfig.ManagerOnlyVpmPackages, package) >= 0 || CoreAddons.CoreOf(package, "").Length > 0) wanted = true;
            if (!wanted) return true;
            var root = PackageInstallService.ProjectRoot;
            var held = VpmMigration.Find(root);
            if (held.Count == 0) return true;
            var lines = held.ConvertAll(h => ManagerText.VcLine.Replace("{n}", ManagerText.VcName(h.Package)).Replace("{v}", h.FolderVersion)
                .Replace("{l}", h.ListedVersion.Length > 0 ? h.ListedVersion : "-"));
            var ask = ManagerText.VsBody.Replace("{list}", string.Join("\n", lines.ToArray()));
            if (!EditorUtility.DisplayDialog(ManagerText.VsTitle, ask, ManagerText.VsYes, ManagerText.Cancel))
            {
                SetMessage(ManagerText.Declined, MessageType.None);
                return false;
            }
            try
            {
                move = VpmMigration.Move(root, held.ConvertAll(h => h.Package), DateTime.Now);
                return true;
            }
            catch (Exception e)
            {
                SetMessage(ManagerText.VsFailed.Replace("{r}", e.Message), MessageType.Warning);
                return false;
            }
        }

        /// <summary>The install after <see cref="SwitchFromVcc"/> did not happen: the entries go back, so the project is as it was.</summary>
        private void UndoSwitch(VpmMove move)
        {
            if (move == null) return;
            try { VpmMigration.Undo(PackageInstallService.ProjectRoot, move); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not put the VCC entries back: " + e.Message); }
        }

        /// <summary>
        /// The install after <see cref="SwitchFromVcc"/> happened: the switch is final (the user already said yes to it), so no undo bar is
        /// left whose Undo would hand MingToon back to VCC (owner decision 2026-10-31: no more MingToon updates through VCC).
        /// </summary>
        private void KeepSwitch(VpmMove move)
        {
            if (move == null) return;
            try { VpmMigration.Keep(move); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not keep the move from VCC: " + e.Message); }
        }

        // ---------- add-ons wait for their core ----------

        /// <summary>
        /// The core is in Packages/, its old Assets folder or a file core's marker is there, or a file core's import is queued ahead
        /// (the add-on then queues behind it).
        /// </summary>
        private static bool CoreInstalled(string core)
        {
            return CoreImports.HereOrComing(PackageInstallService.ProjectRoot, core);
        }

        /// <summary>
        /// The list row that installs <paramref name="core"/>, or null (<see cref="CoreAddons.CoreEntryOf"/>): the core package's license,
        /// a license that ships a file core with a file version, or a purchase row that carries the file core (most VModular buyers).
        /// </summary>
        private CatalogEntry CoreEntry(string core)
        {
            return CoreAddons.CoreEntryOf(_entries, core, e => OfferedPackage(e) != null, e => OfferedFileVersion(e) != null);
        }

        private AddonState AddonStateOf(CatalogEntry entry)
        {
            var core = CoreAddons.CoreOf(entry);
            if (core.Length == 0) return AddonState.Ready;
            return CoreAddons.StateOf(core, CoreInstalled(core), CoreEntry(core) != null);
        }

        /// <summary>The install button text of an add-on whose core is missing ("MingToon Core를 먼저 설치해요"), or <paramref name="label"/>.</summary>
        private string AddonLabel(CatalogEntry entry, string label)
        {
            if (label == null || AddonStateOf(entry) == AddonState.Ready) return label;
            return ManagerText.CoreFirstBtn.Replace("{c}", CoreAddons.CoreName(CoreAddons.CoreOf(entry)));
        }

        /// <summary>
        /// An add-on install while its core is missing. A package add-on that pins its core (URP) brings the core in the same transaction
        /// (dependency first), so it only needs the question; a file add-on (MLC) installs the core first, then itself. Without a core to
        /// install, nothing changes and the reason is shown. Returns true when it handled the click.
        /// </summary>
        private bool InstallAddonCoreFirst(CatalogEntry entry)
        {
            var state = AddonStateOf(entry);
            if (state == AddonState.Ready) return false;
            var core = CoreAddons.CoreOf(entry);
            var coreName = CoreAddons.CoreName(core);
            if (state == AddonState.CoreMissing)
            {
                var text = ManagerText.CoreMissing.Replace("{a}", entry.Name).Replace("{c}", coreName);
                _lastFailure[entry.Key] = text;
                SetMessage(text, MessageType.Warning);
                return true;
            }
            var coreEntry = CoreEntry(core);
            if (!CoreFirstDialog(ManagerText.CoreFirstBtn.Replace("{c}", coreName), CoreFirstText(entry, coreEntry, coreName),
                ManagerText.BInst, ManagerText.Cancel)) return true;
            InstallCoreThenAddon(entry, coreEntry);
            return true;
        }

        /// <summary>The core-first question; it is the only confirm of both installs, so it carries the refund notice when one is a first download.</summary>
        private static string CoreFirstText(CatalogEntry addon, CatalogEntry coreEntry, string coreName)
        {
            var text = ManagerText.CoreFirstAsk.Replace("{a}", addon.Name).Replace("{c}", coreName);
            return ManagerCatalog.CoreFirstNeedsRefundNotice(addon, coreEntry) ? text + "\n\n" + ManagerText.DlgRefund : text;
        }

        /// <summary>
        /// Every way in to a file or purchase add-on (the row, a version in the history, the one-button import) waits for the core: an
        /// add-on without it does not compile. The core goes first when this account has it, else the reason shows and nothing is
        /// downloaded. Null when the core is here, or the product is no add-on.
        /// </summary>
        private async Task<ImportOutcome> CoreFirstOrNullAsync(CatalogEntry entry, LicenseVersionEntry file)
        {
            var core = CoreAddons.CoreOf(entry);
            if (core.Length == 0 || CoreInstalled(core)) return null;
            var coreName = CoreAddons.CoreName(core);
            var coreEntry = CoreEntry(core);
            if (coreEntry == null)
            {
                var missing = ManagerText.CoreMissing.Replace("{a}", entry.Name).Replace("{c}", coreName);
                _lastFailure[entry.Key] = missing;
                SetMessage(missing, MessageType.Warning);
                return ImportOutcome.Failed("dependency_missing");
            }
            if (!CoreFirstDialog(ManagerText.CoreFirstBtn.Replace("{c}", coreName), CoreFirstText(entry, coreEntry, coreName),
                ManagerText.BInst, ManagerText.Cancel))
            {
                SetMessage(ManagerText.Declined, MessageType.None);
                return ImportOutcome.Declined();
            }
            return await InstallCoreThenAddonAsync(entry, coreEntry, file);
        }

        private async void InstallCoreThenAddon(CatalogEntry addon, CatalogEntry coreEntry)
        {
            var package = OfferedPackage(addon);
            if (package != null)
            {
                // The add-on's exact pin installs the core first in the same transaction.
                await DoInstallAsync(addon, package, true, null);
                return;
            }
            await InstallCoreThenAddonAsync(addon, coreEntry, null);
        }

        /// <summary>
        /// The core, then the file add-on (<paramref name="file"/>, or the one it offers) or the purchase add-on (its download). Returns how
        /// the add-on (or the core) ended.
        /// </summary>
        private async Task<ImportOutcome> InstallCoreThenAddonAsync(CatalogEntry addon, CatalogEntry coreEntry, LicenseVersionEntry file)
        {
            var coreVersion = coreEntry != null ? OfferedPackage(coreEntry) : null;
            // A file core (VModular 1.0.0): its .unitypackage version, imported into Assets/.
            var coreFile = coreEntry != null && coreEntry.IsLicense && coreVersion == null ? OfferedFileVersion(coreEntry) : null;
            // A file core that reaches the buyer as a purchase row (the VModular base bought with the product, G4 retest D1).
            var corePurchase = coreEntry != null && !coreEntry.IsLicense && coreEntry.Purchase != null;
            var purchase = addon.Kind == FavoriteKind.Purchase;
            if (!purchase && file == null) file = OfferedFileVersion(addon);
            if ((coreVersion == null && coreFile == null && !corePurchase) || (!purchase && file == null)) return ImportOutcome.Failed("not_available");
            // The core and the add-on import without the import window; an interactive import still waiting in the queue (another
            // purchase's import window) cannot be mixed with them, so nothing starts until it is done (review R3).
            if ((coreFile != null || corePurchase) && !CoreAddons.CanJoinImportQueue(PackageImportQueue.PendingCount, PackageImportQueue.QueuedInteractive, false))
            {
                SetMessage(ManagerText.BusyNotStarted.Replace("{n}", addon.Name), MessageType.Warning);
                return ImportOutcome.Release();
            }
            // Reloads wait until the add-on is in (as in a batch and the one-button import): the core's refresh would end this method
            // before the add-on. The record covers what holding reloads cannot: a core that comes in by its product installer after a
            // reload, or a reload Unity forces anyway; the window then installs the add-on once the core is there.
            var core = CoreAddons.CoreOf(addon);
            _addonLive.Add(addon.Key);
            SavePendingAddon(new PendingAddon { AddonKey = addon.Key, LicenseId = addon.LicenseId, Core = core, Name = addon.Name, SinceUnix = NowUnix() });
            var keepRecord = false;
            EditorApplication.LockReloadAssemblies();
            try
            {
                ImportOutcome outcome;
                if (coreVersion != null) outcome = await DoInstallAsync(coreEntry, coreVersion, true, null);
                else if (coreFile != null) outcome = await DoInstallFileAsync(coreEntry, coreFile, true);
                else
                {
                    // The purchase row's download asks its own refund question when it was never downloaded; its import is not
                    // interactive, so the add-on can queue right behind it.
                    outcome = await DoDownloadAsync(coreEntry, false, false);
                    if (outcome.Report == ImportReport.Waiting) CoreImports.Note(core, CoreAddons.CorePackagesOf(core, coreEntry.Purchase, outcome.WaitImports));
                }
                // A file core's .unitypackage imports one tick later: the add-on queues behind it, and the queue imports both in order.
                if ((coreFile != null || corePurchase) && outcome.Report == ImportReport.Waiting && CoreImports.Coming(core))
                {
                    RemovePendingAddon(addon.Key);
                    return purchase ? await DoDownloadAsync(addon, true) : await DoInstallFileAsync(addon, file, true);
                }
                if (outcome.Report != ImportReport.Done)
                {
                    // A hand-off puts the core in after the next reload: the add-on follows then. Anything else: the core's row says why.
                    keepRecord = outcome.Report == ImportReport.Waiting;
                    if (!keepRecord) _lastFailure[addon.Key] = ManagerText.CoreNotIn.Replace("{a}", addon.Name).Replace("{c}", CoreAddons.CoreName(core));
                    return outcome;
                }
                RemovePendingAddon(addon.Key);
                if (!CoreInstalled(core))
                {
                    // Reported done but not here (never loop back into the question): the reason, nothing more.
                    _lastFailure[addon.Key] = ManagerText.CoreNotIn.Replace("{a}", addon.Name).Replace("{c}", CoreAddons.CoreName(core));
                    return ImportOutcome.Failed("dependency_missing");
                }
                // The person already answered: the core-first question, and for a purchase the refund question before it.
                return purchase ? await DoDownloadAsync(addon, true) : await DoInstallFileAsync(addon, file, true);
            }
            finally
            {
                if (!keepRecord) RemovePendingAddon(addon.Key);
                _addonLive.Remove(addon.Key);
                EditorApplication.UnlockReloadAssemblies();
            }
        }

        // ---------- what a script reload must not drop ----------

        private const string PendingAddonsKey = ReloadCarry.PendingAddonsKey;
        private const string BatchRowsKey = ReloadCarry.BatchRowsKey;
        /// <summary>Add-ons a run in this domain is still handling: not resumed from the record (after a reload the set is empty).</summary>
        [NonSerialized] private readonly HashSet<string> _addonLive = new HashSet<string>();

        private static long NowUnix()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        private static void SavePendingAddon(PendingAddon addon)
        {
            SessionState.SetString(PendingAddonsKey, ReloadCarry.AddAddon(SessionState.GetString(PendingAddonsKey, ""), addon));
        }

        private static void RemovePendingAddon(string addonKey)
        {
            var text = SessionState.GetString(PendingAddonsKey, "");
            if (text.Length == 0) return;
            text = ReloadCarry.RemoveAddon(text, addonKey);
            if (text.Length == 0) SessionState.EraseString(PendingAddonsKey);
            else SessionState.SetString(PendingAddonsKey, text);
        }

        /// <summary>
        /// The list is loaded and nothing runs: an add-on that a run (core first, then the add-on) recorded and did not reach installs now
        /// when its core is here, or says why not when the core is not. One at a time; the next one follows the refresh of this one.
        /// </summary>
        private void ResumePendingAddons()
        {
            if (_session == null || Busy || _entries == null) return;
            var text = SessionState.GetString(PendingAddonsKey, "");
            if (text.Length == 0) return;
            var records = ReloadCarry.ParseAddons(text).FindAll(a => !_addonLive.Contains(a.AddonKey));
            if (records.Count == 0) return;
            var install = new List<PendingAddon>();
            var missing = new List<PendingAddon>();
            ReloadCarry.Split(records, NowUnix(), CoreInstalled, install, missing);
            foreach (var record in records) if (!install.Contains(record)) RemovePendingAddon(record.AddonKey);
            foreach (var record in missing)
            {
                var reason = ManagerText.CoreNotIn.Replace("{a}", record.Name).Replace("{c}", CoreAddons.CoreName(record.Core));
                _lastFailure[record.AddonKey] = reason;
                SetMessage(reason, MessageType.Warning);
            }
            foreach (var record in install)
            {
                RemovePendingAddon(record.AddonKey);
                var entry = FindEntry(record.AddonKey) ?? (_entries.Find(e => e.IsLicense && e.HoldsLicense(record.LicenseId)));
                if (entry == null) continue;
                // A purchase add-on (VModular Plus): its download, asked for before the reload.
                if (entry.Kind == FavoriteKind.Purchase)
                {
                    if (entry.Purchase == null || !entry.Purchase.CanDownload) continue;
                    Debug.Log(OnAirDeckConfig.LogPrefix + ManagerText.AddonResume.Replace("{a}", entry.Name).Replace("{c}", CoreAddons.CoreName(record.Core)));
                    ResumePurchaseAddon(entry);
                    return;
                }
                var file = OfferedFileVersion(entry);
                if (file == null) continue;
                Debug.Log(OnAirDeckConfig.LogPrefix + ManagerText.AddonResume.Replace("{a}", entry.Name).Replace("{c}", CoreAddons.CoreName(record.Core)));
                ResumeAddonInstall(entry, file);
                return;
            }
        }

        private async void ResumeAddonInstall(CatalogEntry entry, LicenseVersionEntry file)
        {
            await DoInstallFileAsync(entry, file, true);
        }

        private async void ResumePurchaseAddon(CatalogEntry entry)
        {
            await DoDownloadAsync(entry, true);
        }

        /// <summary>The row reasons and retry selection of the batch that ended just before this reload.</summary>
        private void RestoreBatchRows()
        {
            var text = SessionState.GetString(BatchRowsKey, "");
            if (text.Length == 0) return;
            SessionState.EraseString(BatchRowsKey);
            var retry = new List<string>();
            var rows = new List<CarriedRow>();
            if (!ReloadCarry.ParseRows(text, NowUnix(), retry, rows)) return;
            foreach (var row in rows)
            {
                if (row.Cleanup) _cleanupRequired[row.Key] = row.Text;
                else _lastFailure[row.Key] = row.Text;
            }
            _retryKeys = new HashSet<string>(retry);
            foreach (var key in retry) _selected.Add(key);
        }
    }
}
