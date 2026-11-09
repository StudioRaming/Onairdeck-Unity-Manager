using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The signed license file written next to a downloaded product:
    /// Assets/StudioRaming_Onairdeck/&lt;Product&gt;/onairdeck.license — one line,
    /// "&lt;base64url(payload JSON)&gt;.&lt;base64url(RSA signature)&gt;", issued by plugin-license.
    /// This class only stores and inspects the file. Signature verification (RSA public key)
    /// is done by the runtime check that ships with sellers' assets, and by the server.
    /// Kept free of Unity JSON APIs so the headless verification build can exercise it.
    /// </summary>
    internal static class LicenseFile
    {
        public const string FileName = "onairdeck.license";

        /// <summary>
        /// Re-issue when the license expires within this window. plugin-license issues it for 14 days, and every successful
        /// re-check (<see cref="LicenseRevalidation.CanRenew"/>) renews it too, so this window only matters for a file the re-check
        /// has not reached yet.
        /// </summary>
        public const long RefreshWindowSeconds = 2L * 24 * 60 * 60;

        /// <summary>Absolute path of the license for a project-relative product folder.</summary>
        public static string PathFor(string productFolder)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            var absoluteFolder = Path.GetFullPath(Path.Combine(projectRoot, productFolder.Replace('/', Path.DirectorySeparatorChar)));
            return Path.Combine(absoluteFolder, FileName);
        }

        /// <summary>Writes the license string (creating the product folder). Returns the absolute path.</summary>
        public static string Write(string productFolder, string license)
        {
            var trimmed = (license ?? "").Trim();
            if (trimmed.IndexOf('.') <= 0) throw new ArgumentException("Not a license string.");
            var path = PathFor(productFolder);
            var folder = Path.GetDirectoryName(path);
            if (!PathSafety.IsInside(folder, path)) throw new Exception("Refusing to write outside " + productFolder);
            Directory.CreateDirectory(folder);
            WriteFileAtomic(path, trimmed + "\n");
            return path;
        }

        /// <summary>
        /// Writes <paramref name="text"/> to a hidden temp file next to <paramref name="path"/> (Unity skips names that start
        /// with '.'), then swaps it in, so a full disk or an I/O error mid-write leaves the old license whole instead of a
        /// truncated one. The swap keeps the old file as a hidden backup until the new one is in place: Windows' ReplaceFile can
        /// move the old file aside and then fail to move the new one in (ERROR_UNABLE_TO_MOVE_REPLACEMENT_2 with a backup named). On failure
        /// <see cref="SettleFailedSwap"/> puts the new or the old license back, and a backup left by a failure nothing could
        /// settle is restored by the next write or the next <see cref="LicenseRevalidation.Scan"/>
        /// (<see cref="RestoreBackup"/>). The error goes on unless the new license ended up in place.
        /// </summary>
        internal static void WriteFileAtomic(string path, string text)
        {
            var temp = TempFor(path);
            var backup = BackupFor(path);
            RestoreBackup(path);
            var written = false;
            var swapping = false;
            try
            {
                WriteText(temp, text);
                written = true;
                if (File.Exists(path))
                {
                    TryDelete(backup); // a stale one: the license it held is still at path
                    swapping = true;
                    ReplaceFile(temp, path, backup);
                }
                else MoveFile(temp, path);
                TryDelete(backup);
            }
            catch
            {
                if (SettleFailedSwap(temp, path, backup, written, swapping)) return;
                throw;
            }
        }

        /// <summary>The hidden backup <see cref="WriteFileAtomic"/> keeps next to <paramref name="path"/> during a swap.</summary>
        internal static string BackupFor(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path) ?? "", "." + Path.GetFileName(path) + ".bak");
        }

        /// <summary>The hidden temp file <see cref="WriteFileAtomic"/> writes before the swap.</summary>
        internal static string TempFor(string path)
        {
            return Path.Combine(Path.GetDirectoryName(path) ?? "", "." + Path.GetFileName(path) + ".tmp");
        }

        /// <summary>
        /// Puts a backup left by a failed swap back at <paramref name="path"/> when no license is there (the runtime check reads
        /// only <see cref="FileName"/>). Only an unsettled swap counts, one whose temp file is still beside it: a backup a delete
        /// failed to clear next to a license removed on purpose (a refund, a product removal) never comes back. Never throws.
        /// </summary>
        internal static void RestoreBackup(string path)
        {
            var backup = BackupFor(path);
            var temp = TempFor(path);
            if (!File.Exists(path) && File.Exists(backup) && File.Exists(temp) && TryMove(backup, path)) TryDelete(temp);
        }

        /// <summary>
        /// Removes the swap's temp and backup files next to <paramref name="path"/> before the license itself is removed. False
        /// while both are still there: <see cref="RestoreBackup"/> would bring the removed license back, so the caller keeps the
        /// license and tries again at its next pass.
        /// </summary>
        internal static bool DeleteSwapFiles(string path)
        {
            var temp = TempFor(path);
            var backup = BackupFor(path);
            TryDelete(temp);
            TryDelete(backup);
            return !(File.Exists(temp) && File.Exists(backup));
        }

        /// <summary>The write, swap and move <see cref="WriteFileAtomic"/> uses; the headless checks inject failures here.</summary>
        internal static Action<string, string> WriteText = (file, text) => File.WriteAllText(file, text, new UTF8Encoding(false));
        internal static Action<string, string, string> ReplaceFile = (source, destination, backup) => File.Replace(source, destination, backup);
        internal static Action<string, string> MoveFile = (source, destination) => File.Move(source, destination);

        /// <summary>
        /// After a failed write or swap, with no license left at <paramref name="path"/>: the whole new file is moved in, or else
        /// the old one from <paramref name="backup"/>, only when this write's own swap (<paramref name="swapping"/>) put it there:
        /// a backup from before the write may belong to a license removed on purpose. The temp file is removed once a license is
        /// in place (or it was never whole); a backup that could not be moved back stays for <see cref="RestoreBackup"/>. True
        /// when the new license is in place.
        /// </summary>
        internal static bool SettleFailedSwap(string temp, string path, string backup, bool written, bool swapping)
        {
            if (!File.Exists(path) && written && File.Exists(temp) && TryMove(temp, path))
            {
                TryDelete(backup);
                return true;
            }
            if (swapping && !File.Exists(path) && File.Exists(backup)) TryMove(backup, path);
            if (File.Exists(path) || !written) TryDelete(temp);
            return false;
        }

        /// <summary>Three tries 100 ms apart: an antivirus or indexer holding the file lets go within that.</summary>
        private static bool TryMove(string source, string destination)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { MoveFile(source, destination); return true; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                if (attempt == 2 || File.Exists(destination) || !File.Exists(source)) return false;
                System.Threading.Thread.Sleep(100);
            }
        }

        private static void TryDelete(string file)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// True when there is no license yet, it is unreadable, it expires within RefreshWindowSeconds, or it was issued to
        /// another account (<paramref name="userId"/>) or, when the file carries a "device" field, another PC
        /// (<paramref name="deviceHash"/>). Empty userId or deviceHash skips that comparison.
        /// </summary>
        public static bool NeedsRefresh(string productFolder, long nowUnix, string userId = null, string deviceHash = null)
        {
            var path = PathFor(productFolder);
            if (!File.Exists(path)) return true;
            string text;
            try { text = File.ReadAllText(path); }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
            return IsDue(text, nowUnix, userId, deviceHash);
        }

        /// <summary>The decision of <see cref="NeedsRefresh"/> for a license string already read. Does not verify the signature.</summary>
        public static bool IsDue(string licenseText, long nowUnix, string userId, string deviceHash)
        {
            long exp;
            if (!TryReadExpiry(licenseText, out exp)) return true;
            if (exp - nowUnix < RefreshWindowSeconds) return true;
            var json = PayloadJson(licenseText);
            if (json == null) return true;
            string value;
            if (!string.IsNullOrEmpty(userId) && (!TryReadString(json, "user_id", out value) || !string.Equals(value, userId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrEmpty(deviceHash) && TryReadString(json, "device", out value) && !string.Equals(value, deviceHash, StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>A top-level string field of the license payload (not verified); false when absent or malformed.</summary>
        internal static bool TryReadPayloadString(string licenseText, string field, out string value)
        {
            value = null;
            var json = PayloadJson(licenseText);
            return json != null && TryReadString(json, field, out value);
        }

        private static string PayloadJson(string license)
        {
            if (string.IsNullOrEmpty(license)) return null;
            var dot = license.IndexOf('.');
            if (dot <= 0) return null;
            try { return Encoding.UTF8.GetString(FromBase64Url(license.Substring(0, dot).Trim())); }
            catch (FormatException) { return null; }
        }

        /// <summary>Minimal extractor for a top-level plain string field (ids and hashes: no escapes expected).</summary>
        internal static bool TryReadString(string json, string field, out string value)
        {
            value = null;
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return false;
            var i = json.IndexOf(':', at + key.Length);
            if (i < 0) return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return false;
            var end = json.IndexOf('"', i + 1);
            if (end < 0) return false;
            value = json.Substring(i + 1, end - i - 1);
            return true;
        }

        /// <summary>Reads "exp" (unix seconds) out of the payload half. Does not verify the signature.</summary>
        public static bool TryReadExpiry(string license, out long exp)
        {
            exp = 0;
            if (string.IsNullOrEmpty(license)) return false;
            var dot = license.IndexOf('.');
            if (dot <= 0) return false;
            string json;
            try { json = Encoding.UTF8.GetString(FromBase64Url(license.Substring(0, dot).Trim())); }
            catch (FormatException) { return false; }
            return TryReadNumber(json, "exp", out exp);
        }

        internal static byte[] FromBase64Url(string value)
        {
            var s = value.Replace('-', '+').Replace('_', '/');
            switch (s.Length % 4)
            {
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        /// <summary>Minimal extractor for a top-level integer field (no JSON dependency).</summary>
        internal static bool TryReadNumber(string json, string field, out long value)
        {
            value = 0;
            var key = "\"" + field + "\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return false;
            var i = json.IndexOf(':', at + key.Length);
            if (i < 0) return false;
            i++;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            var start = i;
            while (i < json.Length && (char.IsDigit(json[i]) || (i == start && json[i] == '-'))) i++;
            return i > start && long.TryParse(json.Substring(start, i - start), out value);
        }
    }
}
