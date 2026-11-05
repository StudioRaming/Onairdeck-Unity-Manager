using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>This PC's device code as a member reads it to support: the hash in groups, a check code that catches a typing error.</summary>
    internal static class OfflineDeviceCode
    {
        public const string CheckPrefix = "sr-device-check|";

        /// <summary>8 groups of 8 hex characters.</summary>
        public static string Groups(string dev)
        {
            var sb = new StringBuilder(dev.Length + 8);
            for (var i = 0; i < dev.Length; i++)
            {
                if (i > 0 && i % 8 == 0) sb.Append(' ');
                sb.Append(dev[i]);
            }
            return sb.ToString();
        }

        /// <summary>The first 4 hex characters of SHA-256 of the prefix + the hash.</summary>
        public static string Check(string dev)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(CheckPrefix + dev));
                var sb = new StringBuilder(4);
                sb.Append(hash[0].ToString("x2")).Append(hash[1].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>The small file a member can send to support. No PC name is filled in for them.</summary>
        public static string FileJson(string dev, string label)
        {
            var root = new Dictionary<string, object> { { "type", "atelier-device" }, { "scheme", "unity-hash" }, { "dev", dev }, { "chk", Check(dev) } };
            if (!string.IsNullOrEmpty(label)) root["label"] = label;
            return JsonWriter.Write(root, "\n");
        }
    }

    /// <summary>The server's 401 for a closed account (its error text is the fixed word account_closed).</summary>
    internal static class ClosedAccount
    {
        public static bool IsClosedAnswer(string body)
        {
            if (string.IsNullOrEmpty(body)) return false;
            try
            {
                var fields = MiniJson.ParseObject(body);
                return fields != null && (MiniJson.GetString(fields, "error") == "account_closed" || MiniJson.GetString(fields, "code") == "account_closed");
            }
            catch (ArgumentException) { return false; }
        }
    }

    internal sealed class OfflineImportResult
    {
        public readonly List<string> Imported = new List<string>();
        /// <summary>Entries fixed to another PC.</summary>
        public int OtherPc;
        /// <summary>WARUDO entries: they belong in the WARUDO Atelier Manager.</summary>
        public int OtherClient;
        /// <summary>Entries this build cannot read (unknown key id).</summary>
        public int UnknownKey;
        /// <summary>Damaged or forged entries.</summary>
        public int Invalid;
        /// <summary>The text was neither a bundle nor a licence string.</summary>
        public bool NotRecognized;
    }

    internal sealed class OfflineEntry
    {
        public string ProductId;
        public string ProductName;
        public LicenseStatus Status;
    }

    /// <summary>
    /// The per-PC folder of offline licence files. Import keeps only Unity files that verify for this PC. No server answer and
    /// no sign-out touches this folder: only Remove does, on the member's click.
    /// </summary>
    internal static class OfflineLicenseStore
    {
        public const string BundleType = "atelier-offline-licences";
        /// <summary>A bundle holds a few kilobytes per file; anything far larger is not one.</summary>
        public const long MaxImportBytes = 4L * 1024 * 1024;

        public static OfflineImportResult Import(string text, string deviceHash, string folder)
        {
            var result = new OfflineImportResult();
            var strings = ReadLicenseStrings(text, result);
            var newest = new Dictionary<string, KeyValuePair<LicenseCheckResult, string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var license in strings)
            {
                var fields = LicenseVerifier.PeekPayload(license);
                if (fields == null) { result.Invalid++; continue; }
                if (MiniJson.GetString(fields, "client") != LicenseVerifier.Client) { result.OtherClient++; continue; }
                var id = MiniJson.GetString(fields, "product_id");
                var check = OfflineLicenseVerifier.Verify(license, id, deviceHash);
                if (check.Status == LicenseStatus.OfflineDeviceMismatch) { result.OtherPc++; continue; }
                if (check.Status == LicenseStatus.OfflineKeyUnknown) { result.UnknownKey++; continue; }
                if (!check.IsValid || !OnAirDeckLicense.IsSafeFileId(id)) { result.Invalid++; continue; }
                KeyValuePair<LicenseCheckResult, string> held;
                if (!newest.TryGetValue(id, out held) || check.IssuedAtUnix > held.Key.IssuedAtUnix)
                    newest[id] = new KeyValuePair<LicenseCheckResult, string>(check, license);
            }
            foreach (var pair in newest)
            {
                var path = Path.Combine(folder, pair.Key + ".license");
                // For one licence only the newest matching file stays; an older bundle never replaces it.
                if (StoredIssuedAt(path, pair.Key, deviceHash) > pair.Value.Key.IssuedAtUnix) continue;
                AtomicFile.Write(path, pair.Value.Value);
                result.Imported.Add(pair.Value.Key.ProductName ?? pair.Key);
            }
            OnAirDeckLicense.ClearCache();
            return result;
        }

        public static List<OfflineEntry> List(string folder, string deviceHash)
        {
            var list = new List<OfflineEntry>();
            if (!Directory.Exists(folder)) return list;
            foreach (var path in Directory.GetFiles(folder, "*.license"))
            {
                var id = Path.GetFileNameWithoutExtension(path);
                string text;
                try { text = File.ReadAllText(path); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                var check = OfflineLicenseVerifier.Verify(text, id, deviceHash);
                list.Add(new OfflineEntry { ProductId = id, ProductName = check.ProductName, Status = check.Status });
            }
            list.Sort((a, b) => string.Compare(a.ProductName ?? a.ProductId, b.ProductName ?? b.ProductId, StringComparison.CurrentCulture));
            return list;
        }

        public static void Remove(string folder, string productId)
        {
            if (!OnAirDeckLicense.IsSafeFileId(productId)) return;
            var path = Path.Combine(folder, productId + ".license");
            if (File.Exists(path)) File.Delete(path);
            OnAirDeckLicense.ClearCache();
        }

        private static long StoredIssuedAt(string path, string id, string deviceHash)
        {
            if (!File.Exists(path)) return -1;
            try
            {
                var check = OfflineLicenseVerifier.Verify(File.ReadAllText(path), id, deviceHash);
                return check.IsValid ? check.IssuedAtUnix : -1;
            }
            catch (IOException) { return -1; }
            catch (UnauthorizedAccessException) { return -1; }
        }

        private static List<string> ReadLicenseStrings(string text, OfflineImportResult result)
        {
            var strings = new List<string>();
            var trimmed = (text ?? "").Trim();
            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                Dictionary<string, object> bundle = null;
                try { bundle = MiniJson.ParseObject(trimmed); }
                catch (ArgumentException) { }
                object entries = null;
                if (bundle == null || MiniJson.GetString(bundle, "type") != BundleType || !bundle.TryGetValue("entries", out entries) || !(entries is List<object>))
                {
                    result.NotRecognized = true;
                    return strings;
                }
                foreach (var entry in (List<object>)entries)
                {
                    var license = entry is Dictionary<string, object> ? MiniJson.GetString((Dictionary<string, object>)entry, "license") : null;
                    if (string.IsNullOrEmpty(license)) result.Invalid++;
                    else strings.Add(license);
                }
                return strings;
            }
            if (trimmed.IndexOf('.') > 0 && trimmed.IndexOfAny(new[] { ' ', '\n', '\r', '\t' }) < 0) strings.Add(trimmed);
            else result.NotRecognized = true;
            return strings;
        }
    }
}
