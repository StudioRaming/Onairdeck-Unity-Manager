using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Entry points other packages use (OpenAndFocus) and the package bookkeeping of the window (mismatches).</summary>
    internal sealed partial class ManagerWindow
    {
        private const string FocusKey = "OnAirDeck.UnityManager.FocusPackage";

        [NonSerialized] private bool _applyingSelfUpdate;
        private string _focusPackage = "";

        /// <summary>Opens the window and highlights one package; used by <see cref="OnAirDeckPackages"/>.</summary>
        internal static void OpenAndFocus(string packageName)
        {
            SessionState.SetString(FocusKey, packageName ?? "");
            Open();
            var window = GetWindow<ManagerWindow>();
            window._focusPackage = packageName ?? "";
            window._pendingFocus = true;
            window._entriesDirty = true;
            window.Focus();
            window.Repaint();
        }

        [NonSerialized] private bool _pendingFocus;

        private void OnEnablePackages()
        {
            _focusPackage = SessionState.GetString(FocusKey, "");
            _pendingFocus = _focusPackage.Length > 0;
            // An install ends in a script reload that abandons the code awaiting it; its result waits here.
            var report = SessionState.GetString(PackageInstallService.ReportKey, "");
            if (report.Length > 0)
            {
                SessionState.EraseString(PackageInstallService.ReportKey);
                SetMessage(report, MessageType.Info);
            }
        }

        /// <summary>Version mismatches of the installed packages that a license of this account offers.</summary>
        private List<PackageMismatch> FindMismatches(string root)
        {
            var names = new List<string>();
            if (_licenses != null)
            {
                foreach (var license in _licenses)
                {
                    if (license == null || license.versions == null) continue;
                    foreach (var version in license.versions)
                        if (version != null && version.IsPackage && !names.Contains(version.package_name)) names.Add(version.package_name);
                }
            }
            try { return InstalledPackages.FindMismatches(root, names); }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Mismatch check failed: " + e.Message);
                return new List<PackageMismatch>();
            }
        }
    }
}
