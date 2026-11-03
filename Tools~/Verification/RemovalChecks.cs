using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OnAirDeck.UnityManager;

// Owner ask 2026-11-03: remove a product, in the reverse of the install order (never MingToon Core before its add-ons), from the
// Manager's own records only. Real sources: PlacedFiles, RemovalPlanner (Removal.cs), InstallRecords, CoreAddons, LicenseFile. The
// window half (the dialogs, MoveAssetToTrash, the package move and resolve) is compile-checked only.
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
        check("removal no record: an install the Manager has no record of removes nothing and names the folder to open (or none)", NoRecordNoDelete);
        check("removal files: only recorded files that are here; a file another product recorded stays; a user file keeps its folder", FilesFromRecord);
        check("removal folders: emptied folders go deepest first; never Assets, the install root or a special Unity folder", EmptiedFolders);
        check("removal package: a package VCC lists is left to VCC; a missing one is nothing to do", PackageRefusals);
        check("removal record: placed-files.json keeps only safe Assets/ file paths, merges a reinstall and prunes what is gone", Record);
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
        Require(order.IndexOf(Urp) < order.IndexOf("texture-tool") && order.IndexOf(Mlc) < order.IndexOf("com.other.tool"), string.Join(",", order));
        // Exactly the install order reversed.
        var install = CoreAddons.OrderCoreFirst(items, x => x[0], x => x[1]).Select(x => x[0].Length > 0 ? x[0] : x[1]).ToList();
        install.Reverse();
        Equal(string.Join(",", install), string.Join(",", order));
    }

    private static void CoreBlocked()
    {
        var project = NewProject();
        Package(project, Core, "0.1.16");
        Package(project, Urp, "0.1.16", Core);
        var plan = RemovalPlanner.ForPackage(project, Core, null);
        Equal(RemovalRefusal.NeededByOthers, plan.Refusal);
        Equal(Urp, string.Join(",", plan.NeededBy));
        Require(!plan.CanRun);
        Equal("", plan.PackageFolder);
        // The URP add-on itself can go: nothing needs it.
        var urp = RemovalPlanner.ForPackage(project, Urp, null);
        Require(urp.CanRun);
        Equal("Packages/" + Urp, urp.PackageFolder);
        // In one run with URP first, the core can go after it.
        var after = RemovalPlanner.ForPackage(project, Core, null, new HashSet<string> { Urp });
        Require(after.CanRun);
        Equal("Packages/" + Core, after.PackageFolder);
    }

    private static void NoRecordNoDelete()
    {
        var project = NewProject();
        Put(project, "Assets/StudioRaming/MaskMaker/MaskMaker.cs", "x");
        var placed = new Dictionary<string, List<string>>();
        var keys = new[] { PlacedFiles.LicenseKey("maskmaker") };
        var plan = RemovalPlanner.ForFiles(project, keys, placed, null, "Assets/StudioRaming/MaskMaker");
        Equal(RemovalRefusal.NoRecord, plan.Refusal);
        Equal(0, plan.Files.Count);
        Require(!plan.CanRun);
        Equal("Assets/StudioRaming/MaskMaker", plan.ManualFolder);
        // Another product's record does not make this one removable.
        placed[PlacedFiles.LicenseKey("other")] = new List<string> { "Assets/StudioRaming/MaskMaker/MaskMaker.cs" };
        plan = RemovalPlanner.ForFiles(project, keys, placed, null, "Assets/Nowhere");
        Equal(RemovalRefusal.NoRecord, plan.Refusal);
        Equal("", plan.ManualFolder);   // a folder that is not there is not offered
        Require(File.Exists(Path.Combine(project, "Assets", "StudioRaming", "MaskMaker", "MaskMaker.cs")));
    }

    private static void FilesFromRecord()
    {
        var project = NewProject();
        Put(project, "Assets/Tool/A.cs", "a");
        Put(project, "Assets/Tool/B.shader", "b");
        Put(project, "Assets/Tool/Shared.cginc", "s");
        Put(project, "Assets/Tool/Mine.mat", "user");   // the user's own file: not recorded
        var placed = new Dictionary<string, List<string>>
        {
            { PlacedFiles.LicenseKey("tool"), new List<string> { "Assets/Tool/A.cs", "Assets/Tool/B.shader", "Assets/Tool/Shared.cginc", "Assets/Tool/Gone.cs" } },
            { PlacedFiles.LicenseKey("other"), new List<string> { "Assets/Tool/Shared.cginc" } },
        };
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Require(plan.CanRun);
        Equal("Assets/Tool/A.cs,Assets/Tool/B.shader", string.Join(",", plan.Files));
        Equal("Assets/Tool/Shared.cginc", string.Join(",", plan.Shared));
        Require(!plan.Files.Contains("Assets/Tool/Mine.mat") && !plan.Files.Contains("Assets/Tool/Gone.cs"));
        // The folder holds the user's file and a shared one: it stays.
        Equal(0, RemovalPlanner.EmptiedFolders(project, plan.Files).Count);
        // Everything recorded is gone already: nothing to do.
        var gone = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("x") },
            new Dictionary<string, List<string>> { { PlacedFiles.LicenseKey("x"), new List<string> { "Assets/Tool/Gone.cs" } } }, null, "");
        Equal(RemovalRefusal.NothingHere, gone.Refusal);
    }

    private static void EmptiedFolders()
    {
        var project = NewProject();
        foreach (var path in new[]
        {
            "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs", "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs.meta",
            "Assets/StudioRaming/MaskMaker/Readme.txt", "Assets/StudioRaming/MaskMaker/Readme.txt.meta",
            "Assets/StudioRaming/MaskMaker/Editor.meta",
            "Assets/StudioRaming/Other/Keep.txt",
            "Assets/StudioRaming_Onairdeck/MaskMaker/onairdeck.license",
        }) Put(project, path, "x");
        var removed = new[] { "Assets/StudioRaming/MaskMaker/Editor/MaskMaker.cs", "Assets/StudioRaming/MaskMaker/Readme.txt", "Assets/StudioRaming_Onairdeck/MaskMaker/onairdeck.license" };
        var folders = RemovalPlanner.EmptiedFolders(project, removed);
        Equal("Assets/StudioRaming/MaskMaker/Editor,Assets/StudioRaming/MaskMaker,Assets/StudioRaming_Onairdeck/MaskMaker", string.Join(",", folders));
        Require(!folders.Contains("Assets/StudioRaming") && !folders.Contains(Installer.InstallRoot) && !folders.Contains("Assets"));
        // A file of the user next to them keeps the folder (and so its parent).
        Put(project, "Assets/StudioRaming/MaskMaker/Editor/MyTweak.cs", "user");
        folders = RemovalPlanner.EmptiedFolders(project, removed);
        Require(!folders.Contains("Assets/StudioRaming/MaskMaker/Editor") && !folders.Contains("Assets/StudioRaming/MaskMaker"), string.Join(",", folders));
        // Special folders never go.
        Put(project, "Assets/Editor/Only.cs", "x");
        Equal(0, RemovalPlanner.EmptiedFolders(project, new[] { "Assets/Editor/Only.cs" }).Count);
    }

    private static void PackageRefusals()
    {
        var project = NewProject();
        Package(project, Core, "0.1.16");
        Put(project, "Packages/vpm-manifest.json", "{\"dependencies\":{},\"locked\":{\"" + Core + "\":{\"version\":\"0.1.16\"}}}");
        Equal(RemovalRefusal.HeldByVcc, RemovalPlanner.ForPackage(project, Core, null).Refusal);
        Put(project, "Packages/vpm-manifest.json", "{ broken");
        Equal(RemovalRefusal.HeldByVcc, RemovalPlanner.ForPackage(project, Core, null).Refusal);   // unreadable counts as listed
        Put(project, "Packages/vpm-manifest.json", "{\"dependencies\":{\"com.vrchat.base\":{\"version\":\"3.7.0\"}},\"locked\":{}}");
        Require(RemovalPlanner.ForPackage(project, Core, null).CanRun);
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForPackage(project, "com.studioraming.vmodular", null).Refusal);
    }

    private static void Record()
    {
        var project = NewProject();
        var key = PlacedFiles.LicenseKey("tool");
        Put(project, "Assets/Tool/A.cs", "a");
        Put(project, "Assets/Tool/B.cs", "b");
        PlacedFiles.Add(project, key, new[] { "Assets/Tool/A.cs", "Assets\\Tool\\B.cs", "../escape.cs", "Packages/x/y.cs", "Assets/Tool/A.cs.meta", "Assets/../x.cs", "C:/abs.cs", "Assets/Tool/" });
        PlacedFiles.Add(project, key, new[] { "Assets/Tool/A.cs", "Assets/Tool/C.cs" });   // a reinstall adds, no duplicate
        Equal("Assets/Tool/A.cs,Assets/Tool/B.cs,Assets/Tool/C.cs", string.Join(",", PlacedFiles.Load(project)[key]));
        PlacedFiles.Prune(project, key);   // C.cs is not here
        Equal("Assets/Tool/A.cs,Assets/Tool/B.cs", string.Join(",", PlacedFiles.Load(project)[key]));
        File.Delete(Path.Combine(project, "Assets", "Tool", "A.cs"));
        File.Delete(Path.Combine(project, "Assets", "Tool", "B.cs"));
        PlacedFiles.Prune(project, key);
        Require(!PlacedFiles.Load(project).ContainsKey(key));
        // An unreadable file reads as no records: nothing is removable by record then.
        Put(project, "ProjectSettings/OnAirDeck/placed-files.json", "{ broken");
        Equal(0, PlacedFiles.Load(project).Count);
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
