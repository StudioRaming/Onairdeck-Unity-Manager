using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.UnityManager;

// The install error report: what is redacted before it is shown and sent, the signature that decides "already sent or
// dismissed", which paths and texts belong to a watched product, and the excerpt the card shows (= the consoleLog sent).
internal static class InstallErrorChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        check("error report redaction: profile folder (both slashes, any case), user name, JWT, hex, Bearer, sk- key and query token; plain text is unchanged", Redaction);
        check("error report signature: same error with other line numbers, paths, GUIDs and numbers is the same; another error, code, product or version is not", SignatureStability);
        check("error report attribution: a path under the imported folders matches, a sibling or other package does not; license-gate texts and compiler lines are told apart", PathAttribution);
        check("error report excerpt: compile errors first, redacted, cut at whole entries under the cap (a lone long one is cut), same text twice", ExcerptOrderAndCap);
        check("error report watch: ends two minutes after the compile, a package without scripts gives up after the cap, a compile that never started does not end it", WatchDeadlines);
        check("error report ledger: a sent or dismissed signature stays known across instances of the same project file", LedgerPersists(project));
    }

    private const string Home = @"C:\Users\zmnbn";

    private static void Redaction()
    {
        var text = FeedbackText.Redact(@"at C:\Users\zmnbn\proj\Assets\A.cs and c:/users/ZMNBN/proj/B.cs", Home, "zmnbn");
        Equal(@"at %USERPROFILE%\proj\Assets\A.cs and %USERPROFILE%/proj/B.cs", text);
        Equal(@"\\PC\<user>\share and <user> failed", FeedbackText.Redact(@"\\PC\zmnbn\share and zmnbn failed", Home, "zmnbn"));
        // E-mail addresses go first, so a user name inside one does not leave half an address behind.
        Equal("mail <email> now", FeedbackText.Redact("mail zmnbn@example.com now", Home, "zmnbn"));
        Equal("<token>", FeedbackText.Redact("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk", null, null));
        Equal("hash <token> end", FeedbackText.Redact("hash 0123456789abcdef0123456789abcdef01234567 end", null, null));
        Equal("auth Bearer <token>", FeedbackText.Redact("auth Bearer abc.def-123_xyz", null, null));
        Equal("key <token> used", FeedbackText.Redact("key sk-abcdEFGH12345678 used", null, null));
        Equal("https://x.test/a?id=5&token=<token>&k=1", FeedbackText.Redact("https://x.test/a?id=5&token=abc123&k=1", null, null));
        Equal("https://x.test/a?Signature=<token>", FeedbackText.Redact("https://x.test/a?Signature=AbC%2Bdef", null, null));
        Equal("api_key=<token>", FeedbackText.Redact("api_key=abcdef123456", null, null));
        Equal("<token>", FeedbackText.Redact("QWxhZGRpbjpvcGVuIHNlc2FtZVF3ZXJ0eTEyMzQ1Njc4", null, null));
        // A short name is masked as a whole word too (the WARUDO rule), not inside longer words; a second pass changes nothing.
        Equal("hi <user> and zmnx", FeedbackText.Redact("hi zmn and zmnx", null, "zmn"));
        Equal(@"C:\x\<user>\y", FeedbackText.Redact(@"C:\x\zmn\y", null, "zmn"));
        var once = FeedbackText.Redact(@"C:\Users\zmnbn\a zmnbn@x.io Bearer abc.def-123_xyz token=abc123 user", Home, "user");
        Equal(once, FeedbackText.Redact(once, Home, "user"));
        // Nothing to hide: identical. A 31-letter identifier, a short user name and a compiler line stay.
        foreach (var plain in new[]
        {
            "Assets/Foo/Bar.cs(12,5): error CS1002: ; expected",
            "OnAirDeckUnityManagerEditorType failed",
            "User cancelled the import of Product 1.2.3",
        })
            Equal(plain, FeedbackText.Redact(plain, Home, "zmn"));
    }

    private static void SignatureStability()
    {
        var a = InstallErrorLogic.Signature(InstallErrorCodes.CompileFailed, "lic-1", "Assets/Prod/Foo.cs(12,5): error CS1002: ; expected");
        var b = InstallErrorLogic.Signature(InstallErrorCodes.CompileFailed, "lic-1", @"C:\Users\x\proj\Packages\com.prod\Runtime\Foo.cs(480,31): error CS1002: ; expected" + "\nsecond line");
        Equal(a, b);
        Equal(16, a.Length);
        var guid = InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "Missing GUID 0123456789abcdef0123456789abcdef at 0x7ff612 in 3 files");
        Equal(guid, InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "Missing GUID fedcba9876543210fedcba9876543210 at 0x1a2b in 12 files"));
        Require(a != InstallErrorLogic.Signature(InstallErrorCodes.CompileFailed, "lic-1", "Assets/Prod/Foo.cs(12,5): error CS0103: The name 'x' does not exist"));
        Require(a != InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "Assets/Prod/Foo.cs(12,5): error CS1002: ; expected"));
        Require(a != InstallErrorLogic.Signature(InstallErrorCodes.CompileFailed, "lic-2", "Assets/Prod/Foo.cs(12,5): error CS1002: ; expected"));
        // The report's own signature uses its first compile error, else the reason; the version does not matter, the product id does.
        var report = new InstallErrorReport { Product = "Product", ProductId = "lic-1", Version = "1.2.0" };
        report.Compile.Add("Assets/Prod/Foo.cs(9,9): error CS1002: ; expected");
        Equal(a, report.Signature);
        report.Version = "9.9.9";
        Equal(a, report.Signature);
        report.ProductId = "lic-2";
        Require(a != report.Signature);
        Equal(InstallErrorCodes.CompileFailed, report.ErrorCode);

        // Every error line has its own signature (the ledger records all of them), compile errors first.
        report.Other.Add("NullReferenceException: boom");
        var entries = report.Entries();
        Equal(2, entries.Count);
        Equal(InstallErrorCodes.CompileFailed, entries[0].Code);
        Equal(InstallErrorCodes.ManagerFailed, entries[1].Code);
        Require(entries[0].Signature != entries[1].Signature);
        // Only the lines the ledger does not know go on; merging what is already there adds nothing.
        var rest = report.OnlyEntries(entries.GetRange(1, 1));
        Equal(0, rest.Compile.Count);
        Equal(1, rest.Other.Count);
        var holder = new InstallErrorReport { Product = "Product", Version = "9.9.9" };
        Require(holder.Merge(report));
        var revision = holder.Revision;
        Require(!holder.Merge(report));
        Equal(revision, holder.Revision);
        // A send takes a snapshot; lines that joined afterwards stay, the sent ones go.
        var shot = holder.TakeSnapshot(20 * 1024, 4000);
        Equal(2, shot.Signatures.Count);
        holder.Other.Add("later exception");
        Require(!holder.RemoveSent(shot));
        Equal(0, holder.Compile.Count);
        Equal(1, holder.Other.Count);
        Equal("later exception", holder.Other[0]);
        Require(shot.Excerpt.IndexOf("later exception", StringComparison.Ordinal) < 0);
    }

    private static void PathAttribution()
    {
        // A folder the import creates is the product's; folders that were already there are not.
        var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets", "Assets/StudioRaming_Onairdeck" };
        var prefixes = InstallErrorLogic.OwnedPaths(new[]
        {
            "Assets", "Assets/StudioRaming_Onairdeck", "Assets/StudioRaming_Onairdeck/Prod",
            "Assets/StudioRaming_Onairdeck/Prod/Editor", "Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs",
            "Assets/StudioRaming_Onairdeck/Prod/Runtime/Core.dll", "Assets/Loose.cs",
        }, existed.Contains);
        Equal(2, prefixes.Count);
        Require(prefixes.Contains("Assets/Loose.cs"));
        Require(prefixes.Contains("Assets/StudioRaming_Onairdeck/Prod"));

        Require(InstallErrorLogic.IsUnder("Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs", prefixes));
        Require(InstallErrorLogic.IsUnder(@"C:\proj\Assets\StudioRaming_Onairdeck\Prod\Runtime\Core.dll", prefixes));
        Require(InstallErrorLogic.IsUnder("assets/loose.cs", prefixes));
        Require(!InstallErrorLogic.IsUnder("Assets/StudioRaming_Onairdeck/Other/Editor/Tool.cs", prefixes));
        Require(!InstallErrorLogic.IsUnder("Assets/StudioRaming_Onairdeck/Prod2/Tool.cs", prefixes));
        Require(!InstallErrorLogic.IsUnder("Packages/com.other/Editor/Tool.cs", prefixes));
        Require(!InstallErrorLogic.IsUnder("", prefixes));

        Require(InstallErrorLogic.MentionsPath("Assets/StudioRaming_Onairdeck/Prod/Runtime/a.png: Texture import failed", prefixes));
        Require(InstallErrorLogic.MentionsPath("  at X.Y () (at Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs:12)", prefixes));
        Require(!InstallErrorLogic.MentionsPath("NullReferenceException at Assets/Other/Thing.cs:3", prefixes));
        Require(!InstallErrorLogic.MentionsPath("MyAssets/Loose.cs failed", prefixes));

        // One file into a shared, existing folder owns that file only, not its neighbours.
        var single = InstallErrorLogic.OwnedPaths(new[] { "Assets", "Assets/Editor", "Assets/Editor/Tool.cs" },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets", "Assets/Editor" }.Contains);
        Equal(1, single.Count);
        Equal("Assets/Editor/Tool.cs", single[0]);
        Require(InstallErrorLogic.IsUnder("Assets/Editor/Tool.cs", single));
        Require(!InstallErrorLogic.IsUnder("Assets/Editor/Other.cs", single));
        Require(!InstallErrorLogic.IsUnder("Assets/Editor/Tool.cs.meta", single));
        Require(!InstallErrorLogic.MentionsPath("Assets/Editor/Other.cs(3,4): error CS1002", single));
        Require(InstallErrorLogic.MentionsPath("Assets/Editor/Tool.cs(3,4): error CS1002", single));

        // Whole segments: com.vendor.tool is not com.vendor.tool.extra.
        var package = InstallErrorLogic.OwnedPaths(new[] { "Packages/com.vendor.tool" }, x => x == "Packages");
        Equal("Packages/com.vendor.tool", package[0]);
        Require(InstallErrorLogic.IsUnder("Packages/com.vendor.tool/Editor/A.cs", package));
        Require(!InstallErrorLogic.IsUnder("Packages/com.vendor.tool.extra/Editor/A.cs", package));
        Require(!InstallErrorLogic.MentionsPath("Packages/com.vendor.tool.extra/Editor/A.cs failed", package));
        Require(InstallErrorLogic.MentionsPath("Packages/com.vendor.tool/Editor/A.cs failed", package));

        // License-gate states and lock-screen lines are never a bug; a real error is not mistaken for them.
        Require(InstallErrorLogic.IsLicenseGateText("[Prod] license gate: NotSignedIn"));
        Require(InstallErrorLogic.IsLicenseGateText("The license has expired. Open Atelier Manager to renew it."));
        Require(InstallErrorLogic.IsLicenseGateText("Sign in with Atelier Manager (Window > Atelier Manager) and reopen this tool."));
        Require(InstallErrorLogic.IsLicenseGateText("No OnAirDeck license for this product was found under Assets/StudioRaming_Onairdeck/."));
        Require(!InstallErrorLogic.IsLicenseGateText("Assets/Prod/Foo.cs(12,5): error CS1002: ; expected"));
        Require(!InstallErrorLogic.IsLicenseGateText("NullReferenceException: Object reference not set"));
        Require(InstallErrorLogic.IsCompilerLine("Assets/Prod/Foo.cs(12,5): error CS1002: ; expected"));
        Require(!InstallErrorLogic.IsCompilerLine("NullReferenceException at Foo"));
    }

    private static void ExcerptOrderAndCap()
    {
        var report = new InstallErrorReport { Product = "Prod", Version = "1.0" };
        report.Other.Add("Exception: boom in C:\\Users\\zmnbn\\proj");
        report.Compile.Add("Assets/Prod/A.cs(1,1): error CS1002: ; expected");
        report.Compile.Add("Assets/Prod/B.cs(2,2): error CS0103: no x");
        var text = report.Excerpt(20 * 1024);
        var compileA = text.IndexOf("A.cs", StringComparison.Ordinal);
        var compileB = text.IndexOf("B.cs", StringComparison.Ordinal);
        var exception = text.IndexOf("Exception: boom", StringComparison.Ordinal);
        Require(compileA >= 0 && compileA < compileB && compileB < exception);
        Equal(text, report.Excerpt(20 * 1024));

        // The cap keeps whole entries from the front.
        var one = Encoding.UTF8.GetByteCount("Assets/Prod/A.cs(1,1): error CS1002: ; expected");
        var capped = report.Excerpt(one + 4);
        Equal("Assets/Prod/A.cs(1,1): error CS1002: ; expected", capped);

        // A single entry longer than the cap is cut, never longer than the cap, never splitting a character.
        var lone = new InstallErrorReport { Product = "Prod" };
        lone.Other.Add(new string('가', 5000));
        var cut = lone.Excerpt(1000);
        Require(Encoding.UTF8.GetByteCount(cut) <= 1000);
        Require(cut.Length > 0);

        var many = new InstallErrorReport { Product = "Prod" };
        for (var i = 0; i < 100; i++) many.Other.Add("line " + i);
        Equal(InstallErrorReport.MaxLines, MergedCount(many));

        Equal("Prod: Assets/Prod/A.cs(1,1): error CS1002: ; expected", ReportWithVersion(report, ""));
        Equal("Prod 1.0: Assets/Prod/A.cs(1,1): error CS1002: ; expected", ReportWithVersion(report, "1.0"));
    }

    private static int MergedCount(InstallErrorReport source)
    {
        var target = new InstallErrorReport { Product = "Prod" };
        target.Merge(source);
        return target.Other.Count;
    }

    private static string ReportWithVersion(InstallErrorReport report, string version)
    {
        var copy = new InstallErrorReport { Product = report.Product, Version = version };
        copy.Merge(report);
        return copy.AutoSummary(4000);
    }

    private static void WatchDeadlines()
    {
        var t0 = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;
        var state = InstallWatchState.Begin("Prod", "lic-1", "1.0", new[] { "Assets/Prod" }, t0);
        Require(state.IsActive(t0 + 30 * minute));

        // The compile is what ends it: a compile-finished event before any compile started changes nothing.
        state.CompileFinished(t0 + 5 * minute);
        Require(!state.CompileDone);
        state.CompileStarted = true;
        state.CompileFinished(t0 + 5 * minute);
        Require(state.CompileDone);
        Require(state.IsActive(t0 + 7 * minute));
        Require(!state.IsActive(t0 + 7 * minute + 1));
        // A finished import does not shorten a watch whose compile already ended.
        state.ImportFinished(t0 + 6 * minute);
        Require(state.IsActive(t0 + 7 * minute));

        // The deadline is fixed by the first compile; a later compile does not extend it.
        state.CompileFinished(t0 + 6 * minute);
        Require(!state.IsActive(t0 + 7 * minute + 1));

        // A compile that is still running is waited for (past the no-script cap) up to the hard cap, then two minutes after it.
        var slow = InstallWatchState.Begin("Prod", "lic-1", "1.0", new[] { "Assets/Prod" }, t0);
        slow.CompileStarted = true;
        slow.ImportFinished(t0 + minute);
        Require(slow.IsActive(t0 + 30 * minute));
        Require(!slow.IsActive(t0 + 61 * minute));
        slow.CompileFinished(t0 + 30 * minute);
        Require(slow.IsActive(t0 + 32 * minute) && !slow.IsActive(t0 + 32 * minute + 1));

        // A second package of the same product joins; ending one job keeps the other's paths and the watch.
        var two = InstallWatchState.Begin("Prod", "lic-1", "1.0", new[] { "Assets/A" }, t0);
        two.AddJob(new[] { "Assets/B", "Assets/A" }, t0 + minute);
        Equal(2, two.Prefixes.Count);
        Require(!two.RemoveJob(new[] { "Assets/A" }));
        Equal("Assets/B", two.Prefixes[0]);
        Require(two.RemoveJob(new[] { "Assets/B" }));

        var noScripts = InstallWatchState.Begin("Prod", "lic-1", "1.0", new[] { "Assets/Prod" }, t0);
        noScripts.ImportFinished(t0 + minute);
        Require(noScripts.IsActive(t0 + 10 * minute));
        Require(!noScripts.IsActive(t0 + 12 * minute));
    }

    private static Action LedgerPersists(string project)
    {
        return () =>
        {
            var file = Path.Combine(project, "Library", "OnAirDeck", "InstallErrorReports.txt");
            var first = new InstallErrorLedger(file);
            Require(!first.Contains("abc"));
            first.Add("abc", "sent");
            first.Add("def", "dismissed");
            first.Add("abc", "sent");
            var second = new InstallErrorLedger(file);
            Require(second.Contains("abc") && second.Contains("def") && !second.Contains("ghi"));
            Equal("sent", second.State("abc"));
            Equal("dismissed", second.State("def"));
            // A dismissed report that is sent later is "sent" (a manual reopen may send it); a sent one is never downgraded.
            second.Add("def", "sent");
            second.Add("abc", "dismissed");
            var third = new InstallErrorLedger(file);
            Equal("sent", third.State("def"));
            Equal("sent", third.State("abc"));
            Equal(null, third.State("ghi"));
            Equal(3, File.ReadAllLines(file).Length);
        };
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
