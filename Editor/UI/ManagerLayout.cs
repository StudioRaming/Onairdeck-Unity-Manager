namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Sizes of the Atelier Manager window that the theme (SRMetrics) has no token for. Values come from mockup v3.2
    /// (list rows 39px with a 30px thumbnail, 208px hover preview, 320px list pane, one pane below 640px).
    /// Everything else uses SRMetrics, so a theme change still reaches this window.
    /// </summary>
    internal static class ManagerLayout
    {
        public const float ListPaneWidth = 320f;
        /// <summary>Below this window width only one pane shows (list, then detail).</summary>
        public const float NarrowBelow = 640f;
        public const float WindowMinWidth = 380f;
        public const float WindowMinHeight = 420f;

        public const float ListRowHeight = 39f;
        public const float ListThumb = 30f;
        public const float GroupRowHeight = 22f;
        public const float Checkbox = 14f;
        public const float Star = 14f;
        public const float RowChipHeight = 16f;

        public const float PreviewCard = 208f;
        public const float DetailThumb = 96f;

        public const float FooterHeight = 28f;
        public const float BatchBarHeight = 40f;
        public const float BatchBarRunningHeight = 56f;
        public const float ListControlsHeight = 58f;
        public const float DetailPad = 12f;
        public const float FormMaxWidth = 460f;

        /// <summary>Notes folded in the version list and the description fold show this many lines before "more".</summary>
        public const int DescriptionLines = 8;
        public const int NoteLines = 3;
    }
}
