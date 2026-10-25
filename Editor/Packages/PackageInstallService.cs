using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Downloads a package and its exact dependencies, checks them, and hands them to one
    /// <see cref="PackageTransaction"/>. The add-on comes from OnAirDeck (presigned URL, identity checked
    /// from its package.json); dependencies come only from the trusted VPM listings in
    /// <see cref="OnAirDeckConfig.TrustedVpmListings"/> and must match their SHA-256.
    /// </summary>
    internal static class PackageInstallService
    {
        public const string ReportKey = "OnAirDeck.UnityManager.InstallReport";
        private const int MaxDependencyDepth = 8;

        public static string ProjectRoot
        {
            get { return Path.GetDirectoryName(Application.dataPath); }
        }

        /// <summary>Installs one OnAirDeck package version plus the dependencies it names, then resolves and refreshes once.</summary>
        public static async Task<InstallReport> InstallAsync(
            string sessionToken, LicenseVersionEntry version, Action<string, float> onProgress, CancellationToken cancel)
        {
            if (!version.IsPackage) throw new PackageInstallException("This version is not an installable package.");
            var root = ProjectRoot;
            var refusal = InstalledPackages.LegacyRefusal(root, version.package_name);
            if (refusal != null) throw new PackageInstallException(refusal);

            var cacheFolders = new List<string>();
            try
            {
                Report(onProgress, "Requesting the download…", 0f);
                var files = await OnAirDeckApi.RequestLicenseVersionDownloadAsync(sessionToken, version.version_id);
                var file = PickFile(files, version);
                PackageDownloadChecks.RequireHttps(file.url);
                var zip = await Downloader.DownloadAsync(file.url, PathSafety.SafeFileName(file.file_name),
                    p => Report(onProgress, "Downloading " + version.package_name + " " + version.package_version, p * 0.6f), cancel);
                cacheFolders.Add(Path.GetDirectoryName(zip));

                Report(onProgress, "Checking " + version.package_name, 0.6f);
                var manifest = PackageZip.Validate(zip, version.package_name, version.package_version);

                var items = new List<InstallItem>();
                await CollectDependenciesAsync(root, manifest, items, new Dictionary<string, string>(), new Dictionary<string, VpmListing>(),
                    cacheFolders, onProgress, cancel, 0);
                items.Add(new InstallItem { Name = version.package_name, Version = version.package_version, ZipPath = zip });
                await AddCollisionReinstallsAsync(root, items, cacheFolders, onProgress, cancel);
                return Apply(root, items, onProgress, cancel);
            }
            finally
            {
                foreach (var folder in cacheFolders) Downloader.TryDeleteFolder(folder);
            }
        }

        /// <summary>
        /// Replaces an embedded Manager with the zip a release points at. There is no listing hash for it,
        /// so its identity rests on package.json (name and version) from <see cref="PackageZip"/>.
        /// </summary>
        public static async Task<InstallReport> SelfUpdateAsync(
            string downloadUrl, string packageName, string version, string packageFolder, Action<string, float> onProgress, CancellationToken cancel)
        {
            var root = ProjectRoot;
            PackageDownloadChecks.RequireHttps(downloadUrl);
            if (string.IsNullOrEmpty(packageFolder) || !Directory.Exists(packageFolder))
                throw new PackageInstallException("Cannot find the folder this package is installed in.");
            // A working copy of the package (git checkout) must never be swapped out from under its owner.
            if (Directory.Exists(Path.Combine(packageFolder, ".git")) || File.Exists(Path.Combine(packageFolder, ".git")))
                throw new PackageInstallException("This copy of the Manager is a git working copy. Update it with git instead.");

            var cacheFolders = new List<string>();
            try
            {
                var zip = await Downloader.DownloadAsync(downloadUrl, packageName + "-" + version + ".zip",
                    p => Report(onProgress, "Downloading " + packageName + " " + version, p * 0.6f), cancel);
                cacheFolders.Add(Path.GetDirectoryName(zip));
                var manifest = PackageZip.Validate(zip, packageName, version);

                var items = new List<InstallItem>();
                await CollectDependenciesAsync(root, manifest, items, new Dictionary<string, string>(), new Dictionary<string, VpmListing>(),
                    cacheFolders, onProgress, cancel, 0);
                items.Add(new InstallItem { Name = packageName, Version = version, ZipPath = zip, TargetFolder = packageFolder });
                await AddCollisionReinstallsAsync(root, items, cacheFolders, onProgress, cancel);
                return Apply(root, items, onProgress, cancel);
            }
            finally
            {
                foreach (var folder in cacheFolders) Downloader.TryDeleteFolder(folder);
            }
        }

        private static InstallReport Apply(string root, List<InstallItem> items, Action<string, float> onProgress, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            Report(onProgress, "Installing…", 0.8f);
            var report = PackageTransaction.Execute(root, items, DateTime.Now);
            // A reload follows Refresh, so the window's own code may never see this result: leave it where the next load can show it.
            SessionState.SetString(ReportKey, report.Describe());
            Client.Resolve();
            AssetDatabase.Refresh();
            Report(onProgress, "Done", 1f);
            return report;
        }

        /// <summary>
        /// Files an older install left inside another package's folder (the old add-on .unitypackage imported into
        /// the core folder) carry the same GUIDs as the incoming package. Reinstalling that package at its CURRENT
        /// version from a trusted listing removes them (the swap does not carry extra files over). Collisions
        /// anywhere else, or in a package no trusted listing can restore, refuse the install before any change.
        /// </summary>
        private static async Task AddCollisionReinstallsAsync(
            string root, List<InstallItem> items, List<string> cacheFolders, Action<string, float> onProgress, CancellationToken cancel)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            var targets = new List<string>();
            foreach (var item in items)
            {
                guids.UnionWith(GuidCollisions.ReadZipGuids(item.ZipPath));
                targets.Add(item.TargetFolder.Length > 0 ? item.TargetFolder : InstalledPackages.FolderOf(root, item.Name));
            }
            var collisions = GuidCollisions.Scan(root, guids, targets);
            if (collisions.Count == 0) return;

            var blocking = new List<GuidCollision>();
            var candidates = GuidCollisions.Candidates(collisions, blocking);
            var listings = new Dictionary<string, VpmListing>();
            var reinstalls = new List<InstallItem>();
            foreach (var candidate in candidates)
            {
                VpmVersion listed = null;
                try { listed = await FindInListingsAsync(candidate.Name, candidate.Version, listings, cancel); }
                catch (PackageInstallException) { }
                if (listed == null)
                {
                    // Not restorable from a trusted listing: these files stay where they are, so the install is refused.
                    foreach (var collision in collisions) if (collision.Container == candidate.Folder) blocking.Add(collision);
                    continue;
                }
                PackageDownloadChecks.RequireHttps(listed.Url);
                var label = candidate.Name + " " + candidate.Version;
                var zip = await Downloader.DownloadAsync(listed.Url, PathSafety.SafeFolderName(candidate.Name + "-" + candidate.Version, "package") + ".zip",
                    p => Report(onProgress, "Downloading " + label, 0.6f * p), cancel);
                cacheFolders.Add(Path.GetDirectoryName(zip));
                PackageDownloadChecks.RequireSha256(zip, listed.Sha256, label);
                PackageZip.Validate(zip, candidate.Name, candidate.Version);
                reinstalls.Add(new InstallItem
                {
                    Name = candidate.Name,
                    Version = candidate.Version,
                    ZipPath = zip,
                    TargetFolder = candidate.Folder,
                    Note = ReinstallNote(candidate)
                });
            }
            if (blocking.Count > 0) throw new PackageInstallException(GuidCollisions.Refusal(blocking));
            items.InsertRange(0, reinstalls);
        }

        internal static string ReinstallNote(ReinstallCandidate candidate)
        {
            return "Files from an older install of the package being installed (" + candidate.Files + " with the same GUIDs) were inside "
                + candidate.Name + " and were removed by reinstalling it at " + candidate.Version + ".";
        }

        private static DownloadFile PickFile(DownloadFile[] files, LicenseVersionEntry version)
        {
            if (files == null || files.Length == 0) throw new PackageInstallException("The server returned no download for this version.");
            var wanted = PathSafety.SafeFileName(version.file_name);
            foreach (var file in files)
                if (PathSafety.SafeFileName(file.file_name) == wanted && wanted.Length > 0) return file;
            if (files.Length == 1 && PathSafety.SafeFileName(files[0].file_name).Length > 0) return files[0];
            throw new PackageInstallException("The server's download does not match the version you picked.");
        }

        // Dependencies are added before the packages that need them, so the swap order is dependency first.
        private static async Task CollectDependenciesAsync(
            string root, PackageManifest manifest, List<InstallItem> items, Dictionary<string, string> planned,
            Dictionary<string, VpmListing> listings, List<string> cacheFolders, Action<string, float> onProgress, CancellationToken cancel, int depth)
        {
            if (depth > MaxDependencyDepth) throw new PackageInstallException("The dependency chain is too deep.");
            foreach (var dependency in manifest.VpmDependencies)
            {
                string already;
                if (planned.TryGetValue(dependency.Key, out already))
                {
                    if (already != dependency.Value)
                        throw new PackageInstallException(dependency.Key + " is required at both " + already + " and " + dependency.Value + ".");
                    continue;
                }
                if (InstalledPackages.InstalledVersion(root, dependency.Key) == dependency.Value) continue;
                planned[dependency.Key] = dependency.Value;

                var refusal = InstalledPackages.LegacyRefusal(root, dependency.Key);
                if (refusal != null) throw new PackageInstallException(refusal);

                var listed = await FindInListingsAsync(dependency.Key, dependency.Value, listings, cancel);
                PackageDownloadChecks.RequireHttps(listed.Url);
                var label = dependency.Key + " " + dependency.Value;
                var zip = await Downloader.DownloadAsync(listed.Url, PathSafety.SafeFolderName(dependency.Key + "-" + dependency.Value, "dependency") + ".zip",
                    p => Report(onProgress, "Downloading " + label, 0.6f * p), cancel);
                cacheFolders.Add(Path.GetDirectoryName(zip));
                PackageDownloadChecks.RequireSha256(zip, listed.Sha256, label);
                var depManifest = PackageZip.Validate(zip, dependency.Key, dependency.Value);

                await CollectDependenciesAsync(root, depManifest, items, planned, listings, cacheFolders, onProgress, cancel, depth + 1);
                items.Add(new InstallItem { Name = dependency.Key, Version = dependency.Value, ZipPath = zip });
            }
        }

        private static async Task<VpmVersion> FindInListingsAsync(string name, string version, Dictionary<string, VpmListing> cache, CancellationToken cancel)
        {
            string lastError = null;
            foreach (var url in OnAirDeckConfig.TrustedVpmListings)
            {
                cancel.ThrowIfCancellationRequested();
                VpmListing listing;
                if (!cache.TryGetValue(url, out listing))
                {
                    try
                    {
                        listing = VpmListing.Parse(await OnAirDeckApi.GetTextAsync(url));
                        cache[url] = listing;
                    }
                    catch (Exception e)
                    {
                        if (e is OperationCanceledException) throw;
                        lastError = e.Message;
                        continue;
                    }
                }
                var found = listing.Find(name, version);
                if (found != null) return found;
            }
            throw new PackageInstallException("Version " + version + " of " + name + " is not in the trusted package listing."
                + (lastError != null ? " (" + lastError + ")" : ""));
        }

        private static void Report(Action<string, float> onProgress, string label, float progress)
        {
            if (onProgress != null) onProgress(label, progress);
        }
    }
}
