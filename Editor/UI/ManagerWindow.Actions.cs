using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// What the buttons do: download a purchase, install a package version, repair a mismatch, reissue a license file, open
    /// folders and pages. One of these runs at a time (<see cref="Busy"/>). Every button here is a user click, so the
    /// license calls under it are user-initiated (they may take a released seat back, contract v7).
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private string _workKey;
        [NonSerialized] private float _progress;
        [NonSerialized] private string _progressLabel = "";
        [NonSerialized] private CancellationTokenSource _workCancel;
        [NonSerialized] private readonly Dictionary<string, string> _doneFolder = new Dictionary<string, string>();
        [NonSerialized] private readonly Dictionary<string, string> _depProblem = new Dictionary<string, string>();
        [NonSerialized] private readonly HashSet<string> _cleanupRequired = new HashSet<string>();

        private bool Busy
        {
            get { return _workKey != null || (_batch != null && _batch.IsRunning) || _applyingSelfUpdate || _applyingUpdate != null || HandoffWaiting; }
        }

        /// <summary>0 nothing, 1 working on this entry now, 2 waiting in a running batch.</summary>
        private int WorkState(string key)
        {
            if (_workKey == key) return 1;
            if (_batch == null || !_batch.IsRunning) return 0;
            for (var i = 0; i < _batchKeys.Count && i < _batch.Results.Count; i++)
            {
                if (_batchKeys[i] != key) continue;
                var status = _batch.Results[i].Status;
                if (status == BatchStatus.Running || status == BatchStatus.Paused) return 1;
                if (status == BatchStatus.Pending) return 2;
            }
            return 0;
        }

        private void CancelWork()
        {
            if (_workCancel != null) _workCancel.Cancel();
            if (_batch != null && _batch.IsRunning) _batch.Cancel();
        }

        private void BeginWork(string key)
        {
            _workKey = key;
            _progress = 0f;
            _progressLabel = ManagerText.Preparing;
            if (_workCancel != null) _workCancel.Dispose();
            _workCancel = new CancellationTokenSource();
            SetMessage(null, MessageType.None);
        }

        private void EndWork()
        {
            _workKey = null;
            if (_workCancel != null) { _workCancel.Dispose(); _workCancel = null; }
            Repaint();
        }

        private void OnWorkProgress(string label, float progress)
        {
            _progressLabel = string.IsNullOrEmpty(label) ? _progressLabel : label;
            _progress = progress;
            Repaint();
        }

        // ------------------------------------------------------------ the main buttons of the detail pane

        /// <summary>The package version a license entry offers by default (the newest release, see BuildKinds.IndexOfOffered), or null.</summary>
        private static LicenseVersionEntry OfferedPackage(CatalogEntry entry)
        {
            foreach (var row in entry.Versions)
                if (row.Latest && row.License != null && row.License.IsPackage) return row.License;
            return null;
        }

        /// <summary>The non-package (.unitypackage or other file) version a license entry offers by default, or null.</summary>
        private static LicenseVersionEntry OfferedFileVersion(CatalogEntry entry)
        {
            foreach (var row in entry.Versions)
                if (row.Latest && row.License != null && !row.License.IsPackage) return row.License;
            return null;
        }

        private void PrimaryAction(CatalogEntry entry)
        {
            if (entry.Kind == FavoriteKind.Purchase) DoDownload(entry);
            else
            {
                var version = OfferedPackage(entry);
                if (version != null) DoInstall(entry, version);
                else
                {
                    var file = OfferedFileVersion(entry);
                    if (file != null) DoInstallFile(entry, file);
                }
            }
        }

        private async void DoDownload(CatalogEntry entry)
        {
            await DoDownloadAsync(entry, false);
        }

        /// <param name="confirmed">The import confirm already asked (and warned about the refund): no second dialog.</param>
        private async Task<ImportOutcome> DoDownloadAsync(CatalogEntry entry, bool confirmed)
        {
            if (_session == null || Busy || entry.Purchase == null) return ImportOutcome.Release();
            var item = entry.Purchase;
            // The first download makes the purchase non-refundable (same rule as the website).
            if (!confirmed && !item.downloaded && !EditorUtility.DisplayDialog(ManagerText.DlgDlT, ManagerText.DlgDl, ManagerText.BGet, ManagerText.Cancel)) return ImportOutcome.Declined();

            BeginWork(entry.Key);
            _doneFolder.Remove(entry.Key);
            var token = _session.token;
            try
            {
                var summary = await DownloadService.DownloadItemAsync(token, item, OnWorkProgress, _workCancel.Token, true, true);
                if (summary.LicenseRefused) return ImportOutcome.Failed("device_limit");   // the device-limit or update-required screen explains it
                item.downloaded = true;
                if (summary.PackagesQueued > 0) SetMessage(ManagerText.DonePkg, MessageType.Info);
                else
                {
                    _doneFolder[entry.Key] = summary.Folder ?? Installer.ProductFolder(entry.Name);
                    SetMessage(null, MessageType.None);
                }
                MarkDirty();
                // A .unitypackage is only queued here; its import (Unity's import window) ends later.
                return summary.QueuedPackages.Count > 0 ? ImportOutcome.WaitForImports(summary.QueuedPackages) : ImportOutcome.Done();
            }
            catch (OperationCanceledException)
            {
                SetMessage(ManagerText.Cancelled, MessageType.None);
                return ImportOutcome.Failed("cancelled");
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
                return ImportOutcome.Nothing();
            }
            catch (Exception e)
            {
                FailedInstall(ProductIdOf(entry), entry.Name, DownloadService.VersionOf(item), e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Download failed: " + e.Message);
                return ImportOutcome.Failed(IsNoticeRefusal(e) ? "device_limit" : "download_failed");
            }
            finally
            {
                EndWork();
            }
        }

        private async void DoInstall(CatalogEntry entry, LicenseVersionEntry version)
        {
            await DoInstallAsync(entry, version, false, null);
        }

        /// <param name="confirmed">The import confirm already asked: no second dialog.</param>
        /// <param name="willInstall">Told the package and version just before the install starts (the import record keeps them).</param>
        private async Task<ImportOutcome> DoInstallAsync(CatalogEntry entry, LicenseVersionEntry version, bool confirmed, Action<string, string> willInstall)
        {
            if (_session == null || Busy || version == null) return ImportOutcome.Release();
            var title = ManagerText.DlgInT.Replace("{n}", version.package_name).Replace("{v}", version.package_version);
            // A backup is made only of a package that is already installed.
            var text = string.IsNullOrEmpty(entry.InstalledVersion) ? ManagerText.DlgInNew : ManagerText.DlgIn;
            var message = text.Replace("{n}", version.package_name).Replace("{v}", version.package_version);
            if (!confirmed && !EditorUtility.DisplayDialog(title, message, ManagerText.BInst, ManagerText.Cancel)) return ImportOutcome.Declined();

            BeginWork(entry.Key);
            _cleanupRequired.Remove(entry.Key);
            _depProblem.Remove(entry.Key);
            var token = _session.token;
            try
            {
                if (willInstall != null) willInstall(version.package_name, version.package_version);
                var report = await PackageInstallService.InstallAsync(token, version, OnWorkProgress, _workCancel.Token, _licenses, true, LegacyInstallDialog.Confirm);
                SessionState.EraseString(PackageInstallService.ReportKey);
                _focusPackage = "";
                SessionState.EraseString(FocusKey);
                SetMessage(report.Describe(), MessageType.Info);
                // A hand-off: the product's own installer puts the package in after the next reload.
                return report.HandedOff.Length > 0 ? ImportOutcome.WaitForPackage(version.package_name, version.package_version) : ImportOutcome.Done();
            }
            catch (LegacyDeclinedException)
            {
                SetMessage(ManagerText.Declined, MessageType.None);
                return ImportOutcome.Declined();
            }
            catch (OperationCanceledException)
            {
                SetMessage(ManagerText.Cancelled, MessageType.None);
                return ImportOutcome.Failed("cancelled");
            }
            catch (LegacyCleanupRequiredException)
            {
                // Not an error: the item waits for a click that accepts the clean-up.
                _cleanupRequired.Add(entry.Key);
                return ImportOutcome.Failed("cleanup_required");
            }
            catch (DependencyUnavailableException e)
            {
                if (e.Kind == DependencyErrorKind.GetOnAtelierFirst) _depProblem[entry.Key] = e.PackageName ?? "";
                else FailedInstall(ProductIdOf(entry), entry.Name, version.package_version, e);
                return ImportOutcome.Failed("dependency_missing");
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
                return ImportOutcome.Nothing();
            }
            catch (Exception e)
            {
                FailedInstall(ProductIdOf(entry), entry.Name, version.package_version, e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Package install failed: " + e.Message);
                return ImportOutcome.Failed(IsNoticeRefusal(e) ? "device_limit" : "install_failed");
            }
            finally
            {
                EndWork();
                RefreshInstalledState();
            }
        }

        /// <summary>A seller version that is not a package (.unitypackage, zip, loose files): downloaded by its version id and placed by the service.</summary>
        private async void DoInstallFile(CatalogEntry entry, LicenseVersionEntry version)
        {
            await DoInstallFileAsync(entry, version, false);
        }

        /// <param name="confirmed">The import confirm already asked: no second dialog.</param>
        private async Task<ImportOutcome> DoInstallFileAsync(CatalogEntry entry, LicenseVersionEntry version, bool confirmed)
        {
            if (_session == null || Busy || version == null || entry.License == null) return ImportOutcome.Release();
            var label = string.IsNullOrEmpty(version.version_label) ? version.file_name : version.version_label;
            var title = ManagerText.DlgInT.Replace("{n}", entry.Name).Replace("{v}", label);
            // A .unitypackage or loose files overwrite in place; nothing is backed up either way.
            var text = entry.State == EntryState.None ? ManagerText.DlgInNew : ManagerText.DlgInFile;
            var message = text.Replace("{n}", entry.Name).Replace("{v}", label);
            if (!confirmed && !EditorUtility.DisplayDialog(title, message, ManagerText.BInst, ManagerText.Cancel)) return ImportOutcome.Declined();

            BeginWork(entry.Key);
            _doneFolder.Remove(entry.Key);
            var token = _session.token;
            try
            {
                var summary = await LicenseVersionInstall.InstallAsync(token, entry.License, version, _licenses, OnWorkProgress, _workCancel.Token, false, true);
                if (summary.PackagesQueued > 0) SetMessage(ManagerText.DonePkgAuto, MessageType.Info);
                else
                {
                    _doneFolder[entry.Key] = summary.Folder ?? Installer.ProductFolder(entry.Name);
                    SetMessage(null, MessageType.None);
                }
                MarkDirty();
                // The files are in, but without its license file the product does not run here (device limit or Manager too old).
                if (summary.LicenseRefused) return ImportOutcome.Failed("device_limit");
                return summary.QueuedPackages.Count > 0 ? ImportOutcome.WaitForImports(summary.QueuedPackages) : ImportOutcome.Done();
            }
            catch (OperationCanceledException)
            {
                SetMessage(ManagerText.Cancelled, MessageType.None);
                return ImportOutcome.Failed("cancelled");
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
                return ImportOutcome.Nothing();
            }
            catch (Exception e)
            {
                FailedInstall(ProductIdOf(entry), entry.Name, label, e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Version install failed: " + e.Message);
                return ImportOutcome.Failed(IsNoticeRefusal(e) ? "device_limit" : "install_failed");
            }
            finally
            {
                EndWork();
                RefreshInstalledState();
            }
        }

        /// <summary>Installs the version a mismatch card names (the one that matches the installed dependency, or the exact one it requires).</summary>
        private void InstallMatching(PackageMismatch mismatch)
        {
            var versions = new List<LicenseVersionEntry>();
            CatalogEntry owner = null;
            if (_entries != null)
            {
                foreach (var entry in _entries)
                {
                    foreach (var row in entry.Versions)
                    {
                        if (row.License == null || !row.License.IsPackage || row.License.package_name != mismatch.Package) continue;
                        versions.Add(row.License);
                        owner = entry;
                    }
                }
            }
            if (owner == null) return;
            var numbers = new List<string>();
            foreach (var v in versions) numbers.Add(v.package_version);
            bool matches;
            var target = InstalledPackages.ChooseTarget(numbers, mismatch.Installed, out matches);
            if (target == null) return;
            DoInstall(owner, versions.Find(v => v.package_version == target));
        }

        /// <summary>Asks the server for the license file of a seller license again (a user click, so a released seat may be taken back).</summary>
        private async void ReissueLicense(CatalogEntry entry)
        {
            if (_session == null || Busy || !entry.IsLicense) return;
            BeginWork(entry.Key);
            var token = _session.token;
            try
            {
                await LicenseService.IssueSellerLicenseAsync(token, _licenses, entry.LicenseId, true);
                // A merged row (same package in two licenses) stands for each of them; every one gets its file again.
                foreach (var other in entry.AlsoLicenseIds) await LicenseService.IssueSellerLicenseAsync(token, _licenses, other, true);
                SetMessage(ManagerText.LicenseReissued, MessageType.Info);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                // A server refusal is kept per license in LicenseRenewalStatus; the detail pane shows it.
                if (!IsNoticeRefusal(e))
                {
                    ManagerErrorWatch.OfferCaught("Renewing a license", e);
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "License renewal refused: " + e.Message);
                }
            }
            finally
            {
                EndWork();
                RefreshInstalledState();
            }
        }

        // ------------------------------------------------------------ folders and pages

        private void OpenEntryFolder(CatalogEntry entry)
        {
            var root = PackageInstallService.ProjectRoot;
            string folder = null;
            string done;
            if (_doneFolder.TryGetValue(entry.Key, out done)) folder = done;
            else if (entry.HasPackageVersions) folder = InstalledPackages.FolderOf(root, entry.PackageName);
            else if (entry.Kind == FavoriteKind.Purchase) folder = Installer.ProductFolder(entry.Name);
            if (string.IsNullOrEmpty(folder)) return;
            var absolute = Path.IsPathRooted(folder) ? folder : Path.Combine(root, folder);
            if (Directory.Exists(absolute)) EditorUtility.RevealInFinder(absolute);
        }

        private static void OpenAtelier(CatalogEntry entry)
        {
            OpenWeb(entry != null ? entry.ProductUrl : null);
        }
    }
}
