using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Device manager (account menu), the device limit screen (409 device_limit), and the devices tab of a limited license.</summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private DevicesResponse _devices;
        [NonSerialized] private bool _devicesLoading;
        [NonSerialized] private string _devicesError;
        [NonSerialized] private bool _removing;

        // ------------------------------------------------------------ loading and removing

        private void OpenDeviceManager()
        {
            _view = (int)View.Devices;
            _devicesError = null;
            LoadDevices();
            Repaint();
        }

        private async void LoadDevices()
        {
            if (_session == null || _devicesLoading) return;
            _devicesLoading = true;
            var token = _session.token;
            try
            {
                _devices = await OnAirDeckApi.GetDevicesAsync(token);
                _devicesError = null;
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                _devicesError = e.Message;
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Loading devices failed: " + e.Message);
            }
            finally
            {
                _devicesLoading = false;
                Repaint();
            }
        }

        private async void RemoveDevice(DeviceEntry device)
        {
            if (_session == null || _removing || device == null || device.this_device) return;
            var message = ManagerText.DlgRem.Replace("{name}", device.name);
            if (!EditorUtility.DisplayDialog(ManagerText.DlgRemT, message, ManagerText.Rem, ManagerText.Cancel)) return;
            _removing = true;
            var token = _session.token;
            try
            {
                await OnAirDeckApi.ReleaseDeviceAsync(token, device.device_id);
                var notice = DeviceLimitNotice.Current;
                if (notice != null && notice.Kind == NoticeKind.DeviceLimit)
                {
                    // The refused action goes on with the freed seat; it may raise the screen again when another license is still full.
                    try { await DeviceLimitNotice.RetryAsync(); }
                    catch (Exception e) { if (!IsNoticeRefusal(e)) SetMessage(e.Message, MessageType.Error); }
                }
                _devices = null;
                LoadDevices();
                LoadEntries(false);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Removing a device failed: " + e.Message);
            }
            finally
            {
                _removing = false;
                Repaint();
            }
        }

        /// <summary>Contract v7: the person asks for this PC back. A full seat comes back as the device limit screen.</summary>
        private async void ReactivateThisPc()
        {
            if (_session == null || _removing) return;
            _removing = true;
            var token = _session.token;
            try
            {
                var notice = DeviceLimitNotice.Current;
                if (notice != null && notice.Retry != null) await DeviceLimitNotice.RetryAsync();
                else await LicenseService.EnsureLicenseFilesAsync(token, _licenses, true);
            }
            catch (SessionExpiredException e)
            {
                OnSessionExpired(e.Message);
            }
            catch (Exception e)
            {
                if (!IsNoticeRefusal(e)) SetMessage(e.Message, MessageType.Error);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Reactivating this PC failed: " + e.Message);
            }
            finally
            {
                var current = DeviceLimitNotice.Current;
                if (current != null && current.Kind == NoticeKind.DeviceReleased) DeviceLimitNotice.Clear();
                _removing = false;
                RefreshInstalledState();
            }
        }

        // ------------------------------------------------------------ drawing

        private static string DeviceLine(DeviceEntry device)
        {
            var seen = ManagerCatalog.FormatDate(device.last_seen_at);
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(device.platform)) parts.Add(device.platform);
            if (seen.Length > 0) parts.Add(ManagerText.LastSeen.Replace("{d}", seen));
            return string.Join("  ·  ", parts.ToArray());
        }

        /// <summary>One device: its name and line; a tag for this PC, otherwise a remove button (never on this PC's own row).</summary>
        private void DrawDeviceRow(DeviceEntry device)
        {
            SRGui.BeginCard();
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label(device.name, SRStyles.Strong);
            var line = DeviceLine(device);
            if (line.Length > 0) GUILayout.Label(line, SRStyles.Mini);
            GUILayout.EndVertical();
            GUILayout.FlexibleSpace();
            if (device.this_device) SRGui.BadgeLayout(ManagerText.ThisTag, SRTone.Brand);
            else
            {
                using (new GUIEnabledScope(!_removing))
                {
                    if (SRGui.Button(new GUIContent(ManagerText.RemoveDev), SRButtonKind.Danger, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                        Defer(() => RemoveDevice(device));
                }
            }
            GUILayout.EndHorizontal();
            SRGui.EndCard();
            GUILayout.Space(SRMetrics.Space4);
        }

        private void DrawDeviceLimit(Rect body, DeviceNotice notice)
        {
            BeginScreen(body);
            _detailScroll = GUILayout.BeginScrollView(_detailScroll);
            GUILayout.Label(ManagerText.DlTitle, SRStyles.Title);
            GUILayout.Space(SRMetrics.Space6);
            var limit = notice.DeviceLimit;
            if (limit != null)
            {
                foreach (var license in limit.Licenses)
                {
                    var text = ManagerText.DlBody.Replace("{name}", license.name)
                        .Replace("{max}", license.max_devices.ToString()).Replace("{used}", license.used.ToString());
                    GUILayout.Label(text, SRStyles.MutedWrap);
                }
                GUILayout.Space(SRMetrics.Space8);
                foreach (var device in limit.Devices) DrawDeviceRow(device);
            }
            else if (!string.IsNullOrEmpty(notice.Message)) GUILayout.Label(notice.Message, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.Label(ManagerText.DlNote, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space12);
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (SRGui.Button(new GUIContent(ManagerText.Cancel), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                Defer(DeviceLimitNotice.Clear);
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            EndScreen();
        }

        private void DrawDeviceManager(Rect body)
        {
            BeginScreen(body);
            _detailScroll = GUILayout.BeginScrollView(_detailScroll);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ManagerText.DmTitle, SRStyles.Title, GUILayout.ExpandWidth(true));
            if (SRGui.Button(new GUIContent(ManagerText.Back), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => { _view = (int)View.Main; });
            GUILayout.EndHorizontal();
            GUILayout.Label(ManagerText.DmSub, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space8);

            if (_devices == null)
            {
                if (_devicesError != null)
                {
                    if (SRGui.HelpBox(_devicesError, SRTone.Danger, ManagerText.PLoadB)) Defer(LoadDevices);
                }
                else GUILayout.Label(ManagerText.Loading, SRStyles.Muted);
            }
            else if (_devices.limits == null || _devices.limits.Length == 0)
            {
                GUILayout.Label(ManagerText.NoLimits, SRStyles.Muted);
            }
            else
            {
                foreach (var license in _devices.limits)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(license.name, SRStyles.Strong, GUILayout.ExpandWidth(true));
                    GUILayout.Label(DeviceCount(license.used, license.max_devices), SRStyles.Muted, GUILayout.ExpandWidth(false));
                    GUILayout.EndHorizontal();
                    GUILayout.Space(SRMetrics.Space4);
                    DrawDevicesOf(license.license_id);
                    GUILayout.Space(SRMetrics.Space8);
                }
            }
            GUILayout.EndScrollView();
            EndScreen();
        }

        private static string DeviceCount(int used, int max)
        {
            return ManagerText.DevCount.Replace("{u}", used.ToString()).Replace("{m}", max.ToString());
        }

        private void DrawDevicesOf(string licenseId)
        {
            var any = false;
            if (_devices != null && _devices.devices != null)
            {
                foreach (var device in _devices.devices)
                {
                    var holds = false;
                    if (device.licenses != null)
                        foreach (var reference in device.licenses) if (reference.license_id == licenseId) holds = true;
                    if (!holds) continue;
                    any = true;
                    DrawDeviceRow(device);
                }
            }
            if (!any) GUILayout.Label(ManagerText.NoDevices, SRStyles.Muted);
        }

        private void DrawDevicesTab(CatalogEntry entry)
        {
            if (_devices == null && !_devicesLoading && _devicesError == null) Defer(LoadDevices);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ManagerText.TabDev, SRStyles.Strong, GUILayout.ExpandWidth(true));
            GUILayout.Label(DeviceCount(entry.DevicesUsed, entry.MaxDevices), SRStyles.Muted, GUILayout.ExpandWidth(false));
            GUILayout.EndHorizontal();
            GUILayout.Space(SRMetrics.Space4);
            if (_devices == null)
            {
                if (_devicesError != null)
                {
                    if (SRGui.HelpBox(_devicesError, SRTone.Danger, ManagerText.PLoadB)) Defer(LoadDevices);
                }
                else GUILayout.Label(ManagerText.Loading, SRStyles.Muted);
                return;
            }
            DrawDevicesOf(entry.LicenseId);
            GUILayout.Label(ManagerText.DlNote, SRStyles.MutedWrap);
        }
    }
}
