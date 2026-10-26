namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Entry point for other packages. A package that wants its users to install or update it through
    /// the Manager looks this type up by name, so it keeps working when the Manager is absent:
    /// <c>Type.GetType("OnAirDeck.UnityManager.OnAirDeckPackages, OnAirDeck.UnityManager.Editor")</c>.
    /// </summary>
    public static class OnAirDeckPackages
    {
        /// <summary>Opens the Manager window with <paramref name="packageName"/> highlighted in its Packages section.</summary>
        public static void OpenAndFocus(string packageName)
        {
            ManagerWindow.OpenAndFocus(packageName);
        }
    }
}
