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

        /// <summary>
        /// Installs one OnAirDeck package version plus the dependencies it names, then resolves and refreshes once.
        /// <paramref name="licenses"/> is the account license list of the caller (the window last plugin-purchases): a dependency
        /// that one of those license versions offers at the exact version is downloaded from there before the public listings are
        /// tried. Null keeps the old behavior (listings only). After the install the license files are ensured for that list.
        /// A missing dependency throws <see cref="DependencyUnavailableException"/> (Kind says whether to offer the website).
        /// </summary>
        public static async Task<InstallReport> InstallAsync(
            string sessionToken, LicenseVersionEntry version, Action<string, float> onProgress, CancellationToken cancel, LicenseEntry[] licenses = null, bool userInitiated = true,
            Func<LegacyPlan, bool> confirmLegacy = null)
        {
            if (!version.IsPackage) throw new PackageInstallException("This version is not an installable package.");
            LegacyHandoff.ThrowIfWaiting();
            var root = ProjectRoot;
            LegacyMove legacyMove = null;
            var collisionCheck = false;

            var cacheFolders = new List<string>();
            try
            {
                Report(onProgress, "Requesting the download…", 0f);
                DownloadFile[] files;
                try
                {
                    files = await OnAirDeckApi.RequestLicenseVersionDownloadAsync(sessionToken, version.version_id, userInitiated);
                }
                catch (Exception e)
                {
                    // A device-limit or update-required refusal reaches the window; retrying runs the whole install again.
                    DeviceLimitNotice.Report(e, version.package_name + " " + version.package_version,
                        async () => (object)await InstallAsync(sessionToken, version, onProgress, cancel, licenses, true, confirmLegacy), "install:" + version.package_name);
                    throw;
                }
                var file = PickFile(files, version);
                PackageDownloadChecks.RequireHttps(file.url);
                var zip = await Downloader.DownloadAsync(file.url, PathSafety.SafeFileName(file.file_name),
                    p => Report(onProgress, "Downloading " + version.package_name + " " + version.package_version, p * 0.6f), cancel);
                cacheFolders.Add(Path.GetDirectoryName(zip));

                Report(onProgress, "Checking " + version.package_name, 0.6f);
                var manifest = PackageZip.Validate(zip, version.package_name, version.package_version);

                var items = new List<InstallItem>();
                Func<Task<object>> retry = async () => (object)await InstallAsync(sessionToken, version, onProgress, cancel, licenses, true, confirmLegacy);
                await CollectDependenciesAsync(root, manifest, items, new Dictionary<string, string>(), new Dictionary<string, VpmListing>(),
                    cacheFolders, onProgress, cancel, 0, sessionToken, licenses, retry, userInitiated);
                items.Add(new InstallItem { Name = version.package_name, Version = version.package_version, ZipPath = zip });
                // Contract v9: an older copy under Assets/ is moved to the backup after consent (never from a background caller).
                LegacyHandoffRequest handoff;
                legacyMove = LegacyInstall.Prepare(root, ProbesFor(root, items, version, licenses), userInitiated, confirmLegacy, DateTime.Now, out handoff);
                if (handoff != null)
                {
                    // The product's own installer cleans up and installs; the Manager imports it and waits (LegacyHandoff). Nothing is moved.
                    await LegacyHandoffRunner.StartAsync(sessionToken, handoff, onProgress, cancel, userInitiated);
                    return new InstallReport { HandedOff = LegacyInstallText.HandedOff(LegacyInstallText.Language(), handoff.Product) };
                }
                collisionCheck = legacyMove != null;
                await AddCollisionReinstallsAsync(root, items, cacheFolders, onProgress, cancel);
                collisionCheck = false;
                var report = Apply(root, items, onProgress, cancel, licenses ?? new LicenseEntry[0]);
                if (legacyMove != null) legacyMove.MarkInstalled();
                await EnsureLicenseFilesQuietlyAsync(sessionToken, licenses, userInitiated);
                return report;
            }
            catch (Exception e)
            {
                if (legacyMove == null) throw;
                throw RestoreLegacy(legacyMove, e, collisionCheck);
            }
            finally
            {
                foreach (var folder in cacheFolders) Downloader.TryDeleteFolder(folder);
            }
        }

        /// <summary>
        /// Replaces an embedded Manager with the zip a release points at. Its identity rests on package.json (name and version) from
        /// <see cref="PackageZip"/>, and on <paramref name="sha256"/> when the release lists one.
        /// </summary>
        /// <param name="refuseBeforeSwap">Asked after the download and checks, right before anything on disk changes; a reason stops the
        /// update there (<see cref="SelfUpdateDeferredException"/>), so a moment that turned unsafe during the download is not used.</param>
        /// <param name="afterTransaction">Gets the transaction's report (every folder swapped and its backup) before Package Manager and the
        /// asset database are told: a refresh may compile, and a rollback must already know where the old files went.</param>
        public static async Task<InstallReport> SelfUpdateAsync(
            string downloadUrl, string packageName, string version, string packageFolder, Action<string, float> onProgress, CancellationToken cancel,
            string sha256 = null, Func<string> refuseBeforeSwap = null, Action<InstallReport> afterTransaction = null)
        {
            LegacyHandoff.ThrowIfWaiting();
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
                if (!string.IsNullOrEmpty(sha256)) PackageDownloadChecks.RequireSha256(zip, sha256, packageName + " " + version);
                var manifest = PackageZip.Validate(zip, packageName, version);

                var items = new List<InstallItem>();
                await CollectDependenciesAsync(root, manifest, items, new Dictionary<string, string>(), new Dictionary<string, VpmListing>(),
                    cacheFolders, onProgress, cancel, 0, null, null, null, false);
                items.Add(new InstallItem { Name = packageName, Version = version, ZipPath = zip, TargetFolder = packageFolder });
                await AddCollisionReinstallsAsync(root, items, cacheFolders, onProgress, cancel);
                var refusal = refuseBeforeSwap != null ? refuseBeforeSwap() : null;
                if (refusal != null) throw new SelfUpdateDeferredException(refusal);
                return Apply(root, items, onProgress, cancel, null, afterTransaction);
            }
            finally
            {
                foreach (var folder in cacheFolders) Downloader.TryDeleteFolder(folder);
            }
        }

        // One probe per item of the install; the requested package carries its license id (the marker rule needs it).
        internal static List<LegacyProbe> ProbesFor(string root, List<InstallItem> items, LicenseVersionEntry version, LicenseEntry[] licenses)
        {
            var licenseId = "";
            var display = version.package_name;
            LegacyInstallerFile installer = null;
            LegacyInstallerFile zipFile = null;
            if (licenses != null)
                foreach (var license in licenses)
                    foreach (var candidate in license.versions ?? new LicenseVersionEntry[0])
                        if (candidate.version_id == version.version_id)
                        {
                            licenseId = license.license_id ?? "";
                            if (!string.IsNullOrEmpty(license.name)) display = license.name;
                            installer = LegacyInstallerFile.ForLegacyCase(root, version.package_name, version, license.versions);
                            if (installer != null) zipFile = LegacyInstallerFile.OfVersion(version);
                        }
            // The same package in two licenses (MingToon's free and commercial Core): the zip may come from one and the installer of its
            // label only from the other.
            if (installer == null && licenseId.Length > 0)
                foreach (var license in licenses)
                {
                    if (installer != null || license == null || license.license_id == licenseId || !CarriesPackage(license, version.package_name)) continue;
                    installer = LegacyInstallerFile.ForLegacyCase(root, version.package_name, version, license.versions);
                    if (installer != null) zipFile = LegacyInstallerFile.OfVersion(version);
                }
            var probes = new List<LegacyProbe>();
            foreach (var item in items)
            {
                var main = item.Name == version.package_name;
                probes.Add(new LegacyProbe
                {
                    Name = item.Name, Version = item.Version, ZipPath = item.ZipPath, TargetFolder = item.TargetFolder,
                    Display = main ? display : item.Name, LicenseId = main ? licenseId : "", Installer = main ? installer : null, Zip = main ? zipFile : null
                });
            }
            return probes;
        }

        private static bool CarriesPackage(LicenseEntry license, string package)
        {
            foreach (var v in license.versions ?? new LicenseVersionEntry[0])
                if (v != null && v.IsPackage && v.package_name == package) return true;
            return false;
        }

        // The install did not happen: the moved old install goes back, and the message says so.
        internal static Exception RestoreLegacy(LegacyMove move, Exception failure, bool stillColliding)
        {
            var language = LegacyInstallText.Language();
            var failed = move.Restore();
            try { AssetDatabase.Refresh(); } catch (Exception) { }
            if (failure is OperationCanceledException) return failure;
            var reason = stillColliding
                ? LegacyInstallText.StillColliding(language) + "\n" + failure.Message
                : LegacyInstallText.InstallFailedRestored(language, failure.Message);
            if (failed.Count > 0) reason += "\n" + LegacyInstallText.RestoreIncomplete(language, move.BackupFolder);
            return new PackageInstallException(reason, failure);
        }

        // A license file that cannot be written must not fail an install that already succeeded; a refusal shows in the notice.
        private static async Task EnsureLicenseFilesQuietlyAsync(string sessionToken, LicenseEntry[] licenses, bool userInitiated)
        {
            try { await LicenseService.EnsureLicenseFilesAsync(sessionToken, licenses, userInitiated); }
            catch (Exception) { }
        }

        /// <param name="watchLicenses">Not null: the installed packages are watched for compile errors and exceptions (<see cref="InstallErrorWatch"/>); the licenses name each product's id.</param>
        private static string LicenseIdOf(LicenseEntry[] licenses, string packageName)
        {
            foreach (var license in licenses)
            {
                if (license == null || license.versions == null) continue;
                foreach (var v in license.versions)
                    if (v != null && v.package_name == packageName) return license.license_id ?? "";
            }
            return packageName;
        }

        private static InstallReport Apply(string root, List<InstallItem> items, Action<string, float> onProgress, CancellationToken cancel, LicenseEntry[] watchLicenses,
            Action<InstallReport> afterTransaction = null)
        {
            cancel.ThrowIfCancellationRequested();
            Report(onProgress, "Installing…", 0.8f);
            var report = PackageTransaction.Execute(root, items, DateTime.Now);
            if (afterTransaction != null) afterTransaction(report);
            // A reload follows Refresh, so the window's own code may never see this result: leave it where the next load can show it.
            SessionState.SetString(ReportKey, report.Describe());
            if (watchLicenses != null)
                foreach (var item in items)
                    if (item.TargetFolder.Length == 0) InstallErrorWatch.Begin("package:" + item.Name + "@" + item.Version, item.Name, LicenseIdOf(watchLicenses, item.Name), item.Version, new[] { "Packages/" + item.Name });
#if UNITY_2021_1_OR_NEWER
            Client.Resolve();   // older editors resolve the changed manifest on the refresh below
#endif
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
            Dictionary<string, VpmListing> listings, List<string> cacheFolders, Action<string, float> onProgress, CancellationToken cancel, int depth,
            string sessionToken, LicenseEntry[] licenses, Func<Task<object>> retry, bool userInitiated)
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

                // Order: installed (skipped above), then a license version of this account, then the trusted listings.
                var resolution = await DependencyResolver.ResolveAsync(dependency.Key, dependency.Value, "", licenses,
                    () => FindInListingsAsync(dependency.Key, dependency.Value, listings, cancel));
                var label = dependency.Key + " " + dependency.Value;
                string zip;
                PackageManifest depManifest;
                if (resolution.Source == DependencySource.LicenseVersion)
                {
                    var licenseVersion = resolution.LicenseVersion;
                    DownloadFile[] files;
                    try
                    {
                        files = await OnAirDeckApi.RequestLicenseVersionDownloadAsync(sessionToken, licenseVersion.version_id, userInitiated);
                    }
                    catch (Exception e)
                    {
                        // The device gate applies here too; a refusal reaches the window and its retry runs the whole install again.
                        if (!(e is OperationCanceledException)) DeviceLimitNotice.Report(e, label, retry, "install-dependency");
                        throw;
                    }
                    var file = PickFile(files, licenseVersion);
                    PackageDownloadChecks.RequireHttps(file.url);
                    zip = await Downloader.DownloadAsync(file.url, PathSafety.SafeFileName(file.file_name),
                        p => Report(onProgress, "Downloading " + label, 0.6f * p), cancel);
                    cacheFolders.Add(Path.GetDirectoryName(zip));
                    depManifest = PackageZip.Validate(zip, dependency.Key, dependency.Value);
                }
                else
                {
                    var listed = resolution.Listed;
                    PackageDownloadChecks.RequireHttps(listed.Url);
                    zip = await Downloader.DownloadAsync(listed.Url, PathSafety.SafeFolderName(dependency.Key + "-" + dependency.Value, "dependency") + ".zip",
                        p => Report(onProgress, "Downloading " + label, 0.6f * p), cancel);
                    cacheFolders.Add(Path.GetDirectoryName(zip));
                    PackageDownloadChecks.RequireSha256(zip, listed.Sha256, label);
                    depManifest = PackageZip.Validate(zip, dependency.Key, dependency.Value);
                }

                await CollectDependenciesAsync(root, depManifest, items, planned, listings, cacheFolders, onProgress, cancel, depth + 1, sessionToken, licenses, retry, userInitiated);
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
            // Null means every listing was read and none has it; an unreadable listing is an error (not "not published").
            if (lastError != null)
                throw new PackageInstallException("Version " + version + " of " + name + " is not in the trusted package listing. (" + lastError + ")");
            return null;
        }

        private static void Report(Action<string, float> onProgress, string label, float progress)
        {
            if (onProgress != null) onProgress(label, progress);
        }
    }
}
