using System;
using System.IO;
using OnAirDeck.License;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Offline licences, usable with no sign-in and no network (terms 9-2, design r6.6 5.2): this PC's device code for support, and
    /// the import of the licence file a member gets at withdrawal or service end. Reachable from the sign-in screen.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private string _offlineMessage;
        [NonSerialized] private SRTone _offlineTone;
        [NonSerialized] private string _offlineLabel = "";

        private void OpenOffline()
        {
            _view = (int)View.Offline;
            _offlineMessage = null;
            _timeoutText = null;
            Repaint();
        }

        private void DrawOffline(Rect body)
        {
            BeginScreen(body);
            _detailScroll = GUILayout.BeginScrollView(_detailScroll);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ManagerText.OlTitle, SRStyles.Title, GUILayout.ExpandWidth(true));
            if (SRGui.Button(new GUIContent(ManagerText.OlBack), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => { _view = (int)View.Main; });
            GUILayout.EndHorizontal();
            GUILayout.Label(ManagerText.OlSub, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space8);

            var dev = OnAirDeckLicense.ResolveDeviceHash();
            GUILayout.Label(ManagerText.OlCodeTitle, SRStyles.Strong);
            if (string.IsNullOrEmpty(dev))
            {
                SRGui.HelpBox(ManagerText.OlNoDevice, SRTone.Danger);
            }
            else
            {
                var chk = OfflineDeviceCode.Check(dev);
                var shown = OfflineDeviceCode.Groups(dev) + "  (" + chk + ")";
                EditorGUILayout.SelectableLabel(shown, SRStyles.MutedWrap, GUILayout.Height(EditorGUIUtility.singleLineHeight * 2f));
                _offlineLabel = EditorGUILayout.TextField(ManagerText.OlLabel, _offlineLabel ?? "");
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (SRGui.Button(new GUIContent(ManagerText.OlCopy), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                        EditorGUIUtility.systemCopyBuffer = dev + " " + chk;
                    if (SRGui.Button(new GUIContent(ManagerText.OlSaveCode), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                        Defer(() => SaveDeviceFile(dev));
                }
            }
            GUILayout.Space(SRMetrics.Space12);

            using (new GUIEnabledScope(!string.IsNullOrEmpty(dev)))
            {
                if (SRGui.Button(new GUIContent(ManagerText.OlImport), SRButtonKind.Primary, SRButtonSize.Default, GUILayout.ExpandWidth(true)))
                    Defer(() => ImportOfflineFile(dev));
            }
            if (!string.IsNullOrEmpty(_offlineMessage)) SRGui.HelpBox(_offlineMessage, _offlineTone);
            GUILayout.Space(SRMetrics.Space12);

            GUILayout.Label(ManagerText.OlList, SRStyles.Strong);
            var folder = OnAirDeckLicense.ResolveOfflineFolder();
            var entries = OfflineLicenseStore.List(folder, dev);
            if (entries.Count == 0) GUILayout.Label(ManagerText.OlEmpty, SRStyles.Muted);
            foreach (var entry in entries)
            {
                SRGui.BeginCard();
                GUILayout.BeginHorizontal();
                GUILayout.BeginVertical();
                GUILayout.Label(entry.ProductName ?? entry.ProductId, SRStyles.Strong);
                GUILayout.Label(ManagerText.OlStatus(entry.Status), SRStyles.Mini);
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                var id = entry.ProductId;
                if (SRGui.Button(new GUIContent(ManagerText.OlRemove), SRButtonKind.Danger, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                    Defer(() => RemoveOffline(folder, id));
                GUILayout.EndHorizontal();
                SRGui.EndCard();
                GUILayout.Space(SRMetrics.Space4);
            }
            GUILayout.EndScrollView();
            EndScreen();
        }

        private void SaveDeviceFile(string dev)
        {
            var path = EditorUtility.SaveFilePanel(ManagerText.OlSaveCode, "", "atelier-device", "json");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllText(path, OfflineDeviceCode.FileJson(dev, _offlineLabel));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                SetOfflineMessage(e.Message, SRTone.Danger);
            }
        }

        private void ImportOfflineFile(string dev)
        {
            var path = EditorUtility.OpenFilePanel(ManagerText.OlImport, "", "json,license,txt");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (new FileInfo(path).Length > OfflineLicenseStore.MaxImportBytes)
                {
                    SetOfflineMessage(ManagerText.OlNotRecognized, SRTone.Danger);
                    return;
                }
                var result = OfflineLicenseStore.Import(File.ReadAllText(path), dev, OnAirDeckLicense.ResolveOfflineFolder());
                SetOfflineMessage(ManagerText.OlResult(result), result.Imported.Count > 0 ? SRTone.Success : SRTone.Warning);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                SetOfflineMessage(e.Message, SRTone.Danger);
            }
        }

        private void RemoveOffline(string folder, string productId)
        {
            if (!EditorUtility.DisplayDialog(ManagerText.OlRemove, ManagerText.OlRemoveAsk, ManagerText.OlRemove, ManagerText.Cancel)) return;
            try { OfflineLicenseStore.Remove(folder, productId); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { SetOfflineMessage(e.Message, SRTone.Danger); }
        }

        private void SetOfflineMessage(string message, SRTone tone)
        {
            _offlineMessage = message;
            _offlineTone = tone;
            Repaint();
        }
    }
}
