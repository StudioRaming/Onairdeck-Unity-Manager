using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The Unity side of <see cref="LegacyHandoff"/>: downloads the product installer through the license-gated download, imports it
    /// without Unity's import window, and watches the wait state while the installer compiles, reloads and works. Nothing in the old
    /// folder is touched here. While a hand-off is Pending the Manager must not import, install, refresh or resolve:
    /// <see cref="RefreshUnlessWaiting"/> is the refresh every Manager path uses, and the install entry points call
    /// <see cref="LegacyHandoff.ThrowIfWaiting"/>.
    /// </summary>
    [InitializeOnLoad]
    internal static class LegacyHandoffRunner
    {
        private const double PollSeconds = 2.0;
        private static double _next;

        static LegacyHandoffRunner()
        {
            EditorApplication.update += Poll;
        }

        /// <summary>AssetDatabase.Refresh, skipped while a product installer is working (it owns the asset database then).</summary>
        public static void RefreshUnlessWaiting()
        {
            if (LegacyHandoff.IsWaiting()) return;
            try { AssetDatabase.Refresh(); } catch (Exception) { }
        }

        /// <summary>
        /// The user said yes. Downloads BOTH files of the license version first (the installer and the package zip; by version id, HTTPS,
        /// the listed size; the zip must be the package the version names), checks that the installer supports the Manager's clean-up
        /// (managerCleanup in its bootstrap config) and keeps the zip in Library/OnAirDeck. Only then are the request written and the
        /// installer imported: the installer's delete has no backup, so every failure that can happen earlier must happen earlier.
        /// A failure before the import removes the request and the state and leaves the project untouched.
        /// </summary>
        public static async Task StartAsync(string sessionToken, LegacyHandoffRequest request, Action<string, float> onProgress, CancellationToken cancel, bool userInitiated)
        {
            LegacyInstallerSpec spec = null;
            if (request == null || request.Installer == null || request.Zip == null || !OnAirDeckConfig.LegacyInstallers.TryGetValue(request.Package, out spec))
                throw new LegacyCleanupRequiredException(LegacyInstallText.HandoffUnavailable(LegacyInstallText.Language(), request == null ? "" : request.Product, request == null ? "" : request.Folder, false));
            LegacyHandoff.ThrowIfWaiting();
            var root = PackageInstallService.ProjectRoot;
            var folders = new System.Collections.Generic.List<string>();
            var begun = false;
            try
            {
                var installerPath = await FetchAsync(sessionToken, request, request.Installer, spec.InstallerSuffix, 0f, onProgress, cancel, userInitiated, folders);
                var zipPath = await FetchAsync(sessionToken, request, request.Zip, ".zip", 0.5f, onProgress, cancel, userInitiated, folders);
                if (!MingToonLegacyCleanup.AllowsCleanup(installerPath))
                    throw new LegacyCleanupRequiredException(LegacyInstallText.HandoffUnsupported(LegacyInstallText.Language(), request.Product, request.Folder));
                // Everything that can fail happens before the request: the full extraction and hashes of the package and its dependencies, the
                // GUID check against everything outside the old folder, the saved and verified state. The installer's delete has no backup.
                MingToonLegacyCleanup.Stage(root, request, zipPath);
                begun = true;
                LegacyHandoff.Start(root, spec, request, DateTime.UtcNow);
                PackageImportQueue.Enqueue(new[] { installerPath }, false);
                // The import queue deletes the installer's cache folder itself; the zip's folder is ours to remove.
                folders.Remove(Path.GetDirectoryName(installerPath));
            }
            catch (Exception)
            {
                if (begun) LegacyHandoff.Clear(root, request.Package);
                else MingToonLegacyCleanup.Unstage(root, request.Package);
                throw;
            }
            finally
            {
                foreach (var folder in folders) Downloader.TryDeleteFolder(folder);
            }
        }

        /// <summary>
        /// The installer reported that the old install is gone: the Manager installs the staged zip (extract, move to Packages/&lt;package&gt;,
        /// Client.Resolve; never Client.Add, a tarball or git, because the installer verifies an embedded package at exactly that path).
        /// A failure stops the hand-off with its message.
        /// </summary>
        private static void InstallNow(string root, string package)
        {
            var error = "";
            try
            {
                // Step by step, each saved: staged folders moved, then Resolve requested. A crash between them resumes at Resolve (the targets
                // match the stage manifest); a folder with other content is refused.
                MingToonLegacyCleanup.InstallStaged(root, package);
                LegacyHandoff.MarkInstallMoved(root, package, DateTime.UtcNow);
#if UNITY_2021_1_OR_NEWER
                Client.Resolve();
#else
                AssetDatabase.Refresh();   // older editors resolve the changed Packages folder on the refresh
#endif
            }
            catch (Exception e)
            {
                error = "install: " + e.Message;
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not install " + package + " after the clean-up: " + e.Message);
            }
            // Saving the step is part of the install: a failure here is shown as Stopped (state: ...), never swallowed.
            LegacyHandoff.MarkInstallStarted(root, package, DateTime.UtcNow, error);
        }

        // One license-gated file by version id: the file with the wanted ending, HTTPS, exactly the listed size.
        private static async Task<string> FetchAsync(string sessionToken, LegacyHandoffRequest request, LegacyInstallerFile wanted, string suffix, float start,
            Action<string, float> onProgress, CancellationToken cancel, bool userInitiated, System.Collections.Generic.List<string> folders)
        {
            DownloadFile[] files;
            try
            {
                files = await OnAirDeckApi.RequestLicenseVersionDownloadAsync(sessionToken, wanted.VersionId, userInitiated);
            }
            catch (Exception e)
            {
                DeviceLimitNotice.Report(e, request.Product,
                    async () => { await StartAsync(sessionToken, request, onProgress, cancel, true); return (object)null; }, "handoff:" + request.Package);
                throw;
            }
            DownloadFile file = null;
            foreach (var candidate in files)
                if (candidate != null && !string.IsNullOrEmpty(candidate.file_name) && candidate.file_name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { file = candidate; break; }
            if (file == null)
                throw new LegacyCleanupRequiredException(LegacyInstallText.HandoffUnavailable(LegacyInstallText.Language(), request.Product, request.Folder, false));
            PackageDownloadChecks.RequireHttps(file.url);

            // The license download gives a size but no hash (contract v9.5): the size check below plus the content checks (installer config,
            // package.json identity, full extraction in MingToonLegacyCleanup.Stage). A server SHA-256 would be verified here, before the size.
            var path = await Downloader.DownloadAsync(file.url, PathSafety.SafeFileName(file.file_name),
                p => { if (onProgress != null) onProgress("Downloading " + request.Product + " " + file.file_name, start + p * 0.5f); }, cancel);
            folders.Add(Path.GetDirectoryName(path));
            var size = new FileInfo(path).Length;
            if (!LegacyInstallerFile.SizeMatches(wanted.Size, size))
                throw new PackageInstallException("The " + request.Product + " download " + file.file_name + " does not match the listed size (" + size + " bytes, expected " + wanted.Size + "). Nothing was imported.");
            return path;
        }

        /// <summary>Opens the product's own installer window (the menu item of the config), for a Stopped hand-off. False when the menu item is missing.</summary>
        public static bool OpenInstaller(string package)
        {
            LegacyInstallerSpec spec;
            if (!OnAirDeckConfig.LegacyInstallers.TryGetValue(package ?? "", out spec) || string.IsNullOrEmpty(spec.MenuPath)) return false;
            return EditorApplication.ExecuteMenuItem(spec.MenuPath);
        }

        private static void Poll()
        {
            if (EditorApplication.timeSinceStartup < _next) return;
            _next = EditorApplication.timeSinceStartup + PollSeconds;
            try
            {
                var root = PackageInstallService.ProjectRoot;
                var busy = EditorApplication.isCompiling || EditorApplication.isUpdating;
                foreach (var package in LegacyHandoff.Packages(root))
                {
                    try
                    {
                        // While a state write failed, the install steps wait: Status shows Stopped and the next successful write clears it.
                        if (!busy && PackageImportQueue.PendingCount == 0 && LegacyHandoff.ImportOutstanding(root, package))
                            LegacyHandoff.MarkImportDone(root, package, DateTime.UtcNow);
                        if (!busy && !LegacyHandoff.HasWriteFailure(package) && LegacyHandoff.InstallDue(root, package)) InstallNow(root, package);
                    }
                    catch (Exception e)
                    {
                        LegacyHandoff.ReportWriteFailure(package, e.Message);
                        Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Hand-off state of " + package + " could not be saved: " + e.Message);
                    }
                    LegacyHandoff.Status(root, package, DateTime.UtcNow, busy, true);   // records progress for the no-progress timer
                }
            }
            catch (Exception)
            {
                // The poller must never break the editor loop; the next tick tries again.
            }
        }
    }
}
