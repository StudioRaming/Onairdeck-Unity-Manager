using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// Package install (Editor/Packages/*.cs, the pure file-system half): zip validation, SHA-256 and HTTPS
// gates, dependency listings, the install transaction with undo and crash recovery, the vpm-manifest
// edit, the legacy refusal and the mismatch finder. The real sources are compiled into this program and
// run on temp folders. No network, no Unity.
internal static class PackageChecks
{
    private const string Core = "com.studioraming.mingtoon";
    private const string Addon = "com.studioraming.mingtoon.urp";
    private static readonly DateTime Now = new DateTime(2026, 10, 25, 12, 0, 0);
    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "packages");
        Directory.CreateDirectory(_root);

        check("package versions: exact form, ranges refused, semver order", VersionRules);
        check("zip: valid package returns its manifest and extracts", ZipValid);
        check("zip: traversal, rooted, drive and backslash entries fail the whole package", ZipUnsafeEntries);
        check("zip: sanitized names, duplicate-by-case entries and a bad zip fail", ZipOddEntries);
        check("zip: missing package.json, name mismatch, version mismatch", ZipIdentity);
        check("zip: range dependency fails, exact dependencies are read", ZipDependencies);
        check("SHA-256: match passes, mismatch and empty fail; HTTPS only", HashAndHttps);
        check("VPM listing: exact version lookup with url and hash", ListingLookup);
        check("transaction: dependency and add-on installed, backup kept, no carry-over, journal complete", TransactionSuccess);
        check("transaction: failure at every step undoes everything (in process)", FailEverywhere);
        check("transaction: crash at every step is rolled back on the next load", CrashEverywhere);
        check("transaction: a rollback that fails is retried by recovery", RollbackRetry);
        check("transaction: an interrupted install blocks the next one until rolled back", InterruptedBlocksNext);
        check("transaction: target outside Packages and bad names are refused", TargetGuards);
        check("transaction: self-update style item replaces a differently named folder", CustomTarget);
        check("vpm-manifest: existing entries only, unknown keys kept, backup written", ManifestUpdate);
        check("vpm-manifest: nothing to update leaves the file alone; CRLF kept; bad JSON fails before any move", ManifestEdgeCases);
        check("legacy Assets install is refused and nothing changes", LegacyRefusal);
        check("journal paths outside the project are refused on recovery", JournalTamper);
        check("mismatch finder and target choice", Mismatches);
        check("built package zip passes the same validation (ONAIRDECK_PACKAGING_ZIP)", BuiltZip);
    }

    // ---------- versions, zip, hash ----------

    private static void VersionRules()
    {
        Require(PackageVersion.IsExact("0.1.16") && PackageVersion.IsExact("1.0.0-beta.2"));
        foreach (var bad in new[] { "^0.1.16", "~1.0.0", ">=1.0.0", "1.0", "1.0.0 ", "1.0.0\n", "v1.0.0", "*", "", null, "1.0.x" })
            Require(!PackageVersion.IsExact(bad));
        Require(PackageVersion.Compare("0.1.16", "0.1.15") > 0);
        Require(PackageVersion.Compare("0.1.9", "0.1.10") < 0);
        Require(PackageVersion.Compare("1.0.0-beta.2", "1.0.0") < 0);
        Require(PackageVersion.Compare("1.0.0-beta.2", "1.0.0-beta.10") < 0);
        Require(PackageVersion.Compare("1.0.0-alpha", "1.0.0-beta") < 0);
        Equal(0, PackageVersion.Compare("2.0.0", "2.0.0"));
    }

    private static void ZipValid()
    {
        var zip = MakePackageZip(NewDir(), Core, "0.1.16", null, "Runtime/A.cs", "Runtime/A.cs.meta", "__MACOSX/._A.cs");
        var manifest = PackageZip.Validate(zip, Core, "0.1.16");
        Equal(Core, manifest.Name);
        Equal("0.1.16", manifest.Version);
        var destination = Path.Combine(NewDir(), "out");
        PackageZip.Extract(zip, destination, Core, "0.1.16");
        Equal("Runtime/A.cs", File.ReadAllText(Path.Combine(destination, "Runtime", "A.cs")));
        Require(!Directory.Exists(Path.Combine(destination, "__MACOSX")));
        Require(File.Exists(Path.Combine(destination, "package.json")));
    }

    private static void ZipUnsafeEntries()
    {
        foreach (var evil in new[] { "../escape.txt", "nested/../../escape.txt", "..\\escape.txt", "/absolute.txt", "\\absolute.txt", "C:/drive.txt", "C:\\drive.txt", "../" })
        {
            var dir = NewDir();
            var zip = MakePackageZip(dir, Core, "0.1.16", null, "Runtime/A.cs", evil);
            var destination = Path.Combine(dir, "out");
            Throws<PackageInstallException>(() => PackageZip.Validate(zip, Core, "0.1.16"), "Unsafe entry", evil);
            Throws<PackageInstallException>(() => PackageZip.Extract(zip, destination, Core, "0.1.16"), "Unsafe entry", evil);
            // The good entries before the bad one must not have been written either.
            Require(!Directory.Exists(destination));
            Require(!File.Exists(Path.Combine(dir, "escape.txt")));
        }
    }

    private static void ZipOddEntries()
    {
        // PathSafety would rewrite these names (reserved character, trailing dot); for a package any rewrite is a rejection.
        foreach (var odd in new[] { "Runtime/ctl.cs", "Runtime/trailing.", "Runtime/ spaced.cs " })
        {
            var zip = MakePackageZip(NewDir(), Core, "0.1.16", null, odd);
            Throws<PackageInstallException>(() => PackageZip.Validate(zip, Core, "0.1.16"), "Unsafe entry", odd);
        }
        var dup = MakePackageZip(NewDir(), Core, "0.1.16", null, "Runtime/A.cs", "runtime/a.cs");
        Throws<PackageInstallException>(() => PackageZip.Validate(dup, Core, "0.1.16"), "Duplicate entry", "case duplicate");

        var notZip = Path.Combine(NewDir(), "x.zip");
        File.WriteAllText(notZip, "this is not a zip");
        Throws<PackageInstallException>(() => PackageZip.Validate(notZip, Core, "0.1.16"), "not a valid zip", "garbage");
    }

    private static void ZipIdentity()
    {
        var dir = NewDir();
        var noJson = MakeRawZip(dir, "nojson.zip", new Dictionary<string, string> { { "Runtime/A.cs", "x" } });
        Throws<PackageInstallException>(() => PackageZip.Validate(noJson, Core, "0.1.16"), "no package.json", "missing package.json");

        var good = MakePackageZip(dir, Core, "0.1.16", null, "Runtime/A.cs");
        Throws<PackageInstallException>(() => PackageZip.Validate(good, Addon, "0.1.16"), "declares the name", "name mismatch");
        Throws<PackageInstallException>(() => PackageZip.Validate(good, Core, "0.1.17"), "declares version", "version mismatch");

        var nested = MakeRawZip(dir, "nested.zip", new Dictionary<string, string> { { "pkg/package.json", PackageJson(Core, "0.1.16", null) } });
        Throws<PackageInstallException>(() => PackageZip.Validate(nested, Core, "0.1.16"), "no package.json", "package.json not at the root");

        var broken = MakeRawZip(dir, "broken.zip", new Dictionary<string, string> { { "package.json", "{ not json" } });
        Throws<PackageInstallException>(() => PackageZip.Validate(broken, Core, "0.1.16"), "not a valid JSON", "broken package.json");
    }

    private static void ZipDependencies()
    {
        foreach (var range in new[] { "^0.1.16", "~0.1.16", ">=0.1.16", "0.1", "*", "latest" })
        {
            var zip = MakePackageZip(NewDir(), Addon, "0.1.16", new[] { Core + "=" + range }, "Editor/B.cs");
            Throws<PackageInstallException>(() => PackageZip.Validate(zip, Addon, "0.1.16"), "exact version", range);
        }
        var ok = MakePackageZip(NewDir(), Addon, "0.1.16", new[] { Core + "=0.1.16" }, "Editor/B.cs");
        var manifest = PackageZip.Validate(ok, Addon, "0.1.16");
        Equal(1, manifest.VpmDependencies.Count);
        Equal(Core, manifest.VpmDependencies[0].Key);
        Equal("0.1.16", manifest.VpmDependencies[0].Value);

        // An installed package the Manager did not validate may carry a range: read leniently, range skipped.
        var lenient = PackageZip.ParseManifest(PackageJson(Addon, "0.1.1", new[] { Core + "=^0.1.0" }), false);
        Equal(0, lenient.VpmDependencies.Count);
    }

    private static void HashAndHttps()
    {
        var file = Path.Combine(NewDir(), "a.zip");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 4 });
        string expected;
        using (var sha = SHA256.Create()) expected = string.Concat(sha.ComputeHash(new byte[] { 1, 2, 3, 4 }).Select(b => b.ToString("x2")));
        Equal(expected, PackageDownloadChecks.Sha256Hex(file));
        PackageDownloadChecks.RequireSha256(file, expected, "x");
        PackageDownloadChecks.RequireSha256(file, expected.ToUpperInvariant(), "x"); // hex case does not matter
        Throws<PackageInstallException>(() => PackageDownloadChecks.RequireSha256(file, new string('0', 64), "core"), "SHA-256", "mismatch");
        Throws<PackageInstallException>(() => PackageDownloadChecks.RequireSha256(file, "", "core"), "no SHA-256", "empty hash");
        File.WriteAllBytes(file, new byte[] { 1, 2, 3, 5 });
        Throws<PackageInstallException>(() => PackageDownloadChecks.RequireSha256(file, expected, "core"), "SHA-256", "changed file");

        PackageDownloadChecks.RequireHttps("https://example.com/a.zip");
        foreach (var bad in new[] { "http://example.com/a.zip", "file:///C:/a.zip", "ftp://x/a.zip", "", null, "not a url", "//example.com/a.zip" })
            Throws<PackageInstallException>(() => PackageDownloadChecks.RequireHttps(bad), "HTTPS", bad ?? "null");
    }

    private static void ListingLookup()
    {
        var json = "{\"name\":\"x\",\"packages\":{\"" + Core + "\":{\"versions\":{"
            + "\"0.1.16\":{\"name\":\"" + Core + "\",\"version\":\"0.1.16\",\"url\":\"https://example.com/0.1.16.zip\",\"zipSHA256\":\"AB12\"},"
            + "\"0.1.15\":{\"url\":\"https://example.com/0.1.15.zip\"}}}}}";
        var listing = VpmListing.Parse(json);
        var hit = listing.Find(Core, "0.1.16");
        Require(hit != null);
        Equal("https://example.com/0.1.16.zip", hit.Url);
        Equal("AB12", hit.Sha256);
        Equal("", listing.Find(Core, "0.1.15").Sha256);
        Require(listing.Find(Core, "0.1.17") == null);   // exact version only, no "nearest"
        Require(listing.Find(Addon, "0.1.16") == null);
        Throws<PackageInstallException>(() => VpmListing.Parse("[]"), "not valid JSON", "array root");
        Require(VpmListing.Parse("{}").Find(Core, "0.1.16") == null);
    }

    // ---------- transaction ----------

    private static void TransactionSuccess()
    {
        var project = NewProject(true);
        var items = StandardItems(project);
        var report = PackageTransaction.Execute(project, items, Now);

        Equal(2, report.Installed.Count);
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Core));
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Addon));
        Equal("Editor/B.cs", File.ReadAllText(Path.Combine(project, "Packages", Addon, "Editor", "B.cs")));

        // Old core: moved to the backup, extra files are not carried over, and the report says so.
        var core = report.Installed[0];
        Equal("0.1.15", core.OldVersion);
        Equal(Path.Combine(project, "Library", "OnAirDeck", "Backup", Core + "-0.1.15-20261025-120000"), core.Backup);
        Equal("legacy", File.ReadAllText(Path.Combine(core.Backup, "Legacy", "Old.cs")));
        Require(!Directory.Exists(Path.Combine(project, "Packages", Core, "Legacy")));
        Require(core.NotCarriedOver.Contains("Legacy/Old.cs") && core.NotCarriedOver.Contains("Legacy/Old.cs.meta"));
        Require(!core.NotCarriedOver.Contains("Runtime/A.cs.meta")); // the new package has it
        Require(report.Describe().Contains("Legacy/Old.cs") && report.Describe().Contains(core.Backup));
        Equal("", report.Installed[1].Backup); // add-on was not installed before

        Equal("complete", JournalState(project));
        Equal(0, StageFolders(project));
        // A repeat of the same install: the second backup does not collide with the first.
        var again = PackageTransaction.Execute(project, StandardItems(project), Now);
        Require(again.Installed[0].Backup != core.Backup && Directory.Exists(core.Backup) && Directory.Exists(again.Installed[0].Backup));
        var third = PackageTransaction.Execute(project, StandardItems(project), Now);
        Require(third.Installed[0].Backup.EndsWith("-2") && Directory.Exists(again.Installed[0].Backup));
    }

    private static void FailEverywhere()
    {
        var points = RecordPoints();
        Require(points.Contains("after-install:" + Core) && points.Contains("before-manifest") && points.Contains("stage:" + Addon));
        foreach (var point in points)
        {
            var project = NewManifestProject();
            var before = Snapshot(project);
            var items = StandardItems(project);
            PackageTransaction.FaultHook = p => { if (p == point) throw new IOException("injected at " + p); };
            try
            {
                Throws<PackageInstallException>(() => PackageTransaction.Execute(project, items, Now), "injected at " + point, point);
            }
            finally { PackageTransaction.FaultHook = null; }
            Equal(before, Snapshot(project));
            Equal(0, StageFolders(project));
            var state = JournalState(project);
            Require(state == null || state == "rolledback");
            // The project is usable afterwards: the same install now succeeds.
            PackageTransaction.Execute(project, StandardItems(project), Now);
            Equal("0.1.16", InstalledPackages.InstalledVersion(project, Addon));
        }
    }

    private static void CrashEverywhere()
    {
        foreach (var point in RecordPoints())
        {
            var project = NewManifestProject();
            var before = Snapshot(project);
            var items = StandardItems(project);
            // Before the journal exists nothing is recoverable, and Unity empties Temp on the next launch.
            var beforeJournal = point.StartsWith("stage:") || point == "journal";
            PackageTransaction.FaultHook = p => { if (p == point) throw new TransactionCrash(p); };
            try
            {
                Throws<TransactionCrash>(() => PackageTransaction.Execute(project, items, Now), "simulated crash", point);
            }
            finally { PackageTransaction.FaultHook = null; }

            var message = PackageTransaction.Recover(project);   // what the next editor load does
            Equal(before, Snapshot(project));
            if (!beforeJournal) Equal(0, StageFolders(project));
            if (File.Exists(PackageTransaction.JournalPath(project)))
            {
                Require(message != null && message.Contains("rolled back"));
                Equal("rolledback", JournalState(project));
            }
            Require(PackageTransaction.Recover(project) == null);  // nothing left to do
        }
    }

    private static void RollbackRetry()
    {
        var project = NewManifestProject();
        var before = Snapshot(project);
        var items = StandardItems(project);
        // Fail after both swaps, then fail the undo of the add-on too.
        PackageTransaction.FaultHook = p =>
        {
            if (p == "before-manifest") throw new IOException("boom");
            if (p == "undo:" + Addon) throw new IOException("undo blocked");
        };
        try
        {
            Throws<PackageInstallException>(() => PackageTransaction.Execute(project, items, Now), "could not finish", "undo failure");
        }
        finally { PackageTransaction.FaultHook = null; }
        Equal("applying", JournalState(project)); // stays open for the next load
        var message = PackageTransaction.Recover(project);
        Require(message != null);
        Equal(before, Snapshot(project));
    }

    private static void InterruptedBlocksNext()
    {
        var project = NewProject(true);
        var before = Snapshot(project);
        PackageTransaction.FaultHook = p => { if (p == "after-backup:" + Core) throw new TransactionCrash(p); };
        try { Throws<TransactionCrash>(() => PackageTransaction.Execute(project, StandardItems(project), Now), "crash", "setup"); }
        finally { PackageTransaction.FaultHook = null; }

        Throws<PackageInstallException>(() => PackageTransaction.Execute(project, StandardItems(project), Now), "interrupted", "blocked install");
        Equal(before, Snapshot(project));                    // the blocked attempt rolled back first, changing nothing else
        PackageTransaction.Execute(project, StandardItems(project), Now);   // and the retry works
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Core));
    }

    private static void TargetGuards()
    {
        var project = NewProject(true);
        var before = Snapshot(project);
        var zip = MakePackageZip(NewDir(), Core, "0.1.16", null, "Runtime/A.cs");

        var outside = Path.Combine(project, "Assets", "Elsewhere");
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project,
            new[] { new InstallItem { Name = Core, Version = "0.1.16", ZipPath = zip, TargetFolder = outside } }, Now), "outside the project's Packages", "target outside");
        var sibling = Path.Combine(project, "Packages-evil", Core);
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project,
            new[] { new InstallItem { Name = Core, Version = "0.1.16", ZipPath = zip, TargetFolder = sibling } }, Now), "outside the project's Packages", "sibling prefix");
        foreach (var badName in new[] { "..", "../x", "Com.Upper.Case", "nodot", "a.b/c", "a.b\\c", "" })
            Throws<PackageInstallException>(() => PackageTransaction.Execute(project,
                new[] { new InstallItem { Name = badName, Version = "0.1.16", ZipPath = zip } }, Now), "Not a valid package name", badName);
        Equal(before, Snapshot(project));
        Equal(0, StageFolders(project));
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project, new InstallItem[0], Now), "Nothing to install", "empty");
    }

    private static void CustomTarget()
    {
        var project = NewProject(false);
        var folder = Path.Combine(project, "Packages", "OnAirDeck-Unity-Manager"); // a dev-style folder name
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), PackageJson("com.studioraming.manager", "0.4.0", null));
        File.WriteAllText(Path.Combine(folder, "Old.txt"), "old");
        var zip = MakePackageZip(NewDir(), "com.studioraming.manager", "0.5.0", null, "Editor/New.cs");
        var report = PackageTransaction.Execute(project, new[] { new InstallItem { Name = "com.studioraming.manager", Version = "0.5.0", ZipPath = zip, TargetFolder = folder } }, Now);
        Equal("0.4.0", report.Installed[0].OldVersion);
        Require(File.Exists(Path.Combine(folder, "Editor", "New.cs")) && !File.Exists(Path.Combine(folder, "Old.txt")));
        Require(!Directory.Exists(Path.Combine(project, "Packages", "com.studioraming.manager")));
        Equal("old", File.ReadAllText(Path.Combine(report.Installed[0].Backup, "Old.txt")));
    }

    // ---------- vpm-manifest ----------

    private const string VpmManifestText = "{\n"
        + "  \"dependencies\": {\n"
        + "    \"" + Core + "\": { \"version\": \"^0.1.15\", \"note\": \"keep me\" },\n"
        + "    \"com.vrchat.avatars\": { \"version\": \"3.7.0\" }\n"
        + "  },\n"
        + "  \"locked\": {\n"
        + "    \"" + Core + "\": { \"version\": \"0.1.15\", \"dependencies\": {}, \"extra\": 5 },\n"
        + "    \"com.vrchat.avatars\": { \"version\": \"3.7.0\", \"dependencies\": { \"com.vrchat.base\": \"3.7.0\" } }\n"
        + "  },\n"
        + "  \"customKey\": { \"list\": [1, 2.5, true, null, \"x\\ny\", {\"k\": []}], \"nested\": { \"a\": \"b\" }, \"empty\": {} },\n"
        + "  \"unityVersion\": \"2022.3.22f1\"\n"
        + "}\n";

    private static void ManifestUpdate()
    {
        var project = NewProject(true);
        var path = Path.Combine(project, "Packages", "vpm-manifest.json");
        File.WriteAllText(path, VpmManifestText.Replace("\"extra\": 5 }", "\"extra\": 5 },\n    \"" + Addon + "\": { \"version\": \"0.1.15\", \"dependencies\": { \"" + Core + "\": \"0.1.15\" } }"));
        var original = File.ReadAllText(path);
        var originalParsed = MiniJson.ParseObject(original);

        var report = PackageTransaction.Execute(project, StandardItems(project), Now);
        Require(report.ManifestUpdated);
        Equal(original, File.ReadAllText(report.ManifestBackup));                       // backed up first, byte for byte
        Require(report.ManifestBackup.StartsWith(Path.Combine(project, "Library", "OnAirDeck", "Backup"), StringComparison.OrdinalIgnoreCase));

        var after = MiniJson.ParseObject(File.ReadAllText(path));
        Require(after != null);
        var deps = Obj(after, "dependencies");
        var locked = Obj(after, "locked");
        Equal("0.1.16", (string)Obj(deps, Core)["version"]);
        Equal("keep me", (string)Obj(deps, Core)["note"]);                              // unknown key in an updated entry
        Equal("0.1.16", (string)Obj(locked, Core)["version"]);
        Equal(5L, Obj(locked, Core)["extra"]);
        Equal("0.1.16", (string)Obj(locked, Addon)["version"]);
        Equal("0.1.16", (string)Obj(Obj(locked, Addon), "dependencies")[Core]);         // dependencies follow vpmDependencies
        Require(!deps.ContainsKey(Addon));                                              // never added to "dependencies"

        // Everything else is untouched, including values JsonUtility could not carry.
        Require(DeepEqual(originalParsed["customKey"], after["customKey"]));
        Require(DeepEqual(originalParsed["unityVersion"], after["unityVersion"]));
        Require(DeepEqual(Obj(Obj(originalParsed, "dependencies"), "com.vrchat.avatars"), Obj(deps, "com.vrchat.avatars")));
        Require(DeepEqual(Obj(Obj(originalParsed, "locked"), "com.vrchat.avatars"), Obj(locked, "com.vrchat.avatars")));
        Equal(string.Join(",", originalParsed.Keys.ToArray()), string.Join(",", after.Keys.ToArray())); // key order kept

        // The add-on had no entry here: a fresh project file gets no new entries at all.
        var fresh = NewProject(true);
        File.WriteAllText(Path.Combine(fresh, "Packages", "vpm-manifest.json"), VpmManifestText);
        PackageTransaction.Execute(fresh, StandardItems(fresh), Now);
        var freshAfter = MiniJson.ParseObject(File.ReadAllText(Path.Combine(fresh, "Packages", "vpm-manifest.json")));
        Require(!Obj(freshAfter, "dependencies").ContainsKey(Addon) && !Obj(freshAfter, "locked").ContainsKey(Addon));
        Equal("0.1.16", (string)Obj(Obj(freshAfter, "dependencies"), Core)["version"]);
    }

    private static void ManifestEdgeCases()
    {
        // No entry for any installed package: file untouched, no backup, report says so.
        var project = NewProject(true);
        var path = Path.Combine(project, "Packages", "vpm-manifest.json");
        var untouched = "{\"dependencies\":{\"com.vrchat.avatars\":{\"version\":\"3.7.0\"}},\"locked\":{}}";
        File.WriteAllText(path, untouched);
        var report = PackageTransaction.Execute(project, StandardItems(project), Now);
        Require(!report.ManifestUpdated && report.ManifestBackup == "");
        Equal(untouched, File.ReadAllText(path));

        // CRLF files stay CRLF.
        var crlf = NewProject(true);
        var crlfPath = Path.Combine(crlf, "Packages", "vpm-manifest.json");
        File.WriteAllText(crlfPath, VpmManifestText.Replace("\n", "\r\n"));
        PackageTransaction.Execute(crlf, StandardItems(crlf), Now);
        var text = File.ReadAllText(crlfPath);
        Require(text.Contains("\r\n") && !text.Replace("\r\n", "").Contains("\n") && text.EndsWith("\r\n"));

        // A manifest that does not parse fails the install before any folder moves.
        var bad = NewProject(true);
        File.WriteAllText(Path.Combine(bad, "Packages", "vpm-manifest.json"), "{ nope");
        var before = Snapshot(bad);
        Throws<PackageInstallException>(() => PackageTransaction.Execute(bad, StandardItems(bad), Now), "vpm-manifest.json is not a valid", "bad manifest");
        Equal(before, Snapshot(bad));
        Equal(0, StageFolders(bad));

        // No manifest at all (a project that never used VCC): nothing to update, nothing created.
        var none = NewProject(true);
        Require(!File.Exists(Path.Combine(none, "Packages", "vpm-manifest.json")));
        Require(!PackageTransaction.Execute(none, StandardItems(none), Now).ManifestUpdated);
        Require(!File.Exists(Path.Combine(none, "Packages", "vpm-manifest.json")));
    }

    // ---------- legacy, journal, mismatch ----------

    private static void LegacyRefusal()
    {
        var project = NewProject(true);
        var legacy = Path.Combine(project, "Assets", "StudioRaming", "MingToon");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "Shader.shader"), "legacy");
        var before = Snapshot(project);
        var items = StandardItems(project);
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project, items, Now), "Migrate the old Assets install first", "core legacy");
        Equal(before, Snapshot(project));
        Equal(0, StageFolders(project));
        Require(File.Exists(Path.Combine(legacy, "Shader.shader")));
        Require(!File.Exists(PackageTransaction.JournalPath(project)));
        Require(InstalledPackages.LegacyRefusal(project, Core) != null);
        Require(InstalledPackages.LegacyRefusal(project, Addon) == null);                // only the table's packages
        Require(InstalledPackages.LegacyRefusal(NewProject(false), Core) == null);       // no folder, no refusal
    }

    private static void JournalTamper()
    {
        var project = NewProject(true);
        var outside = Path.Combine(project, "Assets", "Precious");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
        var journal = PackageTransaction.JournalPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        var text = "{\"format\":1,\"state\":\"applying\",\"stageFolder\":\"\",\"manifestPath\":\"\",\"manifestBackup\":\"\",\"entries\":[{\"name\":\"x.y\",\"oldVersion\":\"1.0.0\",\"newVersion\":\"1.0.1\",\"target\":\""
            + outside.Replace("\\", "\\\\") + "\",\"staged\":\"" + Path.Combine(project, "Temp", "OnAirDeck", "stage-x", "x.y").Replace("\\", "\\\\") + "\",\"backup\":\""
            + Path.Combine(project, "Library", "OnAirDeck", "Backup", "x").Replace("\\", "\\\\") + "\"}]}";
        File.WriteAllText(journal, text);
        var message = PackageTransaction.Recover(project);
        Require(message != null && message.Contains("could not be read"));
        Require(File.Exists(Path.Combine(outside, "keep.txt")));                         // untouched
        Require(!File.Exists(journal) && Directory.GetFiles(Path.GetDirectoryName(journal), "install-journal.json.unreadable-*").Length == 1);
        Require(PackageTransaction.Recover(project) == null);

        File.WriteAllText(journal, "garbage");
        Require(PackageTransaction.Recover(project).Contains("could not be read"));
    }

    private static void Mismatches()
    {
        var project = NewProject(false);
        WritePackage(project, Core, "0.1.16", null);
        WritePackage(project, Addon, "0.1.15", new[] { Core + "=0.1.15" });
        var found = InstalledPackages.FindMismatches(project, new[] { Core, Addon, "com.not.installed" });
        Equal(1, found.Count);
        Equal(Addon, found[0].Package);
        Equal("0.1.15", found[0].Version);
        Equal(Core, found[0].Dependency);
        Equal("0.1.15", found[0].Required);
        Equal("0.1.16", found[0].Installed);

        // Dependency not installed at all.
        var lonely = NewProject(false);
        WritePackage(lonely, Addon, "0.1.15", new[] { Core + "=0.1.15" });
        var missing = InstalledPackages.FindMismatches(lonely, new[] { Addon });
        Equal(1, missing.Count);
        Equal("", missing[0].Installed);

        // A matching pair is clean.
        WritePackage(project, Addon, "0.1.16", new[] { Core + "=0.1.16" });
        Equal(0, InstalledPackages.FindMismatches(project, new[] { Core, Addon }).Count);

        bool matches;
        var versions = new[] { "0.1.14", "0.1.16", "0.1.15" };
        Equal("0.1.15", InstalledPackages.ChooseTarget(versions, "0.1.15", out matches));   // the one equal to the installed core
        Require(matches);
        Equal("0.1.16", InstalledPackages.ChooseTarget(versions, "0.1.20", out matches));   // none equal: newest pair
        Require(!matches);
        Equal("0.1.16", InstalledPackages.ChooseTarget(versions, "", out matches));         // core missing: newest pair
        Require(!matches);
        Require(InstalledPackages.ChooseTarget(new string[0], "0.1.15", out matches) == null);
        Equal("1.0.0", InstalledPackages.ChooseTarget(new[] { "1.0.0-beta.1", "1.0.0" }, "9.9.9", out matches));
    }

    private static void BuiltZip()
    {
        var path = Environment.GetEnvironmentVariable("ONAIRDECK_PACKAGING_ZIP");
        if (string.IsNullOrEmpty(path))
        {
            Console.WriteLine("  (ONAIRDECK_PACKAGING_ZIP not set; run Tools~/Packaging/build_manager_packages.py first to include this check)");
            return;
        }
        var version = Path.GetFileNameWithoutExtension(path).Replace("com.studioraming.manager-", "");
        var manifest = PackageZip.Validate(path, "com.studioraming.manager", version);
        Equal(version, manifest.Version);
        var destination = Path.Combine(NewDir(), "manager");
        PackageZip.Extract(path, destination, "com.studioraming.manager", version);
        Require(File.Exists(Path.Combine(destination, "Editor", "OnAirDeck.UnityManager.Editor.asmdef")));
        Require(File.Exists(Path.Combine(destination, "Editor", "OnAirDeck.UnityManager.Editor.asmdef.meta")));
        Require(!Directory.Exists(Path.Combine(destination, "Tools~")) && !Directory.Exists(Path.Combine(destination, ".git")));
    }

    // ---------- fixtures ----------

    /// <summary>A project with an old core 0.1.15 (plus a file the new core lacks), and an empty Library and Temp.</summary>
    private static string NewProject(bool withOldCore)
    {
        var project = Path.Combine(_root, "project-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        if (withOldCore)
        {
            WritePackage(project, Core, "0.1.15", null);
            var folder = Path.Combine(project, "Packages", Core);
            Directory.CreateDirectory(Path.Combine(folder, "Runtime"));
            Directory.CreateDirectory(Path.Combine(folder, "Legacy"));
            File.WriteAllText(Path.Combine(folder, "Runtime", "A.cs"), "old A");
            File.WriteAllText(Path.Combine(folder, "Runtime", "A.cs.meta"), "guid-a");
            File.WriteAllText(Path.Combine(folder, "Legacy", "Old.cs"), "legacy");
            File.WriteAllText(Path.Combine(folder, "Legacy", "Old.cs.meta"), "guid-old");
        }
        return project;
    }

    /// <summary>The same project with a vpm-manifest.json that has entries to update, so the manifest steps run too.</summary>
    private static string NewManifestProject()
    {
        var project = NewProject(true);
        File.WriteAllText(Path.Combine(project, "Packages", "vpm-manifest.json"), VpmManifestText);
        return project;
    }

    /// <summary>Core 0.1.16 first (a dependency goes before its dependent), then the add-on that names it.</summary>
    private static List<InstallItem> StandardItems(string project)
    {
        var dir = NewDir();
        return new List<InstallItem>
        {
            new InstallItem { Name = Core, Version = "0.1.16", ZipPath = MakePackageZip(dir, Core, "0.1.16", null, "Runtime/A.cs", "Runtime/A.cs.meta") },
            new InstallItem { Name = Addon, Version = "0.1.16", ZipPath = MakePackageZip(dir, Addon, "0.1.16", new[] { Core + "=0.1.16" }, "Editor/B.cs") }
        };
    }

    private static void WritePackage(string project, string name, string version, string[] deps)
    {
        var folder = Path.Combine(project, "Packages", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "package.json"), PackageJson(name, version, deps));
    }

    private static string PackageJson(string name, string version, string[] deps)
    {
        var sb = new StringBuilder("{\"name\":\"" + name + "\",\"version\":\"" + version + "\"");
        if (deps != null)
        {
            sb.Append(",\"vpmDependencies\":{");
            sb.Append(string.Join(",", deps.Select(d => "\"" + d.Split('=')[0] + "\":\"" + d.Split('=')[1] + "\"").ToArray()));
            sb.Append("}");
        }
        return sb.Append("}").ToString();
    }

    private static string MakePackageZip(string dir, string name, string version, string[] deps, params string[] files)
    {
        var entries = new Dictionary<string, string>();
        entries["package.json"] = PackageJson(name, version, deps);
        foreach (var file in files) entries[file] = file;    // each file's content is its own path
        return MakeRawZip(dir, name + "-" + version + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".zip", entries);
    }

    private static string MakeRawZip(string dir, string fileName, Dictionary<string, string> entries)
    {
        var path = Path.Combine(dir, fileName);
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var pair in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(pair.Key).Open())) writer.Write(pair.Value);
        return path;
    }

    private static string NewDir()
    {
        var dir = Path.Combine(_root, "work-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Every fault point a successful install passes, in order.</summary>
    private static List<string> RecordPoints()
    {
        var points = new List<string>();
        var project = NewManifestProject();
        PackageTransaction.FaultHook = p => points.Add(p);
        try { PackageTransaction.Execute(project, StandardItems(project), Now); }
        finally { PackageTransaction.FaultHook = null; }
        return points;
    }

    private static string Snapshot(string project)
    {
        var lines = new List<string>();
        var packages = Path.Combine(project, "Packages");
        foreach (var file in Directory.GetFiles(packages, "*", SearchOption.AllDirectories))
            lines.Add(file.Substring(packages.Length) + "|" + Convert.ToBase64String(SHA256.Create().ComputeHash(File.ReadAllBytes(file))));
        foreach (var dir in Directory.GetDirectories(packages, "*", SearchOption.AllDirectories))
            lines.Add(dir.Substring(packages.Length) + "/");
        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines.ToArray());
    }

    private static int StageFolders(string project)
    {
        var temp = Path.Combine(project, "Temp", "OnAirDeck");
        return Directory.Exists(temp) ? Directory.GetDirectories(temp, "stage-*").Length : 0;
    }

    private static string JournalState(string project)
    {
        var path = PackageTransaction.JournalPath(project);
        if (!File.Exists(path)) return null;
        return (string)MiniJson.ParseObject(File.ReadAllText(path))["state"];
    }

    private static Dictionary<string, object> Obj(Dictionary<string, object> parent, string key)
    {
        return (Dictionary<string, object>)parent[key];
    }

    private static bool DeepEqual(object a, object b)
    {
        var da = a as Dictionary<string, object>;
        var db = b as Dictionary<string, object>;
        if (da != null || db != null)
        {
            if (da == null || db == null || da.Count != db.Count) return false;
            foreach (var pair in da)
            {
                object other;
                if (!db.TryGetValue(pair.Key, out other) || !DeepEqual(pair.Value, other)) return false;
            }
            return true;
        }
        var la = a as List<object>;
        var lb = b as List<object>;
        if (la != null || lb != null)
        {
            if (la == null || lb == null || la.Count != lb.Count) return false;
            for (var i = 0; i < la.Count; i++) if (!DeepEqual(la[i], lb[i])) return false;
            return true;
        }
        return object.Equals(a, b);
    }

    private static void Throws<T>(Action action, string messagePart, string what) where T : Exception
    {
        try { action(); }
        catch (T e)
        {
            if (e.Message.IndexOf(messagePart, StringComparison.OrdinalIgnoreCase) < 0)
                throw new Exception(what + ": expected message containing \"" + messagePart + "\", got \"" + e.Message + "\"");
            return;
        }
        throw new Exception(what + ": expected " + typeof(T).Name);
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
