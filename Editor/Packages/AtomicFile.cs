using System;
using System.IO;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Writes a small state file so that a failure or a crash never leaves it half written: the text goes to a temp file next to it,
    /// is flushed, and replaces the target in one step. With <c>keepPrevious</c> the file it replaces stays as "&lt;path&gt;.prev"
    /// (the last good copy). A failure throws; the caller decides whether it may go on.
    /// </summary>
    internal static class AtomicFile
    {
        /// <summary>Test seam: called with "write:&lt;path&gt;" before anything is written; may throw to fail the write.</summary>
        internal static Action<string> FaultHook;

        public static void Write(string path, string text, bool keepPrevious = false)
        {
            var hook = FaultHook;
            if (hook != null) hook("write:" + path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(text);
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) Replace(temp, path, keepPrevious ? path + ".prev" : null);
                else File.Move(temp, path);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// File.Replace, tried again for up to about a second while Windows refuses it because another process (a virus scanner,
        /// the search indexer) still has the target open for a moment. Two installs in a row rewrite the same journal within
        /// milliseconds (seen in Unity 2022.3: "unable to remove the file to be replaced").
        /// </summary>
        private static void Replace(string source, string target, string backup)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Replace(source, target, backup);
                    return;
                }
                catch (IOException)
                {
                    if (attempt >= 9 || !File.Exists(source)) throw;
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }
}
