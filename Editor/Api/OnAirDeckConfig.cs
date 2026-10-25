using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Public endpoints of the OnAirDeck backend. Nothing here is secret: the publishable key is
    /// the same one the website ships, and every plugin-* function authenticates with the user's
    /// own opaque session token, not with this key.
    /// </summary>
    internal static class OnAirDeckConfig
    {
        public const string WebsiteUrl = "https://onairdeck.com";
        public const string FunctionsUrl = "https://sptkmcrpgdvoegbfzqqq.supabase.co/functions/v1";
        public const string PublishableKey = "sb_publishable_b7oL6JAwAWOSQS-B4a8JXA_NYO0bzw6";

        /// <summary>
        /// Sent as ?client= on the sign-in URL for diagnostics only. The consent page deliberately
        /// ignores it: a desktop tool can't prove its identity, so it shows generic plugin text.
        /// </summary>
        public const string ClientId = "unity";

        public const string LogPrefix = "[OnAirDeck] ";

        /// <summary>
        /// VPM listings package dependencies may be downloaded from, searched in this order. A fixed list:
        /// never a URL read from a zip or from the server. Only the preview listing carries prerelease
        /// versions (for example 0.1.14-preview.2), so a preview add-on can find its exact core.
        /// </summary>
        public static readonly string[] TrustedVpmListings =
        {
            "https://studioraming.github.io/mingtoon-distribution/index.json",
            "https://studioraming.github.io/mingtoon-distribution/preview/index.json"
        };

        /// <summary>
        /// Packages that older versions shipped as a plain folder under Assets/ (no package.json, so a scan
        /// cannot find them). Installing the package over such a project would duplicate its files.
        /// </summary>
        public static readonly Dictionary<string, string> LegacyAssetFolders = new Dictionary<string, string>
        {
            { "com.studioraming.mingtoon", "Assets/StudioRaming/MingToon" }
        };
    }
}
