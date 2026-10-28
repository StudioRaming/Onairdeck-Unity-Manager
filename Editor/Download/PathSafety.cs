using System;
using System.IO;
using System.Runtime.InteropServices;
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
            // Windows and macOS volumes are case-insensitive by default, so a differently-cased
            // spelling names the same folder there. Linux filesystems are case-sensitive, where
            // "Root" and "root" are different folders and must not share ownership.
            var caseInsensitive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
            var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return candidateFull.StartsWith(rootFull, comparison);
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

        /// <summary>
        /// A safe folder name that always ends with <paramref name="suffix"/>: the name part is cut so that name + suffix
        /// stays within the length limit (a plain cut after appending would drop the suffix of a long name).
        /// </summary>
        public static string SafeFolderName(string name, string fallback, string suffix)
        {
            suffix = suffix ?? "";
            var normalized = (name ?? "").Replace('\\', '/');
            var slash = normalized.LastIndexOf('/');
            if (slash >= 0) normalized = normalized.Substring(slash + 1);
            var head = CleanSegment(normalized, Math.Max(1, MaxNameLength - suffix.Length));
            if (head.Length == 0) head = fallback;
            return head + suffix;
        }

        private static readonly string[] ReservedDeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>Windows device names (CON, NUL, COM1...), alone or with an extension ("nul.txt"), any case.</summary>
        internal static bool IsReservedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            var dot = name.IndexOf('.');
            var stem = (dot >= 0 ? name.Substring(0, dot) : name).TrimEnd(' ');
            foreach (var reserved in ReservedDeviceNames)
                if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string CleanSegment(string segment)
        {
            return CleanSegment(segment, MaxNameLength);
        }

        private static string CleanSegment(string segment, int maxLength)
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
            if (IsReservedName(result)) result = "_" + result;           // CON, NUL, COM1... cannot be created on Windows
            if (result.Length > maxLength) result = result.Substring(0, maxLength).TrimEnd('.', ' ');
            return result;
        }
    }
}
