using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>
    /// What happens after a successful WARUDO mod build (called by the UMod hook, WarudoProtectBuildHook) or from the
    /// window's "lock an already built .warudo": looks the mod up in the project's settings and writes &lt;mod&gt;.srlock
    /// next to the built file. Never throws into the build and never blocks it: every outcome is a log line.
    /// </summary>
    internal static class WarudoProtectRunner
    {
        // UMod's EventHooks.LoadEventHooks appends the hook types again on every build, so one build can call the hook
        // several times: a file is handled once per (path, write time).
        private static readonly Dictionary<string, DateTime> Handled = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static void OnModBuilt(string builtFile)
        {
            try
            {
                if (string.IsNullOrEmpty(builtFile) || !File.Exists(builtFile)) return;
                DateTime written = File.GetLastWriteTimeUtc(builtFile);
                DateTime seen;
                if (Handled.TryGetValue(builtFile, out seen) && seen == written) return;
                Handled[builtFile] = written;

                string mod = Path.GetFileNameWithoutExtension(builtFile);
                var data = WarudoProtectSettings.Load();
                var entry = WarudoProtectSettings.Find(data, mod);
                if (entry == null)
                {
                    data.entries.Add(new WarudoProtectEntry { mod_name = mod, enabled = false, kind = "auto", title = mod });
                    WarudoProtectSettings.Save(data);
                    Debug.Log(WarudoProtectText.NewModRegistered(mod));
                    return;
                }
                if (!entry.enabled) return;
                Lock(builtFile, entry, true);
            }
            catch (Exception e)
            {
                Debug.LogError(WarudoProtectText.Failed(Path.GetFileNameWithoutExtension(builtFile ?? ""), e.Message));
            }
        }

        /// <summary>The .srlock this editor session wrote last (shown in the protection window).</summary>
        public static string LastLocked { get; private set; }

        /// <summary>Locks <paramref name="builtFile"/> with <paramref name="entry"/>; returns the .srlock path or null.</summary>
        public static string Lock(string builtFile, WarudoProtectEntry entry, bool notify)
        {
            string mod = Path.GetFileNameWithoutExtension(builtFile);
            if (!SrLockWriter.IsLicenseId((entry.license_id ?? "").Trim()))
            {
                Debug.LogError(WarudoProtectText.NotReady(mod, WarudoProtectText.BadLicense));
                return null;
            }
            string kind = WarudoProtectSettings.ResolveKind(entry, builtFile);
            if (kind == null)
            {
                Debug.LogError(WarudoProtectText.NotReady(mod, WarudoProtectText.KindUnknown));
                return null;
            }
            string output = Path.Combine(Path.GetDirectoryName(builtFile) ?? ".", mod + SrLockWriter.Extension);
            try
            {
                SrLockWriter.Lock(builtFile, output, entry.license_id, kind, string.IsNullOrEmpty(entry.title) ? mod : entry.title, mod);
            }
            catch (Exception e)
            {
                Debug.LogError(WarudoProtectText.Failed(mod, e.Message));
                return null;
            }
            Debug.Log(WarudoProtectText.Locked(output));
            LastLocked = output;
            if (notify && !Application.isBatchMode)
            {
                // Never a modal: one after every build would block the editor (and any scripted build) until clicked
                // (2026-11-17 test). The protection window shows the result with a folder button instead.
                EditorApplication.delayCall += () => WarudoProtectWindow.NotifyLocked(output);
            }
            return output;
        }
    }
}
