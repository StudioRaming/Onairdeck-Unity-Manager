using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// Astra review round 1 (R01-R11), contract v9.5. One check per fix. Real sources: LegacyHandoff, MingToonLegacyCleanup (Stage, InstallStaged),
// GuidCollisions, LegacyInstall (IsPure, TryGuard, Restore), LegacyMove manifests, AtomicFile, JsonInventoryReader. R12 is in ReviewChecks.
internal static partial class LegacyChecks
{
    public static void RunAstraR1(Action<string, Action> check)
    {
        check("R01: a hand-off state that cannot be saved or read back stops the start before any request; a later failed mark is visible", StateWriteFailures);
        check("R02: contract v9.5 size rule (listed size exact, unlisted size any non-empty) and the content checks stop before the request", SizeAndContentChecks);
        check("R03: a package folder under Assets/ is never moved: collisions inside it are refused, TryGuard refuses its descendants", ExternalPackageUnderAssets);
        check("R04: a folder moves as a whole only when every file is a proven product file; one stray file moves only the colliding files", PureNeedsEveryFile);
        check("R05: asset and .meta are restored as a pair: occupied .meta refuses, a .meta left behind is moved on the retry, a lost item is blocked", RestorePairs);
        check("R06: the backup manifest is written atomically with a last good copy; an unreadable one is listed as needing recovery", ManifestRecovery);
        check("R07: every inventory entry needs a 64-hex SHA-256; null, number, empty, short or non-hex makes the inventory invalid", InventoryHashes);
        check("R08: the package is extracted completely into the stage before the request; a file/folder clash fails there", StageFullExtraction);
        check("R09: a crash after the move resumes at Resolve when the target matches the stage manifest; other content is refused", ResumeAfterMove);
        check("R10: a GUID used outside the old product folder (protected folder, external package, elsewhere) is refused before the request and again at install", GuidCollisionsOutsideLegacy);
        check("R11: dependencies are staged before the request and installed with the package; an unstageable one refuses; Prepare passes them on", DependenciesRideAlong);
    }

    // Snapshot without the empty folders a failed attempt may leave under Library/.
    private static string FilesOnly(string project) { return string.Join(((char)10).ToString(), Snapshot(project).Split((char)10).Where(l => !l.StartsWith("dir ")).ToArray()); }

    private static string RequestFile(string project) { return Path.Combine(project, Spec().RequestPath.Replace('/', Path.DirectorySeparatorChar)); }

    private static string StateFile(string project) { return Path.Combine(project, "Library", "OnAirDeck", "handoff-" + Pkg + ".json"); }

    // ---------- R01 ----------

    private static void StateWriteFailures()
    {
        var t0 = new DateTime(2026, 10, 29, 5, 0, 0, DateTimeKind.Utc);
        try
        {
            // The state cannot be saved: Start throws, no request and no state exist (the installer would delete without a backup).
            var p = NewProject();
            OldInstall(p);
            AtomicFile.FaultHook = path => { if (path.Contains("handoff-" + Pkg + ".json")) throw new IOException("disk full"); };
            Throws<PackageInstallException>(() => LegacyHandoff.Start(p, Spec(), HReq(), t0));
            Throws<PackageInstallException>(() => LegacyHandoff.Begin(p, HReq(), t0));
            AtomicFile.FaultHook = null;
            Require(!File.Exists(RequestFile(p)) && !File.Exists(StateFile(p)));
            Equal(HandoffKind.None, LegacyHandoff.Status(p, Pkg, t0, false, false).Kind);

            // The request cannot be written: the state that was saved goes away again, nothing waits for an installer that never starts.
            AtomicFile.FaultHook = path => { if (path.Contains(Path.GetFileName(Spec().RequestPath))) throw new IOException("locked"); };
            Throws<PackageInstallException>(() => LegacyHandoff.Start(p, Spec(), HReq(), t0));
            AtomicFile.FaultHook = null;
            Require(!File.Exists(RequestFile(p)) && !File.Exists(StateFile(p)));
            Require(!LegacyHandoff.IsWaiting(p));

            // Normal start: state and request both exist, the request carries the createdUtc of the state.
            var created = LegacyHandoff.Start(p, Spec(), HReq(), t0);
            Require(File.Exists(RequestFile(p)) && File.Exists(StateFile(p)));
            Require(File.ReadAllText(RequestFile(p)).Contains(created));
            Require(LegacyHandoff.ImportOutstanding(p, Pkg));

            // A later mark that cannot be saved throws (never silent); the poller reports it and Status shows Stopped with "state:".
            AtomicFile.FaultHook = path => { if (path.Contains("handoff-" + Pkg + ".json")) throw new IOException("read-only"); };
            var error = Capture(() => LegacyHandoff.MarkImportDone(p, Pkg, t0.AddSeconds(5)));
            Require(error != null && error.Contains("read-only"));
            LegacyHandoff.ReportWriteFailure(Pkg, error);
            Require(LegacyHandoff.HasWriteFailure(Pkg));
            var status = LegacyHandoff.Status(p, Pkg, t0.AddSeconds(6), false, true);
            Equal(HandoffKind.Stopped, status.Kind);
            Require(status.Reason.StartsWith("state:"));
            Require(!LegacyHandoff.IsWaiting(p));
            // Status's own progress write never throws either: a new result makes progress, the write fails, the answer is Stopped.
            LegacyHandoff.Clear(p, Pkg);
            AtomicFile.FaultHook = null;
            string created2;
            var q = StartedProject(t0, false, out created2);
            AtomicFile.FaultHook = path => { if (path.Contains("handoff-" + Pkg + ".json")) throw new IOException("read-only"); };
            Result(q, created2, "LegacyRemovedForManager", null, "a", "u1");
            status = LegacyHandoff.Status(q, Pkg, t0.AddSeconds(10), false, true);
            Equal(HandoffKind.Stopped, status.Kind);
            Require(status.Reason.StartsWith("state:"));
            AtomicFile.FaultHook = null;
            LegacyHandoff.Clear(q, Pkg);
            Require(!LegacyHandoff.HasWriteFailure(Pkg));
        }
        finally
        {
            AtomicFile.FaultHook = null;
            LegacyHandoff.Clear(Path.Combine(_root, "none"), Pkg);
        }
    }

    // ---------- R02 ----------

    private static void SizeAndContentChecks()
    {
        Require(LegacyInstallerFile.SizeMatches(10, 10) && !LegacyInstallerFile.SizeMatches(10, 11));
        Require(LegacyInstallerFile.SizeMatches(0, 5));
        Require(!LegacyInstallerFile.SizeMatches(0, 0) && !LegacyInstallerFile.SizeMatches(10, 0) && !LegacyInstallerFile.SizeMatches(-1, 0));
        // The content check of the package: a zip that is another package or version never reaches the request (no stage, no state, no request).
        var p = NewProject();
        OldInstall(p);
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(p, HReq(), NamedZip(Pkg, "9.9.9", null)));
        Require(!Directory.Exists(MingToonLegacyCleanup.StageFolder(p, Pkg)) && !File.Exists(RequestFile(p)) && !File.Exists(StateFile(p)));
        // A file that cannot be read stops there too.
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(p, HReq(), Path.Combine(_root, "does-not-exist.zip")));
        Require(!File.Exists(RequestFile(p)) && !File.Exists(StateFile(p)));
    }

    // ---------- R03 ----------

    private static void ExternalPackageUnderAssets()
    {
        var p = NewProject();
        Put(p, "Assets/Ext/package.json", "{\"name\":\"com.vendor.ext\",\"version\":\"2.0.0\"}");
        Put(p, "Assets/Ext/package.json.meta", Meta("99999999999999999999999999999999"));
        Put(p, "Assets/Ext/Stub.cs", "stub");
        Put(p, "Assets/Ext/Stub.cs.meta", Meta(G1));
        Put(p, "Assets/Ext/Deep/Inner.cs", "inner");
        Put(p, "Assets/Ext/Deep/Inner.cs.meta", Meta(G2));
        var zip = Zip(Other, "1.0.0", "", G1, G2);
        var before = Snapshot(p);
        // The collisions sit inside a package: no plan item (neither the files nor a folder), so nothing is ever moved.
        Equal(0, LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) }).Items.Count);
        Equal(before, Snapshot(p));
        // The scan reports the boundary and names the package in the refusal.
        var found = GuidCollisions.Scan(p, new HashSet<string> { G1, G2 }, new string[0]);
        Equal(2, found.Count);
        Require(found.All(c => c.AssetsPackage == "Assets/Ext" && c.PackageName == "com.vendor.ext" && c.Container.Length == 0));
        Require(GuidCollisions.Refusal(found).Contains("com.vendor.ext"));
        // The guard refuses every descendant of a package folder, even when the path itself holds no package.json.
        string rel;
        Require(!LegacyInstall.TryGuard(p, "Assets/Ext/Stub.cs", out rel));
        Require(!LegacyInstall.TryGuard(p, "Assets/Ext/Deep", out rel));
        Require(!LegacyInstall.TryGuard(p, "Assets/Ext/Deep/Inner.cs", out rel));
        // Plain Assets content next to it still moves (the control: the boundary only blocks the package).
        Put(p, "Assets/Strays/B.cs", "b");
        Put(p, "Assets/Strays/B.cs.meta", Meta(G3));
        var plan = LegacyInstall.Detect(p, new[] { Probe(Other, Zip(Other, "1.0.0", "", G1, G3), null) });
        Equal("Assets/Strays", string.Join(",", Paths(plan).ToArray()));
    }

    // ---------- R04 ----------

    private static void PureNeedsEveryFile()
    {
        var p = NewProject();
        Put(p, "Assets/Old/A.cs", "a");
        Put(p, "Assets/Old/A.cs.meta", Meta(G1));
        Put(p, "Assets/Old/notes.txt", "the user's notes, no .meta");
        var zip = Zip(Other, "1.0.0", "", G1);
        var before = Snapshot(p);
        // A file without a .meta is not proven to be a product file: the folder stays, only the colliding file moves.
        Equal("Assets/Old/A.cs", string.Join(",", Paths(LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) })).ToArray()));
        Equal(before, Snapshot(p));
        // The same folder without the stray file is exactly the product: it moves whole.
        File.Delete(Path.Combine(p, "Assets", "Old", "notes.txt"));
        Equal("Assets/Old", string.Join(",", Paths(LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) })).ToArray()));
        // A file whose own .meta carries a GUID the package does not bring is the user's too.
        Put(p, "Assets/Old/mine.txt", "mine");
        Put(p, "Assets/Old/mine.txt.meta", Meta(G3));
        Equal("Assets/Old/A.cs", string.Join(",", Paths(LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) })).ToArray()));
    }

    // ---------- R05 / R06 ----------

    private static LegacyMove MovedFolder(string p)
    {
        Put(p, "Assets/Old/a.txt", "a");
        Put(p, "Assets/Old.meta", Meta(G1));
        var plan = new LegacyPlan();
        plan.Items.Add(new LegacyItem { Path = "Assets/Old", Product = "x" });
        return LegacyInstall.Move(p, plan, Now, Pkg, "1.0.0");
    }

    private static void RestorePairs()
    {
        // 1. Unity made a new .meta at the original place while the asset is in the backup: the pair is not restored over it.
        var p = NewProject();
        var move = MovedFolder(p);
        Put(p, "Assets/Old.meta", Meta(G3));
        var before = Snapshot(p);
        var result = LegacyMove.RestoreFromManifest(move.ManifestPath, p);
        Require(!result.Ok && result.Blocked.Contains("Assets/Old"));
        Equal(before, Snapshot(p));                                    // nothing moved, nothing half restored
        // The in-memory restore refuses the same way and reports the item.
        Equal("Assets/Old", string.Join(",", move.Restore().ToArray()));
        Equal(before, Snapshot(p));

        // 2. The asset is back, its .meta stayed in the backup (an earlier restore stopped halfway): the retry moves the .meta.
        var q = NewProject();
        var m2 = MovedFolder(q);
        Directory.Move(Path.Combine(m2.BackupFolder, "Assets", "Old"), Path.Combine(q, "Assets", "Old"));
        Require(File.Exists(Path.Combine(m2.BackupFolder, "Assets", "Old.meta")));
        result = LegacyMove.RestoreFromManifest(m2.ManifestPath, q);
        Require(result.Ok);
        Require(File.ReadAllText(Path.Combine(q, "Assets", "Old.meta")).Contains(G1));
        Require(!File.Exists(Path.Combine(m2.BackupFolder, "Assets", "Old.meta")));

        // 3. Both are back in the in-memory restore too, and the manifest is restored only then.
        var r = NewProject();
        var m3 = MovedFolder(r);
        Equal(0, m3.Restore().Count);
        Require(Directory.Exists(Path.Combine(r, "Assets", "Old")) && File.Exists(Path.Combine(r, "Assets", "Old.meta")));
        Equal(LegacyMove.StateRestored, MiniJson.GetString(MiniJson.ParseObject(File.ReadAllText(m3.ManifestPath)), "state"));

        // 4. A lost item (neither asset nor .meta anywhere) is blocked, never reported as restored.
        var s = NewProject();
        var m4 = MovedFolder(s);
        Directory.Delete(Path.Combine(m4.BackupFolder, "Assets", "Old"), true);
        File.Delete(Path.Combine(m4.BackupFolder, "Assets", "Old.meta"));
        result = LegacyMove.RestoreFromManifest(m4.ManifestPath, s);
        Require(!result.Ok && result.Blocked.Contains("Assets/Old"));
        Require(LegacyMove.PendingRestores(s).Count == 1);
    }

    private static void ManifestRecovery()
    {
        var p = NewProject();
        var move = MovedFolder(p);
        var manifest = move.ManifestPath;
        // Two writes happened (moving, moved): the first one stays as the last good copy.
        Require(File.Exists(manifest) && File.Exists(manifest + ".prev"));
        Equal("moved", MiniJson.GetString(MiniJson.ParseObject(File.ReadAllText(manifest)), "state"));
        Require(Directory.GetFiles(move.BackupFolder, "*.tmp").Length == 0);

        // The manifest is cut off (a crash during a plain overwrite would do this): the last good copy carries it, the move is still listed.
        File.WriteAllText(manifest, "{\"format\":1,\"packa");
        var pending = LegacyMove.PendingRestores(p);
        Equal(1, pending.Count);
        Equal("Assets/Old", string.Join(",", pending[0].Paths.ToArray()));
        Require(LegacyMove.RestoreFromManifest(manifest, p).Ok);
        Require(Directory.Exists(Path.Combine(p, "Assets", "Old")) && File.Exists(Path.Combine(p, "Assets", "Old.meta")));

        // Both copies unreadable and the folder holds moved files: listed as needing recovery, never skipped; restoring says so.
        var q = NewProject();
        var m2 = MovedFolder(q);
        File.WriteAllText(m2.ManifestPath, "garbage");
        File.WriteAllText(m2.ManifestPath + ".prev", "garbage");
        pending = LegacyMove.PendingRestores(q);
        Equal(1, pending.Count);
        Equal(LegacyMove.StateUnreadable, pending[0].State);
        Equal(0, pending[0].Paths.Count);
        var unreadable = LegacyMove.RestoreFromManifest(m2.ManifestPath, q);
        Require(!unreadable.Ok && unreadable.Message.Length > 0);
        Require(Directory.Exists(Path.Combine(m2.BackupFolder, "Assets", "Old")));     // nothing moved
        // A backup folder with only a broken manifest and nothing else has nothing to recover.
        var e = NewProject();
        var empty = Path.Combine(PackageTransaction.BackupRoot(e), "legacy-20261029-000000");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, LegacyMove.ManifestName), "garbage");
        Equal(0, LegacyMove.PendingRestores(e).Count);

        // A manifest write that fails midway leaves the old, whole file (temp + replace), not a half written one.
        var f = NewProject();
        var m3 = MovedFolder(f);
        var wholeBefore = File.ReadAllText(m3.ManifestPath);
        try
        {
            AtomicFile.FaultHook = path => { if (path.Contains(LegacyMove.ManifestName)) throw new IOException("disk full"); };
            m3.MarkInstalled();                                                         // a failed state update never fails the install
        }
        finally { AtomicFile.FaultHook = null; }
        Equal(wholeBefore, File.ReadAllText(m3.ManifestPath));
        Equal(1, LegacyMove.PendingRestores(f).Count);
    }

    // ---------- R07 ----------

    private static void InventoryHashes()
    {
        var reader = new JsonInventoryReader();
        Dictionary<string, string> files;
        var good = Sha("x");
        Require(reader.TryRead(Encoding.UTF8.GetBytes("{\"files\":{\"A.cs\":\"" + good + "\",\"B.cs\":\"" + good.ToUpperInvariant() + "\"}}"), out files));
        Equal(good, files["B.cs"]);                                          // stored lowercase
        foreach (var bad in new[] { "null", "5", "\"\"", "\"abc\"", "\"" + good.Substring(1) + "\"", "\"" + new string('g', 64) + "\"", "true", "{}" })
        {
            Require(!reader.TryRead(Encoding.UTF8.GetBytes("{\"files\":{\"A.cs\":\"" + good + "\",\"Gone.cs\":" + bad + "}}"), out files));
            Require(files == null);
        }
        // Through the plan: one invalid entry blocks the move and nothing moves (no hand-off handler, so the inventory decides).
        LegacyInstall.Handlers.Clear();
        var p = NewProject();
        OldInstall(p);
        var inventory = "{\"files\":{\"Shader.shader\":\"" + Sha("legacy shader") + "\",\"User/mine.txt\":\"" + Sha("added by the user") + "\",\"Gone.cs\":\"\"}}";
        var plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, ZipWithInventory(inventory, "inventory.json"), null) });
        Require(plan.Items[0].Blocked);
        Equal("inventory-unreadable", plan.Items[0].BlockedReason);
        // A reader that lets an empty hash through still never makes it "any content".
        var folderItem = new LegacyItem { Path = "Assets/StudioRaming/MingToon", Product = "x" };
        var lax = new Dictionary<string, string> { { "Shader.shader", "" }, { "User/mine.txt", Sha("added by the user") } };
        Require(LegacyInventory.Unmatched(p, folderItem, lax).Contains("Assets/StudioRaming/MingToon/Shader.shader"));
    }

    // ---------- R08 ----------

    private static string ClashZip()
    {
        var dir = Path.Combine(_root, "cz-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, Pkg + ".zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "package.json", "{\"name\":\"" + Pkg + "\",\"version\":\"1.0.0\"}");
            Write(zip, "Runtime", "a file");
            Write(zip, "Runtime/Code.cs", "under the same name");
        }
        return path;
    }

    private static void StageFullExtraction()
    {
        var p = NewProject();
        OldInstall(p);
        var before = FilesOnly(p);
        // A file and a folder with one name: only a real extraction notices; it fails here, before the request, and leaves no stage.
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(p, HReq(), ClashZip()));
        Require(!Directory.Exists(MingToonLegacyCleanup.StageFolder(p, Pkg)) && !File.Exists(RequestFile(p)) && !File.Exists(StateFile(p)));
        Equal(before, FilesOnly(p));
        // A good zip: extracted completely, every file hashed in stage.json (readable back), Packages/ untouched.
        var zip = NamedZip(Pkg, "1.0.0", null);
        var set = MingToonLegacyCleanup.Stage(p, HReq(), zip);
        var read = MingToonLegacyCleanup.ReadStage(p, Pkg);
        Require(read != null && read.Main == Pkg && read.Packages.Count == 1);
        Equal(set.Packages[0].Files["Runtime/Code.cs"], read.Packages[0].Files["Runtime/Code.cs"]);
        Equal(Sha("code"), read.Packages[0].Files["Runtime/Code.cs"]);
        Require(File.Exists(Path.Combine(MingToonLegacyCleanup.StageFolder(p, Pkg), "stage", Pkg, "Runtime", "Code.cs")));
        Require(!Directory.Exists(Path.Combine(p, "Packages", Pkg)));
        // A stage manifest with a bad hash is not a stage.
        File.WriteAllText(Path.Combine(MingToonLegacyCleanup.StageFolder(p, Pkg), "stage.json"), File.ReadAllText(Path.Combine(MingToonLegacyCleanup.StageFolder(p, Pkg), "stage.json")).Replace(Sha("code"), "zz"));
        Require(MingToonLegacyCleanup.ReadStage(p, Pkg) == null);
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.InstallStaged(p, Pkg));
    }

    // ---------- R09 ----------

    private static void ResumeAfterMove()
    {
        var t0 = new DateTime(2026, 10, 29, 6, 0, 0, DateTimeKind.Utc);
        string created;
        var p = StartedProject(t0, false, out created);
        MingToonLegacyCleanup.Stage(p, HReq(), NamedZip(Pkg, "1.0.0", null));
        Result(p, created, "LegacyRemovedForManager", null, "a", "u1");
        Require(LegacyHandoff.InstallDue(p, Pkg));
        var target = Path.Combine(p, "Packages", Pkg);
        // The folders moved, then the editor went away before the step was saved: the step is still due and the target is accepted.
        MingToonLegacyCleanup.InstallStaged(p, Pkg);
        Require(Directory.Exists(target) && LegacyHandoff.InstallDue(p, Pkg) && !LegacyHandoff.InstallMoved(p, Pkg));
        Equal(1, MingToonLegacyCleanup.InstallStaged(p, Pkg).Count);                     // resume: no error, same folder
        LegacyHandoff.MarkInstallMoved(p, Pkg, t0.AddSeconds(5));
        Require(LegacyHandoff.InstallMoved(p, Pkg) && LegacyHandoff.InstallDue(p, Pkg));  // Resolve not requested yet
        LegacyHandoff.MarkInstallStarted(p, Pkg, t0.AddSeconds(6), "");
        Require(!LegacyHandoff.InstallDue(p, Pkg));
        // A .meta Unity wrote next to the files is fine; any other extra file, a changed file or a missing one is another folder.
        Put(p, "Packages/" + Pkg + "/Runtime.meta", Meta(G2));
        Equal(1, MingToonLegacyCleanup.InstallStaged(p, Pkg).Count);
        Put(p, "Packages/" + Pkg + "/Runtime/Extra.cs", "mine");
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.InstallStaged(p, Pkg));
        File.Delete(Path.Combine(target, "Runtime", "Extra.cs"));
        Put(p, "Packages/" + Pkg + "/Runtime/Code.cs", "changed");
        var kept = Snapshot(p);
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.InstallStaged(p, Pkg));
        Equal(kept, Snapshot(p));                                                         // refused: nothing touched
    }

    // ---------- R10 ----------

    private static void GuidCollisionsOutsideLegacy()
    {
        var zip = Zip(Pkg, "1.0.0", "", G1);
        // Inside the old product folder: the installer deletes it, so it is no collision at the start.
        var p = NewProject();
        OldInstall(p);
        Put(p, OldFolder + "/Old.cs", "o");
        Put(p, OldFolder + "/Old.cs.meta", Meta(G1));
        MingToonLegacyCleanup.Stage(p, HReq(), zip);
        // Outside it, anywhere: refused before the request, with the file named, and no stage is left. The protected folder first.
        foreach (var where in new[] { "Assets/MingToonPresets/mine.asset", "Assets/Elsewhere/Deep/x.cs", "Assets/Ext/Stub.cs" })
        {
            var q = NewProject();
            OldInstall(q);
            if (where.StartsWith("Assets/Ext")) Put(q, "Assets/Ext/package.json", "{\"name\":\"com.vendor.ext\",\"version\":\"2.0.0\"}");
            Put(q, where, "x");
            Put(q, where + ".meta", Meta(G1));
            var message = Capture(() => MingToonLegacyCleanup.Stage(q, HReq(), zip));
            Require(message != null && (message.Contains(where) || message.Contains("com.vendor.ext")));
            Require(!Directory.Exists(MingToonLegacyCleanup.StageFolder(q, Pkg)) && !File.Exists(RequestFile(q)) && !File.Exists(StateFile(q)));
        }
        // Something that appeared after the start is caught again right before the move: nothing goes into Packages/.
        var r = NewProject();
        OldInstall(r);
        MingToonLegacyCleanup.Stage(r, HReq(), zip);
        Put(r, "Assets/Late/L.cs", "l");
        Put(r, "Assets/Late/L.cs.meta", Meta(G1));
        var late = Capture(() => MingToonLegacyCleanup.InstallStaged(r, Pkg));
        Require(late != null && late.Contains("Assets/Late/L.cs.meta"));
        Require(!Directory.Exists(Path.Combine(r, "Packages", Pkg)));
        Require(Directory.Exists(Path.Combine(MingToonLegacyCleanup.StageFolder(r, Pkg), "stage", Pkg)));     // still staged for a retry
    }

    // ---------- R11 ----------

    private static void DependenciesRideAlong()
    {
        var depZip = Zip(Other, "2.0.0", "", G2);
        var dep = new LegacyHandoffDependency { Name = Other, Version = "2.0.0", ZipPath = depZip };
        var p = NewProject();
        OldInstall(p);
        var set = MingToonLegacyCleanup.Stage(p, HReq(dep), NamedZip(Pkg, "1.0.0", null));
        Equal(2, set.Packages.Count);
        Equal(Other, set.Packages[0].Name);                                              // dependencies first, the package last
        Equal(Pkg, set.Packages[1].Name);
        Require(!Directory.Exists(Path.Combine(p, "Packages", Other)));
        // A different version of the dependency is installed already: it is set aside (not deleted), then both are moved in order.
        Put(p, "Packages/" + Other + "/package.json", "{\"name\":\"" + Other + "\",\"version\":\"1.0.0\"}");
        Put(p, "Packages/" + Other + "/old.txt", "old");
        var folders = MingToonLegacyCleanup.InstallStaged(p, Pkg);
        Equal(2, folders.Count);
        Equal("2.0.0", InstalledPackages.InstalledVersion(p, Other));
        Equal("1.0.0", InstalledPackages.InstalledVersion(p, Pkg));
        Require(!File.Exists(Path.Combine(p, "Packages", Other, "old.txt")));
        var aside = Directory.GetFiles(Path.Combine(p, "Library", "OnAirDeck", "replaced"), "old.txt", SearchOption.AllDirectories);
        Equal(1, aside.Length);

        // A requested package folder with other content is refused before ANY folder moves (the dependency stays unmoved too).
        var q = NewProject();
        OldInstall(q);
        MingToonLegacyCleanup.Stage(q, HReq(dep), NamedZip(Pkg, "1.0.0", null));
        Put(q, "Packages/" + Pkg + "/mine.txt", "mine");
        Throws<PackageInstallException>(() => MingToonLegacyCleanup.InstallStaged(q, Pkg));
        Require(!Directory.Exists(Path.Combine(q, "Packages", Other)));

        // A dependency that cannot be staged refuses everything before the request: no zip, a missing file, a target folder, a wrong version.
        var bad = new[]
        {
            new LegacyHandoffDependency { Name = Other, Version = "2.0.0", ZipPath = "" },
            new LegacyHandoffDependency { Name = Other, Version = "2.0.0", ZipPath = Path.Combine(_root, "missing.zip") },
            new LegacyHandoffDependency { Name = Other, Version = "2.0.0", ZipPath = depZip, TargetFolder = Path.Combine(_root, "elsewhere") },
            new LegacyHandoffDependency { Name = Other, Version = "3.0.0", ZipPath = depZip }
        };
        foreach (var item in bad)
        {
            var r = NewProject();
            OldInstall(r);
            Throws<PackageInstallException>(() => MingToonLegacyCleanup.Stage(r, HReq(item), NamedZip(Pkg, "1.0.0", null)));
            Require(!Directory.Exists(MingToonLegacyCleanup.StageFolder(r, Pkg)) && !File.Exists(RequestFile(r)) && !File.Exists(StateFile(r)));
        }

        // Prepare hands the other items of the install to the request.
        HandoffOnly();
        var s = NewProject();
        OldInstall(s);
        LegacyHandoffRequest handoff;
        var depProbe = Probe(Other, depZip, null);
        depProbe.Version = "2.0.0";
        LegacyInstall.Prepare(s, new[] { depProbe, HandoffProbe(true) }, true, x => true, Now, out handoff);
        Require(handoff != null);
        Equal(1, handoff.Dependencies.Count);
        Equal(Other, handoff.Dependencies[0].Name);
        Equal(depZip, handoff.Dependencies[0].ZipPath);
    }
}
