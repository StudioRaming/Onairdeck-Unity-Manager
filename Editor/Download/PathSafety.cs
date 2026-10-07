using System;
using System.IO;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Every file name, product name and zip entry comes from the server (seller-authored,
    /// not constrained by the database), so none of them may be used on disk unchecked.
    /// </summary>
    internal static class PathSafety
    {
        private const int MaxNameLength = 150;

        /// <summary>
        /// Reduces a server-supplied file name to a single safe path segment, or "" if nothing
        /// usable remains (traversal, drive letters, reserved characters are all dropped).
        /// </summary>
        public static string SafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            var normalized = name.Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            if (slash >= 0) normalized = normalized.Substring(slash + 1);
            return CleanSegment(normalized);
        }

        /// <summary>A safe folder name for a product, with a fallback when nothing usable remains.</summary>
        public static string SafeFolderName(string name, string fallback)
        {
            var clean = SafeFileName(name);
            return clean.Length == 0 ? fallback : clean;
        }

        /// <summary>True when <paramref name="candidate"/> resolves to a path inside <paramref name="root"/>.</summary>
        public static bool IsInside(string root, string candidate)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidateFull = Path.GetFullPath(candidate);
            return candidateFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Validates one zip entry path: relative, no "..", no drive or rooted segments. Returns
        /// the cleaned relative path with '/' separators, or null if the entry must be skipped.
        /// </summary>
        public static string SafeRelativePath(string entryPath)
        {
            if (string.IsNullOrEmpty(entryPath)) return null;
            if (entryPath[0] == '/' || entryPath[0] == '\\') return null;
            var parts = entryPath.Replace('\\', '/').Split('/');
            var sb = new StringBuilder();
            foreach (var raw in parts)
            {
                if (raw.Length == 0 || raw == ".") continue;          // "a//b", "./a"
                var segment = CleanSegment(raw);
                if (segment.Length == 0) return null;                   // "..", "C:", unusable
                if (sb.Length > 0) sb.Append('/');
                sb.Append(segment);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private static string CleanSegment(string segment)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0 || trimmed == "." || trimmed == "..") return "";
            if (trimmed.IndexOf(':') >= 0) return "";                   // drive letter / NTFS stream
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(trimmed.Length);
            foreach (var c in trimmed)
            {
                sb.Append(Array.IndexOf(invalid, c) >= 0 || c < ' ' ? '_' : c);
            }
            var result = sb.ToString().TrimEnd('.', ' ');               // Windows rejects trailing dots/spaces
            if (result.Length > MaxNameLength) result = result.Substring(0, MaxNameLength);
            return result;
        }
    }
}
