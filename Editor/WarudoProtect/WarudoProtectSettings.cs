using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace OnAirDeck.UnityManager.WarudoProtect
{
    /// <summary>One WARUDO mod (export profile) of this project and how its build is protected.</summary>
    [Serializable]
    internal sealed class WarudoProtectEntry
    {
        /// <summary>The export profile's mod name = the built file's name without ".warudo".</summary>
        public string mod_name;
        public bool enabled;
        /// <summary>Unused since v3 (no license id at build; kept so older settings files still load).</summary>
        public string license_id;
        /// <summary>"auto" (from the output folder: Props / Environments / Characters) or prop, environment, character.</summary>
        public string kind = "auto";
        /// <summary>The name buyers see in WARUDO's list; empty = the mod name.</summary>
        public string title;
    }

    [Serializable]
    internal sealed class WarudoProtectSettingsData
    {
        public int v = 1;
        public List<WarudoProtectEntry> entries = new List<WarudoProtectEntry>();
    }

    /// <summary>
    /// Per-project settings in ProjectSettings/AtelierWarudoProtect.json (outside Assets/, so never exported in a mod).
    /// Holds no secret: a license id only says which license unlocks the file.
    /// </summary>
    internal static class WarudoProtectSettings
    {
        public const string FileName = "AtelierWarudoProtect.json";

        public static string FilePath
        {
            get { return Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", "ProjectSettings", FileName); }
        }

        public static WarudoProtectSettingsData Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var data = JsonUtility.FromJson<WarudoProtectSettingsData>(File.ReadAllText(FilePath));
                    if (data != null)
                    {
                        if (data.entries == null) data.entries = new List<WarudoProtectEntry>();
                        return data;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Atelier] " + FileName + ": " + e.Message);
            }
            return new WarudoProtectSettingsData();
        }

        public static void Save(WarudoProtectSettingsData data)
        {
            string path = FilePath;
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(data, true));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static WarudoProtectEntry Find(WarudoProtectSettingsData data, string modName)
        {
            foreach (var e in data.entries)
            {
                if (string.Equals(e.mod_name, modName, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        /// <summary>The kind to write: the entry's own, or from the folder the build went to.</summary>
        public static string ResolveKind(WarudoProtectEntry entry, string builtFile)
        {
            if (entry != null && SrLockWriter.IsKind(entry.kind)) return entry.kind;
            string folder = Path.GetFileName(Path.GetDirectoryName(builtFile) ?? "") ?? "";
            if (string.Equals(folder, "Props", StringComparison.OrdinalIgnoreCase)) return "prop";
            if (string.Equals(folder, "Environments", StringComparison.OrdinalIgnoreCase)) return "environment";
            if (string.Equals(folder, "Characters", StringComparison.OrdinalIgnoreCase)) return "character";
            return null;
        }
    }
}
