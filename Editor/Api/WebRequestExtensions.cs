using System.Threading.Tasks;
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
        public static Task<UnityWebRequest> SendAsync(this UnityWebRequest request)
        {
            var tcs = new TaskCompletionSource<UnityWebRequest>();
            var operation = request.SendWebRequest();
            // Called immediately if the operation already finished.
            operation.completed += _ => tcs.TrySetResult(request);
            return tcs.Task;
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
