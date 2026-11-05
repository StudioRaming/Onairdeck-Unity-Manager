using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Streams a presigned URL to the project's Library cache (never straight into Assets/, so a
    /// half-finished download can't be imported). Runs on the editor main thread via async/await.
    /// </summary>
    internal static class Downloader
    {
        /// <summary>&lt;project&gt;/Library/OnAirDeckCache — outside Assets/, not under version control.</summary>
        public static string CacheRoot
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.dataPath), "Library", "OnAirDeckCache"); }
        }

        /// <summary>Downloads to a fresh cache folder and returns the local path. Progress is 0..1.</summary>
        public static async Task<string> DownloadAsync(string url, string safeFileName, Action<float> onProgress, CancellationToken cancel)
        {
            var folder = Path.Combine(CacheRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, safeFileName);

            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbGET);
            request.downloadHandler = new DownloadHandlerFile(path) { removeFileOnAbort = true };
            request.timeout = 0; // large files; cancellation is handled below
            using (request)
            {
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        request.Abort();
                        TryDeleteFolder(folder);
                        throw new OperationCanceledException("Download cancelled.");
                    }
                    if (onProgress != null) onProgress(request.downloadProgress);
                    await Task.Delay(100);
                }

                if (!request.Succeeded())
                {
                    TryDeleteFolder(folder);
                    throw new DownloadFailedException("Download of " + safeFileName + " failed: HTTP " + request.responseCode + " " + request.error, request.responseCode);
                }
                if (onProgress != null) onProgress(1f);
                return path;
            }
        }

        /// <summary>
        /// A plugin-download file: <see cref="DownloadAsync"/>, then the server's checksum (<see cref="DownloadIntegrity"/>)
        /// before the caller extracts or imports it. A mismatch deletes the download and throws; no checksum is logged.
        /// </summary>
        public static async Task<string> DownloadVerifiedAsync(DownloadFile file, string safeFileName, Action<float> onProgress, CancellationToken cancel)
        {
            var path = await DownloadAsync(file.url, safeFileName, onProgress, cancel);
            try
            {
                if (DownloadIntegrity.Check(path, file.sha256, safeFileName) == DownloadIntegrity.Outcome.NoServerChecksum)
                    Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The store sent no checksum for " + safeFileName + "; installed without one.");
            }
            catch
            {
                TryDeleteFolder(Path.GetDirectoryName(path));
                throw;
            }
            return path;
        }

        public static void TryDeleteFolder(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not clean up " + folder + ": " + e.Message);
            }
        }
    }
}
