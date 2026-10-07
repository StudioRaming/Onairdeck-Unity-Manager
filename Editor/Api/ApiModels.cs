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

    // GET plugin-purchases?client=unity → { purchases: [...] }
    [Serializable]
    internal class PurchasesResponse
    {
        public PurchaseItem[] purchases;
    }

    /// <summary>One purchased line item. `files` lists only the Unity-enabled files this item is entitled to.</summary>
    [Serializable]
    internal class PurchaseItem
    {
        public string name;
        public string date;
        public string item_id;     // empty for legacy orders without line items
        public string product_id;
        public PurchaseFile[] files;
        public bool downloaded;
        public bool update_available;

        public bool CanDownload
        {
            get { return !string.IsNullOrEmpty(item_id) && files != null && files.Length > 0; }
        }
    }

    [Serializable]
    internal class PurchaseFile
    {
        public string file_id;
        public string file_name;
    }

    // POST plugin-download { item_id, client } → { files: [{ url, file_name }] }
    [Serializable]
    internal class DownloadRequest
    {
        public string item_id;
        public string client;
    }

    [Serializable]
    internal class DownloadResponse
    {
        public DownloadFile[] files;
    }

    [Serializable]
    internal class DownloadFile
    {
        public string url;        // presigned, valid for 30 minutes
        public string file_name;
    }
}
