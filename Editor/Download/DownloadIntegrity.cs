using System;
using System.IO;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The server's checksum of a plugin-download file, checked before anything is extracted or imported.
    /// plugin-download sends <c>sha256</c> with every file: the lower-case hex SHA-256 (64 characters) of the exact bytes its
    /// URL serves, or null when the store recorded none; and <c>sha256_required</c>: true when the store promises a digest
    /// for this file. A different digest, a value that is not 64 hex digits, or no digest when one is required refuses the
    /// file. No digest and not required (a file the store has none for yet, or an older server that sends neither field)
    /// is accepted and logged. Pure: no Unity API, so the headless checks run it as is.
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
        /// none (null or empty) and does not require one. Throws <see cref="DownloadIntegrityException"/> on a mismatch, a
        /// malformed value, or a missing value when <paramref name="required"/>.
        /// </summary>
        public static Outcome Check(string path, string expectedSha256, bool required, string label)
        {
            if (string.IsNullOrEmpty(expectedSha256))
            {
                if (required)
                    throw new DownloadIntegrityException(label + ": the store requires a checksum for this file and sent none; nothing was installed.");
                return Outcome.NoServerChecksum;
            }
            if (!IsSha256Hex(expectedSha256))
                throw new DownloadIntegrityException(label + ": the store sent an unreadable checksum; nothing was installed.");
            string actual;
            using (var stream = File.OpenRead(path)) actual = RemovalPlanner.StreamSha256(stream);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new DownloadIntegrityException(label + ": the downloaded file does not match the store's checksum; nothing was installed.");
            return Outcome.Verified;
        }

        /// <summary>
        /// <see cref="Check"/> on a finished download: a refusal runs <paramref name="deleteDownload"/> and goes on to the
        /// caller, so nothing is extracted or imported; an accepted file without a checksum goes to <paramref name="warn"/>.
        /// </summary>
        public static void CheckDownload(string path, string expectedSha256, bool required, string label, Action deleteDownload, Action<string> warn)
        {
            try
            {
                if (Check(path, expectedSha256, required, label) == Outcome.NoServerChecksum)
                    warn("The store sent no checksum for " + label + "; installed without one.");
            }
            catch
            {
                deleteDownload();
                throw;
            }
        }
    }

    /// <summary>A downloaded file is not the one the store recorded (or its checksum is unreadable or missing though required). Nothing was installed.</summary>
    internal sealed class DownloadIntegrityException : PackageInstallException
    {
        public DownloadIntegrityException(string message) : base(message) { }
    }
}
