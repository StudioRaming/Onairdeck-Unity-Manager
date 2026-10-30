using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// Favorites, thumbnail cache rules, update-check rules and the batch queue. The real FavoritesLogic,
// ThumbnailCachePolicy, UpdateCheckLogic and BatchQueue sources are compiled in; the queue runs fake actions.
// EditorPrefs, UnityWebRequest and Texture2D (the Unity halves of these services) are not exercised here.
internal static class CatalogChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("favorites: key per user, entry key per kind", FavoriteKeys);
        check("favorites: toggle, parse and stable text form", FavoriteToggle);
        check("thumbnails: only absolute https URLs without credentials", ThumbUrls);
        check("thumbnails: file name is a SHA-256 vector", ThumbFileName);
        check("thumbnails: eviction is oldest first and stops at the cap", ThumbEviction);
        check("update check: throttle (session, 6 hours, signed in, clock moved back)", Throttle);
        check("update check: purchases and installed packages with a newer version", FindUpdates);
        check("versions: a release is offered before a preview or beta unless a prerelease is installed; hotfix and kind chips", OfferedVersion);
        check("batch: order, progress text, results", BatchOrder);
        check("batch: cancel stops after the current item", BatchCancel);
        check("batch: a failed item does not stop the rest; a rejected session does", BatchFailures);
        check("batch: device limit pauses, retry resumes the same item", BatchPauseResume);
        check("batch: cancel while paused skips the rest and clears the notice", BatchCancelPaused);
        check("v3: every request carries X-Atelier-Manager: 1", ManagerHeader);
        check("v3: plugin-purchases body with description, product_url, versions[] parses; older body keeps defaults", ParseV3Purchases);
        check("file versions: the install record decides new / latest / installed; an installed product without a record is unknown, not latest", FileVersionState);
        check("install record: atomic file round trip; a malformed file reads as none; a record needs license and version ids", InstallRecordFile);
        check("install record: a .unitypackage batch is recorded only after all its imports finished; a failed import drops it", PendingImportRecord);
        check("dates: the server's UTC time is shown as a date in the computer's time zone", LocalDates);
        check("purchases: a line item with an empty files list (a WARUDO product) is not listed and not a new entry, even when downloaded; a missing list keeps it", NonUnityPurchases);
    }

    // ---------- purchases without Unity files ----------

    private static void NonUnityPurchases()
    {
        var unity = new PurchaseItem { name = "Unity tool", product_id = "p1", item_id = "i1", files = new[] { new PurchaseFile { file_id = "f1" } } };
        // downloaded is shared with the WARUDO app, so a WARUDO item downloaded there stays hidden too.
        var warudo = new PurchaseItem { name = "[WARUDO] Color Studio", product_id = "p2", item_id = "i2", files = new PurchaseFile[0], downloaded = true };
        // The old purchase row of a product that now ships through its license: hidden, so only the license row is left.
        var moved = new PurchaseItem { name = "Licensed tool (old purchase)", product_id = "p3", item_id = "i3", files = new PurchaseFile[0], downloaded = true };
        // A body without the field (JsonUtility leaves null) is unknown: keep the item.
        var unknown = new PurchaseItem { name = "No files field", product_id = "p4", item_id = "i4", files = null };
        var license = new LicenseEntry { license_id = "L1", name = "Licensed tool", versions = new[] { FileVer("v1", "1.0.0", "2026-10-29T00:00:00Z") } };
        var entries = ManagerCatalog.Build(new[] { unity, warudo, moved, unknown }, new[] { license }, n => "", new HashSet<string>());
        Equal(3, entries.Count);
        Equal("Unity tool", entries[0].Name);
        Equal("No files field", entries[1].Name);
        Equal("Licensed tool", entries[2].Name);
        Require(!unknown.CanDownload);
        // The watcher would otherwise announce a WARUDO purchase as new in Unity.
        var watched = PurchaseWatcherLogic.Entries(new PurchasesResponse { purchases = new[] { unity, warudo, moved, unknown }, licenses = new[] { license } });
        Equal(3, watched.Count);
        Equal("p:p1", watched[0].Key);
        Equal("p:p4", watched[1].Key);
        Equal("l:L1", watched[2].Key);
    }

    // ---------- install records (non-package license versions) ----------

    private static LicenseVersionEntry FileVer(string id, string label, string at)
    {
        return new LicenseVersionEntry { version_id = id, version_label = label, file_name = "Product.unitypackage", released_at = at };
    }

    private static Dictionary<string, InstallRecord> Records(params InstallRecord[] records)
    {
        var map = new Dictionary<string, InstallRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in records) map[r.LicenseId] = r;
        return map;
    }

    private static void FileVersionState()
    {
        var license = new LicenseEntry { license_id = "L3", name = "Product", versions = new[] {
            FileVer("f1", "2.0.0", "2026-10-01T00:00:00Z"), FileVer("f2", "2.0.1", "2026-10-20T00:00:00Z") } };
        var marked = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "l3" };
        Func<Dictionary<string, InstallRecord>, ISet<string>, CatalogEntry> build = (records, markers) =>
            ManagerCatalog.Build(null, new[] { license }, n => "", markers, records)[0];

        // Installed (marker) but never through this Manager: unknown, not a false "latest".
        var entry = build(null, marked);
        Equal(EntryState.Unknown, entry.State);
        Equal("2.0.1", entry.LatestVersion);
        Require(!entry.Versions[0].Installed && !entry.Versions[1].Installed);

        // The record names the older version: a new version, and the installed row is marked (rows are newest first).
        entry = build(Records(new InstallRecord { LicenseId = "L3", VersionId = "F1", Label = "2.0.0", ReleasedAt = "2026-10-01T00:00:00Z" }), marked);
        Equal(EntryState.New, entry.State);
        Equal("2.0.0", entry.InstalledVersion);
        Require(!entry.Versions[0].Installed && entry.Versions[1].Installed);

        // The record names the offered version: latest.
        entry = build(Records(new InstallRecord { LicenseId = "l3", VersionId = "f2", Label = "2.0.1", ReleasedAt = "2026-10-20T00:00:00Z" }), marked);
        Equal(EntryState.Latest, entry.State);
        Require(entry.Versions[0].Installed);

        // A version no longer listed: newer than the offered one is installed, older is a new version.
        entry = build(Records(new InstallRecord { LicenseId = "L3", VersionId = "f9", Label = "2.1.0 beta", ReleasedAt = "2026-10-25T00:00:00Z" }), marked);
        Equal(EntryState.Installed, entry.State);
        Equal("2.1.0 beta", entry.InstalledVersion);
        entry = build(Records(new InstallRecord { LicenseId = "L3", VersionId = "f0", Label = "1.0.0", ReleasedAt = "2026-09-01T00:00:00Z" }), marked);
        Equal(EntryState.New, entry.State);

        // A record without a readable release time and not in the list: unknown, never a guessed "update" to an older file.
        entry = build(Records(new InstallRecord { LicenseId = "L3", VersionId = "f0", Label = "?", ReleasedAt = "" }), marked);
        Equal(EntryState.Unknown, entry.State);

        // A listed record is placed by its row, so a version without a release time is still known (no "unknown" next to its
        // installed row): the older row is a new version.
        var undated = new LicenseEntry { license_id = "L6", name = "Undated", versions = new[] {
            FileVer("r2", "2.0.1", "2026-10-20T00:00:00Z"), FileVer("u1", "1.9.0", "") } };
        entry = ManagerCatalog.Build(null, new[] { undated }, n => "", new HashSet<string> { "L6" },
            Records(new InstallRecord { LicenseId = "L6", VersionId = "u1", Label = "1.9.0", ReleasedAt = "" }))[0];
        Equal(EntryState.New, entry.State);
        Require(entry.Versions[1].Installed && !entry.Versions[0].Installed);

        // The order follows the parsed time, not the text: a fraction or another offset does not make a version look newer.
        var mixed = new LicenseEntry { license_id = "L7", name = "Mixed", versions = new[] {
            FileVer("old", "1.0.0", "2026-10-30T08:00:00+09:00"), FileVer("new", "1.0.1", "2026-10-29T23:30:00.5+00:00") } };
        entry = ManagerCatalog.Build(null, new[] { mixed }, n => "", new HashSet<string> { "L7" },
            Records(new InstallRecord { LicenseId = "L7", VersionId = "old", Label = "1.0.0" }))[0];
        Equal("1.0.1", entry.LatestVersion);
        Equal(EntryState.New, entry.State);

        // A project on a beta file is offered the newest of all (as packages are), so it is not steered back to the release.
        var withBeta = new LicenseEntry { license_id = "L5", name = "Beta", versions = new[] {
            FileVer("r1", "2.0.1", "2026-10-20T00:00:00Z"), FileVer("b1", "2.1.0 beta", "2026-10-25T00:00:00Z") } };
        var onBeta = Records(new InstallRecord { LicenseId = "L5", VersionId = "b1", Label = "2.1.0 beta", ReleasedAt = "2026-10-25T00:00:00Z" });
        entry = ManagerCatalog.Build(null, new[] { withBeta }, n => "", new HashSet<string> { "L5" }, onBeta)[0];
        Equal("2.1.0 beta", entry.LatestVersion);
        Equal(EntryState.Latest, entry.State);
        // On the release, the beta is not offered and the release is up to date.
        var onRelease = Records(new InstallRecord { LicenseId = "L5", VersionId = "r1", Label = "2.0.1", ReleasedAt = "2026-10-20T00:00:00Z" });
        entry = ManagerCatalog.Build(null, new[] { withBeta }, n => "", new HashSet<string> { "L5" }, onRelease)[0];
        Equal("2.0.1", entry.LatestVersion);
        Equal(EntryState.Latest, entry.State);

        // No marker: the product is not in this project, whatever the record says.
        entry = build(Records(new InstallRecord { LicenseId = "L3", VersionId = "f2", ReleasedAt = "2026-10-20T00:00:00Z" }), new HashSet<string>());
        Equal(EntryState.None, entry.State);

        // Nothing to install (no file versions): an installed product stays latest, as before.
        var bare = new LicenseEntry { license_id = "L4", name = "Bare", max_devices = 2, versions = new LicenseVersionEntry[0] };
        entry = ManagerCatalog.Build(null, new[] { bare }, n => "", new HashSet<string> { "L4" }, null)[0];
        Equal(EntryState.Latest, entry.State);
    }

    private static void InstallRecordFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "atelier-records-" + Guid.NewGuid().ToString("N"));
        try
        {
            Equal(0, InstallRecords.Load(root).Count);
            InstallRecords.Save(root, new InstallRecord { LicenseId = "L1", VersionId = "v1", Label = "1.0.0", ReleasedAt = "2026-10-01T00:00:00Z" });
            InstallRecords.Save(root, new InstallRecord { LicenseId = "L2", VersionId = "v2", Label = "a \"quoted\" label", ReleasedAt = "" });
            InstallRecords.Save(root, new InstallRecord { LicenseId = "l1", VersionId = "v3", Label = "1.1.0", ReleasedAt = "2026-10-20T00:00:00Z" });
            var records = InstallRecords.Load(root);
            Equal(2, records.Count);
            Equal("v3", records["L1"].VersionId);
            Equal("a \"quoted\" label", records["l2"].Label);
            var path = InstallRecords.PathFor(root);
            Require(path.EndsWith(Path.Combine("ProjectSettings", "OnAirDeck", "installed-versions.json"), StringComparison.Ordinal));
            Equal(0, Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp").Length);

            Require(!File.Exists(path + ".prev"));   // a normal save leaves no copy in the project
            File.WriteAllText(path, "{ not json");
            Equal(0, InstallRecords.Load(root).Count);
            // Saving over an unreadable file keeps its last copy next to it.
            InstallRecords.Save(root, new InstallRecord { LicenseId = "L9", VersionId = "v9" });
            Equal("{ not json", File.ReadAllText(path + ".prev"));
            Equal(1, InstallRecords.Load(root).Count);
            File.WriteAllText(path, "{\"licenses\":[{\"license_id\":\"L1\"},{\"license_id\":\"L2\",\"version_id\":\"v2\"}]}");
            Equal(1, InstallRecords.Load(root).Count);

            Throws(() => InstallRecords.Save(root, new InstallRecord { LicenseId = "L1", VersionId = "" }));
            Throws(() => InstallRecords.Save(root, new InstallRecord { LicenseId = "", VersionId = "v1" }));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (Exception) { }
        }
    }

    private static void PendingImportRecord()
    {
        var record = new InstallRecord { LicenseId = "L1", VersionId = "v2", Label = "2.0.0", ReleasedAt = "2026-10-20T00:00:00Z" };
        InstallRecord ready;

        // Two packages in one install: recorded after the second one.
        var state = PendingImportRecords.Add("", new[] { "C:/cache/a.unitypackage", "C:/cache/b.unitypackage" }, "b1", record);
        state = PendingImportRecords.Finish(state, "C:/cache/a.unitypackage", true, out ready);
        Equal(null, ready);
        state = PendingImportRecords.Finish(state, "c:/CACHE/b.unitypackage", true, out ready);
        Require(ready != null && ready.VersionId == "v2" && ready.Label == "2.0.0" && ready.ReleasedAt == record.ReleasedAt);
        Equal("", state);

        // One import fails: the batch is dropped, the other package finishing later records nothing.
        state = PendingImportRecords.Add("", new[] { "C:/cache/c.unitypackage", "C:/cache/d.unitypackage" }, "b2", record);
        state = PendingImportRecords.Finish(state, "C:/cache/c.unitypackage", false, out ready);
        Equal(null, ready);
        state = PendingImportRecords.Finish(state, "C:/cache/d.unitypackage", true, out ready);
        Equal(null, ready);

        // A package without a record (a purchase download) leaves other batches alone.
        state = PendingImportRecords.Add("", new[] { "C:/cache/e.unitypackage" }, "b3", record);
        var unchanged = PendingImportRecords.Finish(state, "C:/cache/other.unitypackage", true, out ready);
        Equal(null, ready);
        Equal(state, unchanged);
        PendingImportRecords.Finish(state, "C:/cache/e.unitypackage", true, out ready);
        Require(ready != null);
    }

    private static void LocalDates()
    {
        var kst = TimeZoneInfo.CreateCustomTimeZone("Test KST", TimeSpan.FromHours(9), "Test KST", "Test KST");
        Equal("2026-10-30", ManagerCatalog.FormatDate("2026-10-29T23:30:00Z", kst));
        Equal("2026-10-29", ManagerCatalog.FormatDate("2026-10-29T23:30:00Z", TimeZoneInfo.Utc));
        Equal("2026-10-30", ManagerCatalog.FormatDate("2026-10-29T23:30:00+00:00", kst));
        Equal("", ManagerCatalog.FormatDate("", kst));
        Equal("not a date", ManagerCatalog.FormatDate("not a date", kst));
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new Exception("Expected an exception");
    }

    // ---------- favorites ----------

    private static void FavoriteKeys()
    {
        Equal("OnAirDeck.UnityManager.Favorites.u1", FavoritesLogic.PrefsKey("u1"));
        Equal(null, FavoritesLogic.PrefsKey(""));
        Equal(null, FavoritesLogic.PrefsKey(null));
        Equal("p:abc", FavoritesLogic.EntryKey(FavoriteKind.Purchase, "abc"));
        Equal("l:abc", FavoritesLogic.EntryKey(FavoriteKind.License, "abc"));
        Equal(null, FavoritesLogic.EntryKey(FavoriteKind.Purchase, ""));
        Require(FavoritesLogic.PrefsKey("u1") != FavoritesLogic.PrefsKey("u2"));
    }

    private static void FavoriteToggle()
    {
        var set = new HashSet<string>();
        Equal(true, FavoritesLogic.Toggle(set, "p:1"));
        Equal(true, FavoritesLogic.Toggle(set, "l:1"));
        Equal(2, set.Count); // same id as purchase and as license are two entries
        Equal(false, FavoritesLogic.Toggle(set, "p:1"));
        Require(!set.Contains("p:1") && set.Contains("l:1"));
        Equal(false, FavoritesLogic.Toggle(set, null));
        Equal(1, set.Count);

        set.Add("p:b"); set.Add("p:a");
        var text = FavoritesLogic.Serialize(set);
        Equal("l:1\np:a\np:b", text);
        var back = FavoritesLogic.Parse(text + "\n\njunk\np:\nx:9\n");
        Equal(3, back.Count);
        Require(back.Contains("p:a") && back.Contains("p:b") && back.Contains("l:1"));
        Equal(0, FavoritesLogic.Parse(null).Count);
    }

    // ---------- thumbnails ----------

    private static void ThumbUrls()
    {
        Require(ThumbnailCachePolicy.IsAllowedUrl("https://cdn.example.com/a.png"));
        Require(ThumbnailCachePolicy.IsAllowedUrl("https://cdn.example.com/a.png?v=2"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("http://cdn.example.com/a.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("ftp://cdn.example.com/a.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("file:///C:/secret.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("//cdn.example.com/a.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("a.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("javascript:alert(1)"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("https://user:pw@cdn.example.com/a.png"));
        Require(!ThumbnailCachePolicy.IsAllowedUrl(""));
        Require(!ThumbnailCachePolicy.IsAllowedUrl(null));
        Require(!ThumbnailCachePolicy.IsAllowedUrl("https://cdn.example.com/" + new string('a', 2100)));
    }

    private static void ThumbFileName()
    {
        // python: hashlib.sha256(b"https://cdn.example.com/a.png").hexdigest()
        Equal("6155f67efa620e76fd5fb2c42f3676aec9cba0ee554f6ad1f33773ff1d26f587.img", ThumbnailCachePolicy.FileNameFor("https://cdn.example.com/a.png"));
        Require(ThumbnailCachePolicy.FileNameFor("https://a/1") != ThumbnailCachePolicy.FileNameFor("https://a/2"));
        Require(ThumbnailCachePolicy.FileNameFor("https://a/../../x").IndexOf('/') < 0);
    }

    private static ThumbnailFile F(string path, long size, int daysAgo)
    {
        return new ThumbnailFile { Path = path, Size = size, LastUsedUtc = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo) };
    }

    private static void ThumbEviction()
    {
        var files = new List<ThumbnailFile> { F("new", 40, 0), F("old", 40, 9), F("mid", 40, 3), F("older", 40, 20) };
        // total 160, cap 100: drop the two least recently used (older, then old), keep mid and new.
        var doomed = ThumbnailCachePolicy.PlanEviction(files, 100);
        Equal(2, doomed.Count);
        Equal("older", doomed[0].Path);
        Equal("old", doomed[1].Path);
        Equal(0, ThumbnailCachePolicy.PlanEviction(files, 160).Count);
        Equal(4, ThumbnailCachePolicy.PlanEviction(files, 0).Count);
        // Equal times: path order decides, so the plan is stable.
        var ties = new List<ThumbnailFile> { F("b", 10, 1), F("a", 10, 1), F("c", 10, 1) };
        var plan = ThumbnailCachePolicy.PlanEviction(ties, 10);
        Equal("a", plan[0].Path);
        Equal("b", plan[1].Path);
        // One big file that alone exceeds the cap is the only thing removed when it is the oldest.
        var big = new List<ThumbnailFile> { F("big", 500, 5), F("s1", 10, 1), F("s2", 10, 0) };
        var one = ThumbnailCachePolicy.PlanEviction(big, 100);
        Equal(1, one.Count);
        Equal("big", one[0].Path);
    }

    // ---------- update check ----------

    private static void Throttle()
    {
        const long H = 3600;
        long now = 1800000000;
        Require(UpdateCheckLogic.ShouldRun(true, false, now, 0));
        Require(!UpdateCheckLogic.ShouldRun(false, false, now, 0));            // not signed in
        Require(!UpdateCheckLogic.ShouldRun(true, true, now, 0));              // already this session
        Require(!UpdateCheckLogic.ShouldRun(true, false, now, now - 6 * H + 1)); // 5h59m59s ago
        Require(UpdateCheckLogic.ShouldRun(true, false, now, now - 6 * H));    // exactly 6h
        Require(UpdateCheckLogic.ShouldRun(true, false, now, now - 30 * H));
        Require(UpdateCheckLogic.ShouldRun(true, false, now, now + H));        // clock moved back
    }

    private static LicenseVersionEntry V(string package, string version)
    {
        return new LicenseVersionEntry { version_id = package + version, package_name = package, package_version = version, file_name = package + "-" + version + ".zip" };
    }

    private static void FindUpdates()
    {
        var purchases = new[]
        {
            new PurchaseItem { name = "Flagged", update_available = true, item_id = "i1", files = new[] { new PurchaseFile { file_id = "f" } } },
            new PurchaseItem { name = "WARUDO only", update_available = true, item_id = "i3", files = new PurchaseFile[0] },   // no Unity files: not an update here
            new PurchaseItem { name = "Quiet", update_available = false, item_id = "i2" },
            null
        };
        var licenses = new[]
        {
            new LicenseEntry { license_id = "L1", versions = new[] { V("pkg.a", "1.2.0"), V("pkg.a", "1.1.0"), V("pkg.b", "2.0.0"), V("pkg.c", "3.0.0"), V("pkg.d", "1.1.0-preview.2") } },
            new LicenseEntry { license_id = "L2", versions = new[] { V("pkg.a", "1.3.0"), V("pkg.e", "1.0.0") } },
            new LicenseEntry { license_id = "L3", versions = null }
        };
        var installed = new Dictionary<string, string>
        {
            { "pkg.a", "1.1.0" },          // newer offered by two licenses: the newest wins
            { "pkg.b", "2.0.0" },          // same version: no update
            { "pkg.d", "1.0.0" },          // a prerelease is not offered to a stable install
            { "pkg.e", "1.0.0-preview.1" } // a prerelease install gets a newer stable
        };
        var found = UpdateCheckLogic.Find(purchases, licenses, n => { string v; return installed.TryGetValue(n, out v) ? v : ""; });
        // pkg.c is not installed, so it is not an update.
        Equal(3, found.Count);
        Equal(UpdateKind.Purchase, found[0].Kind);
        Equal("Flagged", found[0].Name);
        Equal("pkg.a", found[1].Name);
        Equal("1.3.0", found[1].Latest);
        Equal("1.1.0", found[1].Installed);
        Equal("L2", found[1].License.license_id);
        Equal("pkg.e", found[2].Name);
        Equal("1.0.0", found[2].Latest);
        Equal(0, UpdateCheckLogic.Find(null, null, n => "").Count);
    }

    private static void OfferedVersion()
    {
        Func<string, LicenseVersionEntry[], LicenseEntry> license = (id, versions) =>
            new LicenseEntry { license_id = id, name = id, versions = versions };
        var mixed = new[] { V("pkg.a", "0.1.15-preview.1"), V("pkg.a", "0.1.14"), V("pkg.a", "0.1.15-beta.1") };

        // Not installed: the newest release is offered, the preview and beta only carry their chips.
        var entry = ManagerCatalog.Build(null, new[] { license("L1", mixed) }, n => "", null)[0];
        Equal("0.1.14", entry.LatestVersion);
        Equal(EntryState.None, entry.State);
        Equal("0.1.15-preview.1", entry.Versions[0].Label);
        Equal(BuildKind.Preview, entry.Versions[0].Kind);
        Equal(BuildKind.Beta, entry.Versions[1].Kind);
        Require(!entry.Versions[0].Latest && !entry.Versions[1].Latest && entry.Versions[2].Latest);

        // A stable install of the newest release is up to date, not offered the preview.
        entry = ManagerCatalog.Build(null, new[] { license("L1", mixed) }, n => "0.1.14", null)[0];
        Equal("0.1.14", entry.LatestVersion);
        Equal(EntryState.Latest, entry.State);

        // A project already on a prerelease is offered the newest of all.
        entry = ManagerCatalog.Build(null, new[] { license("L1", mixed) }, n => "0.1.15-beta.1", null)[0];
        Equal("0.1.15-preview.1", entry.LatestVersion);
        Equal(EntryState.New, entry.State);

        // Only previews: the newest preview is offered.
        entry = ManagerCatalog.Build(null, new[] { license("L1", new[] { V("pkg.a", "0.2.0-preview.1"), V("pkg.a", "0.2.0-preview.2") }) }, n => "", null)[0];
        Equal("0.2.0-preview.2", entry.LatestVersion);

        // A hotfix is a normal patch release; its label adds the chip.
        var hotfix = V("pkg.a", "0.1.16");
        hotfix.version_label = "0.1.15 Hotfix 1";
        entry = ManagerCatalog.Build(null, new[] { license("L1", new[] { hotfix, V("pkg.a", "0.1.15") }) }, n => "0.1.15", null)[0];
        Equal("0.1.16", entry.LatestVersion);
        Equal(EntryState.New, entry.State);
        Equal(BuildKind.Hotfix, entry.Versions[0].Kind);

        // Files (.unitypackage): the label decides; the newest non-beta file is offered.
        Func<string, string, string, LicenseVersionEntry> file = (id, label, at) =>
            new LicenseVersionEntry { version_id = id, version_label = label, file_name = "MaskMaker.unitypackage", released_at = at };
        entry = ManagerCatalog.Build(null, new[] { license("L2", new[] {
            file("f1", "2.0.0", "2026-10-01T00:00:00Z"), file("f2", "2.1.0 beta", "2026-10-20T00:00:00Z") }) }, n => "", null)[0];
        Equal("2.0.0", entry.LatestVersion);
        Equal(BuildKind.Beta, entry.Versions[0].Kind);
        Require(!entry.Versions[0].Latest && entry.Versions[1].Latest);
    }

    // ---------- batch ----------

    private static BatchItem Item(string name, Func<Action<string, float>, Task<object>> run)
    {
        return new BatchItem { Name = name, Run = run };
    }

    private static void Wait(Task task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("The batch did not finish in time.");
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }

    private static void BatchOrder()
    {
        var order = new List<string>();
        BatchQueue queue = null;
        var progress = new List<string>();
        Func<string, BatchItem> make = name => Item(name, async report =>
        {
            order.Add(name);
            progress.Add(queue.ProgressText);
            report(name + " step", 0.5f);
            await Task.Yield();
            return name + " result";
        });
        queue = new BatchQueue(new[] { make("A"), make("B"), make("C") });
        Equal("1/3", queue.ProgressText);
        Wait(queue.RunAsync());
        Equal("A,B,C", string.Join(",", order.ToArray()));
        Equal("1/3,2/3,3/3", string.Join(",", progress.ToArray()));
        Equal(3, queue.DoneCount);
        Equal("B result", (string)queue.Results[1].Result);
        Equal(BatchStatus.Done, queue.Results[2].Status);
        Equal(false, queue.IsRunning);
        var threw = false;
        try { Wait(queue.RunAsync()); } catch (AggregateException) { threw = true; }
        Require(threw); // a queue runs once
    }

    private static void BatchCancel()
    {
        var ran = new List<string>();
        BatchQueue queue = null;
        queue = new BatchQueue(new[]
        {
            Item("A", r => { ran.Add("A"); return Task.FromResult<object>(1); }),
            Item("B", r => { ran.Add("B"); queue.Cancel(); return Task.FromResult<object>(2); }),
            Item("C", r => { ran.Add("C"); return Task.FromResult<object>(3); }),
            Item("D", r => { ran.Add("D"); return Task.FromResult<object>(4); })
        });
        Wait(queue.RunAsync());
        Equal("A,B", string.Join(",", ran.ToArray()));
        Equal(BatchStatus.Done, queue.Results[1].Status); // the current item finishes
        Equal(BatchStatus.Skipped, queue.Results[2].Status);
        Equal(BatchStatus.Skipped, queue.Results[3].Status);
        Equal(true, queue.CancelRequested);
    }

    private static void BatchFailures()
    {
        var ran = new List<string>();
        var queue = new BatchQueue(new[]
        {
            Item("A", r => { ran.Add("A"); throw new ApiException("A failed: HTTP 500", 500, "", ""); }),
            Item("B", r => { ran.Add("B"); return Task.FromResult<object>(null); })
        });
        Wait(queue.RunAsync());
        Equal("A,B", string.Join(",", ran.ToArray()));
        Equal(BatchStatus.Failed, queue.Results[0].Status);
        Equal("A failed: HTTP 500", queue.Results[0].Message);
        Equal(BatchStatus.Done, queue.Results[1].Status);

        ran.Clear();
        var expired = new BatchQueue(new[]
        {
            Item("A", r => { ran.Add("A"); throw new SessionExpiredException("Your sign-in has expired."); }),
            Item("B", r => { ran.Add("B"); return Task.FromResult<object>(null); })
        });
        Wait(expired.RunAsync());
        Equal("A", string.Join(",", ran.ToArray()));
        Equal(true, expired.SessionExpired);
        Equal(BatchStatus.Failed, expired.Results[0].Status);
        Equal(BatchStatus.Skipped, expired.Results[1].Status);
    }

    private static DeviceLimitException Refusal()
    {
        return (DeviceLimitException)ApiErrorParser.ToException(409, "{\"error\":\"device_limit\",\"message\":\"Too many devices.\"}", "x", null);
    }

    private static void BatchPauseResume()
    {
        DeviceLimitNotice.Clear();
        var attempts = new Dictionary<string, int>();
        var ran = new List<string>();
        Func<string, bool, BatchItem> make = (name, refuseFirst) => Item(name, r =>
        {
            int n;
            attempts.TryGetValue(name, out n);
            attempts[name] = ++n;
            ran.Add(name + n);
            if (refuseFirst && n == 1) throw Refusal();
            return Task.FromResult<object>(name);
        });
        var queue = new BatchQueue(new[] { make("A", false), make("B", true), make("C", false) });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsPaused, "the queue to pause on the device limit");
        Equal("A1,B1", string.Join(",", ran.ToArray())); // C has not started
        Equal(BatchStatus.Paused, queue.Results[1].Status);
        Equal(1, queue.CurrentIndex);
        Require(DeviceLimitNotice.Current != null && DeviceLimitNotice.Current.Kind == NoticeKind.DeviceLimit);
        Require(DeviceLimitNotice.Current.Items.Contains("B"));
        Require(!task.IsCompleted);

        DeviceLimitNotice.RetryAsync().Wait(TimeSpan.FromSeconds(5));
        Wait(task);
        Equal("A1,B1,B2,C1", string.Join(",", ran.ToArray()));
        Equal(3, queue.DoneCount);
        Equal(false, queue.IsPaused);
        Require(DeviceLimitNotice.Current == null); // cleared once resumed
    }

    private static void BatchCancelPaused()
    {
        DeviceLimitNotice.Clear();
        var ran = new List<string>();
        var queue = new BatchQueue(new[]
        {
            Item("A", r => { ran.Add("A"); return Task.FromResult<object>(null); }),
            Item("B", r => { ran.Add("B"); throw Refusal(); }),
            Item("C", r => { ran.Add("C"); return Task.FromResult<object>(null); })
        });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsPaused, "the queue to pause");
        queue.Cancel();
        Wait(task);
        Equal("A,B", string.Join(",", ran.ToArray()));
        Equal(BatchStatus.Done, queue.Results[0].Status);
        Equal(BatchStatus.Skipped, queue.Results[1].Status);
        Equal(BatchStatus.Skipped, queue.Results[2].Status);
        Require(DeviceLimitNotice.Current == null);
    }

    // ---------- contract v3 ----------

    private static void ManagerHeader()
    {
        Equal("X-Atelier-Manager", DeviceIdentityCore.ManagerHeaderName);
        Equal("1", DeviceIdentityCore.ManagerHeaderValue);
        // OnAirDeckApi.AddHeaders sets it for every Post/Get (including plugin-version and the publishable-key calls);
        // that UnityWebRequest code is compile-checked only, so the source is checked for the single call site.
        var api = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Editor", "Api", "OnAirDeckApi.cs"));
        Require(api.Contains("SetRequestHeader(DeviceIdentityCore.ManagerHeaderName, DeviceIdentityCore.ManagerHeaderValue)"));
        var afterAdd = api.Substring(api.IndexOf("private static void AddHeaders", StringComparison.Ordinal));
        Require(afterAdd.IndexOf("ManagerHeaderName", StringComparison.Ordinal) < afterAdd.IndexOf("if (bearer != OnAirDeckConfig.PublishableKey)", StringComparison.Ordinal));
    }

    // JsonUtility is not available here; System.Text.Json (public fields) checks field names and value types of the DTOs.
    private static void ParseV3Purchases()
    {
        const string body = "{\"purchases\":[{\"name\":\"Pack\",\"date\":\"2026-10-01\",\"item_id\":\"i1\",\"product_id\":\"p1\",\"files\":[{\"file_id\":\"f2\",\"file_name\":\"Pack.unitypackage\"}],"
            + "\"downloaded\":false,\"update_available\":true,\"description\":\"A plain text description.\",\"product_url\":\"https://onairdeck.com/p/p1\","
            + "\"versions\":[{\"file_id\":\"f2\",\"version\":\"1.1\",\"released_at\":\"2026-10-20T00:00:00Z\",\"notes\":\"Fixed a bug\",\"size_bytes\":123456789012},"
            + "{\"file_id\":\"f1\",\"version\":\"1.0\",\"released_at\":\"\",\"notes\":\"\",\"size_bytes\":0}]}],"
            + "\"licenses\":[{\"license_id\":\"l1\",\"name\":\"Core\",\"rights\":[],\"description\":\"Core text\",\"product_url\":\"\","
            + "\"versions\":[{\"version_id\":\"v1\",\"version_label\":\"1.0\",\"file_name\":\"a-1.0.0.zip\",\"file_size\":10,\"released_at\":\"2026-10-02T00:00:00Z\",\"release_notes\":\"n\",\"package_name\":\"a\",\"package_version\":\"1.0.0\"}]}]}";
        var options = new JsonSerializerOptions { IncludeFields = true };
        var parsed = JsonSerializer.Deserialize<PurchasesResponse>(body, options);
        var p = parsed.purchases[0];
        Equal("A plain text description.", p.description);
        Equal("https://onairdeck.com/p/p1", p.product_url);
        Equal(2, p.versions.Length);
        Equal("f2", p.versions[0].file_id);
        Equal("1.1", p.versions[0].version);
        Equal("Fixed a bug", p.versions[0].notes);
        Equal(123456789012L, p.versions[0].size_bytes);
        Equal("", p.versions[1].released_at);
        var l = parsed.licenses[0];
        Equal("Core text", l.description);
        Equal("", l.product_url);
        Equal("2026-10-02T00:00:00Z", l.versions[0].released_at);

        // A body from a server without v3: the new fields keep their "" / empty defaults.
        var old = JsonSerializer.Deserialize<PurchasesResponse>("{\"purchases\":[{\"name\":\"X\",\"item_id\":\"i\",\"product_id\":\"p\"}],\"licenses\":[{\"license_id\":\"l\",\"versions\":[{\"version_id\":\"v\"}]}]}", options);
        Equal("", old.purchases[0].description);
        Equal("", old.purchases[0].product_url);
        Equal(0, old.purchases[0].versions.Length);
        Equal("", old.licenses[0].description);
        Equal("", old.licenses[0].versions[0].released_at);
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir, "package.json"))) dir = System.IO.Path.GetDirectoryName(dir);
        if (dir == null) throw new Exception("package.json not found above " + AppContext.BaseDirectory);
        return dir;
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
