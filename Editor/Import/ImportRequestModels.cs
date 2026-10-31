using System;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    // plugin-import-requests?client=unity (contract v1, Vdesk-handoff/manager-import-contract.md). JsonUtility field names = JSON keys.
    // The server sends no null in a 200 body; an object that does not apply is absent, which JsonUtility turns into an empty object.

    [Serializable]
    internal class ImportRequestsResponse
    {
        public ImportRequest[] requests;
    }

    /// <summary>One "가져오기" press on the website waiting for this account's Unity Manager.</summary>
    [Serializable]
    internal class ImportRequest
    {
        public string request_id = "";
        public string target = "";
        /// <summary>"item" (a purchased line item) or "license" (a license held without a purchase, e.g. a BOOTH claim).</summary>
        public string kind = "";
        public string item_id = "";
        public string license_id = "";
        public string product_id = "";
        public string created_at = "";
        public string expires_at = "";
        /// <summary>kind "item": the plugin-purchases row of the item. Absent (empty after JsonUtility) for kind "license".</summary>
        public PurchaseItem purchase;
        /// <summary>Licenses the item grants (kind "item"), or the one license (kind "license").</summary>
        public LicenseEntry[] licenses;
    }

    [Serializable]
    internal class ImportActionRequest
    {
        public string action = "";
        public string request_id = "";
        public string claim_id = "";
        public string code = "";
    }

    [Serializable]
    internal class ImportActionResponse
    {
        public string request_id = "";
        public string status = "";
        public string claim_id = "";
        public string lease_expires_at = "";
        public int lease_seconds;
    }

    /// <summary>
    /// The request this project holds, kept in Library/OnAirDeck/import-claim.json (written atomically) so a script reload or an
    /// editor restart in the middle of an import still reports it. Phases: "claimed" (the confirm is up), "running" (the install
    /// path runs), "report" (the result is known and waits to be posted).
    /// </summary>
    [Serializable]
    internal class ImportClaim
    {
        public string request_id = "";
        public string claim_id = "";
        public string phase = "";
        public string action = "";
        public string code = "";
        /// <summary>The package a package install is putting in (a reload may end the code that awaits it).</summary>
        public string package_name = "";
        public string package_version = "";
        public string product = "";
        public long claimed_at;
        public long renewed_at;
    }
}
