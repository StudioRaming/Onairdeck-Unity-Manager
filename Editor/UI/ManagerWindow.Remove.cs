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
            var placed = PlacedFiles.Load(root);

            // Add-ons of this core that are here go first (the reverse of the install order).
            var addons = new List<CatalogEntry>();
            if (entry.IsLicense && CoreAddons.IsCore(entry.PackageName) && _entries != null)
                foreach (var other in _entries)
                    if (other != entry && CoreAddons.CoreOf(other) == entry.PackageName && AddonIsHere(root, other, placed)) addons.Add(other);
            addons = RemovalPlanner.RemovalOrder(addons, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId, e => e.IsLicense ? "" : CoreAddons.CoreOf(e));

            var steps = new List<RemovalStep>();
            var goingFirst = new HashSet<string>(StringComparer.Ordinal);
            foreach (var addon in addons)
            {
                var step = StepFor(root, addon, placed, goingFirst);
                if (!step.Plan.CanRun)
                {
                    // An add-on the Manager cannot remove keeps the core: never the core first.
                    ExplainRefusal(step, ManagerText.RmAddonBlocks.Replace("{a}", addon.Name).Replace("{c}", entry.Name));
                    return;
                }
                steps.Add(step);
                if (step.Plan.PackageFolder.Length > 0) goingFirst.Add(addon.PackageName);
            }
            var main = StepFor(root, entry, placed, goingFirst);
            if (!main.Plan.CanRun)
            {
                ExplainRefusal(main, null);
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
            RunRemoval(root, steps);
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

        private static RemovalStep StepFor(string root, CatalogEntry entry, PlacedRecords placed, ICollection<string> goingFirst)
        {
            var step = new RemovalStep { Entry = entry, Keys = KeysOf(entry) };
            if (entry.IsLicense)
            {
                step.LicenseIds.Add(entry.LicenseId);
                step.LicenseIds.AddRange(entry.AlsoLicenseIds);
            }
            var own = RemovalPlanner.OwnLicenseFiles(root, step.LicenseIds, entry.IsLicense || entry.Purchase == null ? null : entry.Purchase.item_id);
            if (entry.IsLicense && entry.HasPackageVersions && InstalledPackages.IsInstalled(root, entry.PackageName))
                step.Plan = RemovalPlanner.ForPackage(root, entry.PackageName, placed, own, goingFirst);
            else
                step.Plan = RemovalPlanner.ForFiles(root, step.Keys, placed, own, ManualFolderOf(root, entry));
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
                foreach (var shared in step.Plan.Shared) sb.Append("  stays ").Append(shared).Append('\n');
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
                    foreach (var file in step.Plan.Files)
                    {
                        if (AssetDatabase.MoveAssetToTrash(file)) removed++;
                        else stepFailed.Add(file);
                    }
                    // A folder is taken away only when every file in it went.
                    if (stepFailed.Count == 0)
                        foreach (var folder in step.Plan.Folders)
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
                    // This product gives up the files another product also recorded, so their last owner can remove them later.
                    var released = new HashSet<string>(step.Plan.Shared, StringComparer.OrdinalIgnoreCase);
                    foreach (var key in step.Keys)
                    {
                        try { PlacedFiles.Prune(root, key, released); }
                        catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not update the placed-files record: " + e.Message); }
                    }
                    if (stepFailed.Count == 0)
                    {
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
