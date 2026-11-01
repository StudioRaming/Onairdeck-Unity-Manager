using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace OnAirDeck.UnityManager.EditModeTests
{
    /// <summary>
    /// Owner report 2026-11-01: MingToon Core + URP add-on selected over an existing MingToon ended as "0개를 마쳤고 2개는 못 했어요." with no
    /// reason, and the clean-up banner's install did nothing. These run the real batch (BatchActions.InstallAll, BatchQueue,
    /// PackageInstallService, LegacyInstall, PackageTransaction) against the loopback fake (<see cref="FakeAtelier"/>) with the real package
    /// names, so the MingToon rules (old folder Assets/StudioRaming/MingToon, its release manifest) apply. Needs ATELIER_DEV_SERVER and a test
    /// project without MingToon (no Packages/com.studioraming.mingtoon, no Assets/StudioRaming/MingToon); otherwise ignored.
    /// </summary>
    public class OverwriteInstallTests
    {
        private const string Core = "com.studioraming.mingtoon";
        private const string Urp = "com.studioraming.mingtoon.urp";
        private const string Folder = "Assets/StudioRaming/MingToon";
        private FakeAtelier _server;
        private string _oldFunctionsUrl;
        private bool _madeFolder;

        [SetUp]
        public void SetUp()
        {
#if !ATELIER_DEV_SERVER
            Assert.Ignore("Needs the ATELIER_DEV_SERVER define (Assets/csc.rsp: -define:ATELIER_DEV_SERVER).");
#endif
            var root = PackageInstallService.ProjectRoot;
            if (Directory.Exists(InstalledPackages.FolderOf(root, Core)) || Directory.Exists(InstalledPackages.FolderOf(root, Urp)) || Directory.Exists(Abs(Folder)))
                Assert.Ignore("Needs a test project without MingToon (it installs and removes the real package names).");
            ImportRequestWatcher.Paused = true;
            _server = new FakeAtelier();
            _oldFunctionsUrl = EditorPrefs.GetString(DevServer.FunctionsUrlPref, "");
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _server.BaseUrl);
            Assert.IsTrue(DevServer.Redirected, "the dev server override is not active");
            SessionStore.Save(new ExchangeResponse { session_token = "test-token-" + Guid.NewGuid().ToString("N"), user_id = "test-user", email = "test@example.invalid", expires_in = 3600 });
            _server.AddFile("cv-115", Core + "-0.1.15.zip", PackageZipBytes(Core, "0.1.15", null, null));
            _server.AddFile("uv-115", Urp + "-0.1.15.zip", PackageZipBytes(Urp, "0.1.15", Core, "0.1.15"));
            _madeFolder = false;
        }

        [TearDown]
        public void TearDown()
        {
            if (_server == null) return;
            SessionStore.Clear();
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _oldFunctionsUrl ?? "");
            _server.Dispose();
            var root = PackageInstallService.ProjectRoot;
            foreach (var name in new[] { Core, Urp })
            {
                var folder = InstalledPackages.FolderOf(root, name);
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            if (_madeFolder && Directory.Exists(Abs(Folder))) { Directory.Delete(Abs(Folder), true); File.Delete(Abs(Folder) + ".meta"); }
            AssetDatabase.Refresh();
            UnityEditor.PackageManager.Client.Resolve();
            ImportRequestWatcher.Paused = false;
        }

        [Test]
        public void Summary_NamesEachItem_WithTheReasonAndTheNextStep()
        {
            var results = new List<BatchResult>
            {
                new BatchResult { Name = Core + " 0.1.15", Status = BatchStatus.Done },
                new BatchResult { Name = Urp + " 0.1.15", Status = BatchStatus.Failed, Error = new DownloadFailedException("Download of x.zip failed: HTTP 0 Cannot connect to destination host", 0) }
            };
            var text = InstallResultText.Summary(0, InstallResultText.Lines(0, results, new[] { "MingToon Core", "MingToon URP Addon" }));
            StringAssert.StartsWith("1개를 마쳤고 1개는 못 했어요.", text);
            StringAssert.Contains("- MingToon Core: 설치했어요", text);
            StringAssert.Contains("- MingToon URP Addon: 못 했어요. 서버에 연결하지 못했거나 다운로드가 끊겼어요.", text);
            StringAssert.Contains("다음: 인터넷 연결을 확인하고 [실패한 항목 다시 시도]를 눌러 주세요.", text);
        }

        [Test]
        public void OldBoothFolder_ProvenByItsReleaseManifest_ButAUserFileBlocksIt()
        {
            MakeBoothFolder();
            var probes = new List<LegacyProbe> { new LegacyProbe { Name = Core, Version = "0.1.15", Display = "MingToon Core" } };
            var plan = LegacyInstall.Detect(PackageInstallService.ProjectRoot, probes);
            Assert.AreEqual(1, plan.Items.Count);
            Assert.AreEqual(Folder, plan.Items[0].Path);
            Assert.IsFalse(plan.Items[0].Blocked, string.Join(", ", plan.Items[0].Unmatched));
            Assert.IsFalse(plan.Items[0].HandOff);

            File.WriteAllText(Abs(Folder + "/Presets/Mine.txt"), "my own preset");
            plan = LegacyInstall.Detect(PackageInstallService.ProjectRoot, probes);
            Assert.IsTrue(plan.Items[0].Blocked);
            CollectionAssert.AreEqual(new[] { Folder + "/Presets/Mine.txt" }, plan.Items[0].Unmatched);
            StringAssert.Contains("위 파일은 직접 만들었거나 고친 파일이라 그대로 두었어요.", LegacyInstallText.BlockedBody(0, plan.BlockedItems()));
        }

        [UnityTest]
        public IEnumerator Batch_OverAnOldBoothInstall_MovesItOnce_ThenInstallsCoreThenUrp()
        {
            LogAssert.ignoreFailingMessages = true;
            MakeBoothFolder();
            var asked = 0;
            var versions = OrderedVersions();
            var items = BatchActions.InstallAll(Token(), versions, Licenses(), CancellationToken.None, plan => { asked++; return true; });
            var queue = new BatchQueue(items);
            yield return Await(BatchActions.RunAsync(queue), 180);

            var text = InstallResultText.Summary(0, InstallResultText.Lines(0, queue.Results, new[] { "MingToon Core", "MingToon URP Addon" }));
            Assert.AreEqual(2, queue.DoneCount, text);
            StringAssert.StartsWith("2개를 모두 마쳤어요.", text);
            Assert.AreEqual(1, asked, "one question for the batch");
            CollectionAssert.AreEqual(new[] { "lv:cv-115", "lv:uv-115" }, _server.DownloadItems(), "the core first, then the add-on (its pinned core is already there)");
            Assert.AreEqual("0.1.15", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, Core));
            Assert.AreEqual("0.1.15", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, Urp));
            Assert.IsFalse(Directory.Exists(Abs(Folder)), "the old folder is still in Assets");
            Assert.IsTrue(LegacyMove.PendingRestores().Count == 0, "the move is not marked installed");
        }

        [UnityTest]
        public IEnumerator Batch_WithAUserFileInTheOldFolder_SaysWhyForBoth_AndARetryOfTheFailedOnesWorks()
        {
            LogAssert.ignoreFailingMessages = true;
            MakeBoothFolder();
            File.WriteAllText(Abs(Folder + "/Presets/Mine.txt"), "my own preset");
            var versions = OrderedVersions();
            var queue = new BatchQueue(BatchActions.InstallAll(Token(), versions, Licenses(), CancellationToken.None, plan => true));
            yield return Await(BatchActions.RunAsync(queue), 180);

            var lines = InstallResultText.Lines(0, queue.Results, new[] { "MingToon Core", "MingToon URP Addon" });
            var text = InstallResultText.Summary(0, lines);
            StringAssert.StartsWith("0개를 마쳤고 2개는 못 했어요.", text);
            StringAssert.Contains(Folder + "/Presets/Mine.txt", text);
            Assert.AreEqual(InstallFailureKind.OldInstall, lines[0].Kind, text);
            Assert.AreEqual(InstallFailureKind.OldInstall, lines[1].Kind, text);
            Assert.IsTrue(File.Exists(Abs(Folder + "/Presets/Mine.txt")), "the user's file was touched");
            Assert.IsTrue(string.IsNullOrEmpty(InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, Core)), "the core was installed although its old folder stayed");

            // The user moves the file out and presses the button again: only the failed items run, and now both finish.
            File.Delete(Abs(Folder + "/Presets/Mine.txt"));
            var retry = new List<LicenseVersionEntry>();
            for (var i = 0; i < queue.Results.Count; i++) if (queue.Results[i].Status == BatchStatus.Failed) retry.Add(versions[i]);
            Assert.AreEqual(2, retry.Count);
            var second = new BatchQueue(BatchActions.InstallAll(Token(), retry, Licenses(), CancellationToken.None, plan => true));
            yield return Await(BatchActions.RunAsync(second), 180);
            Assert.AreEqual(2, second.DoneCount, InstallResultText.Summary(0, InstallResultText.Lines(0, second.Results, null)));
            Assert.AreEqual("0.1.15", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, Urp));
        }

        // ------------------------------------------------------------ MingToon Core, then Ming Light Controller (a file add-on)

        private const string Mlc = "73f4e203-ffd0-4e88-9b78-dc116fd74057";   // OnAirDeckConfig.CoreAddonLicenses
        private const string MlcName = "QA Ming Light Controller";
        private const string MlcFile = "MingLightController-readme.txt";

        /// <summary>Owner rule 2026-11-01: Core and MLC picked together install the core first, then MLC, in one run.</summary>
        [UnityTest]
        public IEnumerator Batch_CoreAndMlc_InstallsTheCoreFirst_ThenMlc()
        {
            LogAssert.ignoreFailingMessages = true;
            _server.AddFile("mv-1", MlcFile, Encoding.UTF8.GetBytes("Ming Light Controller test file\n"));
            try
            {
                var queue = new BatchQueue(CoreThenMlc(true));
                yield return Await(BatchActions.RunAsync(queue), 180);
                var text = InstallResultText.Summary(0, InstallResultText.Lines(0, queue.Results, new[] { "MingToon Core", MlcName }));
                Assert.AreEqual(2, queue.DoneCount, text);
                CollectionAssert.AreEqual(new[] { "lv:cv-115", "lv:mv-1" }, _server.DownloadItems(), "the core first, then MLC");
                Assert.AreEqual("0.1.15", InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, Core));
                Assert.IsTrue(File.Exists(Abs(Installer.ProductFolder(MlcName) + "/" + MlcFile)), "MLC's file is not in");
            }
            finally
            {
                RemoveMlc();
            }
        }

        /// <summary>MLC alone without the core: nothing is downloaded and the row says why (not "0 finished, 1 failed" alone).</summary>
        [UnityTest]
        public IEnumerator Batch_MlcWithoutTheCore_DownloadsNothing_AndSaysWhy()
        {
            LogAssert.ignoreFailingMessages = true;
            _server.AddFile("mv-1", MlcFile, Encoding.UTF8.GetBytes("Ming Light Controller test file\n"));
            try
            {
                var queue = new BatchQueue(CoreThenMlc(false));
                yield return Await(BatchActions.RunAsync(queue), 60);
                var lines = InstallResultText.Lines(0, queue.Results, new[] { MlcName });
                Assert.AreEqual(BatchStatus.Failed, queue.Results[0].Status);
                Assert.IsInstanceOf<AddonNeedsCoreException>(queue.Results[0].Error);
                StringAssert.Contains("MingToon Core가 이 프로젝트에 없어서 설치하지 않았어요.", InstallResultText.Summary(0, lines));
                Assert.AreEqual(0, _server.DownloadItems().Length, "something was downloaded");
            }
            finally
            {
                RemoveMlc();
            }
        }

        /// <summary>
        /// A run of Core + MLC cut by a script reload after the core: only the SessionState record is left (what the window wrote before the
        /// core). Read back from SessionState alone, MLC is due because the core is in, and installs; the record is then gone.
        /// </summary>
        [UnityTest]
        public IEnumerator Reload_AfterTheCore_TheRecordedMlcInstallsFromSessionState()
        {
            LogAssert.ignoreFailingMessages = true;
            _server.AddFile("mv-1", MlcFile, Encoding.UTF8.GetBytes("Ming Light Controller test file\n"));
            var oldRecord = SessionState.GetString(ReloadCarry.PendingAddonsKey, "");
            try
            {
                SessionState.SetString(ReloadCarry.PendingAddonsKey, ReloadCarry.AddAddon("", new PendingAddon
                {
                    AddonKey = "license:" + Mlc, LicenseId = Mlc, Core = Core, Name = MlcName, SinceUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }));
                // The run: the core only, then it "ends in a reload" (no MLC step, nothing in memory carries over).
                var first = new BatchQueue(BatchActions.InstallAll(Token(), new List<LicenseVersionEntry> { Version("cv-115", Core, "0.1.15") }, MlcLicenses(), CancellationToken.None, plan => true));
                yield return Await(BatchActions.RunAsync(first), 180);
                Assert.AreEqual(1, first.DoneCount);
                first = null;

                var install = new List<PendingAddon>();
                var missing = new List<PendingAddon>();
                ReloadCarry.Split(ReloadCarry.ParseAddons(SessionState.GetString(ReloadCarry.PendingAddonsKey, "")), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    core => CoreAddons.CoreIsHere(PackageInstallService.ProjectRoot, core), install, missing);
                Assert.AreEqual(0, missing.Count);
                Assert.AreEqual(1, install.Count, "MLC is not due after the reload");
                SessionState.SetString(ReloadCarry.PendingAddonsKey, ReloadCarry.RemoveAddon(SessionState.GetString(ReloadCarry.PendingAddonsKey, ""), install[0].AddonKey));
                var license = Array.Find(MlcLicenses(), l => l.license_id == install[0].LicenseId);
                var second = new BatchQueue(new List<BatchItem> { BatchActions.InstallFile(Token(), license, license.versions[0], MlcLicenses()) });
                yield return Await(BatchActions.RunAsync(second), 120);
                Assert.AreEqual(1, second.DoneCount, InstallResultText.Summary(0, InstallResultText.Lines(0, second.Results, null)));
                CollectionAssert.AreEqual(new[] { "lv:cv-115", "lv:mv-1" }, _server.DownloadItems());
                Assert.IsTrue(File.Exists(Abs(Installer.ProductFolder(MlcName) + "/" + MlcFile)), "MLC's file is not in");
                Assert.AreEqual("", SessionState.GetString(ReloadCarry.PendingAddonsKey, ""), "the record is still there");
            }
            finally
            {
                SessionState.SetString(ReloadCarry.PendingAddonsKey, oldRecord);
                RemoveMlc();
            }
        }

        private List<BatchItem> CoreThenMlc(bool withCore)
        {
            var licenses = MlcLicenses();
            var items = new List<BatchItem>();
            if (withCore) items.AddRange(BatchActions.InstallAll(Token(), new List<LicenseVersionEntry> { Version("cv-115", Core, "0.1.15") }, licenses, CancellationToken.None, plan => true));
            items.Add(BatchActions.InstallFile(Token(), licenses[1], licenses[1].versions[0], licenses));
            return items;
        }

        private static LicenseEntry[] MlcLicenses()
        {
            var file = new LicenseVersionEntry { version_id = "mv-1", version_label = "1.0.0", file_name = MlcFile, released_at = "2026-11-01T00:00:00.000Z" };
            return new[]
            {
                new LicenseEntry { license_id = "l-core", name = "MingToon Core", rights = new string[0], versions = new[] { Version("cv-115", Core, "0.1.15") } },
                new LicenseEntry { license_id = Mlc, name = MlcName, rights = new string[0], versions = new[] { file } }
            };
        }

        private static void RemoveMlc()
        {
            var folder = Abs(Installer.ProductFolder(MlcName));
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta")) File.Delete(folder + ".meta");
        }

        // ------------------------------------------------------------ helpers

        private static string Abs(string relative)
        {
            return Path.Combine(PackageInstallService.ProjectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string Sha(string relative)
        {
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(File.ReadAllBytes(Abs(relative)))) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>MingToon 0.1.7's layout in miniature (text files, so nothing compiles): files and .meta files plus the release manifest.</summary>
        private void MakeBoothFolder()
        {
            _madeFolder = true;
            var files = new[] { Folder + "/Shaders/Readme.txt", Folder + "/Presets/Look.txt", Folder + "/Docs/Manual.txt" };
            var listed = new List<string>();
            foreach (var file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Abs(file)));
                File.WriteAllText(Abs(file), "shipped " + file);
                File.WriteAllText(Abs(file) + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
                listed.Add(file);
                listed.Add(file + ".meta");
            }
            var sb = new StringBuilder("{\"product\":\"MingToon\",\"version\":\"0.1.7\",\"files\":[");
            for (var i = 0; i < listed.Count; i++) sb.Append(i > 0 ? "," : "").Append("{\"path\":\"").Append(listed[i]).Append("\",\"sha256\":\"").Append(Sha(listed[i])).Append("\"}");
            sb.Append("]}");
            Directory.CreateDirectory(Abs(Folder + "/Release"));
            File.WriteAllText(Abs(Folder + "/Release/MingToon_ReleaseManifest.json"), sb.ToString());
        }

        private static List<LicenseVersionEntry> OrderedVersions()
        {
            var urp = Version("uv-115", Urp, "0.1.15");
            var core = Version("cv-115", Core, "0.1.15");
            // The list order puts the add-on first; the window orders the core first (CoreAddons.OrderCoreFirst).
            return CoreAddons.OrderCoreFirst(new List<LicenseVersionEntry> { urp, core }, v => v.package_name, v => "");
        }

        private static LicenseEntry[] Licenses()
        {
            return new[]
            {
                new LicenseEntry { license_id = "l-core", name = "MingToon Core", rights = new string[0], versions = new[] { Version("cv-115", Core, "0.1.15") } },
                new LicenseEntry { license_id = "l-urp", name = "MingToon URP Addon", rights = new string[0], versions = new[] { Version("uv-115", Urp, "0.1.15") } }
            };
        }

        private static LicenseVersionEntry Version(string id, string package, string version)
        {
            return new LicenseVersionEntry { version_id = id, version_label = version, file_name = package + "-" + version + ".zip", package_name = package, package_version = version, released_at = "2026-11-01T00:00:00.000Z" };
        }

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

        private static string Token()
        {
            var session = SessionStore.Load();
            Assert.IsNotNull(session);
            return session.token;
        }

        private static IEnumerator Await(Task task, double seconds)
        {
            var end = EditorApplication.timeSinceStartup + seconds;
            while (!task.IsCompleted && EditorApplication.timeSinceStartup < end) yield return null;
            Assert.IsTrue(task.IsCompleted, "timed out");
            if (task.IsFaulted) throw task.Exception;
        }
    }
}
