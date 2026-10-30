using System;
using System.Collections.Generic;
using System.IO;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>Which version of a non-package license (a .unitypackage or loose files) the Manager last installed in this project.</summary>
    internal sealed class InstallRecord
    {
        public string LicenseId = "";
        public string VersionId = "";
        public string Label = "";
        public string ReleasedAt = "";
    }

    /// <summary>
    /// The Manager's own record of installed non-package versions, in ProjectSettings/OnAirDeck/installed-versions.json, so it
    /// travels with the project. A .unitypackage has no version the Manager can read back (the product marker holds only the
    /// license id, and Unity keeps the packed file times), so this record is the only source of "which version is installed".
    /// A version is recorded only after it is in place: loose files when they are copied, a .unitypackage when its import
    /// finished (<see cref="PendingImportRecords"/>). No UnityEngine, headless-checked.
    /// </summary>
    internal static class InstallRecords
    {
        public static string PathFor(string projectRoot)
        {
            return Path.Combine(Path.Combine(Path.Combine(projectRoot, "ProjectSettings"), "OnAirDeck"), "installed-versions.json");
        }

        /// <summary>The records by license id (case-insensitive). Never throws: a missing, unreadable or malformed file is no records.</summary>
        public static Dictionary<string, InstallRecord> Load(string projectRoot)
        {
            var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(projectRoot)) return records;
            try
            {
                var path = PathFor(projectRoot);
                if (!File.Exists(path)) return records;
                var root = MiniJson.ParseObject(File.ReadAllText(path));
                object raw;
                if (root == null || !root.TryGetValue("licenses", out raw)) return records;
                var list = raw as List<object>;
                if (list == null) return records;
                foreach (var item in list)
                {
                    var record = FromJson(item as Dictionary<string, object>);
                    if (record != null) records[record.LicenseId] = record;
                }
            }
            catch (Exception)
            {
                // An unreadable record is the same as no record: the window shows "version unknown", never a wrong version.
            }
            return records;
        }

        /// <summary>Adds or replaces the record of one license and writes the file atomically. Throws when it cannot be written.</summary>
        public static void Save(string projectRoot, InstallRecord record)
        {
            if (string.IsNullOrEmpty(projectRoot)) throw new ArgumentException("A project root is required.", "projectRoot");
            if (record == null || string.IsNullOrEmpty(record.LicenseId) || string.IsNullOrEmpty(record.VersionId))
                throw new ArgumentException("A record needs a license id and a version id.", "record");
            var records = Load(projectRoot);
            records[record.LicenseId] = record;
            var ids = new List<string>(records.Keys);
            ids.Sort(StringComparer.OrdinalIgnoreCase);
            var list = new List<object>();
            foreach (var id in ids) list.Add(ToJson(records[id]));
            var root = new Dictionary<string, object> { { "format", 1L }, { "licenses", list } };
            // keepPrevious: an unreadable file (a merge conflict) reads as no records and is replaced, so its last copy stays as .prev.
            AtomicFile.Write(PathFor(projectRoot), JsonWriter.Write(root, "\n") + "\n", true);
        }

        /// <summary>One record as a single JSON line (the import queue keeps it in SessionState until the import finishes).</summary>
        public static string Serialize(InstallRecord record)
        {
            return JsonWriter.Write(ToJson(record), "");
        }

        public static InstallRecord Deserialize(string json)
        {
            return FromJson(MiniJson.ParseObject(json));
        }

        private static Dictionary<string, object> ToJson(InstallRecord record)
        {
            return new Dictionary<string, object>
            {
                { "license_id", record.LicenseId ?? "" },
                { "version_id", record.VersionId ?? "" },
                { "label", record.Label ?? "" },
                { "released_at", record.ReleasedAt ?? "" },
            };
        }

        private static InstallRecord FromJson(Dictionary<string, object> obj)
        {
            if (obj == null) return null;
            var licenseId = (MiniJson.GetString(obj, "license_id") ?? "").Trim();
            var versionId = (MiniJson.GetString(obj, "version_id") ?? "").Trim();
            if (licenseId.Length == 0 || versionId.Length == 0) return null;
            return new InstallRecord
            {
                LicenseId = licenseId,
                VersionId = versionId,
                Label = MiniJson.GetString(obj, "label") ?? "",
                ReleasedAt = MiniJson.GetString(obj, "released_at") ?? "",
            };
        }
    }

    /// <summary>
    /// Records waiting for their .unitypackage imports, kept as text in SessionState so they survive the domain reload an import
    /// with scripts causes. Each line is "&lt;package path&gt;\t&lt;batch&gt;\t&lt;record json&gt;"; one install queues one batch.
    /// A record is released only when every package of its batch imported; a cancelled or failed import drops the batch's
    /// record, so the window keeps showing the last version that really went in. Pure text in, text out (headless-checked).
    /// </summary>
    internal static class PendingImportRecords
    {
        public static string Add(string state, IEnumerable<string> packagePaths, string batch, InstallRecord record)
        {
            var lines = Lines(state);
            var json = InstallRecords.Serialize(record);
            foreach (var path in packagePaths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                lines.RemoveAll(l => SamePath(Field(l, 0), path));
                lines.Add(path + "\t" + batch + "\t" + json);
            }
            return string.Join("\n", lines.ToArray());
        }

        /// <summary>
        /// One queued package finished. Returns the new state; <paramref name="ready"/> is the record to save when this was the
        /// last package of a batch whose imports all succeeded, otherwise null.
        /// </summary>
        public static string Finish(string state, string packagePath, bool imported, out InstallRecord ready)
        {
            ready = null;
            var lines = Lines(state);
            var index = lines.FindIndex(l => SamePath(Field(l, 0), packagePath));
            if (index < 0) return string.Join("\n", lines.ToArray());
            var batch = Field(lines[index], 1);
            var json = Field(lines[index], 2);
            lines.RemoveAt(index);
            if (!imported)
                lines.RemoveAll(l => Field(l, 1) == batch);
            else if (!lines.Exists(l => Field(l, 1) == batch))
                ready = InstallRecords.Deserialize(json);
            return string.Join("\n", lines.ToArray());
        }

        private static List<string> Lines(string state)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(state)) return lines;
            foreach (var line in state.Split('\n'))
                if (line.Split('\t').Length == 3) lines.Add(line);
            return lines;
        }

        private static string Field(string line, int index)
        {
            var parts = line.Split('\t');
            return index < parts.Length ? parts[index] : "";
        }

        private static bool SamePath(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
