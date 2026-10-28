using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The signed license file written next to a downloaded product:
    /// Assets/StudioRaming_Onairdeck/&lt;Product&gt;/onairdeck.license — one line,
    /// "&lt;base64url(payload JSON)&gt;.&lt;base64url(RSA signature)&gt;", issued by plugin-license.
    /// This class only stores and inspects the file. Signature verification (RSA public key)
    /// is done by the runtime check that ships with sellers' assets, and by the server.
    /// Kept free of Unity JSON APIs so the headless verification build can exercise it.
    /// </summary>
    internal static class LicenseFile
    {
        public const string FileName = "onairdeck.license";

        /// <summary>Re-issue when the license expires within this window (it lives 7 days).</summary>
        public const long RefreshWindowSeconds = 2L * 24 * 60 * 60;

        /// <summary>Absolute path of the license for a project-relative product folder.</summary>
        public static string PathFor(string productFolder)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var absoluteFolder = Path.GetFullPath(Path.Combine(projectRoot, productFolder.Replace('/', Path.DirectorySeparatorChar)));
            return Path.Combine(absoluteFolder, FileName);
        }

        /// <summary>Writes the license string (creating the product folder). Returns the absolute path.</summary>
        public static string Write(string productFolder, string license)
        {
            var trimmed = (license ?? "").Trim();
            if (trimmed.IndexOf('.') <= 0) throw new ArgumentException("Not a license string.");
            var path = PathFor(productFolder);
            var folder = Path.GetDirectoryName(path);
            if (!PathSafety.IsInside(folder, path)) throw new Exception("Refusing to write outside " + productFolder);
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, trimmed + "\n", new UTF8Encoding(false));
            return path;
        }

        /// <summary>
        /// True when there is no license yet, it is unreadable, it expires within RefreshWindowSeconds, or it was issued to
        /// another account (<paramref name="userId"/>) or, when the file carries a "device" field, another PC
        /// (<paramref name="deviceHash"/>). Empty userId or deviceHash skips that comparison.
        /// </summary>
        public static bool NeedsRefresh(string productFolder, long nowUnix, string userId = null, string deviceHash = null)
        {
            var path = PathFor(productFolder);
            if (!File.Exists(path)) return true;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            return IsDue(text, nowUnix, userId, deviceHash);
        }

        /// <summary>The decision of <see cref="NeedsRefresh"/> for a license string already read. Does not verify the signature.</summary>
        public static bool IsDue(string licenseText, long nowUnix, string userId, string deviceHash)
        {
            long exp;
            if (!TryReadExpiry(licenseText, out exp)) return true;
            if (exp - nowUnix < RefreshWindowSeconds) return true;
            var json = PayloadJson(licenseText);
            if (json == null) return true;
            string value;
            if (!string.IsNullOrEmpty(userId) && (!TryReadString(json, "user_id", out value) || !string.Equals(value, userId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrEmpty(deviceHash) && TryReadString(json, "device", out value) && !string.Equals(value, deviceHash, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>A top-level string field of the license payload (not verified); false when absent or malformed.</summary>
        internal static bool TryReadPayloadString(string licenseText, string field, out string value)
        {
            value = null;
            var json = PayloadJson(licenseText);
            return json != null && TryReadString(json, field, out value);
        }

        private static string PayloadJson(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            var dot = license.IndexOf('.');
            if (dot <= 0) return null;
            try { return Encoding.UTF8.GetString(FromBase64Url(license.Substring(0, dot).Trim())); }
            catch (FormatException) { return null; }
        }

        /// <summary>Minimal extractor for a top-level plain string field (ids and hashes: no escapes expected).</summary>
        internal static bool TryReadString(string json, string field, out string value)
        {
            value = null;
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return false;
            var i = json.IndexOf(':', at + key.Length);
            if (i < 0) return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return false;
            var end = json.IndexOf('"', i + 1);
            if (end < 0) return false;
            value = json.Substring(i + 1, end - i - 1);
            return true;
        }

        /// <summary>Reads "exp" (unix seconds) out of the payload half. Does not verify the signature.</summary>
        public static bool TryReadExpiry(string license, out long exp)
        {
            exp = 0;
            if (string.IsNullOrEmpty(license)) return false;
            var dot = license.IndexOf('.');
            if (dot <= 0) return false;
            string json;
            try { json = Encoding.UTF8.GetString(FromBase64Url(license.Substring(0, dot).Trim())); }
            catch (FormatException) { return false; }
            return TryReadNumber(json, "exp", out exp);
        }

        internal static byte[] FromBase64Url(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>Minimal extractor for a top-level integer field (no JSON dependency).</summary>
        internal static bool TryReadNumber(string json, string field, out long value)
        {
            value = 0;
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return false;
            var i = json.IndexOf(':', at + key.Length);
            if (i < 0) return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            var start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || (i == start && json[i] == '-'))) i++;
            return i > start && long.TryParse(json.Substring(start, i - start), out value);
        }
    }
}
