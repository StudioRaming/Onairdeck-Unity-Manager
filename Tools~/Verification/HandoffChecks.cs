using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OnAirDeck.UnityManager;

// The MingToon hand-off (OnAirDeckConfig.LegacyInstallers): the product installer cleans up the old Assets install, the Manager only
// imports it and waits. Real sources: LegacyInstall.Prepare, InstallerHandoffHandler, LegacyInstallerFile, LegacyHandoff,
// MingToonInstallerHandoff, LicenseRenewalStatus. The Unity half (runner download, import queue, poller, dialog) is compile-checked only.
internal static partial class LegacyChecks
{
    private const string OldFolder = "Assets/StudioRaming/MingToon";

    public static void RunHandoff(Action<string, Action> check)
    {
        check("handoff: chosen over a move in the legacy case; only the hand-off item is shown; the old tree stays byte-identical", HandoffChosen);
        check("handoff: decline, background, no confirm and a version without installer file never import or move (fail closed)", HandoffRefusals);
        check("handoff: the installer file is chosen only in the legacy case (old folder present, package absent), by label and suffix", InstallerOnlyInLegacyCase);
        check("handoff: the never-move list is never part of a plan or a guarded path (folder, file inside, ancestor)", NeverMoveList);
        check("handoff: Done, Stopped and Pending come from manager-result.json, matched by requestCreatedUtc (stale results ignored)", ResultStatus);
        check("handoff: no matching result 120 s after the import = unsupported installer; busy editor time is not idle time", UnsupportedRule);
        check("handoff: the wait state survives a reload (re-read from the file); the install step is due once, after LegacyRemovedForManager", ReloadSafeWait);
        check("handoff: manager-request.json has the exact shape and no BOM; the installer must say managerCleanup true (tar.gz entry)", RequestAndFlag);
        check("handoff: the staged zip extracts to Packages/<package> with the traversal guards; an existing path or a bad zip installs nothing", ExtractionGuard);
        check("handoff: no Manager asmdef or source file references a MingToon assembly or namespace", NoMingToonReference);
        check("renewal status: only HTTP refusals are recorded with a code, success clears, prune keeps account licenses", RenewalStatus);
    }

    private static LegacyInstallerFile Installer() { return new LegacyInstallerFile { VersionId = "v-upd", FileName = "MingToon-1.0.0-update.unitypackage", Size = 10 }; }
    private static LegacyInstallerFile ZipFile() { return new LegacyInstallerFile { VersionId = "v-zip", FileName = Pkg + "-1.0.0.zip", Size = 20 }; }

    private static LegacyProbe HandoffProbe(bool withInstaller)
    {
        var probe = Probe(Pkg, null, null);
        if (withInstaller) { probe.Installer = Installer(); probe.Zip = ZipFile(); }
        return probe;
    }

    private static void HandoffOnly()
    {
        LegacyInstall.Handlers.Clear();
        LegacyInstall.Handlers.Add(new InstallerHandoffHandler());
    }

    private static void HandoffChosen()
    {
        HandoffOnly();
        var p = NewProject();
        var before = OldInstall(p);
        Put(p, "Assets/MingToonPresets/mine.asset", "preset");
        before = Snapshot(p);
        var shown = new List<LegacyPlan>();
        LegacyHandoffRequest handoff;
        var move = LegacyInstall.Prepare(p, new[] { HandoffProbe(true) }, true, plan => { shown.Add(plan); return true; }, Now, out handoff);
        Require(move == null && handoff != null);
        Equal(1, shown.Count);
        Equal(1, shown[0].Items.Count);
        Require(shown[0].Items[0].HandOff);
        Equal(OldFolder, handoff.Folder);
        Equal(Pkg, handoff.Package);
        Equal("v-upd", handoff.Installer.VersionId);
        Equal("v-zip", handoff.Zip.VersionId);
        Equal(before, Snapshot(p));
        // The pending move machinery refuses the item outright.
        var plan2 = LegacyInstall.Detect(p, new[] { HandoffProbe(true) });
        Throws<PackageInstallException>(() => LegacyInstall.Move(p, plan2, Now, Pkg, "1.0.0"));
        Equal(before, Snapshot(p));
        // With the package resolved in Packages/, the item is not a hand-off any more (no folder is touched either way).
        Put(p, "Packages/" + Pkg + "/package.json", "{\"name\":\"" + Pkg + "\",\"version\":\"1.0.0\"}");
        Require(!LegacyInstall.Detect(p, new[] { HandoffProbe(true) }).HasHandoff);
        // The text table has ko, en and ja for the dialog and the result.
        for (var language = 0; language < 3; language++)
        {
            Require(LegacyInstallText.HandoffTitle(language, "MingToon").Length > 0 && LegacyInstallText.HandoffBody(language, "MingToon").Length > 0);
            Require(LegacyInstallText.HandoffYes(language).Length > 0 && LegacyInstallText.HandoffNo(language).Length > 0 && LegacyInstallText.HandedOff(language, "MingToon").Length > 0);
        }
        Equal("이전 MingToon 설치를 정리할까요?", LegacyInstallText.HandoffTitle(0, "MingToon"));
        Equal("삭제하고 설치", LegacyInstallText.HandoffYes(0));
        Equal("설치 안 함", LegacyInstallText.HandoffNo(0));
        Equal("MingToon 설치기가 이어서 설치해요.", LegacyInstallText.HandedOff(0, "MingToon"));
    }

    private static void HandoffRefusals()
    {
        HandoffOnly();
        var p = NewProject();
        var before = OldInstall(p);
        LegacyHandoffRequest handoff;
        var asked = 0;
        Throws<LegacyDeclinedException>(() => LegacyInstall.Prepare(p, new[] { HandoffProbe(true) }, true, plan => { asked++; return false; }, Now, out handoff));
        Equal(1, asked);
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, new[] { HandoffProbe(true) }, false, plan => { asked++; return true; }, Now, out handoff));
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, new[] { HandoffProbe(true) }, true, null, Now, out handoff));
        Equal(1, asked);   // background and no callback never ask
        // No installer file in this version: fail closed with the folder in the text, nothing asked.
        try
        {
            LegacyInstall.Prepare(p, new[] { HandoffProbe(false) }, true, plan => { asked++; return true; }, Now, out handoff);
            throw new Exception("Expected LegacyCleanupRequiredException");
        }
        catch (LegacyCleanupRequiredException e)
        {
            Require(e.Message.Contains(OldFolder));
        }
        Equal(1, asked);
        // A caller that cannot hand off (the old overload) is treated like a background caller.
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, new[] { HandoffProbe(true) }, true, plan => true, Now));
        Equal(before, Snapshot(p));
    }

    private static void InstallerOnlyInLegacyCase()
    {
        var wanted = new LicenseVersionEntry { version_id = "v-zip", version_label = "1.0.0", file_name = Pkg + "-1.0.0.zip", file_size = 20 };
        var versions = new[]
        {
            wanted,
            new LicenseVersionEntry { version_id = "v-old", version_label = "0.9.0", file_name = "MingToon-0.9.0-update.unitypackage", file_size = 5 },
            new LicenseVersionEntry { version_id = "v-other", version_label = "1.0.0", file_name = "notes.txt", file_size = 1 },
            new LicenseVersionEntry { version_id = "v-upd", version_label = "1.0.0", file_name = "MingToon-1.0.0-UPDATE.unitypackage", file_size = 10 }
        };
        var legacy = NewProject();
        OldInstall(legacy);
        var found = LegacyInstallerFile.ForLegacyCase(legacy, Pkg, wanted, versions);
        Require(found != null);
        Equal("v-upd", found.VersionId);   // same label, suffix matched case-insensitively, other labels and files skipped
        Equal(10L, found.Size);
        // No version of the same label with an installer file: null, the caller fails closed.
        Require(LegacyInstallerFile.ForLegacyCase(legacy, Pkg, wanted, new[] { wanted, versions[1], versions[2] }) == null);
        // Not the legacy case: no old folder, or the package is already resolved, or another package.
        var clean = NewProject();
        Require(LegacyInstallerFile.ForLegacyCase(clean, Pkg, wanted, versions) == null);
        var resolved = NewProject();
        OldInstall(resolved);
        Put(resolved, "Packages/" + Pkg + "/package.json", "{\"name\":\"" + Pkg + "\",\"version\":\"1.0.0\"}");
        Require(LegacyInstallerFile.ForLegacyCase(resolved, Pkg, wanted, versions) == null);
        Require(LegacyInstallerFile.ForLegacyCase(legacy, Other, wanted, versions) == null);
        Require(LegacyInstallerFile.OfVersion(wanted).Size == 20);
        // The downloaded size must equal the listed one; an unknown size fails closed.
        // Contract v9.5: the server gives a size but no hash. A listed size must match exactly; an unlisted size (0) accepts any non-empty file; an empty file never.
        Require(LegacyInstallerFile.SizeMatches(10, 10) && !LegacyInstallerFile.SizeMatches(10, 11) && LegacyInstallerFile.SizeMatches(0, 5));
        Require(!LegacyInstallerFile.SizeMatches(0, 0) && !LegacyInstallerFile.SizeMatches(10, 0));
    }

    private static void NeverMoveList()
    {
        foreach (var folder in OnAirDeckConfig.NeverMoveAssetFolders)
        {
            Require(LegacyInstall.IsNeverMove(folder));
            Require(LegacyInstall.IsNeverMove(folder + "/sub/file.asset"));
        }
        Require(LegacyInstall.IsNeverMove("assets/mingtoonpresets/x"));   // case-insensitive
        Require(!LegacyInstall.IsNeverMove("Assets/StudioRaming/MingToon"));
        Require(!LegacyInstall.IsNeverMove("Assets/MingToonPresetsExtra"));
        Require(LegacyInstall.ContainsNeverMove("Assets/StudioRaming"));
        Require(!LegacyInstall.ContainsNeverMove("Assets/StudioRaming/MingToon"));

        var p = NewProject();
        Put(p, "Assets/MingToonPresets/mine.asset", "x");
        Put(p, "Assets/StudioRaming/MingToonGenerated/a.asset", "x");
        Put(p, "Assets/StudioRaming/MingToonInstaller/b.cs", "x");
        Put(p, "Assets/MLC/c.cs", "x");
        string relative;
        foreach (var path in new[] { "Assets/MingToonPresets", "Assets/MingToonPresets/mine.asset", "Assets/StudioRaming/MingToonGenerated", "Assets/StudioRaming/MingToonInstaller", "Assets/MLC", "Assets/StudioRaming" })
            Require(!LegacyInstall.TryGuard(p, path, out relative));

        // A package that names a never-move folder (or its parent) in replacesAssetFolders gets an empty plan; nothing is touched.
        LegacyInstall.Handlers.Clear();
        LegacyInstall.Handlers.Add(new VouchingHandler());
        var before = Snapshot(p);
        var zip = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/MingToonPresets\",\"Assets/StudioRaming\",\"Assets/StudioRaming/MingToonGenerated\"]}");
        Require(LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) }).IsEmpty);
        Equal(before, Snapshot(p));
        // A GUID collision inside a never-move folder is not moved either, and the source-4 walk-up stops before its parent.
        Put(p, "Assets/MingToonPresets/mine.asset.meta", Meta(G1));
        var guidZip = Zip(Other, "1.0.0", "", G1);
        Require(LegacyInstall.Detect(p, new[] { Probe(Other, guidZip, null) }).IsEmpty);
    }

    // ---------- result fixtures ----------

    private static LegacyInstallerSpec Spec() { return OnAirDeckConfig.LegacyInstallers[Pkg]; }

    private static void Result(string project, string created, string phase, string failure, string attempt, string updated)
    {
        var text = "{\"schemaVersion\":1,\"requestCreatedUtc\":\"" + created + "\",\"attemptId\":\"" + attempt + "\",\"targetVersion\":\"1.0.0\",\"phase\":\"" + phase
            + "\",\"failure\":\"" + (failure ?? "") + "\",\"updatedUtc\":\"" + updated + "\"}";
        Put(project, Spec().ResultPath, text);
    }

    private static string StartedProject(DateTime now, bool installed, out string created)
    {
        var p = NewProject();
        OldInstall(p);
        if (installed) Put(p, "Packages/" + Pkg + "/package.json", "{\"name\":\"" + Pkg + "\",\"version\":\"1.0.0\"}");
        created = LegacyHandoff.Begin(p, new LegacyHandoffRequest { Package = Pkg, Version = "1.0.0", Product = "MingToon", Folder = OldFolder }, now);
        LegacyHandoff.MarkImportQueued(p, Pkg, now);
        return p;
    }

    private static void ResultStatus()
    {
        var t0 = new DateTime(2026, 10, 29, 3, 0, 0, DateTimeKind.Utc);
        string created;
        Equal(HandoffKind.None, LegacyHandoff.Status(NewProject(), Pkg, t0, false, false).Kind);

        // Pending after the start. A result of ANOTHER request (stale file) is ignored: not Done, not Stopped by its Failed.
        var p = StartedProject(t0, true, out created);
        Equal(HandoffKind.Pending, LegacyHandoff.Status(p, Pkg, t0.AddSeconds(5), false, true).Kind);
        Result(p, "2020-01-01T00:00:00Z", "Complete", null, "old", "u1");
        Equal(HandoffKind.Pending, LegacyHandoff.Status(p, Pkg, t0.AddSeconds(6), false, true).Kind);
        Result(p, "2020-01-01T00:00:00Z", "Failed", "preflight: stale", "old", "u2");
        Equal(HandoffKind.Pending, LegacyHandoff.Status(p, Pkg, t0.AddSeconds(7), false, true).Kind);
        // The matching result: LegacyRemovedForManager is Pending ("installing"), Complete with the package resolved is Done.
        Result(p, created, "LegacyRemovedForManager", null, "a1", "u3");
        var status = LegacyHandoff.Status(p, Pkg, t0.AddSeconds(10), false, true);
        Equal(HandoffKind.Pending, status.Kind);
        Equal("LegacyRemovedForManager", status.Phase);
        Require(LegacyInstallText.HandoffStatusText(0, "MingToon", status, "m").Contains("MingToon 설치 중"));
        Result(p, created, "Complete", null, "a1", "u4");
        status = LegacyHandoff.Status(p, Pkg, t0.AddSeconds(15), false, true);
        Equal(HandoffKind.Done, status.Kind);

        // Complete without the package resolved is not Done yet.
        var q = StartedProject(t0, false, out created);
        Result(q, created, "Complete", null, "a", "u1");
        Equal(HandoffKind.Pending, LegacyHandoff.Status(q, Pkg, t0.AddSeconds(5), false, true).Kind);

        // Failed: Stopped with the failure text; the copy differs by prefix (preflight: nothing changed; removal/verify: reinstall).
        foreach (var prefix in new[] { "manager-request-rejected:", "preflight:", "removal:", "verify:" })
        {
            var f = StartedProject(t0, false, out created);
            Result(f, created, "Failed", prefix + " boom", "a", "u1");
            status = LegacyHandoff.Status(f, Pkg, t0.AddSeconds(5), false, true);
            Equal(HandoffKind.Stopped, status.Kind);
            Require(status.Reason.StartsWith(prefix));
            Equal(prefix.TrimEnd(':').Replace("manager-request-rejected", "rejected"), MingToonLegacyCleanup.FailureKind(status.Reason));
        }
        for (var language = 0; language < 3; language++)
        {
            var preflight = LegacyInstallText.HandoffFailure(language, "MingToon", "preflight: edited file");
            var removal = LegacyInstallText.HandoffFailure(language, "MingToon", "removal: locked");
            Require(preflight.Length > 0 && removal.Length > 0 && preflight != removal);
            Require(preflight.Contains("edited file") && removal.Contains("locked"));
        }
        Require(LegacyInstallText.HandoffFailure(0, "MingToon", "preflight: x").Contains("아무것도 바뀌지 않았어요"));
        Require(LegacyInstallText.HandoffFailure(0, "MingToon", "verify: x").Contains("다시 설치"));
        Require(LegacyInstallText.HandoffFailure(0, "MingToon", "removal: x").Contains("다시 설치"));

        // The Manager's own install failed: Stopped with that message.
        var m = StartedProject(t0, false, out created);
        Result(m, created, "LegacyRemovedForManager", null, "a", "u1");
        Require(LegacyHandoff.InstallDue(m, Pkg));
        LegacyHandoff.MarkInstallStarted(m, Pkg, t0.AddSeconds(20), "install: disk full");
        status = LegacyHandoff.Status(m, Pkg, t0.AddSeconds(21), false, true);
        Equal(HandoffKind.Stopped, status.Kind);
        Equal("install: disk full", status.Reason);

        // Clear forgets the hand-off, the request and the staged zip.
        Put(m, Spec().RequestPath, "{}");
        LegacyHandoff.Clear(m, Pkg);
        Equal(HandoffKind.None, LegacyHandoff.Status(m, Pkg, t0.AddSeconds(30), false, false).Kind);
        Require(!File.Exists(Path.Combine(m, Spec().RequestPath.Replace('/', Path.DirectorySeparatorChar))));
    }

    private static void UnsupportedRule()
    {
        var t0 = new DateTime(2026, 10, 29, 3, 30, 0, DateTimeKind.Utc);
        string created;
        // The import finished, the installer never answered: 120 s idle = Stopped "unsupported installer". Not before.
        var p = StartedProject(t0, false, out created);
        LegacyHandoff.MarkImportDone(p, Pkg, t0);
        Equal(HandoffKind.Pending, LegacyHandoff.Status(p, Pkg, t0.AddSeconds(119), false, true).Kind);
        var status = LegacyHandoff.Status(p, Pkg, t0.AddSeconds(121), false, true);
        Equal(HandoffKind.Stopped, status.Kind);
        Equal(LegacyHandoff.ReasonUnsupported, status.Reason);
        Require(LegacyInstallText.HandoffStatusText(0, "MingToon", status, "m").Contains("이전 설치 정리 필요"));
        // The editor compiling or importing is not idle: a long compile never counts against the installer.
        var b = StartedProject(t0, false, out created);
        LegacyHandoff.MarkImportDone(b, Pkg, t0);
        Equal(HandoffKind.Pending, LegacyHandoff.Status(b, Pkg, t0.AddSeconds(300), true, true).Kind);
        Equal(HandoffKind.Pending, LegacyHandoff.Status(b, Pkg, t0.AddSeconds(330), false, true).Kind);
        Equal(HandoffKind.Stopped, LegacyHandoff.Status(b, Pkg, t0.AddSeconds(300 + 121), false, true).Kind);
        // A read-only look never moves the clock.
        var r = StartedProject(t0, false, out created);
        LegacyHandoff.MarkImportDone(r, Pkg, t0);
        LegacyHandoff.Status(r, Pkg, t0.AddSeconds(100), true, false);
        Equal(HandoffKind.Stopped, LegacyHandoff.Status(r, Pkg, t0.AddSeconds(125), false, false).Kind);
        // After the Manager's install: no new result for 120 s idle = "needs check" with the last phase; Complete or Failed ends it earlier.
        var n = StartedProject(t0, false, out created);
        Result(n, created, "LegacyRemovedForManager", null, "a", "u1");
        LegacyHandoff.MarkInstallStarted(n, Pkg, t0.AddSeconds(10), "");
        Require(!LegacyHandoff.InstallDue(n, Pkg));
        Equal(HandoffKind.Pending, LegacyHandoff.Status(n, Pkg, t0.AddSeconds(100), false, true).Kind);
        status = LegacyHandoff.Status(n, Pkg, t0.AddSeconds(100 + 121), false, true);
        Equal(HandoffKind.Stopped, status.Kind);
        Equal(LegacyHandoff.ReasonNeedsCheck, status.Reason);
        Equal("LegacyRemovedForManager", status.Phase);
        Require(LegacyInstallText.HandoffStatusText(0, "MingToon", status, "m").Contains("확인 필요"));
    }

    private static void ReloadSafeWait()
    {
        var t0 = new DateTime(2026, 10, 29, 4, 0, 0, DateTimeKind.Utc);
        string created;
        var p = StartedProject(t0, false, out created);
        // Every call reads the file again: nothing lives in memory, so a domain reload changes nothing. createdUtc is in the state file.
        Require(LegacyHandoff.Packages(p).Contains(Pkg));
        Require(LegacyHandoff.ImportOutstanding(p, Pkg));
        Require(LegacyHandoff.IsWaiting(p));
        Equal("1.0.0", LegacyHandoff.VersionOf(p, Pkg));
        LegacyHandoff.MarkImportDone(p, Pkg, t0.AddSeconds(3));
        Require(!LegacyHandoff.ImportOutstanding(p, Pkg));
        Require(!LegacyHandoff.InstallDue(p, Pkg));                               // no result yet
        Result(p, "2000-01-01T00:00:00Z", "LegacyRemovedForManager", null, "a", "u1");
        Require(!LegacyHandoff.InstallDue(p, Pkg));                               // someone else's result
        Result(p, created, "LegacyRemovedForManager", null, "a", "u2");
        Require(LegacyHandoff.InstallDue(p, Pkg));
        LegacyHandoff.MarkInstallStarted(p, Pkg, t0.AddSeconds(8), "");
        Require(!LegacyHandoff.InstallDue(p, Pkg));                               // the install step runs once
        Require(LegacyHandoff.IsWaiting(p));
        // A Stopped hand-off no longer blocks the entry points.
        var s = StartedProject(t0, false, out created);
        Result(s, created, "Failed", "preflight: x", "a", "u1");
        Require(!LegacyHandoff.IsWaiting(s));
    }

    // ---------- the installer contract (MingToonLegacyCleanup) ----------

    private static byte[] Tar(params KeyValuePair<string, byte[]>[] entries)
    {
        using (var tar = new MemoryStream())
        {
            foreach (var entry in entries)
            {
                var header = new byte[512];
                Encoding.ASCII.GetBytes(entry.Key).CopyTo(header, 0);
                Encoding.ASCII.GetBytes(Convert.ToString(entry.Value.Length, 8).PadLeft(11, '0') + "\0").CopyTo(header, 124);
                header[156] = (byte)'0';
                tar.Write(header, 0, 512);
                tar.Write(entry.Value, 0, entry.Value.Length);
                tar.Write(new byte[(512 - entry.Value.Length % 512) % 512], 0, (512 - entry.Value.Length % 512) % 512);
            }
            tar.Write(new byte[1024], 0, 1024);
            return tar.ToArray();
        }
    }

    private static string UnityPackage(string configJson, string pathInPackage)
    {
        var path = Path.Combine(_root, "up-" + Guid.NewGuid().ToString("N") + ".unitypackage");
        var entries = new List<KeyValuePair<string, byte[]>>();
        entries.Add(new KeyValuePair<string, byte[]>("aaaa/pathname", Encoding.UTF8.GetBytes("Assets/StudioRaming/MingToonInstaller/Other.cs")));
        entries.Add(new KeyValuePair<string, byte[]>("aaaa/asset", Encoding.UTF8.GetBytes("class A {}")));
        if (configJson != null)
        {
            entries.Add(new KeyValuePair<string, byte[]>("bbbb/asset", Encoding.UTF8.GetBytes(configJson)));
            entries.Add(new KeyValuePair<string, byte[]>("bbbb/pathname", Encoding.UTF8.GetBytes(pathInPackage)));
        }
        using (var file = File.Create(path))
        using (var gz = new GZipStream(file, CompressionMode.Compress))
        {
            var bytes = Tar(entries.ToArray());
            gz.Write(bytes, 0, bytes.Length);
        }
        return path;
    }

    private static void RequestAndFlag()
    {
        var configPath = "Assets/StudioRaming/MingToonInstaller/Bootstrap/mingtoon-bootstrap-config.json";
        Require(MingToonLegacyCleanup.AllowsCleanup(UnityPackage("{\"managerCleanup\": true}", configPath)));
        Require(!MingToonLegacyCleanup.AllowsCleanup(UnityPackage("{\"managerCleanup\": false}", configPath)));
        Require(!MingToonLegacyCleanup.AllowsCleanup(UnityPackage("{\"managerCleanup\": \"true\"}", configPath)));
        Require(!MingToonLegacyCleanup.AllowsCleanup(UnityPackage("{\"managerSuppliedZip\": true}", configPath)));   // the dropped draft flag does not count
        Require(!MingToonLegacyCleanup.AllowsCleanup(UnityPackage(null, configPath)));                                  // no config entry
        Require(!MingToonLegacyCleanup.AllowsCleanup(UnityPackage("{\"managerCleanup\": true}", "Assets/x/other.json")));
        var junk = Path.Combine(_root, "junk.unitypackage");
        File.WriteAllText(junk, "not a gzip");
        Require(!MingToonLegacyCleanup.AllowsCleanup(junk));
        Require(!MingToonLegacyCleanup.AllowsCleanup(Path.Combine(_root, "missing.unitypackage")));

        // The request: exact keys and values, UTF-8 without a BOM, in ProjectSettings/MingToonInstaller.
        var p = NewProject();
        var now = new DateTime(2026, 10, 29, 5, 0, 0, DateTimeKind.Utc);
        var created = MingToonLegacyCleanup.CreatedUtc(now);
        Equal("2026-10-29T05:00:00Z", created);
        MingToonLegacyCleanup.WriteRequest(p, Spec(), Pkg, "1.2.3", created);
        var file = Path.Combine(p, "ProjectSettings", "MingToonInstaller", "manager-request.json");
        var bytes = File.ReadAllBytes(file);
        Require(bytes.Length > 3 && !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF));
        var fields = OnAirDeck.License.MiniJson.ParseObject(Encoding.UTF8.GetString(bytes));
        Equal("createdUtc,mode,packageId,requestedBy,schemaVersion,targetVersion", string.Join(",", fields.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()));
        Equal(1L, fields["schemaVersion"]);
        Equal("legacy-cleanup", fields["mode"]);
        Equal("atelier-manager", fields["requestedBy"]);
        Equal(Pkg, fields["packageId"]);
        Equal("1.2.3", fields["targetVersion"]);
        Equal(created, fields["createdUtc"]);
        MingToonLegacyCleanup.DeleteRequest(p, Spec());
        Require(!File.Exists(file));
        // The result file parses with the fields the contract names; half-written text is null, never an exception.
        var parsed = MingToonLegacyCleanup.ParseResult("{\"schemaVersion\":1,\"requestCreatedUtc\":\"c\",\"attemptId\":\"a\",\"targetVersion\":\"1\",\"phase\":\"Failed\",\"failure\":\"verify: x\",\"updatedUtc\":\"u\"}");
        Equal("c", parsed.RequestCreatedUtc); Equal("Failed", parsed.Phase); Equal("verify: x", parsed.Failure);
        Require(MingToonLegacyCleanup.ParseResult("{\"phase\":") == null);
    }

    private static string NamedZip(string name, string version, string extraEntry)
    {
        var dir = Path.Combine(_root, "pz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + "-" + version + ".zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "package.json", "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"}");
            Write(zip, "Runtime/Code.cs", "code");
            if (extraEntry != null) Write(zip, extraEntry, "evil");
        }
        return path;
    }

    private static LegacyHandoffRequest HReq(params LegacyHandoffDependency[] deps)
    {
        var request = new LegacyHandoffRequest { Package = Pkg, Version = "1.0.0", Product = "MingToon", Folder = OldFolder };
        request.Dependencies.AddRange(deps);
        return request;
    }

    private static void ExtractionGuard()
    {
        var p = NewProject();
        Directory.CreateDirectory(Path.Combine(p, "Library"));
        // A good zip: fully extracted into the stage folder (outside Assets/Packages) at the start, then moved to Packages/<package>.
        var set = MingToonLegacyCleanup.Stage(p, HReq(), NamedZip(Pkg, "1.0.0", null));
        Equal(1, set.Packages.Count);
        Require(set.Packages[0].Files.ContainsKey("package.json") && set.Packages[0].Files.ContainsKey("Runtime/Code.cs"));
        Require(!Directory.Exists(Path.Combine(p, "Packages", Pkg)));
        var installed = MingToonLegacyCleanup.InstallStaged(p, Pkg);
        var target = Path.Combine(p, "Packages", Pkg);
        Equal(target, installed[0]);
        Require(File.Exists(Path.Combine(target, "package.json")) && File.Exists(Path.Combine(target, "Runtime", "Code.cs")));
        Equal("1.0.0", InstalledPackages.InstalledVersion(p, Pkg));
        Require(!Directory.Exists(Path.Combine(MingToonLegacyCleanup.StageFolder(p, Pkg), "stage", Pkg)));

        // Traversal entries (../, absolute) fail at the start: nothing is staged, written outside, or moved into Packages/.
        foreach (var evil in new[] { "../evil.txt", "..\\evil.txt", "/evil.txt", "Runtime/../../evil.txt" })
        {
            var q = NewProject();
            Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(q, HReq(), NamedZip(Pkg, "1.0.0", evil)));
            Require(!Directory.Exists(Path.Combine(q, "Packages", Pkg)) && !Directory.Exists(MingToonLegacyCleanup.StageFolder(q, Pkg)));
            Require(!File.Exists(Path.Combine(q, "evil.txt")) && !File.Exists(Path.Combine(Path.GetDirectoryName(q), "evil.txt")));
        }
        // A zip that is another package or version is refused (identity check), and a missing stage too.
        var w = NewProject();
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(w, HReq(), NamedZip(Other, "1.0.0", null)));
        Require(!Directory.Exists(MingToonLegacyCleanup.StageFolder(w, Pkg)));
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.InstallStaged(NewProject(), Pkg));
    }

    private static void NoMingToonReference()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Editor", "OnAirDeck.UnityManager.Editor.asmdef"))) dir = Path.GetDirectoryName(dir);
        Require(dir != null);
        var asmdefs = Directory.GetFiles(dir, "*.asmdef", SearchOption.AllDirectories).Where(f => f.IndexOf("Tools~", StringComparison.Ordinal) < 0).ToArray();
        Require(asmdefs.Length >= 2);
        foreach (var asmdef in asmdefs)
        {
            var fields = OnAirDeck.License.MiniJson.ParseObject(File.ReadAllText(asmdef));
            object references;
            if (!fields.TryGetValue("references", out references)) continue;
            foreach (var reference in (List<object>)references)
                Require(((string)reference).IndexOf("MingToon", StringComparison.OrdinalIgnoreCase) < 0);
        }
        foreach (var source in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => f.IndexOf("Tools~", StringComparison.Ordinal) < 0))
            foreach (var line in File.ReadLines(source))
                if (line.TrimStart().StartsWith("using ", StringComparison.Ordinal) && line.IndexOf("MingToon", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new Exception(source + " has a using of a MingToon namespace");
    }

    private static void RenewalStatus()
    {
        LicenseRenewalStatus.Clear();
        var changes = 0;
        Action handler = () => changes++;
        LicenseRenewalStatus.Changed += handler;
        try
        {
            Require(!LicenseRenewalStatus.Refuse("L1", "A", new InvalidOperationException("network")));      // not a server answer
            Require(!LicenseRenewalStatus.Refuse("L1", "A", new ApiException("ok", 200, "", "")));
            Require(LicenseRenewalStatus.For("L1") == null);
            Require(LicenseRenewalStatus.Refuse("L1", "A", new ApiException("gone", 403, "license_revoked", "")));
            Equal("license_revoked", LicenseRenewalStatus.For("L1").Code);
            Require(!LicenseRenewalStatus.For("L1").ShownByDeviceNotice);
            Require(LicenseRenewalStatus.Refuse("L2", "B", new ApiException("x", 500, "", "")));
            Equal("http_500", LicenseRenewalStatus.For("L2").Code);
            Require(LicenseRenewalStatus.Refuse("L3", "C", new DeviceLimitException("limit", "", null, null)));
            Require(LicenseRenewalStatus.For("L3").ShownByDeviceNotice);
            Equal(3, LicenseRenewalStatus.All().Count);
            LicenseRenewalStatus.Succeed("L1");
            Require(LicenseRenewalStatus.For("L1") == null);
            LicenseRenewalStatus.Prune(new[] { "l3" });                                                       // case-insensitive, L2 drops
            Equal(1, LicenseRenewalStatus.All().Count);
            Require(changes >= 4);
        }
        finally
        {
            LicenseRenewalStatus.Changed -= handler;
            LicenseRenewalStatus.Clear();
        }
    }
}
