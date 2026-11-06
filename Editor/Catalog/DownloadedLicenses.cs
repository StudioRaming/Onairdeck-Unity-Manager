using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The license versions the signed-in account downloaded through this PC's Manager, in EditorPrefs (every project on the PC sees
    /// it). Written after plugin-download signed a license version (the server recorded that download); read when the list is built.
    /// See <see cref="DownloadedLicensesLogic"/>. A failure to read or write is logged and never stops a download.
    /// </summary>
    internal static class DownloadedLicenses
    {
        /// <summary>Records that the signed-in account got <paramref name="versionId"/>. Nothing without a saved sign-in.</summary>
        public static void RecordVersion(string versionId)
        {
            try
            {
                var session = SessionStore.Load();
                var key = DownloadedLicensesLogic.PrefsKey(session != null ? session.userId : null);
                if (key == null || string.IsNullOrEmpty(versionId)) return;
                EditorPrefs.SetString(key, DownloadedLicensesLogic.Add(EditorPrefs.GetString(key, ""), versionId));
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not note the license download on this PC: " + e.Message);
            }
        }

        /// <summary>The version ids recorded for <paramref name="userId"/> (empty without one).</summary>
        public static List<string> VersionIds(string userId)
        {
            try
            {
                var key = DownloadedLicensesLogic.PrefsKey(userId);
                return key == null ? new List<string>() : DownloadedLicensesLogic.Parse(EditorPrefs.GetString(key, ""));
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not read the license downloads noted on this PC: " + e.Message);
                return new List<string>();
            }
        }
    }
}
