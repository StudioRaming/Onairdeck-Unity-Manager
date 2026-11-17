using UMod.BuildEngine;
using UMod.BuildEngine.Build;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>
    /// UMod calls this after every successful mod build ("Warudo/Build Mod"), with the written .warudo. The work is in
    /// WarudoProtectRunner (main editor assembly) so it compiles and is testable without the WARUDO SDK; this assembly only
    /// exists where UMod does (define SR_WARUDO_MODTOOL, see WarudoProtectHookState).
    /// </summary>
    [UModBuildEnginePostProcessor]
    internal sealed class WarudoProtectBuildHook : BuildEnginePostProcessor
    {
        public WarudoProtectBuildHook()
        {
        }

        public override void OnBuildEnginePostProcess(ModBuildResult buildResult)
        {
            if (buildResult == null || !buildResult.Successful || buildResult.BuiltModFile == null) return;
            WarudoProtectRunner.OnModBuilt(buildResult.BuiltModFile.FullName);
        }
    }
}
