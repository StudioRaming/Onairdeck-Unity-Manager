using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OnAirDeck.UnityManager;

// The install error report: what is redacted before it is shown and sent, the signature that decides "already sent or
// dismissed", which paths and texts belong to a watched product, the excerpt the card shows (= the consoleLog sent) and which
// entries it holds, the card identity, and the per-job watch.
internal static class InstallErrorChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        check("error report redaction: paths (drive, UNC, //host, file:///, 8.3, JSON-escaped, POSIX) project-relative or <path>/file, names, e-mail, tokens, secret keys, Bearer, Steam ids", Redaction);
        check("error report redaction: spans are found on the original text (a user named key still hides key=value), short names are whole tokens, a second pass changes nothing", RedactionOverlapAndIdempotence);
        check("error report redaction: plain text, project-relative paths and compiler lines stay; the package list is redacted entry by entry", RedactionLeavesPlainText);
        check("error report redaction: a secret value is hidden even with a placeholder inside it; only an exact placeholder is left alone", RedactionSecretWithPlaceholder);
        check("error report redaction: random 20+ runs without digits or split by slashes are tokens; identifiers, constants, Unity paths and package labels stay", RedactionTokensAndIdentifiers);
        check("error report redaction: whole absolute paths go (comma or many words in a folder, JSON-escaped slashes), the sentence and the next line stay", RedactionWholePaths);
        check("error report signature: same error with other line numbers, paths, GUIDs and numbers is the same; another error, code, product or version is not; the first product frame counts", SignatureStability);
        check("error report entries: each keeps its own code, product id and signature through merge; a same-signature error is dropped once sent", EntriesKeepTheirOwnSignature);
        check("error report card: every card has its own id; a snapshot belongs to one card at one revision", CardIdentity);
        check("error report attribution: a path under the imported folders matches, a sibling or other package does not; license-gate texts and compiler lines are told apart", PathAttribution);
        check("error report owned paths: an existing folder owns nothing even without children, a new one does, every owned file brings its .meta; the archive tells folders by their meta", OwnedPathsAndArchive(project));
        check("error report excerpt: compile errors first, redacted before it is cut, cut at whole entries under the cap and only those are 'included'", ExcerptOrderAndCap);
        check("error report watch: ends two minutes after the compile, a package without scripts gives up after the cap, a compile that never started does not end it", WatchDeadlines);
        check("error report watch jobs: each job keeps its own paths, compile state and end time; removing one takes only its part; the scope is the union of active jobs", WatchJobs);
        check("error report ledger: a sent or dismissed signature stays known across instances of the same project file", LedgerPersists(project));
    }

    private static readonly RedactContext Ctx = new RedactContext(@"C:\proj\My Game", "zmnbn", "DESKTOP-K1");

    private static string R(string text) { return FeedbackText.Redact(text, Ctx); }

    private static void Redaction()
    {
        // Inside the project folder (either slash, any case, JSON-escaped): project-relative.
        Equal("at Assets/A.cs(1,2) and Packages/com.x/B.cs", R(@"at C:\proj\My Game\Assets\A.cs(1,2) and c:/PROJ/my game/Packages/com.x/B.cs"));
        Equal(@"{""p"":""Library/L.log""}", R(@"{""p"":""C:\\proj\\My Game\\Library\\L.log""}"));
        Equal("see Assets/H.cs now", R("see file:///C:/proj/My%20Game/Assets/H.cs now"));
        // Everywhere else: <path>/<file name>.
        Equal("open <path>/B.cs now", R(@"open C:\Users\zmnbn\proj\B.cs now"));
        Equal("<path>/C.cs", R("c:/users/ZMNBN/x/C.cs"));
        Equal(@"{""p"":""<path>/D.cs""}", R(@"{""p"":""C:\\Users\\zmnbn\\D.cs""}"));
        Equal("<path>/E.txt and <path>/F.txt", R(@"\\srv\share\E.txt and //srv/share/F.txt"));
        Equal("<path>/G.cs", R("file:///C:/Users/zmnbn/G.cs"));
        Equal("<path>/U.exe", R(@"C:\PROGRA~1\Unity Hub\U.exe"));
        Equal("<path>/I.cs", R("/Users/zmnbn/I.cs"));
        Equal("see <user> here", R("see ZMNBN~1 here"));
        // Names anywhere, case-insensitive.
        Equal("hi <user>, <user> and <host>", R("hi zmnbn, ZMNBN and desktop-k1"));
        Equal("mail <email> now", R("mail zmnbn@example.com now"));
        Equal("id <steamid> x", R("id 76561198012345678 x"));
        // Runs of 20+ base64 / hex characters; a JWT is one token.
        Equal("<token>", R("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        Equal("hash <token> end", R("hash 0123456789abcdef0123456789abcdef01234567 end"));
        Equal("<token>", R("QWxhZGRpbjpvcGVuIHNlc2FtZVF3ZXJ0eTEyMzQ1Njc4"));
        Equal("blob <token>", R("blob QWxhZGRpbjpvcGVuIHNlc2FtZQ+Zg=="));
        // Secret-looking keys keep their name, the value goes.
        Equal("auth Bearer <redacted>", R("auth Bearer abc.def-123_xyz"));
        Equal("Authorization: Bearer <redacted>", R("Authorization: Bearer abc123xyz"));
        Equal("https://x.test/a?id=5&token=<redacted>&k=1", R("https://x.test/a?id=5&token=abc123&k=1"));
        Equal("https://x.test/a?Signature=<redacted>", R("https://x.test/a?Signature=AbC%2Bdef"));
        Equal("api_key: <redacted>", R("api_key: abcdef123456"));
        Equal(@"{""password"": ""<redacted>"", ""n"": 1}", R(@"{""password"": ""hunter2"", ""n"": 1}"));
        Equal("cookie=<redacted>; theme=dark", R("cookie=sess123; theme=dark"));
    }

    private static void RedactionOverlapAndIdempotence()
    {
        // N2: the rules look at the original text, so a user named "key" does not hide the value of key=abc123.
        var key = new RedactContext("", "key", "");
        Equal("<user>=<redacted> failed", FeedbackText.Redact("key=abc123 failed", key));
        // A path that contains the name, the host and a token is replaced once, as a path.
        Equal("<path>/<user>.log", R(@"D:\data\zmnbn.log"));
        Equal("<path>/<token>", R(@"D:\data\0123456789abcdef0123456789abcdef"));

        // N11: one- and two-character names are whole tokens only.
        var one = new RedactContext("", "k", "");
        Equal("User <user> failed", FeedbackText.Redact("User k failed", one));
        Equal("task <user>", FeedbackText.Redact("task k", one));
        Equal("cab <user>", FeedbackText.Redact("cab ab", new RedactContext("", "ab", "")));

        // A second pass changes nothing, whatever the names are (also names that are placeholder words).
        const string sample = @"C:\Users\zmnbn\a\b.log zmnbn@x.io Bearer abc.def-123_xyz token=abc123 desktop-k1 76561198012345678 "
            + "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4 key: v1 {\"secret\":\"s3\"} C:\\proj\\My Game\\Assets\\Z.cs "
            + @"file:///C:/x/y.cs \\srv\s\f.txt 0123456789abcdef0123456789abcdef path user host email token redacted <path> <user> k ab";
        foreach (var user in new[] { "zmnbn", "path", "user", "key", "tok", "k", "ab" })
        {
            var context = new RedactContext(@"C:\proj\My Game", user, "desktop-k1");
            var once = FeedbackText.Redact(sample, context);
            Equal(once, FeedbackText.Redact(once, context));
        }
        Require(R(sample).IndexOf("zmnbn", StringComparison.OrdinalIgnoreCase) < 0);
        Require(R(sample).IndexOf("abc123", StringComparison.Ordinal) < 0);
        Require(R(sample).IndexOf("dBjftJeZ4", StringComparison.Ordinal) < 0);
    }

    private static void RedactionLeavesPlainText()
    {
        foreach (var plain in new[]
        {
            "Assets/Foo/Bar.cs(12,5): error CS1002: ; expected",
            "Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs failed",
            "Packages/com.unity.render-pipelines.universal/Editor/A.cs",
            "OnAirDeckUnityManagerEditorType failed",
            "User cancelled the import of Product 1.2.3",
            "Assets/Foo.cs(3,4): error CS0103: The name 'tokenizer' does not exist",
        })
            Equal(plain, R(plain));

        // The package list of a message is redacted entry by entry (a product label can carry an e-mail or a path).
        var packages = FeedbackText.RedactEach(new[] { "com.studioraming.x@1.0", "Pro a@b.io (C:\\Users\\zmnbn\\Pro)@2.0" }, Ctx);
        Equal("com.studioraming.x@1.0", packages[0]);
        Equal("Pro <email> (<path>/Pro)@2.0", packages[1]);
        Equal(0, FeedbackText.RedactEach(null, Ctx).Length);
    }

    private static void RedactionSecretWithPlaceholder()
    {
        // R3-1: the hidden part is the whole value, not what is left after a placeholder.
        Equal(@"password=""<redacted>""", R(@"password=""prefix<user>hunter2"""));
        Equal("password=<redacted>", R("password=<redacted>hunter2"));
        Equal("auth Bearer <redacted>", R("auth Bearer <redacted>abc123"));
        Equal(@"{""secret"": ""<redacted>""}", R(@"{""secret"": ""x<host>y<token>z""}"));
        // An exact placeholder is already redacted: left alone, so a second pass changes nothing.
        Equal("password=<redacted> Bearer <redacted>", R("password=<redacted> Bearer <redacted>"));
        foreach (var input in new[] { @"password=""prefix<user>hunter2""", "token=<token>abc", "Bearer <redacted>abc123" })
        {
            var once = R(input);
            Equal(once, R(once));
        }
    }

    private static void RedactionTokensAndIdentifiers()
    {
        // R3-2: caught.
        foreach (var token in new[]
        {
            "ABCDEFGHIJKLMNOPQRSTUVWX",                  // base32-like, one case
            "abcdEFGH/ijklMNOP/qrstUVWX",                // half upper case, slashes
            "xKpQmRtVbNwLzHcYjDfGsAeUo",                 // random base64url, no digit
            "k3Jd8sL0pQ7vN2mXb5RtWz",                    // with digits
            "a8f3k2m9x1q7z5b4c6d0e",                     // lower case with many digits
            "Zm9vYmFyYmF6cXV4Zm9vYmFy+Ab",               // '+' (base64)
        })
            Equal("tok <token> end", R("tok " + token + " end"));
        // Kept: identifiers, constants, Unity paths, package labels, dotted names.
        foreach (var plain in new[]
        {
            "InvalidOperationException: bad", "NullReferenceException at", "MissingReferenceException happened",
            "GetComponentInChildrenOfType called", "XMLHttpRequestHandlerFactory failed", "Texture2DArrayInitializerHelper failed",
            "UNITY_EDITOR_WINDOWS_PLATFORM defined", "internationalizationstuffhere",
            "Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs", "Packages/com.vendor.tool/Runtime/Core.cs",
            "Library/PackageCache/Some_Long_Folder_Name/File", "ProjectSettings/ProjectSettingsAssetLong",
            "Resources/Textures/Characters/Body_Albedo", "com.studioraming.x@1.0",
            "UnityEngine.Rendering.Universal.UniversalRenderPipeline:Render",
        })
            Equal(plain, R(plain));
    }

    private static void RedactionWholePaths()
    {
        // R3-3: the whole path goes, whatever the folder names hold.
        Equal("<path>/Config.json", R(@"C:\private\Client,Internal\Config.json"));
        Equal("<path>/Config.json", R(@"C:\private\One Two Three Four Five Six\Config.json"));
        Equal("<path>/Config.json", R(@"C:\/Users\/Alice\/Config.json"));
        Equal(@"{""p"":""<path>/Config.json""}", R(@"{""p"":""C:\/Users\/Alice\/Config.json""}"));
        Equal("<path>/Config.json", R(@"\\srv\a,b\Config.json"));
        Equal("Assets/Config.json", R(@"C:\proj\My Game\Assets\Config.json"));
        // The sentence after a path stays, and a path never joins the next line.
        Equal("<path>/b.txt, then failed", R(@"C:\a\b.txt, then failed"));
        Equal("open <path>/b.txt; then <path>/y.txt", R(@"open C:\a\b.txt; then C:/x/y.txt"));
        Equal("see <path>/dir\nnext line a/b", R("see C:\\a\\dir\nnext line a/b"));
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

        // An exception is also told by its first product frame: engine, runtime and Mono frames are skipped, line numbers do not count.
        Func<string, string> stack = frame => "NullReferenceException: boom\n  at UnityEngine.Object.Get ()\n  at System.Linq.X.Y ()\n  at Mono.Z.W ()\n  at " + frame + " ()";
        var exception = InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", stack("Prod.Core.Run"));
        Require(exception != InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", stack("Prod.Other.Go")));
        Equal(exception, InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1",
            "NullReferenceException: boom\nUnityEngine.Object:Get ()\nProd.Core:Run (int) (at Assets/Prod/Core.cs:12)\nUnityEditor.X:Y ()"));
        Equal(exception, InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1",
            "NullReferenceException: boom\nUnityEngine.Object:Get ()\nProd.Core:Run (int) (at Assets/Prod/Core.cs:99)"));
        // R3-4: generic frames are product frames; digits in identifiers count; line numbers, IL offsets and hashes do not.
        Func<string, string> at = frame => "InvalidOperationException: boom\n  at UnityEngine.Object.Get ()\n  at " + frame;
        var message = InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "InvalidOperationException: boom");
        var generic = InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Cache.Load[T] (T value)"));
        Require(generic != message);
        Require(generic != InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Other.Load[T] (T value)")));
        Require(generic != InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Cache.Load<T> (T value)")));
        Require(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Cache`1[T].Load (T value)")) != message);
        Require(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "InvalidOperationException: boom\nProd.Cache`1[T]:Load (T value) (at Assets/Prod/C.cs:5)") != message);
        Require(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Core.Run12 ()"))
            != InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Core.Run13 ()")));
        Equal(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Core.Run12 () [0x00008] in <0123abcd0123abcd0123abcd0123abcd>:0")),
            InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Core.Run12 () [0x0003f] in <fedcba98fedcba98fedcba98fedcba98>:0")));
        Equal(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Cache.Load[T] (T value) [0x00001] in <aa>:0")),
            InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", at("Prod.Cache.Load[T] (T value) [0x000ff] in <bb>:0")));
        // No product frame: the message alone.
        Equal(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "NullReferenceException: boom"),
            InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-1", "NullReferenceException: boom\n  at UnityEngine.Object.Get ()\n  at System.A.B ()"));
    }

    private static void EntriesKeepTheirOwnSignature()
    {
        // N4: a compile card that merges an exception keeps that exception's own code and signature.
        var card = new InstallErrorReport { Product = "Prod", ProductId = "lic-1", Version = "1.0" };
        Require(card.AddCompile("Assets/Prod/A.cs(1,1): error CS1002: ; expected"));
        Require(!card.AddCompile("Assets/Prod/A.cs(1,1): error CS1002: ; expected"));
        var incoming = new InstallErrorReport { Product = "Prod", ProductId = "lic-2", Version = "1.0", BaseCode = InstallErrorCodes.Exception };
        incoming.AddOther("NullReferenceException: boom\n  at Prod.Core.Run ()");
        var revision = card.Revision;
        Require(card.Merge(incoming));
        Require(card.Revision > revision);
        var entries = card.Entries();
        Equal(2, entries.Count);
        Equal(InstallErrorCodes.CompileFailed, entries[0].Code);
        Equal(InstallErrorCodes.Exception, entries[1].Code);
        Equal("lic-2", entries[1].ProductId);
        Equal(InstallErrorLogic.Signature(InstallErrorCodes.Exception, "lic-2", entries[1].Text), entries[1].Signature);
        Equal(InstallErrorLogic.Signature(InstallErrorCodes.CompileFailed, "lic-1", entries[0].Text), entries[0].Signature);
        Require(entries[0].Signature != entries[1].Signature);
        Equal(InstallErrorCodes.CompileFailed, card.ErrorCode);
        // Merging what is already there changes nothing.
        revision = card.Revision;
        Require(!card.Merge(incoming));
        Equal(revision, card.Revision);
        // OnlyEntries keeps the entries as they are (the ones the ledger does not know).
        var rest = card.OnlyEntries(entries.GetRange(1, 1));
        Equal(1, rest.Count);
        Equal(entries[1].Signature, rest.Entries()[0].Signature);
        Equal(InstallErrorCodes.Exception, rest.ErrorCode);
        // At most MaxLines per kind.
        var many = new InstallErrorReport { Product = "Prod" };
        for (var i = 0; i < 100; i++) many.AddOther("line " + i);
        Equal(InstallErrorReport.MaxLines, many.Count);

        // N6: an error with the same signature but other text (other digits) that joined while another was sending goes with it.
        var waiting = new InstallErrorReport { Product = "Prod", ProductId = "lic-1", Version = "1.0", BaseCode = InstallErrorCodes.Exception };
        waiting.AddOther("Exception: request 1 failed");
        waiting.AddOther("Exception: request 2 failed");
        waiting.AddOther("Exception: disk full");
        var shot = waiting.TakeSnapshot(20 * 1024, 4000, Ctx);
        var sent = new HashSet<string>(shot.Included.Select(e => e.Signature).Take(1));
        waiting.AddOther("Exception: request 3 failed");
        Require(!waiting.RemoveSigned(sent.Contains));
        Equal(1, waiting.Count);
        Equal("Exception: disk full", waiting.Entries()[0].Text);
        Require(waiting.RemoveSigned(s => true));
    }

    private static void CardIdentity()
    {
        // N3: two cards of the same product and version are two cards; a snapshot is one card's, at one revision.
        var a = new InstallErrorReport { Product = "Prod", Version = "1.0" };
        a.AddOther("Exception: first");
        var b = new InstallErrorReport { Product = "Prod", Version = "1.0" };
        b.AddOther("Exception: second");
        Require(a.CardId.Length > 0 && a.CardId != b.CardId);
        Require(a.SameProduct(b));
        Equal(a.Revision, b.Revision);
        var shotA = a.TakeSnapshot(20 * 1024, 4000, Ctx);
        Require(shotA.BelongsTo(a));
        Require(!shotA.BelongsTo(b));
        Require(!shotA.BelongsTo(null));
        a.AddOther("Exception: third");
        Require(!shotA.BelongsTo(a));
        Require(a.TakeSnapshot(20 * 1024, 4000, Ctx).BelongsTo(a));
        // A copy for the waiting list is a card of its own.
        Require(a.OnlyEntries(a.Entries()).CardId != a.CardId);
        // The snapshot shows redacted text only.
        var secret = new InstallErrorReport { Product = "Prod a@b.io", Version = "1.0", Reason = "token=abc123 failed" };
        secret.AddOther("token=abc123 failed at C:\\Users\\zmnbn\\x.cs");
        var shot = secret.TakeSnapshot(20 * 1024, 4000, Ctx);
        Equal("Prod <email>", shot.ProductShown);
        Equal("token=<redacted> failed", shot.Headline);
        Equal("token=<redacted> failed at <path>/x.cs", shot.Excerpt);
        Require(shot.Summary.IndexOf("abc123", StringComparison.Ordinal) < 0 && shot.Summary.IndexOf("a@b.io", StringComparison.Ordinal) < 0);
    }

    private static PackageAsset F(string path) { return new PackageAsset { Path = path }; }
    private static PackageAsset D(string path) { return new PackageAsset { Path = path, IsFolder = true }; }

    private static void PathAttribution()
    {
        // A folder the import creates is the product's; folders that were already there are not.
        var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets", "Assets/StudioRaming_Onairdeck" };
        var prefixes = InstallErrorLogic.OwnedPaths(new[]
        {
            D("Assets"), D("Assets/StudioRaming_Onairdeck"), D("Assets/StudioRaming_Onairdeck/Prod"),
            D("Assets/StudioRaming_Onairdeck/Prod/Editor"), F("Assets/StudioRaming_Onairdeck/Prod/Editor/Tool.cs"),
            F("Assets/StudioRaming_Onairdeck/Prod/Runtime/Core.dll"), F("Assets/Loose.cs"),
        }, existed.Contains);
        Equal(4, prefixes.Count);
        Require(prefixes.Contains("Assets/Loose.cs") && prefixes.Contains("Assets/Loose.cs.meta"));
        Require(prefixes.Contains("Assets/StudioRaming_Onairdeck/Prod") && prefixes.Contains("Assets/StudioRaming_Onairdeck/Prod.meta"));

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

        // Whole segments: com.vendor.tool is not com.vendor.tool.extra.
        var package = InstallErrorLogic.OwnedPaths(new[] { D("Packages/com.vendor.tool") }, x => x == "Packages");
        Require(package.Contains("Packages/com.vendor.tool"));
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

    private static Action OwnedPathsAndArchive(string project)
    {
        return () =>
        {
            var existed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Assets", "Assets/Editor" };

            // N8: a folder entry with no children in the package that already exists owns nothing, and neither does a file beside it.
            var childless = InstallErrorLogic.OwnedPaths(new[] { D("Assets/Editor"), F("Assets/Prod/Core.cs"), D("Assets/Prod") }, existed.Contains);
            Require(!childless.Contains("Assets/Editor"));
            Require(!InstallErrorLogic.IsUnder("Assets/Editor/MyTool.cs", childless));
            Require(!InstallErrorLogic.MentionsPath("Assets/Editor/MyTool.cs(1,1): error CS1002", childless));
            Require(childless.Contains("Assets/Prod"));
            // A new folder owns what is below it, children or not.
            var created = InstallErrorLogic.OwnedPaths(new[] { D("Assets/New") }, existed.Contains);
            Require(created.Contains("Assets/New") && InstallErrorLogic.IsUnder("Assets/New/Any.cs", created));

            // One file into a shared, existing folder owns that file and its .meta, not its neighbours (N9).
            var single = InstallErrorLogic.OwnedPaths(new[] { D("Assets"), D("Assets/Editor"), F("Assets/Editor/Tool.cs") }, existed.Contains);
            Equal(2, single.Count);
            Require(InstallErrorLogic.IsUnder("Assets/Editor/Tool.cs", single));
            Require(InstallErrorLogic.IsUnder("Assets/Editor/Tool.cs.meta", single));
            Require(!InstallErrorLogic.IsUnder("Assets/Editor/Other.cs", single));
            Require(!InstallErrorLogic.IsUnder("Assets/Editor/Other.cs.meta", single));
            Require(!InstallErrorLogic.IsUnder("Assets/Editor/Tool.cs.meta.bak", single));
            Require(InstallErrorLogic.MentionsPath("Assets/Editor/Tool.cs.meta: GUID conflict", single));
            Require(!InstallErrorLogic.MentionsPath("Assets/Editor/Other.cs(3,4): error CS1002", single));
            Require(InstallErrorLogic.MentionsPath("Assets/Editor/Tool.cs(3,4): error CS1002", single));

            // The archive tells a folder by its meta (folderAsset: yes), not by what is below it.
            Directory.CreateDirectory(project);
            var path = Path.Combine(project, "owned-" + Guid.NewGuid().ToString("N") + ".unitypackage");
            var entries = new List<KeyValuePair<string, byte[]>>();
            Func<string, string, KeyValuePair<string, byte[]>> entry = (name, text) => new KeyValuePair<string, byte[]>(name, Encoding.UTF8.GetBytes(text));
            entries.Add(entry("aaaa/pathname", "Assets/Editor"));
            entries.Add(entry("aaaa/asset.meta", "fileFormatVersion: 2\nguid: aaaa\nfolderAsset: yes\nDefaultImporter:\n"));
            entries.Add(entry("bbbb/pathname", "Assets\\Prod\\Core.cs"));
            entries.Add(entry("bbbb/asset", "class Core {}"));
            entries.Add(entry("bbbb/asset.meta", "fileFormatVersion: 2\r\nguid: bbbb\r\nMonoImporter:\r\n"));
            using (var file = File.Create(path))
            using (var gz = new GZipStream(file, CompressionMode.Compress))
            {
                var bytes = LegacyChecks.Tar(entries.ToArray());
                gz.Write(bytes, 0, bytes.Length);
            }
            var assets = UnityPackageArchive.Assets(path);
            Require(assets != null && assets.Count == 2);
            Equal("Assets/Editor", assets[0].Path);
            Require(assets[0].IsFolder);
            Equal("Assets/Prod/Core.cs", assets[1].Path);
            Require(!assets[1].IsFolder);
        };
    }

    private static void ExcerptOrderAndCap()
    {
        var report = new InstallErrorReport { Product = "Prod", Version = "1.0" };
        report.AddOther("Exception: boom in C:\\Users\\zmnbn\\proj");
        report.AddCompile("Assets/Prod/A.cs(1,1): error CS1002: ; expected");
        report.AddCompile("Assets/Prod/B.cs(2,2): error CS0103: no x");
        var excerpt = report.BuildExcerpt(20 * 1024, Ctx);
        var text = excerpt.Text;
        var compileA = text.IndexOf("A.cs", StringComparison.Ordinal);
        var compileB = text.IndexOf("B.cs", StringComparison.Ordinal);
        var exception = text.IndexOf("Exception: boom", StringComparison.Ordinal);
        Require(compileA >= 0 && compileA < compileB && compileB < exception);
        Equal(3, excerpt.Included.Count);
        Require(text.IndexOf("zmnbn", StringComparison.Ordinal) < 0);
        Equal(text, report.BuildExcerpt(20 * 1024, Ctx).Text);
        Equal("Prod: Assets/Prod/A.cs(1,1): error CS1002: ; expected", ReportWithVersion(report, ""));
        Equal("Prod 1.0: Assets/Prod/A.cs(1,1): error CS1002: ; expected", ReportWithVersion(report, "1.0"));

        // N5: the cap keeps whole entries from the front and only those are 'included'; the rest are not marked sent.
        var one = Encoding.UTF8.GetByteCount("Assets/Prod/A.cs(1,1): error CS1002: ; expected");
        var capped = report.BuildExcerpt(one + 4, Ctx);
        Equal("Assets/Prod/A.cs(1,1): error CS1002: ; expected", capped.Text);
        Equal(1, capped.Included.Count);
        var shot = report.TakeSnapshot(one + 4, 4000, Ctx);
        Equal(1, shot.Included.Count);
        var includedSignatures = new HashSet<string>(shot.Included.Select(e => e.Signature));
        Require(!report.RemoveSigned(includedSignatures.Contains));
        Equal(2, report.Count);

        // A single entry longer than the cap is cut, never longer than the cap, never splitting a character.
        var lone = new InstallErrorReport { Product = "Prod" };
        lone.AddOther(new string('가', 5000));
        var cut = lone.BuildExcerpt(1000, Ctx);
        Require(Encoding.UTF8.GetByteCount(cut.Text) <= 1000);
        Require(cut.Text.Length > 0);
        Equal(1, cut.Included.Count);

        // The text is redacted whole before it is cut: a secret that straddles the cap is gone, not left as a 10-character stub.
        var straddle = new InstallErrorReport { Product = "Prod" };
        straddle.AddOther("prefix a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3");
        var kept = straddle.BuildExcerpt(Encoding.UTF8.GetByteCount("prefix ") + 10, Ctx);
        Equal("prefix <token>", kept.Text);
        var cutFirst = FeedbackText.Redact(FeedbackText.CutToBytes("prefix a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3", Encoding.UTF8.GetByteCount("prefix ") + 10), Ctx);
        Require(cutFirst.IndexOf("a1b2c3d4e5", StringComparison.Ordinal) >= 0);
    }

    private static string ReportWithVersion(InstallErrorReport report, string version)
    {
        var copy = new InstallErrorReport { Product = report.Product, Version = version };
        copy.Merge(report);
        return copy.AutoSummary(4000, Ctx);
    }

    private static void WatchDeadlines()
    {
        var t0 = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;
        var state = InstallWatchState.Begin("Prod", "lic-1", "1.0", "job", new[] { "Assets/Prod" }, t0);
        var job = state.Job("job");
        Require(state.Job("job").IsActive(t0 + 30 * minute));

        // The compile is what ends it: a compile-finished event before any compile started changes nothing.
        state.CompileFinished(t0 + 5 * minute);
        Require(!job.CompileDone);
        Require(state.MarkCompileStarted());
        Require(!state.MarkCompileStarted());
        state.CompileFinished(t0 + 5 * minute);
        Require(job.CompileDone);
        Require(state.Job("job").IsActive(t0 + 7 * minute));
        Require(!state.Job("job").IsActive(t0 + 7 * minute + 1));
        // A finished import does not shorten a watch whose compile already ended.
        state.ImportFinished("job", t0 + 6 * minute);
        Require(state.Job("job").IsActive(t0 + 7 * minute));

        // The deadline is fixed by the first compile; a later compile does not extend it.
        state.CompileFinished(t0 + 6 * minute);
        Require(!state.Job("job").IsActive(t0 + 7 * minute + 1));

        // A compile that is still running is waited for (past the no-script cap) up to the hard cap, then two minutes after it.
        var slow = InstallWatchState.Begin("Prod", "lic-1", "1.0", "job", new[] { "Assets/Prod" }, t0);
        slow.MarkCompileStarted();
        slow.ImportFinished("job", t0 + minute);
        Require(slow.Job("job").IsActive(t0 + 30 * minute));
        Require(!slow.Job("job").IsActive(t0 + 61 * minute));
        slow.CompileFinished(t0 + 30 * minute);
        Require(slow.Job("job").IsActive(t0 + 32 * minute) && !slow.Job("job").IsActive(t0 + 32 * minute + 1));

        // A compile the domain reload swallowed counts as finished.
        var reloaded = InstallWatchState.Begin("Prod", "lic-1", "1.0", "job", new[] { "Assets/Prod" }, t0);
        Require(!reloaded.CompileLostToReload(t0 + minute));
        reloaded.MarkCompileStarted();
        Require(reloaded.CompileLostToReload(t0 + minute));
        Require(reloaded.Job("job").CompileDone);

        var noScripts = InstallWatchState.Begin("Prod", "lic-1", "1.0", "job", new[] { "Assets/Prod" }, t0);
        noScripts.ImportFinished("job", t0 + minute);
        Require(noScripts.Job("job").IsActive(t0 + 10 * minute));
        Require(!noScripts.Job("job").IsActive(t0 + 12 * minute));
    }

    private static void WatchJobs()
    {
        var t0 = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc).Ticks;
        var minute = TimeSpan.FromMinutes(1).Ticks;

        // N7: job A (done compiling) and job B share a path. Adding B does not reset A; removing B takes only B's part.
        var watch = InstallWatchState.Begin("Prod", "lic-1", "1.0", "A", new[] { "Assets/Shared/Core.cs", "Assets/Shared/Core.cs.meta" }, t0);
        watch.MarkCompileStarted();
        watch.CompileFinished(t0 + 5 * minute);
        watch.AddJob("B", new[] { "Assets/Shared/Core.cs", "Assets/Shared/Addon.cs" }, t0 + 6 * minute);
        Require(watch.Job("A").CompileDone);
        Require(watch.Job("A").IsActive(t0 + 7 * minute) && !watch.Job("A").IsActive(t0 + 7 * minute + 1));
        Require(!watch.Job("B").CompileStarted && !watch.Job("B").CompileDone);
        Equal(3, watch.Prefixes.Count);
        Require(!watch.RemoveJob("B"));
        Equal(2, watch.Prefixes.Count);
        Require(watch.Prefixes.Contains("Assets/Shared/Core.cs") && !watch.Prefixes.Contains("Assets/Shared/Addon.cs"));
        Require(watch.Job("A").CompileDone);
        Require(!watch.Job("A").IsActive(t0 + 7 * minute + 1));

        // The scope is the union of the jobs still active: once A is over only B's paths are left.
        var two = InstallWatchState.Begin("Prod", "lic-1", "1.0", "A", new[] { "Assets/A" }, t0);
        two.MarkCompileStarted();
        two.CompileFinished(t0 + 5 * minute);
        two.AddJob("B", new[] { "Assets/B" }, t0 + 6 * minute);
        Require(!two.Prune(t0 + 7 * minute));
        Require(two.Prune(t0 + 8 * minute));
        Equal(1, two.Jobs.Count);
        Equal("Assets/B", two.Prefixes[0]);
        Require(two.Job("B").IsActive(t0 + 30 * minute));
        // The watch ends when its last job does.
        Require(two.RemoveJob("B"));
        Equal(0, two.Jobs.Count);

        // The same job id starts over, the others stay.
        var again = InstallWatchState.Begin("Prod", "lic-1", "1.0", "A", new[] { "Assets/A" }, t0);
        again.AddJob("B", new[] { "Assets/B" }, t0);
        again.MarkCompileStarted();
        again.AddJob("A", new[] { "Assets/A2" }, t0 + minute);
        Require(!again.Job("A").CompileStarted && again.Job("B").CompileStarted);
        Equal(2, again.Jobs.Count);
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
