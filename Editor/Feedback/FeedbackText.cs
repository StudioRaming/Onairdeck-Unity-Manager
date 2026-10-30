using System;
using System.Text;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The one place text is cleaned before it is shown and sent in a feedback message: the 문의하기 Console attachment and the
    /// install error report card both use <see cref="Redact(string)"/>, and so does the note, so what the person sees is what leaves the machine.
    /// No UnityEngine here, so the rules are checked headless.
    /// </summary>
    internal static class FeedbackText
    {
        public const string EmailToken = "<email>";
        public const string HomeToken = "%USERPROFILE%";
        public const string UserToken = "<user>";
        public const string SecretToken = "<token>";

        private static readonly Regex Email = new Regex(@"[\w.+-]+@[\w-]+\.[\w.-]+", RegexOptions.Compiled);

        private static readonly Regex Jwt = new Regex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]*", RegexOptions.Compiled);
        private static readonly Regex Bearer = new Regex(@"\bBearer\s+[A-Za-z0-9._~+/=-]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SecretKey = new Regex(@"\bsk-[A-Za-z0-9_-]{8,}", RegexOptions.Compiled);
        // token=..., key=..., secret=... and the same as a quoted JSON/query value; the name stays, the value goes.
        private static readonly Regex NamedSecret = new Regex(@"\b(token|secret|password|passwd|api[_-]?key|authorization|signature|key)([""']?\s*=\s*[""']?)[^\s""'&,;]{3,}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Opaque = new Regex(@"\b[A-Za-z0-9_-]{40,}\b", RegexOptions.Compiled);

        /// <summary>Redacts with the profile folder and user name of this PC.</summary>
        public static string Redact(string text)
        {
            string profile = null, user = null;
            try
            {
                profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                user = Environment.UserName;
            }
            catch (Exception)
            {
                // Redact what can be known.
            }
            return Redact(text, profile, user);
        }

        /// <summary>
        /// In this order: e-mail addresses become &lt;email&gt;; the profile folder (both slash styles, any case) becomes
        /// %USERPROFILE%; the user name as a path segment or a word of four or more characters becomes &lt;user&gt;; JWTs, Bearer
        /// values, sk- keys, token=/key=/secret= values and opaque strings of 40+ characters become &lt;token&gt;.
        /// Text without any of these is returned unchanged.
        /// </summary>
        internal static string Redact(string text, string userProfile, string userName)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            // E-mail addresses first: the user name may be part of one.
            text = Email.Replace(text, EmailToken);
            if (!string.IsNullOrEmpty(userProfile) && userProfile.Length >= 4)
            {
                text = ReplaceIgnoreCase(text, userProfile, HomeToken);
                text = ReplaceIgnoreCase(text, userProfile.Replace('\\', '/'), HomeToken);
            }
            if (!string.IsNullOrEmpty(userName))
            {
                var name = Regex.Escape(userName);
                var pattern = userName.Length >= 4
                    ? @"(?<![A-Za-z0-9_])" + name + @"(?![A-Za-z0-9_])"
                    : @"(?<=[\\/])" + name + @"(?=[\\/]|$|\s)";
                text = Regex.Replace(text, pattern, UserToken, RegexOptions.IgnoreCase);
            }
            text = Jwt.Replace(text, SecretToken);
            text = Bearer.Replace(text, "Bearer " + SecretToken);
            text = SecretKey.Replace(text, SecretToken);
            text = NamedSecret.Replace(text, m => m.Groups[1].Value + m.Groups[2].Value + SecretToken);
            text = Opaque.Replace(text, SecretToken);
            return text;
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
