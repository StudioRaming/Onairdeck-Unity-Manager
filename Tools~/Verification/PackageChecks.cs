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
        check("transaction: a file held open during the swap is waited for; held past the budget rolls back", HeldFileDuringSwap);
        check("transaction: an updated, rolled back or recovered package keeps its folder, so a watcher on it still sees new files", WatchedFolderSurvives);
        check("transaction: a rollback interrupted at every step finishes on the next load", UndoCrashEverywhere);
        check("transaction: an unfinished journal from an older build is set aside, not replayed", OldJournalFormat);
        check("transaction: an interrupted install blocks the next one until rolled back", InterruptedBlocksNext);
        check("transaction: target outside Packages and bad names are refused", TargetGuards);
        check("transaction: self-update style item replaces a differently named folder", CustomTarget);
        check("vpm-manifest: existing entries only, unknown keys kept, backup written", ManifestUpdate);
        check("vpm-manifest: nothing to update leaves the file alone; CRLF kept; bad JSON fails before any move", ManifestEdgeCases);
        check("legacy Assets install is refused and nothing changes", LegacyRefusal);
        check("tampered journals (paths outside, absolute name, no stage folder, missing or odd phase) are refused on recovery, each for its own reason", JournalTamper);
        check("a journal that says a package was untouched while its backup holds files stops recovery and stays unfinished", JournalContradiction);
        check("mismatch finder and target choice", Mismatches);
        check("GUID collision inside an installed package: reinstalled in the same transaction, stray files listed", GuidReinstall);
        check("GUID collision in Assets, a non-package folder or between two zips of one install is refused before any change (10 paths shown)", GuidRefusals);
        check("no GUID collision: no extra reinstall; ignored folders do not count", GuidNoCollision);
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
        foreach (var range in new[] { "^0.1.16", "~0.1.16", ">=0.1.16", "0.1", "*", "latest", "==0.1.16", "= 0.1.16", " 0.1.16", "0.1.16 ", "=^0.1.16", "=", "= ", "0.1.16 - 0.2.0", "=0.1.x" })
        {
            var zip = MakePackageZip(NewDir(), Addon, "0.1.16", new[] { Core + "=" + range }, "Editor/B.cs");
            Throws<PackageInstallException>(() => PackageZip.Validate(zip, Addon, "0.1.16"), "exact version", range);
        }
        var ok = MakePackageZip(NewDir(), Addon, "0.1.16", new[] { Core + "=0.1.16" }, "Editor/B.cs");
        var manifest = PackageZip.Validate(ok, Addon, "0.1.16");
        Equal(1, manifest.VpmDependencies.Count);
        Equal(Core, manifest.VpmDependencies[0].Key);
        Equal("0.1.16", manifest.VpmDependencies[0].Value);

        // Both "=X" and bare "X" pin X; the raw spelling is kept for vpm-manifest.json.
        var pinned = PackageZip.Validate(MakePackageZip(NewDir(), Addon, "0.1.16", new[] { Core + "==0.1.16", "com.other.pkg=1.2.3-beta.1" }, "Editor/B.cs"), Addon, "0.1.16");
        Equal("0.1.16", pinned.VpmDependencies[0].Value);
        Equal("=0.1.16", pinned.VpmDependenciesRaw[0].Value);
        Equal("1.2.3-beta.1", pinned.VpmDependencies[1].Value);
        Equal("1.2.3-beta.1", pinned.VpmDependenciesRaw[1].Value);
        string exact;
        Require(PackageVersion.TryParsePin("=1.0.0", out exact) && exact == "1.0.0");
        Require(PackageVersion.TryParsePin("1.0.0", out exact) && exact == "1.0.0");
        Require(!PackageVersion.TryParsePin("==1.0.0", out exact) && !PackageVersion.TryParsePin(">=1.0.0", out exact));

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
        var points = RecordPoints();
        // The journal is replaced whole: a crash after its new copy is written but before it takes the old one's place
        // must leave a journal the next load can read and act on.
        Require(points.Contains("journal-write:old-out:" + Core) && points.Contains("journal-write:new-in:" + Core) && points.Contains("journal-write:complete"));
        foreach (var point in points)
        {
            var project = NewManifestProject();
            var before = Snapshot(project);
            var items = StandardItems(project);
            // Before the journal exists nothing is recoverable, and Unity empties Temp on the next launch.
            var beforeJournal = point.StartsWith("stage:") || point == "journal" || point == "journal-write:applying";
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

    // Antivirus holds a freshly extracted file for a moment; Windows then refuses to rename its folder. The swap waits
    // for a short hold and rolls back cleanly when the hold outlasts the retry budget.
    private static void HeldFileDuringSwap()
    {
        var project = NewManifestProject();
        FileStream held = null;
        PackageTransaction.FaultHook = p =>
        {
            if (p != "after-backup:" + Addon) return;
            held = HoldStagedFile(project);
            var release = held;
            new System.Threading.Thread(() => { System.Threading.Thread.Sleep(300); release.Dispose(); }).Start();
        };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { PackageTransaction.Execute(project, StandardItems(project), Now); }
        finally { PackageTransaction.FaultHook = null; }
        Require(held != null && clock.ElapsedMilliseconds >= 250);   // the move really waited for the release
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Addon));
        Equal("complete", JournalState(project));
        Equal(0, StageFolders(project));

        var stuck = NewManifestProject();
        var before = Snapshot(stuck);
        held = null;
        PackageTransaction.MoveRetryBudgetMs = 100;
        PackageTransaction.FaultHook = p => { if (p == "after-backup:" + Addon) held = HoldStagedFile(stuck); };
        try
        {
            Throws<PackageInstallException>(() => PackageTransaction.Execute(stuck, StandardItems(stuck), Now), "rolled back", "held past the budget");
        }
        finally
        {
            PackageTransaction.FaultHook = null;
            PackageTransaction.MoveRetryBudgetMs = 5000;
            if (held != null) held.Dispose();
        }
        Equal(before, Snapshot(stuck));
        Equal("rolledback", JournalState(stuck));
    }

    // Unity watches each embedded package folder and keeps watching it when it is renamed. On the 2021.3 bed, an update
    // that renamed the old folder away and put a new one in its place left every later AssetDatabase.Refresh waiting
    // forever in DirectoryMonitorWindows::Sync. A watcher on the package folder stands in for Unity's directory monitor:
    // after an update, a rollback and a recovery it must still see a file created in Packages/<name>.
    private static void WatchedFolderSurvives()
    {
        var updated = NewManifestProject();
        Require(WatcherSeesProbe(updated, () => PackageTransaction.Execute(updated, StandardItems(updated), Now)));
        Equal("0.1.16", InstalledPackages.InstalledVersion(updated, Core));

        var failed = NewManifestProject();
        Require(WatcherSeesProbe(failed, () =>
        {
            PackageTransaction.FaultHook = p => { if (p == "before-manifest") throw new IOException("boom"); };
            try { Throws<PackageInstallException>(() => PackageTransaction.Execute(failed, StandardItems(failed), Now), "rolled back", "failed install"); }
            finally { PackageTransaction.FaultHook = null; }
        }));
        Equal("0.1.15", InstalledPackages.InstalledVersion(failed, Core));

        var crashed = NewManifestProject();
        Require(WatcherSeesProbe(crashed, () =>
        {
            PackageTransaction.FaultHook = p => { if (p == "after-install:" + Core) throw new TransactionCrash(p); };
            try { Throws<TransactionCrash>(() => PackageTransaction.Execute(crashed, StandardItems(crashed), Now), "simulated crash", "crash"); }
            finally { PackageTransaction.FaultHook = null; }
            Require(PackageTransaction.Recover(crashed) != null);
        }));
        Equal("0.1.15", InstalledPackages.InstalledVersion(crashed, Core));
    }

    private static bool WatcherSeesProbe(string project, Action action)
    {
        var core = Path.Combine(project, "Packages", Core);
        using (var seen = new System.Threading.ManualResetEventSlim(false))
        using (var watcher = new FileSystemWatcher(core) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName })
        {
            watcher.Created += (s, e) => { if (e.Name == "probe.txt") seen.Set(); };
            watcher.EnableRaisingEvents = true;
            action();
            File.WriteAllText(Path.Combine(core, "probe.txt"), "probe");
            var result = seen.Wait(3000);
            watcher.EnableRaisingEvents = false;
            File.Delete(Path.Combine(core, "probe.txt"));
            return result;
        }
    }

    // A rollback can itself be cut short (editor killed, a file locked). Every point of the rollback is crashed once and
    // the next load must still bring Packages/ back byte for byte.
    private static void UndoCrashEverywhere()
    {
        var undoPoints = new List<string>();
        var probe = NewManifestProject();
        var failing = false;
        PackageTransaction.FaultHook = p =>
        {
            if (failing) undoPoints.Add(p);
            if (p == "before-manifest") { failing = true; throw new IOException("boom"); }
        };
        try { Throws<PackageInstallException>(() => PackageTransaction.Execute(probe, StandardItems(probe), Now), "rolled back", "probe"); }
        finally { PackageTransaction.FaultHook = null; }
        Require(undoPoints.Contains("undo:" + Core) && undoPoints.Any(p => p.StartsWith("restored:" + Core + "/", StringComparison.Ordinal)));

        foreach (var point in undoPoints)
        {
            var project = NewManifestProject();
            var before = Snapshot(project);
            PackageTransaction.FaultHook = p =>
            {
                if (p == "before-manifest") throw new IOException("boom");
                if (p == point) throw new TransactionCrash(p);
            };
            try { Throws<PackageInstallException>(() => PackageTransaction.Execute(project, StandardItems(project), Now), "could not finish", point); }
            finally { PackageTransaction.FaultHook = null; }
            Equal("applying", JournalState(project));
            Require(PackageTransaction.Recover(project) != null);
            Equal(before, Snapshot(project));
            Equal("rolledback", JournalState(project));
            Require(PackageTransaction.Recover(project) == null);
        }
    }

    // Builds before the folder-keeping swap moved whole package folders; their unfinished journals cannot be replayed safely.
    private static void OldJournalFormat()
    {
        var project = NewProject(true);
        var before = Snapshot(project);
        var journal = PackageTransaction.JournalPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(journal));
        var text = "{\"format\":1,\"state\":\"applying\",\"stageFolder\":\"\",\"manifestPath\":\"\",\"manifestBackup\":\"\",\"entries\":[{\"name\":\"" + Core
            + "\",\"oldVersion\":\"0.1.15\",\"newVersion\":\"0.1.16\",\"target\":\"" + Path.Combine(project, "Packages", Core).Replace("\\", "\\\\")
            + "\",\"staged\":\"" + Path.Combine(project, "Temp", "OnAirDeck", "stage-x", Core).Replace("\\", "\\\\")
            + "\",\"backup\":\"" + Path.Combine(project, "Library", "OnAirDeck", "Backup", Core + "-0.1.15-x").Replace("\\", "\\\\") + "\"}]}";
        File.WriteAllText(journal, text);
        var message = PackageTransaction.Recover(project);
        Require(message != null && message.Contains("older"));
        Equal(before, Snapshot(project));
        Require(!File.Exists(journal) && PackageTransaction.Recover(project) == null);

        // A finished journal of the old format is history and changes nothing.
        File.WriteAllText(journal, text.Replace("\"applying\"", "\"complete\""));
        Require(PackageTransaction.Recover(project) == null && File.Exists(journal));
    }

    // Opens the staged add-on's file without delete sharing, the way a scanner does.
    private static FileStream HoldStagedFile(string project)
    {
        var stage = Directory.GetDirectories(Path.Combine(project, "Temp", "OnAirDeck"), "stage-*").Single();
        return new FileStream(Path.Combine(stage, Addon, "Editor", "B.cs"), FileMode.Open, FileAccess.Read, FileShare.Read);
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
        Equal("=0.1.16", (string)Obj(Obj(locked, Addon), "dependencies")[Core]);        // copied exactly as package.json spells it
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

    // Each case starts from a valid format-2 journal and changes one field, so a refusal can only come from that field.
    private static void JournalTamper()
    {
        var valid = NewProject(true);
        WriteJournal(valid, new Dictionary<string, string>());
        var recovered = PackageTransaction.Recover(valid);
        Require(recovered != null && recovered.Contains("rolled back"));   // the untouched fixture is accepted

        var cases = new[]
        {
            new { Field = "target", Value = "OUTSIDE", Reason = "target outside Packages" },
            new { Field = "staged", Value = "OUTSIDE", Reason = "staged folder outside" },
            new { Field = "backup", Value = "OUTSIDE", Reason = "backup outside" },
            new { Field = "name", Value = "OUTSIDE", Reason = "not a valid package name" },
            new { Field = "stageFolder", Value = "\"\"", Reason = "no stage folder" },
            new { Field = "phase", Value = (string)null, Reason = "no phase" },
            new { Field = "phase", Value = "null", Reason = "no phase" },
            new { Field = "phase", Value = "3", Reason = "no phase" },
            new { Field = "phase", Value = "\"sideways\"", Reason = "unknown phase" }
        };
        foreach (var c in cases)
        {
            var project = NewProject(true);
            var outside = Path.Combine(project, "Assets", "Precious");
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "keep.txt"), "keep");
            var before = Snapshot(project);
            var journal = WriteJournal(project, new Dictionary<string, string> { { c.Field, c.Value == "OUTSIDE" ? Json(outside) : c.Value } });
            var message = PackageTransaction.Recover(project);
            if (message == null || !message.Contains("could not be read") || !message.Contains(c.Reason))
                throw new Exception(c.Field + "=" + (c.Value ?? "(missing)") + ": expected \"" + c.Reason + "\", got " + (message ?? "null"));
            Require(File.Exists(Path.Combine(outside, "keep.txt")) && Directory.GetFileSystemEntries(outside).Length == 1);   // untouched
            Equal(before, Snapshot(project));
            Require(!File.Exists(journal) && Directory.GetFiles(Path.GetDirectoryName(journal), "install-journal.json.unreadable-*").Length == 1);
            Require(PackageTransaction.Recover(project) == null);
        }

        var garbage = NewProject(true);
        File.WriteAllText(WriteJournal(garbage, new Dictionary<string, string>()), "garbage");
        Require(PackageTransaction.Recover(garbage).Contains("could not be read"));
    }

    // Old-out is journaled before the first move, so "no phase" with files in the backup can only be a wrong journal.
    private static void JournalContradiction()
    {
        var project = NewProject(true);
        var backup = Path.Combine(project, "Library", "OnAirDeck", "Backup", Core + "-0.1.15-x");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "moved.txt"), "moved");
        var before = Snapshot(project);
        WriteJournal(project, new Dictionary<string, string> { { "phase", "\"\"" } });
        Throws<PackageInstallException>(() => PackageTransaction.Recover(project), "by hand", "recovery");
        Equal("applying", JournalState(project));
        Equal(before, Snapshot(project));
        Require(File.Exists(Path.Combine(backup, "moved.txt")));
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project, StandardItems(project), Now), "by hand", "next install");
        Equal("applying", JournalState(project));
    }

    /// <summary>
    /// Writes an unfinished format-2 journal for the core (phase old-out, nothing moved yet). Each override replaces one
    /// field with raw JSON; a null value leaves the field out. Returns the journal path.
    /// </summary>
    private static string WriteJournal(string project, Dictionary<string, string> overrides)
    {
        var stage = Path.Combine(project, "Temp", "OnAirDeck", "stage-x");
        var top = new Dictionary<string, string>
        {
            { "format", "2" }, { "state", "\"applying\"" }, { "stageFolder", Json(stage) }, { "manifestPath", "\"\"" }, { "manifestBackup", "\"\"" }
        };
        var entry = new Dictionary<string, string>
        {
            { "name", "\"" + Core + "\"" }, { "oldVersion", "\"0.1.15\"" }, { "newVersion", "\"0.1.16\"" },
            { "target", Json(Path.Combine(project, "Packages", Core)) }, { "staged", Json(Path.Combine(stage, Core)) },
            { "backup", Json(Path.Combine(project, "Library", "OnAirDeck", "Backup", Core + "-0.1.15-x")) }, { "phase", "\"old-out\"" }
        };
        foreach (var pair in overrides)
        {
            var fields = top.ContainsKey(pair.Key) ? top : entry;
            if (pair.Value == null) fields.Remove(pair.Key);
            else fields[pair.Key] = pair.Value;
        }
        Func<Dictionary<string, string>, string> obj = d => "{" + string.Join(",", d.Select(p => "\"" + p.Key + "\":" + p.Value).ToArray()) + "}";
        var text = obj(top).TrimEnd('}') + ",\"entries\":[" + obj(entry) + "]}";
        var path = PackageTransaction.JournalPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
        return path;
    }

    private static string Json(string path)
    {
        return "\"" + path.Replace("\\", "\\\\") + "\"";
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

    // ---------- GUID collisions ----------

    private const string G1 = "11111111111111111111111111111111";
    private const string G2 = "22222222222222222222222222222222";
    private const string G3 = "33333333333333333333333333333333";
    private const string GA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static string Meta(string guid) { return "fileFormatVersion: 2\nguid: " + guid + "\n"; }

    /// <summary>Core 0.1.16 as VCC left it, with the old add-on's files inside it (the legacy unitypackage import).</summary>
    private static string NewStrayProject()
    {
        var project = NewProject(false);
        WritePackage(project, Core, "0.1.16", null);
        var core = Path.Combine(project, "Packages", Core);
        Directory.CreateDirectory(Path.Combine(core, "Runtime"));
        Directory.CreateDirectory(Path.Combine(core, "Editor", "AddonStray"));
        File.WriteAllText(Path.Combine(core, "Runtime", "A.cs"), "A");
        File.WriteAllText(Path.Combine(core, "Runtime", "A.cs.meta"), Meta(GA));
        File.WriteAllText(Path.Combine(core, "Editor", "AddonStray", "B.cs"), "old B");
        File.WriteAllText(Path.Combine(core, "Editor", "AddonStray", "B.cs.meta"), Meta(G1));
        File.WriteAllText(Path.Combine(core, "Editor", "AddonStray.meta"), Meta(G2));
        return project;
    }

    private static string AddonZipWithMetas(string dir)
    {
        return MakeRawZip(dir, "addon.zip", new Dictionary<string, string>
        {
            { "package.json", PackageJson(Addon, "0.1.16", new[] { Core + "==0.1.16" }) },
            { "Editor/B.cs", "new B" },
            { "Editor/B.cs.meta", Meta(G1) },
            { "Editor.meta", Meta(G3) }
        });
    }

    private static string CoreZipWithMetas(string dir)
    {
        return MakeRawZip(dir, "core.zip", new Dictionary<string, string>
        {
            { "package.json", PackageJson(Core, "0.1.16", null) },
            { "Runtime/A.cs", "A" },
            { "Runtime/A.cs.meta", Meta(GA) }
        });
    }

    private static void GuidReinstall()
    {
        var project = NewStrayProject();
        var dir = NewDir();
        var addon = new InstallItem { Name = Addon, Version = "0.1.16", ZipPath = AddonZipWithMetas(dir) };
        var before = Snapshot(project);

        // The add-on alone would duplicate the stray file's GUID: refused before anything moves.
        Throws<PackageInstallException>(() => PackageTransaction.Execute(project, new[] { addon }, Now), "cannot be installed in a project that has " + Core, "alone");
        Equal(before, Snapshot(project));
        Require(!File.Exists(PackageTransaction.JournalPath(project)) && !Directory.Exists(PackageTransaction.BackupRoot(project)));

        var guids = GuidCollisions.ReadZipGuids(addon.ZipPath);
        Require(guids.Contains(G1) && guids.Contains(G3) && guids.Count == 2);
        var found = GuidCollisions.Scan(project, guids, new string[0]);
        Equal(1, found.Count);
        Equal(G1, found[0].Guid);
        Equal("Packages/" + Core + "/Editor/AddonStray/B.cs.meta", found[0].Path);
        Equal(Core, found[0].PackageName);
        Equal("0.1.16", found[0].PackageVersion);
        var blocking = new List<GuidCollision>();
        var candidates = GuidCollisions.Candidates(found, blocking);
        Equal(1, candidates.Count);
        Equal(0, blocking.Count);
        Equal(Core, candidates[0].Name);
        Equal(1, candidates[0].Files);

        // The same transaction reinstalls the core at its current version: the stray files leave with the swap.
        var reinstall = new InstallItem { Name = Core, Version = candidates[0].Version, ZipPath = CoreZipWithMetas(dir), Note = ReinstallNote(candidates[0]) };
        var report = PackageTransaction.Execute(project, new[] { reinstall, addon }, Now);
        Equal(2, report.Installed.Count);
        var coreFolder = Path.Combine(project, "Packages", Core);
        Require(File.Exists(Path.Combine(coreFolder, "Runtime", "A.cs")));
        Require(!Directory.Exists(Path.Combine(coreFolder, "Editor")));
        Require(report.Installed[0].NotCarriedOver.Contains("Editor/AddonStray/B.cs") && report.Installed[0].NotCarriedOver.Contains("Editor/AddonStray/B.cs.meta"));
        Equal("old B", File.ReadAllText(Path.Combine(report.Installed[0].Backup, "Editor", "AddonStray", "B.cs")));
        Equal("new B", File.ReadAllText(Path.Combine(project, "Packages", Addon, "Editor", "B.cs")));
        Require(report.Describe().Contains("removed by reinstalling it at 0.1.16"));
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Core));
        // Nothing else in the project uses the add-on's GUIDs any more.
        Equal(0, GuidCollisions.Scan(project, guids, new[] { Path.Combine(project, "Packages", Addon) }).Count);
    }

    // Same wording as PackageInstallService.ReinstallNote, which needs Unity and cannot be compiled here.
    private static string ReinstallNote(ReinstallCandidate candidate)
    {
        return "Files from an older install of the package being installed (" + candidate.Files + " with the same GUIDs) were inside "
            + candidate.Name + " and were removed by reinstalling it at " + candidate.Version + ".";
    }

    private static void GuidRefusals()
    {
        // A collision under Assets/ cannot be repaired by a reinstall: refused, nothing moved.
        var project = NewProject(false);
        WritePackage(project, Core, "0.1.16", null);
        var old = Path.Combine(project, "Assets", "Old");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "B.cs.meta"), Meta(G1));
        var addon = new InstallItem { Name = Addon, Version = "0.1.16", ZipPath = AddonZipWithMetas(NewDir()) };
        var before = Snapshot(project);
        var error = Capture(() => PackageTransaction.Execute(project, new[] { addon }, Now));
        Require(error != null && error.Contains("Assets/Old/B.cs.meta") && error.Contains(G1));
        Equal(before, Snapshot(project));
        Require(File.Exists(Path.Combine(old, "B.cs.meta")) && !Directory.Exists(PackageTransaction.BackupRoot(project)) && !File.Exists(PackageTransaction.JournalPath(project)));
        var blocking = new List<GuidCollision>();
        Equal(0, GuidCollisions.Candidates(GuidCollisions.Scan(project, GuidCollisions.ReadZipGuids(addon.ZipPath), new string[0]), blocking).Count);
        Equal(1, blocking.Count);

        // At most 10 paths are listed.
        for (var i = 0; i < 12; i++) File.WriteAllText(Path.Combine(old, "many" + i + ".meta"), Meta(G1));
        var many = Capture(() => PackageTransaction.Execute(project, new[] { addon }, Now));
        Equal(10, many.Split('\n').Count(l => l.StartsWith("  Assets/")));
        Require(many.Contains("and 3 more"));

        // A folder that is no package cannot be reinstalled either.
        var odd = NewProject(false);
        var loose = Path.Combine(odd, "Packages", "not-a-package");
        Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, "X.meta"), Meta(G1));
        var found = GuidCollisions.Scan(odd, GuidCollisions.ReadZipGuids(addon.ZipPath), new string[0]);
        var oddBlocking = new List<GuidCollision>();
        Equal(0, GuidCollisions.Candidates(found, oddBlocking).Count);
        Equal(1, oddBlocking.Count);
        Require(Capture(() => PackageTransaction.Execute(odd, new[] { addon }, Now)) != null);

        // Two packages of one install that share a GUID are refused too, before anything moves.
        var pair = NewProject(false);
        var clash = new InstallItem
        {
            Name = Core,
            Version = "0.1.16",
            ZipPath = MakeRawZip(NewDir(), "core-clash.zip", new Dictionary<string, string>
            {
                { "package.json", PackageJson(Core, "0.1.16", null) },
                { "Editor/B.cs", "B" },
                { "Editor/B.cs.meta", Meta(G1) }
            })
        };
        var pairBefore = Snapshot(pair);
        var pairError = Capture(() => PackageTransaction.Execute(pair, new[] { clash, addon }, Now));
        Require(pairError != null && pairError.Contains("both use GUID " + G1));
        Equal(pairBefore, Snapshot(pair));
        Require(!Directory.Exists(PackageTransaction.BackupRoot(pair)) && !File.Exists(PackageTransaction.JournalPath(pair)));
    }

    private static void GuidNoCollision()
    {
        var project = NewProject(false);
        WritePackage(project, Core, "0.1.16", null);
        var core = Path.Combine(project, "Packages", Core);
        File.WriteAllText(Path.Combine(core, "A.cs.meta"), Meta(GA));
        // Ignored folders (name ending in ~) and non-meta text do not count.
        var hidden = Path.Combine(project, "Assets", "Docs~");
        Directory.CreateDirectory(hidden);
        File.WriteAllText(Path.Combine(hidden, "B.cs.meta"), Meta(G1));
        File.WriteAllText(Path.Combine(project, "Assets", "garbage.meta"), "not a meta");
        var addon = new InstallItem { Name = Addon, Version = "0.1.16", ZipPath = AddonZipWithMetas(NewDir()) };
        Equal(0, GuidCollisions.Scan(project, GuidCollisions.ReadZipGuids(addon.ZipPath), new string[0]).Count);
        var report = PackageTransaction.Execute(project, new[] { addon }, Now);
        Equal(1, report.Installed.Count);                       // no extra reinstall
        Equal("", report.Installed[0].Note);
        Equal("0.1.16", InstalledPackages.InstalledVersion(project, Core));
        Require(!report.Describe().Contains("reinstalling"));
    }

    private static string Capture(Action action)
    {
        try { action(); return null; }
        catch (PackageInstallException e) { return e.Message; }
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
            new InstallItem { Name = Addon, Version = "0.1.16", ZipPath = MakePackageZip(dir, Addon, "0.1.16", new[] { Core + "==0.1.16" }, "Editor/B.cs") }
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
            sb.Append(string.Join(",", deps.Select(d => "\"" + d.Substring(0, d.IndexOf('=')) + "\":\"" + d.Substring(d.IndexOf('=') + 1) + "\"").ToArray()));
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
