using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>What to do with a license file on disk after asking plugin-license for it again.</summary>
    internal enum RevalidationAction
    {
        /// <summary>The right still holds, or the answer proves nothing (offline, 5xx, 401, 429, 426, device_limit): keep the file until its own exp.</summary>
        Keep,
        /// <summary>The server says the right is gone (refund, cancel, revoked grant, released PC): remove the file now.</summary>
        Delete
    }

    /// <summary>One onairdeck.license under the install root and what its payload names (not verified).</summary>
    internal sealed class RevalidationFile
    {
        /// <summary>Project-relative folder, Assets/StudioRaming_Onairdeck/&lt;folder&gt;.</summary>
        public string Folder = "";
        /// <summary>The file text as read, so a file renewed while the request ran is not removed.</summary>
        public string Text = "";
        /// <summary>A seller-license file (contract v4): asked again by license_id.</summary>
        public string LicenseId = "";
        /// <summary>A purchase-line file (no license_id): asked again by item_id.</summary>
        public string ItemId = "";
        public string UserId = "";

        /// <summary>The key of this right in the last-checked map (per account, so an account switch starts over).</summary>
        public string Key
        {
            get { return UserId.ToLowerInvariant() + "|" + (LicenseId.Length > 0 ? "L:" + LicenseId.ToLowerInvariant() : "I:" + ItemId.ToLowerInvariant()); }
        }
    }

    /// <summary>
    /// Online re-check of the license files already on disk (no UnityEngine, headless-checked). A refunded or revoked right
    /// leaves the account's list, and <see cref="LicenseFilePlan"/> renews only what the list holds, so its file used to keep
    /// working until its exp (up to 7 days). Now the file is asked for again and removed when the server says the right is
    /// gone. The answer rules are the license re-check contract of StudioRaming/Vdesk#661.
    /// </summary>
    internal static class LicenseRevalidation
    {
        /// <summary>A file whose right is not in the account's list is asked again at most this often.</summary>
        public const long OutsideListSeconds = 60L * 60;
        /// <summary>Every file of the signed-in account is asked again at most this often.</summary>
        public const long AllFilesSeconds = 6L * 60 * 60;
        /// <summary>Requests per pass, well under plugin-license's 30 per minute per account; the rest wait for the next refresh.</summary>
        public const int MaxPerPass = 10;

        /// <summary>plugin-license 403 "error" strings that mean the right is gone.</summary>
        private static readonly HashSet<string> Revoked403 = new HashSet<string>(StringComparer.Ordinal)
        {
            "forbidden: no active license grant",
            "forbidden: refunded",
            "forbidden: not paid",
            "forbidden: not your purchase",
            "forbidden: license subject conflict",
            "forbidden: not your product",
        };

        /// <summary>The decision for an HTTP answer: status (0 = no answer) and the server's "error" code.</summary>
        public static RevalidationAction Classify(long status, string code)
        {
            code = code ?? "";
            // plugin-license's own 404 only: a gateway 404 (function missing, wrong URL) has another body and must not
            // remove every file.
            if (status == 404) return code == "not found" ? RevalidationAction.Delete : RevalidationAction.Keep;
            if (status == 403) return Revoked403.Contains(code) ? RevalidationAction.Delete : RevalidationAction.Keep;
            if (status == 409 && code == "device_released") return RevalidationAction.Delete;
            return RevalidationAction.Keep; // 200, 401, 409 device_limit, 426, 429, 5xx, no answer
        }

        /// <summary>The decision for the outcome of one request: null = a license came back (the right holds).</summary>
        public static RevalidationAction Classify(Exception error)
        {
            if (error == null) return RevalidationAction.Keep;
            if (error is SessionExpiredException) return RevalidationAction.Keep;
            var api = error as ApiException;
            return api == null ? RevalidationAction.Keep : Classify(api.Status, api.Code);
        }

        /// <summary>
        /// The license files under &lt;project&gt;/Assets/StudioRaming_Onairdeck that name a right this Manager can ask for again:
        /// a license_id, or an item_id when there is no license_id (the seller's own-product files have neither). Never throws.
        /// </summary>
        public static List<RevalidationFile> Scan(string projectRoot)
        {
            var result = new List<RevalidationFile>();
            try
            {
                if (string.IsNullOrEmpty(projectRoot)) return result;
                var root = Path.Combine(projectRoot, Installer.InstallRoot.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(root)) return result;
                foreach (var dir in Directory.GetDirectories(root))
                {
                    try
                    {
                        var path = Path.Combine(dir, LicenseFile.FileName);
                        if (!File.Exists(path)) continue;
                        var text = File.ReadAllText(path, Encoding.UTF8);
                        string licenseId, itemId, userId;
                        LicenseFile.TryReadPayloadString(text, "license_id", out licenseId);
                        LicenseFile.TryReadPayloadString(text, "item_id", out itemId);
                        if (!LicenseFile.TryReadPayloadString(text, "user_id", out userId) || string.IsNullOrEmpty(userId)) continue;
                        licenseId = (licenseId ?? "").Trim();
                        itemId = (itemId ?? "").Trim();
                        if (licenseId.Length == 0 && itemId.Length == 0) continue;
                        result.Add(new RevalidationFile
                        {
                            Folder = Installer.InstallRoot + "/" + Path.GetFileName(dir),
                            Text = text,
                            LicenseId = licenseId,
                            ItemId = licenseId.Length > 0 ? "" : itemId,
                            UserId = userId.Trim()
                        });
                    }
                    catch (Exception)
                    {
                        // An unreadable file is left alone.
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        /// <summary>
        /// The files to ask for again now, at most <see cref="MaxPerPass"/>: only files issued to <paramref name="userId"/> (the
        /// server answers for the signed-in account, so another account's file is never judged by it). A file whose right is
        /// missing from the account's list is due after <see cref="OutsideListSeconds"/>, any file after
        /// <see cref="AllFilesSeconds"/>; never checked in this editor session = due. A null id list means that list was not
        /// sent, and its files count as listed. Most overdue first.
        /// </summary>
        /// <param name="lastChecked">Key (<see cref="RevalidationFile.Key"/>) to the unix time it was last asked; may be null.</param>
        public static List<RevalidationFile> Select(IList<RevalidationFile> files, string userId, ICollection<string> licenseIds,
            ICollection<string> itemIds, IDictionary<string, long> lastChecked, long nowUnix)
        {
            var due = new List<KeyValuePair<long, RevalidationFile>>();
            if (files == null || string.IsNullOrEmpty(userId)) return new List<RevalidationFile>();
            var licenses = Lower(licenseIds);
            var items = Lower(itemIds);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (file == null || !string.Equals(file.UserId, userId.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                if (!seen.Add(file.Key)) continue; // two folders with the same right: one request
                bool listed;
                if (file.LicenseId.Length > 0) listed = licenses == null || licenses.Contains(file.LicenseId.ToLowerInvariant());
                else listed = items == null || items.Contains(file.ItemId.ToLowerInvariant());
                long last;
                if (lastChecked == null || !lastChecked.TryGetValue(file.Key, out last)) last = 0;
                var age = nowUnix - last;
                if (age >= AllFilesSeconds || (!listed && age >= OutsideListSeconds))
                    due.Add(new KeyValuePair<long, RevalidationFile>(last, file));
            }
            due.Sort((a, b) => a.Key.CompareTo(b.Key));
            var result = new List<RevalidationFile>();
            foreach (var pair in due)
            {
                if (result.Count >= MaxPerPass) break;
                result.Add(pair.Value);
            }
            return result;
        }

        /// <summary>The same right asked again for every folder that holds it (Select sends one request per right).</summary>
        public static List<RevalidationFile> SameRight(IList<RevalidationFile> files, RevalidationFile asked)
        {
            var result = new List<RevalidationFile>();
            if (files == null || asked == null) return result;
            foreach (var file in files)
                if (file != null && file.Key == asked.Key) result.Add(file);
            return result;
        }

        /// <summary>The last-checked map as stored in SessionState: one "key=unix" per line. Bad lines are skipped.</summary>
        public static Dictionary<string, long> ParseChecked(string text)
        {
            var result = new Dictionary<string, long>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return result;
            foreach (var line in text.Split('\n'))
            {
                var eq = line.LastIndexOf('=');
                long at;
                if (eq <= 0 || !long.TryParse(line.Substring(eq + 1).Trim(), out at)) continue;
                result[line.Substring(0, eq)] = at;
            }
            return result;
        }

        public static string FormatChecked(IDictionary<string, long> map)
        {
            var sb = new StringBuilder();
            if (map == null) return "";
            foreach (var pair in map) sb.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            return sb.ToString();
        }

        private static HashSet<string> Lower(ICollection<string> ids)
        {
            if (ids == null) return null;
            var set = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids) if (!string.IsNullOrEmpty(id)) set.Add(id.Trim().ToLowerInvariant());
            return set;
        }
    }
}
