using System;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    // POST plugin-license { item_id, client } → { license, payload, file_name, public_key_pem, expires_at }
    [Serializable]
    internal class LicenseRequest
    {
        public string item_id;
        public string client;
    }

    // POST plugin-license { product_id, client } for the sellers own product (the contract names no field; item_id is empty there)
    [Serializable]
    internal class OwnProductLicenseRequest
    {
        public string product_id;
        public string client;
    }

    // POST plugin-license { license_id, client } for a seller license (contract v4); the payload product_id is the license id
    [Serializable]
    internal class SellerLicenseRequest
    {
        public string license_id;
        public string client;
    }

    /// <summary>The signed half of onairdeck.license, as the server describes it (informational here).</summary>
    [Serializable]
    internal class LicensePayload
    {
        public int v;
        public string iss;
        public string client;
        public string user_id;
        public string item_id;
        public string product_id;
        public string product_name;
        public long iat;
        public long exp;
        public string nonce;
    }

    [Serializable]
    internal class LicenseResponse
    {
        public string license;        // "<base64url(payload)>.<base64url(signature)>" — written verbatim to the file
        public LicensePayload payload;
        public string file_name;      // "onairdeck.license"
        public string public_key_pem;
        public string expires_at;
    }

    // GET plugin-version?client=unity-manager&current=<version>
    [Serializable]
    internal class ReleaseInfo
    {
        public string version;
        public string git_url;        // UPM install URL (with #tag once the release is tagged)
        public string download_url;
        public string notes;
        public string published_at;
        public string sha256 = "";    // hex SHA-256 of download_url's zip when the release row lists one (checked before the swap); "" from today's server
    }

    [Serializable]
    internal class VersionResponse
    {
        public string client;
        public string download_url;   // zip for Git-free installs, "" when the release has none (latest.download_url carries the same value)
        public ReleaseInfo latest;    // JsonUtility leaves a blank instance when the server sends null: check latest.version
        public string min_supported_version;
        public bool update_available;
        public bool must_update;
    }
}
