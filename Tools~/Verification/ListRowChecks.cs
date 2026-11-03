using System;
using System.Collections.Generic;
using System.Linq;
using OnAirDeck.UnityManager;

// Owner report 2026-11-03 on the list: an installed, up-to-date row had no chip next to "to get" rows; some rows had a checkbox and others
// not; VModular Plus needs VModular the way the URP add-on needs MingToon Core. Real sources: ManagerCatalog (chip, batch rule, filter),
// CoreAddons (purchase add-ons, order, nesting). The window half (drawing the disabled box, its tooltip, the run) is compile-checked only.
internal static class ListRowChecks
{
    private const string Vmodular = "com.studioraming.vmodular";
    private const string Product = "vmodular-product";

    public static void Run(Action<string, Action> check)
    {
        check("list chip: installed at the newest version says 'installed' (it had no chip), as does a newer install; update and to-get keep theirs", Chips);
        check("list filter 'installed': takes up-to-date, newer and update rows, not the to-get ones", InstalledFilter);
        check("list checkbox: a package or a file (.unitypackage) license can be batched; up to date, newer, or nothing offered cannot, each with its reason", BatchRule);
        check("purchase add-on: a row is VModular Plus only when every file it lists is the add-on option's; the base product alone, a mixed row, no files or another product never are", PurchaseAddonRule);
        check("purchase add-on: a run puts VModular first and its purchase add-on last, the base product's own row in between", PurchaseAddonOrderAndNest);
        check("file core (G4): the live ids map the Plus license to VModular, the base license and a base purchase row carry it, a mixed Plus row is no add-on", FileCoreIds);
        check("file core (G4): VModular is here by its marker in Assets/ or by a package in Packages/, not by an unrelated file", FileCoreHere);
        check("file core (G4): a run puts the base license before the Plus license, and the list nests Plus under the base license row", FileCoreOrderAndNest);
        check("file core (G4 retest D1): the base that reaches a buyer only as a purchase row is the core to install first, and Plus nests under it", FileCoreFromPurchase);
        check("import queue (G4 retest D2): an import a script reload cut off counts as imported only when every file of the package is on disk with its GUID", ImportSettledAfterReload);
        check("import queue (G4 retest review R1-R3): old content is not an import, a cut-off head settles at once, core-first does not mix import modes", RetestReviewRules);
        check("import queue (G4 design A): script reloads are held from before ImportPackage until the import ends, released once on completed, cancelled, failed or a throw", ReloadHeldDuringImport);
        check("import queue (G4 design A r1): an open import window always waits; a window gone for the grace, or a non-interactive import with no callback in time, settles from disk", ImportWatchRules);
    }

    // Design A (lead, 2026-11-04): the reload that dropped the completion callback (D2) cannot happen while the queue holds reloads.
    private static void ReloadHeldDuringImport()
    {
        var locks = 0;
        var hold = new ImportReloadHold(() => locks++, () => locks--);

        // Normal path: held before the import call runs, still held after it returns (an interactive window is open), released once
        // by the completion callback. A second release (a late callback) does not unlock someone else's hold.
        var heldDuringImport = -1;
        Require(hold.Run(() => heldDuringImport = locks) == null, "import ran");
        Equal(1, heldDuringImport);
        Require(hold.Held && locks == 1, "held while the window is open");
        Require(hold.Release(), "completion releases");
        Equal(0, locks);
        Require(!hold.Release(), "a second release is a no-op");
        Equal(0, locks);

        // Cancelled and failed end the same way (Finish releases); holding twice locks once.
        hold.Hold();
        hold.Hold();
        Equal(1, locks);
        hold.Release();
        Equal(0, locks);

        // A throw from ImportPackage releases at once and hands the error back, so the queue can fail the head.
        var thrown = hold.Run(() => { throw new InvalidOperationException("locked file"); });
        Require(thrown is InvalidOperationException, "error handed back");
        Require(!hold.Held && locks == 0, "released after a throw");

    }

    // Design A review r1 (R1, R2): Unity raises no callback when its import window is closed with X (2021.3 PackageImport has no
    // close hook), so a hold must end without one; but never while the window is open, or the person's answer would be lost to a reload.
    private static void ImportWatchRules()
    {
        var grace = ImportWatch.ClosedGraceSeconds;
        var noCallback = ImportWatch.NoCallbackSeconds;
        // An open window waits for the person, however long (R2: no unlock, no settle under it).
        Equal(ImportWatchStep.Wait, ImportWatch.Next(true, true, true, false, 100000, 0));
        Equal(ImportWatchStep.Wait, ImportWatch.Next(false, true, true, false, 100000, 100000));
        // Unity is still importing or compiling: the callback may still come.
        Equal(ImportWatchStep.Wait, ImportWatch.Next(true, false, true, true, 100000, 100000));
        Equal(ImportWatchStep.Wait, ImportWatch.Next(false, false, false, true, 100000, 100000));
        // Interactive, the window was shown and is gone (closed with X): settles after the grace, so the queue goes on (R1).
        Equal(ImportWatchStep.Wait, ImportWatch.Next(true, false, true, false, 100, grace - 0.5));
        Equal(ImportWatchStep.Settle, ImportWatch.Next(true, false, true, false, 100, grace));
        // A window gone but not for long enough waits even long after the start (the person pressed Import a moment ago).
        Equal(ImportWatchStep.Wait, ImportWatch.Next(true, false, true, false, noCallback * 10, 1));
        // Never shown yet (Unity may still be unpacking a large package): only the long limit settles it.
        Equal(ImportWatchStep.Wait, ImportWatch.Next(true, false, false, false, noCallback - 1, noCallback - 1));
        Equal(ImportWatchStep.Settle, ImportWatch.Next(true, false, false, false, noCallback, noCallback));
        // Non-interactive: no window to watch; only a callback that never came within the limit settles it.
        Equal(ImportWatchStep.Wait, ImportWatch.Next(false, false, false, false, noCallback - 1, noCallback - 1));
        Equal(ImportWatchStep.Settle, ImportWatch.Next(false, false, false, false, noCallback, noCallback));
    }

    private static void RetestReviewRules()
    {
        // R2: an update keeps paths and GUIDs; the old files alone are not the new import. With the package's hashes, content decides.
        var project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oad-r2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var full = System.IO.Path.Combine(project, "Assets", "Tool", "A.cs");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
            System.IO.File.WriteAllText(full, "old");
            System.IO.File.WriteAllText(full + ".meta", "guid: a1\n");
            var assets = new List<PackageAsset> { new PackageAsset { Path = "Assets/Tool/A.cs", Guid = "a1" } };
            string newHash;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                newHash = BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("new"))).Replace("-", "").ToLowerInvariant();
            var hashes = new Dictionary<string, string> { { "a1", newHash } };
            Require(!UnityPackageArchive.ImportedOnDisk(project, assets, hashes), "old content with the same GUID");
            System.IO.File.WriteAllText(full, "new");
            Require(UnityPackageArchive.ImportedOnDisk(project, assets, hashes), "new content");
            Require(UnityPackageArchive.ImportedOnDisk(project, assets), "without hashes the GUID check alone still works");
        }
        finally
        {
            try { System.IO.Directory.Delete(project, true); } catch (Exception) { }
        }

        // Safety net only (design A, review r2 R6): reloads are held during a queued import, so a started head in a new domain means
        // the editor restarted or Unity forced a reload. It settles at once from disk: waiting for a window that may never come back
        // would stop every later import of the session.
        Equal(ImportSettle.Imported, UnityPackageArchive.SettleAfterReload(true, true));
        Equal(ImportSettle.Imported, UnityPackageArchive.SettleAfterReload(true, false));
        Equal(ImportSettle.Failed, UnityPackageArchive.SettleAfterReload(false, true));
        Equal(ImportSettle.Failed, UnityPackageArchive.SettleAfterReload(false, false));

        // R3: a non-interactive core download may not join a queue that holds an interactive import (Enqueue would throw).
        Require(CoreAddons.CanJoinImportQueue(0, true, false), "empty queue");
        Require(CoreAddons.CanJoinImportQueue(2, false, false), "same mode");
        Require(!CoreAddons.CanJoinImportQueue(1, true, false), "an interactive import is still queued");
    }

    // test1 on 2026-11-04: the base import compiled scripts, the domain reloaded mid-import, the queue's completion callback never
    // ran, so the queue kept the package and refused the next (non-interactive) import. After a reload the queue settles that head
    // from what is on disk.
    private static void ImportSettledAfterReload()
    {
        var project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oad-settle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var assets = new List<PackageAsset>
            {
                new PackageAsset { Path = "Assets/Editor/VModularEditor", IsFolder = true, Guid = "f0" },
                new PackageAsset { Path = "Assets/Editor/VModularEditor/VModularManager.cs", Guid = "a1" },
                new PackageAsset { Path = "Assets/VModualr_InAvartar/VModularOneBone.cs", Guid = "b2" },
            };
            Action<string, string> put = (path, guid) =>
            {
                var full = System.IO.Path.Combine(project, path.Replace('/', System.IO.Path.DirectorySeparatorChar));
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full));
                System.IO.File.WriteAllText(full, "//");
                if (guid != null) System.IO.File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\nMonoImporter:\n");
            };
            Require(!UnityPackageArchive.ImportedOnDisk(project, assets), "nothing on disk");
            put("Assets/Editor/VModularEditor/VModularManager.cs", "a1");
            Require(!UnityPackageArchive.ImportedOnDisk(project, assets), "partial import counts as failed");
            put("Assets/VModualr_InAvartar/VModularOneBone.cs", "zz");
            Require(!UnityPackageArchive.ImportedOnDisk(project, assets), "a file with another GUID is not this package's");
            put("Assets/VModualr_InAvartar/VModularOneBone.cs", "b2");
            Require(UnityPackageArchive.ImportedOnDisk(project, assets), "every file with its GUID");
            put("Assets/VModualr_InAvartar/VModularOneBone.cs", null);
            System.IO.File.Delete(System.IO.Path.Combine(project, "Assets", "VModualr_InAvartar", "VModularOneBone.cs.meta"));
            Require(!UnityPackageArchive.ImportedOnDisk(project, assets), "no .meta yet: not imported");
            Require(!UnityPackageArchive.ImportedOnDisk(project, new List<PackageAsset>()), "an empty list proves nothing");
            Require(!UnityPackageArchive.ImportedOnDisk(project, null), "an unreadable package proves nothing");
        }
        finally
        {
            try { System.IO.Directory.Delete(project, true); } catch (Exception) { }
        }
    }

    // test1 on 2026-11-04: the base license came with 0 versions (the purchase offers the file), so the list had no base license row,
    // only the purchase row "VModular / VModular Plus 1.0.0". Core-first must find that row, not report the core missing.
    private static void FileCoreFromPurchase()
    {
        var basePurchase = new CatalogEntry { Key = "p:base", Name = "VModular / VModular Plus 1.0.0", Kind = FavoriteKind.Purchase, Purchase = Item(LiveProduct, BaseFile) };
        var plus = LiveLicense("VModular Plus 1.0.0", PlusLicense);
        var tool = LiveLicense("MaskMaker", "maskmaker");
        Func<CatalogEntry, bool> offersPackage = e => false;
        Func<CatalogEntry, bool> offersFile = e => e.IsLicense;
        var entries = new List<CatalogEntry> { tool, basePurchase, plus };
        Require(object.ReferenceEquals(basePurchase, CoreAddons.CoreEntryOf(entries, Vmodular, offersPackage, offersFile)), "purchase row carries the core");
        // A base license row with a file wins over the purchase row (it installs the same file non-interactively by itself).
        var baseLicense = LiveLicense("VModular", BaseLicense);
        entries.Add(baseLicense);
        Require(object.ReferenceEquals(baseLicense, CoreAddons.CoreEntryOf(entries, Vmodular, offersPackage, offersFile)), "license row first");
        // A purchase row without files (past its download window) cannot install anything.
        var expired = new CatalogEntry { Key = "p:old", Kind = FavoriteKind.Purchase, Purchase = new PurchaseItem { product_id = LiveProduct, item_id = "old", files = new PurchaseFile[0] } };
        Require(CoreAddons.CoreEntryOf(new List<CatalogEntry> { expired, plus }, Vmodular, offersPackage, offersFile) == null, "no files, no core entry");
        // The list nests Plus under the purchase row that carries the core.
        var nested = CoreAddons.Nest(new List<ListLine> { new ListLine { Entry = plus }, new ListLine { Entry = tool }, new ListLine { Entry = basePurchase } });
        Equal("MaskMaker|VModular / VModular Plus 1.0.0|VModular Plus 1.0.0", string.Join("|", nested.Select(l => l.Entry.Name)));
        Equal("False|False|True", string.Join("|", nested.Select(l => l.Child.ToString())));
    }

    // The live ids (desk read 2026-11-04): product 7aac2c69, base license 0c873fc3 (VModular_1.0.0), Plus license 39a68f32
    // (VModular_Plus_1.0.0), base file 2a0417aa, Plus file d888196a.
    private const string LiveProduct = "7aac2c69-e72e-46f9-a928-5a9779392165";
    private const string BaseLicense = "0c873fc3-af50-4a2c-987f-cce1b9949185";
    private const string PlusLicense = "39a68f32-437f-48e0-8082-da3b67b3c319";
    private const string BaseFile = "2a0417aa-0f1f-43b8-a7e7-c53824bf4ff6";
    private const string PlusFile = "d888196a-c4ac-4f97-8fff-680effb40f7e";

    private static void FileCoreIds()
    {
        Equal(Vmodular, CoreAddons.CoreOf("", PlusLicense));
        Equal("", CoreAddons.CoreOf("", BaseLicense));
        Equal(Vmodular, CoreAddons.FileCoreOfLicense(BaseLicense));
        Equal("", CoreAddons.FileCoreOfLicense(PlusLicense));
        Equal(Vmodular, CoreAddons.CoreProvidedBy(LiveLicense("VModular", BaseLicense)));
        Equal("", CoreAddons.CoreProvidedBy(LiveLicense("VModular Plus", PlusLicense)));
        // Purchase rows: the base option lists the base file; the Plus option lists both and installs both in order.
        var baseRow = new CatalogEntry { Kind = FavoriteKind.Purchase, Purchase = Item(LiveProduct, BaseFile) };
        var plusRow = new CatalogEntry { Kind = FavoriteKind.Purchase, Purchase = Item(LiveProduct, BaseFile, PlusFile) };
        Equal(Vmodular, CoreAddons.CoreProvidedBy(baseRow));
        Equal(Vmodular, CoreAddons.CoreProvidedBy(plusRow));
        Equal("", CoreAddons.CoreOf(plusRow));                                    // carries its own core: never waits for it
        Equal("", CoreAddons.CoreProvidedBy(new CatalogEntry { Kind = FavoriteKind.Purchase, Purchase = Item("other-product", BaseFile) }));
        Equal(0, OnAirDeckConfig.CoreAddonsPending.Length);
        // Removal: any file of the core counts, not only the marker (review R2: the marker kept as changed, the rest removed).
        Require(CoreAddons.CoreFilesAmong(Vmodular, new[] { "Assets/Editor/VModularEditor/VModularManager.cs" }));
        Require(CoreAddons.CoreFilesAmong(Vmodular, new[] { "Assets\\Editor\\VModularEditor\\VModularManager.cs" }));
        Require(CoreAddons.CoreFilesAmong(Vmodular, new[] { "Assets/VModualr_InAvartar/VModularOneBone.cs" }));
        Require(CoreAddons.CoreFilesAmong(Vmodular, new[] { "Assets/Editor/VModularEditor/VModularMultiBoneEditor.cs" }));
        Require(!CoreAddons.CoreFilesAmong(Vmodular, new[] { "Assets/Editor/VModularEditorPlus/X.cs", "Assets/StudioRaming_Onairdeck/VModular Plus/a.cs" }));
        Require(!CoreAddons.CoreFilesAmong("com.studioraming.mingtoon", new[] { "Assets/Editor/VModularEditor/VModularManager.cs" }));
        // A mixed Plus row queues both packages: only the base one is the core's import (review R3).
        var mixed = new PurchaseItem
        {
            product_id = LiveProduct, item_id = "plus-item",
            files = new[] { new PurchaseFile { file_id = BaseFile, file_name = "VModular_1.0.0.unitypackage" }, new PurchaseFile { file_id = PlusFile, file_name = "VModular_Plus_1.0.0.unitypackage" } }
        };
        var queued = new List<string> { "C:/cache/a/VModular_Plus_1.0.0.unitypackage", "C:/cache/b/VModular_1.0.0.unitypackage" };
        Equal("C:/cache/b/VModular_1.0.0.unitypackage", string.Join("|", CoreAddons.CorePackagesOf(Vmodular, mixed, queued)));
        Equal(0, CoreAddons.CorePackagesOf("", mixed, queued).Count);

        // The queue at the Plus import (review r1 R1, r2 R1, r3 R5): at the add-on's turn a core import queued ahead has run, so the
        // add-on goes only when its core is here now; a core queued behind it would come too late.
        Require(!CoreAddons.AddonImportBlocked(Vmodular, true), "core here");
        Require(CoreAddons.AddonImportBlocked(Vmodular, false), "core failed, missing, never queued or queued behind");
        Require(!CoreAddons.AddonImportBlocked("", false), "not an add-on");
    }

    private static void FileCoreHere()
    {
        var project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "oad-filecore-" + Guid.NewGuid().ToString("N"));
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(project, "Assets", "Editor", "VModularEditor"));
            Require(!CoreAddons.CoreIsHere(project, Vmodular), "empty project");
            System.IO.File.WriteAllText(System.IO.Path.Combine(project, "Assets", "Editor", "VModularEditor", "Other.cs"), "//");
            Require(!CoreAddons.CoreIsHere(project, Vmodular), "an unrelated file in the folder");
            System.IO.File.WriteAllText(System.IO.Path.Combine(project, "Assets", "Editor", "VModularEditor", "VModularManager.cs"), "//");
            Require(CoreAddons.CoreIsHere(project, Vmodular), "the 1.0.0 marker");
            System.IO.File.Delete(System.IO.Path.Combine(project, "Assets", "Editor", "VModularEditor", "VModularManager.cs"));
            var package = System.IO.Path.Combine(project, "Packages", Vmodular);
            System.IO.Directory.CreateDirectory(package);
            System.IO.File.WriteAllText(System.IO.Path.Combine(package, "package.json"), "{\"name\":\"" + Vmodular + "\",\"version\":\"2.0.0\"}");
            Require(CoreAddons.CoreIsHere(project, Vmodular), "the package form (a later option B)");
        }
        finally
        {
            try { System.IO.Directory.Delete(project, true); } catch (Exception) { }
        }
    }

    private static void FileCoreOrderAndNest()
    {
        var plus = LiveLicense("VModular Plus", PlusLicense);
        var tool = LiveLicense("MaskMaker", "maskmaker");
        var baseLicense = LiveLicense("VModular", BaseLicense);
        var ordered = CoreAddons.OrderCoreFirst(new List<CatalogEntry> { plus, tool, baseLicense }, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId);
        Equal("VModular|MaskMaker|VModular Plus", string.Join("|", ordered.Select(e => e.Name)));
        Equal("VModular Plus|MaskMaker|VModular", string.Join("|",
            RemovalPlanner.RemovalOrder(new List<CatalogEntry> { baseLicense, tool, plus }, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId, e => "").Select(e => e.Name)));
        var lines = new List<ListLine>
        {
            new ListLine { Header = true, Title = "all" },
            new ListLine { Entry = plus }, new ListLine { Entry = tool }, new ListLine { Entry = baseLicense }
        };
        var nested = CoreAddons.Nest(lines);
        Equal("all|MaskMaker|VModular|VModular Plus", string.Join("|", nested.Select(l => l.Header ? l.Title : l.Entry.Name)));
        Equal("False|False|False|True", string.Join("|", nested.Select(l => l.Child.ToString())));
    }

    private static CatalogEntry LiveLicense(string name, string licenseId)
    {
        return new CatalogEntry { Key = "l:" + licenseId, Name = name, Kind = FavoriteKind.License, License = new LicenseEntry { license_id = licenseId } };
    }

    private static void Chips()
    {
        Equal(RowChipKind.Installed, ManagerCatalog.RowChipOf(EntryState.Latest));
        Equal(RowChipKind.Installed, ManagerCatalog.RowChipOf(EntryState.Installed));
        Equal(RowChipKind.New, ManagerCatalog.RowChipOf(EntryState.New));
        Equal(RowChipKind.NotGot, ManagerCatalog.RowChipOf(EntryState.None));
        Equal(RowChipKind.Got, ManagerCatalog.RowChipOf(EntryState.Got));
        Equal(RowChipKind.Unknown, ManagerCatalog.RowChipOf(EntryState.Unknown));
        // The state itself: the offered version installed is Latest, so the chip above is what that row shows.
        Equal(EntryState.Latest, ManagerCatalog.StateOfPackage("0.1.16", "0.1.16"));
    }

    private static void InstalledFilter()
    {
        foreach (var state in new[] { EntryState.Latest, EntryState.Installed, EntryState.New })
            Require(ManagerCatalog.Matches(new CatalogEntry { State = state }, "", FilterMode.Installed, "", false), state.ToString());
        Require(!ManagerCatalog.Matches(new CatalogEntry { State = EntryState.None }, "", FilterMode.Installed, "", false));
    }

    private static void BatchRule()
    {
        // A package license: batchable until this project has the offered version.
        Equal(BatchBlock.None, ManagerCatalog.BatchBlockOf(License(EntryState.None, Package("com.studioraming.mingtoon", "0.1.16"))));
        Equal(BatchBlock.None, ManagerCatalog.BatchBlockOf(License(EntryState.New, Package("com.studioraming.mingtoon", "0.1.16"))));
        Equal(BatchBlock.UpToDate, ManagerCatalog.BatchBlockOf(License(EntryState.Latest, Package("com.studioraming.mingtoon", "0.1.16"))));
        Equal(BatchBlock.UpToDate, ManagerCatalog.BatchBlockOf(License(EntryState.Installed, Package("com.studioraming.mingtoon", "0.1.16"))));
        // A .unitypackage license that is no add-on (MaskMaker, EasyMeshEditor): batchable now, like Ming Light Controller already was.
        Equal(BatchBlock.None, ManagerCatalog.BatchBlockOf(License(EntryState.None, File("MaskMaker_1.2.unitypackage"))));
        Equal(BatchBlock.None, ManagerCatalog.BatchBlockOf(License(EntryState.Unknown, File("MaskMaker_1.2.unitypackage"))));
        Equal(BatchBlock.UpToDate, ManagerCatalog.BatchBlockOf(License(EntryState.Latest, File("FaceSdfMaker.unitypackage"))));
        // Nothing offered (kept for its device limit, or installed another way).
        Equal(BatchBlock.NothingToInstall, ManagerCatalog.BatchBlockOf(License(EntryState.Latest)));
        Equal(BatchBlock.NothingToInstall, ManagerCatalog.BatchBlockOf(License(EntryState.None, File("old.unitypackage", false))));
        // Purchases: downloadable is batchable, also once downloaded (the server cannot tell what this project has).
        Equal(BatchBlock.None, ManagerCatalog.BatchBlockOf(Purchase(EntryState.Got, "f1")));
        Equal(BatchBlock.NothingToInstall, ManagerCatalog.BatchBlockOf(new CatalogEntry { Kind = FavoriteKind.Purchase, Purchase = new PurchaseItem { item_id = "i", files = new PurchaseFile[0] } }));
    }

    private static void PurchaseAddonRule()
    {
        var addons = Addons();
        Equal(Vmodular, CoreAddons.CoreOfPurchase(Item(Product, "plus-1"), addons));
        Equal(Vmodular, CoreAddons.CoreOfPurchase(Item(Product, "plus-1", "plus-2"), addons));
        Equal("", CoreAddons.CoreOfPurchase(Item(Product, "base-1"), addons));            // the base product alone
        Equal("", CoreAddons.CoreOfPurchase(Item(Product, "base-1", "plus-1"), addons));  // the base and the option together
        Equal("", CoreAddons.CoreOfPurchase(Item(Product), addons));                      // no files listed
        Equal("", CoreAddons.CoreOfPurchase(new PurchaseItem { product_id = Product, item_id = "i" }, addons));   // files missing (old server)
        Equal("", CoreAddons.CoreOfPurchase(Item("other-product", "plus-1"), addons));    // same file id, another product
        Equal("", CoreAddons.CoreOfPurchase(Item(Product, "plus-1"), new Dictionary<string, PurchaseAddon>()));
        // A license row is never read through the purchase table.
        Equal("", CoreAddons.CoreOf(new CatalogEntry { Kind = FavoriteKind.License, License = new LicenseEntry { license_id = "plus-1" } }));
        Require(CoreAddons.IsCore(Vmodular));
        Equal("VModular", CoreAddons.CoreName(Vmodular));
    }

    private static void PurchaseAddonOrderAndNest()
    {
        var addons = Addons();
        Func<CatalogEntry, string> purchaseCore = e => e.IsLicense ? "" : CoreAddons.CoreOfPurchase(e.Purchase, addons);
        var plus = Purchase(EntryState.None, "plus-1");
        plus.Name = "VModular Plus";
        var basePurchase = Purchase(EntryState.None, "base-1");
        basePurchase.Name = "VModular base files";
        var core = License(EntryState.None, Package(Vmodular, "2.0.0"));
        core.Name = "VModular";
        core.PackageName = Vmodular;
        var tool = License(EntryState.None, File("MaskMaker.unitypackage"));
        tool.Name = "MaskMaker";
        var picked = new List<CatalogEntry> { plus, tool, basePurchase, core };
        var ordered = CoreAddons.OrderCoreFirst(picked, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId, purchaseCore);
        Equal("VModular|MaskMaker|VModular base files|VModular Plus", string.Join("|", ordered.Select(e => e.Name)));
        // The two-argument order (MingToon) is unchanged by the new hook.
        Equal("MaskMaker|VModular Plus|VModular base files", string.Join("|",
            CoreAddons.OrderCoreFirst(new List<CatalogEntry> { tool, plus, basePurchase }, e => e.IsLicense ? e.PackageName : "", e => e.LicenseId).Select(e => e.Name)));
        Equal(Vmodular, purchaseCore(plus));
        Equal("", purchaseCore(basePurchase));
    }

    // ---------- fixtures (synthetic ids) ----------

    private static Dictionary<string, PurchaseAddon> Addons()
    {
        return new Dictionary<string, PurchaseAddon> { { Product, new PurchaseAddon { Core = Vmodular, FileIds = new[] { "plus-1", "plus-2" } } } };
    }

    private static PurchaseItem Item(string productId, params string[] fileIds)
    {
        return new PurchaseItem { product_id = productId, item_id = "item-" + productId, files = fileIds.Select(id => new PurchaseFile { file_id = id, file_name = id + ".unitypackage" }).ToArray() };
    }

    private static CatalogEntry Purchase(EntryState state, params string[] fileIds)
    {
        return new CatalogEntry { Key = "p:" + string.Join("+", fileIds), Kind = FavoriteKind.Purchase, Purchase = Item(Product, fileIds), State = state };
    }

    private static LicenseVersionEntry Package(string name, string version)
    {
        return new LicenseVersionEntry { version_id = name + "@" + version, package_name = name, package_version = version, file_name = name + "-" + version + ".zip" };
    }

    private static LicenseVersionEntry File(string fileName, bool offered = true)
    {
        return offered ? new LicenseVersionEntry { version_id = "v-" + fileName, file_name = fileName, version_label = "1.0" } : null;
    }

    private static CatalogEntry License(EntryState state, LicenseVersionEntry offered = null)
    {
        var entry = new CatalogEntry { Key = "l:" + Guid.NewGuid().ToString("N"), Kind = FavoriteKind.License, License = new LicenseEntry { license_id = "lic" }, State = state };
        if (offered != null) entry.Versions.Add(new VersionRow { Label = offered.version_label ?? offered.package_version, License = offered, Latest = true });
        return entry;
    }

    private static void Require(bool condition, string detail = "") { if (!condition) throw new Exception("Assertion failed " + detail); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
