using System;
using System.IO;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The server's checksum of a plugin-download file, checked before anything is extracted or imported.
    /// plugin-download sends <c>sha256</c> with every file: the lower-case hex SHA-256 (64 characters) of the exact bytes its
    /// URL serves, or null when the store recorded none (files uploaded before the server recorded digests). A different
    /// digest, or a value that is not 64 hex digits, refuses the file; none at all is accepted during the transition and
    /// logged. Pure: no Unity API, so the headless checks run it as is.
    /// </summary>
    internal static class DownloadIntegrity
    {
        public enum Outcome { Verified, NoServerChecksum }

        /// <summary>True for exactly 64 hex digits (either case).</summary>
        public static bool IsSha256Hex(string value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var c in value) if (!Uri.IsHexDigit(c)) return false;
            return true;
        }

        /// <summary>
        /// Verified when the file's SHA-256 equals <paramref name="expectedSha256"/>; NoServerChecksum when the server sent
        /// none (null or empty). Throws <see cref="DownloadIntegrityException"/> on a mismatch or a malformed value.
        /// </summary>
        public static Outcome Check(string path, string expectedSha256, string label)
        {
            if (string.IsNullOrEmpty(expectedSha256)) return Outcome.NoServerChecksum;
            if (!IsSha256Hex(expectedSha256))
                throw new DownloadIntegrityException(label + ": the store sent an unreadable checksum; nothing was installed.");
            string actual;
            using (var stream = File.OpenRead(path)) actual = RemovalPlanner.StreamSha256(stream);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadIntegrityException(label + ": the downloaded file does not match the store's checksum; nothing was installed.");
            return Outcome.Verified;
        }
    }

    /// <summary>A downloaded file is not the one the store recorded (or its checksum is unreadable). Nothing was installed.</summary>
    internal sealed class DownloadIntegrityException : PackageInstallException
    {
        public DownloadIntegrityException(string message) : base(message) { }
    }
}
