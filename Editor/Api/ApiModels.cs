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

    // GET plugin-purchases?client=unity → { purchases: [...], licenses: [...] }
    [Serializable]
    internal class PurchasesResponse
    {
        public PurchaseItem[] purchases;
        public LicenseEntry[] licenses;   // seller licenses the account holds an active grant on, with their versions
    }

    /// <summary>One seller license the account may download versions of (an OnAirDeck purchase or an approved BOOTH claim).</summary>
    [Serializable]
    internal class LicenseEntry
    {
        public string license_id;
        public string name;
        public string[] rights;
        public LicenseVersionEntry[] versions;   // active versions, newest first, at most 20
    }

    [Serializable]
    internal class LicenseVersionEntry
    {
        public string version_id;
        public string version_label;
        public string file_name;
        public long file_size;
        public string released_at;
        public string release_notes;
        public string package_name;      // "" unless file_name looks like <package>-<version>.zip
        public string package_version;   // "" likewise

        public bool IsPackage
        {
            get { return !string.IsNullOrEmpty(package_name) && !string.IsNullOrEmpty(package_version); }
        }
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

    // POST plugin-download { license_version_id, client } → same response. A separate type from
    // DownloadRequest: JsonUtility writes every field, and the server rejects item_id together with
    // license_version_id, so an empty item_id must not be sent.
    [Serializable]
    internal class LicenseDownloadRequest
    {
        public string license_version_id;
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
