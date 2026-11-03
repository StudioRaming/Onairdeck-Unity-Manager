using System;
using System.Collections.Generic;
using System.Linq;
using OnAirDeck.UnityManager;

// Owner report 2026-11-03: one product bought in several options could not be told apart, and rows read as storage file names.
// The Unity list kept only the first line item of a product (the other options were not listed at all). Real sources: FavoritesLogic
// (rows and keys), ManagerCatalog (labels, search), PurchaseWatcherLogic (new-purchase keys). Synthetic data only. The window half
// (the option line under the detail title, the batch names) is compile-checked only.
internal static class PurchaseOptionChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("purchase options: every line item of a product is a row; the oldest keeps p:<product>, later ones p:<product>/<item>; the watcher uses the same keys", RowsAndKeys);
        check("purchase options: each row of a product bought more than once names its option (server label, else its file names), a product bought once gets none", Labels);
        check("purchase options: rows whose labels still read the same get their purchase date, then a number; the search finds a row by its option", SameLabelAndSearch);
        check("file variant: no storage prefix, no product name, '_' as a space; a (URP) part wins; a name that is only the product stays readable", FileVariants);
    }

    private static PurchaseItem Item(string product, string item, string at, string label, params string[] files)
    {
        return new PurchaseItem
        {
            name = "Sample Resort", product_id = product, item_id = item, purchased_at = at, date = at.Substring(0, 10), option_label = label,
            files = files.Select((f, i) => new PurchaseFile { file_id = item + "-" + i, file_name = f }).ToArray(),
        };
    }

    // Newest first, as the server sends them.
    private static PurchaseItem[] Bought()
    {
        return new[]
        {
            Item("P", "i3", "2026-10-28T03:00:00Z", "PRO / Day", "sr-66f3b3c3-SampleResort_PRO_Day_.unitypackage"),
            Item("P", "i2", "2026-10-20T03:00:00Z", "PRO / Night", "sr-a79261c3-SampleResort_PRO_Night_.unitypackage"),
            Item("P", "i1", "2026-10-01T03:00:00Z", "", "sr-0b1c2d3e-SampleResort_Basic_.unitypackage"),
            new PurchaseItem { name = "Lamp", product_id = "Q", item_id = "j1", purchased_at = "2026-10-02T00:00:00Z", files = new[] { new PurchaseFile { file_id = "q", file_name = "Lamp(URP).unitypackage" } } },
            // The same line item twice, and an item without an id next to its product: neither can be told apart, so no extra row.
            Item("P", "i2", "2026-10-20T03:00:00Z", "PRO / Night", "x.unitypackage"),
            Item("P", "", "2026-09-01T03:00:00Z", "", "y.unitypackage"),
        };
    }

    private static void RowsAndKeys()
    {
        var entries = ManagerCatalog.Build(Bought(), new LicenseEntry[0], n => "", new HashSet<string>());
        Equal("p:P/i3,p:P/i2,p:P,p:Q", string.Join(",", entries.Select(e => e.Key)));
        Equal("i1", entries[2].Purchase.item_id);
        var watched = PurchaseWatcherLogic.Entries(new PurchasesResponse { purchases = Bought(), licenses = new LicenseEntry[0] });
        Equal(string.Join(",", entries.Select(e => e.Key)), string.Join(",", watched.Select(w => w.Key)));
        // Buying one more option adds a key and leaves every earlier one as it was.
        var more = new List<PurchaseItem> { Item("P", "i4", "2026-11-01T03:00:00Z", "Set", "z.unitypackage") };
        more.AddRange(Bought());
        Equal("p:P/i4,p:P/i3,p:P/i2,p:P,p:Q", string.Join(",", ManagerCatalog.Build(more.ToArray(), new LicenseEntry[0], n => "", new HashSet<string>()).Select(e => e.Key)));
    }

    private static void Labels()
    {
        var entries = ManagerCatalog.Build(Bought(), new LicenseEntry[0], n => "", new HashSet<string>());
        Equal("PRO / Day|PRO / Night|Basic|", string.Join("|", entries.Select(e => e.Variant)));
        Equal("Sample Resort · PRO / Day", entries[0].DisplayName);
        Equal("Lamp", entries[3].DisplayName);
        foreach (var e in entries) Require(!e.DisplayName.Contains("sr-") && !e.DisplayName.Contains("_"), e.DisplayName);
    }

    private static void SameLabelAndSearch()
    {
        var twice = new[]
        {
            Item("P", "i2", "2026-10-20T03:00:00Z", "PRO", "a.unitypackage"),
            Item("P", "i1", "2026-10-01T03:00:00Z", "PRO", "b.unitypackage"),
        };
        var entries = ManagerCatalog.Build(twice, new LicenseEntry[0], n => "", new HashSet<string>());
        Require(entries[0].BoughtDate.Length > 0 && entries[0].BoughtDate != entries[1].BoughtDate);
        Equal("PRO · " + entries[0].BoughtDate, entries[0].Variant);
        Equal("PRO · " + entries[1].BoughtDate, entries[1].Variant);
        // "..._PRO_<night in Japanese>" and "..._PRO_<day in Japanese>" both reach storage as "..._PRO__": one label, bought the same day, so a number.
        var sameDay = new[]
        {
            Item("P", "i2", "2026-10-28T03:00:00Z", "", "sr-a79261c3-SampleResort_PRO__.unitypackage"),
            Item("P", "i1", "2026-10-28T03:00:00Z", "", "sr-b9a1c2d3-SampleResort_PRO___.unitypackage"),
        };
        var numbered = ManagerCatalog.Build(sameDay, new LicenseEntry[0], n => "", new HashSet<string>());
        Equal("PRO \u00B7 " + numbered[0].BoughtDate + " \u00B7 1", numbered[0].Variant);
        Equal("PRO \u00B7 " + numbered[1].BoughtDate + " \u00B7 2", numbered[1].Variant);
        var options = ManagerCatalog.Build(Bought(), new LicenseEntry[0], n => "", new HashSet<string>());
        Equal("p:P/i2", string.Join(",", options.Where(e => ManagerCatalog.Matches(e, "night", FilterMode.All, "", false)).Select(e => e.Key)));
    }

    private static void FileVariants()
    {
        Equal("PRO Night", ManagerCatalog.FileVariant("sr-a79261c3-SampleResort_PRO_Night_.unitypackage", "Sample Resort"));
        Equal("PRO Day", ManagerCatalog.FileVariant("sr-66F3B3C3-SampleResort_PRO_Day_.zip", "Sample Resort"));
        Equal("URP", ManagerCatalog.FileVariant("sr-a79261c3-Lamp(URP).unitypackage", "Lamp"));
        Equal("SampleResorts Night", ManagerCatalog.FileVariant("SampleResorts_Night.zip", "Sample Resort"));
        Equal("SampleResort", ManagerCatalog.FileVariant("sr-a79261c3-SampleResort.zip", "Sample Resort"));
        Equal("sr-day-Night", ManagerCatalog.FileVariant("sr-day-Night.zip", ""));
    }

    private static void Require(bool condition, string detail = "") { if (!condition) throw new Exception("Assertion failed " + detail); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
