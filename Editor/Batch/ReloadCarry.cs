using System;
using System.Collections.Generic;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>An add-on that installs once its core is in (owner rule 2026-11-01: MingToon Core first, then URP/MLC).</summary>
    internal sealed class PendingAddon
    {
        /// <summary>The add-on's list row (CatalogEntry.Key).</summary>
        public string AddonKey = "";
        public string LicenseId = "";
        /// <summary>The core package it waits for.</summary>
        public string Core = "";
        public string Name = "";
        public long SinceUnix;
    }

    /// <summary>A row's reason after a batch: an old install to clean up, or another failure.</summary>
    internal sealed class CarriedRow
    {
        public string Key = "";
        public bool Cleanup;
        public string Text = "";
    }

    /// <summary>
    /// What a run hands over a script reload (the window's dictionaries are not serialized, and the code awaiting a step does not resume):
    /// the add-ons still waiting for their core, and a batch's per-row reasons and retry selection. Plain lines for SessionState; no
    /// UnityEditor, so checked headless.
    /// </summary>
    internal static class ReloadCarry
    {
        /// <summary>SessionState keys (the window writes and reads them; the EditMode tests read them too).</summary>
        public const string PendingAddonsKey = "OnAirDeck.Manager.PendingAddons";
        public const string BatchRowsKey = "OnAirDeck.Manager.BatchRows";

        /// <summary>An add-on not picked up within this time is dropped: an install the person no longer expects never starts on its own.</summary>
        public const long MaxAgeSeconds = 30 * 60;
        /// <summary>A batch's row reasons older than this are not restored (a later window opening is not the reload after that batch).</summary>
        public const long RowsMaxAgeSeconds = 10 * 60;

        // ---------- add-ons waiting for their core ----------

        public static List<PendingAddon> ParseAddons(string text)
        {
            var list = new List<PendingAddon>();
            if (string.IsNullOrEmpty(text)) return list;
            foreach (var line in text.Split('\n'))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 5 || parts[0].Length == 0) continue;
                long since;
                if (!long.TryParse(parts[4], out since)) continue;
                list.Add(new PendingAddon { AddonKey = parts[0], LicenseId = parts[1], Core = parts[2], Name = parts[3], SinceUnix = since });
            }
            return list;
        }

        public static string FormatAddons(IList<PendingAddon> list)
        {
            var sb = new StringBuilder();
            foreach (var a in list)
            {
                if (a == null || string.IsNullOrEmpty(a.AddonKey)) continue;
                sb.Append(Field(a.AddonKey)).Append('\t').Append(Field(a.LicenseId)).Append('\t').Append(Field(a.Core)).Append('\t')
                  .Append(Field(a.Name)).Append('\t').Append(a.SinceUnix).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Adds <paramref name="addon"/>, replacing an older record of the same row.</summary>
        public static string AddAddon(string text, PendingAddon addon)
        {
            var list = ParseAddons(text);
            list.RemoveAll(a => a.AddonKey == addon.AddonKey);
            list.Add(addon);
            return FormatAddons(list);
        }

        public static string RemoveAddon(string text, string addonKey)
        {
            var list = ParseAddons(text);
            list.RemoveAll(a => a.AddonKey == addonKey);
            return FormatAddons(list);
        }

        /// <summary>
        /// After the run that wrote them is gone: an add-on whose core is in now installs (<paramref name="install"/>); one whose core is not
        /// is reported and dropped (<paramref name="missing"/>); one older than <see cref="MaxAgeSeconds"/> (or dated ahead) is dropped
        /// quietly. Every record lands in exactly one list or is dropped.
        /// </summary>
        public static void Split(IList<PendingAddon> list, long nowUnix, Func<string, bool> coreIsHere, List<PendingAddon> install, List<PendingAddon> missing)
        {
            foreach (var a in list)
            {
                if (a == null) continue;
                if (nowUnix - a.SinceUnix > MaxAgeSeconds || a.SinceUnix - nowUnix > 60) continue;
                if (coreIsHere(a.Core)) install.Add(a);
                else missing.Add(a);
            }
        }

        // ---------- a batch's row reasons ----------

        /// <summary>First line: the time and the retry keys; then one line per row reason (newlines in the text become \n escapes).</summary>
        public static string FormatRows(long nowUnix, ICollection<string> retryKeys, IList<CarriedRow> rows)
        {
            var sb = new StringBuilder();
            sb.Append(nowUnix);
            foreach (var key in retryKeys) sb.Append('\t').Append(Field(key));
            sb.Append('\n');
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrEmpty(row.Key)) continue;
                sb.Append(Field(row.Key)).Append('\t').Append(row.Cleanup ? "c" : "f").Append('\t').Append(Escape(row.Text)).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>False when there is nothing, or it is older than <see cref="RowsMaxAgeSeconds"/>.</summary>
        public static bool ParseRows(string text, long nowUnix, List<string> retryKeys, List<CarriedRow> rows)
        {
            if (string.IsNullOrEmpty(text)) return false;
            var lines = text.Split('\n');
            var head = lines[0].TrimEnd('\r').Split('\t');
            long at;
            if (!long.TryParse(head[0], out at) || nowUnix - at > RowsMaxAgeSeconds || at - nowUnix > 60) return false;
            for (var i = 1; i < head.Length; i++) if (head[i].Length > 0) retryKeys.Add(head[i]);
            for (var i = 1; i < lines.Length; i++)
            {
                var parts = lines[i].TrimEnd('\r').Split(new[] { '\t' }, 3);
                if (parts.Length < 3 || parts[0].Length == 0) continue;
                rows.Add(new CarriedRow { Key = parts[0], Cleanup = parts[1] == "c", Text = Unescape(parts[2]) });
            }
            return true;
        }

        private static string Field(string value)
        {
            return (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }

        private static string Escape(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "").Replace("\n", "\\n");
        }

        private static string Unescape(string value)
        {
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c != '\\' || i + 1 >= value.Length) { sb.Append(c); continue; }
                var n = value[++i];
                sb.Append(n == 'n' ? '\n' : n == 't' ? '\t' : n);
            }
            return sb.ToString();
        }
    }
}
