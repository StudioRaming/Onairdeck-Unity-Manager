using System;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// On every editor load, rolls back an install transaction that never finished (the editor was closed
    /// or crashed between two folder moves) and leaves the report for the Manager window.
    /// </summary>
    [InitializeOnLoad]
    internal static class PackageInstallRecovery
    {
        static PackageInstallRecovery()
        {
            EditorApplication.delayCall += Run;
        }

        private static void Run()
        {
            try
            {
                var message = PackageTransaction.Recover(PackageInstallService.ProjectRoot);
                if (message == null) return;
                SessionState.SetString(PackageInstallService.ReportKey, message);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + message);
#if UNITY_2021_1_OR_NEWER
                Client.Resolve();
#endif
                AssetDatabase.Refresh();
            }
            catch (Exception e)
            {
                Debug.LogError(OnAirDeckConfig.LogPrefix + "Could not roll back an interrupted install: " + e.Message);
                InstallErrorReporter.Offer(InstallErrorReporter.FromFailure("com.studioraming.manager", "Atelier Manager", UpdateService.CurrentVersion, e));
            }
        }
    }
}
