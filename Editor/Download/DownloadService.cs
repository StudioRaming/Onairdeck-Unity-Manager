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
        public string Folder;

        public override string ToString()
        {
            var parts = new List<string>();
            if (Copied > 0) parts.Add(Copied + " file" + (Copied == 1 ? "" : "s") + " copied");
            if (Extracted > 0) parts.Add(Extracted + " file" + (Extracted == 1 ? "" : "s") + " extracted");
            if (PackagesQueued > 0) parts.Add(PackagesQueued + " package" + (PackagesQueued == 1 ? "" : "s") + " opened for import");
            if (SkippedUnsafe > 0) parts.Add(SkippedUnsafe + " unsafe entr" + (SkippedUnsafe == 1 ? "y" : "ies") + " skipped");
            var text = parts.Count == 0 ? "Nothing was installed." : string.Join(", ", parts.ToArray()) + ".";
            return Copied + Extracted > 0 ? text + " Folder: " + Folder : text;
        }
    }

    /// <summary>
    /// One purchased item end to end: ask the server for links, download everything to the
    /// cache, then install. All downloads finish before any import starts, because importing
    /// scripts reloads the editor and would abandon in-flight work.
    /// </summary>
    internal static class DownloadService
    {
        public static async Task<DownloadSummary> DownloadItemAsync(
            string sessionToken, PurchaseItem item, Action<string, float> onProgress, CancellationToken cancel)
        {
            var files = await OnAirDeckApi.RequestDownloadAsync(sessionToken, item.item_id);
            if (files.Length == 0) throw new Exception("The server returned no downloadable files for this item.");

            var cached = new List<KeyValuePair<string, string>>(); // local path → safe file name
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

                var summary = new DownloadSummary { Downloaded = cached.Count, Folder = Installer.ProductFolder(item.name) };
                var packages = new List<string>();
                foreach (var pair in cached)
                {
                    var path = pair.Key;
                    var name = pair.Value;
                    if (Installer.IsUnityPackage(name))
                    {
                        packages.Add(path);
                    }
                    else if (Installer.IsZip(name))
                    {
                        int skipped;
                        summary.Extracted += Installer.ExtractZip(path, summary.Folder, out skipped);
                        summary.SkippedUnsafe += skipped;
                    }
                    else
                    {
                        Installer.CopyFile(path, summary.Folder, name);
                        summary.Copied++;
                    }
                }

                if (summary.Copied + summary.Extracted > 0) AssetDatabase.Refresh();

                // Package folders are deleted by the import queue once imported; the rest now.
                foreach (var folder in cacheFolders)
                {
                    var keep = false;
                    foreach (var package in packages) if (package.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) keep = true;
                    if (!keep) Downloader.TryDeleteFolder(folder);
                }

                if (packages.Count > 0)
                {
                    summary.PackagesQueued = packages.Count;
                    PackageImportQueue.Enqueue(packages);
                }
                return summary;
            }
            catch
            {
                foreach (var folder in cacheFolders) Downloader.TryDeleteFolder(folder);
                throw;
            }
        }
    }
}
