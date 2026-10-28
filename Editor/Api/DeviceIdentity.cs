using UnityEngine;
using UnityEngine.Networking;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Who this PC is, for the server's per-license device limit. Computed once per domain load.
    /// The raw hardware id never leaves the PC; only its salted SHA-256 is sent.
    /// </summary>
    internal static class DeviceIdentity
    {
        public const string HashHeader = "X-OnAirDeck-Device";
        public const string NameHeader = "X-OnAirDeck-Device-Name";
        public const string PlatformHeader = "X-OnAirDeck-Device-Platform";

        /// <summary>64 hex characters, or "" when Unity cannot supply a hardware id.</summary>
        private static readonly string RawId = SystemInfo.deviceUniqueIdentifier;
        private static readonly string PlainHash = DeviceIdentityCore.Hash(RawId);
        public static string Hash
        {
            get
            {
                var salt = DevServer.DeviceSalt;   // "" unless ATELIER_DEV_SERVER is defined and the pref is set
                return salt.Length == 0 ? PlainHash : DeviceIdentityCore.Hash(RawId, salt);
            }
        }
        /// <summary>URI-escaped computer name (what is sent). Use <see cref="DisplayName"/> to show it.</summary>
        public static readonly string Name = DeviceIdentityCore.Name(SystemInfo.deviceName);
        public static readonly string Platform = DeviceIdentityCore.Platform(SystemInfo.operatingSystemFamily.ToString());
        /// <summary>The computer name as typed (for the window).</summary>
        public static readonly string DisplayName = SystemInfo.deviceName ?? "";

        public static void Apply(UnityWebRequest request)
        {
            if (Hash.Length > 0) request.SetRequestHeader(HashHeader, Hash);
            if (Name.Length > 0) request.SetRequestHeader(NameHeader, Name);
            if (Platform.Length > 0) request.SetRequestHeader(PlatformHeader, Platform);
        }
    }
}
