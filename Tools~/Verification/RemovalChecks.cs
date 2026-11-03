using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OnAirDeck.UnityManager;

// Owner ask 2026-11-03: remove a product, in the reverse of the install order (never MingToon Core before its add-ons), from the
// Manager's own records only. Real sources: PlacedFiles, PlacementTracker, RemovalPlanner (Removal.cs), InstallRecords, CoreAddons,
// LicenseFile. The window half (the dialogs, MoveAssetToTrash, the package move and resolve) is compile-checked only.
internal static class RemovalChecks
{
    private const string Core = "com.studioraming.mingtoon";
    private const string Urp = "com.studioraming.mingtoon.urp";
    private const string Mlc = "73f4e203-ffd0-4e88-9b78-dc116fd74057";
    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "removal");
        Directory.CreateDirectory(_root);
        check("removal order: the reverse of the install order, add-ons first and MingToon Core last", Order);
        check("removal core: blocked while an installed package (URP) names it in vpmDependencies; runs when that add-on goes first in the same run", CoreBlocked);
        check("removal no record: a file install or a package the Manager has no record of removes nothing and names the folder to open (r1 R3)", NoRecordNoDelete);
        check("removal record: an install records only the files and folders it creates, never what was there (a user's Assets/Editor file, a user's empty folder; r1 R1 R4)", TrackerCreatesOnly);
        check("removal files: only recorded files that are here; a file another product recorded stays; another product's license file never goes (r1 R2)", FilesFromRecord);
        check("removal folders: only folders the install created, once empty, deepest first; never Assets, the install root or a special folder (r1 R4)", EmptiedFolders);
        check("removal shared: a file two products recorded stays with the first removal and goes with the last (r1 R6)", SharedReleased);
        check("removal package: VCC lists it, VModular's add-ons not yet known (r1 R5), or not here: refused with its reason", PackageRefusals);
        check("removal record file: keeps only safe Assets/ paths, merges a reinstall, prunes what is gone, lists packages", Record);
        check("removal license file: the product's own onairdeck.license by the id inside it, never another product's", OwnLicenseFiles);
        check("removal install record: removed licenses lose their version record, the others keep theirs", InstallRecordRemoved);
    }

    private static void Order()
    {
        var items = new List<string[]>
        {
            new[] { Core, "" }, new[] { "", "texture-tool" }, new[] { Urp, "" }, new[] { "", Mlc }, new[] { "com.other.tool", "" }
        };
        var order = RemovalPlanner.RemovalOrder(items, x => x[0], x => x[1], null).Select(x => x[0].Length > 0 ? x[0] : x[1]).ToList();
        Equal(Core, order.Last());
        Require(order.IndexOf(Urp) < order.IndexOf(Core) && order.IndexOf(Mlc) < order.IndexOf(Core), string.Join(",", order));
        var install = CoreAddons.OrderCoreFirst(items, x => x[0], x => x[1]).Select(x => x[0].Length > 0 ? x[0] : x[1]).ToList();
        install.Reverse();
        Equal(string.Join(",", install), string.Join(",", order));
    }

    private static void CoreBlocked()
    {
        var project = NewProject();
        Package(project, Core, "0.1.16");
        Package(project, Urp, "0.1.16", Core);
        var placed = Packages(Core, Urp);
        var plan = RemovalPlanner.ForPackage(project, Core, placed, null);
        Equal(RemovalRefusal.NeededByOthers, plan.Refusal);
        Equal(Urp, string.Join(",", plan.NeededBy));
        Require(!plan.CanRun);
        var urp = RemovalPlanner.ForPackage(project, Urp, placed, null);
        Require(urp.CanRun);
        Equal("Packages/" + Urp, urp.PackageFolder);
        var after = RemovalPlanner.ForPackage(project, Core, placed, null, new HashSet<string> { Urp });
        Require(after.CanRun);
        Equal("Packages/" + Core, after.PackageFolder);
    }

    private static void NoRecordNoDelete()
    {
        var project = NewProject();
        Put(project, "Assets/StudioRaming/MaskMaker/MaskMaker.cs", "x");
        var placed = new PlacedRecords();
        var keys = new[] { PlacedFiles.LicenseKey("maskmaker") };
        var plan = RemovalPlanner.ForFiles(project, keys, placed, null, "Assets/StudioRaming/MaskMaker");
        Equal(RemovalRefusal.NoRecord, plan.Refusal);
        Equal(0, plan.Files.Count);
        Require(!plan.CanRun);
        Equal("Assets/StudioRaming/MaskMaker", plan.ManualFolder);
        placed.Products[PlacedFiles.LicenseKey("other")] = Entry("Assets/StudioRaming/MaskMaker/MaskMaker.cs");
        plan = RemovalPlanner.ForFiles(project, keys, placed, null, "Assets/Nowhere");
        Equal(RemovalRefusal.NoRecord, plan.Refusal);
        Equal("", plan.ManualFolder);
        // A package folder the Manager did not record (copied by hand, or installed before records): removed by hand.
        Package(project, Core, "0.1.16");
        var pkg = RemovalPlanner.ForPackage(project, Core, new PlacedRecords(), null);
        Equal(RemovalRefusal.NoRecord, pkg.Refusal);
        Equal("Packages/" + Core, pkg.ManualFolder);
        Equal("", pkg.PackageFolder);
    }

    private static void TrackerCreatesOnly()
    {
        var project = NewProject();
        Put(project, "Assets/Editor/UserTool.cs", "the user's");          // listed by the product's .unitypackage too
        Directory.CreateDirectory(Path.Combine(project, "Assets", "MyTools"));   // the user's own empty folder
        var tracker = new PlacementTracker(project);
        foreach (var path in new[] { "Assets/Editor/UserTool.cs", "Assets/MyTools/Product.cs", "Assets/Tool/Sub/New.cs", "Assets/Tool/Sub/New.cs", "../x.cs" })
            tracker.BeforeWrite(path);
        Equal("Assets/MyTools/Product.cs,Assets/Tool/Sub/New.cs", string.Join(",", tracker.Paths));
        Equal("Assets/Tool/Sub,Assets/Tool", string.Join(",", tracker.Folders));
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("tool"), tracker.Paths, tracker.Folders);
        // The import was cancelled after the record was written: nothing of it is here, so nothing is removed; the user's file stays.
        var placed = PlacedFiles.Load(project);
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Equal(RemovalRefusal.NothingHere, plan.Refusal);
        // After the import: its files go, the user's MyTools folder and Assets/Editor stay, the folders it created go.
        Put(project, "Assets/MyTools/Product.cs", "p");
        Put(project, "Assets/Tool/Sub/New.cs", "n");
        Put(project, "Assets/Tool/Sub/New.cs.meta", "m");
        Put(project, "Assets/Tool/Sub.meta", "m");
        plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Equal("Assets/MyTools/Product.cs,Assets/Tool/Sub/New.cs", string.Join(",", plan.Files));
        Equal("Assets/Tool/Sub,Assets/Tool", string.Join(",", plan.Folders));
        Require(!plan.Files.Contains("Assets/Editor/UserTool.cs"));
    }

    private static void FilesFromRecord()
    {
        var project = NewProject();
        Put(project, "Assets/Tool/A.cs", "a");
        Put(project, "Assets/Tool/B.shader", "b");
        Put(project, "Assets/Tool/Shared.cginc", "s");
        Put(project, "Assets/Tool/Mine.mat", "user");
        Put(project, Installer.InstallRoot + "/Other/onairdeck.license", License("{\"license_id\":\"lic-other\"}"));
        var placed = new PlacedRecords();
        placed.Products[PlacedFiles.LicenseKey("tool")] = Entry("Assets/Tool/A.cs", "Assets/Tool/B.shader", "Assets/Tool/Shared.cginc", "Assets/Tool/Gone.cs",
            Installer.InstallRoot + "/Other/onairdeck.license");
        placed.Products[PlacedFiles.LicenseKey("other")] = Entry("Assets/Tool/Shared.cginc");
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Require(plan.CanRun);
        Equal("Assets/Tool/A.cs,Assets/Tool/B.shader", string.Join(",", plan.Files));
        Require(plan.Shared.Contains("Assets/Tool/Shared.cginc") && plan.Shared.Contains(Installer.InstallRoot + "/Other/onairdeck.license"), string.Join(",", plan.Shared));
        Require(!plan.Files.Contains("Assets/Tool/Mine.mat") && !plan.Files.Contains("Assets/Tool/Gone.cs"));
        var gone = new PlacedRecords();
        gone.Products[PlacedFiles.LicenseKey("x")] = Entry("Assets/Tool/Gone.cs");
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("x") }, gone, null, "").Refusal);
    }

    private static void EmptiedFolders()
    {
        var project = NewProject();
        foreach (var path in new[]
        {
            "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs", "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs.meta",
            "Assets/StudioRaming/MaskMaker/Readme.txt", "Assets/StudioRaming/MaskMaker/Readme.txt.meta", "Assets/StudioRaming/MaskMaker/Editor.meta",
            "Assets/StudioRaming/Other/Keep.txt",
        }) Put(project, path, "x");
        var removed = new[] { "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs", "Assets/StudioRaming/MaskMaker/Readme.txt" };
        var created = new HashSet<string> { "Assets/StudioRaming/MaskMaker", "Assets/StudioRaming/MaskMaker/Editor", "Assets/StudioRaming" };
        var folders = RemovalPlanner.EmptiedFolders(project, removed, created);
        Equal("Assets/StudioRaming/MaskMaker/Editor,Assets/StudioRaming/MaskMaker", string.Join(",", folders));
        // A folder the install did not create stays, even when it empties.
        Equal("Assets/StudioRaming/MaskMaker/Editor", string.Join(",", RemovalPlanner.EmptiedFolders(project, removed, new HashSet<string> { "Assets/StudioRaming/MaskMaker/Editor" })));
        Equal(0, RemovalPlanner.EmptiedFolders(project, removed, new HashSet<string>()).Count);
        // A file of the user next to them keeps the folder (and so its parent).
        Put(project, "Assets/StudioRaming/MaskMaker/Editor/MyTweak.cs", "user");
        Equal(0, RemovalPlanner.EmptiedFolders(project, removed, created).Count);
        Put(project, "Assets/Editor/Only.cs", "x");
        Equal(0, RemovalPlanner.EmptiedFolders(project, new[] { "Assets/Editor/Only.cs" }, new HashSet<string> { "Assets/Editor" }).Count);
        Require(!RemovalPlanner.IsAssetFolder("Assets") && !RemovalPlanner.IsAssetFolder(Installer.InstallRoot) && !RemovalPlanner.IsAssetFolder("Assets/Editor"));
    }

    private static void SharedReleased()
    {
        var project = NewProject();
        Put(project, "Assets/Common/Lib.cginc", "s");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("a"), new[] { "Assets/Common/Lib.cginc" }, null);
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("b"), new[] { "Assets/Common/Lib.cginc" }, null);
        var first = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("a") }, PlacedFiles.Load(project), null, "");
        Equal(RemovalRefusal.NothingHere, first.Refusal);   // only a shared file: nothing of A's own
        Equal("Assets/Common/Lib.cginc", string.Join(",", first.Shared));
        PlacedFiles.Prune(project, PlacedFiles.LicenseKey("a"), new HashSet<string>(first.Shared));
        Require(!PlacedFiles.Load(project).Products.ContainsKey(PlacedFiles.LicenseKey("a")));
        var last = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("b") }, PlacedFiles.Load(project), null, "");
        Equal("Assets/Common/Lib.cginc", string.Join(",", last.Files));
    }

    private static void PackageRefusals()
    {
        var project = NewProject();
        Package(project, Core, "0.1.16");
        var placed = Packages(Core, "com.studioraming.vmodular");
        Put(project, "Packages/vpm-manifest.json", "{\"dependencies\":{},\"locked\":{\"" + Core + "\":{\"version\":\"0.1.16\"}}}");
        Equal(RemovalRefusal.HeldByVcc, RemovalPlanner.ForPackage(project, Core, placed, null).Refusal);
        Put(project, "Packages/vpm-manifest.json", "{ broken");
        Equal(RemovalRefusal.HeldByVcc, RemovalPlanner.ForPackage(project, Core, placed, null).Refusal);
        Put(project, "Packages/vpm-manifest.json", "{\"dependencies\":{\"com.vrchat.base\":{\"version\":\"3.7.0\"}},\"locked\":{}}");
        Require(RemovalPlanner.ForPackage(project, Core, placed, null).CanRun);
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForPackage(project, "com.example.none", placed, null).Refusal);
        // VModular's add-ons (VModular Plus) are not known yet: the core is never removed before them.
        Package(project, "com.studioraming.vmodular", "2.0.0");
        Equal(RemovalRefusal.AddonsUnknown, RemovalPlanner.ForPackage(project, "com.studioraming.vmodular", placed, null).Refusal);
    }

    private static void Record()
    {
        var project = NewProject();
        var key = PlacedFiles.LicenseKey("tool");
        Put(project, "Assets/Tool/A.cs", "a");
        Put(project, "Assets/Tool/B.cs", "b");
        PlacedFiles.Add(project, key, new[] { "Assets/Tool/A.cs", "Assets\\Tool\\B.cs", "../escape.cs", "Packages/x/y.cs", "Assets/Tool/A.cs.meta", "Assets/../x.cs", "C:/abs.cs", "Assets/Tool/" },
            new[] { "Assets/Tool", "Assets", "Assets/Editor", "../up" });
        PlacedFiles.Add(project, key, new[] { "Assets/Tool/A.cs", "Assets/Tool/C.cs" }, null);
        var loaded = PlacedFiles.Load(project).Products[key];
        Equal("Assets/Tool/A.cs,Assets/Tool/B.cs,Assets/Tool/C.cs", string.Join(",", loaded.Paths));
        Equal("Assets/Tool", string.Join(",", loaded.Folders));
        PlacedFiles.Prune(project, key, null);
        Equal("Assets/Tool/A.cs,Assets/Tool/B.cs", string.Join(",", PlacedFiles.Load(project).Products[key].Paths));
        File.Delete(Path.Combine(project, "Assets", "Tool", "A.cs"));
        File.Delete(Path.Combine(project, "Assets", "Tool", "B.cs"));
        PlacedFiles.Prune(project, key, null);
        Require(!PlacedFiles.Load(project).Products.ContainsKey(key));
        PlacedFiles.AddPackage(project, Core);
        Require(PlacedFiles.Load(project).Packages.Contains(Core));
        PlacedFiles.RemovePackage(project, Core);
        Require(!PlacedFiles.Load(project).Packages.Contains(Core));
        Put(project, "ProjectSettings/OnAirDeck/placed-files.json", "{ broken");
        Equal(0, PlacedFiles.Load(project).Products.Count);
        Equal("purchase:item-1", PlacedFiles.PurchaseKey(new PurchaseItem { item_id = "item-1", product_id = "p" }));
        Equal("own:p", PlacedFiles.PurchaseKey(new PurchaseItem { product_id = "p" }));
    }

    private static void OwnLicenseFiles()
    {
        var project = NewProject();
        Put(project, Installer.InstallRoot + "/MaskMaker/onairdeck.license", License("{\"license_id\":\"lic-mask\"}"));
        Put(project, Installer.InstallRoot + "/Other/onairdeck.license", License("{\"license_id\":\"lic-other\"}"));
        Put(project, Installer.InstallRoot + "/VModular Plus 1.0.0/onairdeck.license", License("{\"v\":1,\"item_id\":\"item-plus\",\"product_id\":\"p\"}"));
        Put(project, Installer.InstallRoot + "/Broken/onairdeck.license", "not a license");
        Equal(Installer.InstallRoot + "/MaskMaker/onairdeck.license", string.Join(",", RemovalPlanner.OwnLicenseFiles(project, new[] { "lic-mask" }, null)));
        Equal(Installer.InstallRoot + "/VModular Plus 1.0.0/onairdeck.license", string.Join(",", RemovalPlanner.OwnLicenseFiles(project, null, "item-plus")));
        Equal(0, RemovalPlanner.OwnLicenseFiles(project, new[] { "lic-none" }, "item-none").Count);
    }

    private static void InstallRecordRemoved()
    {
        var project = NewProject();
        InstallRecords.Save(project, new InstallRecord { LicenseId = "a", VersionId = "va", Label = "1.0" });
        InstallRecords.Save(project, new InstallRecord { LicenseId = "b", VersionId = "vb", Label = "2.0" });
        InstallRecords.Remove(project, new[] { "a", "" });
        var left = InstallRecords.Load(project);
        Require(!left.ContainsKey("a") && left.ContainsKey("b"));
    }

    // ---------- fixtures ----------

    private static PlacedEntry Entry(params string[] paths)
    {
        var entry = new PlacedEntry();
        entry.Paths.AddRange(paths);
        return entry;
    }

    private static PlacedRecords Packages(params string[] names)
    {
        var records = new PlacedRecords();
        foreach (var name in names) records.Packages.Add(name);
        return records;
    }

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

    private static void Package(string project, string name, string version, string dependsOn = null)
    {
        var deps = dependsOn == null ? "" : ",\"vpmDependencies\":{\"" + dependsOn + "\":\"" + version + "\"}";
        Put(project, "Packages/" + name + "/package.json", "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"" + deps + "}");
    }

    private static string License(string payloadJson)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return b64 + ".signature";
    }

    private static void Require(bool condition, string detail = "") { if (!condition) throw new Exception("Assertion failed " + detail); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
