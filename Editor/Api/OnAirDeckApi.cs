using System;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
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

        /// <summary>Every purchased line item, newest first (server caps at 50).</summary>
        public static async Task<PurchaseItem[]> GetPurchasesAsync(string sessionToken)
        {
            return (await GetPurchasesAndLicensesAsync(sessionToken)).purchases;
        }

        /// <summary>Purchases plus the seller licenses (with their versions) the account may download. Arrays are never null.</summary>
        public static async Task<PurchasesResponse> GetPurchasesAndLicensesAsync(string sessionToken)
        {
            using (var request = Get("plugin-purchases?client=" + OnAirDeckConfig.ClientId, sessionToken))
            {
                await request.SendAsync();
                ThrowIfFailed(request, "Loading purchases");
                var response = JsonUtility.FromJson<PurchasesResponse>(request.downloadHandler.text) ?? new PurchasesResponse();
                if (response.purchases == null) response.purchases = new PurchaseItem[0];
                if (response.licenses == null) response.licenses = new LicenseEntry[0];
                return response;
            }
        }

        /// <summary>
        /// Asks for presigned download links for one purchased item. The server records the
        /// download (the purchase becomes non-refundable) and returns only entitled files.
        /// </summary>
        /// <param name="reactivate">true only when a user click started this (header X-Atelier-Reactivate: 1).</param>
        public static async Task<DownloadFile[]> RequestDownloadAsync(string sessionToken, string itemId, bool reactivate = false)
        {
            var body = JsonUtility.ToJson(new DownloadRequest { item_id = itemId, client = OnAirDeckConfig.ClientId });
            using (var request = Post("plugin-download", body, sessionToken, reactivate))
            {
                request.timeout = 60;
                await request.SendAsync();
                ThrowIfFailed(request, "Preparing the download");
                var response = JsonUtility.FromJson<DownloadResponse>(request.downloadHandler.text);
                return response != null && response.files != null ? response.files : new DownloadFile[0];
            }
        }

        /// <summary>
        /// Presigned link for one file of the signed-in sellers OWN product (owner path: never counts as a download,
        /// no device gate). Used instead of <see cref="RequestDownloadAsync"/> when <see cref="PurchaseItem.IsOwn"/>.
        /// </summary>
        public static async Task<DownloadFile[]> RequestOwnProductDownloadAsync(string sessionToken, string productId, string fileId, bool reactivate = false)
        {
            var body = JsonUtility.ToJson(new OwnProductDownloadRequest { product_id = productId, file_id = fileId, client = OnAirDeckConfig.ClientId });
            using (var request = Post("plugin-download", body, sessionToken, reactivate))
            {
                request.timeout = 60;
                await request.SendAsync();
                ThrowIfFailed(request, "Preparing the download");
                var response = JsonUtility.FromJson<DownloadResponse>(request.downloadHandler.text);
                return response != null && response.files != null ? response.files : new DownloadFile[0];
            }
        }

        /// <summary>
        /// Presigned link (30 minutes) for one seller license version. Requires an active grant on its license;
        /// the server records the download like an item download.
        /// </summary>
        public static async Task<DownloadFile[]> RequestLicenseVersionDownloadAsync(string sessionToken, string versionId, bool reactivate = false)
        {
            var body = JsonUtility.ToJson(new LicenseDownloadRequest { license_version_id = versionId, client = OnAirDeckConfig.ClientId });
            using (var request = Post("plugin-download", body, sessionToken, reactivate))
            {
                request.timeout = 60;
                await request.SendAsync();
                ThrowIfFailed(request, "Preparing the package download");
                var response = JsonUtility.FromJson<DownloadResponse>(request.downloadHandler.text);
                return response != null && response.files != null ? response.files : new DownloadFile[0];
            }
        }

        /// <summary>
        /// Signed onairdeck.license for one purchased item. Same ownership rules as a download,
        /// but issuing never counts as a download, so it can be repeated to refresh the file.
        /// </summary>
        public static async Task<LicenseResponse> RequestLicenseAsync(string sessionToken, string itemId, bool reactivate = false)
        {
            return await RequestLicenseCoreAsync(sessionToken, JsonUtility.ToJson(new LicenseRequest { item_id = itemId, client = OnAirDeckConfig.ClientId }), reactivate);
        }

        /// <summary>Signed license for the sellers own product (no purchase item exists, so the product id is sent).</summary>
        public static async Task<LicenseResponse> RequestOwnProductLicenseAsync(string sessionToken, string productId, bool reactivate = false)
        {
            return await RequestLicenseCoreAsync(sessionToken, JsonUtility.ToJson(new OwnProductLicenseRequest { product_id = productId, client = OnAirDeckConfig.ClientId }), reactivate);
        }

        /// <summary>Signed license for a seller license the account holds (contract v4). OnAirDeckLicense.Check(license_id) passes on it.</summary>
        public static async Task<LicenseResponse> RequestSellerLicenseAsync(string sessionToken, string licenseId, bool reactivate = false)
        {
            return await RequestLicenseCoreAsync(sessionToken, JsonUtility.ToJson(new SellerLicenseRequest { license_id = licenseId, client = OnAirDeckConfig.ClientId }), reactivate);
        }

        private static async Task<LicenseResponse> RequestLicenseCoreAsync(string sessionToken, string body, bool reactivate)
        {
            using (var request = Post("plugin-license", body, sessionToken, reactivate))
            {
                await request.SendAsync();
                ThrowIfFailed(request, "Issuing the license");
                var response = JsonUtility.FromJson<LicenseResponse>(request.downloadHandler.text);
                if (response == null || string.IsNullOrEmpty(response.license))
                    throw new Exception("Issuing the license failed: the server returned no license.");
                return response;
            }
        }

        /// <summary>Release manifest for the Unity Manager itself. Needs no session (the publishable key is enough).</summary>
        public static async Task<VersionResponse> GetVersionAsync(string currentVersion)
        {
            var query = "plugin-version?client=unity-manager&current=" + Uri.EscapeDataString(currentVersion ?? "");
            using (var request = Get(query, OnAirDeckConfig.PublishableKey))
            {
                await request.SendAsync();
                if (!request.Succeeded()) throw new Exception("Update check failed: " + request.Describe());
                var response = JsonUtility.FromJson<VersionResponse>(request.downloadHandler.text);
                if (response == null) throw new Exception("Update check failed: empty response.");
                return response;
            }
        }

        /// <summary>Plain GET of a public text file (a VPM listing), as a string. HTTPS only.</summary>
        public static async Task<string> GetTextAsync(string url)
        {
            PackageDownloadChecks.RequireHttps(url);
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = TimeoutSeconds;
                request.SetRequestHeader("Accept", "application/json");
                await request.SendAsync();
                if (!request.Succeeded()) throw new Exception("Loading " + url + " failed: " + request.Describe());
                return request.downloadHandler.text;
            }
        }

        /// <param name="reactivate">Adds X-Atelier-Reactivate: 1. Pass true only for a request a user click started.</param>
        internal static UnityWebRequest Post(string function, string jsonBody, string bearer, bool reactivate = false)
        {
            var request = new UnityWebRequest(DevServer.FunctionsUrl + "/" + function, "POST");
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");
            AddHeaders(request, bearer, reactivate);
            return request;
        }

        internal static UnityWebRequest Get(string functionAndQuery, string bearer)
        {
            var request = UnityWebRequest.Get(DevServer.FunctionsUrl + "/" + functionAndQuery);
            AddHeaders(request, bearer);
            return request;
        }

        /// <summary>
        /// Throws SessionExpiredException for 401, DeviceLimitException for 409 device_limit,
        /// ManagerUpdateRequiredException for 426, and one ApiException (short message, body kept apart) otherwise.
        /// </summary>
        internal static void ThrowIfFailed(UnityWebRequest request, string action)
        {
            if (request.Succeeded()) return;
            if (request.responseCode == 401) throw new SessionExpiredException("Your sign-in has expired. Please sign in again.");
            var body = request.downloadHandler != null ? request.downloadHandler.text : null;
            throw ApiErrorParser.ToException(request.responseCode, body, action, request.error);
        }

        /// <summary>This account's devices that hold a seat on a limited license, plus the limits. Arrays are never null.</summary>
        public static async Task<DevicesResponse> GetDevicesAsync(string sessionToken)
        {
            using (var request = Get("plugin-devices", sessionToken))
            {
                await request.SendAsync();
                ThrowIfFailed(request, "Loading devices");
                var response = JsonUtility.FromJson<DevicesResponse>(request.downloadHandler.text) ?? new DevicesResponse();
                if (response.devices == null) response.devices = new DeviceEntry[0];
                if (response.limits == null) response.limits = new DeviceLimitLicense[0];
                foreach (var device in response.devices)
                    if (device != null && device.licenses == null) device.licenses = new DeviceLicenseRef[0];
                return response;
            }
        }

        /// <summary>Gives up the seats of one device (any of the account's devices, this one included). Returns how many seats were freed.</summary>
        public static async Task<int> ReleaseDeviceAsync(string sessionToken, string deviceId)
        {
            var body = JsonUtility.ToJson(new ReleaseDeviceRequest { device_id = deviceId });
            using (var request = Post("plugin-devices", body, sessionToken))
            {
                await request.SendAsync();
                ThrowIfFailed(request, "Releasing the device");
                var response = JsonUtility.FromJson<ReleaseDeviceResponse>(request.downloadHandler.text);
                if (response == null || !response.ok) throw new ApiException("Releasing the device failed: the server did not confirm it.", request.responseCode, "", request.downloadHandler.text);
                return response.released;
            }
        }

        private static void AddHeaders(UnityWebRequest request, string bearer, bool reactivate = false)
        {
            request.SetRequestHeader("apikey", OnAirDeckConfig.PublishableKey);
            request.SetRequestHeader("Authorization", "Bearer " + bearer);
            request.SetRequestHeader("Accept", "application/json");
            request.SetRequestHeader(DeviceIdentityCore.ManagerHeaderName, DeviceIdentityCore.ManagerHeaderValue);
            // Calls that carry a session token also say which PC they come from (the exchange and version calls send the publishable key instead).
            if (bearer != OnAirDeckConfig.PublishableKey) DeviceIdentity.Apply(request);
            if (reactivate) request.SetRequestHeader(DeviceIdentityCore.ReactivateHeaderName, DeviceIdentityCore.ReactivateHeaderValue);
            request.timeout = TimeoutSeconds;
        }
    }
}
