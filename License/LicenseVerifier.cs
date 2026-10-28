using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OnAirDeck.License
{
    /// <summary>
    /// Offline verification of an onairdeck.license string:
    /// "&lt;base64url(payload JSON)&gt;.&lt;base64url(signature)&gt;", where the signature is
    /// RSASSA-PKCS1-v1_5 / SHA-256 over the payload bytes, made by OnAirDeck's plugin-license service.
    /// No file or network access here; OnAirDeckLicense adds the file and session-marker lookup.
    /// </summary>
    public static class LicenseVerifier
    {
        public const string Issuer = "onairdeck";
        public const string Client = "unity-manager";
        public const int PayloadVersion = 1;

        /// <summary>How far in the future "iat" may be before the license counts as not yet valid.</summary>
        public const long ClockSkewSeconds = 5 * 60;

        // OnAirDeck's license signing key, public half (RSA-2048; SubjectPublicKeyInfo SHA-256 starts 5b1a0cce51e260f2).
        // Only the private half, which never leaves the plugin-license edge function, can produce a valid signature.
        private const string ModulusBase64 =
            "vymo5HYZxfibsKluIDrpt0xcP3A40Xa/afKx/t7mM8ezfEfhQDn30S9ZwjeSVuvvdPxM7W29xDWFrgpY/OPVBpf/VRh7oSNF4WqSo6ziKvme1lXJV9Ag2fBNgoj1p117ke+Znf39lYX8JcSWVwdh5UI8rPpeE6NrODLJ1JPx2yqnzg9foEa/q84u+37uaOTdSaBrHsp/EjV0YYLOKvulnlUOeGIF6Oh2bs+UKIRKeuX+sKjFfer56Aq6dG9xNb2u/ZwEKa4gr48Mtf5YsIdLPGS7JkdXTXoui60bZIPQIKXbunf71rpsxtEZLM4dFJWgkyT3jSmMOGZf4Y83P/zDrw==";
        private const string ExponentBase64 = "AQAB";

        /// <summary>The embedded OnAirDeck public key.</summary>
        public static RSAParameters PublicKey
        {
            get
            {
#if ATELIER_DEV_SERVER
                // Developer builds only: a local stack signs with its own key. Never defined in a normal install.
                RSAParameters devKey;
                var pem = DevPublicKeyPem == null ? null : DevPublicKeyPem();
                if (!string.IsNullOrWhiteSpace(pem) && TryParsePublicKeyPem(pem, out devKey)) return devKey;
#endif
                return new RSAParameters
                {
                    Modulus = Convert.FromBase64String(ModulusBase64),
                    Exponent = Convert.FromBase64String(ExponentBase64)
                };
            }
        }

#if ATELIER_DEV_SERVER
        /// <summary>Set by the Manager's DevServerHook: the PEM of a local stack's public key, or empty.</summary>
        internal static Func<string> DevPublicKeyPem;
#endif

        /// <summary>Reads an RSA public key from PEM (SubjectPublicKeyInfo "PUBLIC KEY" or PKCS#1 "RSA PUBLIC KEY"). False when it is not one.</summary>
        internal static bool TryParsePublicKeyPem(string pem, out RSAParameters key)
        {
            key = new RSAParameters();
            try
            {
                var base64 = new System.Text.StringBuilder();
                foreach (var raw in (pem ?? "").Replace("\r", "").Split('\n'))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("-----", StringComparison.Ordinal)) continue;
                    base64.Append(line);
                }
                var der = Convert.FromBase64String(base64.ToString());
                int tag, start, length;
                if (!Tlv(der, 0, der.Length, out tag, out start, out length) || tag != 0x30) return false;
                var end = start + length;
                int innerTag, innerStart, innerLength;
                if (!Tlv(der, start, end, out innerTag, out innerStart, out innerLength)) return false;
                if (innerTag == 0x30)
                {
                    // SubjectPublicKeyInfo: { algorithm, BIT STRING { 0x00, RSAPublicKey } }
                    int bitTag, bitStart, bitLength;
                    if (!Tlv(der, innerStart + innerLength, end, out bitTag, out bitStart, out bitLength) || bitTag != 0x03 || bitLength < 2) return false;
                    var bitEnd = bitStart + bitLength;
                    if (!Tlv(der, bitStart + 1, bitEnd, out tag, out start, out length) || tag != 0x30) return false;
                    end = start + length;
                    if (!Tlv(der, start, end, out innerTag, out innerStart, out innerLength)) return false;
                }
                if (innerTag != 0x02) return false;
                int exponentTag, exponentStart, exponentLength;
                if (!Tlv(der, innerStart + innerLength, end, out exponentTag, out exponentStart, out exponentLength) || exponentTag != 0x02) return false;
                key = new RSAParameters
                {
                    Modulus = Unsigned(der, innerStart, innerLength),
                    Exponent = Unsigned(der, exponentStart, exponentLength)
                };
                return key.Modulus.Length >= 128 && key.Exponent.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool Tlv(byte[] data, int position, int end, out int tag, out int start, out int length)
        {
            tag = 0; start = 0; length = 0;
            if (position < 0 || position + 2 > end || end > data.Length) return false;
            tag = data[position];
            int size = data[position + 1];
            var at = position + 2;
            if (size >= 0x80)
            {
                var count = size & 0x7f;
                if (count < 1 || count > 3 || at + count > end) return false;
                size = 0;
                for (var i = 0; i < count; i++) size = (size << 8) | data[at++];
            }
            if (size < 0 || at + size > end) return false;
            start = at;
            length = size;
            return true;
        }

        private static byte[] Unsigned(byte[] data, int start, int length)
        {
            while (length > 1 && data[start] == 0) { start++; length--; }
            var result = new byte[length];
            Array.Copy(data, start, result, 0, length);
            return result;
        }

        /// <summary>
        /// Verifies a license string against the embedded OnAirDeck key for the given product and
        /// signed-in account at the given time (unix seconds). Pass null/empty signedInUserId when
        /// nobody is signed in; the result is then NotSignedIn once the license itself checks out.
        /// </summary>
        public static LicenseCheckResult Verify(string license, string expectedProductId, string signedInUserId, long nowUnix)
        {
            return Verify(license, expectedProductId, signedInUserId, nowUnix, PublicKey);
        }

        internal static LicenseCheckResult Verify(string license, string expectedProductId, string signedInUserId, long nowUnix, RSAParameters publicKey)
        {
            var result = new LicenseCheckResult();
            var text = (license ?? "").Trim();
            var dot = text.IndexOf('.');
            if (dot <= 0 || dot == text.Length - 1 || text.IndexOf('.', dot + 1) >= 0)
                return result.With(LicenseStatus.LicenseMalformed, "The license file is not in the expected format.");

            byte[] payload, signature;
            try
            {
                payload = FromBase64Url(text.Substring(0, dot));
                signature = FromBase64Url(text.Substring(dot + 1));
            }
            catch (FormatException)
            {
                return result.With(LicenseStatus.LicenseMalformed, "The license file is not valid base64url.");
            }

            if (!VerifySignature(payload, signature, publicKey))
                return result.With(LicenseStatus.InvalidSignature, "The license signature is not OnAirDeck's. Download the asset again with Atelier Manager.");

            // Only trust the payload after the signature proves it left the server unchanged.
            var fields = ParsePayload(payload);
            long version, iat, exp;
            if (fields == null || !MiniJson.TryGetInt(fields, "v", out version) || !MiniJson.TryGetInt(fields, "iat", out iat) || !MiniJson.TryGetInt(fields, "exp", out exp))
                return result.With(LicenseStatus.LicenseMalformed, "The license payload is incomplete.");

            result.UserId = MiniJson.GetString(fields, "user_id");
            result.ProductId = MiniJson.GetString(fields, "product_id");
            result.ProductName = MiniJson.GetString(fields, "product_name");
            result.IssuedAtUnix = iat;
            result.ExpiresAtUnix = exp;

            if (version != PayloadVersion || MiniJson.GetString(fields, "iss") != Issuer || MiniJson.GetString(fields, "client") != Client)
                return result.With(LicenseStatus.WrongIssuer, "The license was not issued by OnAirDeck for Unity Manager.");
            if (string.IsNullOrEmpty(expectedProductId))
                return result.With(LicenseStatus.WrongProduct, "No product id was given to the license check.");
            if (!SameId(result.ProductId, expectedProductId))
                return result.With(LicenseStatus.WrongProduct, "The license belongs to another product" + (string.IsNullOrEmpty(result.ProductName) ? "." : " (" + result.ProductName + ")."));
            if (string.IsNullOrEmpty(signedInUserId))
                return result.With(LicenseStatus.NotSignedIn, OnAirDeckLicense.SignInHint);
            if (!SameId(result.UserId, signedInUserId))
                return result.With(LicenseStatus.UserMismatch, "The license was issued to a different OnAirDeck account than the one signed in. Sign in with the buying account, or download the asset again.");
            if (iat > nowUnix + ClockSkewSeconds)
                return result.With(LicenseStatus.NotYetValid, "The license was issued in the future; check this computer's clock.");
            if (nowUnix >= exp)
                return result.With(LicenseStatus.Expired, "The license has expired. Open Atelier Manager to renew it.");

            return result.With(LicenseStatus.Valid, "Licensed to the signed-in OnAirDeck account.");
        }

        /// <summary>Payload fields without checking the signature. For locating files only, never for trust.</summary>
        internal static Dictionary<string, object> PeekPayload(string license)
        {
            var text = (license ?? "").Trim();
            var dot = text.IndexOf('.');
            if (dot <= 0) return null;
            try { return ParsePayload(FromBase64Url(text.Substring(0, dot))); }
            catch (FormatException) { return null; }
        }

        private static Dictionary<string, object> ParsePayload(byte[] payload)
        {
            try { return MiniJson.ParseObject(Encoding.UTF8.GetString(payload)); }
            catch (ArgumentException) { return null; }
        }

        private static bool VerifySignature(byte[] data, byte[] signature, RSAParameters publicKey)
        {
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(publicKey);
                    return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch (CryptographicException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool SameId(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b)
                && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
        }

        internal static byte[] FromBase64Url(string value)
        {
            var s = value.Trim().Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
                case 1: throw new FormatException("Invalid base64url length.");
            }
            return Convert.FromBase64String(s);
        }
    }
}
