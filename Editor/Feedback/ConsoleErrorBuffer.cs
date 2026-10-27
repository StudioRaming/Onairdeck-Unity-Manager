using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The last <see cref="Capacity"/> Error / Exception / Assert messages of this Editor session, so a feedback message
    /// can carry them. Captured from <see cref="Application.logMessageReceivedThreaded"/> (any thread) into a ring buffer;
    /// a script reload starts it empty. Each entry keeps its message, the first <see cref="MaxStackLines"/> stack lines and
    /// the time. Nothing leaves the machine unless the user sends feedback with "Attach recent Console errors" ticked.
    /// </summary>
    [InitializeOnLoad]
    internal static class ConsoleErrorBuffer
    {
        public const int Capacity = 50;
        public const int MaxStackLines = 20;
        private const int MaxConditionChars = 2000;

        internal struct Entry
        {
            public DateTime Time;
            public LogType Type;
            public string Condition;
            public string Stack;
        }

        private static readonly object Gate = new object();
        private static readonly Entry[] Ring = new Entry[Capacity];
        private static int _next;
        private static int _count;

        static ConsoleErrorBuffer()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        /// <summary>Entries held now (at most <see cref="Capacity"/>).</summary>
        public static int Count
        {
            get { lock (Gate) return _count; }
        }

        /// <summary>Copy of the held entries, oldest first.</summary>
        public static Entry[] Snapshot()
        {
            lock (Gate)
            {
                var result = new Entry[_count];
                var start = (_next - _count + Capacity) % Capacity;
                for (var i = 0; i < _count; i++) result[i] = Ring[(start + i) % Capacity];
                return result;
            }
        }

        public static void Clear()
        {
            lock (Gate)
            {
                Array.Clear(Ring, 0, Ring.Length);
                _next = 0;
                _count = 0;
            }
        }

        /// <summary>
        /// The entries as plain text, oldest first, at most <paramref name="maxBytes"/> of UTF-8. When they do not all fit,
        /// the newest are kept. The user's home folder in paths is replaced with "~".
        /// </summary>
        public static string Format(int maxBytes)
        {
            var entries = Snapshot();
            var home = HomeFolder();
            var blocks = new List<string>();
            var used = 0;
            for (var i = entries.Length - 1; i >= 0; i--)
            {
                var block = FormatEntry(entries[i], home);
                var size = Encoding.UTF8.GetByteCount(block);
                if (used + size > maxBytes)
                {
                    // The newest entry alone is too long: keep its start rather than nothing.
                    if (blocks.Count == 0) blocks.Add(CutToBytes(block, maxBytes));
                    break;
                }
                blocks.Add(block);
                used += size;
            }
            blocks.Reverse();
            return string.Concat(blocks.ToArray());
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert) return;
            // Never log from here: this runs inside Unity's log callback.
            var entry = new Entry
            {
                Time = DateTime.Now,
                Type = type,
                Condition = Truncate(condition ?? "", MaxConditionChars),
                Stack = FirstLines(stackTrace ?? "", MaxStackLines),
            };
            lock (Gate)
            {
                Ring[_next] = entry;
                _next = (_next + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        private static string FormatEntry(Entry entry, string home)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(entry.Time.ToString("yyyy-MM-dd HH:mm:ss")).Append("] ").Append(entry.Type).Append(": ");
            sb.Append(Redact(entry.Condition, home).TrimEnd()).Append('\n');
            if (entry.Stack.Length > 0)
            {
                foreach (var line in entry.Stack.Split('\n'))
                    if (line.Trim().Length > 0) sb.Append("    ").Append(Redact(line.TrimEnd('\r'), home)).Append('\n');
            }
            sb.Append('\n');
            return sb.ToString();
        }

        internal static string FirstLines(string text, int maxLines)
        {
            if (text.Length == 0) return text;
            var index = -1;
            for (var line = 0; line < maxLines; line++)
            {
                index = text.IndexOf('\n', index + 1);
                if (index < 0) return text.TrimEnd();
            }
            return text.Substring(0, index).TrimEnd();
        }

        private static string Truncate(string text, int maxChars)
        {
            return text.Length <= maxChars ? text : text.Substring(0, maxChars) + "…";
        }

        internal static string CutToBytes(string text, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(text) <= maxBytes) return text;
            // Walk characters (a surrogate pair is one 4-byte character) so the cut never splits one.
            var bytes = 0;
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                int size, step = 1;
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { size = 4; step = 2; }
                else if (c < 0x80) size = 1;
                else if (c < 0x800) size = 2;
                else size = 3;
                if (bytes + size > maxBytes) break;
                bytes += size;
                i += step;
            }
            return text.Substring(0, i);
        }

        private static string HomeFolder()
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrEmpty(home) || home.Length < 4 ? null : home;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Redact(string text, string home)
        {
            if (home == null || text.Length == 0) return text;
            text = ReplaceIgnoreCase(text, home, "~");
            return ReplaceIgnoreCase(text, home.Replace('\\', '/'), "~");
        }

        private static string ReplaceIgnoreCase(string text, string find, string replacement)
        {
            var index = text.IndexOf(find, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return text;
            var sb = new StringBuilder();
            var from = 0;
            while (index >= 0)
            {
                sb.Append(text, from, index - from).Append(replacement);
                from = index + find.Length;
                index = text.IndexOf(find, from, StringComparison.OrdinalIgnoreCase);
            }
            sb.Append(text, from, text.Length - from);
            return sb.ToString();
        }
    }
}
