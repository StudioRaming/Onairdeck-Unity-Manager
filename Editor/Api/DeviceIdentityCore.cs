using System;
using System.Security.Cryptography;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The pure part of the device identity (no UnityEngine, so the Verification suite compiles it):
    /// hashing, name truncation and escaping, platform mapping. <see cref="DeviceIdentity"/> feeds it the real values.
    /// </summary>
    internal static class DeviceIdentityCore
    {
        public const string HashPrefix = "onairdeck-device-v1|";
        public const int MaxNameLength = 64;
        /// <summary>Sent on every request: tells the server this is the Atelier Manager line (0.1.0 and newer), not the old OnAirDeck Unity Manager.</summary>
        public const string ManagerHeaderName = "X-Atelier-Manager";
        public const string ManagerHeaderValue = "1";
        /// <summary>Sent only on requests a user click started: lets this PC take a seat it had released (contract v7).</summary>
        public const string ReactivateHeaderName = "X-Atelier-Reactivate";
        public const string ReactivateHeaderValue = "1";
        public const string UnsupportedId = "n/a"; // SystemInfo.unsupportedIdentifier

        /// <summary>Lowercase hex SHA-256 of UTF-8 "onairdeck-device-v1|" + id (64 chars); "" when the id is unusable.</summary>
        public static string Hash(string rawId)
        {
            if (string.IsNullOrEmpty(rawId) || rawId == UnsupportedId) return "";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(HashPrefix + rawId));
                var sb = new StringBuilder(64);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The hash with a dev salt mixed in (ATELIER_DEV_SERVER only, to simulate another PC); an empty salt gives <see cref="Hash(string)"/>.</summary>
        public static string Hash(string rawId, string salt)
        {
            var plain = Hash(rawId);
            if (plain.Length == 0 || string.IsNullOrEmpty(salt)) return plain;
            return Hash(rawId + "|dev|" + salt);
        }

        /// <summary>Header value: the name cut to 64 characters first (never inside a surrogate pair), then URI-escaped.</summary>
        public static string Name(string rawName)
        {
            if (string.IsNullOrEmpty(rawName)) return "";
            var name = rawName.Length > MaxNameLength ? rawName.Substring(0, MaxNameLength) : rawName;
            // Drop a cut-off high surrogate and any lone surrogate, which EscapeDataString rejects.
            var sb = new StringBuilder(name.Length);
            for (var i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsHighSurrogate(c) && i + 1 < name.Length && char.IsLowSurrogate(name[i + 1])) { sb.Append(c).Append(name[i + 1]); i++; }
                else if (char.IsSurrogate(c)) continue;
                else sb.Append(c);
            }
            return Uri.EscapeDataString(sb.ToString());
        }

        /// <summary>Maps the name of UnityEngine.OperatingSystemFamily to the contract value; "" for anything else.</summary>
        public static string Platform(string operatingSystemFamily)
        {
            switch (operatingSystemFamily)
            {
                case "Windows": return "Windows";
                case "MacOSX": return "macOS";
                case "Linux": return "Linux";
                default: return "";
            }
        }
    }
}
