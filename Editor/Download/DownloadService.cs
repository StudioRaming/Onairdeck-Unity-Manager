using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    internal sealed class DownloadSummary
    {
        public int Downloaded;
        public int Copied;
        public int Extracted;
        public int SkippedUnsafe;
        public int PackagesQueued;
        /// <summary>The .unitypackage files handed to <see cref="PackageImportQueue"/> (their import ends later; see <see cref="PackageImportQueue.ResultOf"/>).</summary>
        public List<string> QueuedPackages = new List<string>();
        public bool LicenseWritten;
        public bool LicenseRefused;   // the server refused the license for this PC (device limit or Manager too old); see DeviceLimitNotice
        public string Folder;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Copied > 0) parts.Add(Copied + " file" + (Copied == 1 ? "" : "s") + " copied");
            if (Extracted > 0) parts.Add(Extracted + " file" + (Extracted == 1 ? "" : "s") + " extracted");
            if (PackagesQueued > 0) parts.Add(PackagesQueued + " package" + (PackagesQueued == 1 ? "" : "s") + " opened for import");
            if (SkippedUnsafe > 0) parts.Add(SkippedUnsafe + " unsafe entr" + (SkippedUnsafe == 1 ? "y" : "ies") + " skipped");
            var text = parts.Count == 0 ? "Nothing was installed." : string.Join(", ", parts.ToArray()) + ".";
            if (LicenseWritten) text += " License saved.";
            return Copied + Extracted > 0 || LicenseWritten ? text + " Folder: " + Folder : text;
        }
    }

    /// <summary>
    /// One purchased item end to end: ask the server for links, download everything to the
    /// cache, then install. All downloads finish before any import starts, because importing
    /// scripts reloads the editor and would abandon in-flight work.
    /// </summary>
    internal static class DownloadService
    {
        /// <summary>
        /// Presigned links for the item: one request for a purchase, one per file for the sellers own product
        /// (owner path: product_id + file_id, no download is counted).
        /// </summary>
        private static async Task<DownloadFile[]> RequestFilesAsync(string sessionToken, PurchaseItem item, bool userInitiated)
        {
            if (!item.IsOwn) return await OnAirDeckApi.RequestDownloadAsync(sessionToken, item.item_id, userInitiated);
            var all = new List<DownloadFile>();
            foreach (var file in item.files ?? new PurchaseFile[0])
            {
                if (file == null || string.IsNullOrEmpty(file.file_id)) continue;
                all.AddRange(await OnAirDeckApi.RequestOwnProductDownloadAsync(sessionToken, item.product_id, file.file_id, userInitiated));
            }
            return all.ToArray();
        }

        /// <summary>What the cached files would write, before anything is: .unitypackage assets by GUID, zip entries and copied files by SHA-256.</summary>
        private static List<IncomingFile> IncomingOf(List<KeyValuePair<string, string>> cached, string folder)
        {
            var incoming = new List<IncomingFile>();
            foreach (var pair in cached)
            {
                if (Installer.IsUnityPackage(pair.Value))
                {
                    var assets = UnityPackageArchive.Assets(pair.Key);
                    if (assets != null) foreach (var asset in assets) if (!asset.IsFolder) incoming.Add(new IncomingFile { Path = asset.Path, Guid = asset.Guid });
                }
                else if (Installer.IsZip(pair.Value)) incoming.AddRange(Installer.ZipTargets(pair.Key, folder));
                else
                    using (var stream = File.OpenRead(pair.Key))
                        incoming.Add(new IncomingFile { Path = folder + "/" + pair.Value, Sha256 = RemovalPlanner.StreamSha256(stream) });
            }
            return incoming;
        }

        /// <summary>The id the error report signature is keyed on: the product id, else the line item id.</summary>
        public static string ProductIdOf(PurchaseItem item)
        {
            return !string.IsNullOrEmpty(item.product_id) ? item.product_id : item.item_id ?? "";
        }

        /// <summary>The version the item's files belong to (the newest entry of its file history), or "".</summary>
        public static string VersionOf(PurchaseItem item)
        {
            return item.versions != null && item.versions.Length > 0 && item.versions[0] != null ? item.versions[0].version ?? "" : "";
        }

        /// <param name="interactiveImport">false: .unitypackage files import without the Unity import window (batch runs).</param>
        /// <param name="userInitiated">true (default): a user click started this, so the request may take back a released seat (X-Atelier-Reactivate).</param>
        /// <param name="otherOptionKeys">The PlacedFiles keys of the other options (line items) of this product, for <see cref="OptionOverwrite"/>.</param>
        /// <param name="confirmOverwrite">Asked with the paths when this option would write over another option's files; false cancels this option.</param>
        public static async Task<DownloadSummary> DownloadItemAsync(
            string sessionToken, PurchaseItem item, Action<string, float> onProgress, CancellationToken cancel,
            bool interactiveImport = true, bool userInitiated = true, ICollection<string> otherOptionKeys = null, Func<List<string>, bool> confirmOverwrite = null)
        {
            LegacyHandoff.ThrowIfWaiting();
            DownloadFile[] files;
            try
            {
                files = await RequestFilesAsync(sessionToken, item, userInitiated);
            }
            catch (Exception e)
            {
                // A device-limit or update-required refusal reaches the window; retrying runs the whole download again.
                DeviceLimitNotice.Report(e, item.name, async () => (object)await DownloadItemAsync(sessionToken, item, onProgress, cancel, interactiveImport, true, otherOptionKeys, confirmOverwrite), "download:" + item.name);
                throw;
            }
            // Signed license next to the product, written before any .unitypackage import starts: an import with scripts reloads
            // the domain, and the code after it would never run. A failure here must not undo a finished download; the window
            // refreshes missing licenses the next time it opens. A device-limit refusal is already in DeviceLimitNotice (IssueAsync
            // reports it; its retry only issues the license).
            return await PlaceFilesAsync(files, Installer.ProductFolder(item.name), onProgress, cancel, interactiveImport,
                product: item.name, version: VersionOf(item), productId: ProductIdOf(item), placedKey: PlacedFiles.PurchaseKey(item),
                otherOptionKeys: otherOptionKeys, confirmOverwrite: confirmOverwrite,
                beforeImports: async summary =>
                {
                    try
                    {
                        await LicenseService.IssueAsync(sessionToken, item, userInitiated);
                        summary.LicenseWritten = true;
                    }
                    catch (Exception e)
                    {
                        summary.LicenseRefused = e is DeviceLimitException || e is ManagerUpdateRequiredException;
                        Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Downloaded, but the license file could not be written: " + e.Message);
                    }
                });
        }

        /// <summary>
        /// Downloads every file to the cache, then places them: .unitypackage files go to <see cref="PackageImportQueue"/> (paths and
        /// GUIDs as the package has them, overwriting in place), zips are extracted into <paramref name="folder"/>, other files are
        /// copied there. All downloads finish before any import starts. The cache is cleaned on a failure.
        /// <paramref name="record"/> (a license version) is saved once everything is in place: now when nothing is imported, after
        /// the last .unitypackage import otherwise (see <see cref="PendingImportRecords"/>). <paramref name="product"/>,
        /// <paramref name="version"/> and <paramref name="productId"/> name the import for <see cref="InstallErrorWatch"/>.
        /// </summary>
        /// <param name="beforeImports">Runs once the loose files and zips are in place and before the first .unitypackage is queued
        /// (the license step: an import may reload the domain). It must not throw.</param>
        /// <param name="placedKey">The removal record of this product (<see cref="PlacedFiles"/>): every file placed here, and every file a
        /// .unitypackage lists, is added to it before the imports start. Null: nothing is recorded.</param>
        /// <param name="otherOptionKeys">Other options of the same product (<see cref="OptionOverwrite"/>): before anything is written, a file one of
        /// them created that this install would change is asked about with <paramref name="confirmOverwrite"/>.</param>
        internal static async Task<DownloadSummary> PlaceFilesAsync(DownloadFile[] files, string folder, Action<string, float> onProgress, CancellationToken cancel, bool interactiveImport,
            InstallRecord record = null, string product = null, string version = null, string productId = null, Func<DownloadSummary, Task> beforeImports = null,
            string placedKey = null, ICollection<string> otherOptionKeys = null, Func<List<string>, bool> confirmOverwrite = null)
        {
            if (files.Length == 0) throw new Exception("The server returned no downloadable files for this item.");

            var cached = new List<KeyValuePair<string, string>>(); // local path -> safe file name
            var cacheFolders = new List<string>();
            try
            {
                for (var i = 0; i < files.Length; i++)
                {
                    var safeName = PathSafety.SafeFileName(files[i].file_name);
                    if (safeName.Length == 0)
                    {
                        Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Skipped a file with an unusable name.");
                        continue;
                    }
                    var index = i;
                    var path = await Downloader.DownloadAsync(files[i].url, safeName,
                        p => { if (onProgress != null) onProgress(safeName, (index + p) / files.Length); }, cancel);
                    cacheFolders.Add(Path.GetDirectoryName(path));
                    cached.Add(new KeyValuePair<string, string>(path, safeName));
                }

                var summary = new DownloadSummary { Downloaded = cached.Count, Folder = folder };
                var packages = new List<string>();
                // Another option of this product created some of these files with other contents: asked now, before anything is written.
                // No: OptionOverwriteDeclinedException, and the cache goes below. Yes: those files pass to this option once written.
                var handOver = OptionOverwrite.Check(PackageInstallService.ProjectRoot, otherOptionKeys, () => IncomingOf(cached, folder), confirmOverwrite);
                // What this install creates (never a file or folder that is already there): what a removal may take away again.
                var tracker = new PlacementTracker(PackageInstallService.ProjectRoot);
                foreach (var pair in cached)
                {
                    var path = pair.Key;
                    var name = pair.Value;
                    if (Installer.IsUnityPackage(name))
                    {
                        packages.Add(path);
                        // The files it lists (not its folders), checked now, before its import writes them.
                        var assets = UnityPackageArchive.Assets(path);
                        if (assets != null) foreach (var asset in assets) if (!asset.IsFolder) tracker.BeforeWrite(asset.Path, asset.Guid);
                    }
                    else if (Installer.IsZip(name))
                    {
                        int skipped;
                        summary.Extracted += Installer.ExtractZip(path, summary.Folder, out skipped, tracker);
                        summary.SkippedUnsafe += skipped;
                    }
                    else
                    {
                        tracker.BeforeWrite(summary.Folder + "/" + name);
                        Installer.CopyFile(path, summary.Folder, name);
                        tracker.AfterWrite(summary.Folder + "/" + name);
                        summary.Copied++;
                    }
                }
                if (handOver.Count > 0)
                {
                    tracker.Adopt(handOver);
                    try { PlacedFiles.HandOver(PackageInstallService.ProjectRoot, otherOptionKeys, handOver); }
                    catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not hand the overwritten files over in the removal record: " + e.Message); }
                }
                if (!string.IsNullOrEmpty(placedKey) && tracker.Paths.Count + tracker.Touched.Count > 0)
                {
                    // Written before an import can reload the domain. A failure only means this install cannot be removed by record later.
                    try { PlacedFiles.Add(PackageInstallService.ProjectRoot, placedKey, tracker); }
                    catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not record the placed files (removal will ask to remove them by hand): " + e.Message); }
                }

                if (summary.Copied + summary.Extracted > 0) LegacyHandoffRunner.RefreshUnlessWaiting();

                // Package folders are deleted by the import queue once imported; the rest now.
                foreach (var cacheFolder in cacheFolders)
                {
                    var keep = false;
                    foreach (var package in packages) if (package.StartsWith(cacheFolder, StringComparison.OrdinalIgnoreCase)) keep = true;
                    if (!keep) Downloader.TryDeleteFolder(cacheFolder);
                }

                // A version is recorded only when something of it was placed. Entries skipped for an unusable name or an unsafe
                // zip path are the Manager's own safety choice and a reinstall would skip them again, so they do not block it.
                if (record != null && packages.Count == 0 && summary.Copied + summary.Extracted == 0)
                {
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Nothing of this version was placed, so the installed version is not recorded.");
                    record = null;
                }
                if (beforeImports != null) await beforeImports(summary);
                // A cancel pressed while the license step ran stops here: nothing is queued, the package downloads are cleaned up.
                cancel.ThrowIfCancellationRequested();
                if (packages.Count > 0)
                {
                    summary.PackagesQueued = packages.Count;
                    summary.QueuedPackages.AddRange(packages);
                    PackageImportQueue.Enqueue(packages, interactiveImport, record, product, version, productId);
                }
                else if (record != null) PackageImportQueue.SaveRecord(record);
                return summary;
            }
            catch
            {
                foreach (var cacheFolder in cacheFolders) Downloader.TryDeleteFolder(cacheFolder);
                throw;
            }
        }
    }
}
