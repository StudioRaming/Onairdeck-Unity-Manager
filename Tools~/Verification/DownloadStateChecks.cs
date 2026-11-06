using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.UnityManager;

// Real QA of 0.1.7 (2026-11-06): F5 a license installed and removed read "to get" again although its download had ended the refund;
// F6 a purchase downloaded elsewhere offered "remove" in a project without it and then showed a wrong install failure; F7 the detail's
// install of a never-downloaded license had no refund notice. Real sources: ManagerCatalog (chip, filter, notice, remove rule),
// DownloadedLicensesLogic, RemovalPlanner.FilesHere, ImportRequestLogic. The dialogs and EditorPrefs are compile-checked only.
internal static class DownloadStateChecks
{
    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "download-state");
        Directory.CreateDirectory(_root);
        check("license downloaded (F5): not in this project, it reads 'downloaded' once the server says so or this PC recorded one of its versions, else 'to get'; the 'to get' filter follows the chip", LicenseChip);
        check("license downloaded (F5): a merged row is downloaded when a license it stands for is; an installed row keeps 'installed'", MergedAndInstalled);
        check("downloaded record (F5): version ids once each, newest last, capped; no user, no key", Record);
        check("refund notice (F7): a never-downloaded purchase or a license neither downloaded nor here; never the seller's own; core-first carries it for a license core or add-on", RefundNotice);
        check("remove offered (F6): an installed license, or a downloaded purchase with files in this project (its record's files, else its product folder)", RemoveOffered);
    }

    private static LicenseEntry MaskMaker(bool downloaded, string source = "grant")
    {
        return new LicenseEntry
        {
            license_id = "lic-mm", name = "MaskMaker", source = source, downloaded = downloaded,
            versions = new[] { new LicenseVersionEntry { version_id = "v-mm-210", version_label = "2.1.0", file_name = "MaskMaker.unitypackage", released_at = "2026-11-05T00:00:00Z" } },
        };
    }

    private static CatalogEntry One(LicenseEntry license, ICollection<string> recorded, ISet<string> markers = null, IDictionary<string, InstallRecord> records = null)
    {
        var list = ManagerCatalog.Build(null, new[] { license }, n => "", markers ?? new HashSet<string>(), records, recorded);
        Equal(1, list.Count);
        return list[0];
    }

    private static void LicenseChip()
    {
        var never = One(MaskMaker(false), null);
        Equal(EntryState.None, never.State);
        Require(!never.Downloaded, "never downloaded");
        Equal(RowChipKind.NotGot, ManagerCatalog.RowChipOf(never));
        Require(ManagerCatalog.Matches(never, "", FilterMode.None, "", false), "'to get' lists it");

        // The server's flag (once plugin-purchases sends it), after an install and a removal: still not here, but downloaded.
        var server = One(MaskMaker(true), null);
        Equal(EntryState.None, server.State);
        Require(server.Downloaded, "server flag");
        Equal(RowChipKind.Got, ManagerCatalog.RowChipOf(server));
        Require(!ManagerCatalog.Matches(server, "", FilterMode.None, "", false), "'to get' leaves it out");
        Require(!ManagerCatalog.Matches(server, "", FilterMode.Installed, "", false), "not installed here");
        Require(ManagerCatalog.Matches(server, "", FilterMode.All, "", false), "listed");

        // This PC's record of the version download (an older server without the flag); the id compares without case.
        var local = One(MaskMaker(false), new[] { "V-MM-210" });
        Require(local.Downloaded, "local record");
        Equal(RowChipKind.Got, ManagerCatalog.RowChipOf(local));
        var other = One(MaskMaker(false), new[] { "v-another-product" });
        Require(!other.Downloaded, "another version id");
        Equal(RowChipKind.NotGot, ManagerCatalog.RowChipOf(other));

        // A purchase keeps its server-driven chip.
        var bought = ManagerCatalog.Build(new[] { new PurchaseItem { name = "VModular", product_id = "p-v", item_id = "i-v", files = new[] { new PurchaseFile() }, downloaded = true } },
            null, n => "", new HashSet<string>());
        Equal(RowChipKind.Got, ManagerCatalog.RowChipOf(bought[0]));
        Require(bought[0].Downloaded, "purchase flag");
    }

    private static void MergedAndInstalled()
    {
        // Two licenses of one package are one row (the stronger one); a version download through the folded one counts for the row.
        Func<string, int, string, LicenseEntry> pkg = (id, rights, versionId) => new LicenseEntry
        {
            license_id = id, name = "Tool " + id, source = "grant", rights = new string[rights],
            versions = new[] { new LicenseVersionEntry { version_id = versionId, version_label = "1.0.0", file_name = "com.seller.tool-1.0.0.zip", package_name = "com.seller.tool", package_version = "1.0.0" } },
        };
        var rows = ManagerCatalog.Build(null, new[] { pkg("lic-a", 2, "v-a"), pkg("lic-b", 1, "v-b") }, n => "", new HashSet<string>(), null, new[] { "v-b" });
        Equal(1, rows.Count);
        Equal("lic-a", rows[0].LicenseId);
        Require(rows[0].AlsoLicenseIds.Contains("lic-b"), "merged");
        Require(rows[0].Downloaded, "folded license downloaded");

        // Installed here (marker and install record): the state's chip, not "downloaded".
        var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase)
        {
            { "lic-mm", new InstallRecord { LicenseId = "lic-mm", VersionId = "v-mm-210", Label = "2.1.0", ReleasedAt = "2026-11-05T00:00:00Z" } },
        };
        var here = One(MaskMaker(true), null, new HashSet<string> { "lic-mm" }, records);
        Equal(EntryState.Latest, here.State);
        Equal(RowChipKind.Installed, ManagerCatalog.RowChipOf(here));
    }

    private static void Record()
    {
        Equal(null, DownloadedLicensesLogic.PrefsKey(null));
        Equal(null, DownloadedLicensesLogic.PrefsKey(""));
        Equal(DownloadedLicensesLogic.PrefsPrefix + "user-1", DownloadedLicensesLogic.PrefsKey("user-1"));
        Equal("a", DownloadedLicensesLogic.Add("", "a"));
        Equal("b\nA", DownloadedLicensesLogic.Add("a\nb", "A"));
        Equal("a\nb", DownloadedLicensesLogic.Add("a\nb", " "));
        Equal("a|b", string.Join("|", DownloadedLicensesLogic.Parse(" a \n\nb\na\r\n").ToArray()));
        var raw = "";
        for (var i = 0; i <= DownloadedLicensesLogic.MaxEntries; i++) raw = DownloadedLicensesLogic.Add(raw, "v" + i);
        var kept = DownloadedLicensesLogic.Parse(raw);
        Equal(DownloadedLicensesLogic.MaxEntries, kept.Count);
        Equal("v1", kept[0]);
        Equal("v" + DownloadedLicensesLogic.MaxEntries, kept[kept.Count - 1]);
    }

    private static void RefundNotice()
    {
        // The QA case: the detail's install of MaskMaker 2.1.0, never downloaded.
        Require(ManagerCatalog.NeedsRefundNotice(One(MaskMaker(false), null)), "never downloaded license");
        Require(!ManagerCatalog.NeedsRefundNotice(One(MaskMaker(true), null)), "server says downloaded");
        Require(!ManagerCatalog.NeedsRefundNotice(One(MaskMaker(false), new[] { "v-mm-210" })), "this PC downloaded it");
        Require(!ManagerCatalog.NeedsRefundNotice(One(MaskMaker(false, "own"), null)), "the seller's own license");
        var records = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase)
        {
            { "lic-mm", new InstallRecord { LicenseId = "lic-mm", VersionId = "v-mm-210", Label = "2.1.0", ReleasedAt = "2026-11-05T00:00:00Z" } },
        };
        Require(!ManagerCatalog.NeedsRefundNotice(One(MaskMaker(false), null, new HashSet<string> { "lic-mm" }, records)), "installed here");

        Func<bool, string, CatalogEntry> purchase = (downloaded, source) => ManagerCatalog.Build(
            new[] { new PurchaseItem { name = "VModular", product_id = "p-v", item_id = "i-v", files = new[] { new PurchaseFile() }, downloaded = downloaded, source = source } },
            null, n => "", new HashSet<string>())[0];
        Require(ManagerCatalog.NeedsRefundNotice(purchase(false, "purchase")), "purchase never downloaded");
        Require(!ManagerCatalog.NeedsRefundNotice(purchase(true, "purchase")), "purchase downloaded");
        Require(!ManagerCatalog.NeedsRefundNotice(purchase(false, "own")), "own product");

        // Core first: the question is the only confirm of both installs.
        var newAddon = One(MaskMaker(false), null);
        var gotAddon = One(MaskMaker(true), null);
        var newCore = One(new LicenseEntry { license_id = "lic-core", name = "Core", source = "grant", versions = MaskMaker(false).versions }, null);
        Require(ManagerCatalog.CoreFirstNeedsRefundNotice(newAddon, null), "add-on first download");
        Require(ManagerCatalog.CoreFirstNeedsRefundNotice(gotAddon, newCore), "core first download");
        Require(!ManagerCatalog.CoreFirstNeedsRefundNotice(gotAddon, null), "nothing new");
        // A purchase add-on or core asks its own refund question in its download: not again here.
        Require(!ManagerCatalog.CoreFirstNeedsRefundNotice(purchase(false, "purchase"), purchase(false, "purchase")), "purchases ask in their download");
    }

    private static void RemoveOffered()
    {
        var license = One(MaskMaker(true), null);
        Require(!ManagerCatalog.OffersRemove(license), "license not here");
        license.State = EntryState.Latest;
        Require(ManagerCatalog.OffersRemove(license), "license here");

        var item = new PurchaseItem { name = "VModular", product_id = "p-v", item_id = "i-v", files = new[] { new PurchaseFile() }, downloaded = true };
        var row = ManagerCatalog.Build(new[] { item }, null, n => "", new HashSet<string>())[0];
        Require(row.InProject, "true until the window looked");
        Require(ManagerCatalog.OffersRemove(row), "downloaded purchase, not checked yet");
        row.InProject = false;
        Require(!ManagerCatalog.OffersRemove(row), "downloaded elsewhere, nothing here (the QA case)");
        var never = ManagerCatalog.Build(new[] { new PurchaseItem { name = "X", product_id = "p-x", item_id = "i-x", files = new[] { new PurchaseFile() } } }, null, n => "", new HashSet<string>())[0];
        Require(!ManagerCatalog.OffersRemove(never), "never downloaded");

        // What the window feeds into InProject.
        var project = Path.Combine(_root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        var keys = new List<string> { PlacedFiles.PurchaseKey(item) };
        const string folder = "Assets/StudioRaming_Onairdeck/VModular";
        Require(!RemovalPlanner.FilesHere(project, keys, new PlacedRecords(), folder), "no record, no folder");
        var placed = new PlacedRecords();
        var entry = new PlacedEntry();
        entry.Paths.Add("Assets/VModular/Editor/VModular.cs");
        placed.Products[keys[0]] = entry;
        Require(!RemovalPlanner.FilesHere(project, keys, placed, folder), "record whose files are gone");
        Put(project, "Assets/VModular/Editor/VModular.cs", "code");
        Require(RemovalPlanner.FilesHere(project, keys, placed, folder), "a recorded file is here");
        Require(!RemovalPlanner.FilesHere(project, new List<string> { "purchase:other" }, placed, folder), "another product's record");
        Directory.CreateDirectory(RemovalPlanner.Absolute(project, folder));
        Require(RemovalPlanner.FilesHere(project, keys, new PlacedRecords(), folder), "no record, the product folder is here (removal points at it)");
    }

    private static void Put(string project, string relative, string text)
    {
        var path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static void Require(bool condition, string detail = "") { if (!condition) throw new Exception("Assertion failed " + detail); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
