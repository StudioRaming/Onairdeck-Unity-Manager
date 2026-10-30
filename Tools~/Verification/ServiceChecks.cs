using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// Contract v4 (license files), v5 (dependency resolver order and error kinds) and v6 (purchase watcher rules).
// The real DependencyResolver, LicenseFilePlan, LicenseFile, InstalledPackages and PurchaseWatcherLogic sources are compiled in.
// plugin-license, plugin-download and EditorPrefs are not exercised here (compile-checked only).
internal static class ServiceChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        _project = project;
        check("v5 resolver: installed first, then a license version, then the listings", ResolverOrder);
        check("v5 resolver: error kinds (not published yet, get it on Atelier first, plain, unreadable listing)", ResolverErrors);
        check("v4 license files: which licenses are due (installed by any route, renewal, duplicate names)", LicensePlan);
        check("v4 license files: path, written file, local check with a test-signed payload, renewal window", LicenseFileProof);
        check("v6 watcher: first load marks everything seen, later entries are new, mark seen", SeenLogic);
        check("v6 watcher: 60 s limit, signed in only, 5 minute tick only while the window is open", WatcherThrottle);
    }

    private static string _project;

    // ---------- v5 ----------

    private static LicenseEntry LicenseWith(string id, params string[] packageVersions)
    {
        var versions = new List<LicenseVersionEntry>();
        for (var i = 0; i < packageVersions.Length; i += 2)
            versions.Add(new LicenseVersionEntry { version_id = id + "-v" + i, package_name = packageVersions[i], package_version = packageVersions[i + 1], file_name = packageVersions[i] + "-" + packageVersions[i + 1] + ".zip" });
        return new LicenseEntry { license_id = id, name = "License " + id, versions = versions.ToArray() };
    }

    private static DependencyResolution Resolve(string installed, LicenseEntry[] licenses, Func<Task<VpmVersion>> listings)
    {
        return DependencyResolver.ResolveAsync("com.example.core", "0.1.15", installed, licenses, listings).GetAwaiter().GetResult();
    }

    private static void ResolverOrder()
    {
        var listingCalls = 0;
        Func<Task<VpmVersion>> listing = () => { listingCalls++; return Task.FromResult(new VpmVersion { Name = "com.example.core", Version = "0.1.15", Url = "https://x/y.zip" }); };
        var licenses = new[] { LicenseWith("l1", "com.other", "1.0.0"), LicenseWith("l2", "com.example.core", "0.1.15") };

        // 1) installed wins, nothing else is asked.
        var r = Resolve("0.1.15", licenses, listing);
        Equal(DependencySource.Installed, r.Source);
        Equal(0, listingCalls);

        // 2) a license version beats the listing that also has it; the listing is not even read.
        r = Resolve("0.1.14", licenses, listing);
        Equal(DependencySource.LicenseVersion, r.Source);
        Equal("l2-v0", r.LicenseVersion.version_id);
        Equal(0, listingCalls);

        // 3) no license version: the listing.
        r = Resolve("", new[] { LicenseWith("l1", "com.other", "1.0.0") }, listing);
        Equal(DependencySource.TrustedListing, r.Source);
        Equal(1, listingCalls);
        r = Resolve("", null, listing);
        Equal(DependencySource.TrustedListing, r.Source);

        // The version has to match exactly, and a license entry that is not a package is ignored.
        Require(DependencyResolver.FindLicenseVersion(licenses, "com.example.core", "0.1.14") == null);
        Require(DependencyResolver.FindLicenseVersion(new[] { new LicenseEntry { license_id = "x", versions = new[] { new LicenseVersionEntry { file_name = "a.zip" } } } }, "a", "1") == null);
    }

    private static void ResolverErrors()
    {
        Func<Task<VpmVersion>> none = () => Task.FromResult<VpmVersion>(null);

        // Listed in an account license at another version only: not published yet (no website button needed).
        var other = new[] { LicenseWith("l1", "com.example.core", "0.1.14") };
        var e = Throws<DependencyUnavailableException>(() => Resolve("", other, none));
        Equal(DependencyErrorKind.NotPublishedYet, e.Kind);
        Equal("com.example.core 0.1.15 is not published yet.", e.Message);
        Equal("com.example.core", e.PackageName);
        Equal("0.1.15", e.PackageVersion);

        // In no license of the account: get it on Atelier first (the window adds the open-website button).
        e = Throws<DependencyUnavailableException>(() => Resolve("", new[] { LicenseWith("l1", "com.other", "1.0.0") }, none));
        Equal(DependencyErrorKind.GetOnAtelierFirst, e.Kind);
        Equal("Get com.example.core on Atelier first.", e.Message);
        e = Throws<DependencyUnavailableException>(() => Resolve("", new LicenseEntry[0], none));
        Equal(DependencyErrorKind.GetOnAtelierFirst, e.Kind);

        // Callers without account data keep the plain message and the generic exception.
        var plain = Throws<PackageInstallException>(() => Resolve("", null, none));
        Require(!(plain is DependencyUnavailableException));
        Equal("Version 0.1.15 of com.example.core is not in the trusted package listing.", plain.Message);

        // A listing that could not be read is that error, never "not published".
        var broken = Throws<PackageInstallException>(() => Resolve("", other, () => { throw new PackageInstallException("offline"); }));
        Equal("offline", broken.Message);
        Require(DependencyUnavailableException_IsInstallException());
    }

    private static bool DependencyUnavailableException_IsInstallException()
    {
        return typeof(PackageInstallException).IsAssignableFrom(typeof(DependencyUnavailableException));
    }

    // ---------- v4 ----------

    private static string Root { get { return Path.GetDirectoryName(UnityEngine.Application.dataPath); } }

    private static void LicensePlan()
    {
        var root = Path.Combine(_project, "plan-project");
        Directory.CreateDirectory(Path.Combine(root, "Packages", "com.embedded", "x"));
        File.WriteAllText(Path.Combine(root, "Packages", "com.embedded", "package.json"), "{\"name\":\"com.embedded\",\"version\":\"1.0.0\"}");
        File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), "{\"dependencies\":{ \"com.git.one\" : \"https://x/y.git\", \"com.other\": \"1.0.0\"}}");
        File.WriteAllText(Path.Combine(root, "Packages", "vpm-manifest.json"), "{\"locked\":{\"com.vpm.one\":{\"version\":\"1.2.3\"}}}");
        Require(InstalledPackages.IsInstalled(root, "com.embedded"));
        Require(InstalledPackages.IsInstalled(root, "com.git.one"));
        Require(InstalledPackages.IsInstalled(root, "com.vpm.one"));
        Require(!InstalledPackages.IsInstalled(root, "com.git"));        // a prefix is not the package
        Require(!InstalledPackages.IsInstalled(root, "1.2.3"));          // a value is not a package
        Require(!InstalledPackages.IsInstalled(root, "com.missing"));
        Require(!InstalledPackages.IsInstalled(root, ""));

        Func<string, bool> installed = name => InstalledPackages.IsInstalled(root, name);
        var licenses = new[]
        {
            LicenseWith("aaaaaaaa-1", "com.git.one", "1.0.0"),
            LicenseWith("bbbbbbbb-2", "com.missing", "1.0.0"),
            LicenseWith("cccccccc-3", "com.missing", "1.0.0", "com.vpm.one", "1.2.3"),
            new LicenseEntry { license_id = "dddddddd-4", name = "No versions" }
        };
        var jobs = LicenseFilePlan.Select(licenses, installed, folder => true);
        Equal(2, jobs.Count);
        Equal("aaaaaaaa-1", jobs[0].License.license_id);
        Equal("Assets/StudioRaming_Onairdeck/License aaaaaaaa-1", jobs[0].Folder);
        Equal("cccccccc-3", jobs[1].License.license_id);

        // A license that does not need a refresh is left alone.
        Equal(0, LicenseFilePlan.Select(licenses, installed, folder => false).Count);

        // Two licenses with the same name get their own folders.
        var same = new[]
        {
            new LicenseEntry { license_id = "11111111-aaaa", name = "MingToon", versions = new[] { new LicenseVersionEntry { package_name = "com.git.one", package_version = "1" } } },
            new LicenseEntry { license_id = "22222222-bbbb", name = "MingToon", versions = new[] { new LicenseVersionEntry { package_name = "com.git.one", package_version = "1" } } }
        };
        jobs = LicenseFilePlan.Select(same, installed, folder => true);
        Equal(2, jobs.Count);
        Equal("Assets/StudioRaming_Onairdeck/MingToon (11111111)", jobs[0].Folder);
        Equal("Assets/StudioRaming_Onairdeck/MingToon (22222222)", jobs[1].Folder);

        // A name with path characters stays inside the install root.
        var evil = new LicenseEntry { license_id = "e", name = "..\\..\\Evil/Name", versions = new[] { new LicenseVersionEntry { package_name = "com.git.one", package_version = "1" } } };
        jobs = LicenseFilePlan.Select(new[] { evil }, installed, folder => true);
        Require(jobs[0].Folder.StartsWith("Assets/StudioRaming_Onairdeck/", StringComparison.Ordinal));
        Require(jobs[0].Folder.IndexOf("..", StringComparison.Ordinal) < 0 && jobs[0].Folder.Substring("Assets/StudioRaming_Onairdeck/".Length).IndexOf('/') < 0);
    }

    private static string Base64Url(byte[] data)
    {
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void LicenseFileProof()
    {
        // The server (contract v4) sets product_id = license_id and adds license_id and kind. Signed here with a throwaway key,
        // because the embedded public key only verifies licenses the real server signed.
        const string licenseId = "5d1e7a40-1111-4222-8333-944455556666";
        const string userId = "u-1";
        var now = 1792000000L;
        using (var rsa = RSA.Create(2048))
        {
            var key = rsa.ExportParameters(false);
            Func<long, string> issue = exp =>
            {
                var json = "{\"v\":1,\"iss\":\"onairdeck\",\"client\":\"unity-manager\",\"user_id\":\"" + userId + "\",\"product_id\":\"" + licenseId + "\",\"license_id\":\"" + licenseId
                    + "\",\"kind\":\"license\",\"product_name\":\"MingToon\",\"iat\":" + now + ",\"exp\":" + exp + ",\"nonce\":\"n\"}";
                return Base64Url(Encoding.UTF8.GetBytes(json)) + "." + Base64Url(rsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            };

            var license = new LicenseEntry { license_id = licenseId, name = "MingToon Core", versions = new[] { new LicenseVersionEntry { package_name = "com.studioraming.mingtoon", package_version = "0.1.15" } } };
            var job = LicenseFilePlan.Select(new[] { license }, n => true, folder => LicenseFile.NeedsRefresh(folder, now))[0];
            Equal("Assets/StudioRaming_Onairdeck/MingToon Core", job.Folder);
            Require(LicenseFile.NeedsRefresh(job.Folder, now)); // no file yet: due

            var path = LicenseFile.Write(job.Folder, issue(now + 7 * 86400));
            Equal(Path.Combine(Root, "Assets", "StudioRaming_Onairdeck", "MingToon Core", "onairdeck.license"), path);
            Require(File.Exists(path));
            Require(!LicenseFile.NeedsRefresh(job.Folder, now)); // 7 days left: not due
            Equal(0, LicenseFilePlan.Select(new[] { license }, n => true, folder => LicenseFile.NeedsRefresh(folder, now)).Count);

            // The written file passes the same check OnAirDeckLicense.Check runs, for the license id.
            var text = File.ReadAllText(path).Trim();
            var result = LicenseVerifier.Verify(text, licenseId, userId, now + 10, key);
            Equal(LicenseStatus.Valid, result.Status);
            Equal(LicenseStatus.WrongProduct, LicenseVerifier.Verify(text, "another-license", userId, now + 10, key).Status);
            Equal(LicenseStatus.UserMismatch, LicenseVerifier.Verify(text, licenseId, "u-2", now + 10, key).Status);
            Equal(LicenseStatus.Expired, LicenseVerifier.Verify(text, licenseId, userId, now + 8 * 86400, key).Status);

            // Renewal: due 2 days before expiry, not 3.
            LicenseFile.Write(job.Folder, issue(now + 3 * 86400));
            Require(!LicenseFile.NeedsRefresh(job.Folder, now));
            LicenseFile.Write(job.Folder, issue(now + 2 * 86400 - 1));
            Require(LicenseFile.NeedsRefresh(job.Folder, now));
        }
    }

    // ---------- v6 ----------

    private static PurchasesResponse Listing(string[] productIds, string[] licenseIds)
    {
        var purchases = new List<PurchaseItem>();
        foreach (var id in productIds) purchases.Add(new PurchaseItem { name = "P " + id, product_id = id, item_id = "i-" + id, files = new[] { new PurchaseFile { file_id = "f" } } });
        var licenses = new List<LicenseEntry>();
        foreach (var id in licenseIds) licenses.Add(new LicenseEntry { license_id = id, name = "L " + id });
        return new PurchasesResponse { purchases = purchases.ToArray(), licenses = licenses.ToArray() };
    }

    private static void SeenLogic()
    {
        Equal("OnAirDeck.UnityManager.SeenEntries.u1", PurchaseWatcherLogic.SeenKey("u1"));
        Equal(null, PurchaseWatcherLogic.SeenKey(""));
        Require(PurchaseWatcherLogic.SeenKey("u1") != PurchaseWatcherLogic.SeenKey("u2"));

        // First load for an account (no stored text): nothing is new, everything is stored as seen.
        var first = PurchaseWatcherLogic.Diff(null, Listing(new[] { "a", "b" }, new[] { "L1" }));
        Require(first.IsFirstLoad);
        Equal(0, first.Unseen.Count);
        Equal("l:L1\np:a\np:b", first.FirstLoadText);
        // An empty first load is stored too (an empty string is "seen, nothing"), so the next purchase is new.
        var empty = PurchaseWatcherLogic.Diff(null, Listing(new string[0], new string[0]));
        Require(empty.IsFirstLoad);
        Equal("", empty.FirstLoadText);
        var afterEmpty = PurchaseWatcherLogic.Diff("", Listing(new[] { "a" }, new string[0]));
        Require(!afterEmpty.IsFirstLoad);
        Equal(1, afterEmpty.Unseen.Count);

        // Later loads: what is not stored is new, in list order; stored ones are not.
        var diff = PurchaseWatcherLogic.Diff(first.FirstLoadText, Listing(new[] { "a", "c", "b" }, new[] { "L1", "L2" }));
        Require(!diff.IsFirstLoad);
        Equal(2, diff.Unseen.Count);
        Equal("p:c", diff.Unseen[0].Key);
        Equal(FavoriteKind.Purchase, diff.Unseen[0].Kind);
        Equal("l:L2", diff.Unseen[1].Key);
        Equal(FavoriteKind.License, diff.Unseen[1].Kind);

        // Marking seen appends keys after the stored ones, without duplicates; the same list then has nothing new.
        var stored = PurchaseWatcherLogic.MarkSeen(first.FirstLoadText, new[] { "p:c", "l:L2", "p:c", "" });
        Equal("l:L1\np:a\np:b\np:c\nl:L2", stored);
        Equal(0, PurchaseWatcherLogic.Diff(stored, Listing(new[] { "a", "c", "b" }, new[] { "L1", "L2" })).Unseen.Count);

        // A legacy order without a product id is keyed by its item id; duplicates collapse.
        var legacy = new PurchasesResponse { purchases = new[] { new PurchaseItem { name = "Old", item_id = "i9", files = new[] { new PurchaseFile { file_id = "f" } } }, new PurchaseItem { name = "Old again", item_id = "i9", files = new[] { new PurchaseFile { file_id = "f" } } }, new PurchaseItem { name = "No id", files = new[] { new PurchaseFile { file_id = "f" } } } }, licenses = new LicenseEntry[0] };
        var entries = PurchaseWatcherLogic.Entries(legacy);
        Equal(1, entries.Count);
        Equal("p:i9", entries[0].Key);
        Equal(0, PurchaseWatcherLogic.Entries(null).Count);
    }

    private static void WatcherThrottle()
    {
        const long now = 1792000000;
        Require(!PurchaseWatcherLogic.CanRefresh(false, now, 0));            // not signed in
        Require(PurchaseWatcherLogic.CanRefresh(true, now, 0));              // never ran
        Require(!PurchaseWatcherLogic.CanRefresh(true, now, now - 59));      // within 60 s
        Require(PurchaseWatcherLogic.CanRefresh(true, now, now - 60));       // exactly 60 s
        Require(PurchaseWatcherLogic.CanRefresh(true, now, now + 5));        // the clock moved back
        Require(!PurchaseWatcherLogic.PeriodicDue(false, now, 0));           // window closed: no tick
        Require(PurchaseWatcherLogic.PeriodicDue(true, now, 0));
        Require(!PurchaseWatcherLogic.PeriodicDue(true, now, now - 299));
        Require(PurchaseWatcherLogic.PeriodicDue(true, now, now - 300));
    }

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (Exception e)
        {
            var inner = e is AggregateException && e.InnerException != null ? e.InnerException : e;
            if (inner is T) return (T)inner;
            throw new Exception("Expected " + typeof(T).Name + ", got " + inner.GetType().Name + ": " + inner.Message);
        }
        throw new Exception("Expected " + typeof(T).Name + ", nothing was thrown");
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
