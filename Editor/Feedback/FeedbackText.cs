using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>What <see cref="FeedbackText.Redact"/> needs to know about this PC: the Unity project folder, the Windows user name and the host name.</summary>
    internal sealed class RedactContext
    {
        public readonly string ProjectRoot;
        public readonly string UserName;
        public readonly string HostName;

        public RedactContext(string projectRoot, string userName, string hostName)
        {
            ProjectRoot = projectRoot ?? "";
            UserName = (userName ?? "").Trim();
            HostName = (hostName ?? "").Trim();
        }
    }

    /// <summary>
    /// The one place text is cleaned before it is shown and sent in a feedback message: the 문의하기 message, error code, Console
    /// attachment and package list and everything the install error report card shows or sends go through <see cref="Redact"/>,
    /// so what the person sees is what leaves the machine. No UnityEngine here, so the rules are checked headless.
    /// </summary>
    internal static class FeedbackText
    {
        public const string EmailToken = "<email>";
        public const string UserToken = "<user>";
        public const string HostToken = "<host>";
        public const string PathToken = "<path>";
        public const string SecretToken = "<token>";
        public const string ValueToken = "<redacted>";
        public const string SteamToken = "<steamid>";

        // Keys whose value is a secret (case-insensitive substring of the key name).
        private const string KeyWords = "token|secret|password|auth|session|cookie|key|sig";
        private const string KeyChars = @"[A-Za-z0-9_.-]*";
        // A path separator; JSON-escaped backslashes (C:\\Users\\x) count as one.
        private const string Sep = @"(?:\\+|/)";
        // A folder name: up to four words (Program Files, My Project 2024), never across a comma or semicolon.
        private const string Word = @"[^\\/:*?""<>|\s,;]+";
        private const string Segment = Word + "(?: " + Word + "){0,3}";
        private const string Last = @"[^\\/:*?""<>|\s,;]*";

        private const RegexOptions Fast = RegexOptions.Compiled | RegexOptions.CultureInvariant;

        private static readonly Regex Placeholder = new Regex("<(?:path|user|host|email|token|redacted|steamid)>", Fast);
        private static readonly Regex Email = new Regex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)*\.[A-Za-z]{2,}", Fast);
        private static readonly Regex Steam = new Regex(@"(?<!\d)7656119\d{10}(?!\d)", Fast);
        private static readonly Regex Jwt = new Regex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", Fast);
        private static readonly Regex Bearer = new Regex(@"\bBearer\s+(?<v>\S+)", Fast | RegexOptions.IgnoreCase);
        private static readonly Regex JsonSecret = new Regex(
            @"""" + @"(?<k>" + KeyChars + "(?:" + KeyWords + ")" + KeyChars + @")""\s*:\s*""(?<v>(?:[^""\\]|\\.)*)""", Fast | RegexOptions.IgnoreCase);
        private static readonly Regex PlainSecret = new Regex(
            @"(?<![A-Za-z0-9_.-])(?<k>" + KeyChars + "(?:" + KeyWords + ")" + KeyChars + @")[""']?\s*[=:]\s*(?:""(?<v>[^""\r\n]*)""|'(?<v>[^'\r\n]*)'|(?<v>[^\s""'&,;]+))",
            Fast | RegexOptions.IgnoreCase);
        private static readonly Regex TokenRun = new Regex(@"[A-Za-z0-9+/_-]{20,}={0,2}", Fast);
        private static readonly Regex FileUri = new Regex(@"file://[^\s""'<>|]*", Fast | RegexOptions.IgnoreCase);
        private static readonly Regex DrivePath = new Regex(@"(?<![A-Za-z0-9])[A-Za-z]:" + Sep + "(?:" + Segment + Sep + ")*" + Last, Fast);
        private static readonly Regex UncPath = new Regex(@"(?<![\w\\/:.])(?:\\{2,}|//)[^\\/\s:*?""<>|,;]+(?:" + Sep + "(?:" + Segment + Sep + ")*" + Last + ")?", Fast);
        private static readonly Regex PosixPath = new Regex(@"(?<![\w.:/\\<>~-])/(?:[^\\/:*?""<>|\s]+/)+" + Last, Fast);
        private static readonly Regex SlashRun = new Regex(@"\\+", Fast);

        private sealed class Span
        {
            public int Start, End, Priority;
            public string Replacement;
        }

        /// <summary>
        /// Every rule looks at the ORIGINAL text; the spans they find are merged (overlapping spans become one, the longest wins) and
        /// replaced once, so no rule runs on another rule's output and a second pass changes nothing. Rules, by priority:
        /// <c>key=value</c> / <c>"key": "value"</c> / <c>key: value</c> for secret-like keys and <c>Bearer x</c> (value becomes
        /// &lt;redacted&gt;); e-mail addresses (&lt;email&gt;); absolute paths (drive, UNC, //host, file:///, POSIX, either slash,
        /// JSON-escaped): below the project folder project-relative, elsewhere &lt;path&gt;/file; Steam ids; JWTs and runs of 20+
        /// base64/hex characters (&lt;token&gt;); the host name and Windows user name (3+ characters anywhere, 1-2 characters only
        /// as a whole token). Callers redact the whole text BEFORE any cut.
        /// </summary>
        public static string Redact(string text, RedactContext context)
        {
            return RedactCore(text, context ?? new RedactContext("", "", ""), true);
        }

        /// <summary>Redacts each value (the package list of a feedback message).</summary>
        public static string[] RedactEach(string[] values, RedactContext context)
        {
            var result = new string[values == null ? 0 : values.Length];
            for (var i = 0; i < result.Length; i++) result[i] = Redact(values[i], context);
            return result;
        }

        private static string RedactCore(string text, RedactContext ctx, bool paths)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var spans = new List<Span>();
            CollectSecrets(text, spans);
            CollectMatches(Email, text, 2, EmailToken, spans);
            if (paths) CollectPaths(text, ctx, spans);
            CollectMatches(Steam, text, 4, SteamToken, spans);
            CollectMatches(Jwt, text, 5, SecretToken, spans);
            CollectTokens(text, spans);
            CollectName(text, ctx.UserName, UserToken, spans);
            CollectName(text, ctx.HostName, HostToken, spans);
            if (spans.Count == 0) return text;

            // Text that is already a placeholder is never redacted again.
            var zones = Placeholder.Matches(text);
            spans.RemoveAll(s => Overlaps(s, zones));
            if (spans.Count == 0) return text;

            spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));
            var sb = new StringBuilder();
            var at = 0;
            var i = 0;
            while (i < spans.Count)
            {
                var best = spans[i];
                var start = best.Start;
                var end = best.End;
                var j = i + 1;
                for (; j < spans.Count && spans[j].Start < end; j++)
                {
                    var s = spans[j];
                    end = Math.Max(end, s.End);
                    var longer = s.End - s.Start > best.End - best.Start;
                    var same = s.End - s.Start == best.End - best.Start && s.Priority < best.Priority;
                    if (longer || same) best = s;
                }
                sb.Append(text, at, start - at).Append(best.Replacement);
                at = end;
                i = j;
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString();
        }

        private static bool Overlaps(Span span, MatchCollection zones)
        {
            foreach (Match zone in zones)
                if (span.Start < zone.Index + zone.Length && zone.Index < span.End) return true;
            return false;
        }

        private static bool StartsWithPlaceholder(string value)
        {
            var m = Placeholder.Match(value);
            return m.Success && m.Index == 0;
        }

        private static void CollectMatches(Regex regex, string text, int priority, string replacement, List<Span> spans)
        {
            foreach (Match m in regex.Matches(text))
                spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = priority, Replacement = replacement });
        }

        // ------------------------------------------------------------ secrets

        private static void CollectSecrets(string text, List<Span> spans)
        {
            foreach (Match m in JsonSecret.Matches(text)) AddValue(m.Groups["v"], spans);
            foreach (Match m in PlainSecret.Matches(text)) AddValue(m.Groups["v"], spans);
            foreach (Match m in Bearer.Matches(text))
            {
                if (StartsWithPlaceholder(m.Groups["v"].Value)) continue;
                spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = 1, Replacement = "Bearer " + ValueToken });
            }
        }

        private static void AddValue(Group value, List<Span> spans)
        {
            if (!value.Success || value.Length == 0 || StartsWithPlaceholder(value.Value)) return;
            spans.Add(new Span { Start = value.Index, End = value.Index + value.Length, Priority = 0, Replacement = ValueToken });
        }

        private static void CollectTokens(string text, List<Span> spans)
        {
            foreach (Match m in TokenRun.Matches(text))
            {
                var run = m.Value;
                if (IsHex(run) || (HasDigit(run) && (run.IndexOf('+') >= 0 || run.EndsWith("=", StringComparison.Ordinal))))
                {
                    spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = 6, Replacement = SecretToken });
                    continue;
                }
                // Without + or = a slash is more likely a path separator: judge each part on its own.
                var offset = m.Index;
                foreach (var part in run.Split('/'))
                {
                    if (part.Length >= 20 && (HasDigit(part) || IsHex(part)))
                        spans.Add(new Span { Start = offset, End = offset + part.Length, Priority = 6, Replacement = SecretToken });
                    offset += part.Length + 1;
                }
            }
        }

        private static bool HasDigit(string s)
        {
            foreach (var c in s) if (c >= '0' && c <= '9') return true;
            return false;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))) return false;
            return s.Length >= 20;
        }

        // ------------------------------------------------------------ names

        private static void CollectName(string text, string name, string replacement, List<Span> spans)
        {
            if (string.IsNullOrEmpty(name)) return;
            var escaped = Regex.Escape(name);
            // 3+ characters anywhere; 1-2 characters only as a whole token, so a short name does not eat every letter.
            var pattern = name.Length >= 3 ? escaped : @"(?<![A-Za-z0-9])" + escaped + @"(?![A-Za-z0-9])";
            foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = 7, Replacement = replacement });
            // The 8.3 short form of the name (ZMNBN~1, a long name's first six characters).
            var alnum = new StringBuilder();
            foreach (var c in name) if (char.IsLetterOrDigit(c) && alnum.Length < 6) alnum.Append(c);
            if (alnum.Length >= 3)
                foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z0-9])" + Regex.Escape(alnum.ToString()) + @"~\d+(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = 7, Replacement = replacement });
        }

        // ------------------------------------------------------------ paths

        private static void CollectPaths(string text, RedactContext ctx, List<Span> spans)
        {
            foreach (var regex in new[] { FileUri, DrivePath, UncPath, PosixPath })
                foreach (Match m in regex.Matches(text))
                {
                    var raw = m.Value;
                    if (regex == FileUri) raw = FromFileUri(raw);
                    spans.Add(new Span { Start = m.Index, End = m.Index + m.Length, Priority = 3, Replacement = PathReplacement(raw, ctx) });
                }
        }

        private static string FromFileUri(string uri)
        {
            var rest = uri.Substring("file://".Length);
            try { rest = Uri.UnescapeDataString(rest); }
            catch (Exception) { /* keep the raw text */ }
            // file:///C:/x has an empty host, then the drive; file://host/share is a UNC path.
            if (rest.StartsWith("/", StringComparison.Ordinal) && rest.Length > 2 && rest[2] == ':') rest = rest.Substring(1);
            else if (!rest.StartsWith("/", StringComparison.Ordinal)) rest = "//" + rest;
            return rest;
        }

        private static string Unslash(string path)
        {
            return SlashRun.Replace(path, "/");
        }

        private static string PathReplacement(string raw, RedactContext ctx)
        {
            var path = Unslash(raw);
            var root = Unslash(ctx.ProjectRoot).TrimEnd('/');
            if (root.Length > 0)
            {
                if (path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.Equals(root + "/", StringComparison.OrdinalIgnoreCase)) return ".";
                if (path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                    return RedactCore(path.Substring(root.Length + 1), ctx, false);
            }
            var slash = path.TrimEnd('/').LastIndexOf('/');
            var file = path.EndsWith("/", StringComparison.Ordinal) ? "" : path.Substring(slash + 1);
            return PathToken + "/" + RedactCore(file, ctx, false);
        }

        // ------------------------------------------------------------ cutting

        public static string CutToChars(string text, int maxChars)
        {
            return text.Length <= maxChars ? text : text.Substring(0, maxChars);
        }

        /// <summary>The longest start of <paramref name="text"/> that fits <paramref name="maxBytes"/> of UTF-8, never splitting a character.</summary>
        public static string CutToBytes(string text, int maxBytes)
        {
            text = text ?? "";
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
    }
}
