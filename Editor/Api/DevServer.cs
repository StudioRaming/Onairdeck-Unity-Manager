using System;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Developer-only server override for testing the real flows against a local Supabase stack and a local web server.
    /// Everything here is behind the compile-time define ATELIER_DEV_SERVER (Player Settings, Scripting Define Symbols); a normal
    /// install does not have it, so <see cref="Pick"/> compiles to the fallback and the EditorPrefs below are never read.
    /// <para>EditorPrefs strings, all optional (empty means "use the real constant"):
    /// <c>Atelier.Dev.FunctionsUrl</c> (replaces OnAirDeckConfig.FunctionsUrl, no trailing slash),
    /// <c>Atelier.Dev.SiteUrl</c> (replaces OnAirDeckConfig.WebsiteUrl for the browser sign-in page),
    /// <c>Atelier.Dev.LicensePublicKeyPem</c> (a PEM public key that replaces the embedded license key in LicenseVerifier),
    /// <c>Atelier.Dev.DeviceSalt</c> (mixed into DeviceIdentity.Hash: a different salt is a different PC to the server).
    /// With the define, a plain-http loopback download URL (127.0.0.1, localhost, ::1) is accepted too.</para>
    /// </summary>
    internal static class DevServer
    {
        public const string FunctionsUrlPref = "Atelier.Dev.FunctionsUrl";
        public const string SiteUrlPref = "Atelier.Dev.SiteUrl";
        public const string LicensePublicKeyPemPref = "Atelier.Dev.LicensePublicKeyPem";
        public const string DeviceSaltPref = "Atelier.Dev.DeviceSalt";

        /// <summary>Reads one preference. The Unity side (DevServerHook, define only) points it at EditorPrefs; the default knows nothing.</summary>
        public static Func<string, string> ReadPref = key => "";

        /// <summary>True only in a build compiled with ATELIER_DEV_SERVER.</summary>
        public static bool Enabled
        {
            get
            {
#if ATELIER_DEV_SERVER
                return true;
#else
                return false;
#endif
            }
        }

        public static string FunctionsUrl { get { return Pick(FunctionsUrlPref, OnAirDeckConfig.FunctionsUrl, true); } }
        public static string SiteUrl { get { return Pick(SiteUrlPref, OnAirDeckConfig.WebsiteUrl, true); } }
        public static string LicensePublicKeyPem { get { return Pick(LicensePublicKeyPemPref, "", false); } }
        public static string DeviceSalt { get { return Pick(DeviceSaltPref, "", false); } }

        private static string Pick(string pref, string fallback, bool isUrl)
        {
#if ATELIER_DEV_SERVER
            string value = null;
            try { value = ReadPref(pref); } catch (Exception) { }
            if (!string.IsNullOrWhiteSpace(value)) return isUrl ? value.Trim().TrimEnd('/') : value.Trim();
#endif
            return fallback;
        }

        public static bool IsLoopback(Uri uri)
        {
            return uri != null && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
        }
    }
}
