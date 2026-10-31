using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// Manager 0.1.4, MingToon through the Manager only: the move of a VCC install (G5), one row for the free and the commercial Core license
// (G3), and the install order of an import that grants several licenses (G4).
internal static class MingToonMoveChecks
{
    private const string Core = "com.studioraming.mingtoon";
    private const string Urp = "com.studioraming.mingtoon.urp";

    public static void Run(Action<string, Action> check, string project)
    {
        check("vcc move: a Manager-only package VCC lists (in dependencies or locked) with its folder here is found; one without a folder, or another package, is not", Find(project));
        check("vcc move: both entries of core and add-on leave dependencies and locked; every other entry, unknown keys, CRLF and the final newline stay; the file is backed up first", Move(project));
        check("vcc move: undo puts the entries back at the folder's current version (no downgrade by the resolver); a package listed again refuses and changes nothing; keep ends the offer", UndoAndKeep(project));
        check("vcc move: nothing listed changes nothing; a file that is not JSON refuses", Refusals);
        check("license rows: the free and the commercial Core license are one row (the commercial one, its versions taken from the unlimited license when it has a device limit, installers paired by label); the folded license's keys stay on the row; a device-limited or other-package license keeps its own row", MergeRows);
        check("import order: an add-on (<core>.<suffix>) installs before its core, file-only licenses last, otherwise the given order; a core is skipped after its add-on", Order);
    }

    // ------------------------------------------------------------ G5

    private static string NewProject(string parent, string name)
    {
        var root = Path.Combine(parent, "vcc-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        return root;
    }

    private static void Package(string root, string name, string version)
    {
        var full = Path.Combine(root, "Packages", name);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "package.json"), "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"}");
    }

    // The shape VCC writes, with CRLF, a final newline, a third-party package and a key the Manager does not know.
    private static string VccManifest(string coreVersion, bool withUrp)
    {
        var urpDeps = withUrp ? "    \"" + Urp + "\": {\r\n      \"version\": \"0.1.14\"\r\n    },\r\n" : "";
        var urpLocked = withUrp ? "    \"" + Urp + "\": {\r\n      \"version\": \"0.1.14\",\r\n      \"dependencies\": {\r\n        \"" + Core + "\": \"0.1.14\"\r\n      }\r\n    },\r\n" : "";
        return "{\r\n  \"dependencies\": {\r\n    \"com.vrchat.avatars\": {\r\n      \"version\": \"3.10.1\"\r\n    },\r\n" + urpDeps
            + "    \"" + Core + "\": {\r\n      \"version\": \"" + coreVersion + "\"\r\n    }\r\n  },\r\n"
            + "  \"locked\": {\r\n    \"com.vrchat.avatars\": {\r\n      \"version\": \"3.10.1\",\r\n      \"dependencies\": {\r\n        \"com.vrchat.base\": \"3.10.1\"\r\n      }\r\n    },\r\n"
            + "    \"com.vrchat.base\": {\r\n      \"version\": \"3.10.1\"\r\n    },\r\n" + urpLocked
            + "    \"" + Core + "\": {\r\n      \"version\": \"" + coreVersion + "\"\r\n    }\r\n  },\r\n  \"x-vcc-note\": [1, \"a\"]\r\n}\r\n";
    }

    private static string ManifestPath(string root) { return Path.Combine(root, "Packages", "vpm-manifest.json"); }

    private static Dictionary<string, object> Section(string root, string section)
    {
        var parsed = MiniJson.ParseObject(File.ReadAllText(ManifestPath(root), Encoding.UTF8));
        return parsed[section] as Dictionary<string, object>;
    }

    private static string VersionIn(string root, string section, string package)
    {
        object entry;
        return Section(root, section).TryGetValue(package, out entry) ? MiniJson.GetString((Dictionary<string, object>)entry, "version") : null;
    }

    private static Action Find(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "find");
            Equal(0, VpmMigration.Find(root).Count);   // no vpm-manifest.json
            File.WriteAllText(ManifestPath(root), VccManifest("0.1.14", true));
            Equal(0, VpmMigration.Find(root).Count);   // listed, but no folder (VCC would download it; nothing to move)
            Package(root, Core, "0.1.16");
            var held = VpmMigration.Find(root);
            Equal(1, held.Count);
            Equal(Core, held[0].Package);
            Equal("0.1.16", held[0].FolderVersion);
            Equal("0.1.14", held[0].ListedVersion);
            Package(root, Urp, "0.1.14");
            Equal(2, VpmMigration.Find(root).Count);

            // Only a "locked" entry left (a hand-edited file) still counts: the resolver acts on it alone.
            var only = NewProject(parent, "find-locked");
            Package(only, Core, "0.1.16");
            File.WriteAllText(ManifestPath(only), "{\"dependencies\":{},\"locked\":{\"" + Core + "\":{\"version\":\"0.1.14\"}}}");
            Equal("0.1.14", VpmMigration.Find(only).Single().ListedVersion);
        };
    }

    private static Action Move(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "move");
            Package(root, Core, "0.1.16");
            Package(root, Urp, "0.1.14");
            var original = VccManifest("0.1.14", true);
            File.WriteAllText(ManifestPath(root), original);

            var move = VpmMigration.Move(root, VpmMigration.Find(root).ConvertAll(h => h.Package), new DateTime(2026, 10, 31, 17, 0, 0));
            var after = File.ReadAllText(ManifestPath(root), Encoding.UTF8);
            foreach (var section in new[] { "dependencies", "locked" })
            {
                Require(VersionIn(root, section, Core) == null);
                Require(VersionIn(root, section, Urp) == null);
                Equal("3.10.1", VersionIn(root, section, "com.vrchat.avatars"));
            }
            Equal("3.10.1", VersionIn(root, "locked", "com.vrchat.base"));
            var avatars = (Dictionary<string, object>)Section(root, "locked")["com.vrchat.avatars"];
            Equal("3.10.1", MiniJson.GetString((Dictionary<string, object>)avatars["dependencies"], "com.vrchat.base"));
            Require(after.Contains("\"x-vcc-note\""));
            Require(after.Contains("\r\n") && !after.Replace("\r\n", "").Contains("\n"));
            Require(after.EndsWith("\r\n", StringComparison.Ordinal));

            // The backup is the file as it was, beside its record; the folders were not touched.
            Equal(original, File.ReadAllText(Path.Combine(move.Folder, VpmMigration.ManifestName), Encoding.UTF8));
            Require(Path.GetFileName(move.Folder).StartsWith(VpmMigration.FolderPrefix, StringComparison.Ordinal));
            Equal("0.1.16", InstalledPackages.InstalledVersion(root, Core));
            Equal(2, move.Packages.Count);
            Equal(1, VpmMigration.Pending(root).Count);
            Equal(0, VpmMigration.Find(root).Count);
            // A second move finds nothing and refuses without a new backup.
            try { VpmMigration.Move(root, new List<string> { Core }, DateTime.Now); throw new Exception("a second move did not refuse"); }
            catch (PackageInstallException) { }
            Equal(1, VpmMigration.Pending(root).Count);
        };
    }

    private static Action UndoAndKeep(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "undo");
            Package(root, Core, "0.1.14");
            Package(root, Urp, "0.1.14");
            File.WriteAllText(ManifestPath(root), VccManifest("0.1.14", true));
            var move = VpmMigration.Move(root, new List<string> { Core, Urp }, DateTime.Now);

            // The Manager updates the core afterwards; undo must not list the old version (the resolver would put it back).
            Package(root, Core, "0.1.17");
            VpmMigration.Undo(root, move);
            Equal("0.1.17", VersionIn(root, "dependencies", Core));
            Equal("0.1.17", VersionIn(root, "locked", Core));
            Equal("0.1.14", VersionIn(root, "locked", Urp));
            var urpLocked = (Dictionary<string, object>)Section(root, "locked")[Urp];
            Equal("0.1.14", MiniJson.GetString((Dictionary<string, object>)urpLocked["dependencies"], Core));
            Equal(0, VpmMigration.Pending(root).Count);
            Equal(2, VpmMigration.Find(root).Count);

            // Moved again, then VCC lists the core again: undo refuses and the file stays as it is.
            var again = VpmMigration.Move(root, new List<string> { Core, Urp }, DateTime.Now);
            var relisted = File.ReadAllText(ManifestPath(root), Encoding.UTF8).Replace("\"locked\": {", "\"locked\": {\r\n    \"" + Core + "\": {\r\n      \"version\": \"0.1.14\"\r\n    },");
            File.WriteAllText(ManifestPath(root), relisted);
            try { VpmMigration.Undo(root, again); throw new Exception("undo over a listed package did not refuse"); }
            catch (PackageInstallException) { }
            Equal(relisted, File.ReadAllText(ManifestPath(root), Encoding.UTF8));
            Equal(1, VpmMigration.Pending(root).Count);
            // Listed again in the other object only: still refused (an entry left there would downgrade the folder).
            var depsOnly = "{\"dependencies\":{\"" + Core + "\":{\"version\":\"0.1.14\"}},\"locked\":{}}";
            File.WriteAllText(ManifestPath(root), depsOnly);
            try { VpmMigration.Undo(root, again); throw new Exception("undo over a dependencies-only entry did not refuse"); }
            catch (PackageInstallException) { }
            Equal(depsOnly, File.ReadAllText(ManifestPath(root), Encoding.UTF8));

            VpmMigration.Keep(again);
            Equal(0, VpmMigration.Pending(root).Count);
            Require(File.Exists(Path.Combine(again.Folder, VpmMigration.ManifestName)));
        };
    }

    private static void Refusals()
    {
        var removed = new List<string>();
        Require(VpmMigration.Without("{\"dependencies\":{\"a\":{\"version\":\"1\"}},\"locked\":{}}", new[] { Core }, removed) == null);
        Equal(0, removed.Count);
        try { VpmMigration.Without("not json", new[] { Core }, removed); throw new Exception("invalid JSON did not refuse"); }
        catch (PackageInstallException) { }
    }

    // ------------------------------------------------------------ G3

    private static LicenseVersionEntry Zip(string package, string version, string id)
    {
        return new LicenseVersionEntry { version_id = id, version_label = version, file_name = package + "-" + version + ".zip", package_name = package, package_version = version };
    }

    private static LicenseVersionEntry Installer(string version, string id)
    {
        return new LicenseVersionEntry { version_id = id, version_label = version, file_name = "MingToon-" + version + "-update.unitypackage" };
    }

    private static void MergeRows()
    {
        var commercial = new LicenseEntry { license_id = "4740448e", name = "MingToon", rights = new[] { "personal", "commercial_streaming", "commercial_creation" }, max_devices = 3,
            versions = new[] { Zip(Core, "0.1.15", "c15"), Installer("0.1.15", "c15i") } };
        var free = new LicenseEntry { license_id = "free-core", name = "MingToon (Free)", rights = new[] { "personal" },
            versions = new[] { Zip(Core, "0.1.16", "f16"), Installer("0.1.16", "f16i"), Zip(Core, "0.1.15", "f15") } };
        var other = new LicenseEntry { license_id = "urp", name = "MingToon URP", versions = new[] { Zip(Urp, "0.1.16", "u16") } };

        var also = new Dictionary<string, List<string>>();
        var rows = ManagerCatalog.MergeSamePackage(new[] { free, commercial, other }, also);
        Equal(2, rows.Count);
        var row = rows.Single(r => r.license_id == "4740448e");
        Require(!ReferenceEquals(row, commercial));             // a copy: the account's own list (license files) stays as it was
        Equal(2, commercial.versions.Length);
        // The commercial license has a device limit: a version both offer comes from the free one, so a full seat count does not stop it.
        Equal("f16,f16i,f15", string.Join(",", row.versions.Select(v => v.version_id)));
        Equal("free-core", also["4740448e"].Single());
        Require(rows.Contains(other));

        var entries = ManagerCatalog.Build(null, new[] { free, commercial, other }, p => p == Core ? "0.1.15" : "", null);
        var entry = entries.Single(e => e.PackageName == Core);
        Equal("0.1.16", entry.LatestVersion);
        Equal(EntryState.New, entry.State);
        Require(entry.HoldsLicense("free-core") && entry.HoldsLicense("4740448e") && !entry.HoldsLicense("urp"));
        // Favorites and the new-purchase notice of the folded license still find the row.
        Equal("l:4740448e,l:free-core", string.Join(",", entry.Keys));
        Equal(3, entry.MaxDevices);                              // the commercial license's devices stay manageable on the row
        // The installer of the free version travels with it (the legacy hand-off finds it by label in the row's versions).
        Equal("f16i", LegacyInstallerFile.Find(entry.License.versions, entry.Versions.First(v => v.Latest).License, "-update.unitypackage").VersionId);

        // Without a device limit the row's own license serves a version both offer.
        var unlimited = new LicenseEntry { license_id = "4740448e", name = "MingToon", rights = commercial.rights, versions = commercial.versions };
        Equal("c15,c15i,f16,f16i", string.Join(",", ManagerCatalog.MergeSamePackage(new[] { free, unlimited }, new Dictionary<string, List<string>>()).Single().versions.Select(v => v.version_id)));

        // A free license with its own device limit keeps its row.
        var limited = new LicenseEntry { license_id = "free-core", name = "MingToon (Free)", max_devices = 2, versions = free.versions };
        Equal(2, ManagerCatalog.Build(null, new[] { limited, commercial }, p => "", null).Count);
        // A single license is passed through as it is.
        var single = ManagerCatalog.MergeSamePackage(new[] { other }, new Dictionary<string, List<string>>());
        Require(ReferenceEquals(single.Single(), other));
    }

    // ------------------------------------------------------------ G4

    private static void Order()
    {
        Equal("1,0,3,2", string.Join(",", ImportRequestLogic.InstallOrder(new[] { Core, Urp, "", "com.studioraming.mlc" })));
        Equal("0,1", string.Join(",", ImportRequestLogic.InstallOrder(new[] { Core, "com.studioraming.mingtoonx" })));
        Equal("0", string.Join(",", ImportRequestLogic.InstallOrder(new[] { Core })));
        // The core's own step is skipped once an add-on of it was installed in the run (it found or brought the version it pins).
        Require(ImportRequestLogic.ExtendsAny(Core, new[] { Urp }));
        Require(!ImportRequestLogic.ExtendsAny(Core, new[] { "com.studioraming.mingtoonx", Core }));
        Require(!ImportRequestLogic.ExtendsAny(Urp, new[] { Core }));
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
