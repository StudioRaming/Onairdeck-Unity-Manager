using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
    /// <summary>Thrown when the server no longer accepts the saved session (401).</summary>
    internal sealed class SessionExpiredException : Exception
    {
        public SessionExpiredException(string message) : base(message) { }
    }

    /// <summary>Calls to the OnAirDeck plugin-* edge functions.</summary>
    internal static class OnAirDeckApi
    {
        private const int TimeoutSeconds = 30;

        /// <summary>Exchanges the one-time browser code for a 30-day session token (PKCE).</summary>
        public static async Task<ExchangeResponse> ExchangeCodeAsync(string code, string verifier)
        {
            var body = JsonUtility.ToJson(new ExchangeRequest { code = code, code_verifier = verifier });
            using (var request = Post("plugin-auth-exchange", body, OnAirDeckConfig.PublishableKey))
            {
                await request.SendAsync();
                if (!request.Succeeded()) throw new Exception("Sign-in failed: " + request.Describe());
                var response = JsonUtility.FromJson<ExchangeResponse>(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.session_token))
                    throw new Exception("Sign-in failed: the server returned no session.");
                return response;
            }
        }

        /// <summary>
        /// Revokes the session on the server. Returns false if the server could not be reached;
        /// the caller should still forget the token locally.
        /// </summary>
        public static async Task<bool> SignOutAsync(string sessionToken)
        {
            using (var request = Post("plugin-signout", "{}", sessionToken))
            {
                await request.SendAsync();
                return request.Succeeded();
            }
        }

        internal static UnityWebRequest Post(string function, string jsonBody, string bearer)
        {
            var request = new UnityWebRequest(OnAirDeckConfig.FunctionsUrl + "/" + function, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            AddHeaders(request, bearer);
            return request;
        }

        internal static UnityWebRequest Get(string functionAndQuery, string bearer)
        {
            var request = UnityWebRequest.Get(OnAirDeckConfig.FunctionsUrl + "/" + functionAndQuery);
            AddHeaders(request, bearer);
            return request;
        }

        /// <summary>Throws SessionExpiredException for 401, a readable Exception otherwise.</summary>
        internal static void ThrowIfFailed(UnityWebRequest request, string action)
        {
            if (request.Succeeded()) return;
            if (request.responseCode == 401) throw new SessionExpiredException("Your sign-in has expired. Please sign in again.");
            throw new Exception(action + " failed: " + request.Describe());
        }

        private static void AddHeaders(UnityWebRequest request, string bearer)
        {
            request.SetRequestHeader("apikey", OnAirDeckConfig.PublishableKey);
            request.SetRequestHeader("Authorization", "Bearer " + bearer);
            request.SetRequestHeader("Accept", "application/json");
            request.timeout = TimeoutSeconds;
        }
    }
}
