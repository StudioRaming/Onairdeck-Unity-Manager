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
        Require(CoreAddons.MarkerAmong(Vmodular, new[] { "Assets/Editor/VModularEditor/VModularManager.cs" }));
        Require(CoreAddons.MarkerAmong(Vmodular, new[] { "Assets\\Editor\\VModularEditor\\VModularManager.cs" }));
        Require(!CoreAddons.MarkerAmong(Vmodular, new[] { "Assets/VModualr_InAvartar/VModularOneBone.cs" }));
        Require(!CoreAddons.MarkerAmong("com.studioraming.mingtoon", new[] { "Assets/Editor/VModularEditor/VModularManager.cs" }));
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
