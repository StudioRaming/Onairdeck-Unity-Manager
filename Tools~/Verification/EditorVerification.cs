using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

// JsonUtility assigns these fields through reflection.
#pragma warning disable 0649

// Copy to the isolated project's Assets/Editor. This does not ship in the manager.
// Invoke Run without -quit: asynchronous requests and imports need editor update ticks.
[InitializeOnLoad]
public static class OnAirDeckEditorVerification
{
    private const string ActiveKey = "OnAirDeck.Verification.Active";
    private const string ReportKey = "OnAirDeck.Verification.Report";
    private const string DeadlineKey = "OnAirDeck.Verification.Deadline";
    private const string ReloadKey = "OnAirDeck.Verification.Reloads";

    [Serializable] private sealed class Fixture
    {
        public string root;
        public string markerType;
        public string textAsset;
        public string[] packages;
    }

    [Serializable] private sealed class Report
    {
        public string unityVersion;
        public string result;
        public string error;
        public string[] checks;
        public int scriptReloads;
    }

    private static string ProjectRoot { get { return Path.GetDirectoryName(Application.dataPath); } }
    private static string WorkRoot { get { return Path.Combine(ProjectRoot, "Library", "OnAirDeckVerification"); } }
    private static string CacheRoot { get { return (string)TypeFor("Downloader").GetProperty("CacheRoot").GetValue(null, null); } }
    private static Fixture LoadFixture() { return JsonUtility.FromJson<Fixture>(File.ReadAllText(Path.Combine(WorkRoot, "fixtures.json"))); }

    static OnAirDeckEditorVerification()
    {
        if (File.Exists(Path.Combine(WorkRoot, "run.request")))
        {
            SessionState.SetBool(ActiveKey, false);
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                Run();
            };
        }
        else if (SessionState.GetBool(ActiveKey, false))
        {
            SessionState.SetInt(ReloadKey, SessionState.GetInt(ReloadKey, 0) + 1);
            EditorApplication.update += PollImports;
        }
    }

    public static async void Run()
    {
        var report = new Report { unityVersion = Application.unityVersion, result = "running" };
        var checks = new List<string>();
        try
        {
            File.WriteAllText(Path.Combine(WorkRoot, "results.json"), JsonUtility.ToJson(report, true));
            var fixture = LoadFixture();
            Require(fixture.root.StartsWith("Assets/StudioRaming_Onairdeck/__Verification_", StringComparison.Ordinal), "Unexpected fixture destination");
            PreparePackages(fixture);
            var absolute = Path.Combine(ProjectRoot, fixture.root);
            Directory.CreateDirectory(absolute);
            var source = Path.Combine(WorkRoot, "payload.txt");
            File.WriteAllText(source, "OnAirDeck loose file verification");
            var copied = (string)Call("Installer", "CopyFile", source, fixture.root, "payload.txt");
            Require(File.Exists(Path.Combine(ProjectRoot, copied)), "Image copy missing");
            checks.Add("Loose file copied to the new root in the real Unity project");

            var zipPath = Path.Combine(WorkRoot, "fixture.zip");
            using (var stream = File.Create(zipPath))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                foreach (var entry in new[] { "nested/valid.txt", "../escape.txt", "/absolute.txt", "C:/drive.txt" })
                    using (var writer = new StreamWriter(zip.CreateEntry(entry).Open())) writer.Write(entry);
            var zipArgs = new object[] { zipPath, fixture.root, 0 };
            Require((int)Call("Installer", "ExtractZip", zipArgs) == 1 && (int)zipArgs[2] == 3, "ZIP extraction/unsafe-entry counts differ");
            checks.Add("ZIP extraction skips traversal, rooted and drive entries");

            await CheckRequest(false, false);
            checks.Add("UnityWebRequest downloads the expected bytes");
            await CheckRequest(true, false);
            checks.Add("Cancellation removes the incomplete file and cache folder");
            await CheckRequest(false, true);
            checks.Add("HTTP failure removes its cache folder");

            Require(PendingCount() == 0, "A package import was already queued");
            var request = Path.Combine(WorkRoot, "run.request");
            if (File.Exists(request)) File.Delete(request);
            report.checks = checks.ToArray();
            SessionState.SetString(ReportKey, JsonUtility.ToJson(report));
            SessionState.SetBool(ActiveKey, true);
            SessionState.SetInt(ReloadKey, 0);
            SessionState.SetFloat(DeadlineKey, (float)(EditorApplication.timeSinceStartup + 180));
            EditorApplication.update += PollImports;
            Call("PackageImportQueue", "Enqueue", fixture.packages, false);
        }
        catch (Exception e)
        {
            report.checks = checks.ToArray();
            Fail(report, e);
        }
    }

    private static async Task CheckRequest(bool cancel, bool fail)
    {
        var before = Directory.Exists(CacheRoot) ? Directory.GetDirectories(CacheRoot).Length : 0;
        var payload = new byte[cancel ? 4 * 1024 * 1024 : 32768];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);
        using (var server = new LoopbackServer(payload, cancel, fail))
        using (var cancellation = new CancellationTokenSource())
        {
            if (cancel) cancellation.CancelAfter(300);
            var task = (Task<string>)Call("Downloader", "DownloadAsync", server.Url, "payload.bin", null, cancellation.Token);
            string path = null;
            Exception error = null;
            try { path = await task; }
            catch (Exception e) { error = e; }
            if (cancel) Require(error is OperationCanceledException, "Cancellation was not observed");
            else if (fail) Require(error != null, "HTTP failure was accepted");
            else
            {
                Require(error == null && path != null, "Download failed: " + error);
                Require(Convert.ToBase64String(File.ReadAllBytes(path)) == Convert.ToBase64String(payload), "Downloaded bytes differ");
                Call("Downloader", "TryDeleteFolder", Path.GetDirectoryName(path));
            }
        }
        Require((Directory.Exists(CacheRoot) ? Directory.GetDirectories(CacheRoot).Length : 0) == before, "Download left a cache folder behind");
    }

    private static void PollImports()
    {
        if (!SessionState.GetBool(ActiveKey, false)) { EditorApplication.update -= PollImports; return; }
        var report = JsonUtility.FromJson<Report>(SessionState.GetString(ReportKey, ""));
        try
        {
            if (EditorApplication.timeSinceStartup > SessionState.GetFloat(DeadlineKey, 0)) throw new Exception("Import verification timed out");
            if (PendingCount() != 0 || EditorApplication.isCompiling || EditorApplication.isUpdating) return;
            var fixture = LoadFixture();
            if (Type.GetType(fixture.markerType + ", Assembly-CSharp-Editor", false) == null) return;
            Require(File.Exists(Path.Combine(ProjectRoot, fixture.textAsset)), "Second queued package was not imported");
            Require(File.ReadAllText(Path.Combine(ProjectRoot, fixture.textAsset)) == "OnAirDeck package queue verification\n", "Second package contents differ");
            report.scriptReloads = SessionState.GetInt(ReloadKey, 0);
            Require(report.scriptReloads > 0, "No script reload occurred during queued imports");
            foreach (var package in fixture.packages) Require(!Directory.Exists(Path.GetDirectoryName(package)), "Completed import cache remains");
            var checks = new List<string>(report.checks);
            checks.Add("Two queued packages imported, including a script that reloaded the editor domain");
            checks.Add("Import queue emptied and deleted completed package caches");
            report.checks = checks.ToArray();
            report.result = "passed";
            Finish(report, 0);
        }
        catch (Exception e) { Fail(report, e); }
    }

    private static void PreparePackages(Fixture fixture)
    {
        var exportRoot = fixture.root + "/_Export";
        Directory.CreateDirectory(Path.Combine(ProjectRoot, exportRoot));
        var scriptSource = exportRoot + "/marker.txt";
        var textSource = exportRoot + "/after-reload.txt";
        var markerName = fixture.markerType.Substring(fixture.markerType.LastIndexOf('.') + 1);
        File.WriteAllText(Path.Combine(ProjectRoot, scriptSource), "namespace OnAirDeckVerificationFixtures { public static class " + markerName + " { } }\n");
        File.WriteAllText(Path.Combine(ProjectRoot, textSource), "OnAirDeck package queue verification\n");
        AssetDatabase.ImportAsset(scriptSource, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.ImportAsset(textSource, ImportAssetOptions.ForceSynchronousImport);
        foreach (var package in fixture.packages) Directory.CreateDirectory(Path.GetDirectoryName(package));
        AssetDatabase.ExportPackage(scriptSource, fixture.packages[0], ExportPackageOptions.Default);
        AssetDatabase.ExportPackage(textSource, fixture.packages[1], ExportPackageOptions.Default);
        // Export text first so preparation itself does not compile scripts. Preserve Unity's
        // TAR headers and only change the target path/importer of the script fixture.
        RewritePackage(fixture.packages[0], fixture.root + "/Editor/" + markerName + ".cs", true);
        RewritePackage(fixture.packages[1], fixture.textAsset, false);
        Require(AssetDatabase.DeleteAsset(exportRoot), "Could not remove temporary export sources");
    }

    private static void RewritePackage(string path, string assetPath, bool script)
    {
        byte[] tar;
        using (var input = File.OpenRead(path))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        using (var buffer = new MemoryStream()) { gzip.CopyTo(buffer); tar = buffer.ToArray(); }
        using (var output = new MemoryStream())
        {
            var position = 0;
            while (position + 512 <= tar.Length && tar[position] != 0)
            {
                var header = new byte[512];
                Array.Copy(tar, position, header, 0, 512);
                var name = System.Text.Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                var size = Convert.ToInt32(System.Text.Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' '), 8);
                var data = new byte[size];
                Array.Copy(tar, position + 512, data, 0, size);
                position += 512 + ((size + 511) / 512) * 512;
                if (name.EndsWith("/pathname", StringComparison.Ordinal)) data = System.Text.Encoding.UTF8.GetBytes(assetPath);
                else if (script && name.EndsWith("/asset.meta", StringComparison.Ordinal))
                    data = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(data).Replace("TextScriptImporter:", "MonoImporter:"));
                var sizeField = System.Text.Encoding.ASCII.GetBytes(Convert.ToString(data.Length, 8).PadLeft(11, '0') + "\0");
                Array.Copy(sizeField, 0, header, 124, 12);
                for (var i = 148; i < 156; i++) header[i] = (byte)' ';
                var checksum = 0;
                foreach (var value in header) checksum += value;
                var checksumField = System.Text.Encoding.ASCII.GetBytes(Convert.ToString(checksum, 8).PadLeft(6, '0') + "\0 ");
                Array.Copy(checksumField, 0, header, 148, 8);
                output.Write(header, 0, header.Length);
                output.Write(data, 0, data.Length);
                var padding = new byte[(512 - data.Length % 512) % 512];
                output.Write(padding, 0, padding.Length);
            }
            output.Write(new byte[1024], 0, 1024);
            using (var file = File.Create(path))
            using (var gzip = new GZipStream(file, CompressionMode.Compress))
            {
                var data = output.ToArray();
                gzip.Write(data, 0, data.Length);
            }
        }
    }

    private static int PendingCount() { return (int)TypeFor("PackageImportQueue").GetProperty("PendingCount").GetValue(null, null); }
    private static Type TypeFor(string name) { return Type.GetType("OnAirDeck.UnityManager." + name + ", OnAirDeck.UnityManager.Editor", true); }
    private static object Call(string type, string method, params object[] args) { return TypeFor(type).GetMethod(method, BindingFlags.Public | BindingFlags.Static).Invoke(null, args); }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Fail(Report report, Exception e) { report.result = "failed"; report.error = e.ToString(); Finish(report, 1); }
    private static void Finish(Report report, int code)
    {
        SessionState.SetBool(ActiveKey, false);
        var request = Path.Combine(WorkRoot, "run.request");
        if (File.Exists(request)) File.Delete(request);
        EditorApplication.update -= PollImports;
        File.WriteAllText(Path.Combine(WorkRoot, "results.json"), JsonUtility.ToJson(report, true));
        if (code == 0) Debug.Log("OnAirDeck verification passed.");
        else Debug.LogError(report.error);
        if (Application.isBatchMode) EditorApplication.Exit(code);
    }

    private sealed class LoopbackServer : IDisposable
    {
        private readonly HttpListener _listener;
        public readonly string Url;

        public LoopbackServer(byte[] payload, bool slow, bool fail)
        {
            var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            Url = "http://localhost:" + port + "/payload";
            _listener = new HttpListener();
            _listener.Prefixes.Add("http://localhost:" + port + "/");
            _listener.Start();
            Task.Run(async () =>
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    context.Response.StatusCode = fail ? 503 : 200;
                    context.Response.ContentLength64 = fail ? 0 : payload.Length;
                    if (!fail)
                        for (var offset = 0; offset < payload.Length; offset += 8192)
                        {
                            await context.Response.OutputStream.WriteAsync(payload, offset, Math.Min(8192, payload.Length - offset));
                            if (slow) await Task.Delay(20);
                        }
                    context.Response.Close();
                }
                catch (Exception) { } // Abort/dispose closes the deliberately slow response.
            });
        }
        public void Dispose() { _listener.Close(); }
    }
}
