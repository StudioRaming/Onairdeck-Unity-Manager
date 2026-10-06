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
                    throw new Exception("Download of " + safeFileName + " failed: HTTP " + request.responseCode + " " + request.error);
                }
                if (onProgress != null) onProgress(1f);
                return path;
            }
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
