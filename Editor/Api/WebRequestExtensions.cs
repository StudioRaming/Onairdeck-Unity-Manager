using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Small helpers so UnityWebRequest can be awaited on every Unity version from 2019.4
    /// (no UniTask). Completion is raised on the editor main thread, so code after the await
    /// may touch Unity APIs.
    /// </summary>
    internal static class WebRequestExtensions
    {
        // Requests still running. A script reload replaces the managed side of every handler while the native request goes on,
        // so they are aborted and disposed in beforeAssemblyReload instead of calling back into a domain that is going away.
        private static readonly List<UnityWebRequest> InFlight = new List<UnityWebRequest>();
        private static bool _reloading;

        static WebRequestExtensions()
        {
            AssemblyReloadEvents.beforeAssemblyReload += ReleaseInFlight;
        }

        public static Task<UnityWebRequest> SendAsync(this UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            var operation = request.SendTracked();
            // Called immediately if the operation already finished.
            operation.completed += _ => { if (!_reloading) tcs.TrySetResult(request); };
            return tcs.Task;
        }

        /// <summary>SendWebRequest, remembered until it completes or <see cref="Untrack"/> (see <see cref="InFlight"/>).</summary>
        public static UnityWebRequestAsyncOperation SendTracked(this UnityWebRequest request)
        {
            var operation = request.SendWebRequest();
            InFlight.Add(request);
            operation.completed += _ => InFlight.Remove(request);
            return operation;
        }

        /// <summary>For a request its owner disposes before it completes (a cancelled download).</summary>
        public static void Untrack(UnityWebRequest request)
        {
            InFlight.Remove(request);
        }

        private static void ReleaseInFlight()
        {
            _reloading = true;   // the domain goes away: no continuation runs from here on
            var running = new List<UnityWebRequest>(InFlight);
            InFlight.Clear();
            foreach (var request in running)
            {
                try { request.Abort(); } catch { /* already finished or disposed */ }
                try { request.Dispose(); } catch { /* already disposed */ }
            }
        }

        public static bool Succeeded(this UnityWebRequest request)
        {
#if UNITY_2020_2_OR_NEWER
            return request.result == UnityWebRequest.Result.Success;
#else
            // Pre-2020.2 API (not obsolete on those versions).
            return !request.isNetworkError && !request.isHttpError;
#endif
        }

        public static string Describe(this UnityWebRequest request)
        {
            var body = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (string.IsNullOrEmpty(body)) return "HTTP " + request.responseCode + " " + request.error;
            if (body.Length > 240) body = body.Substring(0, 240) + "…";
            return "HTTP " + request.responseCode + " — " + body;
        }
    }
}
