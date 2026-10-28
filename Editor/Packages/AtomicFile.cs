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
                if (File.Exists(path)) File.Replace(temp, path, keepPrevious ? path + ".prev" : null);
                else File.Move(temp, path);
            }
            catch (Exception)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch (Exception) { }
                throw;
            }
        }
    }
}
