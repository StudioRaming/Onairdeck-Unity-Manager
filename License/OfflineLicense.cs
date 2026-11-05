using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OnAirDeck.License
{
    /// <summary>The Unity device hash an offline licence file is bound to. Pure: the raw id comes from the adapter.</summary>
    public static class OfflineDeviceHash
    {
        public const string Prefix = "onairdeck-device-v1|";
        private const string UnsupportedId = "n/a"; // SystemInfo.unsupportedIdentifier

        /// <summary>Lowercase hex SHA-256 of UTF-8 prefix + id, or null when the id is unusable.</summary>
        public static string Compute(string rawId)
        {
            if (string.IsNullOrEmpty(rawId) || rawId == UnsupportedId) return null;
            using (var sha = SHA256.Create())
                return OfflineKey.Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(Prefix + rawId)));
        }
    }

    /// <summary>One public offline signing key: its id, modulus and the SHA-256 of that modulus as four numbers.</summary>
    internal sealed class OfflineKey
    {
        internal readonly string Kid;
        internal readonly byte[] Modulus;
        internal readonly ulong H0, H1, H2, H3;

        internal OfflineKey(string kid, byte[] modulus, ulong h0, ulong h1, ulong h2, ulong h3)
        {
            Kid = kid; Modulus = modulus; H0 = h0; H1 = h1; H2 = h2; H3 = h3;
        }

        /// <summary>True when the modulus still hashes to the stored value. Constant-time compare.</summary>
        internal bool Intact()
        {
            if (Modulus == null || Modulus.Length == 0) return false;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(Modulus);
            ulong diff = (Word(hash, 0) ^ H0) | (Word(hash, 1) ^ H1) | (Word(hash, 2) ^ H2) | (Word(hash, 3) ^ H3);
            return diff == 0;
        }

        internal RSAParameters Parameters
        {
            get { return new RSAParameters { Modulus = Modulus, Exponent = new byte[] { 1, 0, 1 } }; }
        }

        /// <summary>The key id: the first 16 hex characters of SHA-256 of the unsigned big-endian modulus.</summary>
        internal static string KidOf(byte[] modulus)
        {
            using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(modulus)).Substring(0, 16);
        }

        internal static ulong Word(byte[] hash, int index)
        {
            ulong value = 0;
            for (var i = 0; i < 8; i++) value = (value << 8) | hash[index * 8 + i];
            return value;
        }

        internal static string Hex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    /// <summary>
    /// Every offline key ever used. A perpetual file has no expiry and a closed member cannot get a newer build, so a key
    /// is never removed from this table. The online signing key is never in it, so an online signature cannot pass here.
    /// No production key exists yet: until one is added, every perpetual file reports OfflineKeyUnknown (fails closed).
    /// </summary>
    internal static class OfflineKeys
    {
        internal static readonly OfflineKey[] Production = new OfflineKey[0];

#if ONAIRDECK_VERIFICATION
        /// <summary>Only in the Verification program; a normal build has no way to add a key.</summary>
        internal static readonly List<OfflineKey> TestKeys = new List<OfflineKey>();
#endif

        internal static OfflineKey Find(string kid)
        {
            foreach (var key in Production)
                if (key.Kid == kid) return key;
#if ONAIRDECK_VERIFICATION
            foreach (var key in TestKeys)
                if (key.Kid == kid) return key;
#endif
            return null;
        }
    }

    /// <summary>
    /// Checks a perpetual device-bound licence: the file a member gets at withdrawal or service end. It checks signature,
    /// product and device only. There is no account, no expiry and no clock, so it works with no login and no internet.
    /// </summary>
    public static class OfflineLicenseVerifier
    {
        public const int PayloadVersion = 2;
        public const string Mode = "perpetual-device";
        public const string DevScheme = "unity-hash";

        /// <summary>Verifies a perpetual licence for the product on the device with this hash (null: device id unavailable).</summary>
        public static LicenseCheckResult Verify(string license, string expectedProductId, string deviceHash)
        {
            var result = new LicenseCheckResult();
            var text = (license ?? "").Trim();
            var dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1 || text.IndexOf('.', dot + 1) >= 0)
                return result.With(LicenseStatus.LicenseMalformed, "The offline licence file is not in the expected format.");

            byte[] payload, signature;
            try
            {
                payload = LicenseVerifier.FromBase64Url(text.Substring(0, dot));
                signature = LicenseVerifier.FromBase64Url(text.Substring(dot + 1));
            }
            catch (FormatException)
            {
                return result.With(LicenseStatus.LicenseMalformed, "The offline licence file is not valid base64url.");
            }

            // The key id only picks the key to try; nothing else is trusted before the signature passes.
            var fields = LicenseVerifier.ParsePayload(payload);
            var kid = fields == null ? null : MiniJson.GetString(fields, "kid");
            if (string.IsNullOrEmpty(kid))
                return result.With(LicenseStatus.LicenseMalformed, "The offline licence file has no key id.");
            var key = OfflineKeys.Find(kid);
            if (key == null)
                return result.With(LicenseStatus.OfflineKeyUnknown, "This licence file cannot be read by this build. Contact Atelier support.");
            if (!key.Intact() || !LicenseVerifier.VerifySignature(payload, signature, key.Parameters))
                return result.With(LicenseStatus.InvalidSignature, "The offline licence file's signature is not OnAirDeck's.");

            long version;
            if (!MiniJson.TryGetInt(fields, "v", out version))
                return result.With(LicenseStatus.LicenseMalformed, "The offline licence payload is incomplete.");
            // A perpetual payload that carries exp is malformed, so the two modes can never be confused.
            if (fields.ContainsKey("exp"))
                return result.With(LicenseStatus.LicenseMalformed, "The offline licence payload carries an expiry.");

            long iat;
            MiniJson.TryGetInt(fields, "iat", out iat);
            result.ProductId = MiniJson.GetString(fields, "product_id");
            result.ProductName = MiniJson.GetString(fields, "product_name");
            result.IssuedAtUnix = iat;

            if (version != PayloadVersion || MiniJson.GetString(fields, "iss") != LicenseVerifier.Issuer || MiniJson.GetString(fields, "client") != LicenseVerifier.Client
                || MiniJson.GetString(fields, "mode") != Mode || MiniJson.GetString(fields, "dev_scheme") != DevScheme)
                return result.With(LicenseStatus.WrongIssuer, "The offline licence file was not issued by OnAirDeck for Unity Manager.");
            if (string.IsNullOrEmpty(expectedProductId))
                return result.With(LicenseStatus.WrongProduct, "No product id was given to the license check.");
            if (!LicenseVerifier.SameId(result.ProductId, expectedProductId))
                return result.With(LicenseStatus.WrongProduct, "The offline licence file belongs to another product" + (string.IsNullOrEmpty(result.ProductName) ? "." : " (" + result.ProductName + ")."));
            if (string.IsNullOrEmpty(deviceHash))
                return result.With(LicenseStatus.OfflineDeviceUnavailable, "This PC's device id could not be read, so the offline licence file cannot be checked.");
            if (!SameDevice(MiniJson.GetString(fields, "dev"), deviceHash))
                return result.With(LicenseStatus.OfflineDeviceMismatch, "This licence file is for another PC. Atelier support can issue one for this PC: open Atelier Manager, Offline licences.");

            return result.With(LicenseStatus.Valid, "Licensed by an offline licence file fixed to this PC.");
        }

        private static bool SameDevice(string a, string b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
