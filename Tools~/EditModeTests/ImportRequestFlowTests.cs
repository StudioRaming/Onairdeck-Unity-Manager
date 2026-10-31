using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace OnAirDeck.UnityManager.EditModeTests
{
    /// <summary>
    /// The one-button import end to end in a real Editor, against a loopback fake of plugin-import-requests, plugin-purchases and
    /// plugin-download (contract v1 bodies). Needs the ATELIER_DEV_SERVER define (Assets/csc.rsp in the test project), which points
    /// the Manager at the fake and keeps its sign-in apart from the real one; without it the tests are ignored.
    /// </summary>
    public class ImportRequestFlowTests
    {
        private FakeAtelier _server;
        private string _oldFunctionsUrl;
        private string _product;
        // Every test has a request id of its own: "not this project" and a refused claim skip an id for the editor session.
        private string _id;
        private int _choice;
        private int _dialogs;

        [SetUp]
        public void SetUp()
        {
#if !ATELIER_DEV_SERVER
            Assert.Ignore("Needs the ATELIER_DEV_SERVER define (Assets/csc.rsp: -define:ATELIER_DEV_SERVER).");
#endif
            ImportRequestWatcher.Paused = true;
            _server = new FakeAtelier();
            _oldFunctionsUrl = EditorPrefs.GetString(DevServer.FunctionsUrlPref, "");
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _server.BaseUrl);
            Assert.IsTrue(DevServer.Redirected, "the dev server override is not active");
            // A throwaway sign-in under the ".Dev" key: the real sign-in is never touched.
            SessionStore.Save(new ExchangeResponse { session_token = "test-token-" + Guid.NewGuid().ToString("N"), user_id = "test-user", email = "test@example.invalid", expires_in = 3600 });
            _product = "ImportProbe " + Letters();
            _id = "r-" + Letters();
            _server.SetRequest(_product, _id);
            DeleteClaimFile();
            _dialogs = 0;
            ImportRequestWatcher.ConfirmDialog = (title, message, ok, cancel, alt) =>
            {
                _dialogs++;
                StringAssert.Contains(_product, title);
                StringAssert.Contains(Path.GetFileName(PackageInstallService.ProjectRoot), message);
                return _choice;
            };
        }

        [TearDown]
        public void TearDown()
        {
            ImportRequestWatcher.ConfirmDialog = EditorUtility.DisplayDialogComplex;
            var close = AtelierManagerApi.CloseWindows;
            if (close != null) close();
            if (_server == null) return;
            SessionStore.Clear();
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _oldFunctionsUrl ?? "");
            _server.Dispose();
            DeleteClaimFile();
            var folder = Installer.ProductFolder(_product);
            if (AssetDatabase.IsValidFolder(folder)) AssetDatabase.DeleteAsset(folder);
            ImportRequestWatcher.Paused = false;
        }

        [UnityTest]
        public IEnumerator Import_DownloadsIntoThisProject_AndReportsDone()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 60);

            Assert.AreEqual(1, _dialogs);
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
            CollectionAssert.AreEqual(new[] { "it-1" }, _server.DownloadItems());
            Assert.IsTrue(File.Exists(Path.Combine(PackageInstallService.ProjectRoot, Installer.ProductFolder(_product), "readme.txt")), "the file did not land in the project");
            Assert.IsNull(ImportRequestWatcher.LoadClaim(), "the record outlived the report");
        }

        [UnityTest]
        public IEnumerator NotThisProject_ReleasesIt_AndThisEditorDoesNotAskAgain()
        {
            _choice = (int)ImportChoice.NotThisProject;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);

            Assert.AreEqual(1, _dialogs);
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "release:" + _id + ":c-1:" }, _server.Posts());
            Assert.AreEqual(2, _server.Lists);
        }

        [UnityTest]
        public IEnumerator Cancel_IsDeclined_AndNothingIsDownloaded()
        {
            _choice = (int)ImportChoice.Cancel;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);

            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "declined:" + _id + ":c-1:" }, _server.Posts());
            Assert.AreEqual(0, _server.Downloads);
        }

        [UnityTest]
        public IEnumerator ARefusedClaim_ShowsNoDialog()
        {
            _server.ClaimStatus = 409;
            _server.ClaimError = "not_available";
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);

            Assert.AreEqual(0, _dialogs);
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::" }, _server.Posts());
            Assert.IsNull(ImportRequestWatcher.LoadClaim());
        }

        [UnityTest]
        public IEnumerator AHeldResultIsPostedFirst_AndKeptWhileTheServerIsDown()
        {
            // As a reload leaves it: the result is known, the POST never finished.
            var claim = new ImportClaim { request_id = "r-9", claim_id = "c-9", phase = ImportRequestLogic.PhaseReport, action = "failed", code = "download_failed", claimed_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
            AtomicFile.Write(ClaimFile(), JsonUtility.ToJson(claim));

            _server.ActionStatus = 503;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);
            Assert.IsNotNull(ImportRequestWatcher.LoadClaim(), "a 503 must keep the record");

            _server.ActionStatus = 200;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);
            CollectionAssert.AreEqual(new[] { "failed:r-9:c-9:download_failed", "failed:r-9:c-9:download_failed" }, _server.Posts());
            Assert.AreEqual(0, _server.Lists, "nothing new is taken while a result waits");
            Assert.IsNull(ImportRequestWatcher.LoadClaim());
            Assert.AreEqual(0, _dialogs);
        }

        [UnityTest]
        public IEnumerator AUnityPackage_IsReportedDoneOnlyAfterItsImport()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            var folder = "Assets/" + _product.Replace(" ", "");
            _server.PackageBytes = ExportProbePackage(folder);
            _server.SetRequest(_product, _id, "probe.unitypackage");
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 60);

            // Queued, not imported yet: nothing but the claim went out.
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::" }, _server.Posts());
            var held = ImportRequestWatcher.LoadClaim();
            Assert.IsNotNull(held);
            Assert.AreEqual(ImportRequestLogic.PhaseWaiting, held.phase);

            var end = EditorApplication.timeSinceStartup + 120;
            var settled = false;
            while (!settled && EditorApplication.timeSinceStartup < end)
            {
                yield return null;
                var settle = ImportRequestWatcher.SettleNowAsync(Token());
                yield return Await(settle, 30);
                settled = settle.Result;
            }
            Assert.IsTrue(settled, "the import never ended");
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
            Assert.IsTrue(File.Exists(Path.Combine(PackageInstallService.ProjectRoot, folder, "probe.txt")), "the package was not imported");
            AssetDatabase.DeleteAsset(folder);
        }

        [UnityTest]
        public IEnumerator ASecondItemOfTheSameProduct_DownloadsThatItem_NotTheListedOne()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            // The list shows one row per product (the first item); the page asked for the other one.
            _server.SetRequest(_product, _id, "readme.txt", true);
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 60);

            CollectionAssert.AreEqual(new[] { "it-2" }, _server.DownloadItems());
            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
        }

        [UnityTest]
        public IEnumerator ALeaseLostWhileTheConfirmWasOpen_InstallsNothing()
        {
            _choice = (int)ImportChoice.Import;
            _server.RenewStatus = 409;
            yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 30);

            CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "renew:" + _id + ":c-1:" }, _server.Posts());
            Assert.AreEqual(0, _server.Downloads);
            Assert.IsNull(ImportRequestWatcher.LoadClaim());
        }

        [UnityTest]
        public IEnumerator AnItemWithSeveralLicenses_InstallsTheAddOnFirst_WithTheCoreItPins()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            var core = "com.qa.importcore" + Letters();
            var addon = core + ".addon";
            _server.AddFile("cv-110", core + "-1.1.0.zip", PackageZipBytes(core, "1.1.0", null, null));
            _server.AddFile("cv-100", core + "-1.0.0.zip", PackageZipBytes(core, "1.0.0", null, null));
            _server.AddFile("av-100", addon + "-1.0.0.zip", PackageZipBytes(addon, "1.0.0", core, "1.0.0"));
            // The core is listed first, as a Founders edition grants it; its newest version is not the one the add-on pins.
            _server.SetLicenseItemRequest(_product, _id,
                FakeAtelier.License("l-core", _product + " Core", FakeAtelier.Version("cv-110", core, "1.1.0"), FakeAtelier.Version("cv-100", core, "1.0.0")),
                FakeAtelier.License("l-addon", _product + " Add-on", FakeAtelier.Version("av-100", addon, "1.0.0")));
            try
            {
                yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 120);

                Assert.AreEqual(1, _dialogs, "one confirm for the whole edition");
                CollectionAssert.AreEqual(new[] { "lv:av-100", "lv:cv-100" }, _server.DownloadItems(), "the add-on first, then the core it pins; the core's own newest is skipped");
                Assert.AreEqual("1.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, addon));
                Assert.AreEqual("1.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, core));
                CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
            }
            finally
            {
                RemovePackages(core, addon);
            }
        }

        [UnityTest]
        public IEnumerator AnItemWithSeveralLicenses_KeepsACoreThatIsAlreadyAtThePin()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            var core = "com.qa.importcore" + Letters();
            var addon = core + ".addon";
            // The project already runs the core version the add-on pins; the core's newest must not replace it.
            var folder = InstalledPackages.FolderOf(PackageInstallService.ProjectRoot, core);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "package.json"), "{\"name\":\"" + core + "\",\"version\":\"1.0.0\",\"displayName\":\"" + core + "\",\"unity\":\"2021.3\"}");
            AssetDatabase.Refresh();
            _server.AddFile("cv-110", core + "-1.1.0.zip", PackageZipBytes(core, "1.1.0", null, null));
            _server.AddFile("cv-100", core + "-1.0.0.zip", PackageZipBytes(core, "1.0.0", null, null));
            _server.AddFile("av-100", addon + "-1.0.0.zip", PackageZipBytes(addon, "1.0.0", core, "1.0.0"));
            _server.SetLicenseItemRequest(_product, _id,
                FakeAtelier.License("l-core", _product + " Core", FakeAtelier.Version("cv-110", core, "1.1.0"), FakeAtelier.Version("cv-100", core, "1.0.0")),
                FakeAtelier.License("l-addon", _product + " Add-on", FakeAtelier.Version("av-100", addon, "1.0.0")));
            try
            {
                yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 120);

                CollectionAssert.AreEqual(new[] { "lv:av-100" }, _server.DownloadItems(), "only the add-on; the core it pins is already here");
                Assert.AreEqual("1.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, core));
                Assert.AreEqual("1.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, addon));
                CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
            }
            finally
            {
                RemovePackages(core, addon);
            }
        }

        [UnityTest]
        public IEnumerator AnItemWithTwoUnrelatedLicenses_InstallsBoth()
        {
            LogAssert.ignoreFailingMessages = true;
            _choice = (int)ImportChoice.Import;
            var one = "com.qa.importone" + Letters();
            var two = "com.qa.importtwo" + Letters();
            _server.AddFile("o-1", one + "-2.0.0.zip", PackageZipBytes(one, "2.0.0", null, null));
            _server.AddFile("t-1", two + "-3.0.0.zip", PackageZipBytes(two, "3.0.0", null, null));
            _server.SetLicenseItemRequest(_product, _id,
                FakeAtelier.License("l-one", _product + " One", FakeAtelier.Version("o-1", one, "2.0.0")),
                FakeAtelier.License("l-two", _product + " Two", FakeAtelier.Version("t-1", two, "3.0.0")));
            try
            {
                yield return Await(ImportRequestWatcher.PollNowAsync(Token()), 120);

                CollectionAssert.AreEqual(new[] { "lv:o-1", "lv:t-1" }, _server.DownloadItems());
                Assert.AreEqual("2.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, one));
                Assert.AreEqual("3.0.0", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, two));
                CollectionAssert.AreEqual(new[] { "claim:" + _id + "::", "done:" + _id + ":c-1:" }, _server.Posts());
            }
            finally
            {
                RemovePackages(one, two);
            }
        }

        // ------------------------------------------------------------ helpers

        /// <summary>A VPM-style package zip: package.json at the root, with one exact vpmDependency when <paramref name="dependency"/> is set.</summary>
        private static byte[] PackageZipBytes(string name, string version, string dependency, string dependencyVersion)
        {
            var json = "{\"name\":\"" + name + "\",\"version\":\"" + version + "\",\"displayName\":\"" + name + "\",\"unity\":\"2021.3\"" +
                (dependency != null ? ",\"vpmDependencies\":{\"" + dependency + "\":\"" + dependencyVersion + "\"}" : "") + "}";
            using (var stream = new MemoryStream())
            {
                using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, true))
                {
                    var entry = zip.CreateEntry("package.json");
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(json);
                }
                return stream.ToArray();
            }
        }

        private static void RemovePackages(params string[] names)
        {
            foreach (var name in names)
            {
                var folder = InstalledPackages.FolderOf(PackageInstallService.ProjectRoot, name);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            AssetDatabase.Refresh();
            UnityEditor.PackageManager.Client.Resolve();
        }

        /// <summary>A real .unitypackage of one text file, exported by Unity, with the source removed again.</summary>
        private static byte[] ExportProbePackage(string folder)
        {
            var root = PackageInstallService.ProjectRoot;
            Directory.CreateDirectory(Path.Combine(root, folder));
            File.WriteAllText(Path.Combine(root, folder, "probe.txt"), "one-button import probe\n");
            AssetDatabase.Refresh();
            var file = Path.Combine(Path.GetTempPath(), "import-probe-" + Guid.NewGuid().ToString("N") + ".unitypackage");
            AssetDatabase.ExportPackage(folder, file, ExportPackageOptions.Recurse);
            AssetDatabase.DeleteAsset(folder);
            var bytes = File.ReadAllBytes(file);
            File.Delete(file);
            return bytes;
        }

        private static string Token()
        {
            var session = SessionStore.Load();
            Assert.IsNotNull(session);
            return session.token;
        }

        private static string ClaimFile()
        {
            return Path.Combine(PackageInstallService.ProjectRoot, "Library", "OnAirDeck", "import-claim.json");
        }

        private static void DeleteClaimFile()
        {
            if (File.Exists(ClaimFile())) File.Delete(ClaimFile());
        }

        private static IEnumerator Await(Task task, double seconds)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < end) yield return null;
            Assert.IsTrue(task.IsCompleted, "timed out");
            if (task.IsFaulted) throw task.Exception;
        }

        private static string Letters()
        {
            var bytes = Guid.NewGuid().ToByteArray();
            var chars = new char[8];
            for (var i = 0; i < chars.Length; i++) chars[i] = (char)('g' + bytes[i] % 16);
            return new string(chars);
        }
    }

    /// <summary>A loopback stand-in for the plugin-* functions the import touches, answering with contract-shaped JSON.</summary>
    internal sealed class FakeAtelier : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Thread _thread;
        private readonly List<string> _posts = new List<string>();
        private string _requestsJson = "{\"requests\":[]}";
        private string _purchasesJson = "{\"purchases\":[],\"licenses\":[]}";

        public string BaseUrl { get; private set; }
        public int Lists;
        public int Downloads;
        public int ClaimStatus = 200;
        public string ClaimError = "";
        public int ActionStatus = 200;
        public int RenewStatus = 200;
        public byte[] PackageBytes = new byte[0];
        /// <summary>The plugin-version answer; null answers 404.</summary>
        public string VersionJson;
        private readonly List<string> _downloads = new List<string>();
        private readonly Dictionary<string, byte[]> _files = new Dictionary<string, byte[]>();
        private readonly Dictionary<string, string> _versionFiles = new Dictionary<string, string>();
        private string _fileName = "readme.txt";

        public FakeAtelier()
        {
            var port = FreePort();
            BaseUrl = "http://127.0.0.1:" + port;
            _listener.Prefixes.Add(BaseUrl + "/");
            _listener.Start();
            _thread = new Thread(Serve) { IsBackground = true, Name = "FakeAtelier" };
            _thread.Start();
        }

        /// <param name="secondItem">The request is for "it-2", a second line item of the same product listed after "it-1" (already downloaded).</param>
        public void SetRequest(string product, string requestId, string fileName = "readme.txt", bool secondItem = false)
        {
            _fileName = fileName;
            var expires = DateTime.UtcNow.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var first = Purchase(product, "it-1", fileName, secondItem);
            var requested = secondItem ? Purchase(product, "it-2", fileName, false) : first;
            _requestsJson = "{\"requests\":[{\"request_id\":\"" + requestId + "\",\"target\":\"unity\",\"kind\":\"item\",\"item_id\":\"" + (secondItem ? "it-2" : "it-1") + "\",\"license_id\":\"\",\"product_id\":\"p-1\"," +
                "\"created_at\":\"2026-10-31T00:00:00.000Z\",\"expires_at\":\"" + expires + "\",\"purchase\":" + requested + ",\"licenses\":[]}]}";
            _purchasesJson = "{\"purchases\":[" + first + (secondItem ? "," + requested : "") + "],\"licenses\":[]}";
        }

        /// <summary>
        /// A request for an item that grants <paramref name="licenses"/> and has no Unity file of its own (a MingToon Founders edition):
        /// the request carries the granted licenses, plugin-purchases lists them with their versions.
        /// </summary>
        public void SetLicenseItemRequest(string product, string requestId, params string[] licenses)
        {
            var expires = DateTime.UtcNow.AddMinutes(30).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            var item = "{\"name\":\"" + product + "\",\"date\":\"2026-10-31\",\"item_id\":\"it-1\",\"product_id\":\"p-1\",\"files\":[],\"downloaded\":false,\"update_available\":false," +
                "\"thumbnail_url\":\"\",\"seller_id\":\"s-1\",\"seller_name\":\"Seller\",\"category\":\"\",\"purchased_at\":\"2026-10-31T00:00:00.000Z\",\"source\":\"purchase\",\"description\":\"\",\"product_url\":\"\",\"versions\":[]}";
            var list = string.Join(",", licenses);
            _requestsJson = "{\"requests\":[{\"request_id\":\"" + requestId + "\",\"target\":\"unity\",\"kind\":\"item\",\"item_id\":\"it-1\",\"license_id\":\"\",\"product_id\":\"p-1\"," +
                "\"created_at\":\"2026-10-31T00:00:00.000Z\",\"expires_at\":\"" + expires + "\",\"purchase\":" + item + ",\"licenses\":[" + list + "]}]}";
            _purchasesJson = "{\"purchases\":[" + item + "],\"licenses\":[" + list + "]}";
        }

        public static string License(string id, string name, params string[] versions)
        {
            return "{\"license_id\":\"" + id + "\",\"name\":\"" + name + "\",\"rights\":[],\"max_devices\":0,\"devices_used\":0,\"this_device\":false,\"seller_id\":\"s-1\",\"seller_name\":\"Seller\"," +
                "\"thumbnail_url\":\"\",\"source\":\"grant\",\"description\":\"\",\"product_url\":\"\",\"versions\":[" + string.Join(",", versions) + "]}";
        }

        public static string Version(string id, string package, string version)
        {
            return "{\"version_id\":\"" + id + "\",\"version_label\":\"" + version + "\",\"file_name\":\"" + package + "-" + version + ".zip\",\"file_size\":0,\"released_at\":\"2026-10-31T00:00:00.000Z\"," +
                "\"release_notes\":\"\",\"package_name\":\"" + package + "\",\"package_version\":\"" + version + "\"}";
        }

        /// <summary>A license version's file: plugin-download { license_version_id } answers with it, served under /files/.</summary>
        public void AddFile(string versionId, string fileName, byte[] bytes)
        {
            lock (_files) { _files[fileName] = bytes; _versionFiles[versionId] = fileName; }
        }

        private static string Purchase(string product, string itemId, string fileName, bool downloaded)
        {
            return "{\"name\":\"" + product + "\",\"date\":\"2026-10-31\",\"item_id\":\"" + itemId + "\",\"product_id\":\"p-1\",\"files\":[{\"file_id\":\"f-1\",\"file_name\":\"" + fileName + "\"}]," +
                "\"downloaded\":" + (downloaded ? "true" : "false") + ",\"update_available\":false,\"thumbnail_url\":\"\",\"seller_id\":\"s-1\",\"seller_name\":\"Seller\",\"category\":\"\",\"purchased_at\":\"2026-10-31T00:00:00.000Z\"," +
                "\"source\":\"purchase\",\"description\":\"\",\"product_url\":\"\",\"versions\":[]}";
        }

        public string[] DownloadItems()
        {
            lock (_downloads) return _downloads.ToArray();
        }

        public string[] Posts()
        {
            lock (_posts) return _posts.ToArray();
        }

        private void Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = _listener.GetContext(); }
                catch (Exception) { return; }
                try { Handle(context); }
                catch (Exception) { try { context.Response.Abort(); } catch (Exception) { } }
            }
        }

        private void Handle(HttpListenerContext context)
        {
            var path = context.Request.Url.AbsolutePath.Trim('/');
            var method = context.Request.HttpMethod;
            string body;
            using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();

            if (path == "plugin-import-requests" && method == "GET")
            {
                Interlocked.Increment(ref Lists);
                Reply(context, 200, _requestsJson);
            }
            else if (path == "plugin-import-requests" && method == "POST")
            {
                var request = JsonUtility.FromJson<ImportActionRequest>(body);
                // A renew that keeps the lease is routine; one that loses it is part of what a test checks.
                if (request.action != "renew" || RenewStatus != 200)
                    lock (_posts) _posts.Add(request.action + ":" + request.request_id + ":" + request.claim_id + ":" + request.code);
                if (request.action == "renew" && RenewStatus != 200) Reply(context, RenewStatus, "{\"error\":\"lease_lost\"}");
                else if (request.action == "claim")
                {
                    if (ClaimStatus != 200) Reply(context, ClaimStatus, "{\"error\":\"" + ClaimError + "\"}");
                    else Reply(context, 200, "{\"request_id\":\"" + request.request_id + "\",\"status\":\"claimed\",\"claim_id\":\"c-1\",\"lease_expires_at\":\"2026-10-31T00:10:00.000Z\",\"lease_seconds\":600}");
                }
                else if (ActionStatus != 200) Reply(context, ActionStatus, "{\"error\":\"internal_error\"}");
                else Reply(context, 200, "{\"request_id\":\"" + request.request_id + "\",\"status\":\"" + request.action + "\"}");
            }
            else if (path == "plugin-purchases") Reply(context, 200, _purchasesJson);
            else if (path == "plugin-version" && VersionJson != null) Reply(context, 200, VersionJson);
            else if (path == "plugin-download")
            {
                Interlocked.Increment(ref Downloads);
                var version = JsonUtility.FromJson<LicenseDownloadRequest>(body);
                string versionFile = null;
                if (version != null && !string.IsNullOrEmpty(version.license_version_id))
                    lock (_files) _versionFiles.TryGetValue(version.license_version_id, out versionFile);
                if (versionFile != null)
                {
                    lock (_downloads) _downloads.Add("lv:" + version.license_version_id);
                    Reply(context, 200, "{\"files\":[{\"url\":\"" + BaseUrl + "/files/" + versionFile + "\",\"file_name\":\"" + versionFile + "\"}]}");
                    return;
                }
                var download = JsonUtility.FromJson<DownloadRequest>(body);
                lock (_downloads) _downloads.Add(download != null ? download.item_id : "");
                Reply(context, 200, "{\"files\":[{\"url\":\"" + BaseUrl + "/files/" + _fileName + "\",\"file_name\":\"" + _fileName + "\"}]}");
            }
            else if (path == "files/readme.txt") Reply(context, 200, "Imported through the one-button import test.\n", "text/plain");
            else if (path == "files/probe.unitypackage") ReplyBytes(context, PackageBytes);
            else if (path.StartsWith("files/", StringComparison.Ordinal) && Served(path.Substring(6)) != null) ReplyBytes(context, Served(path.Substring(6)));
            else Reply(context, 404, "{\"error\":\"not_found\"}");
        }

        private byte[] Served(string fileName)
        {
            byte[] bytes;
            lock (_files) return _files.TryGetValue(fileName, out bytes) ? bytes : null;
        }

        private static void Reply(HttpListenerContext context, int status, string text, string type = "application/json")
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            context.Response.StatusCode = status;
            context.Response.ContentType = type;
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static void ReplyBytes(HttpListenerContext context, byte[] bytes)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            context.Response.OutputStream.Close();
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch (Exception) { }
        }
    }
}
