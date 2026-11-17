using System;
using System.Linq;
using UnityEditor;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>
    /// Whether this project can run the protection hook. The hook assembly (UModHook/) compiles only with the
    /// SR_WARUDO_MODTOOL define: set automatically by the asmdef when the WARUDO mod tool is a package
    /// (app.warudo.modtool), or added here on the seller's request when UMod lives under Assets/ (WARUDO SDK 0.12.x).
    /// Nothing is changed unless the seller presses the button: installing the Manager alone never edits project settings.
    /// </summary>
    internal static class WarudoProtectHookState
    {
        public const string Define = "SR_WARUDO_MODTOOL";
        private const string HookTypeName = "OnAirDeck.UnityManager.WarudoProtect.WarudoProtectBuildHook, OnAirDeck.WarudoProtect.UModHook";

        public static bool UModPresent
        {
            get { return AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "UMod-BuildEngine"); }
        }

        public static bool HookLoaded
        {
            get { return Type.GetType(HookTypeName, false) != null; }
        }

        /// <summary>Adds the define for the Standalone target (WARUDO mods build for Windows); Unity then recompiles.</summary>
        public static void EnableHook()
        {
            const BuildTargetGroup group = BuildTargetGroup.Standalone;
#pragma warning disable 0618 // the BuildTargetGroup overloads are what 2021.3 down to 2019.4 all have
            string defines = PlayerSettings.GetScriptingDefineSymbolsForGroup(group) ?? "";
            var list = defines.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(d => d.Trim()).ToList();
            if (list.Contains(Define)) return;
            list.Add(Define);
            PlayerSettings.SetScriptingDefineSymbolsForGroup(group, string.Join(";", list.ToArray()));
#pragma warning restore 0618
        }
    }
}
