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

        /// <summary>The core is in Packages/, or its old Assets folder is there (an old install the add-on also works with).</summary>
        private static bool CoreInstalled(string core)
        {
            var root = PackageInstallService.ProjectRoot;
            if (!string.IsNullOrEmpty(InstalledPackages.InstalledVersion(root, core))) return true;
            string legacy;
            return OnAirDeckConfig.LegacyAssetFolders.TryGetValue(core, out legacy) && Directory.Exists(Path.Combine(root, legacy.Replace('/', Path.DirectorySeparatorChar)));
        }

        /// <summary>The list row that installs <paramref name="core"/>, or null.</summary>
        private CatalogEntry CoreEntry(string core)
        {
            if (_entries == null) return null;
            foreach (var entry in _entries)
                if (entry.IsLicense && entry.PackageName == core && OfferedPackage(entry) != null) return entry;
            return null;
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
            if (!EditorUtility.DisplayDialog(ManagerText.CoreFirstBtn.Replace("{c}", coreName), ManagerText.CoreFirstAsk.Replace("{a}", entry.Name).Replace("{c}", coreName),
                ManagerText.BInst, ManagerText.Cancel)) return true;
            InstallCoreThenAddon(entry, CoreEntry(core));
            return true;
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
            var coreVersion = coreEntry != null ? OfferedPackage(coreEntry) : null;
            if (coreVersion == null) return;
            var outcome = await DoInstallAsync(coreEntry, coreVersion, true, null);
            // A script reload after the core's refresh can end this method here; the add-on's button is then a plain install.
            if (outcome.Report != ImportReport.Done) return;
            var file = OfferedFileVersion(addon);
            if (file != null) await DoInstallFileAsync(addon, file, true);
        }
    }
}
