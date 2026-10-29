using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Reads a .unitypackage without Unity: a tar.gz where &lt;guid&gt;/pathname holds the asset path and &lt;guid&gt;/asset the
    /// content. No UnityEngine, headless-checked.
    /// </summary>
    internal static class UnityPackageArchive
    {
        private const int MaxPathNameBytes = 4096;

        /// <summary>
        /// Walks the regular-file entries in archive order. <paramref name="want"/> gets the guid, the part after it ("pathname",
        /// "asset", "asset.meta", ...) and the size; it must bound the size of what it accepts. Accepted entries are read whole and
        /// passed to <paramref name="read"/>, the rest are skipped. False for a bad size field or a short entry.
        /// </summary>
        public static bool Walk(string unityPackagePath, Func<string, string, long, bool> want, Action<string, string, byte[]> read)
        {
            using (var file = File.OpenRead(unityPackagePath))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadFully(gz, header, 512) == 512)
                {
                    var allZero = true;
                    for (var i = 0; i < 512; i++) if (header[i] != 0) { allZero = false; break; }
                    if (allZero) break;
                    var name = Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0').Replace('\\', '/');
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    long size = 0;
                    foreach (var ch in Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' '))
                    {
                        if (ch < '0' || ch > '7') { size = -1; break; }
                        size = size * 8 + (ch - '0');
                    }
                    if (size < 0) return false;
                    var typeFlag = (char)header[156];
                    var padded = (size + 511) / 512 * 512;
                    var slash = name.IndexOf('/');
                    var guid = slash < 0 ? "" : name.Substring(0, slash);
                    var leaf = slash < 0 ? "" : name.Substring(slash + 1);
                    if ((typeFlag == '0' || typeFlag == '\0') && want(guid, leaf, size))
                    {
                        var data = new byte[padded];
                        if (ReadFully(gz, data, (int)padded) != padded) return false;
                        var content = new byte[size];
                        Array.Copy(data, content, (int)size);
                        read(guid, leaf, content);
                    }
                    else
                    {
                        Skip(gz, padded);
                    }
                }
            }
            return true;
        }

        /// <summary>The asset path of every entry (forward slashes), in archive order; null for a malformed archive.</summary>
        public static List<string> AssetPaths(string unityPackagePath)
        {
            var paths = new List<string>();
            var ok = Walk(unityPackagePath, (guid, leaf, size) => leaf == "pathname" && size <= MaxPathNameBytes,
                (guid, leaf, content) => paths.Add(PathNameOf(content)));
            return ok ? paths : null;
        }

        /// <summary>The asset path a &lt;guid&gt;/pathname entry holds: its first line, trimmed, with forward slashes.</summary>
        public static string PathNameOf(byte[] content)
        {
            return Encoding.UTF8.GetString(content).Split('\n')[0].Trim().Replace('\\', '/');
        }

        /// <summary>
        /// Asset path to last write time (UTC) of the given package paths that already exist as files under
        /// <paramref name="projectRoot"/>. Only clean relative Assets/ and Packages/ paths count.
        /// </summary>
        public static Dictionary<string, DateTime> ExistingFileTimes(string projectRoot, IEnumerable<string> assetPaths)
        {
            var times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in assetPaths)
            {
                var path = PathSafety.SafeRelativePath(raw);
                if (path == null || !(path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))) continue;
                var full = Path.Combine(projectRoot, path);
                if (File.Exists(full)) times[path] = File.GetLastWriteTimeUtc(full);
            }
            return times;
        }

        /// <summary>The paths of <paramref name="before"/> that still exist with the recorded write time.</summary>
        public static List<string> UnchangedSince(string projectRoot, IDictionary<string, DateTime> before)
        {
            var same = new List<string>();
            foreach (var pair in before)
            {
                var full = Path.Combine(projectRoot, pair.Key);
                if (File.Exists(full) && File.GetLastWriteTimeUtc(full) == pair.Value) same.Add(pair.Key);
            }
            return same;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[16384];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException();
                count -= read;
            }
        }
    }
}
