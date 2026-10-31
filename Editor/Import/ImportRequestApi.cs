using System;
using System.Threading.Tasks;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>An error answer of plugin-import-requests: <see cref="ApiException.Status"/> (0 when nothing answered) and the body's error code.</summary>
    internal sealed class ImportRequestException : ApiException
    {
        public ImportRequestException(string message, long status, string code, string body) : base(message, status, code, body) { }
    }

    /// <summary>plugin-import-requests?client=unity: list the account's pending requests and move one through claim, renew and its end.</summary>
    internal static class ImportRequestApi
    {
        private const string Function = "plugin-import-requests?client=unity";

        /// <summary>Pending requests for Unity, newest first. 401 → <see cref="SessionExpiredException"/>; other failures → <see cref="ImportRequestException"/>.</summary>
        public static async Task<ImportRequest[]> ListAsync(string sessionToken)
        {
            using (var request = OnAirDeckApi.Get(Function, sessionToken))
            {
                await request.SendAsync();
                Check(request, "Listing import requests");
                var response = JsonUtility.FromJson<ImportRequestsResponse>(request.downloadHandler.text);
                return response != null && response.requests != null ? response.requests : new ImportRequest[0];
            }
        }

        /// <summary>One transition: claim, renew, done, failed, declined or release. Returns the server's answer.</summary>
        public static async Task<ImportActionResponse> PostAsync(string sessionToken, string action, string requestId, string claimId, string code)
        {
            var body = JsonUtility.ToJson(new ImportActionRequest { action = action, request_id = requestId ?? "", claim_id = claimId ?? "", code = code ?? "" });
            using (var request = OnAirDeckApi.Post(Function, body, sessionToken))
            {
                await request.SendAsync();
                Check(request, "Import request " + action);
                return JsonUtility.FromJson<ImportActionResponse>(request.downloadHandler.text) ?? new ImportActionResponse();
            }
        }

        private static void Check(UnityEngine.Networking.UnityWebRequest request, string action)
        {
            if (request.Succeeded()) return;
            if (request.responseCode == 401) throw new SessionExpiredException("Your sign-in has expired. Please sign in again.");
            var body = request.downloadHandler != null ? request.downloadHandler.text : null;
            var code = ApiErrorParser.ReadErrorCode(body) ?? "";
            throw new ImportRequestException(action + " failed (" + request.responseCode + (code.Length > 0 ? " " + code : "") + ").", request.responseCode, code, body);
        }
    }
}
