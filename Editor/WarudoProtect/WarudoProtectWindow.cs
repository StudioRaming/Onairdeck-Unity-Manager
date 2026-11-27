using System.IO;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>Seller settings for WARUDO protection: which mods get a .srlock after Build Mod, and for which license.</summary>
    internal sealed class WarudoProtectWindow : EditorWindow
    {
        private static readonly string[] KindValues = { "auto", "prop", "environment", "character" };

        private WarudoProtectSettingsData data;
        private Vector2 scroll;
        private string newModName = "";

        [MenuItem("Window/Atelier WARUDO Protection")]
        public static void Open()
        {
            var w = GetWindow<WarudoProtectWindow>();
            w.titleContent = new GUIContent(WarudoProtectText.WindowTitle);
            w.minSize = new Vector2(520, 320);
            w.Show();
        }

        private void OnEnable()
        {
            data = WarudoProtectSettings.Load();
        }

        /// <summary>After a lock: a non-blocking notice on the window if it is open (no modal, no focus steal).</summary>
        public static void NotifyLocked(string output)
        {
            var windows = Resources.FindObjectsOfTypeAll<WarudoProtectWindow>();
            if (windows.Length == 0) return;
            windows[0].ShowNotification(new GUIContent(WarudoProtectText.LockedShort(Path.GetFileName(output))));
            windows[0].Repaint();
        }

        private void OnFocus()
        {
            // A build may have added a mod while the window was open.
            data = WarudoProtectSettings.Load();
        }

        private void OnGUI()
        {
            if (data == null) data = WarudoProtectSettings.Load();
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(WarudoProtectText.Intro, MessageType.Info);
            if (GUILayout.Button(WarudoProtectText.Guide, EditorStyles.linkLabel)) Application.OpenURL(OnAirDeckConfig.WarudoProtectGuideUrl);

            if (!WarudoProtectHookState.UModPresent)
            {
                EditorGUILayout.HelpBox(WarudoProtectText.NoUMod, MessageType.Warning);
            }
            else if (WarudoProtectHookState.HookLoaded)
            {
                EditorGUILayout.HelpBox(WarudoProtectText.HookOn, MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox(WarudoProtectText.HookOff, MessageType.Warning);
                if (GUILayout.Button(WarudoProtectText.EnableHook)) WarudoProtectHookState.EnableHook();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField(WarudoProtectText.Mods, EditorStyles.boldLabel);
            bool changed = false;
            int removeAt = -1;
            string[] kindLabels = { WarudoProtectText.KindAuto, WarudoProtectText.KindProp, WarudoProtectText.KindEnvironment, WarudoProtectText.KindCharacter };

            scroll = EditorGUILayout.BeginScrollView(scroll);
            if (data.entries.Count == 0) EditorGUILayout.LabelField(WarudoProtectText.NoMods, EditorStyles.wordWrappedMiniLabel);
            for (int i = 0; i < data.entries.Count; i++)
            {
                var e = data.entries[i];
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                e.enabled = EditorGUILayout.ToggleLeft(WarudoProtectText.Protect, e.enabled, GUILayout.Width(80));
                EditorGUILayout.LabelField(e.mod_name, EditorStyles.boldLabel);
                if (GUILayout.Button(WarudoProtectText.Remove, GUILayout.Width(70))) removeAt = i;
                EditorGUILayout.EndHorizontal();

                int k = System.Array.IndexOf(KindValues, string.IsNullOrEmpty(e.kind) ? "auto" : e.kind);
                e.kind = KindValues[EditorGUILayout.Popup(WarudoProtectText.Kind, k < 0 ? 0 : k, kindLabels)];
                e.title = EditorGUILayout.TextField(WarudoProtectText.Title, e.title ?? "");
                if (EditorGUI.EndChangeCheck()) changed = true;
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();

            if (removeAt >= 0)
            {
                data.entries.RemoveAt(removeAt);
                changed = true;
            }

            EditorGUILayout.BeginHorizontal();
            newModName = EditorGUILayout.TextField(newModName ?? "");
            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty((newModName ?? "").Trim())
                   || WarudoProtectSettings.Find(data, newModName.Trim()) != null))
            {
                if (GUILayout.Button(WarudoProtectText.AddMod, GUILayout.Width(170)))
                {
                    string name = newModName.Trim();
                    data.entries.Add(new WarudoProtectEntry { mod_name = name, enabled = true, kind = "auto", title = name });
                    newModName = "";
                    changed = true;
                }
            }
            EditorGUILayout.EndHorizontal();

            if (changed) WarudoProtectSettings.Save(data);

            EditorGUILayout.Space();
            string last = WarudoProtectRunner.LastLocked;
            if (!string.IsNullOrEmpty(last))
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(WarudoProtectText.LastLocked, last, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(WarudoProtectText.OpenFolder, GUILayout.Width(110))) EditorUtility.RevealInFinder(last);
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button(WarudoProtectText.LockExisting)) LockExisting();
        }

        /// <summary>Lock a .warudo that is already built, with the settings of the mod of the same name.</summary>
        private void LockExisting()
        {
            string file = EditorUtility.OpenFilePanel(WarudoProtectText.PickWarudo, "", "warudo");
            if (string.IsNullOrEmpty(file)) return;
            string mod = Path.GetFileNameWithoutExtension(file);
            var entry = WarudoProtectSettings.Find(data, mod);
            if (entry == null)
            {
                entry = new WarudoProtectEntry { mod_name = mod, enabled = true, kind = "auto", title = mod };
                data.entries.Add(entry);
                WarudoProtectSettings.Save(data);
                Debug.Log(WarudoProtectText.NewModRegistered(mod));
            }
            WarudoProtectRunner.Lock(file, entry, true);
        }
    }
}
