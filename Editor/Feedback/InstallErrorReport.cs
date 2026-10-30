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

    /// <summary>One error of a report with the code, product id and signature it is deduplicated by; kept as it is through merge, filter and snapshot.</summary>
    [Serializable]
    internal sealed class InstallErrorEntry
    {
        public bool IsCompile;
        public string Text = "";
        public string Code = "";
        public string ProductId = "";
        public string Signature = "";

        public InstallErrorEntry Clone()
        {
            return new InstallErrorEntry { IsCompile = IsCompile, Text = Text, Code = Code, ProductId = ProductId, Signature = Signature };
        }
    }

    /// <summary>What <see cref="InstallErrorReport.BuildExcerpt"/> produced: the redacted text and the entries that are actually in it.</summary>
    internal sealed class InstallErrorExcerpt
    {
        public string Text = "";
        public List<InstallErrorEntry> Included = new List<InstallErrorEntry>();
    }

    /// <summary>
    /// What the card showed when Send was pressed: the redacted excerpt that is sent and the entries it holds. Taken once per revision
    /// of one card (<see cref="BelongsTo"/>), so the preview, the request and "mark what was sent" all work on the same text.
    /// </summary>
    internal sealed class InstallErrorSnapshot
    {
        public string CardId = "";
        public int Revision;
        /// <summary>Product name for the card title, already redacted.</summary>
        public string ProductShown = "";
        /// <summary>The first error line, already redacted.</summary>
        public string Headline = "";
        public string ErrorCode = "";
        /// <summary>"name@version"; <see cref="FeedbackClient.Build"/> redacts it.</summary>
        public string PackageLabel = "";
        public string Excerpt = "";
        public string Summary = "";
        /// <summary>Only the entries that are in <see cref="Excerpt"/>: these are recorded sent and leave the card.</summary>
        public List<InstallErrorEntry> Included = new List<InstallErrorEntry>();

        public bool BelongsTo(InstallErrorReport card)
        {
            return card != null && card.CardId == CardId && card.Revision == Revision;
        }
    }

    /// <summary>
    /// One thing that went wrong while installing or importing one product, as the report card shows and sends it. Several
    /// errors of one import collapse into one report (<see cref="Merge"/>). No UnityEngine here, so the rules are checked headless.
    /// </summary>
    [Serializable]
    internal sealed class InstallErrorReport
    {
        /// <summary>Most entries kept per kind (compile, other); a broken import can print hundreds.</summary>
        public const int MaxLines = 40;

        /// <summary>Tells this card from every other one, also of the same product and version.</summary>
        public string CardId = Guid.NewGuid().ToString("N");
        public string Product = "";
        /// <summary>The license id of the product (what the signature is keyed on); the product name when there is none.</summary>
        public string ProductId = "";
        public string Version = "";
        /// <summary>The code of an entry added with <see cref="AddOther"/> (<see cref="InstallErrorCodes.ManagerFailed"/> or <see cref="InstallErrorCodes.Exception"/>).</summary>
        public string BaseCode = InstallErrorCodes.ManagerFailed;
        /// <summary>One line saying what failed, used for the headline when there is no compile error.</summary>
        public string Reason = "";
        /// <summary>Every error with its own code, product id and signature, in the order it arrived.</summary>
        public List<InstallErrorEntry> Items = new List<InstallErrorEntry>();
        /// <summary>Counts every change of the entries, so a cached preview knows when it is stale.</summary>
        public int Revision;

        public int Count { get { return Items.Count; } }

        /// <summary>Compile when the card has a compile error, else the code of its first entry.</summary>
        public string ErrorCode
        {
            get
            {
                foreach (var entry in Items) if (entry.IsCompile) return InstallErrorCodes.CompileFailed;
                return Items.Count > 0 ? Items[0].Code : BaseCode;
            }
        }

        private string KeyId { get { return string.IsNullOrEmpty(ProductId) ? Product : ProductId; } }

        /// <summary>A compiler error message of the product's scripts. False when it is already there or the kind is full.</summary>
        public bool AddCompile(string text)
        {
            return Add(true, InstallErrorCodes.CompileFailed, KeyId, text);
        }

        /// <summary>Anything else: an exception with its stack, an asset import error, the Manager's own failure text.</summary>
        public bool AddOther(string text)
        {
            return Add(false, BaseCode, KeyId, text);
        }

        private bool Add(bool compile, string code, string productId, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var same = 0;
            foreach (var item in Items)
            {
                if (item.IsCompile != compile) continue;
                if (item.Text == text) return false;
                same++;
            }
            if (same >= MaxLines) return false;
            Items.Add(new InstallErrorEntry { IsCompile = compile, Text = text, Code = code, ProductId = productId, Signature = InstallErrorLogic.Signature(code, productId, text) });
            Revision++;
            return true;
        }

        /// <summary>Every entry, compile errors first.</summary>
        public List<InstallErrorEntry> Entries()
        {
            var list = new List<InstallErrorEntry>();
            foreach (var entry in Items) if (entry.IsCompile) list.Add(entry);
            foreach (var entry in Items) if (!entry.IsCompile) list.Add(entry);
            return list;
        }

        /// <summary>The first line of the first compile error, else the reason, else the first other entry; redacted and short enough for one line.</summary>
        public string Headline(RedactContext context)
        {
            var source = "";
            foreach (var entry in Items) if (entry.IsCompile) { source = entry.Text; break; }
            if (source.Length == 0) source = !string.IsNullOrEmpty(Reason) ? Reason : Items.Count > 0 ? Items[0].Text : "";
            var line = FeedbackText.Redact(InstallErrorLogic.FirstLine(source), context);
            return line.Length > 200 ? line.Substring(0, 200) + "..." : line;
        }

        /// <summary>The "name@version" the feedback message names.</summary>
        public string PackageLabel { get { return (Product ?? "") + "@" + (string.IsNullOrEmpty(Version) ? "unknown" : Version); } }

        /// <summary>
        /// The text that is shown on the card and sent as consoleLog: compile errors first, then the rest. Each entry is redacted
        /// whole, then whole entries are added up to <paramref name="maxBytes"/> of UTF-8 (the first one is cut when it alone is too
        /// long). <see cref="InstallErrorExcerpt.Included"/> is exactly what made it in.
        /// </summary>
        public InstallErrorExcerpt BuildExcerpt(int maxBytes, RedactContext context)
        {
            var result = new InstallErrorExcerpt();
            var sb = new StringBuilder();
            var used = 0;
            foreach (var entry in Entries())
            {
                var block = FeedbackText.Redact(entry.Text.Trim(), context) + "\n\n";
                var size = Encoding.UTF8.GetByteCount(block);
                if (used + size > maxBytes)
                {
                    if (result.Included.Count == 0)
                    {
                        sb.Append(FeedbackText.CutToBytes(block, maxBytes));
                        result.Included.Add(entry);
                    }
                    break;
                }
                sb.Append(block);
                used += size;
                result.Included.Add(entry);
            }
            result.Text = sb.ToString().TrimEnd();
            return result;
        }

        /// <summary>The message sent when the person wrote no note: "product version: reason".</summary>
        public string AutoSummary(int maxChars, RedactContext context)
        {
            var text = FeedbackText.Redact(Product, context) + (string.IsNullOrEmpty(Version) ? "" : " " + Version) + ": " + Headline(context);
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }

        /// <summary>Freezes what is shown now: the excerpt, the entries in it, the summary.</summary>
        public InstallErrorSnapshot TakeSnapshot(int maxExcerptBytes, int maxMessageChars, RedactContext context)
        {
            var excerpt = BuildExcerpt(maxExcerptBytes, context);
            var shot = new InstallErrorSnapshot
            {
                CardId = CardId, Revision = Revision,
                ProductShown = FeedbackText.Redact(Product, context), Headline = Headline(context),
                ErrorCode = ErrorCode, PackageLabel = PackageLabel,
                Excerpt = excerpt.Text, Summary = AutoSummary(maxMessageChars, context),
            };
            foreach (var entry in excerpt.Included) shot.Included.Add(entry.Clone());
            return shot;
        }

        /// <summary>Removes every entry whose signature <paramref name="isSent"/> accepts (the same error with other digits goes too). True when nothing is left.</summary>
        public bool RemoveSigned(Func<string, bool> isSent)
        {
            if (Items.RemoveAll(e => isSent(e.Signature)) > 0) Revision++;
            return Items.Count == 0;
        }

        /// <summary>A new card with copies of only the given entries (the ones the ledger does not already know).</summary>
        public InstallErrorReport OnlyEntries(List<InstallErrorEntry> keep)
        {
            var copy = new InstallErrorReport { Product = Product, ProductId = ProductId, Version = Version, BaseCode = BaseCode, Reason = Reason };
            foreach (var entry in keep) copy.Items.Add(entry.Clone());
            return copy;
        }

        /// <summary>Adds the entries of <paramref name="other"/> that this report does not have yet, each with its own code and signature. True when something was added.</summary>
        public bool Merge(InstallErrorReport other)
        {
            if (other == null) return false;
            var added = false;
            foreach (var entry in other.Entries())
            {
                if (HasText(entry) || CountOf(entry.IsCompile) >= MaxLines) continue;
                Items.Add(entry.Clone());
                added = true;
            }
            if (string.IsNullOrEmpty(Reason)) Reason = other.Reason;
            if (added) Revision++;
            return added;
        }

        private bool HasText(InstallErrorEntry entry)
        {
            foreach (var item in Items) if (item.IsCompile == entry.IsCompile && item.Text == entry.Text) return true;
            return false;
        }

        private int CountOf(bool compile)
        {
            var n = 0;
            foreach (var item in Items) if (item.IsCompile == compile) n++;
            return n;
        }

        public bool SameProduct(InstallErrorReport other)
        {
            return other != null
                && string.Equals(Product, other.Product, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Version ?? "", other.Version ?? "", StringComparison.OrdinalIgnoreCase);
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

        // "Namespace.Type:Method (args) (at Assets/File.cs:12)" in the Console, "  at Namespace.Type.Method (args)" in an exception's own text.
        private static readonly Regex FrameLine = new Regex(@"^(?:at\s+)?(?<n>[A-Za-z_][\w.`+<>$]*(?::[\w.`+<>$]+)?)\s*\(", RegexOptions.Compiled);
        private static readonly string[] EngineFramePrefixes = { "UnityEngine.", "UnityEditor.", "System.", "Mono." };

        /// <summary>
        /// The first stack frame of <paramref name="text"/> (the lines after the first) that is not the engine's or the runtime's:
        /// the first product-assembly frame as "Namespace.Type.Method" without numbers. "" when there is no stack or no such frame.
        /// </summary>
        public static string FirstProductFrame(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var first = true;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                if (first) { first = false; continue; }
                var match = FrameLine.Match(line);
                if (!match.Success) continue;
                var name = match.Groups["n"].Value.Replace(':', '.');
                if (name.IndexOf('.') < 0) continue;
                var engine = false;
                foreach (var prefix in EngineFramePrefixes)
                    if (name.StartsWith(prefix, StringComparison.Ordinal)) { engine = true; break; }
                if (!engine) return Number.Replace(name, "");
            }
            return "";
        }

        /// <summary>
        /// A stable id (16 hex characters) of productId | errorCode | the first error line without digits and paths | for an entry with
        /// a stack trace, its first product frame (no product frame: the message only).
        /// </summary>
        public static string Signature(string errorCode, string productId, string errorText)
        {
            var key = (productId ?? "").Trim() + "|" + (errorCode ?? "").Trim() + "|" + NormalizeFirstLine(errorText) + "|" + FirstProductFrame(errorText);
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
        /// The paths an import owns, so errors can be attributed to it. Every file of the package counts as itself and so does its
        /// .meta (exact paths); a folder counts only when the import created it (<paramref name="existedBefore"/> was false when the
        /// import started), whatever its children, and then it owns everything below. A folder that already existed
        /// (Assets/Editor, Assets/StudioRaming_Onairdeck) owns nothing, so a product that brings one file into it does not claim its
        /// neighbours. Folders come from the archive's folder entries (<see cref="PackageAsset.IsFolder"/>) and from the parents of
        /// every path.
        /// </summary>
        public static List<string> OwnedPaths(IEnumerable<PackageAsset> assets, Func<string, bool> existedBefore)
        {
            var files = new List<string>();
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in assets)
            {
                var path = Normalize(asset.Path);
                if (path.Length == 0 || !seen.Add(path)) continue;
                if (asset.IsFolder) folders.Add(path); else files.Add(path);
            }
            foreach (var path in new List<string>(seen))
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
                if (!HasAncestor(folder, created)) { owned.Add(folder); owned.Add(folder + ".meta"); }
            foreach (var path in files)
                if (!folders.Contains(path) && !HasAncestor(path, created)) { owned.Add(path); owned.Add(path + ".meta"); }
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
    /// One import or package install of a watched product: the paths it owns and its own compile state and end time. Jobs never
    /// touch each other's state.
    /// </summary>
    [Serializable]
    internal sealed class InstallWatchJob
    {
        public string Id = "";
        public List<string> Paths = new List<string>();
        public bool CompileStarted;
        public bool CompileDone;
        /// <summary>UTC ticks after which this job is over (unless a compile it saw start is still running).</summary>
        public long DeadlineTicks;
        public long HardCapTicks;

        public bool IsActive(long nowTicks)
        {
            // A compile the job saw start is waited for up to the hard cap, however long it takes.
            var limit = CompileStarted && !CompileDone ? HardCapTicks : Math.Min(DeadlineTicks, HardCapTicks);
            return nowTicks <= limit;
        }

        public void ImportFinished(long nowTicks)
        {
            if (!CompileStarted && !CompileDone) DeadlineTicks = Math.Min(DeadlineTicks, nowTicks + InstallWatchState.NoCompileTicks);
        }

        /// <summary>The first compile ended (with or without errors): the job stays open two more minutes. A later compile changes nothing.</summary>
        public void CompileFinished(long nowTicks)
        {
            if (!CompileStarted || CompileDone) return;
            CompileDone = true;
            DeadlineTicks = nowTicks + InstallWatchState.AfterCompileTicks;
        }
    }

    /// <summary>
    /// What the watch of one product remembers across a domain reload: one <see cref="InstallWatchJob"/> per import or package
    /// install, each with its own paths, compile state and end time. The watch scope is the union of the jobs still active, and the
    /// watch is over when its last job is. A job runs from its import start until the first post-import script compile ends, plus
    /// <see cref="AfterCompileTicks"/>; later compiles do not extend it.
    /// </summary>
    [Serializable]
    internal sealed class InstallWatchState
    {
        public static readonly long AfterCompileTicks = TimeSpan.FromMinutes(2).Ticks;
        /// <summary>The longest a job lives (an import window can stay open, a compile can be long).</summary>
        public static readonly long HardCapTicksSpan = TimeSpan.FromHours(1).Ticks;
        /// <summary>After the import finished, how long a job waits for a compile that may never start (a package without scripts).</summary>
        public static readonly long NoCompileTicks = TimeSpan.FromMinutes(10).Ticks;

        public string Product = "";
        public string ProductId = "";
        public string Version = "";
        public List<InstallWatchJob> Jobs = new List<InstallWatchJob>();
        public List<string> Assemblies = new List<string>();

        public static InstallWatchState Begin(string product, string productId, string version, string jobId, IEnumerable<string> paths, long nowTicks)
        {
            var state = new InstallWatchState { Product = product ?? "", ProductId = productId ?? "", Version = version ?? "" };
            state.AddJob(jobId, paths, nowTicks);
            return state;
        }

        /// <summary>The owned paths of every job (see <see cref="InstallErrorLogic.OwnedPaths"/>).</summary>
        public List<string> Prefixes
        {
            get
            {
                var all = new List<string>();
                foreach (var job in Jobs)
                    foreach (var path in job.Paths)
                        if (!all.Contains(path)) all.Add(path);
                return all;
            }
        }

        public InstallWatchJob Job(string jobId)
        {
            return Jobs.Find(j => j.Id == jobId);
        }

        /// <summary>Another package of the same product: a job of its own. Other jobs keep their state; the same id starts over.</summary>
        public void AddJob(string jobId, IEnumerable<string> paths, long nowTicks)
        {
            Jobs.RemoveAll(j => j.Id == jobId);
            var job = new InstallWatchJob { Id = jobId ?? "", DeadlineTicks = nowTicks + HardCapTicksSpan };
            job.HardCapTicks = job.DeadlineTicks;
            foreach (var path in paths) if (!job.Paths.Contains(path)) job.Paths.Add(path);
            Jobs.Add(job);
        }

        /// <summary>A cancelled or failed job takes only its own paths and state away. True when no job is left and the watch is over.</summary>
        public bool RemoveJob(string jobId)
        {
            Jobs.RemoveAll(j => j.Id == jobId);
            return Jobs.Count == 0;
        }

        public bool IsActive(long nowTicks)
        {
            return Jobs.Exists(j => j.IsActive(nowTicks));
        }

        /// <summary>Drops the jobs that are over. True when some were.</summary>
        public bool Prune(long nowTicks)
        {
            return Jobs.RemoveAll(j => !j.IsActive(nowTicks)) > 0;
        }

        public void ImportFinished(string jobId, long nowTicks)
        {
            var job = Job(jobId);
            if (job != null) job.ImportFinished(nowTicks);
        }

        /// <summary>A script compile started: every job that has not seen one yet is waiting for this one. True when something changed.</summary>
        public bool MarkCompileStarted()
        {
            var changed = false;
            foreach (var job in Jobs)
                if (!job.CompileStarted) { job.CompileStarted = true; changed = true; }
            return changed;
        }

        public void CompileFinished(long nowTicks)
        {
            foreach (var job in Jobs) job.CompileFinished(nowTicks);
        }

        /// <summary>A compile a job saw start is gone (the domain reloaded under it): those jobs move on. True when one did.</summary>
        public bool CompileLostToReload(long nowTicks)
        {
            var changed = false;
            foreach (var job in Jobs)
                if (job.CompileStarted && !job.CompileDone) { job.CompileFinished(nowTicks); changed = true; }
            return changed;
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
