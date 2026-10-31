using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEditor;
using UnityEngine.TestTools;

namespace OnAirDeck.UnityManager.EditModeTests
{
    /// <summary>
    /// The automatic self-update in a real Editor: the plugin-version answer from a loopback fake, the guard that never replaces a
    /// git working copy, the record an automatic install writes (the install step itself is replaced, since the Manager under test
    /// is the host's linked checkout), and the note the next version shows. Needs ATELIER_DEV_SERVER like the import tests.
    /// </summary>
    public class AutoUpdateTests
    {
        private FakeAtelier _server;
        private string _oldFunctionsUrl;
        private string _savedRecord;

        [SetUp]
        public void SetUp()
        {
#if !ATELIER_DEV_SERVER
            Assert.Ignore("Needs the ATELIER_DEV_SERVER define (Assets/csc.rsp: -define:ATELIER_DEV_SERVER).");
#endif
            _server = new FakeAtelier();
            _oldFunctionsUrl = EditorPrefs.GetString(DevServer.FunctionsUrlPref, "");
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _server.BaseUrl);
            var path = AutoUpdateLogic.RecordPath(PackageInstallService.ProjectRoot);
            _savedRecord = File.Exists(path) ? File.ReadAllText(path) : null;
            if (File.Exists(path)) File.Delete(path);
        }

        [TearDown]
        public void TearDown()
        {
            AutoUpdater.ApplySeam = null;
            if (_server == null) return;
            EditorPrefs.SetString(DevServer.FunctionsUrlPref, _oldFunctionsUrl ?? "");
            _server.Dispose();
            var path = AutoUpdateLogic.RecordPath(PackageInstallService.ProjectRoot);
            if (_savedRecord != null) File.WriteAllText(path, _savedRecord);
            else if (File.Exists(path)) File.Delete(path);
        }

        private string Newer()
        {
            var parts = AutoUpdater.LoadedVersion.Split('.');
            return parts[0] + "." + parts[1] + "." + (int.Parse(parts[2].Split('-')[0]) + 1);
        }

        [UnityTest]
        public IEnumerator ANewerRelease_IsSeen_ButAGitWorkingCopyIsNeverReplaced()
        {
            var newer = Newer();
            _server.VersionJson = "{\"client\":\"unity-manager\",\"latest\":{\"version\":\"" + newer + "\",\"git_url\":\"https://example.invalid/m.git#" + newer +
                "\",\"download_url\":\"https://example.invalid/m-" + newer + ".zip\",\"notes\":\"\",\"published_at\":\"2026-10-31T00:00:00Z\"},\"min_supported_version\":null,\"update_available\":true,\"must_update\":false}";
            var check = UpdateService.CheckAsync();
            yield return Await(check, 30);

            var state = check.Result;
            Assert.IsTrue(state.UpdateAvailable, "the fake's newer release was not read");
            Assert.AreEqual(newer, state.Response.latest.version);
            // The Manager under test is the host's link to a git checkout: it must stay as it is, by a click or automatically.
            Assert.IsFalse(state.CanApply);
            StringAssert.Contains("git working copy", state.ApplyBlockedReason);
            Assert.AreEqual(AutoUpdateAction.None, AutoUpdateLogic.Decide(state.UpdateAvailable, state.CanApply, newer, AutoUpdater.LoadedVersion, "", new AutoUpdateMoment()));
        }

        [UnityTest]
        public IEnumerator AnAutomaticInstall_WritesItsRecordFirst_AndAFailureIsNotTriedAgain()
        {
            var newer = Newer();
            var state = Embedded(newer);
            SelfUpdateRecord seen = null;
            AutoUpdater.ApplySeam = (s, record) =>
            {
                seen = AutoUpdateLogic.Load(PackageInstallService.ProjectRoot);   // on disk before the install step runs
                return Task.FromResult(0);
            };
            var apply = AutoUpdater.ApplyAsync(state);
            yield return Await(apply, 30);

            Assert.IsNull(apply.Result);
            Assert.IsNotNull(seen);
            Assert.AreEqual(SelfUpdateRecord.PhaseApplying, seen.Phase);
            Assert.AreEqual(AutoUpdater.LoadedVersion, seen.From);
            Assert.AreEqual(newer, seen.To);
            Assert.AreEqual("embedded", seen.Route);
            Assert.AreEqual(newer, AutoUpdater.BlockedVersion(), "an update in progress blocks another automatic one");

            // While it settles (no reload yet), nothing may start another update over it, not even a click.
            var second = AutoUpdater.ApplyAsync(state, false, true);
            yield return Await(second, 30);
            StringAssert.Contains("still settling", second.Result);
            // As after the reload: the update is done; a later failure of the same release blocks only automatic installs.
            var settled = AutoUpdateLogic.Load(PackageInstallService.ProjectRoot);
            settled.Phase = SelfUpdateRecord.PhaseShown;
            AutoUpdateLogic.Save(PackageInstallService.ProjectRoot, settled);
            AutoUpdater.ApplySeam = (s, record) => { throw new PackageInstallException("download failed"); };
            var failing = AutoUpdater.ApplyAsync(state);
            yield return Await(failing, 30);
            StringAssert.Contains("download failed", failing.Result);
            Assert.AreEqual(newer, AutoUpdater.BlockedVersion());
            Assert.AreEqual(AutoUpdateAction.Blocked, AutoUpdateLogic.Decide(true, true, newer, AutoUpdater.LoadedVersion, AutoUpdater.BlockedVersion(), new AutoUpdateMoment()));
            // An older or equal release is refused whatever the server says.
            var same = AutoUpdater.ApplyAsync(Embedded(AutoUpdater.LoadedVersion));
            yield return Await(same, 30);
            StringAssert.Contains("not newer", same.Result);
        }

        [Test]
        public void TheNewVersion_ShowsTheNoteOnce()
        {
            var root = PackageInstallService.ProjectRoot;
            var current = AutoUpdater.LoadedVersion;
            AutoUpdateLogic.Save(root, new SelfUpdateRecord { From = "0.0.9", To = current, Route = "embedded", Phase = SelfUpdateRecord.PhaseApplied });
            string kind;
            var note = AutoUpdater.PendingNote(out kind);
            Assert.AreEqual("updated", kind);
            Assert.AreEqual("0.0.9", note.From);
            AutoUpdater.NoteSeen(note);
            AutoUpdater.PendingNote(out kind);
            Assert.IsNull(kind);
            Assert.AreEqual(SelfUpdateRecord.PhaseShown, AutoUpdateLogic.Load(root).Phase);
        }

        private static UpdateState Embedded(string version)
        {
            return new UpdateState
            {
                CurrentVersion = AutoUpdater.LoadedVersion, CanApply = true, UseEmbedded = true, PackageName = "com.studioraming.manager",
                PackageFolder = "unused-by-the-seam", DownloadUrl = "https://example.invalid/m.zip",
                Response = new VersionResponse { latest = new ReleaseInfo { version = version }, update_available = true },
            };
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
