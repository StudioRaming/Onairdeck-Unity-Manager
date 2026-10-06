using System;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    // JSON shapes of the OnAirDeck plugin-* edge functions (read with UnityEngine.JsonUtility,
    // so fields must stay public, [Serializable], and named exactly like the JSON keys).

    [Serializable]
    internal class ExchangeRequest
    {
        public string code;
        public string code_verifier;
    }

    [Serializable]
    internal class ExchangeResponse
    {
        public string session_token;
        public string user_id;
        public string email;
        public long expires_in;
    }

    [Serializable]
    internal class ErrorResponse
    {
        public string error;
    }
}
