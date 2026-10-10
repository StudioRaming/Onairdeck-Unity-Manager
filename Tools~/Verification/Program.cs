using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using OnAirDeck.UnityManager;

internal static class Program
{
    private static string _project;
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        _project = Path.Combine(Path.GetTempPath(), "onairdeck-verification-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_project, "Assets"));
        UnityEngine.Application.dataPath = Path.Combine(_project, "Assets");
        try
        {
            Check("download destination", () => Equal("Assets/StudioRaming_Onairdeck/test_sell", Installer.ProductFolder("test_sell")));
            Check("safe product folder", () => Equal("Assets/StudioRaming_Onairdeck/test_sell", Installer.ProductFolder("../../test_sell")));
            Check("empty product fallback", () => Equal("Assets/StudioRaming_Onairdeck/Product", Installer.ProductFolder("..")));
            Check("loose file contents and destination", CopyImage);
            Check("loose overwrite preserves Unity metadata", CopyPreservesMeta);
            Check("loose file traversal is refused", RefuseLooseTraversal);
            Check("ZIP nested files and Finder metadata", ExtractNestedZip);
            Check("ZIP traversal and drive entries are skipped", SkipTraversalZip);
            Check("ZIP rooted entries are skipped", SkipRootedZip);
            Check("ZIP overwrite preserves Unity metadata", ZipPreservesMeta);
            Check("sibling prefix is outside product folder", () => Require(!PathSafety.IsInside(Path.Combine(_project, "product"), Path.Combine(_project, "product-other", "file.txt"))));
            Check("case-different sibling follows filesystem case rules", CaseSiblingFollowsFilesystemCase);
            Check("separator normalization", () => Equal("nested/file.txt", PathSafety.SafeRelativePath("./nested\\file.txt")));
            Check("unusable file names", () => { Equal("", PathSafety.SafeFileName("..")); Equal("", PathSafety.SafeFileName("C:")); });
            Check("file type detection ignores case", () => { Require(Installer.IsZip("fixture.ZIP")); Require(Installer.IsUnityPackage("fixture.UNITYPACKAGE")); Require(!Installer.IsZip("fixture.jpg")); });
            AuthChecks.Run(Check);
        }
        finally
        {
            // Only delete the fresh directory owned by this run, directly inside the temp folder.
            var full = Path.GetFullPath(_project);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(Path.GetDirectoryName(full), temp, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("onairdeck-verification-", StringComparison.Ordinal))
                Directory.Delete(full, true);
        }
        Console.WriteLine("Verification checks: " + _passed + " passed, " + _failed + " failed.");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action test)
    {
        try { test(); _passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { _failed++; Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }

    private static void CaseSiblingFollowsFilesystemCase()
    {
        var root = Path.Combine(_project, "owned-cache");
        var recased = Path.Combine(_project, "OWNED-CACHE");
        Require(PathSafety.IsInside(root, Path.Combine(root, "payload.txt")));
        // Same folder on case-insensitive volumes (Windows, macOS); a different folder on Linux.
        bool caseInsensitive = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        Require(PathSafety.IsInside(root, Path.Combine(recased, "payload.txt")) == caseInsensitive);
        // A real sibling folder is outside the owned root on every platform.
        Require(!PathSafety.IsInside(root, Path.Combine(_project, "owned-cache-other", "payload.txt")));
        Require(!PathSafety.IsInside(root, Path.Combine(root, "..", "owned-cache-other", "payload.txt")));
        Require(!PathSafety.IsInside(root, root));
    }

    private static void CopyImage()
    {
        var source = Path.Combine(_project, "fixture.jpg");
        File.WriteAllBytes(source, new byte[] { 255, 216, 1, 2, 3, 255, 217 });
        var path = Installer.CopyFile(source, Installer.ProductFolder("image"), "fixture.jpg");
        Equal("Assets/StudioRaming_Onairdeck/image/fixture.jpg", path);
        Equal(Convert.ToBase64String(File.ReadAllBytes(source)), Convert.ToBase64String(File.ReadAllBytes(Absolute(path))));
    }

    private static void CopyPreservesMeta()
    {
        var folder = Installer.ProductFolder("overwrite");
        var source = Path.Combine(_project, "payload.txt");
        File.WriteAllText(source, "first");
        var path = Installer.CopyFile(source, folder, "payload.txt");
        File.WriteAllText(Absolute(path) + ".meta", "existing-guid");
        File.WriteAllText(source, "second");
        Installer.CopyFile(source, folder, "payload.txt");
        Equal("second", File.ReadAllText(Absolute(path)));
        Equal("existing-guid", File.ReadAllText(Absolute(path) + ".meta"));
    }

    private static void RefuseLooseTraversal()
    {
        var source = Path.Combine(_project, "source.txt");
        File.WriteAllText(source, "payload");
        var rejected = false;
        try { Installer.CopyFile(source, Installer.ProductFolder("loose"), "../escape.txt"); }
        catch (Exception) { rejected = true; }
        Require(rejected);
        Require(!File.Exists(Absolute(Installer.InstallRoot + "/escape.txt")));
    }

    private static void ExtractNestedZip()
    {
        var zip = MakeZip("nested.zip", new[] { "nested/file.txt", "root.txt", "__MACOSX/._root.txt" });
        int skipped;
        var folder = Installer.ProductFolder("nested");
        Equal(2, Installer.ExtractZip(zip, folder, out skipped));
        Equal(0, skipped);
        Equal("nested/file.txt", File.ReadAllText(Absolute(folder + "/nested/file.txt")));
        Require(!Directory.Exists(Absolute(folder + "/__MACOSX")));
    }

    private static void SkipTraversalZip()
    {
        var zip = MakeZip("traversal.zip", new[] { "valid.txt", "../escape.txt", "nested/../../escape.txt", "..\\escape.txt", "C:/drive.txt" });
        int skipped;
        var folder = Installer.ProductFolder("traversal");
        Equal(1, Installer.ExtractZip(zip, folder, out skipped));
        Equal(4, skipped);
        Equal(1, Directory.GetFiles(Absolute(folder), "*", SearchOption.AllDirectories).Length);
        Require(!File.Exists(Absolute(Installer.InstallRoot + "/escape.txt")));
    }

    private static void SkipRootedZip()
    {
        var zip = MakeZip("rooted.zip", new[] { "valid.txt", "/absolute.txt", "\\absolute.txt" });
        int skipped;
        Equal(1, Installer.ExtractZip(zip, Installer.ProductFolder("rooted"), out skipped));
        Equal(2, skipped);
    }

    private static void ZipPreservesMeta()
    {
        var folder = Installer.ProductFolder("zip-overwrite");
        Directory.CreateDirectory(Absolute(folder));
        File.WriteAllText(Absolute(folder + "/payload.txt"), "old");
        File.WriteAllText(Absolute(folder + "/payload.txt.meta"), "existing-guid");
        int skipped;
        Installer.ExtractZip(MakeZip("overwrite.zip", new[] { "payload.txt" }), folder, out skipped);
        Equal("payload.txt", File.ReadAllText(Absolute(folder + "/payload.txt")));
        Equal("existing-guid", File.ReadAllText(Absolute(folder + "/payload.txt.meta")));
    }

    private static string MakeZip(string name, string[] entries)
    {
        var path = Path.Combine(_project, name);
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            foreach (var nameInZip in entries)
                using (var writer = new StreamWriter(zip.CreateEntry(nameInZip).Open())) writer.Write(nameInZip);
        return path;
    }

    private static string Absolute(string relative) { return Path.Combine(_project, relative.Replace('/', Path.DirectorySeparatorChar)); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
