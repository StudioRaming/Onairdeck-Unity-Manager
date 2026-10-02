using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Browser sign-in (PKCE + localhost callback) and sign-out against OnAirDeck.
    /// Flow: open onairdeck.com/plugin-auth → user approves → browser redirects to our
    /// localhost listener with a one-time code → exchange it for a 30-day session token.
    /// </summary>
    internal static class AuthService
    {
        public const int SignInTimeoutMs = 3 * 60 * 1000;

        /// <summary>
        /// Runs the whole sign-in. <paramref name="onBrowserOpened"/> receives the sign-in URL
        /// so the window can offer it again if the browser didn't open.
        /// </summary>
        public static async Task<SavedSession> SignInAsync(Action<string> onBrowserOpened, CancellationToken cancel)
        {
            string verifier, challenge;
            Pkce.Create(out verifier, out challenge);
            var state = Pkce.RandomString(16);

            using (var listener = LoopbackListener.Start())
            {
                var url = OnAirDeckConfig.WebsiteUrl + "/plugin-auth" +
                          "?redirect_uri=" + Uri.EscapeDataString(listener.RedirectUri) +
                          "&state=" + Uri.EscapeDataString(state) +
                          "&code_challenge=" + Uri.EscapeDataString(challenge) +
                          "&client=" + OnAirDeckConfig.ClientId;

                Application.OpenURL(url);
                if (onBrowserOpened != null) onBrowserOpened(url);

                var code = await listener.WaitForCodeAsync(state, SignInTimeoutMs, cancel);
                cancel.ThrowIfCancellationRequested();

                var response = await OnAirDeckApi.ExchangeCodeAsync(code, verifier);
                return SessionStore.Save(response);
            }
        }

        /// <summary>
        /// Revokes the token on the server (best effort) and always forgets it locally.
        /// </summary>
        public static async Task SignOutAsync(SavedSession session)
        {
            SessionStore.Clear();
            if (session == null || string.IsNullOrEmpty(session.token)) return;
            try
            {
                var revoked = await OnAirDeckApi.SignOutAsync(session.token);
                if (!revoked) Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Signed out locally, but the server could not be reached to end the session.");
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Signed out locally, but ending the server session failed: " + e.Message);
            }
        }
    }
}
