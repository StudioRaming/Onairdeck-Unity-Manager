using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
    private static string _host;

    public static void Run(Action<string, Action> check, string project)
    {
        _host = project;
        _root = Path.Combine(project, "removal");
        Directory.CreateDirectory(_root);
        check("removal order: the reverse of the install order, add-ons first and MingToon Core last", Order);
        check("removal core: blocked while an installed package (URP) names it in vpmDependencies; runs when that add-on goes first in the same run", CoreBlocked);
        check("removal no record: a file install or a package the Manager has no record of removes nothing and names the folder to open (r1 R3)", NoRecordNoDelete);
        check("removal record: an install records only the files and folders it creates, never what was there (a user's Assets/Editor file, a user's empty folder; r1 R1 R4)", TrackerCreatesOnly);
        check("removal files: only recorded files that are here; a file another product recorded stays; another product's license file never goes (r1 R2)", FilesFromRecord);
        check("removal folders: only folders the install created, once empty, deepest first; never Assets, the install root or a special folder (r1 R4)", EmptiedFolders);
        check("removal shared: a file two products installed stays with the first removal, passes to the other and goes with the last (r1 R6, r2 R9)", SharedReleased);
        check("removal GUID: a .unitypackage file goes only while its .meta keeps the package's GUID (an import cancelled, then a user's file at that path; r2 R1)", GuidProof);
        check("removal proof: a copied file the user changed, or replaced after deleting the product's, stays; an update of the product takes the new contents; no proof, no removal (r3 R13)", HashProof);
        check("removal after the files went: a file saved meanwhile at a path that just went keeps its folder (r3 R10)", FolderRecheckAfter);
        check("removal zip: a zip's folder entries and the product folder are recorded before they are made, so they go once empty (r2 R12)", ZipFolders);
        check("removal package: VCC lists it or not here: refused with its reason; VModular is removable now its add-on is known", PackageRefusals);
        check("removal record file: keeps only safe Assets/ paths, merges a reinstall, prunes what is gone, lists packages", Record);
        check("removal license file: the product's own onairdeck.license by the id inside it, never another product's", OwnLicenseFiles);
        check("removal license file: one option of a product keeps the shared license while another option still has a recorded file here", OtherOptionKeepsLicense);
        check("option overwrite: asked only for a file another option of the product created, that is here, with other contents (SHA-256) or another GUID; every write is compared", OptionOverwriteRule);
        check("option overwrite 'overwrite': the file passes to this option, so removing it takes the file and removing the other option leaves it", OptionOverwriteYes);
        check("option overwrite 'cancel': nothing is written or handed over, and a batch skips that option and goes on with the next", OptionOverwriteNo);
        check("option overwrite: the zip entries it checks are exactly the files ExtractZip writes, with their SHA-256", ZipTargetsMatchExtract);
        check("option overwrite: a .unitypackage asset is claimed before its import; the other option keeps the file until it is written, and for good when it is cancelled", OptionOverwritePendingImport);
        check("option overwrite: an option the list no longer shows is found by its record's product; a record from before is not", OptionOverwriteByRecord);
        check("option overwrite: the contents of every .unitypackage asset are hashed as a stream", PackageAssetHashes);
        check("removal install record: removed licenses lose their version record, the others keep theirs", InstallRecordRemoved);
        check("removal run (G4 retest 3.3): a core's file an add-on going first also recorded goes with the core; the add-on alone, or the core alone, keeps it", SharedWithAddonGoingFirst);
        check("installed by record (G4 retest 3.2): a file core or add-on license counts as installed when its install record exists and a file it placed is here; generic, never outside that scope", InstalledByRecord);
        check("remnants (G4-R7): an add-on removed alone keeps a record of its files that stay with no other owner; a core is refused while one naming it is here; pruned once they are gone", Remnants);
    }

    // G4-R6/R7 (option A, lead 2026-11-04): an add-on's file that stays after the add-on went (the user changed it, or it wrote over
    // a file nobody else owns) still needs the core. The record of it (a remnant) keeps the core from going too early.
    private static void Remnants()
    {
        var project = NewProject();
        Put(project, "Assets/Core/Core.cs", "core");
        Put(project, "Assets/Plus/Plus.cs", "plus");
        Put(project, "Assets/Plus/Helper.cs", "helper");
        Put(project, "Assets/User/Mine.cs", "user");
        var coreKey = PlacedFiles.PurchaseKey("core-item");
        var addonKey = PlacedFiles.LicenseKey("addon");
        var placed = new PlacedRecords();
        placed.Products[coreKey] = Entry(project, "Assets/Core/Core.cs");
        var addonEntry = Entry(project, "Assets/Plus/Plus.cs", "Assets/Plus/Helper.cs");
        addonEntry.Touched.Add("Assets/Core/Core.cs");   // the add-on carried the core's file over the installed core
        addonEntry.Touched.Add("Assets/User/Mine.cs");   // and wrote over a file nobody recorded
        placed.Products[addonKey] = addonEntry;
        Put(project, "Assets/Plus/Plus.cs", "plus, edited by the user");

        var plan = RemovalPlanner.ForFiles(project, new[] { addonKey }, placed, null, "");
        Equal("Assets/Plus/Helper.cs", string.Join(",", plan.Files));
        // What stays with no other owner: the changed file and the written-over one; the core's own file is the core's.
        var stays = RemovalPlanner.RemnantPaths(plan, placed, new[] { addonKey });
        Equal("Assets/Plus/Plus.cs,Assets/User/Mine.cs", string.Join(",", stays));

        PlacedFiles.Save(project, placed);
        // The run releases the record first, then keeps the remnant (review R1 of option A: in either order it stays).
        PlacedFiles.Release(project, addonKey);
        PlacedFiles.KeepRemnant(project, addonKey, "core-x", stays);
        var loaded = PlacedFiles.Load(project);
        Require(!loaded.Products.ContainsKey(addonKey), "record released");
        Equal("Assets/Plus/Plus.cs,Assets/User/Mine.cs", string.Join(",", RemovalPlanner.RemnantsBlocking(project, loaded, "core-x")));
        Equal(0, RemovalPlanner.RemnantsBlocking(project, loaded, "other-core").Count);

        // review R2: an older Manager rewrites placed-files.json with products and packages only; the remnants live in their own
        // file, so they survive that save.
        File.WriteAllText(PlacedFiles.PathFor(project), "{\"format\":3,\"products\":[],\"packages\":[]}\n");
        Equal("Assets/Plus/Plus.cs,Assets/User/Mine.cs", string.Join(",", RemovalPlanner.RemnantsBlocking(project, PlacedFiles.Load(project), "core-x")));

        // review R1: an add-on that moves nothing (every file of its own changed) still keeps what stays.
        var nothing = NewProject();
        Put(nothing, "Assets/Plus/Plus.cs", "plus");
        var nothingPlaced = new PlacedRecords();
        nothingPlaced.Products[addonKey] = Entry(nothing, "Assets/Plus/Plus.cs");
        Put(nothing, "Assets/Plus/Plus.cs", "plus, edited");
        var nothingPlan = RemovalPlanner.ForFiles(nothing, new[] { addonKey }, nothingPlaced, null, "");
        Equal(RemovalRefusal.NothingHere, nothingPlan.Refusal);
        Equal("Assets/Plus/Plus.cs", string.Join(",", RemovalPlanner.RemnantPaths(nothingPlan, nothingPlaced, new[] { addonKey })));

        // review r2 R8 / R1: the release keeps the remnant first and gives up the record only once that is written.
        PlacedFiles.Save(nothing, nothingPlaced);
        PlacedFiles.ReleaseKeepingRemnant(nothing, new[] { addonKey }, "core-x", new[] { "Assets/Plus/Plus.cs" });
        var afterRelease = PlacedFiles.Load(nothing);
        Require(!afterRelease.Products.ContainsKey(addonKey), "record released");
        Equal("Assets/Plus/Plus.cs", string.Join(",", RemovalPlanner.RemnantsBlocking(nothing, afterRelease, "core-x")));
        // The remnant cannot be written (a folder where its file goes): the record stays, nothing is lost.
        var blocked = NewProject();
        Put(blocked, "Assets/Plus/Plus.cs", "plus");
        var blockedPlaced = new PlacedRecords();
        blockedPlaced.Products[addonKey] = Entry(blocked, "Assets/Plus/Plus.cs");
        PlacedFiles.Save(blocked, blockedPlaced);
        Directory.CreateDirectory(PlacedFiles.RemnantsPathFor(blocked));
        var threw = false;
        try { PlacedFiles.ReleaseKeepingRemnant(blocked, new[] { addonKey }, "core-x", new[] { "Assets/Plus/Plus.cs" }); }
        catch (Exception) { threw = true; }
        Require(threw, "the failed write is reported");
        Require(PlacedFiles.Load(blocked).Products.ContainsKey(addonKey), "record kept when the remnant could not be written");

        // review r2 R7: an unreadable remnant file is not an empty one: it is not overwritten, and it reads as unknown.
        var broken = NewProject();
        Directory.CreateDirectory(Path.GetDirectoryName(PlacedFiles.RemnantsPathFor(broken)));
        File.WriteAllText(PlacedFiles.RemnantsPathFor(broken), "{\"remnants\": [");
        var brokenLoaded = PlacedFiles.Load(broken);
        Require(brokenLoaded.RemnantsUnreadable, "a cut file is unreadable");
        PlacedFiles.Save(broken, brokenLoaded);
        Equal("{\"remnants\": [", File.ReadAllText(PlacedFiles.RemnantsPathFor(broken)));
        var keepThrew = false;
        try { PlacedFiles.KeepRemnant(broken, addonKey, "core-x", new[] { "Assets/Plus/Plus.cs" }); }
        catch (Exception) { keepThrew = true; }
        Require(keepThrew, "no remnant is added over an unreadable file");
        File.WriteAllText(PlacedFiles.RemnantsPathFor(broken), "{\"remnants\": []}");
        Require(!PlacedFiles.Load(broken).RemnantsUnreadable, "an empty list is readable");

        // Gone files no longer block, and the record is pruned once nothing of it is here.
        File.Delete(Path.Combine(project, "Assets", "Plus", "Plus.cs"));
        Equal("Assets/User/Mine.cs", string.Join(",", RemovalPlanner.RemnantsBlocking(project, PlacedFiles.Load(project), "core-x")));
        File.Delete(Path.Combine(project, "Assets", "User", "Mine.cs"));
        Equal(0, RemovalPlanner.RemnantsBlocking(project, PlacedFiles.Load(project), "core-x").Count);
        PlacedFiles.PruneRemnants(project);
        Equal(0, PlacedFiles.Load(project).Remnants.Count);

        // The usual add-on removal leaves none: its own files go, the core's files it carried stay the core's.
        var usual = NewProject();
        Put(usual, "Assets/Core/Core.cs", "core");
        Put(usual, "Assets/Plus/Plus.cs", "plus");
        var usualPlaced = new PlacedRecords();
        usualPlaced.Products[coreKey] = Entry(usual, "Assets/Core/Core.cs");
        var usualAddon = Entry(usual, "Assets/Plus/Plus.cs");
        usualAddon.Touched.Add("Assets/Core/Core.cs");
        usualPlaced.Products[addonKey] = usualAddon;
        var usualPlan = RemovalPlanner.ForFiles(usual, new[] { addonKey }, usualPlaced, null, "");
        Equal(0, RemovalPlanner.RemnantPaths(usualPlan, usualPlaced, new[] { addonKey }).Count);
    }

    // G4 retest 2026-11-04: VModular Plus ships the base's files too, so they were recorded under the base purchase and the Plus license.
    // Removing the base then kept them as shared, so the base did not count as going and Plus was not asked to go first.
    private static void SharedWithAddonGoingFirst()
    {
        var project = NewProject();
        Put(project, "Assets/Core/Core.cs", "core");
        Put(project, "Assets/Core/Both.cs", "both");
        Put(project, "Assets/Core/Addon/Plus.cs", "plus");
        var placed = new PlacedRecords();
        var coreKey = PlacedFiles.PurchaseKey("core-item");
        var addonKey = PlacedFiles.LicenseKey("addon");
        placed.Products[coreKey] = Entry(project, "Assets/Core/Core.cs", "Assets/Core/Both.cs");
        placed.Products[addonKey] = Entry(project, "Assets/Core/Both.cs", "Assets/Core/Addon/Plus.cs");

        // The add-on goes first: its own file; the file the core also recorded stays for the core's step.
        var addon = RemovalPlanner.ForFiles(project, new[] { addonKey }, placed, null, "");
        Equal("Assets/Core/Addon/Plus.cs", string.Join(",", addon.Files));
        Equal("Assets/Core/Both.cs", string.Join(",", addon.Shared));
        // The core after it in the same run: the shared file is its too, nobody else keeps it.
        var core = RemovalPlanner.ForFiles(project, new[] { coreKey }, placed, null, "", new[] { addonKey });
        Equal("Assets/Core/Both.cs,Assets/Core/Core.cs", string.Join(",", core.Files));
        // The core alone (the add-on stays): the add-on still needs that file.
        var alone = RemovalPlanner.ForFiles(project, new[] { coreKey }, placed, null, "");
        Equal("Assets/Core/Core.cs", string.Join(",", alone.Files));
        Equal("Assets/Core/Both.cs", string.Join(",", alone.Shared));
        // A third product that also recorded the file keeps it even when the add-on goes first.
        placed.Products[PlacedFiles.LicenseKey("third")] = Entry(project, "Assets/Core/Both.cs");
        var third = RemovalPlanner.ForFiles(project, new[] { coreKey }, placed, null, "", new[] { addonKey });
        Equal("Assets/Core/Core.cs", string.Join(",", third.Files));

        // review R1: when a third product also holds the add-on's own file, the add-on gives up its record (nothing of its own goes)
        // and its code stays; the core taking the file they share would leave that code without its core. That is a leftover, and
        // the window then refuses the core.
        var r1 = new PlacedRecords();
        r1.Products[coreKey] = Entry(project, "Assets/Core/Core.cs", "Assets/Core/Both.cs");
        r1.Products[addonKey] = Entry(project, "Assets/Core/Both.cs", "Assets/Core/Addon/Plus.cs");
        r1.Products[PlacedFiles.LicenseKey("holder")] = Entry(project, "Assets/Core/Addon/Plus.cs");
        var addonPlan = RemovalPlanner.ForFiles(project, new[] { addonKey }, r1, null, "");
        Equal(RemovalRefusal.NothingHere, addonPlan.Refusal);
        var corePlan = RemovalPlanner.ForFiles(project, new[] { coreKey }, r1, null, "", new[] { addonKey });
        Equal("Assets/Core/Addon/Plus.cs", string.Join(",", RemovalPlanner.AddonLeftovers(new[] { addonPlan }, corePlan)));
        // The usual run leaves nothing: the add-on's own file goes with it, the shared one with the core.
        Equal(0, RemovalPlanner.AddonLeftovers(new[] { addon }, core).Count);

        // review r2 R3: an add-on file the user changed stays (never removed): a leftover too.
        var r3 = NewProject();
        Put(r3, "Assets/Core/Core.cs", "core");
        Put(r3, "Assets/Core/Addon/Plus.cs", "plus");
        Put(r3, "Assets/Core/Addon/Helper.cs", "helper");
        var r3Placed = new PlacedRecords();
        r3Placed.Products[coreKey] = Entry(r3, "Assets/Core/Core.cs");
        r3Placed.Products[addonKey] = Entry(r3, "Assets/Core/Addon/Plus.cs", "Assets/Core/Addon/Helper.cs");
        Put(r3, "Assets/Core/Addon/Plus.cs", "plus, edited by the user");
        var r3Addon = RemovalPlanner.ForFiles(r3, new[] { addonKey }, r3Placed, null, "");
        Equal("Assets/Core/Addon/Plus.cs", string.Join(",", r3Addon.Changed));
        var r3Core = RemovalPlanner.ForFiles(r3, new[] { coreKey }, r3Placed, null, "", new[] { addonKey });
        Equal("Assets/Core/Addon/Plus.cs", string.Join(",", RemovalPlanner.AddonLeftovers(new[] { r3Addon }, r3Core)));

        // review r2 R4: a file the add-on only wrote over (another product's, recorded as touched) stays: a leftover. One the core
        // recorded (the add-on carried the core's file over an install of the core) goes with the core and is none.
        var r4 = NewProject();
        Put(r4, "Assets/Core/Core.cs", "core");
        Put(r4, "Assets/Other/Lib.cs", "lib");
        Put(r4, "Assets/Core/Addon/Plus.cs", "plus");
        var r4Placed = new PlacedRecords();
        r4Placed.Products[coreKey] = Entry(r4, "Assets/Core/Core.cs");
        r4Placed.Products[PlacedFiles.LicenseKey("other")] = Entry(r4, "Assets/Other/Lib.cs");
        var r4AddonEntry = Entry(r4, "Assets/Core/Addon/Plus.cs");
        r4AddonEntry.Touched.Add("Assets/Other/Lib.cs");
        r4AddonEntry.Touched.Add("Assets/Core/Core.cs");
        r4Placed.Products[addonKey] = r4AddonEntry;
        var r4Addon = RemovalPlanner.ForFiles(r4, new[] { addonKey }, r4Placed, null, "");
        var r4Core = RemovalPlanner.ForFiles(r4, new[] { coreKey }, r4Placed, null, "", new[] { addonKey });
        Equal("Assets/Core/Core.cs", string.Join(",", r4Core.Files));
        Equal("Assets/Other/Lib.cs", string.Join(",", RemovalPlanner.AddonLeftovers(new[] { r4Addon }, r4Core)));
    }

    // G4 retest 2026-11-04: VModular Plus ships no atelier.product.json, so its row said "to get" after a good install.
    private static void InstalledByRecord()
    {
        var project = NewProject();
        Put(project, "Assets/Addon/Plus.cs", "plus");
        Put(project, "Assets/Core/Core.cs", "core");
        var placed = new PlacedRecords();
        // The add-on carries the core's file too (VModular Plus); the core's own row recorded it as well.
        placed.Products[PlacedFiles.LicenseKey("addon-lic")] = Entry(project, "Assets/Addon/Plus.cs", "Assets/Core/Core.cs");
        placed.Products[PlacedFiles.PurchaseKey("core-item")] = Entry(project, "Assets/Core/Core.cs");
        placed.Products[PlacedFiles.LicenseKey("core-lic")] = Entry(project, "Assets/Core/Core.cs");
        placed.Products[PlacedFiles.LicenseKey("gone-lic")] = Entry(project, "Assets/Gone/Gone.cs");
        var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in new[] { "addon-lic", "core-lic", "gone-lic" })
            records[id] = new InstallRecord { LicenseId = id, VersionId = "v1", Label = "1.0" };
        var scope = new[] { "addon-lic", "core-lic", "gone-lic", "unrecorded-lic" };
        var addons = new[] { "addon-lic" };
        // Recorded and a file of its own here: installed. A core may count a file it shares (its add-on carries it). Recorded but its
        // files gone: not. Not in scope: never (a marker decides there).
        Equal("addon-lic,core-lic", string.Join(",", CoreAddons.InstalledByRecord(scope, addons, records, placed, project)));
        // review R2: an add-on whose own file is gone counts not by the core's file it also carried.
        File.Delete(Path.Combine(project, "Assets", "Addon", "Plus.cs"));
        Equal("core-lic", string.Join(",", CoreAddons.InstalledByRecord(scope, addons, records, placed, project)));
        // Its own path with other contents (the user's file now) is no proof either.
        Put(project, "Assets/Addon/Plus.cs", "the user's own");
        Equal("core-lic", string.Join(",", CoreAddons.InstalledByRecord(scope, addons, records, placed, project)));
        Put(project, "Assets/Addon/Plus.cs", "plus");
        Equal("addon-lic,core-lic", string.Join(",", CoreAddons.InstalledByRecord(scope, addons, records, placed, project)));
        // No record, files here: not (the files alone could be another row's).
        records.Remove("addon-lic");
        Equal("core-lic", string.Join(",", CoreAddons.InstalledByRecord(scope, addons, records, placed, project)));
        // The live scope is the file cores' licenses and the add-on licenses (config), whatever product they are.
        var live = new List<string>(CoreAddons.RecordScopeLicenseIds());
        foreach (var id in OnAirDeckConfig.CoreAddonLicenses.Keys) Require(live.Contains(id), "add-on license " + id);
        foreach (var core in OnAirDeckConfig.FileCores.Values) foreach (var id in core.LicenseIds) Require(live.Contains(id), "file core license " + id);
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
        placed.Products[PlacedFiles.LicenseKey("other")] = Entry(project, "Assets/StudioRaming/MaskMaker/MaskMaker.cs");
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
            tracker.BeforeWrite(path, GuidOf(path));
        Equal("Assets/MyTools/Product.cs,Assets/Tool/Sub/New.cs", string.Join(",", tracker.Paths));
        Equal("Assets/Editor/UserTool.cs", string.Join(",", tracker.Touched));
        Equal("Assets/Tool/Sub,Assets/Tool", string.Join(",", tracker.Folders));
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("tool"), tracker);
        // The import was cancelled after the record was written: nothing of it is here, so nothing is removed; the user's file stays.
        var placed = PlacedFiles.Load(project);
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Equal(RemovalRefusal.NothingHere, plan.Refusal);
        // After the import: its files go, the user's MyTools folder and Assets/Editor stay, the folders it created go.
        Put(project, "Assets/MyTools/Product.cs", "p");
        Put(project, "Assets/MyTools/Product.cs.meta", "guid: " + GuidOf("Assets/MyTools/Product.cs") + "\n");
        Put(project, "Assets/Tool/Sub/New.cs", "n");
        Put(project, "Assets/Tool/Sub/New.cs.meta", "guid: " + GuidOf("Assets/Tool/Sub/New.cs") + "\n");
        Put(project, "Assets/Tool/Sub.meta", "m");
        Require(PlacedFiles.Load(project).Products[PlacedFiles.LicenseKey("tool")].Guids.Count == 2);   // none for the touched user file
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
        placed.Products[PlacedFiles.LicenseKey("tool")] = Entry(project, "Assets/Tool/A.cs", "Assets/Tool/B.shader", "Assets/Tool/Shared.cginc", "Assets/Tool/Gone.cs",
            Installer.InstallRoot + "/Other/onairdeck.license");
        placed.Products[PlacedFiles.LicenseKey("other")] = Entry(project, "Assets/Tool/Shared.cginc");
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("tool") }, placed, null, "");
        Require(plan.CanRun);
        Equal("Assets/Tool/A.cs,Assets/Tool/B.shader", string.Join(",", plan.Files));
        Require(plan.Shared.Contains("Assets/Tool/Shared.cginc") && plan.Shared.Contains(Installer.InstallRoot + "/Other/onairdeck.license"), string.Join(",", plan.Shared));
        Require(!plan.Files.Contains("Assets/Tool/Mine.mat") && !plan.Files.Contains("Assets/Tool/Gone.cs"));
        var gone = new PlacedRecords();
        gone.Products[PlacedFiles.LicenseKey("x")] = Entry(project, "Assets/Tool/Gone.cs");
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
        // A installs first and creates Lib.cginc; B's install finds it there and only touches it.
        var a = new PlacementTracker(project);
        a.BeforeWrite("Assets/Common/Lib.cginc");
        a.BeforeWrite("Assets/A/Only.cs");
        Put(project, "Assets/Common/Lib.cginc", "s");
        Put(project, "Assets/A/Only.cs", "a");
        a.AfterWrite("Assets/Common/Lib.cginc");
        a.AfterWrite("Assets/A/Only.cs");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("a"), a);
        var b = new PlacementTracker(project);
        b.BeforeWrite("Assets/Common/Lib.cginc");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("b"), b);
        // Removing A takes its own file, never Lib.cginc (B wrote it too).
        var first = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("a") }, PlacedFiles.Load(project), null, "");
        Equal("Assets/A/Only.cs", string.Join(",", first.Files));
        Equal("Assets/Common/Lib.cginc", string.Join(",", first.Shared));
        // B alone cannot take it yet: A created it.
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("b") }, PlacedFiles.Load(project), null, "").Refusal);
        File.Delete(Path.Combine(project, "Assets", "A", "Only.cs"));
        PlacedFiles.Release(project, PlacedFiles.LicenseKey("a"));   // A's removal went through
        Require(!PlacedFiles.Load(project).Products.ContainsKey(PlacedFiles.LicenseKey("a")));
        var last = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("b") }, PlacedFiles.Load(project), null, "");
        Equal("Assets/Common/Lib.cginc", string.Join(",", last.Files));
        // A user's file that a product only touched never passes to anyone.
        Put(project, "Assets/User/Mine.cs", "user");
        var c = new PlacementTracker(project);
        c.BeforeWrite("Assets/User/Mine.cs");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("c"), c);
        var d = new PlacementTracker(project);
        d.BeforeWrite("Assets/User/Mine.cs");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("d"), d);
        PlacedFiles.Release(project, PlacedFiles.LicenseKey("c"));
        foreach (var entry in PlacedFiles.Load(project).Products.Values) Require(!entry.Paths.Contains("Assets/User/Mine.cs"));
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("d") }, PlacedFiles.Load(project), null, "").Refusal);
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
        // VModular's add-on (the VModular Plus license) is known since 2026-11-04: a package form of the core is removable, and the
        // window takes the add-on out first (CoreAddons.CoreOf of the Plus license is the core). No core is pending any more.
        Package(project, "com.studioraming.vmodular", "2.0.0");
        Require(RemovalPlanner.ForPackage(project, "com.studioraming.vmodular", placed, null).CanRun);
        Equal(0, OnAirDeckConfig.CoreAddonsPending.Length);
    }

    private static void Record()
    {
        var project = NewProject();
        var key = PlacedFiles.LicenseKey("tool");
        var tracker = new PlacementTracker(project);
        foreach (var path in new[] { "Assets/Tool/A.cs", "Assets\\Tool\\B.cs", "../escape.cs", "Packages/x/y.cs", "Assets/Tool/A.cs.meta", "Assets/../x.cs", "C:/abs.cs", "Assets/Tool/" })
            tracker.BeforeWrite(path);
        tracker.BeforeCreateFolder("Assets");
        tracker.BeforeCreateFolder("Assets/Editor");
        PlacedFiles.Add(project, key, tracker);
        var again = new PlacementTracker(project);
        again.BeforeWrite("Assets/Tool/C.cs");
        PlacedFiles.Add(project, key, again);
        Put(project, "Assets/Tool/A.cs", "a");
        var reinstall = new PlacementTracker(project);
        reinstall.BeforeWrite("Assets/Tool/A.cs");   // its own file, there from the first install: stays created by it
        PlacedFiles.Add(project, key, reinstall);
        var loaded = PlacedFiles.Load(project).Products[key];
        Equal("Assets/Tool/A.cs,Assets/Tool/B.cs,Assets/Tool/C.cs", string.Join(",", loaded.Paths));
        Equal(0, loaded.Touched.Count);
        Equal("Assets/Tool", string.Join(",", loaded.Folders));
        PlacedFiles.Release(project, key);
        Require(!PlacedFiles.Load(project).Products.ContainsKey(key));
        // A format-1 file (the unreleased first draft) or a broken one reads as no records.
        Put(project, "ProjectSettings/OnAirDeck/placed-files.json", "{\"format\":1,\"products\":[{\"key\":\"license:x\",\"paths\":[\"Assets/Tool/A.cs\"]}]}");
        Equal(0, PlacedFiles.Load(project).Products.Count);
        PlacedFiles.AddPackage(project, Core);
        PlacedFiles.RemovePackage(project, Core);
        Require(!PlacedFiles.Load(project).Packages.Contains(Core));
        Put(project, "ProjectSettings/OnAirDeck/placed-files.json", "{ broken");
        Equal(0, PlacedFiles.Load(project).Products.Count);
        PlacedFiles.AddPackage(project, Core);
        Require(PlacedFiles.Load(project).Packages.Contains(Core));
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

    private static void OtherOptionKeepsLicense()
    {
        // Two options of one product share its folder and its onairdeck.license (checked by product id).
        var project = NewProject();
        var placed = new PlacedRecords();
        var night = new PlacedEntry();
        night.Paths.Add("Assets/OnAirDeck/Sample Resort/Night.unity");
        var day = new PlacedEntry();
        day.Touched.Add("Assets/OnAirDeck/Sample Resort/Shared.png");
        placed.Products["purchase:item-night"] = night;
        placed.Products["purchase:item-day"] = day;
        Require(!RemovalPlanner.OtherOptionHere(project, placed, new[] { "purchase:item-night" }), "nothing on disk yet");
        Put(project, "Assets/OnAirDeck/Sample Resort/Night.unity", "scene");
        Require(RemovalPlanner.OtherOptionHere(project, placed, new[] { "purchase:item-night" }), "a created file is here");
        Put(project, "Assets/OnAirDeck/Sample Resort/Shared.png", "png");
        Require(RemovalPlanner.OtherOptionHere(project, placed, new[] { "purchase:item-day" }), "a file it wrote over is here");
        Require(!RemovalPlanner.OtherOptionHere(project, placed, new[] { "purchase:item-none", null, "" }), "no record");
        Require(!RemovalPlanner.OtherOptionHere(project, placed, new string[0]), "no other option");
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

    private static void GuidProof()
    {
        var project = NewProject();
        const string Guid = "0123456789abcdef0123456789abcdef";
        var tracker = new PlacementTracker(project);
        tracker.BeforeWrite("Assets/Pkg/Tool.cs", Guid);
        tracker.BeforeWrite("Assets/Pkg/Shader.shader", "ffffffffffffffffffffffffffffffff");
        PlacedFiles.Add(project, PlacedFiles.LicenseKey("pkg"), tracker);
        // The import ran for Tool.cs (its .meta has the package's GUID). Shader.shader is the user's own, saved later at that path.
        Put(project, "Assets/Pkg/Tool.cs", "t");
        Put(project, "Assets/Pkg/Tool.cs.meta", "fileFormatVersion: 2\nguid: " + Guid + "\n");
        Put(project, "Assets/Pkg/Shader.shader", "user");
        Put(project, "Assets/Pkg/Shader.shader.meta", "fileFormatVersion: 2\nguid: 99999999999999999999999999999999\n");
        var plan = RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("pkg") }, PlacedFiles.Load(project), null, "");
        Equal("Assets/Pkg/Tool.cs", string.Join(",", plan.Files));
        Equal("Assets/Pkg/Shader.shader", string.Join(",", plan.Changed));
        // No .meta at all (never imported): not removed either.
        File.Delete(Path.Combine(project, "Assets", "Pkg", "Tool.cs.meta"));
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForFiles(project, new[] { PlacedFiles.LicenseKey("pkg") }, PlacedFiles.Load(project), null, "").Refusal);
    }

    private static void HashProof()
    {
        var project = NewProject();
        var key = PlacedFiles.LicenseKey("tool");
        var first = new PlacementTracker(project);
        foreach (var path in new[] { "Assets/Tool/A.txt", "Assets/Tool/B.txt", "Assets/Tool/C.txt" }) first.BeforeWrite(path);
        Put(project, "Assets/Tool/A.txt", "a1");
        Put(project, "Assets/Tool/B.txt", "b1");
        Put(project, "Assets/Tool/C.txt", "c1");
        foreach (var path in new[] { "Assets/Tool/A.txt", "Assets/Tool/B.txt", "Assets/Tool/C.txt" }) first.AfterWrite(path);
        PlacedFiles.Add(project, key, first);
        // An update of the same product rewrites C.txt: its new contents are its proof now, it stays the product's own.
        var update = new PlacementTracker(project);
        update.BeforeWrite("Assets/Tool/C.txt");
        Put(project, "Assets/Tool/C.txt", "c2");
        update.AfterWrite("Assets/Tool/C.txt");
        PlacedFiles.Add(project, key, update);
        // The user changed A.txt; B.txt was deleted and the user saved their own B.txt there.
        Put(project, "Assets/Tool/A.txt", "edited by the user");
        Put(project, "Assets/Tool/B.txt", "the user's own");
        var plan = RemovalPlanner.ForFiles(project, new[] { key }, PlacedFiles.Load(project), null, "");
        Equal("Assets/Tool/C.txt", string.Join(",", plan.Files));
        Equal("Assets/Tool/A.txt,Assets/Tool/B.txt", string.Join(",", plan.Changed));
        // A recorded file without any proof stays.
        var bare = new PlacedRecords();
        var entry = new PlacedEntry();
        entry.Paths.Add("Assets/Tool/C.txt");
        bare.Products[key] = entry;
        Equal(RemovalRefusal.NothingHere, RemovalPlanner.ForFiles(project, new[] { key }, bare, null, "").Refusal);
    }

    private static void FolderRecheckAfter()
    {
        var project = NewProject();
        Put(project, "Assets/Tool/Sub/New.cs", "saved after the removal");   // the removed New.cs is back at the same path
        var created = new HashSet<string> { "Assets/Tool", "Assets/Tool/Sub" };
        Equal(0, RemovalPlanner.EmptiedFolders(project, new[] { "Assets/Tool/Sub/New.cs" }, created, true).Count);
        File.Delete(Path.Combine(project, "Assets", "Tool", "Sub", "New.cs"));
        Equal("Assets/Tool/Sub,Assets/Tool", string.Join(",", RemovalPlanner.EmptiedFolders(project, new[] { "Assets/Tool/Sub/New.cs" }, created, true)));
    }

    private static string GuidOf(string path)
    {
        using (var md5 = System.Security.Cryptography.MD5.Create())
            return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(path))).Replace("-", "").ToLowerInvariant();
    }

    private static void ZipFolders()
    {
        var zip = Path.Combine(_root, "dirs-" + System.Guid.NewGuid().ToString("N") + ".zip");
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Sub/");
            using (var writer = new StreamWriter(archive.CreateEntry("Sub/Tool.cs").Open())) writer.Write("t");
        }
        var folder = Installer.ProductFolder("zipdirs-" + System.Guid.NewGuid().ToString("N").Substring(0, 6));
        var tracker = new PlacementTracker(_host);
        int skipped;
        Equal(1, Installer.ExtractZip(zip, folder, out skipped, tracker));
        Equal(folder + "/Sub/Tool.cs", string.Join(",", tracker.Paths));
        Require(tracker.Folders.Contains(folder) && tracker.Folders.Contains(folder + "/Sub"), string.Join(",", tracker.Folders));
        Require(!tracker.Folders.Contains(Installer.InstallRoot));
    }

    // ---------- option overwrite (review R2, r3) ----------

    private const string Night = "purchase:item-night";
    private const string Day = "purchase:item-day";
    private const string Scene = "Assets/OnAirDeck/Sample Resort/Scene.unity";
    private const string Product = "product-resort";

    private static string Sha(string text)
    {
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text))) return RemovalPlanner.StreamSha256(stream);
    }

    private static string Meta(string guid) { return "fileFormatVersion: 2\nguid: " + guid + "\n"; }

    /// <summary>The night option installed Scene.unity (recorded as created, with its proof and its product), as an install does.</summary>
    private static string NightInstalled()
    {
        var project = NewProject();
        var tracker = new PlacementTracker(project);
        tracker.BeforeWrite(Scene);
        Put(project, Scene, "night");
        tracker.AfterWrite(Scene);
        PlacedFiles.Add(project, Night, tracker, Product);
        return project;
    }

    private static string Paths(IEnumerable<IncomingFile> files) { return string.Join(",", files.Select(f => f.Path)); }

    private static void OptionOverwriteRule()
    {
        var project = NightInstalled();
        var prefab = "Assets/OnAirDeck/Sample Resort/Lamp.prefab";
        var touched = "Assets/OnAirDeck/Sample Resort/Touched.png";
        var others = "Assets/Other/Shared.png";
        Put(project, prefab, "lamp");
        Put(project, prefab + ".meta", Meta(new string('a', 32)));
        Put(project, touched, "t");
        Put(project, others, "o");
        var placed = PlacedFiles.Load(project);
        placed.Products[Night].Paths.Add(prefab);
        placed.Products[Night].Touched.Add(touched);
        var other = new PlacedEntry();
        other.Paths.Add(others);
        placed.Products["purchase:other-product"] = other;
        var incoming = new[]
        {
            new IncomingFile { Path = Scene, Sha256 = Sha("day") },                 // other contents: asked
            new IncomingFile { Path = prefab, Guid = new string('b', 32) },          // another GUID: asked
            new IncomingFile { Path = touched, Sha256 = Sha("other") },              // the night option only wrote over it: not its file
            new IncomingFile { Path = others, Sha256 = Sha("other") },               // another product's file: not an option
            new IncomingFile { Path = "Assets/OnAirDeck/Sample Resort/New.png", Sha256 = Sha("n") },
        };
        Equal(Scene + "," + prefab, Paths(OptionOverwrite.Conflicts(project, placed, new[] { Night }, incoming)));
        // The same contents, the same GUID and contents, or no other option: nothing to ask.
        Equal(0, OptionOverwrite.Conflicts(project, placed, new[] { Night }, new[] { new IncomingFile { Path = Scene, Sha256 = Sha("night") }, new IncomingFile { Path = prefab, Guid = new string('a', 32), Sha256 = Sha("lamp") } }).Count);
        Equal(0, OptionOverwrite.Conflicts(project, placed, new string[0], incoming).Count);
        Equal(0, OptionOverwrite.Conflicts(project, placed, new[] { Day }, incoming).Count);
        // R3-01: a .unitypackage asset with the same path and GUID but other contents is asked about.
        Equal(prefab, Paths(OptionOverwrite.Conflicts(project, placed, new[] { Night }, new[] { new IncomingFile { Path = prefab, Guid = new string('a', 32), Sha256 = Sha("other lamp") } })));
        // R3-02: two writes to one path (two zips): the first is the same, the second is not; asked, with the last write as the claim.
        // The path's case does not matter (Windows).
        var twice = OptionOverwrite.Conflicts(project, placed, new[] { Night }, new[]
        {
            new IncomingFile { Path = Scene, Sha256 = Sha("night") },
            new IncomingFile { Path = Scene.ToLowerInvariant(), Sha256 = Sha("day") },
        });
        Equal(1, twice.Count);
        Equal(Sha("day"), twice[0].Sha256);
        // A file the other option created but the person deleted since: nothing is overwritten.
        File.Delete(Path.Combine(project, Scene.Replace('/', Path.DirectorySeparatorChar)));
        Equal(prefab, Paths(OptionOverwrite.Conflicts(project, placed, new[] { Night }, incoming)));
    }

    private static void OptionOverwriteYes()
    {
        var project = NightInstalled();
        // An earlier install of the day option wrote the same scene over it (no question then): day only touched it.
        var earlier = new PlacementTracker(project);
        earlier.BeforeWrite(Scene);
        PlacedFiles.Add(project, Day, earlier, Product);
        Require(PlacedFiles.Load(project).Products[Day].Touched.Contains(Scene));
        var asked = new List<string>();
        var claims = OptionOverwrite.Check(project, Day, Product, new[] { Night }, () => new[] { new IncomingFile { Path = Scene, Sha256 = Sha("day") } },
            paths => { asked.AddRange(paths); return true; });
        Equal(Scene, string.Join(",", asked));
        Equal(Scene, Paths(claims));
        // The day option's install, as PlaceFilesAsync runs it: write, record, claim (a written file passes over at once).
        var tracker = new PlacementTracker(project);
        tracker.BeforeWrite(Scene);
        Put(project, Scene, "day");
        tracker.AfterWrite(Scene);
        PlacedFiles.Add(project, Day, tracker, Product);
        PlacedFiles.Claim(project, Day, claims);
        var placed = PlacedFiles.Load(project);
        Require(!placed.Products[Night].Paths.Contains(Scene) && !placed.Products[Night].Hashes.ContainsKey(Scene), "night gave it up");
        Require(placed.Products[Day].Paths.Contains(Scene) && !placed.Products[Day].Touched.Contains(Scene), "day created it");
        Require(placed.Products[Day].Claims.Count == 0, "the claim is settled");
        Equal(Sha("day"), placed.Products[Day].Hashes[Scene]);
        Require(RemovalPlanner.ForFiles(project, new[] { Day }, placed, null, "").Files.Contains(Scene), "removing day takes it");
        Require(!RemovalPlanner.ForFiles(project, new[] { Night }, placed, null, "").Files.Contains(Scene), "removing night leaves it");
    }

    private static void OptionOverwritePendingImport()
    {
        // R3-03: a .unitypackage asset is claimed before its import; until the import wrote it, and for good when it was cancelled,
        // the other option keeps the file.
        var project = NightInstalled();
        Put(project, Scene + ".meta", Meta(new string('a', 32)));
        var night = PlacedFiles.Load(project).Products[Night];
        night.Guids[Scene] = new string('a', 32);
        var claims = new[] { new IncomingFile { Path = Scene, Guid = new string('b', 32), Sha256 = Sha("day") } };
        PlacedFiles.Claim(project, Day, claims);
        var placed = PlacedFiles.Load(project);
        Require(placed.Products[Night].Paths.Contains(Scene), "night keeps it before the import");
        Require(placed.Products[Day].Claims.ContainsKey(Scene) && !placed.Products[Day].Paths.Contains(Scene), "day only claims it");
        Require(RemovalPlanner.ForFiles(project, new[] { Night }, placed, null, "").Files.Contains(Scene), "removing night still takes its own file");
        Require(!PlacedFiles.ResolveClaims(project), "a cancelled import changes nothing");
        // The import wrote it: the next resolve passes it over.
        Put(project, Scene, "day");
        Put(project, Scene + ".meta", Meta(new string('b', 32)));
        Require(PlacedFiles.ResolveClaims(project));
        placed = PlacedFiles.Load(project);
        Require(!placed.Products[Night].Paths.Contains(Scene) && placed.Products[Day].Paths.Contains(Scene));
        Equal(new string('b', 32), placed.Products[Day].Guids[Scene]);
        // A claimed file the person deleted is dropped.
        PlacedFiles.Claim(project, Night, new[] { new IncomingFile { Path = Scene, Sha256 = Sha("night again") } });
        File.Delete(Path.Combine(project, Scene.Replace('/', Path.DirectorySeparatorChar)));
        Require(PlacedFiles.ResolveClaims(project));
        Equal(0, PlacedFiles.Load(project).Products[Night].Claims.Count);
    }

    private static void OptionOverwriteByRecord()
    {
        // R3-04: the night option is no longer a row of the list (its files went), but its record names the product: still asked.
        var project = NightInstalled();
        var asked = 0;
        var claims = OptionOverwrite.Check(project, Day, Product, new string[0], () => new[] { new IncomingFile { Path = Scene, Sha256 = Sha("day") } }, paths => { asked++; return true; });
        Equal(1, asked);
        Equal(Scene, Paths(claims));
        Equal(Night, string.Join(",", PlacedFiles.OptionKeys(PlacedFiles.Load(project), Product, Day)));
        Equal(0, PlacedFiles.OptionKeys(PlacedFiles.Load(project), "another-product", Day).Count);
        // A record from before the product was kept, and no row: not asked (existing installs are unaffected).
        var old = NewProject();
        var tracker = new PlacementTracker(old);
        tracker.BeforeWrite(Scene);
        Put(old, Scene, "night");
        tracker.AfterWrite(Scene);
        PlacedFiles.Add(old, Night, tracker);
        Equal(0, OptionOverwrite.Check(old, Day, Product, new string[0], () => new[] { new IncomingFile { Path = Scene, Sha256 = Sha("day") } }, paths => { asked++; return true; }).Count);
        Equal(1, asked);
    }

    private static void PackageAssetHashes()
    {
        // R3-01: the contents of each .unitypackage asset, read without Unity.
        var package = Path.Combine(_root, "assets-" + Guid.NewGuid().ToString("N") + ".unitypackage");
        var big = new string('x', 200000);
        using (var file = File.Create(package))
        using (var gz = new GZipStream(file, CompressionMode.Compress))
        {
            var bytes = LegacyChecks.Tar(
                new KeyValuePair<string, byte[]>(new string('a', 32) + "/pathname", Encoding.UTF8.GetBytes(Scene)),
                new KeyValuePair<string, byte[]>(new string('a', 32) + "/asset", Encoding.UTF8.GetBytes("day")),
                new KeyValuePair<string, byte[]>(new string('b', 32) + "/pathname", Encoding.UTF8.GetBytes("Assets/Big.bytes")),
                new KeyValuePair<string, byte[]>(new string('b', 32) + "/asset", Encoding.UTF8.GetBytes(big)));
            gz.Write(bytes, 0, bytes.Length);
        }
        var hashes = UnityPackageArchive.AssetSha256(package);
        Equal(Sha("day"), hashes[new string('a', 32)]);
        Equal(Sha(big), hashes[new string('b', 32)]);
    }

    private static void OptionOverwriteNo()
    {
        var project = NightInstalled();
        var record = Path.Combine(project, "ProjectSettings", "OnAirDeck", "placed-files.json");
        var before = File.ReadAllText(record);
        var listed = false;
        var threw = false;
        try
        {
            OptionOverwrite.Check(project, Day, Product, new[] { Night }, () => { listed = true; return new[] { new IncomingFile { Path = Scene, Sha256 = Sha("day") } }; }, paths => false);
        }
        catch (OptionOverwriteDeclinedException e) { threw = e.Files == 1; }
        Require(threw && listed, "declined before anything is written");
        Equal(before, File.ReadAllText(record));
        Equal("night", File.ReadAllText(Path.Combine(project, Scene.Replace('/', Path.DirectorySeparatorChar))));
        // Without another option nothing is listed or asked.
        listed = false;
        Equal(0, OptionOverwrite.Check(project, Day, null, new string[0], () => { listed = true; return new IncomingFile[0]; }, paths => false).Count);
        Require(!listed);
        // In a batch the declined option is skipped and the next one still runs.
        var ran = new List<string>();
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = "Sample Resort \u00B7 PRO Day", Run = r => { ran.Add("day"); throw new OptionOverwriteDeclinedException(1); } },
            new BatchItem { Name = "Lamp", Run = r => { ran.Add("lamp"); return System.Threading.Tasks.Task.FromResult<object>(1); } },
        });
        queue.RunAsync().GetAwaiter().GetResult();
        Equal("day,lamp", string.Join(",", ran));
        Equal(BatchStatus.Skipped, queue.Results[0].Status);
        Equal(BatchStatus.Done, queue.Results[1].Status);
    }

    private static void ZipTargetsMatchExtract()
    {
        var zip = Path.Combine(_root, "targets-" + Guid.NewGuid().ToString("N") + ".zip");
        using (var stream = File.Create(zip))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Sub/");
            using (var writer = new StreamWriter(archive.CreateEntry("Sub/Night.unity").Open())) writer.Write("night");
            using (var writer = new StreamWriter(archive.CreateEntry("__MACOSX/._Night.unity").Open())) writer.Write("fork");
            using (var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open())) writer.Write("no");
            using (var writer = new StreamWriter(archive.CreateEntry("Top.txt").Open())) writer.Write("top");
        }
        var folder = Installer.ProductFolder("targets-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        var targets = Installer.ZipTargets(zip, folder);
        var tracker = new PlacementTracker(_host);
        int skipped;
        Installer.ExtractZip(zip, folder, out skipped, tracker);
        Equal(string.Join(",", tracker.Paths.OrderBy(x => x, StringComparer.Ordinal)), string.Join(",", targets.Select(t => t.Path).OrderBy(x => x, StringComparer.Ordinal)));
        foreach (var t in targets) Equal(RemovalPlanner.FileSha256(_host, t.Path), t.Sha256);
    }

    // ---------- fixtures ----------

    private static PlacedEntry Entry(string project, params string[] paths)
    {
        var entry = new PlacedEntry();
        entry.Paths.AddRange(paths);
        foreach (var path in paths)
        {
            var hash = RemovalPlanner.FileSha256(project, path);
            if (hash.Length > 0) entry.Hashes[path] = hash;
        }
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
