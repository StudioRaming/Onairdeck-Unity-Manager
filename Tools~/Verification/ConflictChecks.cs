using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// The standing project check (U3): what it finds in a temp project, what a fix may move, and that every fix can be undone.
internal static class ConflictChecks
{
    public static void Run(Action<string, Action> check, string project)
    {
        check("project check: an Assets copy of a package's files is one grouped duplicate-GUID conflict; the whole copy folder moves, or only the copies when a user file sits beside them", DuplicateGuids(project));
        check("project check: an old product copy found by its marker is reported once, beside its package; a guarded or blocked item is never offered for a move", OldCopy(project));
        check("project check: two assemblies with one name (asmdef and managed DLL); native DLLs of two platforms and Editor-disabled DLLs are not conflicts", DuplicateAssemblies(project));
        check("project check: a package both embedded and in manifest.json (not as file: of itself) is found; removing the entry keeps CRLF, order and every other entry, and undo puts it back", EmbeddedAndManifest(project));
        check("project check: dependency text edits (first, middle, last, only entry; a key-looking text inside another value stays)", DependencyEdits);
        check("project check: move to backup, undo and keep (the backup stays, the undo offer goes)", MoveUndoKeep(project));
        check("project check: plugin meta Editor setting (explicit Editor, Any, Exclude Editor, missing meta, the Editor's OS and CPU)", EditorSetting);
        check("project check: a cached package Unity did not resolve is no conflict partner; a resolved one is", CachedPackages(project));
        check("project check: an edit that keeps a file's size still stops a move; a fix runs on a fresh check that refuses a vanished partner", EditsAndRecheck(project));
        check("project check: a fix re-checks what it touches (a user file added to a checked folder, a changed manifest entry or a gone embedded package refuse and change nothing)", StaleFixes(project));
    }

    private static string NewProject(string parent, string name)
    {
        var root = Path.Combine(parent, "conflicts-" + name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        Directory.CreateDirectory(Path.Combine(root, "Packages"));
        return root;
    }

    private static string Guid32() { return Guid.NewGuid().ToString("N"); }

    private static void Asset(string root, string relative, string guid, string text = "x")
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full));
        File.WriteAllText(full, text);
        File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\n");
    }

    private static void Folder(string root, string relative, string guid)
    {
        var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        File.WriteAllText(full + ".meta", "fileFormatVersion: 2\nguid: " + guid + "\nfolderAsset: yes\n");
    }

    private static void Package(string root, string folder, string name, string version)
    {
        var full = Path.Combine(root, "Packages", folder);
        Directory.CreateDirectory(full);
        File.WriteAllText(Path.Combine(full, "package.json"), "{\"name\":\"" + name + "\",\"version\":\"" + version + "\"}");
    }

    private static List<ProjectConflict> Scan(string root, Dictionary<string, string> licenses = null, List<string> resolvedCache = null)
    {
        var input = new ConflictScanInput { ProjectRoot = root, ResolvedCacheFolders = resolvedCache ?? new List<string>() };
        if (licenses != null) input.LicenseOfPackage = licenses;
        return ProjectConflictScan.Run(input, CancellationToken.None);
    }

    private static Action DuplicateGuids(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "guid");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            string g1 = Guid32(), g2 = Guid32(), gf = Guid32();
            Folder(root, "Packages/com.seller.tool/Runtime", gf);
            Asset(root, "Packages/com.seller.tool/Runtime/A.mat", g1);
            Asset(root, "Packages/com.seller.tool/Runtime/B.mat", g2);
            Folder(root, "Assets/Old", Guid32());
            Folder(root, "Assets/Old/Tool", gf);
            Asset(root, "Assets/Old/Tool/A.mat", g1);
            Asset(root, "Assets/Old/Tool/B.mat", g2);
            Asset(root, "Assets/Mine.mat", Guid32());

            var found = Scan(root).Where(c => c.Kind == ConflictKind.DuplicateGuid).ToList();
            Equal(1, found.Count);
            Equal(3, found[0].Count);
            Equal("com.seller.tool", found[0].Package);
            Equal("Assets/Old/Tool", string.Join(",", found[0].Movable));
            Require(!found[0].NeedsReinstall);

            // A file of the user's own beside the copies: only the copies move.
            Asset(root, "Assets/Old/Tool/mine.txt", Guid32());
            found = Scan(root).Where(c => c.Kind == ConflictKind.DuplicateGuid).ToList();
            Equal("Assets/Old/Tool/A.mat,Assets/Old/Tool/B.mat", string.Join(",", found[0].Movable.OrderBy(p => p, StringComparer.Ordinal)));

            // Copies inside two package folders: only a reinstall helps, nothing under Assets moves.
            var other = NewProject(parent, "guid2");
            Package(other, "com.seller.tool", "com.seller.tool", "1.0.0");
            Package(other, "com.other.core", "com.other.core", "2.0.0");
            var g = Guid32();
            Asset(other, "Packages/com.seller.tool/A.mat", g);
            Asset(other, "Packages/com.other.core/A.mat", g);
            found = Scan(other).Where(c => c.Kind == ConflictKind.DuplicateGuid).ToList();
            Equal(1, found.Count);
            Require(found[0].NeedsReinstall && found[0].Movable.Count == 0);
        };
    }

    private static Action OldCopy(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "old");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            Folder(root, "Assets/Seller", Guid32());
            Folder(root, "Assets/Seller/Tool", Guid32());
            Asset(root, "Assets/Seller/Tool/Tool.cs", Guid32());
            File.WriteAllText(Path.Combine(root, "Assets", "Seller", "Tool", ProductMarkers.FileName), "{\"license_id\":\"lic-1\"}");

            Equal(0, Scan(root).Count(c => c.Kind == ConflictKind.OldCopyBesidePackage));   // no license: not the account's product
            var found = Scan(root, new Dictionary<string, string> { { "com.seller.tool", "lic-1" } }).Where(c => c.Kind == ConflictKind.OldCopyBesidePackage).ToList();
            Equal(1, found.Count);
            Equal("com.seller.tool", found[0].Package);
            Require(found[0].Paths.Contains("Assets/Seller/Tool"));
            Require(found[0].Paths.Contains("Packages/com.seller.tool"));
            // Whatever is offered for a move passes the install's own guard rails; the rest needs a reinstall.
            foreach (var path in found[0].Movable)
            {
                string relative;
                Require(LegacyInstall.TryGuard(root, path, out relative));
            }
            Require(found[0].Movable.Count > 0 || found[0].NeedsReinstall);
        };
    }

    private static Action DuplicateAssemblies(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "asm");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            Directory.CreateDirectory(Path.Combine(root, "Packages", "com.seller.tool", "Runtime"));
            File.WriteAllText(Path.Combine(root, "Packages", "com.seller.tool", "Runtime", "Tool.asmdef"), "{\n  \"name\": \"Seller.Tool\"\n}");
            var managed = typeof(ConflictChecks).Assembly.Location;
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Plugins", "x86"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Plugins", "x86_64"));
            File.Copy(managed, Path.Combine(root, "Assets", "Plugins", "Seller.Tool.dll"));
            // Native plugins of two platforms share a name on purpose.
            var native = new byte[512]; native[0] = (byte)'M'; native[1] = (byte)'Z';
            File.WriteAllBytes(Path.Combine(root, "Assets", "Plugins", "x86", "Native.dll"), native);
            File.WriteAllBytes(Path.Combine(root, "Assets", "Plugins", "x86_64", "Native.dll"), native);
            // A managed DLL turned off for the Editor does not clash in the Editor.
            File.Copy(managed, Path.Combine(root, "Assets", "Plugins", "x86", "Player.dll"));
            File.Copy(managed, Path.Combine(root, "Assets", "Plugins", "x86_64", "Player.dll"));
            File.WriteAllText(Path.Combine(root, "Assets", "Plugins", "x86", "Player.dll.meta"), "PluginImporter:\n  platformData:\n  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n");

            Require(ProjectConflictScan.IsManagedAssembly(managed));
            Require(!ProjectConflictScan.IsManagedAssembly(Path.Combine(root, "Assets", "Plugins", "x86", "Native.dll")));
            var found = Scan(root).Where(c => c.Kind == ConflictKind.DuplicateAssembly).ToList();
            Equal(1, found.Count);
            Equal("Seller.Tool", found[0].Subject);
            Equal("com.seller.tool", found[0].Package);
            Equal("Assets/Plugins/Seller.Tool.dll", string.Join(",", found[0].Movable));
        };
    }

    private static Action EmbeddedAndManifest(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "man");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            Package(root, "com.x", "com.x", "0.1.0");
            var manifest = Path.Combine(root, "Packages", "manifest.json");
            var original = "{\r\n  \"dependencies\": {\r\n    \"com.unity.ugui\": \"1.0.0\",\r\n    \"com.seller.tool\": \"https://example.invalid/tool.git#1.0.0\",\r\n    \"com.x\": \"file:com.x\"\r\n  },\r\n  \"testables\": [\"com.seller.tool\"]\r\n}\r\n";
            File.WriteAllText(manifest, original);

            var found = Scan(root).Where(c => c.Kind == ConflictKind.EmbeddedAndManifest).ToList();
            Equal(1, found.Count);
            Equal("com.seller.tool", found[0].Package);
            Equal("https://example.invalid/tool.git#1.0.0", found[0].ManifestValue);

            var removal = ProjectConflictFixes.RemoveManifestEntry(root, "com.seller.tool", new DateTime(2026, 10, 31, 12, 0, 0));
            var edited = File.ReadAllText(manifest);
            Equal("{\r\n  \"dependencies\": {\r\n    \"com.unity.ugui\": \"1.0.0\",\r\n    \"com.x\": \"file:com.x\"\r\n  },\r\n  \"testables\": [\"com.seller.tool\"]\r\n}\r\n", edited);
            Equal(original, File.ReadAllText(Path.Combine(removal.Folder, "manifest.json")));
            Equal(1, ProjectConflictFixes.PendingRemovals(root).Count);
            Equal(0, Scan(root).Count(c => c.Kind == ConflictKind.EmbeddedAndManifest));

            ProjectConflictFixes.RestoreManifestEntry(root, ProjectConflictFixes.PendingRemovals(root)[0]);
            var deps = MiniJson.ParseObject(File.ReadAllText(manifest))["dependencies"] as Dictionary<string, object>;
            Equal("https://example.invalid/tool.git#1.0.0", deps["com.seller.tool"]);
            Equal(3, deps.Count);
            Require(File.ReadAllText(manifest).Contains("\r\n") && !File.ReadAllText(manifest).Replace("\r\n", "").Contains("\n"));
            Equal(0, ProjectConflictFixes.PendingRemovals(root).Count);
            // An undo that finds the entry back already refuses and changes nothing.
            var again = ProjectConflictFixes.RemoveManifestEntry(root, "com.seller.tool", new DateTime(2026, 10, 31, 12, 1, 0));
            ProjectConflictFixes.RestoreManifestEntry(root, again);
            var before = File.ReadAllText(manifest);
            try { ProjectConflictFixes.RestoreManifestEntry(root, again); throw new Exception("a second undo was accepted"); }
            catch (PackageInstallException) { }
            Equal(before, File.ReadAllText(manifest));
        };
    }

    private static void DependencyEdits()
    {
        string value;
        Equal("{\n  \"dependencies\": {\n    \"b\": \"2\"\n  }\n}", ProjectConflictFixes.RemoveDependency("{\n  \"dependencies\": {\n    \"a\": \"1\",\n    \"b\": \"2\"\n  }\n}", "a", out value));
        Equal("1", value);
        Equal("{\n  \"dependencies\": {\n    \"a\": \"1\"\n  }\n}", ProjectConflictFixes.RemoveDependency("{\n  \"dependencies\": {\n    \"a\": \"1\",\n    \"b\": \"2\"\n  }\n}", "b", out value));
        Equal("{\n  \"dependencies\": {\n    \"a\": \"1\",\n    \"c\": \"3\"\n  }\n}", ProjectConflictFixes.RemoveDependency("{\n  \"dependencies\": {\n    \"a\": \"1\",\n    \"b\": \"2\",\n    \"c\": \"3\"\n  }\n}", "b", out value));
        Equal("{\"dependencies\": {}}", ProjectConflictFixes.RemoveDependency("{\"dependencies\": {\"a\": \"1\"}}", "a", out value));
        Equal(null, ProjectConflictFixes.RemoveDependency("{\"dependencies\": {\"a\": \"has \\\"b\\\": \\\"x\\\" inside\"}}", "b", out value));
        Equal(null, ProjectConflictFixes.RemoveDependency("{\"scopedRegistries\": [{\"name\": \"a\"}], \"dependencies\": {\"b\": \"1\"}}", "a", out value));
        var inserted = ProjectConflictFixes.InsertDependency("{\n  \"dependencies\": {\n    \"b\": \"2\"\n  }\n}", "a", "1");
        Equal("{\n  \"dependencies\": {\n    \"a\": \"1\",\n    \"b\": \"2\"\n  }\n}", inserted);
        var intoEmpty = ProjectConflictFixes.InsertDependency("{\n  \"dependencies\": {}\n}", "a", "1");
        Equal("1", (MiniJson.ParseObject(intoEmpty)["dependencies"] as Dictionary<string, object>)["a"]);
    }

    private static Action MoveUndoKeep(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "move");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            string g1 = Guid32(), gf = Guid32();
            Folder(root, "Packages/com.seller.tool/Runtime", gf);
            Asset(root, "Packages/com.seller.tool/Runtime/A.mat", g1);
            Folder(root, "Assets/Copy", gf);
            Asset(root, "Assets/Copy/A.mat", g1, "copy");

            var conflict = Scan(root).Single(c => c.Kind == ConflictKind.DuplicateGuid);
            ProjectConflictFixes.MoveToBackup(root, conflict, new DateTime(2026, 10, 31, 13, 0, 0));
            Require(!Directory.Exists(Path.Combine(root, "Assets", "Copy")) && !File.Exists(Path.Combine(root, "Assets", "Copy.meta")));
            Equal(0, Scan(root).Count);
            var pending = ProjectConflictFixes.PendingMoves(root);
            Equal(1, pending.Count);
            Equal("Assets/Copy", string.Join(",", pending[0].Paths));

            var result = LegacyMove.RestoreFromManifest(pending[0].ManifestPath, root);
            Require(result.Ok);
            Equal("copy", File.ReadAllText(Path.Combine(root, "Assets", "Copy", "A.mat")));
            Equal(0, ProjectConflictFixes.PendingMoves(root).Count);

            conflict = Scan(root).Single(c => c.Kind == ConflictKind.DuplicateGuid);
            ProjectConflictFixes.MoveToBackup(root, conflict, new DateTime(2026, 10, 31, 13, 5, 0));
            pending = ProjectConflictFixes.PendingMoves(root);
            Require(ProjectConflictFixes.KeepMove(pending[0].ManifestPath));
            Equal(0, ProjectConflictFixes.PendingMoves(root).Count);
            Require(Directory.GetFiles(Path.GetDirectoryName(pending[0].ManifestPath), "A.mat", SearchOption.AllDirectories).Length == 1);

            // Nothing to move is refused, and a path outside the guard rails is never moved.
            try { ProjectConflictFixes.MoveToBackup(root, new ProjectConflict(), DateTime.Now); throw new Exception("an empty move was accepted"); }
            catch (PackageInstallException) { }
            var outside = new ProjectConflict();
            outside.Movable.Add("Packages/com.seller.tool/Runtime/A.mat");
            try { ProjectConflictFixes.MoveToBackup(root, outside, DateTime.Now); throw new Exception("a package file was moved"); }
            catch (PackageInstallException) { }
            Require(File.Exists(Path.Combine(root, "Packages", "com.seller.tool", "Runtime", "A.mat")));
        };
    }

    private static void EditorSetting()
    {
        Require(ProjectConflictScan.EditorEnabled(""));
        Require(!ProjectConflictScan.EditorEnabled("platformData:\n  - first:\n      Editor: Editor\n    second:\n      enabled: 0\n"));
        Require(ProjectConflictScan.EditorEnabled("platformData:\r\n  - first:\r\n      Editor: Editor\r\n    second:\r\n      enabled: 1\r\n"));
        Require(!ProjectConflictScan.EditorEnabled("platformData:\n  - first:\n      Any: \n    second:\n      enabled: 0\n"));
        Require(!ProjectConflictScan.EditorEnabled("  - first:\n      Any: \n    second:\n      enabled: 1\n      settings:\n        Exclude Editor: 1\n"));
        const string editorBlock = "platformData:\n  - first:\n      Editor: Editor\n    second:\n      enabled: 1\n      settings:\n        CPU: {cpu}\n        DefaultValueInitialized: true\n        OS: {os}\n  - first:\n      Standalone: Linux64\n    second:\n      enabled: 1\n      settings:\n        CPU: None\n";
        Require(ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "AnyCPU").Replace("{os}", "AnyOS"), "Windows"));
        Require(ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "x86_64").Replace("{os}", "Windows"), "Windows"));
        Require(!ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "AnyCPU").Replace("{os}", "Linux"), "Windows"));
        Require(ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "AnyCPU").Replace("{os}", "Linux"), "Linux"));
        Require(!ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "None").Replace("{os}", "AnyOS"), "Windows"));
        // The next platform's "CPU: None" belongs to that platform, not to the Editor.
        Require(ProjectConflictScan.EditorEnabled(editorBlock.Replace("{cpu}", "AnyCPU").Replace("{os}", "Windows"), "Windows"));
    }

    private static Action CachedPackages(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "cache");
            var cached = Path.Combine(root, "Library", "PackageCache", "com.vendor.tool@1.0.0");
            Directory.CreateDirectory(cached);
            File.WriteAllText(Path.Combine(cached, "package.json"), "{\"name\":\"com.vendor.tool\",\"version\":\"1.0.0\"}");
            File.Copy(typeof(ConflictChecks).Assembly.Location, Path.Combine(cached, "Tool.dll"));
            Directory.CreateDirectory(Path.Combine(root, "Assets", "Plugins"));
            File.Copy(typeof(ConflictChecks).Assembly.Location, Path.Combine(root, "Assets", "Plugins", "Tool.dll"));

            Equal(0, Scan(root).Count(c => c.Kind == ConflictKind.DuplicateAssembly));
            var found = Scan(root, null, new List<string> { cached }).Where(c => c.Kind == ConflictKind.DuplicateAssembly).ToList();
            Equal(1, found.Count);
            Equal("Assets/Plugins/Tool.dll", string.Join(",", found[0].Movable));
            // A resolved path outside the cache (a local package elsewhere) is not read.
            Equal(0, Scan(root, null, new List<string> { Path.Combine(parent, "elsewhere") }).Count(c => c.Kind == ConflictKind.DuplicateAssembly));
        };
    }

    private static Action EditsAndRecheck(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "recheck");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            string g1 = Guid32(), gf = Guid32();
            Folder(root, "Packages/com.seller.tool/Runtime", gf);
            Asset(root, "Packages/com.seller.tool/Runtime/A.mat", g1, "color: 0");
            Folder(root, "Assets/Copy", gf);
            Asset(root, "Assets/Copy/A.mat", g1, "color: 0");
            var input = new ConflictScanInput { ProjectRoot = root };

            var shown = Scan(root).Single(c => c.Kind == ConflictKind.DuplicateGuid);
            // Unchanged: the fresh check is the same conflict.
            Equal(shown.MovablePrints[0], ProjectConflictFixes.Recheck(input, shown, CancellationToken.None).MovablePrints[0]);

            // The user edits the copy without changing its size.
            var file = Path.Combine(root, "Assets", "Copy", "A.mat");
            File.WriteAllText(file, "color: 1");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            try { ProjectConflictFixes.MoveToBackup(root, shown, DateTime.Now); throw new Exception("an edited copy was moved"); }
            catch (ConflictChangedException) { }
            Equal("color: 1", File.ReadAllText(file));
            try { ProjectConflictFixes.Recheck(input, shown, CancellationToken.None); throw new Exception("the recheck missed the edit"); }
            catch (ConflictChangedException) { }

            // The package side goes away: the Assets copy is the only one left and must not move.
            shown = Scan(root).Single(c => c.Kind == ConflictKind.DuplicateGuid);
            Directory.Delete(Path.Combine(root, "Packages", "com.seller.tool"), true);
            try { ProjectConflictFixes.Recheck(input, shown, CancellationToken.None); throw new Exception("a conflict without its partner was confirmed"); }
            catch (ConflictChangedException) { }
            Require(File.Exists(file));
            // An unreadable or missing path has no print, so it is never moved.
            Equal("", ProjectConflictScan.Fingerprint(root, "Assets/Nothing"));
        };
    }

    private static Action StaleFixes(string parent)
    {
        return () =>
        {
            var root = NewProject(parent, "stale");
            Package(root, "com.seller.tool", "com.seller.tool", "1.0.0");
            string g1 = Guid32(), gf = Guid32();
            Folder(root, "Packages/com.seller.tool/Runtime", gf);
            Asset(root, "Packages/com.seller.tool/Runtime/A.mat", g1);
            Folder(root, "Assets/Copy", gf);
            Asset(root, "Assets/Copy/A.mat", g1);

            var conflict = Scan(root).Single(c => c.Kind == ConflictKind.DuplicateGuid);
            Equal("Assets/Copy", string.Join(",", conflict.Movable));
            Equal(1, conflict.MovablePrints.Count);
            // The user adds a material of their own to the checked folder before pressing Move.
            Asset(root, "Assets/Copy/Mine.mat", Guid32());
            try { ProjectConflictFixes.MoveToBackup(root, conflict, DateTime.Now); throw new Exception("a stale move went ahead"); }
            catch (ConflictChangedException) { }
            Require(File.Exists(Path.Combine(root, "Assets", "Copy", "Mine.mat")) && File.Exists(Path.Combine(root, "Assets", "Copy", "A.mat")));
            Equal(0, ProjectConflictFixes.PendingMoves(root).Count);

            var manifest = Path.Combine(root, "Packages", "manifest.json");
            File.WriteAllText(manifest, "{\n  \"dependencies\": {\n    \"com.seller.tool\": \"1.0.0\"\n  }\n}\n");
            var entry = Scan(root).Single(c => c.Kind == ConflictKind.EmbeddedAndManifest);
            Equal("Packages/com.seller.tool", entry.EmbeddedFolder);
            File.WriteAllText(manifest, "{\n  \"dependencies\": {\n    \"com.seller.tool\": \"2.0.0\"\n  }\n}\n");
            var before = File.ReadAllText(manifest);
            try { ProjectConflictFixes.RemoveManifestEntry(root, entry.Package, entry.ManifestValue, entry.EmbeddedFolder, DateTime.Now); throw new Exception("a changed entry was removed"); }
            catch (ConflictChangedException) { }
            Equal(before, File.ReadAllText(manifest));
            Directory.Delete(Path.Combine(root, "Packages", "com.seller.tool"), true);
            try { ProjectConflictFixes.RemoveManifestEntry(root, entry.Package, "2.0.0", entry.EmbeddedFolder, DateTime.Now); throw new Exception("an entry was removed although its folder is gone"); }
            catch (ConflictChangedException) { }
            Equal(before, File.ReadAllText(manifest));
            Equal(0, ProjectConflictFixes.PendingRemovals(root).Count);
        };
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
