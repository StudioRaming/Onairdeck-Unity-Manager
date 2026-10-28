using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The old-install hand-off (the product's own installer cleans up, the Manager installs afterwards) and the license renewal
    /// refusals, as the window shows them. The hand-off state lives in a small file, so the window polls it about once a second and
    /// repaints when it changes. While a hand-off is Pending nothing here installs, updates or refreshes.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private const double HandoffPollSeconds = 1.0;

        [NonSerialized] private readonly Dictionary<string, HandoffStatus> _handoff = new Dictionary<string, HandoffStatus>();
        [NonSerialized] private bool _handoffWaiting;
        [NonSerialized] private double _handoffNext;
        [NonSerialized] private bool _handoffHooked;

        /// <summary>True while a product installer owns the project: install, update and refresh stay disabled.</summary>
        private bool HandoffWaiting { get { return _handoffWaiting; } }

        private void HookHandoff()
        {
            if (_handoffHooked) return;
            _handoffHooked = true;
            EditorApplication.update += PollHandoff;
            LicenseRenewalStatus.Changed += OnRenewalChanged;
            LicenseRenewalStatus.Issued += OnLicenseIssued;
        }

        private void UnhookHandoff()
        {
            if (!_handoffHooked) return;
            _handoffHooked = false;
            EditorApplication.update -= PollHandoff;
            LicenseRenewalStatus.Changed -= OnRenewalChanged;
            LicenseRenewalStatus.Issued -= OnLicenseIssued;
        }

        private void OnRenewalChanged()
        {
            Repaint();
        }

        private static string HandoffKey(HandoffStatus s)
        {
            return s == null ? "" : (int)s.Kind + "|" + s.Phase + "|" + s.Reason;
        }

        private void PollHandoff()
        {
            if (EditorApplication.timeSinceStartup < _handoffNext) return;
            _handoffNext = EditorApplication.timeSinceStartup + HandoffPollSeconds;
            try
            {
                var root = PackageInstallService.ProjectRoot;
                var changed = false;
                var waiting = false;
                var seen = new HashSet<string>();
                foreach (var package in LegacyHandoff.Packages(root))
                {
                    seen.Add(package);
                    var status = LegacyHandoff.Status(package);
                    if (status.Kind == HandoffKind.Pending) waiting = true;
                    HandoffStatus before;
                    if (!_handoff.TryGetValue(package, out before) || HandoffKey(before) != HandoffKey(status))
                    {
                        _handoff[package] = status;
                        changed = true;
                    }
                    if (status.Kind == HandoffKind.Done) FinishHandoff(package);
                }
                var gone = new List<string>();
                foreach (var key in _handoff.Keys) if (!seen.Contains(key)) gone.Add(key);
                foreach (var key in gone) { _handoff.Remove(key); changed = true; }
                if (waiting != _handoffWaiting) { _handoffWaiting = waiting; changed = true; }
                if (changed) Repaint();
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Hand-off status check failed: " + e.Message);
            }
        }

        /// <summary>Done: the installer's package is in. Show the installed state, then drop the record.</summary>
        private void FinishHandoff(string package)
        {
            LegacyHandoff.Clear(package);
            _handoff.Remove(package);
            _handoffWaiting = LegacyHandoff.IsWaiting();
            SetMessage(ManagerText.HoDone.Replace("{n}", ProductNameOf(package)), MessageType.Info);
            RefreshInstalledState();
        }

        private string ProductNameOf(string package)
        {
            if (_entries != null)
                foreach (var entry in _entries) if (entry.PackageName == package) return entry.Name;
            return package;
        }

        private HandoffStatus HandoffOf(string package)
        {
            HandoffStatus status;
            return !string.IsNullOrEmpty(package) && _handoff.TryGetValue(package, out status) && status.Kind != HandoffKind.None && status.Kind != HandoffKind.Done ? status : null;
        }

        private void OpenHandoffInstaller(string package)
        {
            if (!LegacyHandoffRunner.OpenInstaller(package)) SetMessage(ManagerText.HoNoMenu, MessageType.Warning);
        }

        private void DismissHandoff(string package)
        {
            LegacyHandoff.Clear(package);
            _handoff.Remove(package);
            _handoffWaiting = LegacyHandoff.IsWaiting();
            Repaint();
        }

        // ------------------------------------------------------------ cards

        /// <summary>The hand-off card of one package: progress while Pending (not an error), the reason and two buttons when Stopped.</summary>
        private void DrawHandoffCard(CatalogEntry entry)
        {
            var status = HandoffOf(entry.PackageName);
            if (status == null) return;
            LegacyInstallerSpec spec;
            OnAirDeckConfig.LegacyInstallers.TryGetValue(entry.PackageName, out spec);
            var text = LegacyInstallText.HandoffStatusText(LegacyInstallDialog.CurrentLanguage(), entry.Name, status, spec != null ? spec.MenuPath : "");
            if (status.Kind == HandoffKind.Pending)
            {
                SRGui.HelpBox(text, SRTone.Info);
                GUILayout.Space(SRMetrics.Space6);
                return;
            }
            var package = entry.PackageName;
            SRGui.HelpBox(text, SRTone.Warning);
            GUILayout.BeginHorizontal();
            if (SRGui.Button(new GUIContent(ManagerText.HoOpen.Replace("{n}", entry.Name)), SRButtonKind.Secondary, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => OpenHandoffInstaller(package));
            if (SRGui.Button(new GUIContent(ManagerText.Close), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => DismissHandoff(package));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(SRMetrics.Space6);
        }

        /// <summary>The server's refusal of the last license renewal; device_released and device_limit have their own words and button.</summary>
        private void DrawRenewalCard(CatalogEntry entry)
        {
            if (!entry.IsLicense) return;
            var refusal = LicenseRenewalStatus.For(entry.LicenseId);
            if (refusal == null) return;
            var lang = ManagerText.Language;
            if (refusal.Code == "device_released")
            {
                if (SRGui.HelpBox(DeviceReleasedText.Body(lang), SRTone.Warning, DeviceReleasedText.Button(lang)) && !_removing && !Busy) Defer(ReactivateThisPc);
            }
            else if (refusal.Code == "device_limit")
            {
                if (SRGui.HelpBox(ManagerText.DlTitle, SRTone.Warning, ManagerText.MAcctDev) && !Busy) Defer(OpenDeviceManager);
            }
            else if (SRGui.HelpBox(ManagerText.RenBody.Replace("{n}", entry.Name), SRTone.Warning, ManagerText.PRenB)) OpenAtelier(entry);
            GUILayout.Space(SRMetrics.Space6);
        }
    }
}
