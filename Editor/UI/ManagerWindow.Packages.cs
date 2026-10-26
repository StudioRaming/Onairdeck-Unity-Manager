using System;
using System.Collections.Generic;
using System.Threading;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The Packages section: seller-license packages (installed vs. newest), install/update, older versions, mismatch repair.</summary>
    internal sealed partial class ManagerWindow
    {
        private const string FocusKey = "OnAirDeck.UnityManager.FocusPackage";

        private sealed class PackageGroup
        {
            public string Name = "";
            public string Installed = "";
            public readonly List<LicenseVersionEntry> Versions = new List<LicenseVersionEntry>(); // newest first
        }

        [NonSerialized] private LicenseEntry[] _licenses;
        [NonSerialized] private List<PackageGroup> _packages = new List<PackageGroup>();
        [NonSerialized] private List<PackageMismatch> _mismatches = new List<PackageMismatch>();
        [NonSerialized] private Dictionary<string, int> _pickedVersion = new Dictionary<string, int>();
        [NonSerialized] private bool _installingPackage;
        [NonSerialized] private bool _applyingSelfUpdate;
        [NonSerialized] private string _packageProgressLabel = "";
        [NonSerialized] private float _packageProgress;
        [NonSerialized] private CancellationTokenSource _packageCancel;
        private string _focusPackage = "";

        /// <summary>Opens the window and highlights one package; used by <see cref="OnAirDeckPackages"/>.</summary>
        internal static void OpenAndFocus(string packageName)
        {
            SessionState.SetString(FocusKey, packageName ?? "");
            Open();
            var window = GetWindow<ManagerWindow>();
            window._focusPackage = packageName ?? "";
            window.Focus();
            window.Repaint();
        }

        private void OnEnablePackages()
        {
            _focusPackage = SessionState.GetString(FocusKey, "");
            // An install ends in a script reload that abandons the code awaiting it; its result waits here.
            var report = SessionState.GetString(PackageInstallService.ReportKey, "");
            if (report.Length > 0)
            {
                SessionState.EraseString(PackageInstallService.ReportKey);
                SetMessage(report, MessageType.Info);
            }
        }

        private void SetLicenses(LicenseEntry[] licenses)
        {
            _licenses = licenses ?? new LicenseEntry[0];
            _packages = new List<PackageGroup>();
            foreach (var license in _licenses)
            {
                if (license == null || license.versions == null) continue;
                foreach (var version in license.versions)
                {
                    if (version == null || !version.IsPackage) continue;
                    var group = _packages.Find(g => g.Name == version.package_name);
                    if (group == null)
                    {
                        group = new PackageGroup { Name = version.package_name };
                        _packages.Add(group);
                    }
                    group.Versions.Add(version);
                }
            }
            foreach (var group in _packages)
                group.Versions.Sort((a, b) => PackageVersion.Compare(b.package_version, a.package_version));
            _pickedVersion = new Dictionary<string, int>();
            RefreshInstalledPackages();
        }

        private void RefreshInstalledPackages()
        {
            if (_packages == null) return;
            var root = PackageInstallService.ProjectRoot;
            var names = new List<string>();
            foreach (var group in _packages)
            {
                group.Installed = InstalledPackages.InstalledVersion(root, group.Name);
                names.Add(group.Name);
            }
            _mismatches = InstalledPackages.FindMismatches(root, names);
            Repaint();
        }

        private bool PackageBusy { get { return _installingPackage || _applyingSelfUpdate || _downloadingItemId != null; } }

        // ---------- drawing ----------

        private void DrawPackages()
        {
            if (_licenses == null) return;
            if (_packages.Count == 0)
            {
                if (_focusPackage.Length > 0)
                    SRGui.HelpBox("OnAirDeck lists no installable version of " + _focusPackage + " for this account.", SRTone.Info);
                return;
            }

            GUILayout.Label("Packages", SRStyles.Section);
            GUILayout.Space(SRMetrics.Space4);
            if (_installingPackage)
            {
                SRGui.ProgressBar(_packageProgress, _packageProgressLabel);
                if (ActionButton("Cancel", true)) CancelPackageInstall();
            }

            foreach (var mismatch in _mismatches) DrawMismatch(mismatch);
            foreach (var group in _packages) DrawPackageRow(group);
            EditorGUILayout.Space();
        }

        private void DrawMismatch(PackageMismatch mismatch)
        {
            var group = _packages.Find(g => g.Name == mismatch.Package);
            if (group == null) return;

            var have = mismatch.Installed.Length == 0
                ? mismatch.Dependency + " is not installed"
                : mismatch.Dependency + " " + mismatch.Installed + " is installed";
            var versions = new List<string>();
            foreach (var v in group.Versions) versions.Add(v.package_version);
            bool matches;
            var target = InstalledPackages.ChooseTarget(versions, mismatch.Installed, out matches);

            SRGui.HelpBox(mismatch.Package + " " + mismatch.Version + " needs " + mismatch.Dependency + " " + mismatch.Required + ", but " + have + ".", SRTone.Warning);
            if (target == null) return;
            var label = matches
                ? "Install " + mismatch.Package + " " + target + " to match " + mismatch.Dependency + " " + mismatch.Installed
                : "Install " + mismatch.Package + " " + target + " with the exact " + mismatch.Dependency + " version it requires";
            GUI.enabled = !PackageBusy;
            if (ActionButton(label, true, true)) InstallPackage(group.Versions.Find(v => v.package_version == target));
            GUI.enabled = true;
            EditorGUILayout.Space();
        }

        private void DrawPackageRow(PackageGroup group)
        {
            var newest = group.Versions[0];
            var focused = group.Name == _focusPackage;
            var upToDate = group.Installed.Length > 0 && PackageVersion.Compare(newest.package_version, group.Installed) <= 0;
            SRGui.BeginCard();
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(group.Name, _purchaseTitleStyle, GUILayout.ExpandWidth(true));
                    if (focused) SRGui.BadgeLayout("Requested", SRTone.Brand);
                    if (group.Installed.Length == 0) SRGui.BadgeLayout("Not installed", SRTone.Neutral);
                    else if (upToDate) SRGui.BadgeLayout("Up to date", SRTone.Success);
                    else SRGui.BadgeLayout("Update available", SRTone.Warning);
                }
                var installed = group.Installed.Length > 0 ? group.Installed : "not installed";
                GUILayout.Label("Installed: " + installed + "  ·  Newest: " + newest.package_version, _detailsStyle, GUILayout.ExpandWidth(true));
                if (focused) GUILayout.Label("Requested by another package.", _detailsStyle, GUILayout.ExpandWidth(true));

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUI.enabled = !PackageBusy && !upToDate;
                    var label = group.Installed.Length == 0 ? "Install " + newest.package_version : upToDate ? "Up to date" : "Update to " + newest.package_version;
                    if (ActionButton(label, false, !upToDate)) InstallPackage(newest);
                    GUI.enabled = true;
                }

                if (group.Versions.Count > 1)
                {
                    int index;
                    if (!_pickedVersion.TryGetValue(group.Name, out index)) index = 0;
                    index = Mathf.Clamp(index, 0, group.Versions.Count - 1);
                    var labels = new string[group.Versions.Count];
                    for (var i = 0; i < labels.Length; i++)
                    {
                        var v = group.Versions[i].package_version;
                        labels[i] = v == group.Installed ? v + " (installed)" : v;
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("Other version", _detailsStyle, GUILayout.ExpandWidth(false));
                        index = SRGui.Popup(index, labels, GUILayout.ExpandWidth(true));
                        _pickedVersion[group.Name] = index;
                        var picked = group.Versions[index];
                        GUI.enabled = !PackageBusy && picked.package_version != group.Installed;
                        if (ActionButton("Install " + picked.package_version)) InstallPackage(picked);
                        GUI.enabled = true;
                    }
                    var notes = group.Versions[index].release_notes;
                    if (!string.IsNullOrEmpty(notes))
                        GUILayout.Label(notes.Length > 300 ? notes.Substring(0, 300) + "…" : notes, _detailsStyle, GUILayout.ExpandWidth(true));
                }
            }
            SRGui.EndCard();
            GUILayout.Space(SRMetrics.Space4);
        }

        // ---------- actions ----------

        private async void InstallPackage(LicenseVersionEntry version)
        {
            if (version == null || _session == null || PackageBusy) return;
            if (!EditorUtility.DisplayDialog(
                    "Install " + version.package_name + " " + version.package_version,
                    "The current version, if any, is moved to Library/OnAirDeck/Backup. Packages it depends on are installed at the exact versions it names.\n\n"
                    + "Downloading makes this purchase non-refundable, the same as downloading on onairdeck.com.",
                    "Install", "Cancel"))
            {
                return;
            }

            CancelPackageInstall();
            _packageCancel = new CancellationTokenSource();
            var cancel = _packageCancel.Token;
            _installingPackage = true;
            _packageProgress = 0f;
            _packageProgressLabel = "Preparing…";
            SetMessage(null, MessageType.None);

            try
            {
                var report = await PackageInstallService.InstallAsync(_session.token, version,
                    (label, p) => { _packageProgressLabel = label; _packageProgress = p; Repaint(); }, cancel);
                SessionState.EraseString(PackageInstallService.ReportKey);
                _focusPackage = "";
                SessionState.EraseString(FocusKey);
                SetMessage(report.Describe(), MessageType.Info);
            }
            catch (OperationCanceledException)
            {
                SetMessage("Install cancelled. Nothing was changed.", MessageType.None);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Package install failed: " + e.Message);
            }
            finally
            {
                _installingPackage = false;
                if (_packageCancel != null) { _packageCancel.Dispose(); _packageCancel = null; }
                RefreshInstalledPackages();
            }
        }

        private void CancelPackageInstall()
        {
            if (_packageCancel == null) return;
            _packageCancel.Cancel();
        }

        private async void ApplyEmbeddedUpdate()
        {
            if (_update == null || !_update.UseEmbedded || PackageBusy) return;
            _applyingSelfUpdate = true;
            SetMessage("Updating the OnAirDeck Unity Manager… Unity will reload scripts when it finishes.", MessageType.Info);
            try
            {
                await PackageInstallService.SelfUpdateAsync(_update.DownloadUrl, _update.PackageName, _update.Response.latest.version,
                    _update.PackageFolder, null, CancellationToken.None);
                SessionState.EraseString(PackageInstallService.ReportKey);
                SetMessage("Updated to " + _update.Response.latest.version + ".", MessageType.Info);
            }
            catch (Exception e)
            {
                SetMessage("Update failed: " + e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Self-update failed: " + e.Message);
            }
            finally
            {
                _applyingSelfUpdate = false;
                Repaint();
            }
        }
    }
}
