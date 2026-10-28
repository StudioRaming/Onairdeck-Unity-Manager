using System;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Keeps the signed-in session in EditorPrefs: per user and per machine, shared by every
    /// Unity project, and outside the project folder so it can't be committed or shared with
    /// the project. Stored as plain text locally (like the Warudo plugin); the real protection
    /// is server-side expiry (30 days) and sign-out revocation.
    /// </summary>
    internal static class SessionStore
    {
        internal const string DefaultKey = "OnAirDeck.UnityManager.Session";

        /// <summary>DefaultKey, or its ".Dev" twin while a dev build points at a local server (see DevServer.Redirected).</summary>
        internal static string ActiveKey { get { return DevServer.Separate(DefaultKey); } }

        public static SavedSession Load()
        {
            return Load(ActiveKey);
        }

        // The key overloads exist so verification can use a throwaway EditorPrefs key without
        // changing any shared state; the window and AuthService always use DefaultKey.
        internal static SavedSession Load(string key)
        {
            try
            {
                var json = EditorPrefs.GetString(key, "");
                if (string.IsNullOrEmpty(json)) return null;
                var session = JsonUtility.FromJson<SavedSession>(json);
                if (session == null || string.IsNullOrEmpty(session.token) || session.IsExpired)
                {
                    Clear(key);
                    return null;
                }
                return session;
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not read the saved sign-in: " + e.Message);
                return null;
            }
        }

        public static SavedSession Save(ExchangeResponse response)
        {
            var lifetime = response.expires_in > 0 ? response.expires_in : 30L * 24 * 60 * 60;
            var session = new SavedSession
            {
                token = response.session_token,
                userId = response.user_id ?? "",
                email = response.email ?? "",
                expiresAtUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + lifetime,
            };
            EditorPrefs.SetString(ActiveKey, JsonUtility.ToJson(session));
            return session;
        }

        public static void Clear()
        {
            Clear(ActiveKey);
        }

        internal static void Clear(string key)
        {
            EditorPrefs.DeleteKey(key);
        }
    }
}
