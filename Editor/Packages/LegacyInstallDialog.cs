using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The EditorUtility.DisplayDialog side of contract v9. A window or batch entry passes
    /// <see cref="Confirm"/> as the confirm callback of PackageInstallService.InstallAsync. The language comes from the
    /// shared StudioRaming.Language preference (0 ko, 1 en, 2 ja), the same one the window uses.
    /// </summary>
    internal static class LegacyInstallDialog
    {
        private const string LanguageKey = "StudioRaming.Language";

        [InitializeOnLoadMethod]
        private static void Register()
        {
            LegacyInstallText.LanguageProvider = CurrentLanguage;
            // A move whose install never finished (crash, reload): say so once; the window offers the undo.
            var pending = LegacyMove.PendingRestores();
            if (pending.Count > 0)
                Debug.LogWarning("[Atelier Manager] " + pending.Count + " old install(s) were moved to Library/OnAirDeck/Backup and the install did not finish. Open the Manager to put them back.");
        }

        public static int CurrentLanguage()
        {
            var fallback = Application.systemLanguage == SystemLanguage.Korean ? 0 : Application.systemLanguage == SystemLanguage.Japanese ? 2 : 1;
            return Mathf.Clamp(EditorPrefs.GetInt(LanguageKey, fallback), 0, 2);
        }

        /// <summary>One install: the plan in the user's language. "Move and install" returns true.</summary>
        public static bool Confirm(LegacyPlan plan)
        {
            var language = CurrentLanguage();
            var handoff = plan.HandoffItem();
            if (handoff != null)
            {
                // The product's own installer cleans up (nothing is moved): its own title, text and buttons.
                var product = string.IsNullOrEmpty(handoff.Product) ? "" : handoff.Product;
                return EditorUtility.DisplayDialog(LegacyInstallText.HandoffTitle(language, product), LegacyInstallText.HandoffBody(language, product),
                    LegacyInstallText.HandoffYes(language), LegacyInstallText.HandoffNo(language));
            }
            return EditorUtility.DisplayDialog(LegacyInstallText.Title(language), LegacyInstallText.Body(language, plan),
                LegacyInstallText.Yes(language), LegacyInstallText.No(language));
        }

        /// <summary>A batch: one dialog listing everything that needs the move.</summary>
        public static bool ConfirmBatch(LegacyPlan plan)
        {
            var language = CurrentLanguage();
            return EditorUtility.DisplayDialog(LegacyInstallText.Title(language), LegacyInstallText.BatchBody(language, plan),
                LegacyInstallText.Yes(language), LegacyInstallText.No(language));
        }
    }
}
