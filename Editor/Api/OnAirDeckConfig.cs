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
    }
}
