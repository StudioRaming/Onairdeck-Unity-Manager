using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The pure part of the Manager's own record of license downloads on this PC (no UnityEngine, headless-checked): the license
    /// version ids this account got through plugin-download, oldest first, one per line, per account in EditorPrefs.
    /// <see cref="DownloadedLicenses"/> does the EditorPrefs part. It stands in for the server's <see cref="LicenseEntry.downloaded"/>
    /// while a server does not send that: a license downloaded and then removed from the project still reads "downloaded" (its refund
    /// already ended), and its next install asks no refund question (QA 2026-11-06 F5, F7). Display and wording only, never a gate.
    /// </summary>
    internal static class DownloadedLicensesLogic
    {
        public const string PrefsPrefix = "OnAirDeck.UnityManager.DownloadedLicenseVersions.";

        /// <summary>The newest ids kept; an older one drops off (a license usually keeps its recent versions listed).</summary>
        public const int MaxEntries = 500;

        /// <summary>One EditorPrefs key per account on this PC, or null without a user id (nothing is stored then).</summary>
        public static string PrefsKey(string userId)
        {
            return string.IsNullOrEmpty(userId) ? null : PrefsPrefix + userId;
        }

        /// <summary>The stored ids in order, trimmed, without empty lines or repeats.</summary>
        public static List<string> Parse(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in raw.Split('\n'))
            {
                var id = line.Trim();
                if (id.Length > 0 && seen.Add(id)) list.Add(id);
            }
            return list;
        }

        /// <summary>The stored text with <paramref name="versionId"/> as the newest entry (moved to the end when it was there), capped at <see cref="MaxEntries"/>.</summary>
        public static string Add(string raw, string versionId)
        {
            var list = Parse(raw);
            var id = (versionId ?? "").Trim();
            if (id.Length == 0) return string.Join("\n", list.ToArray());
            list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            list.Add(id);
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            return string.Join("\n", list.ToArray());
        }

        /// <summary>
        /// The license ids among <paramref name="licenses"/> this account downloaded before: the server says so, or one of the license's
        /// listed versions is in <paramref name="versionIds"/>.
        /// </summary>
        public static HashSet<string> DownloadedLicenseIds(LicenseEntry[] licenses, ICollection<string> versionIds)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (licenses == null) return ids;
            var recorded = new HashSet<string>(versionIds ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (var license in licenses)
            {
                if (license == null || string.IsNullOrEmpty(license.license_id)) continue;
                var downloaded = license.downloaded;
                if (!downloaded && recorded.Count > 0 && license.versions != null)
                    foreach (var v in license.versions)
                        if (v != null && !string.IsNullOrEmpty(v.version_id) && recorded.Contains(v.version_id)) { downloaded = true; break; }
                if (downloaded) ids.Add(license.license_id);
            }
            return ids;
        }
    }
}
