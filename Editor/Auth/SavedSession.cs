using System;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    /// <summary>The signed-in session kept in EditorPrefs by SessionStore (plain .NET, so it can be tested outside Unity).</summary>
    [Serializable]
    internal class SavedSession
    {
        public string token;
        public string userId;
        public string email;
        public long expiresAtUnix;

        public bool IsExpired
        {
            get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= expiresAtUnix; }
        }
    }
}
