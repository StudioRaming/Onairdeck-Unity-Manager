using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace OnAirDeck.UnityManager
{
    internal sealed class ThumbnailFile
    {
        public string Path;
        public long Size;
        /// <summary>Last use (a cache hit touches the file), UTC.</summary>
        public DateTime LastUsedUtc;
    }

    /// <summary>The pure rules of <see cref="ThumbnailCache"/> (no UnityEngine, headless-checked).</summary>
    internal static class ThumbnailCachePolicy
    {
        public const long MaxCacheBytes = 64L * 1024 * 1024;
        public const int MaxFileBytes = 2 * 1024 * 1024;

        /// <summary>Only absolute https URLs with a host and no embedded credentials.</summary>
        public static bool IsAllowedUrl(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 2048) return false;
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.UserInfo);
        }

        /// <summary>Stable file name for a URL: lowercase hex SHA-256 plus ".img" (no path characters from the server ever reach the disk).</summary>
        public static string FileNameFor(string url)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
                var sb = new StringBuilder(68);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.Append(".img").ToString();
            }
        }

        /// <summary>The files to delete so the rest fits in <paramref name="maxBytes"/>: least recently used first (ties by path).</summary>
        public static List<ThumbnailFile> PlanEviction(IList<ThumbnailFile> files, long maxBytes)
        {
            var doomed = new List<ThumbnailFile>();
            long total = 0;
            foreach (var f in files) total += f.Size;
            if (total <= maxBytes) return doomed;
            var ordered = new List<ThumbnailFile>(files);
            ordered.Sort((a, b) =>
            {
                var c = a.LastUsedUtc.CompareTo(b.LastUsedUtc);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });
            foreach (var f in ordered)
            {
                if (total <= maxBytes) break;
                doomed.Add(f);
                total -= f.Size;
            }
            return doomed;
        }
    }
}
