using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.UnityManager;

// Contract v9: an old Assets install is moved to the backup, with consent, before a package install. Real sources:
// LegacyInstall, LegacyInstallText, GuidCollisions.Refusal, PackageZip.ParseManifest, PackageTransaction. The Unity half
// (dialog, AssetDatabase.Refresh, InstallAsync wiring) is compile-checked only.
internal static partial class LegacyChecks
{
    private const string Pkg = "com.studioraming.mingtoon";
    private const string Other = "com.studioraming.other";
    private const string G1 = "11111111111111111111111111111111";
    private static string _root;
    private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0);

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "v9");
        Directory.CreateDirectory(_root);
        // The v9 mechanics checks run with a product handler that vouches for every folder; the v9c checks below remove it.
        LegacyInstall.Handlers.Clear();
        LegacyInstall.Handlers.Add(new VouchingHandler());
        check("v9: detection by config table, package.json replacesAssetFolders, marker and GUID collision", Detection);
        check("v9: guard rails (Assets root, folder with package.json, outside the project, Packages) refuse a path", GuardRails);
        check("v9: a plan is empty when nothing old is there; nested items fold into their folder", EmptyAndNested);
        check("v9: declined leaves the tree byte-identical; background and no-callback never move", DeclineAndBackground);
        check("v9: move then install succeeds; the old install sits in Library/OnAirDeck/Backup/legacy-*", MoveAndInstall);
        check("v9: install failure and a failed move put everything back", RestoreOnFailure);
        check("v9b: GUID-only finds move only product files (mixed folder: files alone; pure folder: whole; nested pure folder inside a mixed one)", GuidScope);
        check("v9b: backup-manifest.json state moving -> moved -> installed; a pending move is found and restored", ManifestStates);
        check("v9b: restore refuses when an original place is occupied, rejects tampered manifests, never throws", ManifestRefusals);
        check("v9: batch consent covers only approved paths; an unapproved one is declined", BatchCoverage);
        check("v9: refusal for a foreign package (ko/en/ja) and the dialog texts (10 paths, 'and N more')", Wording);
        LegacyInstall.Handlers.Clear();
        check("v9c: sources 1-3 without an inventory or handler never move; the plan says which folder needs clean-up", NoInventoryNoMove);
        check("v9c: the package's inventory gates the move: exact folder moves; user-added, changed or unreadable does not", InventoryGate);
        check("v9c: a product handler validates and may detach; a throwing or objecting handler blocks", HandlerSeam);
        RunHandoff(check);
        RunAstraR1(check);
        LegacyInstall.Handlers.Clear();
        LegacyInstall.Handlers.Add(new InstallerHandoffHandler());
    }

    // ---------- helpers ----------

    private static string NewProject()
    {
        var project = Path.Combine(_root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        return project;
    }

    private static void Put(string project, string relative, string text)
    {
        var path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string Meta(string guid) { return "fileFormatVersion: 2\nguid: " + guid + "\n"; }

    // Every file with its hash: "byte-identical" means this string does not change.
    private static string Snapshot(string project)
    {
        var sb = new StringBuilder();
        using (var sha = SHA256.Create())
            foreach (var file in Directory.GetFiles(project, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                sb.Append(file.Substring(project.Length)).Append(':').Append(BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(file)))).Append('\n');
        foreach (var dir in Directory.GetDirectories(project, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
            sb.Append("dir ").Append(dir.Substring(project.Length)).Append('\n');
        return sb.ToString();
    }

    private static string Zip(string name, string version, string packageJsonExtra, params string[] metaGuids)
    {
        var dir = Path.Combine(_root, "z-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name + ".zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Write(zip, "package.json", "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"" + packageJsonExtra + "}");
            for (var i = 0; i < metaGuids.Length; i++) Write(zip, "Runtime/F" + i + ".cs.meta", Meta(metaGuids[i]));
            Write(zip, "Runtime/Code.cs", "code");
        }
        return path;
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write(text);
    }

    private static LegacyProbe Probe(string name, string zip, string licenseId)
    {
        return new LegacyProbe { Name = name, Version = "1.0.0", Display = name, ZipPath = zip ?? "", LicenseId = licenseId ?? "" };
    }

    private static List<string> Paths(LegacyPlan plan) { return plan.Items.Select(i => i.Path).OrderBy(p => p, StringComparer.Ordinal).ToList(); }

    // ---------- checks ----------

    private static void Detection()
    {
        // 1. config table: com.studioraming.mingtoon -> Assets/StudioRaming/MingToon
        var p = NewProject();
        Put(p, "Assets/StudioRaming/MingToon/Shader.shader", "s");
        Put(p, "Assets/StudioRaming/MingToon.meta", Meta("aaaa"));
        var plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) });
        Equal("Assets/StudioRaming/MingToon", string.Join(",", Paths(plan).ToArray()));
        Equal("config", plan.Items[0].Source);
        Equal(1, plan.Items[0].Files);

        // 2. the incoming package.json names the folder
        p = NewProject();
        Put(p, "Assets/Vendor/OldTool/a.txt", "a");
        Put(p, "Assets/Vendor/OldTool/b.txt", "b");
        var zip = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/Vendor/OldTool\",\"Assets/Missing\",7]}");
        plan = LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) });
        Equal("Assets/Vendor/OldTool", string.Join(",", Paths(plan).ToArray()));
        Equal("package", plan.Items[0].Source);
        Equal(2, plan.Items[0].Files);
        // Known-only detection does not read zips.
        Equal(0, LegacyInstall.DetectKnown(p, new[] { Probe(Other, zip, null) }).Items.Count);

        // 3. a marker with the same license_id (another id is not ours)
        p = NewProject();
        Put(p, "Assets/StudioRaming/Mine/atelier.product.json", "{\"license_id\":\"L-1\",\"name\":\"Mine\"}");
        Put(p, "Assets/StudioRaming/Mine/x.txt", "x");
        Put(p, "Assets/Else/atelier.product.json", "{\"license_id\":\"L-2\"}");
        plan = LegacyInstall.Detect(p, new[] { Probe(Other, null, "L-1") });
        Equal("Assets/StudioRaming/Mine", string.Join(",", Paths(plan).ToArray()));
        Equal("marker", plan.Items[0].Source);
        Equal(1, LegacyInstall.DetectKnown(p, new[] { Probe(Other, null, "L-1") }).Items.Count);
        Equal(0, LegacyInstall.Detect(p, new[] { Probe(Other, null, "L-9") }).Items.Count);

        // 4. GUID collisions under Assets/: the file's folder, or the lone file straight in Assets/. Inside a package: not ours.
        p = NewProject();
        Put(p, "Assets/Strays/Deep/B.cs.meta", Meta(G1));
        Put(p, "Assets/Strays/Deep/B.cs", "b");
        Put(p, "Assets/Lone.cs.meta", Meta("22222222222222222222222222222222"));
        Put(p, "Assets/Lone.cs", "l");
        Put(p, "Packages/com.vendor.sdk/package.json", "{\"name\":\"com.vendor.sdk\",\"version\":\"3.0.0\"}");
        Put(p, "Packages/com.vendor.sdk/Stub.cs.meta", Meta("33333333333333333333333333333333"));
        zip = Zip(Other, "1.0.0", "", G1, "22222222222222222222222222222222", "33333333333333333333333333333333");
        plan = LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) });
        Equal("Assets/Lone.cs,Assets/Strays", string.Join(",", Paths(plan).ToArray()));
        Require(plan.Items.All(i => i.Source == "guid"));
        Equal(1, plan.Items.First(i => i.Path == "Assets/Strays").Files);   // B.cs; the .meta is not counted

        // Two sources naming one folder give one item.
        p = NewProject();
        Put(p, "Assets/StudioRaming/MingToon/Shader.shader", "s");
        zip = Zip(Pkg, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/StudioRaming/MingToon\"]}");
        Equal(1, LegacyInstall.Detect(p, new[] { Probe(Pkg, zip, null) }).Items.Count);
    }

    private static void GuardRails()
    {
        var p = NewProject();
        Put(p, "Assets/Keep/a.txt", "a");
        Put(p, "Assets/IsPackage/package.json", "{\"name\":\"com.x.y\",\"version\":\"1.0.0\"}");
        Put(p, "Assets/IsPackage/a.txt", "a");
        Put(p, "Packages/com.x.y/a.txt", "a");
        Put(p, "outside/a.txt", "a");
        string rel;
        Require(LegacyInstall.TryGuard(p, "Assets/Keep", out rel) && rel == "Assets/Keep");
        Require(LegacyInstall.TryGuard(p, "Assets\\Keep\\", out rel) && rel == "Assets/Keep");     // separators and trailing slash
        Require(LegacyInstall.TryGuard(p, "assets/Keep/a.txt", out rel) && rel == "Assets/Keep/a.txt");
        Require(!LegacyInstall.TryGuard(p, "Assets", out rel));                  // never Assets itself
        Require(!LegacyInstall.TryGuard(p, "Assets/", out rel));
        Require(!LegacyInstall.TryGuard(p, "Assets/IsPackage", out rel));         // holds a package.json
        Require(!LegacyInstall.TryGuard(p, "Assets/IsPackage/package.json", out rel));
        Require(!LegacyInstall.TryGuard(p, "Assets/../outside", out rel));        // leaves Assets
        Require(!LegacyInstall.TryGuard(p, "Assets/Keep/../../outside", out rel));
        Require(!LegacyInstall.TryGuard(p, "../outside", out rel));
        Require(!LegacyInstall.TryGuard(p, "Packages/com.x.y", out rel));
        Require(!LegacyInstall.TryGuard(p, "outside", out rel));
        Require(!LegacyInstall.TryGuard(p, Path.Combine(p, "outside"), out rel)); // absolute (also holds a drive colon on Windows)
        Require(!LegacyInstall.TryGuard(p, "/etc", out rel));
        Require(!LegacyInstall.TryGuard(p, "", out rel) && !LegacyInstall.TryGuard(p, null, out rel));
        Require(!LegacyInstall.TryGuard(p, "Assets/Missing", out rel));           // nothing there

        // A package.json that names a bad path never reaches the plan.
        var zip = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets\",\"Assets/IsPackage\",\"Packages/com.x.y\",\"../outside\"]}");
        Equal(0, LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) }).Items.Count);
        // Move itself re-checks: a hand-made plan with a bad path moves nothing.
        var bad = new LegacyPlan();
        bad.Items.Add(new LegacyItem { Path = "Assets", Product = "x" });
        var before = Snapshot(p);
        Throws<PackageInstallException>(() => LegacyInstall.Move(p, bad, Now));
        Equal(before, Snapshot(p));
    }

    private static void EmptyAndNested()
    {
        var p = NewProject();
        Require(LegacyInstall.Detect(p, new[] { Probe(Pkg, null, "L-1") }).IsEmpty);
        Require(LegacyInstall.Prepare(p, new[] { Probe(Pkg, null, "L-1") }, true, plan => true, Now) == null);
        // A folder and a collision file inside it: one item, the folder (it holds an asset of its own, so the scan stops there).
        Put(p, "Assets/StudioRaming/MingToon/Own.cs", "own");
        Put(p, "Assets/StudioRaming/MingToon/Own.cs.meta", Meta("12121212121212121212121212121212"));
        Put(p, "Assets/StudioRaming/MingToon/Sub/B.cs.meta", Meta(G1));
        Put(p, "Assets/StudioRaming/MingToon/Sub/B.cs", "b");
        var zip = Zip(Pkg, "1.0.0", "", G1);
        var detected = LegacyInstall.Detect(p, new[] { Probe(Pkg, zip, null) });
        Equal("Assets/StudioRaming/MingToon", string.Join(",", Paths(detected).ToArray()));
    }

    private static string OldInstall(string project)
    {
        Put(project, "Assets/StudioRaming/MingToon/Shader.shader", "legacy shader");
        Put(project, "Assets/StudioRaming/MingToon/User/mine.txt", "added by the user");
        Put(project, "Assets/StudioRaming/MingToon.meta", Meta("44444444444444444444444444444444"));
        Put(project, "Assets/Unrelated/keep.txt", "keep");
        return Snapshot(project);
    }

    private static void DeclineAndBackground()
    {
        var p = NewProject();
        var before = OldInstall(p);
        var probes = new[] { Probe(Pkg, null, null) };
        var asked = 0;
        Throws<LegacyDeclinedException>(() => LegacyInstall.Prepare(p, probes, true, plan => { asked++; return false; }, Now));
        Equal(1, asked);
        Equal(before, Snapshot(p));                                 // "설치 안 함": byte-identical, no backup folder
        Require(!Directory.Exists(PackageTransaction.BackupRoot(p)));
        Require(new LegacyDeclinedException() is OperationCanceledException);   // the window's "cancelled" path

        // Background: never asks, never moves; the text says the item needs clean-up.
        asked = 0;
        var error = Capture(() => LegacyInstall.Prepare(p, probes, false, plan => { asked++; return true; }, Now));
        Equal(0, asked);
        Require(error != null);
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, probes, false, plan => true, Now));
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, probes, true, null, Now));   // no callback: never moves
        Equal(before, Snapshot(p));
        LegacyInstallText.LanguageProvider = () => 0;
        try { Require(Capture(() => LegacyInstall.Prepare(p, probes, false, null, Now)).Contains("이전 설치 정리 필요")); }
        finally { LegacyInstallText.LanguageProvider = () => 1; }
    }

    private static void MoveAndInstall()
    {
        var p = NewProject();
        OldInstall(p);
        var zip = Zip(Pkg, "1.0.0", "", "55555555555555555555555555555555");
        var items = new List<InstallItem> { new InstallItem { Name = Pkg, Version = "1.0.0", ZipPath = zip } };
        LegacyPlan shown = null;
        var move = LegacyInstall.Prepare(p, new[] { Probe(Pkg, zip, null) }, true, plan => { shown = plan; return true; }, Now);
        Require(move != null && shown != null);
        Equal("Assets/StudioRaming/MingToon", string.Join(",", Paths(shown).ToArray()));
        Equal(2, shown.Items[0].Files);                              // the user's own file counts and moves too
        Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));
        Require(!File.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon.meta")));
        Require(File.Exists(Path.Combine(p, "Assets", "Unrelated", "keep.txt")));
        var backup = Path.Combine(PackageTransaction.BackupRoot(p), "legacy-20261028-120000");
        Equal(backup, move.BackupFolder);
        Equal("legacy shader", File.ReadAllText(Path.Combine(backup, "Assets", "StudioRaming", "MingToon", "Shader.shader")));
        Equal("added by the user", File.ReadAllText(Path.Combine(backup, "Assets", "StudioRaming", "MingToon", "User", "mine.txt")));
        Require(File.Exists(Path.Combine(backup, "Assets", "StudioRaming", "MingToon.meta")));

        // The scan after the move is clean, and the real install (which still refuses a legacy folder) goes through.
        Equal(0, GuidCollisions.Scan(p, GuidCollisions.ReadZipGuids(zip), new string[0]).Count);
        PackageTransaction.Execute(p, items, Now);
        Require(File.Exists(Path.Combine(p, "Packages", Pkg, "package.json")));
        Require(InstalledPackages.LegacyRefusal(p, Pkg) == null);
        // A second move in the same second does not overwrite the first backup.
        Put(p, "Assets/StudioRaming/MingToon/Again.txt", "again");
        var second = LegacyInstall.Prepare(p, new[] { Probe(Pkg, null, null) }, true, plan => true, Now);
        Require(second.BackupFolder != backup && File.Exists(Path.Combine(backup, "Assets", "StudioRaming", "MingToon", "Shader.shader")));
    }

    private static void RestoreOnFailure()
    {
        // The install fails after the move: Restore() puts every item back.
        var p = NewProject();
        var before = OldInstall(p);
        var zip = Zip(Pkg, "1.0.0", "", "55555555555555555555555555555555");
        var move = LegacyInstall.Prepare(p, new[] { Probe(Pkg, zip, null) }, true, plan => true, Now);
        Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));
        PackageTransaction.FaultHook = point => { if (point == "stage:" + Pkg) throw new IOException("disk full"); };
        try { Throws<PackageInstallException>(() => PackageTransaction.Execute(p, new[] { new InstallItem { Name = Pkg, Version = "1.0.0", ZipPath = zip } }, Now)); }
        finally { PackageTransaction.FaultHook = null; }
        Equal(0, move.Restore().Count);
        Equal(RemoveEmptyBackup(p, before), Snapshot(p));

        // A move that fails halfway restores what it already moved and throws.
        p = NewProject();
        Put(p, "Assets/A/a.txt", "a");
        Put(p, "Assets/B/b.txt", "b");
        before = Snapshot(p);
        var plan2 = new LegacyPlan();
        plan2.Items.Add(new LegacyItem { Path = "Assets/A", Product = "x" });
        plan2.Items.Add(new LegacyItem { Path = "Assets/B", Product = "x" });
        LegacyInstall.FaultHook = point => { if (point == "legacy-move:Assets/B") throw new IOException("locked"); };
        try { Throws<PackageInstallException>(() => LegacyInstall.Move(p, plan2, Now)); }
        finally { LegacyInstall.FaultHook = null; }
        Equal(RemoveEmptyBackup(p, before), Snapshot(p));
        Require(File.Exists(Path.Combine(p, "Assets", "A", "a.txt")) && File.Exists(Path.Combine(p, "Assets", "B", "b.txt")));

        // Something appeared at the original place: that item stays in the backup and is reported, never overwritten.
        p = NewProject();
        OldInstall(p);
        move = LegacyInstall.Prepare(p, new[] { Probe(Pkg, null, null) }, true, plan => true, Now);
        Put(p, "Assets/StudioRaming/MingToon/new.txt", "new");
        var failed = move.Restore();
        Equal(1, failed.Count);
        Require(File.Exists(Path.Combine(move.BackupFolder, "Assets", "StudioRaming", "MingToon", "Shader.shader")));
        Equal("new", File.ReadAllText(Path.Combine(p, "Assets", "StudioRaming", "MingToon", "new.txt")));
    }

    // The backup root is created by a move even when everything went back; the project must otherwise match.
    private static string RemoveEmptyBackup(string project, string before)
    {
        var lib = Path.Combine(project, "Library");
        // Only the manifest (state restored) may be left behind: it is the record of the move.
        if (Directory.Exists(lib) && Directory.GetFiles(lib, "*", SearchOption.AllDirectories).All(f => Path.GetFileName(f).StartsWith(LegacyMove.ManifestName, StringComparison.Ordinal))) Directory.Delete(lib, true);
        return before;
    }

    private static void BatchCoverage()
    {
        var p = NewProject();
        OldInstall(p);
        Put(p, "Assets/Other/OldTool/a.txt", "a");
        var zip = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/Other/OldTool\"]}");
        // The combined dialog could only list what DetectKnown finds (config table); the zip rule shows up after the download.
        var known = LegacyInstall.DetectKnown(p, new[] { Probe(Pkg, null, null), Probe(Other, zip, null) });
        Equal("Assets/StudioRaming/MingToon", string.Join(",", Paths(known).ToArray()));
        var approved = new HashSet<string>(known.Items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var before = Snapshot(p);

        // Item 1 is covered by the one consent and moves; item 2 found something nobody approved: declined, tree unchanged by it.
        var first = LegacyInstall.Prepare(p, new[] { Probe(Pkg, null, null) }, true, plan => plan.IsCoveredBy(approved), Now);
        Require(first != null && !Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));
        var afterFirst = Snapshot(p);
        Throws<LegacyDeclinedException>(() => LegacyInstall.Prepare(p, new[] { Probe(Other, zip, null) }, true, plan => plan.IsCoveredBy(approved), Now));
        Equal(afterFirst, Snapshot(p));
        Require(File.Exists(Path.Combine(p, "Assets", "Other", "OldTool", "a.txt")));
        Require(new LegacyPlan().IsCoveredBy(null));                     // an empty plan needs no consent
        // "Do not install" for the flagged items only: nothing was approved, so nothing moves.
        var none = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Require(!known.IsCoveredBy(none));
        Require(before.Length > 0);
    }

    private static void Wording()
    {
        // Parser: replacesAssetFolders is read, wrong shapes are ignored.
        var manifest = PackageZip.ParseManifest("{\"name\":\"a.b\",\"version\":\"1.0.0\",\"atelier\":{\"replacesAssetFolders\":[\"Assets/X\",3,\"\"]}}");
        Equal("Assets/X", string.Join(",", manifest.ReplacesAssetFolders.ToArray()));
        Equal(0, PackageZip.ParseManifest("{\"name\":\"a.b\",\"version\":\"1.0.0\",\"atelier\":\"x\"}").ReplacesAssetFolders.Count);
        Equal(0, PackageZip.ParseManifest("{\"name\":\"a.b\",\"version\":\"1.0.0\",\"atelier\":{\"replacesAssetFolders\":\"Assets/X\"}}").ReplacesAssetFolders.Count);

        // Foreign package refusal, three languages, replacing "Remove or migrate them first".
        var collisions = new List<GuidCollision>
        {
            new GuidCollision { Guid = G1, Path = "Packages/com.vrchat.base/A.meta", Container = "Packages/com.vrchat.base", PackageName = "com.vrchat.base", PackageVersion = "3.0.0" },
            new GuidCollision { Guid = G1, Path = "Packages/com.vrchat.base/B.meta", Container = "Packages/com.vrchat.base", PackageName = "com.vrchat.base", PackageVersion = "3.0.0" }
        };
        try
        {
            LegacyInstallText.LanguageProvider = () => 0;
            Equal("com.vrchat.base 이(가) 있는 프로젝트에는 설치할 수 없어요 (같은 GUID 파일 2개)", GuidCollisions.Refusal(collisions));
            LegacyInstallText.LanguageProvider = () => 1;
            Equal("This package cannot be installed in a project that has com.vrchat.base (2 files share GUIDs).", GuidCollisions.Refusal(collisions));
            LegacyInstallText.LanguageProvider = () => 2;
            Require(GuidCollisions.Refusal(collisions).Contains("com.vrchat.base") && GuidCollisions.Refusal(collisions).Contains("2"));
            LegacyInstallText.LanguageProvider = () => 1;
            Require(!GuidCollisions.Refusal(collisions).Contains("Remove or migrate"));
            // Leftovers under Assets/ keep their path list.
            collisions.Add(new GuidCollision { Guid = G1, Path = "Assets/Old/C.meta", Container = "" });
            var mixed = GuidCollisions.Refusal(collisions);
            Require(mixed.Contains("com.vrchat.base") && mixed.Contains("Assets/Old/C.meta"));
        }
        finally { LegacyInstallText.LanguageProvider = () => 1; }

        // Dialog texts: title and buttons per language; 10 paths, then "외 N개".
        Equal("이전 설치를 옮기고 설치할까요?", LegacyInstallText.Title(0));
        Equal("Move the old install and continue?", LegacyInstallText.Title(1));
        Equal("以前のインストールを移動してインストールしますか？", LegacyInstallText.Title(2));
        Equal("옮기고 설치", LegacyInstallText.Yes(0));
        Equal("설치 안 함", LegacyInstallText.No(0));
        var plan = new LegacyPlan();
        for (var i = 0; i < 13; i++) plan.Items.Add(new LegacyItem { Path = "Assets/Old/F" + i, Files = i + 1, Product = "MingToon" });
        var body = LegacyInstallText.Body(0, plan);
        Require(body.StartsWith("MingToon의 이전 버전이 Assets 폴더에 있어요. 패키지로 설치하면 파일이 겹쳐서, 아래 항목을 프로젝트 밖(Library/OnAirDeck/Backup)으로 옮긴 뒤 설치해요. 지워지지 않고 백업에 남아요."));
        Equal(10, body.Split('\n').Count(l => l.StartsWith("  Assets/")));
        Require(body.Contains("파일 1개") && body.Contains("외 3개") && body.Contains("통째로"));
        Require(LegacyInstallText.Body(1, plan).Contains("and 3 more") && LegacyInstallText.Body(2, plan).Contains("3件"));
        Require(LegacyInstallText.BatchBody(1, plan).Contains("only the products above are skipped"));
    }

    // ---------- v9 rework: scope of GUID-only finds, crash manifest ----------

    private const string G2 = "22222222222222222222222222222222";
    private const string G3 = "33333333333333333333333333333333";

    private static void GuidScope()
    {
        var p = NewProject();
        // Mixed: the user's own script lives next to the product file. Only the product file (and its .meta) moves.
        Put(p, "Assets/Scripts/Product.cs", "p");
        Put(p, "Assets/Scripts/Product.cs.meta", Meta(G1));
        Put(p, "Assets/Scripts/Mine.cs", "m");
        Put(p, "Assets/Scripts/Mine.cs.meta", Meta("99999999999999999999999999999999"));
        // Pure: every asset collides, so the whole folder moves.
        Put(p, "Assets/Pure/Sub/A.cs", "a");
        Put(p, "Assets/Pure/Sub/A.cs.meta", Meta(G2));
        Put(p, "Assets/Pure/Sub.meta", Meta("88888888888888888888888888888888"));   // a folder .meta is not an asset here
        Put(p, "Assets/Pure/B.cs", "b");
        Put(p, "Assets/Pure/B.cs.meta", Meta(G3));
        // Nested pure folder inside a mixed one: only the nested folder moves.
        Put(p, "Assets/Mixed/User.cs", "u");
        Put(p, "Assets/Mixed/User.cs.meta", Meta("77777777777777777777777777777777"));
        Put(p, "Assets/Mixed/Product/C.cs", "c");
        Put(p, "Assets/Mixed/Product/C.cs.meta", Meta("44444444444444444444444444444444"));
        var zip = Zip(Other, "1.0.0", "", G1, G2, G3, "44444444444444444444444444444444");
        var plan = LegacyInstall.Detect(p, new[] { Probe(Other, zip, null) });
        Equal("Assets/Mixed/Product,Assets/Pure,Assets/Scripts/Product.cs", string.Join(",", Paths(plan).ToArray()));
        Require(plan.Items.All(i => i.Source == "guid"));

        // Moving it leaves the user's files exactly where they were.
        var move = LegacyInstall.Move(p, plan, Now);
        Require(File.Exists(Path.Combine(p, "Assets", "Scripts", "Mine.cs")) && File.Exists(Path.Combine(p, "Assets", "Scripts", "Mine.cs.meta")));
        Require(!File.Exists(Path.Combine(p, "Assets", "Scripts", "Product.cs")) && !File.Exists(Path.Combine(p, "Assets", "Scripts", "Product.cs.meta")));
        Require(File.Exists(Path.Combine(p, "Assets", "Mixed", "User.cs")) && !Directory.Exists(Path.Combine(p, "Assets", "Mixed", "Product")));
        Require(!Directory.Exists(Path.Combine(p, "Assets", "Pure")));
        Equal(0, GuidCollisions.Scan(p, new HashSet<string>(new[] { G1, G2, G3, "44444444444444444444444444444444" }), new string[0]).Count);
        Equal(0, move.Restore().Count);

        // A product file whose folder holds nothing else of the product's, straight in Assets: alone. Assets itself never moves.
        p = NewProject();
        Put(p, "Assets/Lone.cs", "l");
        Put(p, "Assets/Lone.cs.meta", Meta(G1));
        Put(p, "Assets/Other.cs.meta", Meta("66666666666666666666666666666666"));
        plan = LegacyInstall.Detect(p, new[] { Probe(Other, Zip(Other, "1.0.0", "", G1), null) });
        Equal("Assets/Lone.cs", string.Join(",", Paths(plan).ToArray()));

        // The dialog tells the user which kind of move this is.
        var body = LegacyInstallText.Body(1, plan);
        Require(body.Contains("Only this product's files move") && !body.Contains("moves as a whole"));
        Require(LegacyInstallText.Body(0, plan).Contains("직접 만든 파일은 그대로"));
    }

    private static string ManifestText(string project, string folder)
    {
        return File.ReadAllText(Path.Combine(folder, LegacyMove.ManifestName));
    }

    private static void ManifestStates()
    {
        var p = NewProject();
        OldInstall(p);
        var states = new List<string>();
        var plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) });
        LegacyInstall.FaultHook = point => states.Add(ManifestStateOf(p));   // read while the first item is about to move
        LegacyMove move;
        try { move = LegacyInstall.Move(p, plan, Now, Pkg, "1.0.0"); }
        finally { LegacyInstall.FaultHook = null; }
        Equal("moving", states[0]);                                    // written before the first move
        Equal("moved", move.State);
        var text = ManifestText(p, move.BackupFolder);
        Require(text.Contains("\"package\": \"" + Pkg + "\"") && text.Contains("\"version\": \"1.0.0\"") && text.Contains("\"from\": \"Assets/StudioRaming/MingToon\"")
            && text.Contains("\"to\": \"Library/OnAirDeck/Backup/legacy-20261028-120000/Assets/StudioRaming/MingToon\"") && text.Contains("\"created\"") && text.Contains("\"updated\""));

        // Moved and not installed: it is pending (this is what a crash leaves).
        var pending = LegacyMove.PendingRestores(p);
        Equal(1, pending.Count);
        Equal("moved", pending[0].State);
        Equal(Pkg, pending[0].Package);
        Equal("Assets/StudioRaming/MingToon", string.Join(",", pending[0].Paths.ToArray()));
        // A half-done move (a crash while moving) is pending too.
        var halfFolder = Path.Combine(PackageTransaction.BackupRoot(p), "legacy-20261028-110000");
        Directory.CreateDirectory(halfFolder);
        File.WriteAllText(Path.Combine(halfFolder, LegacyMove.ManifestName), text.Replace("\"moved\"", "\"moving\"").Replace("legacy-20261028-120000", "legacy-20261028-110000"));
        Equal(2, LegacyMove.PendingRestores(p).Count);
        Directory.Delete(halfFolder, true);

        // Install finished: no longer pending, the backup stays.
        move.MarkInstalled();
        Equal("installed", move.State);
        Equal(0, LegacyMove.PendingRestores(p).Count);
        Require(Directory.Exists(move.BackupFolder));
        var done = LegacyMove.RestoreFromManifest(Path.Combine(move.BackupFolder, LegacyMove.ManifestName), p);
        Require(!done.Ok);                                               // installed: nothing to put back

        // Crash recovery: moved, the editor restarts, the card's button restores from the manifest alone.
        p = NewProject();
        var before = OldInstall(p);
        plan = LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) });
        move = LegacyInstall.Move(p, plan, Now, Pkg, "1.0.0");
        var manifest = LegacyMove.PendingRestores(p)[0].ManifestPath;      // no LegacyMove object any more
        var result = LegacyMove.RestoreFromManifest(manifest, p);
        Require(result.Ok);
        Equal(0, result.Blocked.Count);
        Equal("restored", ManifestStateOf(p));
        Equal(0, LegacyMove.PendingRestores(p).Count);
        Require(File.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon", "Shader.shader")) && File.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon.meta")));
        Equal("added by the user", File.ReadAllText(Path.Combine(p, "Assets", "StudioRaming", "MingToon", "User", "mine.txt")));
        Require(!result.Message.Contains("Exception"));
        // In-process Restore() writes the state too.
        move = LegacyInstall.Move(p, LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) }), Now.AddSeconds(5), Pkg, "1.0.0");
        Equal(0, move.Restore().Count);
        Equal("restored", move.State);
        Equal(0, LegacyMove.PendingRestores(p).Count);
        Require(before.Length > 0);
    }

    private static string ManifestStateOf(string project)
    {
        var root = PackageTransaction.BackupRoot(project);
        var file = Directory.GetFiles(root, LegacyMove.ManifestName, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).Last();
        var text = File.ReadAllText(file);
        var i = text.IndexOf("\"state\": \"", StringComparison.Ordinal) + 10;
        return text.Substring(i, text.IndexOf('"', i) - i);
    }

    private static void ManifestRefusals()
    {
        var p = NewProject();
        var before = OldInstall(p);
        var move = LegacyInstall.Move(p, LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) }), Now, Pkg, "1.0.0");
        var manifest = move.ManifestPath;
        // Something else now sits where the old install was: refuse, move nothing, keep the state.
        Put(p, "Assets/StudioRaming/MingToon/new.txt", "new");
        var afterBlock = Snapshot(p);
        var result = LegacyMove.RestoreFromManifest(manifest, p);
        Require(!result.Ok && result.Blocked.Count == 1 && result.Blocked[0] == "Assets/StudioRaming/MingToon");
        Require(result.Message.Length > 0);
        Equal(afterBlock, Snapshot(p));
        Equal("moved", ManifestStateOf(p));
        Equal(1, LegacyMove.PendingRestores(p).Count);
        // Clear the way and it works.
        Directory.Delete(Path.Combine(p, "Assets", "StudioRaming", "MingToon"), true);
        Require(LegacyMove.RestoreFromManifest(manifest, p).Ok);

        // Tampered or broken manifests: refused without a throw, nothing moves.
        move = LegacyInstall.Move(p, LegacyInstall.Detect(p, new[] { Probe(Pkg, null, null) }), Now.AddSeconds(9), Pkg, "1.0.0");
        var good = File.ReadAllText(move.ManifestPath);
        // R06: the last good copy (.prev) would stand in for a damaged manifest; drop it so these cases test the refusal itself.
        if (File.Exists(move.ManifestPath + ".prev")) File.Delete(move.ManifestPath + ".prev");
        var tamperedTo = good.Replace("\"from\": \"Assets/StudioRaming/MingToon\"", "\"from\": \"Packages/com.x.y\"");
        File.WriteAllText(move.ManifestPath, tamperedTo);
        Require(!LegacyMove.RestoreFromManifest(move.ManifestPath, p).Ok);
        Equal(LegacyMove.StateUnreadable, LegacyMove.PendingRestores(p)[0].State);   // R06: an unreadable record over a folder with files is shown, not hidden
        File.WriteAllText(move.ManifestPath, good.Replace("Library/OnAirDeck/Backup/legacy-20261028-120009", "Assets/elsewhere"));
        Require(!LegacyMove.RestoreFromManifest(move.ManifestPath, p).Ok);
        File.WriteAllText(move.ManifestPath, "not json");
        Require(!LegacyMove.RestoreFromManifest(move.ManifestPath, p).Ok);
        Require(!LegacyMove.RestoreFromManifest(Path.Combine(p, "missing.json"), p).Ok);
        Require(!LegacyMove.RestoreFromManifest(null, p).Ok);
        Require(!LegacyMove.RestoreFromManifest(move.ManifestPath, "").Ok);
        Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));            // still in the backup, untouched
        Require(Directory.Exists(Path.Combine(move.BackupFolder, "Assets", "StudioRaming", "MingToon")));
        File.WriteAllText(move.ManifestPath, good);
        Require(LegacyMove.RestoreFromManifest(move.ManifestPath, p).Ok);
        Require(before.Length > 0);
    }

    private static string Capture(Action action)
    {
        try { action(); return null; } catch (Exception e) { return e.Message; }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }

    // ---------- v9c: the fail-closed gate for sources 1-3 ----------

    private sealed class VouchingHandler : ILegacyHandler
    {
        public bool Handles(string projectRoot, LegacyProbe probe, LegacyItem item) { return true; }
        public bool HandsOff(LegacyProbe probe, LegacyItem item) { return false; }
        public IList<string> Validate(string projectRoot, LegacyProbe probe, LegacyItem item) { return new string[0]; }
        public bool Detach(string projectRoot, LegacyItem item, string backupFolder) { return false; }
    }

    private sealed class ScriptedHandler : ILegacyHandler
    {
        public Func<IList<string>> OnValidate = () => new string[0];
        public bool DetachDone;
        public int Detached;
        public bool Handles(string projectRoot, LegacyProbe probe, LegacyItem item) { return item.Path == "Assets/StudioRaming/MingToon"; }
        public bool HandsOff(LegacyProbe probe, LegacyItem item) { return false; }
        public IList<string> Validate(string projectRoot, LegacyProbe probe, LegacyItem item) { return OnValidate(); }
        public bool Detach(string projectRoot, LegacyItem item, string backupFolder)
        {
            Detached++;
            if (!DetachDone) return false;
            var from = Path.Combine(projectRoot, "Assets", "StudioRaming", "MingToon");
            var to = Path.Combine(backupFolder, "Assets", "StudioRaming", "MingToon");
            Directory.CreateDirectory(Path.GetDirectoryName(to));
            Directory.Move(from, to);
            File.Move(from + ".meta", to + ".meta");
            return true;
        }
    }

    private static string ZipWithInventory(string inventoryJson, string inventoryName)
    {
        var path = Zip(Pkg, "1.0.0", ",\"atelier\":{\"legacyInventory\":\"" + inventoryName + "\"}", "55555555555555555555555555555555");
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update))
            if (inventoryJson != null) Write(zip, "inventory.json", inventoryJson);
        return path;
    }

    private static string Sha(string text)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))).Replace("-", "").ToLowerInvariant();
    }

    private static string Inventory(params string[] pairs)
    {
        var sb = new StringBuilder("{\"files\":{");
        for (var i = 0; i < pairs.Length; i += 2) sb.Append(i > 0 ? "," : "").Append('"').Append(pairs[i]).Append("\":\"").Append(pairs[i + 1]).Append('"');
        return sb.Append("}}").ToString();
    }

    private static void NoInventoryNoMove()
    {
        var p = NewProject();
        var before = OldInstall(p);
        var zip = Zip(Pkg, "1.0.0", "", "55555555555555555555555555555555");
        var probes = new[] { Probe(Pkg, zip, null) };

        var plan = LegacyInstall.Detect(p, probes);
        Equal(1, plan.Items.Count);
        Require(plan.Items[0].Blocked);
        Equal("no-inventory", plan.Items[0].BlockedReason);
        Equal(1, plan.BlockedItems().Count);
        Equal(1, LegacyInstall.DetectKnown(p, probes).BlockedItems().Count);

        // Prepare never asks and never moves; the text names the folder.
        var asked = 0;
        var message = Capture(() => LegacyInstall.Prepare(p, probes, true, x => { asked++; return true; }, Now));
        Equal(0, asked);
        Require(message.Contains("Assets/StudioRaming/MingToon"));
        Require(message.Contains("clean-up"));
        Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, probes, true, x => true, Now));
        LegacyInstallText.LanguageProvider = () => 0;
        try { Require(LegacyInstallText.BlockedBody(0, plan.BlockedItems()).Contains("이전 설치 정리 필요")); }
        finally { LegacyInstallText.LanguageProvider = () => 1; }
        // The move itself refuses too (a plan built elsewhere cannot bypass the gate).
        Throws<PackageInstallException>(() => LegacyInstall.Move(p, plan, Now, Pkg, "1.0.0"));
        Equal(before, Snapshot(p));

        // A marker find and a package.json replacesAssetFolders find are sources 1-3 as well.
        var q = NewProject();
        Put(q, "Assets/Stuff/Old.txt", "old");
        Put(q, "Assets/Stuff.meta", Meta("66666666666666666666666666666666"));
        var declared = Zip(Other, "1.0.0", ",\"atelier\":{\"replacesAssetFolders\":[\"Assets/Stuff\"]}", "77777777777777777777777777777777");
        var viaPackage = LegacyInstall.Detect(q, new[] { Probe(Other, declared, null) });
        Equal(1, viaPackage.BlockedItems().Count);
        Equal("package", viaPackage.Items[0].Source);

        // GUID-only finds (source 4) keep their per-file rule and are not blocked.
        var g = NewProject();
        Put(g, "Assets/Shared/Mine.txt", "mine");
        Put(g, "Assets/Shared/Mine.txt.meta", Meta("88888888888888888888888888888888"));
        Put(g, "Assets/Shared/Old.cs", "old");
        Put(g, "Assets/Shared/Old.cs.meta", Meta(G1));
        var guidPlan = LegacyInstall.Detect(g, new[] { Probe(Other, Zip(Other, "1.0.0", "", G1), null) });
        Equal("Assets/Shared/Old.cs", string.Join(",", Paths(guidPlan).ToArray()));
        Equal(0, guidPlan.BlockedItems().Count);
    }

    private static void InventoryGate()
    {
        var p = NewProject();
        OldInstall(p);
        var inventory = Inventory("Shader.shader", Sha("legacy shader"), "User/mine.txt", Sha("added by the user"), "Gone.cs", Sha("gone"));
        var zip = ZipWithInventory(inventory, "inventory.json");
        var probes = new[] { Probe(Pkg, zip, null) };

        // The folder holds exactly what the inventory lists (a listed file may be missing): it is the old install.
        var plan = LegacyInstall.Detect(p, probes);
        Require(!plan.Items[0].Blocked);
        Equal(0, plan.Items[0].Unmatched.Count);

        // A file the user added, and a listed file whose content changed: blocked, both reported, nothing moves.
        Put(p, "Assets/StudioRaming/MingToon/MyNotes.txt", "my notes");
        Put(p, "Assets/StudioRaming/MingToon/Shader.shader", "edited by the user");
        var before = Snapshot(p);
        plan = LegacyInstall.Detect(p, probes);
        Require(plan.Items[0].Blocked);
        Equal("unmatched", plan.Items[0].BlockedReason);
        Equal("Assets/StudioRaming/MingToon/MyNotes.txt,Assets/StudioRaming/MingToon/Shader.shader", string.Join(",", plan.Items[0].Unmatched.ToArray()));
        var message = Capture(() => LegacyInstall.Prepare(p, probes, true, x => true, Now));
        Require(message.Contains("MyNotes.txt") && message.Contains("Shader.shader"));
        Equal(before, Snapshot(p));

        // The user's file is gone and the shader is back as shipped: the same inventory now passes and the move runs.
        File.Delete(Path.Combine(p, "Assets", "StudioRaming", "MingToon", "MyNotes.txt"));
        Put(p, "Assets/StudioRaming/MingToon/Shader.shader", "legacy shader");
        var move = LegacyInstall.Prepare(p, probes, true, x => true, Now);
        Require(move != null);
        Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));

        // An inventory that cannot be read (bad JSON, missing entry, no files object) blocks.
        foreach (var bad in new[] { "not json", "{\"other\":1}", null })
        {
            var q = NewProject();
            OldInstall(q);
            var badPlan = LegacyInstall.Detect(q, new[] { Probe(Pkg, ZipWithInventory(bad, "inventory.json"), null) });
            Require(badPlan.Items[0].Blocked);
            Equal("inventory-unreadable", badPlan.Items[0].BlockedReason);
        }

        // A path that tries to leave the zip is not an inventory.
        var r = NewProject();
        OldInstall(r);
        Require(LegacyInstall.Detect(r, new[] { Probe(Pkg, ZipWithInventory(inventory, "../inventory.json"), null) }).Items[0].Blocked);
    }

    private static void HandlerSeam()
    {
        var handler = new ScriptedHandler();
        LegacyInstall.Handlers.Add(handler);
        try
        {
            var p = NewProject();
            var before = OldInstall(p);
            var probes = new[] { Probe(Pkg, null, null) };

            // The handler objects: its paths are reported, nothing moves.
            handler.OnValidate = () => new[] { "Assets/StudioRaming/MingToon/User/mine.txt" };
            var plan = LegacyInstall.Detect(p, probes);
            Require(plan.Items[0].Blocked);
            Equal("Assets/StudioRaming/MingToon/User/mine.txt", string.Join(",", plan.Items[0].Unmatched.ToArray()));
            Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(p, probes, true, x => true, Now));

            // A handler that throws is a refusal, never an exception for the caller.
            handler.OnValidate = () => { throw new InvalidOperationException("boom"); };
            plan = LegacyInstall.Detect(p, probes);
            Require(plan.Items[0].Blocked);
            Equal(before, Snapshot(p));

            // The handler vouches and returns false from Detach: the Manager moves the folder itself.
            handler.OnValidate = () => new string[0];
            var move = LegacyInstall.Prepare(p, probes, true, x => true, Now);
            Equal(1, handler.Detached);
            Require(!Directory.Exists(Path.Combine(p, "Assets", "StudioRaming", "MingToon")));
            Require(move.Restore().Count == 0);

            // The handler detaches the folder itself: the move records it, and a restore brings it back.
            handler.DetachDone = true;
            handler.Detached = 0;
            var q = NewProject();
            var beforeQ = OldInstall(q);
            move = LegacyInstall.Prepare(q, probes, true, x => true, Now.AddSeconds(30));
            Equal(1, handler.Detached);
            Require(!Directory.Exists(Path.Combine(q, "Assets", "StudioRaming", "MingToon")));
            Require(File.Exists(Path.Combine(move.BackupFolder, "Assets", "StudioRaming", "MingToon", "Shader.shader")));
            Equal(1, move.Entries.Count);
            Require(move.Restore().Count == 0);
            Require(File.Exists(Path.Combine(q, "Assets", "StudioRaming", "MingToon", "Shader.shader")));
        }
        finally
        {
            LegacyInstall.Handlers.Remove(handler);
        }
    }
}
