using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Removing one product (owner ask 2026-11-03: "원하면 삭제도 할 수 있게, 설치할 때의 역순으로, 밍툰코어부터 지우면 안 됨"). Only what the
    /// Manager's own records say it placed goes (PlacedFiles): a package it installed (moved to Library/OnAirDeck/Backup), the files an
    /// install created (moved to the system trash with AssetDatabase.MoveAssetToTrash), the product's own onairdeck.license and the
    /// folders those installs created once they are empty. With no record nothing is guessed: the person gets the folder to remove by
    /// hand. A core goes only after its add-ons; one question removes the add-ons first, then the core (RemovalPlanner.RemovalOrder).
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private sealed class RemovalStep
        {
            public CatalogEntry Entry;
            public RemovalPlan Plan;
            public List<string> Keys = new List<string>();
            public List<string> LicenseIds = new List<string>();
        }

        /// <summary>The detail pane offers removal for what this project has: an installed license, or a downloaded purchase.</summary>
        private static bool CanOfferRemove(CatalogEntry entry)
        {
            if (entry == null) return false;
            if (entry.IsLicense) return entry.State != EntryState.None;
            return entry.State == EntryState.Got || entry.State == EntryState.New;
        }

        private void RemoveEntry(CatalogEntry entry)
        {
            if (entry == null || Busy) return;
            var root = PackageInstallService.ProjectRoot;
            // Files another option took over and has since written (an import that finished) are its own before anything is planned.
            try { PlacedFiles.ResolveClaims(root); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not update the removal record: " + e.Message); }
            var placed = PlacedFiles.Load(root);

            // Add-ons of this core that are here go first (the reverse of the install order). Whether the core goes is judged with them
            // going first: files only they also recorded (an add-on that carries the core's files, G4 retest 3.3) are then the core's.
            var addons = new List<CatalogEntry>();
            var providedCore = CoreAddons.CoreProvidedBy(entry);
            if (providedCore.Length > 0 && _entries != null)
                foreach (var other in _entries)
                    if (other != entry && CoreAddons.CoreOf(other) == providedCore && AddonIsHere(root, other, placed)) addons.Add(other);
            var addonKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addon in addons) addonKeys.UnionWith(KeysOf(addon));
            if (addons.Count > 0 && !TakesCoreAway(root, entry, providedCore, placed, addonKeys))
            {
                addons.Clear();
                addonKeys.Clear();
            }
            addons = RemovalPlanner.RemovalOrder(addons, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId, e => e.IsLicense ? "" : CoreAddons.CoreOf(e));

            var steps = new List<RemovalStep>();
            var releaseOnly = new List<RemovalStep>();
            var goingFirst = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addon in addons)
            {
                var step = StepFor(root, addon, placed, goingFirst, OtherOptionKeys(addon), null);
                // Nothing of its own is left, only files another product also wrote: it gives them up when the run goes, and does not
                // hold the core. One whose own files the user changed is still here, and does.
                if (step.Plan.Refusal == RemovalRefusal.NothingHere && step.Plan.Changed.Count == 0) { releaseOnly.Add(step); continue; }
                if (!step.Plan.CanRun)
                {
                    // An add-on the Manager cannot remove keeps the core: never the core first.
                    ExplainRefusal(step, ManagerText.RmAddonBlocks.Replace("{a}", addon.Name).Replace("{c}", entry.Name));
                    return;
                }
                steps.Add(step);
                if (step.Plan.PackageFolder.Length > 0) goingFirst.Add(addon.PackageName);
            }
            var main = StepFor(root, entry, placed, goingFirst, OtherOptionKeys(entry), addonKeys);
            if (!main.Plan.CanRun)
            {
                // Only files another product also wrote are left: this product gives them up (they pass to that product), nothing moves.
                if (main.Plan.Refusal == RemovalRefusal.NothingHere && addons.Count == 0)
                    foreach (var key in main.Keys) ReleaseRecord(root, key);
                ExplainRefusal(main, null);
                return;
            }
            // An add-on file another product still holds stays after the run: the core must stay too, or that code loses it (review R1).
            var blocking = new List<string>();
            var leftovers = new List<string>();
            var addonSteps = new List<RemovalStep>(steps);
            addonSteps.AddRange(releaseOnly);
            foreach (var addonStep in addonSteps)
            {
                var left = RemovalPlanner.AddonLeftovers(new[] { addonStep.Plan }, main.Plan);
                if (left.Count == 0) continue;
                blocking.Add(addonStep.Entry.Name);
                leftovers.AddRange(left);
            }
            if (blocking.Count > 0)
            {
                Debug.Log(OnAirDeckConfig.LogPrefix + "Not removing " + entry.Name + ": files of " + string.Join(", ", blocking.ToArray())
                    + " stay with another product:\n" + string.Join("\n", leftovers.ToArray()));
                SetMessage(ManagerText.RmAddonBlocks.Replace("{a}", string.Join(", ", blocking.ToArray())).Replace("{c}", entry.Name), MessageType.Warning);
                return;
            }
            steps.Add(main);

            if (addons.Count > 0)
            {
                var names = string.Join(", ", addons.ConvertAll(a => a.Name).ToArray());
                if (!EditorUtility.DisplayDialog(ManagerText.RmAskT.Replace("{n}", entry.Name), ManagerText.RmAddonsFirst.Replace("{c}", entry.Name).Replace("{a}", names),
                    ManagerText.RmTogether, ManagerText.Cancel)) return;
            }
            // The whole list in the Console (a dialog shows only the first files), then the question.
            Debug.Log(OnAirDeckConfig.LogPrefix + "Removal of " + entry.Name + " would move:\n" + FullList(steps));
            if (!EditorUtility.DisplayDialog(ManagerText.RmAskT.Replace("{n}", entry.Name), ConfirmText(steps), ManagerText.BRemove, ManagerText.Cancel)) return;
            foreach (var step in releaseOnly) foreach (var key in step.Keys) ReleaseRecord(root, key);
            RunRemoval(root, steps);
        }

        private static void ReleaseRecord(string root, string key)
        {
            try { PlacedFiles.Release(root, key); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not update the placed-files record: " + e.Message); }
        }

        /// <summary>
        /// Removing <paramref name="entry"/> takes <paramref name="core"/> out of the project: always for a core package; for a file core
        /// (VModular 1.0.0, review N2) when this removal moves any file of the core (its marker or a file in its folders; review R2: a
        /// marker kept as changed does not keep the rest). Another row that installed the same files (the base license and a base
        /// purchase) keeps them, so nothing of the core moves and the add-ons stay too.
        /// </summary>
        private bool TakesCoreAway(string root, CatalogEntry entry, string core, PlacedRecords placed, ICollection<string> addonKeys)
        {
            if (entry.IsLicense && CoreAddons.IsCore(entry.PackageName)) return true;
            var dry = StepFor(root, entry, placed, new HashSet<string>(StringComparer.Ordinal), OtherOptionKeys(entry), addonKeys);
            return dry.Plan.CanRun && CoreAddons.CoreFilesAmong(core, dry.Plan.Files);
        }

        /// <summary>An add-on of a core is here: its package is installed, or files it recorded (or its marked state) are in the project.</summary>
        private static bool AddonIsHere(string root, CatalogEntry addon, PlacedRecords placed)
        {
            if (addon.IsLicense && addon.HasPackageVersions) return InstalledPackages.IsInstalled(root, addon.PackageName);
            if (addon.IsLicense && addon.State != EntryState.None) return true;
            foreach (var key in KeysOf(addon))
            {
                PlacedEntry recorded;
                if (!placed.Products.TryGetValue(key, out recorded)) continue;
                foreach (var path in recorded.Paths) if (File.Exists(RemovalPlanner.Absolute(root, path))) return true;
            }
            return false;
        }

        private static List<string> KeysOf(CatalogEntry entry)
        {
            var keys = new List<string>();
            if (entry.IsLicense)
            {
                keys.Add(PlacedFiles.LicenseKey(entry.LicenseId));
                foreach (var id in entry.AlsoLicenseIds) keys.Add(PlacedFiles.LicenseKey(id));
            }
            else keys.Add(PlacedFiles.PurchaseKey(entry.Purchase));
            return keys;
        }

        /// <summary>The PlacedFiles keys of the other options (line items) of a purchase row's product in the list; none for a license.</summary>
        private List<string> OtherOptionKeys(CatalogEntry entry)
        {
            var keys = new List<string>();
            if (entry.IsLicense || entry.Purchase == null || string.IsNullOrEmpty(entry.Purchase.product_id) || _entries == null) return keys;
            foreach (var other in _entries)
                if (other != entry && !other.IsLicense && other.Purchase != null && other.Purchase.product_id == entry.Purchase.product_id
                    && other.Purchase.item_id != entry.Purchase.item_id)
                    keys.Add(PlacedFiles.PurchaseKey(other.Purchase));
            return keys;
        }

        /// <param name="addonKeysGoingFirst">The PlacedFiles keys of add-ons removed before this row in the same run (null: none).</param>
        private static RemovalStep StepFor(string root, CatalogEntry entry, PlacedRecords placed, ICollection<string> goingFirst, ICollection<string> otherOptionKeys,
            ICollection<string> addonKeysGoingFirst)
        {
            var step = new RemovalStep { Entry = entry, Keys = KeysOf(entry) };
            if (entry.IsLicense)
            {
                step.LicenseIds.Add(entry.LicenseId);
                step.LicenseIds.AddRange(entry.AlsoLicenseIds);
            }
            var own = RemovalPlanner.OwnLicenseFiles(root, step.LicenseIds, entry.IsLicense || entry.Purchase == null ? null : entry.Purchase.item_id);
            // Another option of this product is still here: the product folder's license is its license too (review R1, 2026-11-04).
            // The other options are the list's rows and every record of the product (also an option the list no longer shows).
            var options = new List<string>(otherOptionKeys);
            if (!entry.IsLicense && entry.Purchase != null)
                options.AddRange(PlacedFiles.OptionKeys(placed, entry.Purchase.product_id, PlacedFiles.PurchaseKey(entry.Purchase)));
            if (RemovalPlanner.OtherOptionHere(root, placed, options)) own.Clear();
            if (entry.IsLicense && entry.HasPackageVersions && InstalledPackages.IsInstalled(root, entry.PackageName))
                step.Plan = RemovalPlanner.ForPackage(root, entry.PackageName, placed, own, goingFirst);
            else
                step.Plan = RemovalPlanner.ForFiles(root, step.Keys, placed, own, ManualFolderOf(root, entry), addonKeysGoingFirst);
            return step;
        }

        /// <summary>Where a product without a record probably is: the folder of its marker, else its product folder.</summary>
        private static string ManualFolderOf(string root, CatalogEntry entry)
        {
            if (entry.IsLicense)
                foreach (var folder in ProductMarkers.FoldersFor(root, entry.LicenseId))
                {
                    var relative = folder.Length > root.Length ? folder.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/') : "";
                    if (relative.StartsWith("Assets/", StringComparison.Ordinal)) return relative;
                }
            return Installer.ProductFolder(entry.Name);
        }

        private void ExplainRefusal(RemovalStep step, string prefix)
        {
            var entry = step.Entry;
            string text;
            switch (step.Plan.Refusal)
            {
                case RemovalRefusal.NoRecord:
                    text = step.Plan.ManualFolder.Length > 0
                        ? ManagerText.RmNoRecord.Replace("{n}", entry.Name).Replace("{f}", step.Plan.ManualFolder)
                        : ManagerText.RmNoRecordNoFolder.Replace("{n}", entry.Name);
                    break;
                case RemovalRefusal.HeldByVcc: text = ManagerText.RmVcc.Replace("{n}", entry.Name); break;
                case RemovalRefusal.NeededByOthers: text = ManagerText.RmNeeded.Replace("{n}", entry.Name).Replace("{d}", string.Join(", ", step.Plan.NeededBy.ToArray())); break;
                case RemovalRefusal.AddonsUnknown: text = ManagerText.RmAddonsUnknown.Replace("{n}", entry.Name); break;
                default: text = ManagerText.RmNothing.Replace("{n}", entry.Name); break;
            }
            if (!string.IsNullOrEmpty(prefix)) text = prefix + " " + text;
            _lastFailure[entry.Key] = text;
            SetMessage(text, MessageType.Warning);
            if (step.Plan.Refusal == RemovalRefusal.NoRecord && step.Plan.ManualFolder.Length > 0
                && EditorUtility.DisplayDialog(ManagerText.RmAskT.Replace("{n}", entry.Name), text, ManagerText.OpenFolder, ManagerText.Close))
                EditorUtility.RevealInFinder(RemovalPlanner.Absolute(PackageInstallService.ProjectRoot, step.Plan.ManualFolder));
        }

        /// <summary>The confirm text: per product what goes (package, file and folder counts, its first files), and that the trash can undo it.</summary>
        private static string ConfirmText(List<RemovalStep> steps)
        {
            const int ShownPerStep = 6;
            var sb = new StringBuilder(ManagerText.RmAsk);
            foreach (var step in steps)
            {
                sb.Append("\n\n").Append(step.Entry.Name).Append(" · ")
                  .Append(ManagerText.RmCounts.Replace("{f}", step.Plan.Files.Count.ToString()).Replace("{d}", step.Plan.Folders.Count.ToString()));
                if (step.Plan.PackageFolder.Length > 0) sb.Append("\n  ").Append(ManagerText.RmPkgLine.Replace("{p}", step.Plan.PackageFolder));
                for (var i = 0; i < step.Plan.Files.Count && i < ShownPerStep; i++) sb.Append("\n  ").Append(step.Plan.Files[i]);
                if (step.Plan.Files.Count > ShownPerStep) sb.Append("\n  ").Append(ManagerText.RmMore.Replace("{n}", (step.Plan.Files.Count - ShownPerStep).ToString()));
                if (step.Plan.Shared.Count > 0) sb.Append("\n  ").Append(ManagerText.RmShared.Replace("{n}", step.Plan.Shared.Count.ToString()));
                if (step.Plan.Changed.Count > 0) sb.Append("\n  ").Append(ManagerText.RmChanged.Replace("{n}", step.Plan.Changed.Count.ToString()));
            }
            sb.Append("\n\n").Append(ManagerText.RmListInConsole);
            return sb.ToString();
        }

        private static string FullList(List<RemovalStep> steps)
        {
            var sb = new StringBuilder();
            foreach (var step in steps)
            {
                sb.Append(step.Entry.Name).Append('\n');
                if (step.Plan.PackageFolder.Length > 0) sb.Append("  package ").Append(step.Plan.PackageFolder).Append('\n');
                foreach (var file in step.Plan.Files) sb.Append("  file ").Append(file).Append('\n');
                foreach (var folder in step.Plan.Folders) sb.Append("  folder ").Append(folder).Append('\n');
                foreach (var shared in step.Plan.Shared) sb.Append("  stays (another product's) ").Append(shared).Append('\n');
                foreach (var changed in step.Plan.Changed) sb.Append("  stays (changed) ").Append(changed).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Runs the steps in order without awaiting anything, so no script reload can come between an add-on and its core: files to the
        /// trash, then the folders they leave empty, then a package folder to the backup; the records follow what really went; one
        /// refresh (and a package resolve) at the end.
        /// </summary>
        private void RunRemoval(string root, List<RemovalStep> steps)
        {
            var failed = new List<string>();
            var backups = new List<string>();
            var removed = 0;
            var packagesMoved = false;
            try
            {
                foreach (var step in steps)
                {
                    var stepFailed = new List<string>();
                    var trashed = new List<string>();
                    foreach (var file in step.Plan.Files)
                    {
                        if (AssetDatabase.MoveAssetToTrash(file) && !File.Exists(RemovalPlanner.Absolute(root, file))) { removed++; trashed.Add(file); }
                        else stepFailed.Add(file);
                    }
                    // A folder is taken away only when every file went, and only if it is still empty now: checked again with the files
                    // that really left, so anything saved into it meanwhile (also at a path that just went) keeps it.
                    if (stepFailed.Count == 0)
                        foreach (var folder in RemovalPlanner.EmptiedFolders(root, trashed, new HashSet<string>(step.Plan.CreatedFolders, StringComparer.OrdinalIgnoreCase), true))
                            if (Directory.Exists(RemovalPlanner.Absolute(root, folder))) AssetDatabase.MoveAssetToTrash(folder);
                    if (step.Plan.PackageFolder.Length > 0 && stepFailed.Count == 0)
                    {
                        var name = step.Plan.PackageFolder.Substring("Packages/".Length);
                        var backup = Path.Combine(PackageTransaction.BackupRoot(root), "removed-" + name + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ"));
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(backup));
                            Directory.Move(RemovalPlanner.Absolute(root, step.Plan.PackageFolder), backup);
                            backups.Add(backup);
                            packagesMoved = true;
                            PlacedFiles.RemovePackage(root, name);
                        }
                        catch (Exception e)
                        {
                            stepFailed.Add(step.Plan.PackageFolder + " (" + e.Message + ")");
                        }
                    }
                    if (stepFailed.Count == 0)
                    {
                        // Only a removal that went through gives up the record: the files another product also wrote pass to it, so the
                        // last of them removes them. A partial one keeps it, so nothing passes on too early.
                        foreach (var key in step.Keys) ReleaseRecord(root, key);
                        try { InstallRecords.Remove(root, step.LicenseIds); }
                        catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not update the install record: " + e.Message); }
                        _lastFailure.Remove(step.Entry.Key);
                    }
                    failed.AddRange(stepFailed);
                    if (stepFailed.Count > 0) break;   // the core never goes while an add-on before it is only partly removed
                }
            }
            finally
            {
                AssetDatabase.Refresh();
                if (packagesMoved) UnityEditor.PackageManager.Client.Resolve();
                RefreshInstalledState();
            }
            var text = ManagerText.RmDone.Replace("{n}", removed.ToString());
            if (backups.Count > 0) text += " " + ManagerText.RmBackup.Replace("{b}", string.Join(", ", backups.ToArray()));
            if (failed.Count > 0) text += " " + ManagerText.RmFailed.Replace("{list}", string.Join(", ", failed.ToArray()));
            foreach (var line in failed) Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Not removed: " + line);
            // A package removal reloads scripts, which clears the window's message: the Console keeps it.
            Debug.Log(OnAirDeckConfig.LogPrefix + text);
            SetMessage(text, failed.Count > 0 ? MessageType.Warning : MessageType.Info);
        }
    }
}
