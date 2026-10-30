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
        public int max_devices;       // 0 = no device limit
        public int devices_used;      // devices holding a seat on this license
        public bool this_device;      // this PC holds a seat
        public string seller_id = "";
        public string seller_name = "";
        public string thumbnail_url = "";   // first linked product thumbnail, or ""
        public string source = "";          // "grant" or "own" (the sellers own license); "" from an older server
        public string description = "";     // plain text, at most 4000 chars; "" when none
        public string product_url = "";     // public product page; "" when the product is not public

        public bool IsOwn { get { return source == "own"; } }
        public LicenseVersionEntry[] versions;   // active versions, newest first, at most 20
    }

    [Serializable]
    internal class LicenseVersionEntry
    {
        public string version_id;
        public string version_label;
        public string file_name;
        public long file_size;
        public string released_at = "";
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
        public string thumbnail_url = "";
        public string seller_id = "";
        public string seller_name = "";
        public string category = "";
        public string purchased_at = "";   // ISO 8601 UTC (an own product: its creation time)
        public string source = "";         // "purchase" or "own" (the sellers own product); "" from an older server
        public string description = "";    // plain text, at most 4000 chars; "" when none
        public string product_url = "";    // public product page; "" when the product is not public
        public PurchaseVersion[] versions = new PurchaseVersion[0]; // file history, newest first (one entry when there is no history)

        public bool IsOwn { get { return source == "own"; } }

        /// <summary>
        /// The server sends every purchased line item; one without Unity files (a WARUDO product, or a product whose files
        /// now come through its seller license) has nothing to install in Unity, so the Manager does not list it.
        /// </summary>
        public bool HasUnityFiles { get { return files != null && files.Length > 0; } }

        /// <summary>An own product is downloaded by product_id + file_id, a purchase by item_id.</summary>
        public bool CanDownload
        {
            get
            {
                if (!HasUnityFiles) return false;
                return IsOwn ? !string.IsNullOrEmpty(product_id) : !string.IsNullOrEmpty(item_id);
            }
        }
    }

    /// <summary>One entry of a purchased product's file history.</summary>
    [Serializable]
    internal class PurchaseVersion
    {
        public string file_id = "";
        public string version = "";
        public string released_at = "";
        public string notes = "";
        public long size_bytes;
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

    // POST plugin-download { product_id, file_id, client } (the sellers own product; never with item_id or license_version_id)
    [Serializable]
    internal class OwnProductDownloadRequest
    {
        public string product_id;
        public string file_id;
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
