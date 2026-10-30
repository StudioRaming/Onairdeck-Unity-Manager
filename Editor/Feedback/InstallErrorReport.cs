using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OnAirDeck.UnityManager
{
    /// <summary>The error codes an install error report carries (FeedbackRequest.errorCode).</summary>
    internal static class InstallErrorCodes
    {
        /// <summary>A handled refusal or failure of the Manager's own install or import (download, extract, import, package install, recovery).</summary>
        public const string ManagerFailed = "install.manager_failed";
        /// <summary>A script of the imported product does not compile.</summary>
        public const string CompileFailed = "install.compile_failed";
        /// <summary>An unexpected exception, an asset import error or a runtime exception of the imported product.</summary>
        public const string Exception = "install.exception";
    }

    /// <summary>
    /// One thing that went wrong while installing or importing one product, as the report card shows and sends it. Several
    /// errors of one import collapse into one report (<see cref="Merge"/>). No UnityEngine here, so the rules are checked headless.
    /// </summary>
    [Serializable]
    internal sealed class InstallErrorReport
    {
        /// <summary>Most lines kept per list; a broken import can print hundreds.</summary>
        public const int MaxLines = 40;

        public string Product = "";
        /// <summary>The license id of the product (what the signature is keyed on); the product name when there is none.</summary>
        public string ProductId = "";
        public string Version = "";
        /// <summary>The code when there is no compile error (<see cref="InstallErrorCodes.ManagerFailed"/> or <see cref="InstallErrorCodes.Exception"/>).</summary>
        public string BaseCode = InstallErrorCodes.ManagerFailed;
        /// <summary>One line saying what failed, used when there is no compile error.</summary>
        public string Reason = "";
        /// <summary>Compiler error messages of the product's scripts, in the order they arrived.</summary>
        public List<string> Compile = new List<string>();
        /// <summary>Everything else: exceptions with their stack, asset import errors, the Manager's own failure text.</summary>
        public List<string> Other = new List<string>();

        public string ErrorCode { get { return Compile.Count > 0 ? InstallErrorCodes.CompileFailed : BaseCode; } }

        /// <summary>The first line of the first compile error, else the reason, else the first other line; redacted and short enough for one line.</summary>
        public string Headline
        {
            get
            {
                var source = Compile.Count > 0 ? Compile[0] : !string.IsNullOrEmpty(Reason) ? Reason : Other.Count > 0 ? Other[0] : "";
                var line = InstallErrorLogic.FirstLine(source);
                line = FeedbackText.Redact(line);
                return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
            }
        }

        public string Signature
        {
            get
            {
                var source = Compile.Count > 0 ? Compile[0] : !string.IsNullOrEmpty(Reason) ? Reason : Other.Count > 0 ? Other[0] : "";
                return InstallErrorLogic.Signature(ErrorCode, string.IsNullOrEmpty(ProductId) ? Product : ProductId, source);
            }
        }

        /// <summary>The "name@version" the feedback message names.</summary>
        public string PackageLabel { get { return (Product ?? "") + "@" + (string.IsNullOrEmpty(Version) ? "unknown" : Version); } }

        /// <summary>
        /// The text that is shown on the card and sent as consoleLog: compile errors first, then the rest, each entry redacted,
        /// whole entries only up to <paramref name="maxBytes"/> of UTF-8 (the first one is cut when it alone is too long).
        /// </summary>
        public string Excerpt(int maxBytes)
        {
            var sb = new StringBuilder();
            var used = 0;
            var first = true;
            foreach (var raw in AllLines())
            {
                var block = FeedbackText.Redact(raw.Trim()) + "\n\n";
                var size = Encoding.UTF8.GetByteCount(block);
                if (used + size > maxBytes)
                {
                    if (first) sb.Append(FeedbackText.CutToBytes(block, maxBytes));
                    break;
                }
                sb.Append(block);
                used += size;
                first = false;
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>The message sent when the person wrote no note: "product version: reason".</summary>
        public string AutoSummary(int maxChars)
        {
            var text = Product + (string.IsNullOrEmpty(Version) ? "" : " " + Version) + ": " + Headline;
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        /// <summary>Adds the lines of <paramref name="other"/> that this report does not have yet.</summary>
        public void Merge(InstallErrorReport other)
        {
            if (other == null) return;
            AddNew(Compile, other.Compile);
            AddNew(Other, other.Other);
            if (string.IsNullOrEmpty(Reason)) Reason = other.Reason;
        }

        public bool SameProduct(InstallErrorReport other)
        {
            return other != null
                && string.Equals(Product, other.Product, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Version ?? "", other.Version ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> AllLines()
        {
            foreach (var line in Compile) if (!string.IsNullOrWhiteSpace(line)) yield return line;
            foreach (var line in Other) if (!string.IsNullOrWhiteSpace(line)) yield return line;
            if (Compile.Count == 0 && Other.Count == 0 && !string.IsNullOrWhiteSpace(Reason)) yield return Reason;
        }

        private static void AddNew(List<string> into, List<string> from)
        {
            foreach (var line in from)
                if (into.Count < MaxLines && !into.Contains(line)) into.Add(line);
        }
    }

    /// <summary>Pure rules of the install error report: signature, path attribution, what is never a bug.</summary>
    internal static class InstallErrorLogic
    {
        private static readonly Regex Guid = new Regex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled);
        private static readonly Regex Hex = new Regex(@"\b(?:0x[0-9a-fA-F]+|[0-9a-fA-F]{32,})\b", RegexOptions.Compiled);
        private static readonly Regex PathLike = new Regex(@"(?:[A-Za-z]:)?[\\/]?(?:[^\s\\/:()'""<>|]+[\\/])+[^\s\\/:()'""<>|]*", RegexOptions.Compiled);
        private static readonly Regex Position = new Regex(@"\(\d+(?:\s*,\s*\d+)*\)", RegexOptions.Compiled);
        // A number that is not part of a word, so the compiler code CS1002 stays.
        private static readonly Regex Number = new Regex(@"(?<![A-Za-z_])\d+(?:\.\d+)*", RegexOptions.Compiled);
        private static readonly Regex Spaces = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex CompilerLine = new Regex(@":\s*error\s+CS\d+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Texts of the license check (OnAirDeck.License) and of the lock screens the shared gate template draws. These are
        // states of the license, never an install bug.
        private static readonly string[] LicenseGateMarkers =
        {
            "LicenseStatus", "LicenseCheckResult", "OnAirDeckLicense", "onairdeck.license", "license gate", "license re-check", "license request",
            "Sign in with Atelier Manager", "The license has expired", "No OnAirDeck license", "license belongs to another product",
            "different OnAirDeck account", "The license signature is not", "The license was not issued by", "license was issued in the future",
            "license file is not in the expected format", "license payload is incomplete", "license file could not be read",
        };

        /// <summary>The first non-empty line of <paramref name="text"/>, trimmed ("" for null).</summary>
        public static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }
            return "";
        }

        /// <summary>The first error line without what changes between runs: GUIDs, hex, paths, positions and numbers.</summary>
        public static string NormalizeFirstLine(string text)
        {
            var line = FirstLine(text);
            line = Guid.Replace(line, "");
            line = Hex.Replace(line, "");
            line = PathLike.Replace(line, "");
            line = Position.Replace(line, "");
            line = Number.Replace(line, "");
            return Spaces.Replace(line, " ").Trim();
        }

        /// <summary>A stable id (16 hex characters) of productId | errorCode | the first error line without digits and paths.</summary>
        public static string Signature(string errorCode, string productId, string errorText)
        {
            var key = (productId ?? "").Trim() + "|" + (errorCode ?? "").Trim() + "|" + NormalizeFirstLine(errorText);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder();
                for (var i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>True for a license-check result or a lock-screen line; these never become a report.</summary>
        public static bool IsLicenseGateText(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var marker in LicenseGateMarkers)
                if (text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>True for a compiler message line ("Assets/A.cs(3,5): error CS1002: ..."); the compile pipeline reports those itself.</summary>
        public static bool IsCompilerLine(string text)
        {
            return !string.IsNullOrEmpty(text) && CompilerLine.IsMatch(text);
        }

        /// <summary>
        /// Shrinks the asset paths a package brings to the folders and files that identify it. A path with something below it
        /// is a folder entry and is dropped; a file two or more folders deep stands for its folder, and a folder inside another
        /// listed folder is dropped. A file directly under Assets/ or Packages/ is kept as itself.
        /// </summary>
        public static List<string> ReducePrefixes(IEnumerable<string> assetPaths)
        {
            var all = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in assetPaths)
            {
                var path = Normalize(raw);
                if (path.Length > 0 && seen.Add(path)) all.Add(path);
            }
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in all)
            {
                var slash = path.LastIndexOf('/');
                while (slash > 0)
                {
                    folders.Add(path.Substring(0, slash));
                    slash = path.LastIndexOf('/', slash - 1);
                }
            }
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in all)
            {
                if (folders.Contains(path)) continue;                    // a folder entry
                var slash = path.LastIndexOf('/');
                kept.Add(slash > 0 && path.IndexOf('/') != slash ? path.Substring(0, slash) : path);
            }
            var result = new List<string>();
            foreach (var path in kept)
            {
                var covered = false;
                var slash = path.LastIndexOf('/');
                while (slash > 0 && !covered)
                {
                    covered = kept.Contains(path.Substring(0, slash));
                    slash = path.LastIndexOf('/', slash - 1);
                }
                if (!covered) result.Add(path);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
            return result;
        }

        /// <summary>True when <paramref name="path"/> (relative or absolute, any slash style) is one of the prefixes or below one.</summary>
        public static bool IsUnder(string path, IList<string> prefixes)
        {
            var p = Normalize(path);
            if (p.Length == 0) return false;
            foreach (var prefix in prefixes)
            {
                if (string.IsNullOrEmpty(prefix)) continue;
                var index = IndexOfPrefix(p, prefix, 0);
                if (index >= 0) return true;
            }
            return false;
        }

        /// <summary>True when the text names a path below one of the prefixes (a console line, a stack trace, a compiler message).</summary>
        public static bool MentionsPath(string text, IList<string> prefixes)
        {
            if (string.IsNullOrEmpty(text) || prefixes == null) return false;
            var t = text.Replace('\\', '/');
            foreach (var prefix in prefixes)
                if (!string.IsNullOrEmpty(prefix) && IndexOfPrefix(t, prefix, 0) >= 0) return true;
            return false;
        }

        // The prefix as a whole path: not glued to a longer name before it, and followed by "/" or the end of the name.
        private static int IndexOfPrefix(string text, string prefix, int from)
        {
            var index = text.IndexOf(prefix, from, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var before = index == 0 ? '/' : text[index - 1];
                var end = index + prefix.Length;
                var after = end >= text.Length ? '/' : text[end];
                var leftOk = !(char.IsLetterOrDigit(before) || before == '_' || before == '-' || before == '.');
                var rightOk = after == '/' || !(char.IsLetterOrDigit(after) || after == '_' || after == '-');
                if (leftOk && rightOk) return index;
                index = text.IndexOf(prefix, index + 1, StringComparison.OrdinalIgnoreCase);
            }
            return -1;
        }

        private static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? "" : path.Trim().Replace('\\', '/').TrimEnd('/');
        }
    }

    /// <summary>
    /// What the watch of one import remembers across a domain reload. The watch runs from the import start until the
    /// post-import script compile ends, plus <see cref="AfterCompileTicks"/>.
    /// </summary>
    [Serializable]
    internal sealed class InstallWatchState
    {
        public static readonly long AfterCompileTicks = TimeSpan.FromMinutes(2).Ticks;
        /// <summary>The longest a watch waits for an import to finish (the import window can stay open).</summary>
        public static readonly long ImportCapTicks = TimeSpan.FromHours(1).Ticks;
        /// <summary>After the import finished, how long a watch waits for a compile that may never start (a package without scripts).</summary>
        public static readonly long NoCompileTicks = TimeSpan.FromMinutes(10).Ticks;

        public string Product = "";
        public string ProductId = "";
        public string Version = "";
        public List<string> Prefixes = new List<string>();
        public List<string> Assemblies = new List<string>();
        public bool CompileStarted;
        public bool CompileDone;
        /// <summary>UTC ticks after which the watch is over.</summary>
        public long DeadlineTicks;

        public static InstallWatchState Begin(string product, string productId, string version, IEnumerable<string> prefixes, long nowTicks)
        {
            var state = new InstallWatchState { Product = product ?? "", ProductId = productId ?? "", Version = version ?? "", DeadlineTicks = nowTicks + ImportCapTicks };
            state.Prefixes.AddRange(prefixes);
            return state;
        }

        public bool IsActive(long nowTicks)
        {
            return nowTicks <= DeadlineTicks;
        }

        public void ImportFinished(long nowTicks)
        {
            if (!CompileDone) DeadlineTicks = Math.Min(DeadlineTicks, nowTicks + NoCompileTicks);
        }

        /// <summary>The script compile ended (with or without errors): the watch stays open two more minutes.</summary>
        public void CompileFinished(long nowTicks)
        {
            if (!CompileStarted) return;
            CompileDone = true;
            DeadlineTicks = nowTicks + AfterCompileTicks;
        }
    }

    /// <summary>
    /// The signatures of reports that were sent or dismissed in this project, one line each ("signature state utc"), in a
    /// text file under Library/. A signature in it is not offered again.
    /// </summary>
    internal sealed class InstallErrorLedger
    {
        private readonly string _file;
        private Dictionary<string, string> _known;

        public const string Sent = "sent";
        public const string Dismissed = "dismissed";

        public InstallErrorLedger(string file)
        {
            _file = file;
        }

        public bool Contains(string signature)
        {
            Load();
            return _known.ContainsKey(signature);
        }

        /// <summary>"sent", "dismissed" or null; a later line wins, so a dismissed report that is sent afterwards is "sent".</summary>
        public string State(string signature)
        {
            Load();
            string state;
            return _known.TryGetValue(signature, out state) ? state : null;
        }

        public void Add(string signature, string state)
        {
            Load();
            string known;
            if (_known.TryGetValue(signature, out known) && (known == state || known == Sent)) return;
            _known[signature] = state;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file));
                File.AppendAllText(_file, signature + " " + state + " " + DateTime.UtcNow.ToString("o") + "\n", Encoding.UTF8);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void Load()
        {
            if (_known != null) return;
            _known = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(_file)) return;
                foreach (var line in File.ReadAllLines(_file))
                {
                    var parts = line.Split(' ');
                    if (parts.Length >= 2 && parts[0].Length > 0) _known[parts[0]] = parts[1];
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
