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

    /// <summary>One error line of a report with the code and signature it is deduplicated by.</summary>
    internal sealed class InstallErrorEntry
    {
        public bool IsCompile;
        public string Text = "";
        public string Code = "";
        public string Signature = "";
    }

    /// <summary>
    /// What the card showed when Send was pressed: the exact lines and the redacted excerpt that is sent. Taken once per revision of
    /// the report, so the preview, the request and "remove what was sent" all work on the same text.
    /// </summary>
    internal sealed class InstallErrorSnapshot
    {
        public string Product = "";
        public string ProductId = "";
        public string Version = "";
        public string ErrorCode = "";
        public string PackageLabel = "";
        public string Excerpt = "";
        public string Summary = "";
        public List<string> Compile = new List<string>();
        public List<string> Other = new List<string>();
        /// <summary>The signature of every error line in it (not only the headline one).</summary>
        public List<string> Signatures = new List<string>();
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
        /// <summary>One line saying what failed, used when there is no line below.</summary>
        public string Reason = "";
        /// <summary>Compiler error messages of the product's scripts, in the order they arrived.</summary>
        public List<string> Compile = new List<string>();
        /// <summary>Everything else: exceptions with their stack, asset import errors, the Manager's own failure text.</summary>
        public List<string> Other = new List<string>();
        /// <summary>Counts every change of the lines, so a cached preview knows when it is stale.</summary>
        public int Revision;

        public string ErrorCode { get { return Compile.Count > 0 ? InstallErrorCodes.CompileFailed : BaseCode; } }

        private string Id { get { return string.IsNullOrEmpty(ProductId) ? Product : ProductId; } }

        /// <summary>Every error line with its own code and signature, compile errors first.</summary>
        public List<InstallErrorEntry> Entries()
        {
            var list = new List<InstallErrorEntry>();
            foreach (var line in Compile) AddEntry(list, true, line);
            foreach (var line in Other) AddEntry(list, false, line);
            if (list.Count == 0) AddEntry(list, false, Reason);
            return list;
        }

        private void AddEntry(List<InstallErrorEntry> list, bool compile, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var code = compile ? InstallErrorCodes.CompileFailed : BaseCode;
            list.Add(new InstallErrorEntry { IsCompile = compile, Text = text, Code = code, Signature = InstallErrorLogic.Signature(code, Id, text) });
        }

        /// <summary>The first line of the first compile error, else the reason, else the first other line; redacted and short enough for one line.</summary>
        public string Headline
        {
            get
            {
                var source = Compile.Count > 0 ? Compile[0] : !string.IsNullOrEmpty(Reason) ? Reason : Other.Count > 0 ? Other[0] : "";
                var line = FeedbackText.Redact(InstallErrorLogic.FirstLine(source));
                return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
            }
        }

        /// <summary>The signature of the headline error (compile errors first).</summary>
        public string Signature
        {
            get
            {
                var source = Compile.Count > 0 ? Compile[0] : !string.IsNullOrEmpty(Reason) ? Reason : Other.Count > 0 ? Other[0] : "";
                return InstallErrorLogic.Signature(ErrorCode, Id, source);
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
            foreach (var entry in Entries())
            {
                var block = FeedbackText.Redact(entry.Text.Trim()) + "\n\n";
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

        /// <summary>Freezes what is shown now: the lines, the excerpt, the summary and every line's signature.</summary>
        public InstallErrorSnapshot TakeSnapshot(int maxExcerptBytes, int maxMessageChars)
        {
            var shot = new InstallErrorSnapshot
            {
                Product = Product, ProductId = Id, Version = Version, ErrorCode = ErrorCode, PackageLabel = PackageLabel,
                Excerpt = Excerpt(maxExcerptBytes), Summary = AutoSummary(maxMessageChars),
            };
            shot.Compile.AddRange(Compile);
            shot.Other.AddRange(Other);
            foreach (var entry in Entries()) if (!shot.Signatures.Contains(entry.Signature)) shot.Signatures.Add(entry.Signature);
            return shot;
        }

        /// <summary>Removes the lines a sent snapshot held; lines that joined after it stay. True when nothing is left.</summary>
        public bool RemoveSent(InstallErrorSnapshot shot)
        {
            var removed = Compile.RemoveAll(shot.Compile.Contains) + Other.RemoveAll(shot.Other.Contains);
            if (removed > 0) Revision++;
            return Compile.Count == 0 && Other.Count == 0;
        }

        /// <summary>A copy with only the given entries (the ones the ledger does not already know).</summary>
        public InstallErrorReport OnlyEntries(List<InstallErrorEntry> keep)
        {
            var copy = new InstallErrorReport { Product = Product, ProductId = ProductId, Version = Version, BaseCode = BaseCode, Reason = Reason };
            foreach (var entry in keep) (entry.IsCompile ? copy.Compile : copy.Other).Add(entry.Text);
            return copy;
        }

        /// <summary>Adds the lines of <paramref name="other"/> that this report does not have yet. True when something was added.</summary>
        public bool Merge(InstallErrorReport other)
        {
            if (other == null) return false;
            var added = AddNew(Compile, other.Compile) | AddNew(Other, other.Other);
            if (string.IsNullOrEmpty(Reason)) Reason = other.Reason;
            if (added) Revision++;
            return added;
        }

        public bool SameProduct(InstallErrorReport other)
        {
            return other != null
                && string.Equals(Product, other.Product, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Version ?? "", other.Version ?? "", StringComparison.OrdinalIgnoreCase);
        }

        private static bool AddNew(List<string> into, List<string> from)
        {
            var added = false;
            foreach (var line in from)
            {
                if (into.Count >= MaxLines || into.Contains(line)) continue;
                into.Add(line);
                added = true;
            }
            return added;
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
        /// The paths an import owns, so errors can be attributed to it. Every file of the package counts as itself; a folder counts
        /// only when the import created it (<paramref name="existedBefore"/> was false when the import started), and then it owns
        /// everything below. A folder that already existed (Assets/Editor, Assets/StudioRaming_Onairdeck) owns nothing, so a product
        /// that brings one file into it does not claim its neighbours.
        /// </summary>
        public static List<string> OwnedPaths(IEnumerable<string> assetPaths, Func<string, bool> existedBefore)
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
            var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in folders)
                if (!existedBefore(folder)) created.Add(folder);

            var owned = new List<string>();
            foreach (var folder in created)
                if (!HasAncestor(folder, created)) owned.Add(folder);
            foreach (var path in all)
                if (!folders.Contains(path) && !HasAncestor(path, created)) owned.Add(path);
            owned.Sort(StringComparer.OrdinalIgnoreCase);
            return owned;
        }

        private static bool HasAncestor(string path, HashSet<string> folders)
        {
            var slash = path.LastIndexOf('/');
            while (slash > 0)
            {
                if (folders.Contains(path.Substring(0, slash))) return true;
                slash = path.LastIndexOf('/', slash - 1);
            }
            return false;
        }

        /// <summary>True when <paramref name="path"/> (relative or absolute, any slash style) is one of the owned paths or below one, by whole segments.</summary>
        public static bool IsUnder(string path, IList<string> owned)
        {
            var p = Normalize(path);
            if (p.Length == 0 || owned == null) return false;
            foreach (var prefix in owned)
                if (!string.IsNullOrEmpty(prefix) && IndexOfPrefix(p, prefix, true) >= 0) return true;
            return false;
        }

        /// <summary>True when the text names a path below one of the owned paths (a console line, a stack trace, a compiler message).</summary>
        public static bool MentionsPath(string text, IList<string> owned)
        {
            if (string.IsNullOrEmpty(text) || owned == null) return false;
            var t = text.Replace('\\', '/');
            foreach (var prefix in owned)
                if (!string.IsNullOrEmpty(prefix) && IndexOfPrefix(t, prefix, false) >= 0) return true;
            return false;
        }

        // The prefix as whole path segments: not glued to a longer name before it, and followed by "/" (or the end; in running text
        // also by punctuation, but not by more of a name such as ".extra" or ".meta").
        private static int IndexOfPrefix(string text, string prefix, bool strict)
        {
            var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                var before = index == 0 ? '/' : text[index - 1];
                var end = index + prefix.Length;
                var after = end >= text.Length ? '/' : text[end];
                var leftOk = index == 0 || before == '/' || (!strict && !(char.IsLetterOrDigit(before) || before == '_' || before == '-' || before == '.'));
                bool rightOk;
                if (after == '/') rightOk = true;
                else if (strict) rightOk = false;
                else if (char.IsLetterOrDigit(after) || after == '_' || after == '-') rightOk = false;
                else if (after == '.') rightOk = end + 1 >= text.Length || !char.IsLetterOrDigit(text[end + 1]);
                else rightOk = true;
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
    /// What the watch of one product remembers across a domain reload. The watch runs from the import start until the first
    /// post-import script compile ends, plus <see cref="AfterCompileTicks"/>; later compiles do not extend it. More packages of the
    /// same product and version join the same watch (<see cref="AddJob"/>).
    /// </summary>
    [Serializable]
    internal sealed class InstallWatchState
    {
        public static readonly long AfterCompileTicks = TimeSpan.FromMinutes(2).Ticks;
        /// <summary>The longest a watch lives (an import window can stay open, a compile can be long).</summary>
        public static readonly long HardCapTicksSpan = TimeSpan.FromHours(1).Ticks;
        /// <summary>After the import finished, how long a watch waits for a compile that may never start (a package without scripts).</summary>
        public static readonly long NoCompileTicks = TimeSpan.FromMinutes(10).Ticks;

        public string Product = "";
        public string ProductId = "";
        public string Version = "";
        /// <summary>The owned paths of every job of this product (see <see cref="InstallErrorLogic.OwnedPaths"/>).</summary>
        public List<string> Prefixes = new List<string>();
        public List<string> Assemblies = new List<string>();
        public bool CompileStarted;
        public bool CompileDone;
        /// <summary>UTC ticks after which the watch is over (unless a compile it saw start is still running).</summary>
        public long DeadlineTicks;
        public long HardCapTicks;

        public static InstallWatchState Begin(string product, string productId, string version, IEnumerable<string> prefixes, long nowTicks)
        {
            var state = new InstallWatchState { Product = product ?? "", ProductId = productId ?? "", Version = version ?? "" };
            state.AddJob(prefixes, nowTicks);
            return state;
        }

        /// <summary>Another package of the same product: its paths join the watch and the wait for its compile starts over.</summary>
        public void AddJob(IEnumerable<string> prefixes, long nowTicks)
        {
            foreach (var prefix in prefixes)
                if (!Prefixes.Contains(prefix)) Prefixes.Add(prefix);
            CompileStarted = false;
            CompileDone = false;
            DeadlineTicks = nowTicks + HardCapTicksSpan;
            HardCapTicks = DeadlineTicks;
        }

        /// <summary>A cancelled or failed job takes only its own paths away. True when no path is left and the watch is over.</summary>
        public bool RemoveJob(IEnumerable<string> prefixes)
        {
            foreach (var prefix in prefixes) Prefixes.Remove(prefix);
            return Prefixes.Count == 0;
        }

        public bool IsActive(long nowTicks)
        {
            // A compile the watch saw start is waited for up to the hard cap, however long it takes.
            var limit = CompileStarted && !CompileDone ? HardCapTicks : Math.Min(DeadlineTicks, HardCapTicks);
            return nowTicks <= limit;
        }

        public void ImportFinished(long nowTicks)
        {
            if (!CompileStarted && !CompileDone) DeadlineTicks = Math.Min(DeadlineTicks, nowTicks + NoCompileTicks);
        }

        /// <summary>The first compile ended (with or without errors): the watch stays open two more minutes. A later compile changes nothing.</summary>
        public void CompileFinished(long nowTicks)
        {
            if (!CompileStarted || CompileDone) return;
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
