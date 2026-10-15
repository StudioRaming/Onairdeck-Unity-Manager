using System;
using System.IO;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The "signed in" marker read by the runtime license check that ships with sellers' assets:
    /// %LOCALAPPDATA%/OnAirDeck/session.json = { "user_id", "client", "issued_at" }.
    /// It carries no token (the session itself stays in EditorPrefs), so a copied marker proves
    /// nothing by itself: the check also needs an onairdeck.license signed for the same user_id.
    /// Written on sign-in and whenever the window loads a saved session; removed on sign-out
    /// and when the server rejects the session.
    /// </summary>
    internal static class SessionMarker
    {
        public const string FolderName = "OnAirDeck";
        public const string FileName = "session.json";

        public static string DefaultPath
        {
            get
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(Path.Combine(local, FolderName), FileName);
            }
        }

        public static void Write(string userId)
        {
            Write(DefaultPath, userId, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        public static void Clear()
        {
            Clear(DefaultPath);
        }

        internal static void Write(string path, string userId, long issuedAtUnix)
        {
            if (string.IsNullOrEmpty(userId))
            {
                Clear(path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var json = "{\"user_id\":\"" + Escape(userId) + "\",\"client\":\"unity-manager\",\"issued_at\":" + issuedAtUnix + "}\n";
            File.WriteAllText(path, json, new UTF8Encoding(false));
        }

        internal static void Clear(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>user_id stored in the marker, or null when it is absent or unreadable.</summary>
        internal static string ReadUserId(string path)
        {
            if (!File.Exists(path)) return null;
            string json;
            try { json = File.ReadAllText(path); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            const string key = "\"user_id\":\"";
            var at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            var start = at + key.Length;
            var end = json.IndexOf('"', start);
            return end > start ? json.Substring(start, end - start) : null;
        }

        private static string Escape(string value)
        {
            return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
