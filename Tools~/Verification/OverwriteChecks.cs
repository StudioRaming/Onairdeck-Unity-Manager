using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// Owner report 2026-11-01: installing MingToon Core + URP over an existing MingToon said "0개를 마쳤고 2개는 못 했어요." with no reason,
// and the clean-up banner's install button did nothing. Real sources: InstallResultText, BatchQueue, ReleaseManifestHandler, LegacyInstall,
// LegacyInstallText, CoreAddons. The window half (dialogs, selection, the VCC switch) is compile-checked only.
internal static class OverwriteChecks
{
    private const string Core = "com.studioraming.mingtoon";
    private const string Urp = "com.studioraming.mingtoon.urp";
    private const string Mlc = "73f4e203-ffd0-4e88-9b78-dc116fd74057";
    private const string Folder = "Assets/StudioRaming/MingToon";
    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "overwrite");
        Directory.CreateDirectory(_root);
        check("results: every batch item says what finished, and a failure says why and what next (ko)", SummaryKorean);
        check("results: failures sort by type (old install, network, license, dependency, cancel, a wrapped restore)", Classify);
        check("results: a real queue with one old-install refusal and one success gives 1 done / 1 not, with the refusal's own text", QueueSummary);
        check("old BOOTH folder: an exact copy of its release manifest is moved to the backup (hash of a .meta ignored), then the plan is empty", ManifestMoves);
        check("old BOOTH folder: a user file, an edited file or a git checkout blocks the move, names the file and says what to do", ManifestBlocks);
        check("old BOOTH folder: with no release manifest the old rule stays (legacy case without installer = clear refusal, not silent)", NoManifestUnchanged);
        check("working copy: an old folder with .git (or .serena, a nested .vs, a .git file) and an unmatched file says 'move this folder out by hand' with its path before any question, never hands off, moves nothing", WorkingCopyNeedsManualMove);
        check("working copy: a clean old install keeps today's plan (manifest move, installer hand-off, M1 hand-off); no refusal before the question", CleanInstallUnchanged);
        // A real MingToon zip (its Release/MingToon_ReleaseManifest.json lists Assets/StudioRaming/MingToon paths) unpacked as the old layout.
        var realZip = Environment.GetEnvironmentVariable("ONAIRDECK_MINGTOON_ZIP");
        if (!string.IsNullOrEmpty(realZip)) check("old BOOTH folder: the real MingToon " + Path.GetFileName(realZip) + " unpacked under " + Folder + " proves itself", () => RealZip(realZip));
        check("add-ons: URP (package name) and MLC (license table) need MingToon Core; core first, add-ons last in a run", CoreFirstOrder);
        check("add-ons: blocked state (core missing: install it first when offered, else explain); rows nest under the core", AddonStateAndNest);
        check("reload: Core + MLC cut after the core; from the SessionState text alone MLC installs once the core is in, else says why; row reasons come back", AddonSurvivesReload);
        check("batch: an item counts for exactly its package (URP is not the core); a missing core is a plain reason", ItemNamesExact);
    }

    // ---------- results ----------

    private static void SummaryKorean()
    {
        var results = new List<BatchResult>
        {
            new BatchResult { Name = Core + " 0.1.15", Status = BatchStatus.Done },
            new BatchResult { Name = Urp + " 0.1.15", Status = BatchStatus.Failed, Error = new LegacyCleanupRequiredException("MingToon URP Addon: 이전 설치 정리 필요 (Assets/StudioRaming/MingToon).") },
            new BatchResult { Name = "x 1.0.0", Status = BatchStatus.Skipped, Message = "Cancelled." }
        };
        var lines = InstallResultText.Lines(0, results, new[] { "MingToon Core", "MingToon URP Addon", "" });
        var text = InstallResultText.Summary(0, lines);
        Require(text.StartsWith("1개를 마쳤고 2개는 못 했어요.", StringComparison.Ordinal), text);
        Require(text.Contains("- MingToon Core: 설치했어요"), text);
        Require(text.Contains("- MingToon URP Addon: 못 했어요. 이전 설치 정리 필요 (Assets/StudioRaming/MingToon)."), text);
        Require(text.Contains("다음: 위 안내대로 정리한 뒤 [실패한 항목 다시 시도]를 눌러 주세요."), text);
        Require(text.Contains("- x 1.0.0: 못 했어요. 시작하지 않았어요"), text);
        Equal(InstallFailureKind.OldInstall, lines[1].Kind);
        var all = InstallResultText.Summary(0, InstallResultText.Lines(0, new[] { results[0] }, null));
        Require(all.StartsWith("1개를 모두 마쳤어요.", StringComparison.Ordinal), all);
    }

    private static void Classify()
    {
        Equal(InstallFailureKind.OldInstall, InstallResultText.Classify(new LegacyCleanupRequiredException("x")));
        Equal(InstallFailureKind.Network, InstallResultText.Classify(new DownloadFailedException("Download of a.zip failed: HTTP 0 Cannot connect", 0)));
        Equal(InstallFailureKind.Other, InstallResultText.Classify(new DownloadFailedException("HTTP 500", 500)));
        Equal(InstallFailureKind.Network, InstallResultText.Classify(new ApiException("Download failed: no answer from the server", 0, "", "")));
        Equal(InstallFailureKind.License, InstallResultText.Classify(new ApiException("Download failed: no grant", 403, "forbidden", "")));
        Equal(InstallFailureKind.Dependency, InstallResultText.Classify(new DependencyUnavailableException(DependencyErrorKind.GetOnAtelierFirst, Core, "0.1.15", "needs core")));
        Equal(InstallFailureKind.Dependency, InstallResultText.Classify(new AddonNeedsCoreException(Core, "needs core")));
        Equal(InstallFailureKind.Cancelled, InstallResultText.Classify(new LegacyDeclinedException()));
        Equal(InstallFailureKind.Session, InstallResultText.Classify(new SessionExpiredException("401")));
        // RestoreLegacy wraps the real failure: a network break during the install after the move is still a network failure.
        Equal(InstallFailureKind.Network, InstallResultText.Classify(new PackageInstallException("restored", new DownloadFailedException("HTTP 0", 0))));
        Equal(InstallFailureKind.Other, InstallResultText.Classify(new PackageInstallException("The dependency chain is too deep.")));
        Require(InstallResultText.Reason(0, new DownloadFailedException("HTTP 0", 0)).Contains("서버에 연결하지 못했거나"));
        Require(InstallResultText.Reason(0, new ApiException("x", 403, "", "")).Contains("라이선스 파일을 못 받았어요"));
        Equal("The dependency chain is too deep.", InstallResultText.Reason(0, new PackageInstallException("The dependency chain is too deep.")));
    }

    private static void QueueSummary()
    {
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = Core + " 0.1.15", Run = p => Task.FromResult<object>("ok") },
            new BatchItem { Name = Urp + " 0.1.15", Run = p => { throw new LegacyCleanupRequiredException("MingToon: 이전 설치 정리 필요 (" + Folder + ")."); } }
        });
        queue.RunAsync().GetAwaiter().GetResult();
        Equal(1, queue.DoneCount);
        Equal(1, queue.FailedCount);
        var text = InstallResultText.Summary(0, InstallResultText.Lines(0, queue.Results, new[] { "MingToon Core", "MingToon URP Addon" }));
        Require(text.StartsWith("1개를 마쳤고 1개는 못 했어요.", StringComparison.Ordinal), text);
        Require(text.Contains("MingToon URP Addon: 못 했어요. MingToon: 이전 설치 정리 필요 (" + Folder + ")."), text);
    }

    // ---------- old BOOTH folder with its release manifest ----------

    private static string NewProject()
    {
        var project = Path.Combine(_root, "p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(project, "Assets"));
        Directory.CreateDirectory(Path.Combine(project, "Packages"));
        return project;
    }

    private static void Put(string project, string relative, string text)
    {
        var path = Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string Sha(string project, string relative)
    {
        using (var sha = SHA256.Create())
            return string.Concat(sha.ComputeHash(File.ReadAllBytes(Path.Combine(project, relative.Replace('/', Path.DirectorySeparatorChar)))).Select(b => b.ToString("x2")));
    }

    // MingToon 0.1.7's layout in miniature: shipped files with .meta files, Release/MingToon_ReleaseManifest.json listing all of them.
    private static string BoothProject()
    {
        var project = NewProject();
        var files = new[]
        {
            Folder + "/Shaders/MingToon.shader", Folder + "/Shaders/MingToon.shader.meta", Folder + "/Shaders.meta",
            Folder + "/Presets/Look.asset", Folder + "/Presets/Look.asset.meta", Folder + "/Presets.meta",
            Folder + "/Editor/Tool.cs", Folder + "/Editor/Tool.cs.meta", Folder + "/Editor.meta", Folder + "/Release.meta"
        };
        foreach (var file in files) Put(project, file, file.EndsWith(".meta") ? "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n" : "content of " + file);
        Put(project, Folder + ".meta", "fileFormatVersion: 2\nguid: 00000000000000000000000000000001\n");
        var sb = new StringBuilder("{\n  \"product\": \"MingToon\",\n  \"version\": \"0.1.7\",\n  \"files\": [\n");
        for (var i = 0; i < files.Length; i++)
            sb.Append("    {\"path\": \"").Append(files[i]).Append("\", \"sha256\": \"").Append(Sha(project, files[i])).Append("\"}").Append(i < files.Length - 1 ? ",\n" : "\n");
        sb.Append("  ]\n}\n");
        Put(project, Folder + "/Release/MingToon_ReleaseManifest.json", sb.ToString());
        Put(project, Folder + "/Release/MingToon_ReleaseManifest.json.meta", "fileFormatVersion: 2\nguid: 00000000000000000000000000000002\n");
        Put(project, Folder + "/Release/MingToon_URP_AddonManifest.json", "{\"product\": \"MingToon URP Add-on\", \"version\": \"0.1.7\", \"releaseReady\": false}");
        return project;
    }

    private static List<LegacyProbe> CoreProbe()
    {
        return new List<LegacyProbe> { new LegacyProbe { Name = Core, Version = "0.1.15", Display = "MingToon Core" } };
    }

    private static void DefaultHandlers()
    {
        LegacyInstall.Handlers.Clear();
        LegacyInstall.Handlers.Add(new ReleaseManifestHandler());
        LegacyInstall.Handlers.Add(new InstallerHandoffHandler());
    }

    private static void ManifestMoves()
    {
        DefaultHandlers();
        var project = BoothProject();
        // Unity rewrote an importer setting: a .meta whose content no longer matches the manifest is not the user's data.
        Put(project, Folder + "/Presets/Look.asset.meta", "fileFormatVersion: 2\nguid: 22222222222222222222222222222222\nNativeFormatImporter:\n  mainObjectFileID: 11400000\n");
        var plan = LegacyInstall.Detect(project, CoreProbe());
        Equal(1, plan.Items.Count);
        var item = plan.Items[0];
        Equal(Folder, item.Path);
        Require(!item.Blocked, "blocked: " + string.Join(", ", item.Unmatched));
        Require(!item.HandOff, "hand-off chosen although the folder proves itself");
        var asked = 0;
        var move = LegacyInstall.Prepare(project, CoreProbe(), true, p => { asked++; return true; }, new DateTime(2026, 11, 1, 17, 0, 0));
        Equal(1, asked);
        Require(move != null);
        Require(!Directory.Exists(Path.Combine(project, Folder.Replace('/', Path.DirectorySeparatorChar))), "old folder still there");
        Require(File.Exists(Path.Combine(move.BackupFolder, Folder.Replace('/', Path.DirectorySeparatorChar), "Presets", "Look.asset")), "not in the backup");
        Require(LegacyInstall.Detect(project, CoreProbe()).IsEmpty, "plan not empty after the move");
        // Undo puts it all back.
        move.Restore();
        Require(File.Exists(Path.Combine(project, Folder.Replace('/', Path.DirectorySeparatorChar), "Presets", "Look.asset")), "restore failed");
    }

    private static void ManifestBlocks()
    {
        DefaultHandlers();
        // A preset the user saved inside the old folder.
        var project = BoothProject();
        Put(project, Folder + "/Presets/MyLook.asset", "mine");
        Put(project, Folder + "/Presets/MyLook.asset.meta", "fileFormatVersion: 2\nguid: 33333333333333333333333333333333\n");
        var before = Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length;
        var e = Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(project, CoreProbe(), true, p => true, DateTime.Now));
        Require(e.Message.Contains(Folder + "/Presets/MyLook.asset"), e.Message);
        Require(e.Message.Contains(Folder + "/Presets/MyLook.asset.meta"), e.Message);
        Require(e.Message.Contains("직접 만들었거나 고친 파일이라 그대로 두었어요") || LegacyInstallText.Language() != 0, e.Message);
        Equal(before, Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length);
        var ko = LegacyInstallText.BlockedBody(0, LegacyInstall.Detect(project, CoreProbe()).BlockedItems());
        Require(ko.Contains("위 파일은 직접 만들었거나 고친 파일이라 그대로 두었어요."), ko);
        // Review r1 M1: the same folder with the product installer at hand still goes to the installer (as it did before this handler).
        var withInstaller = CoreProbe();
        withInstaller[0].Installer = new LegacyInstallerFile { VersionId = "v-installer", FileName = "MingToon_Installer.unitypackage", Size = 1 };
        withInstaller[0].Zip = new LegacyInstallerFile { VersionId = "v-zip", FileName = "com.studioraming.mingtoon-0.1.15.zip", Size = 1 };
        var handed = LegacyInstall.Detect(project, withInstaller).Items.Single();
        Require(handed.HandOff && !handed.Blocked && handed.Handler is InstallerHandoffHandler, "user file + installer: not handed off");
        Equal(0, handed.Unmatched.Count);

        // An edited shipped file.
        project = BoothProject();
        Put(project, Folder + "/Editor/Tool.cs", "edited by the user");
        var plan = LegacyInstall.Detect(project, CoreProbe());
        Require(plan.Items[0].Blocked);
        Equal(Folder + "/Editor/Tool.cs", plan.Items[0].Unmatched.Single());

        // A developer's git checkout of the same folder.
        project = BoothProject();
        Put(project, Folder + "/.git/HEAD", "ref: refs/heads/main\n");
        plan = LegacyInstall.Detect(project, CoreProbe());
        Require(plan.Items[0].Blocked);
        Require(plan.Items[0].ManualMove, plan.Items[0].BlockedReason);
        Equal(Folder + "/.git", plan.Items[0].Unmatched.Single());
        ko = LegacyInstallText.BlockedBody(0, plan.BlockedItems());
        Require(ko.Contains("아래 폴더를 직접 프로젝트 밖으로 옮겨 주세요:\n  " + Folder), ko);
    }

    // Owner report 2026-11-02: Assets/StudioRaming/MingToon was a developer's git working copy (.git, .serena, handoff docs). The
    // install said the old install would be cleaned up, the clean-up refused, and the last text did not say what to do.
    private static void WorkingCopyNeedsManualMove()
    {
        DefaultHandlers();
        var installer = CoreProbe();
        installer[0].Installer = new LegacyInstallerFile { VersionId = "v-installer", FileName = "MingToon_Installer.unitypackage", Size = 1 };
        installer[0].Zip = new LegacyInstallerFile { VersionId = "v-zip", FileName = "com.studioraming.mingtoon-0.1.15.zip", Size = 1 };

        // With and without its release manifest (a checkout may carry Release/), with and without the product installer at hand.
        foreach (var withManifest in new[] { true, false })
            foreach (var probes in new[] { CoreProbe(), installer })
            {
                var project = withManifest ? BoothProject() : NewProject();
                if (!withManifest) Put(project, Folder + "/Shaders/MingToon.shader", "work in progress");
                Put(project, Folder + "/.git/HEAD", "ref: refs/heads/main\n");
                Put(project, Folder + "/.git/objects/ab/cdef", "blob");
                Put(project, Folder + "/.serena/project.yml", "name: mingtoon\n");
                Put(project, Folder + "/HANDOFF.md", "notes");
                Put(project, Folder + ".meta", "fileFormatVersion: 2\nguid: 00000000000000000000000000000001\n");
                var before = Snapshot(project);

                foreach (var plan in new[] { LegacyInstall.Detect(project, probes), LegacyInstall.DetectKnown(project, probes) })
                {
                    var item = plan.Items.Single();
                    Require(item.Blocked && item.ManualMove && !item.HandOff && item.Handler == null, item.BlockedReason + " handoff=" + item.HandOff);
                    CollectionAssertEqual(new[] { Folder + "/.git", Folder + "/.serena" }, item.Unmatched);
                    Require(!plan.HasHandoff, "a hand-off is still offered");
                    var known = LegacyInstall.RefusalBeforeConfirm(plan);
                    Require(known != null, "no refusal before the question");
                    Equal(Folder, known.ManualFolder);
                }

                // The install asks nothing, hands nothing off and moves nothing; the reason names the folder and what to do.
                var asked = 0;
                LegacyHandoffRequest handoff = null;
                var e = Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(project, probes, true, p => { asked++; return true; }, DateTime.Now, out handoff));
                Equal(0, asked);
                Require(handoff == null, "handed off");
                Equal(Folder, e.ManualFolder);
                Require(e.Message.Contains("\n  " + Folder + "\n"), e.Message);
                Require(e.Message.Contains(".git"), e.Message);
                // The short overload (no hand-off caller) and a background caller say the same, not "install it yourself and it is moved".
                Equal(e.Message, Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(project, probes, true, p => { asked++; return true; }, DateTime.Now)).Message);
                Equal(e.Message, Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(project, probes, false, null, DateTime.Now)).Message);
                Equal(0, asked);
                Require(Snapshot(project) == before, "something was moved or deleted");
                Require(!Directory.Exists(Path.Combine(project, "Library", "OnAirDeck", "Backup")), "a backup folder was made");
            }

        // The texts in every language: why in one line (with the names found), the exact folder, nothing changed.
        var sample = new LegacyItem { Path = Folder, Product = "MingToon Core", Blocked = true, BlockedReason = LegacyItem.ReasonManualMove };
        sample.Unmatched.Add(Folder + "/.git");
        sample.Unmatched.Add(Folder + "/.serena");
        var items = new List<LegacyItem> { sample };
        Equal("MingToon Core: 이전 설치를 아틀리에 매니저가 자동으로 정리할 수 없어요.\n이 폴더에 MingToon Core 배포 파일이 아닌 파일이 있어요 (예: .git, .serena).\n아래 폴더를 직접 프로젝트 밖으로 옮겨 주세요:\n  "
            + Folder + "\n옮긴 뒤 다시 설치해 주세요. 아무것도 옮기거나 지우지 않았어요.", LegacyInstallText.ManualMoveBody(0, items));
        Require(LegacyInstallText.ManualMoveBody(1, items).Contains("Move this folder out of the project yourself:\n  " + Folder + "\nThen install again. Nothing was moved or deleted."), LegacyInstallText.ManualMoveBody(1, items));
        Require(LegacyInstallText.ManualMoveBody(2, items).Contains("（例: .git, .serena）") && LegacyInstallText.ManualMoveBody(2, items).Contains("\n  " + Folder + "\n"), LegacyInstallText.ManualMoveBody(2, items));
        Equal(LegacyInstallText.ManualMoveBody(0, items), LegacyInstallText.BlockedBody(0, items));

        // Only an IDE folder, deeper down; a ".git" file (worktree pointer); the name in any case.
        var nested = BoothProject();
        Put(nested, Folder + "/Editor/.VS/state", "x");
        Equal(Folder + "/Editor/.VS", LegacyInstall.Detect(nested, CoreProbe()).Items.Single().Unmatched.Single());
        var pointer = BoothProject();
        Put(pointer, Folder + "/.git", "gitdir: C:/dev/mingtoon/.git/worktrees/a\n");
        var pointed = LegacyInstall.Detect(pointer, installer).Items.Single();
        Require(pointed.ManualMove && !pointed.HandOff, pointed.BlockedReason);
        Equal(Folder + "/.git", pointed.Unmatched.Single());
        // Not a match: a file that only starts with .git (.gitattributes, .gitignore) is a plain unmatched file, as before.
        var attributes = BoothProject();
        Put(attributes, Folder + "/.gitattributes", "* text=auto\n");
        var plain = LegacyInstall.Detect(attributes, CoreProbe()).Items.Single();
        Require(plain.Blocked && !plain.ManualMove && plain.BlockedReason == "unmatched", plain.BlockedReason);
    }

    private static void CleanInstallUnchanged()
    {
        DefaultHandlers();
        // A clean BOOTH folder: the Manager moves it after its usual question.
        var project = BoothProject();
        var plan = LegacyInstall.DetectKnown(project, CoreProbe());
        Require(!plan.Items.Single().Blocked && !plan.Items.Single().HandOff);
        Require(LegacyInstall.RefusalBeforeConfirm(plan) == null);
        var asked = 0;
        var move = LegacyInstall.Prepare(project, CoreProbe(), true, p => { asked++; return true; }, DateTime.Now);
        Equal(1, asked);
        Require(move != null && !Directory.Exists(Path.Combine(project, Folder.Replace('/', Path.DirectorySeparatorChar))));

        // No release manifest, installer at hand: the product installer hand-off, after its question.
        var installer = CoreProbe();
        installer[0].Installer = new LegacyInstallerFile { VersionId = "v-installer", FileName = "MingToon_Installer.unitypackage", Size = 1 };
        installer[0].Zip = new LegacyInstallerFile { VersionId = "v-zip", FileName = "com.studioraming.mingtoon-0.1.15.zip", Size = 1 };
        project = NewProject();
        Put(project, Folder + "/Shaders/MingToon.shader", "old");
        plan = LegacyInstall.DetectKnown(project, installer);
        Require(plan.HasHandoff && !plan.Items.Single().Blocked);
        Require(LegacyInstall.RefusalBeforeConfirm(plan) == null);
        LegacyHandoffRequest handoff;
        asked = 0;
        Require(LegacyInstall.Prepare(project, installer, true, p => { asked++; return true; }, DateTime.Now, out handoff) == null);
        Equal(1, asked);
        Require(handoff != null && handoff.Folder == Folder);

        // Review r1 M1 (a user's file, installer at hand): still the hand-off, no refusal before the question.
        project = BoothProject();
        Put(project, Folder + "/Presets/MyLook.asset", "mine");
        plan = LegacyInstall.DetectKnown(project, installer);
        Require(plan.HasHandoff);
        Require(LegacyInstall.RefusalBeforeConfirm(plan) == null);
        // The same without the installer: refused before the question, files named, no folder to move by hand.
        plan = LegacyInstall.DetectKnown(project, CoreProbe());
        var refusal = LegacyInstall.RefusalBeforeConfirm(plan);
        Require(refusal != null && refusal.ManualFolder.Length == 0);
        Require(refusal.Message.Contains(Folder + "/Presets/MyLook.asset"), refusal.Message);
        // Nothing old in the project: nothing to say.
        Require(LegacyInstall.RefusalBeforeConfirm(LegacyInstall.DetectKnown(NewProject(), CoreProbe())) == null);
    }

    private static string Snapshot(string project)
    {
        var files = Directory.GetFiles(project, "*", SearchOption.AllDirectories).Select(f => f.Substring(project.Length) + ":" + File.ReadAllText(f)).ToList();
        files.Sort(StringComparer.Ordinal);
        return string.Join("\n", files);
    }

    private static void CollectionAssertEqual(IList<string> expected, IList<string> actual)
    {
        if (!expected.SequenceEqual(actual)) throw new Exception("Expected [" + string.Join(", ", expected) + "], got [" + string.Join(", ", actual) + "]");
    }

    private static void NoManifestUnchanged()
    {
        DefaultHandlers();
        var project = NewProject();
        Put(project, Folder + "/Shaders/MingToon.shader", "old");
        // Legacy case (no package in Packages/), no installer file in the license version: the hand-off cannot start; the text says why.
        var e = Throws<LegacyCleanupRequiredException>(() => LegacyInstall.Prepare(project, CoreProbe(), true, p => true, DateTime.Now, out _));
        Require(e.Message.Length > 0);
        Equal(LegacyInstallText.HandoffUnavailable(LegacyInstallText.Language(), "MingToon Core", Folder, false), e.Message);
        // The package is already in Packages/ (VCC) and an old folder without a list is also there: no rule can prove it, it is named.
        Put(project, "Packages/" + Core + "/package.json", "{\"name\":\"" + Core + "\",\"version\":\"0.1.14\"}");
        var plan = LegacyInstall.Detect(project, CoreProbe());
        Require(plan.Items[0].Blocked);
        var ko = LegacyInstallText.BlockedBody(0, plan.BlockedItems());
        Require(ko.Contains("확인할 목록이 없어요"), ko);
    }

    private static void RealZip(string zip)
    {
        DefaultHandlers();
        var project = NewProject();
        var target = Path.Combine(project, Folder.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, target);
        File.Delete(Path.Combine(target, "package.json"));   // the old layout had no package.json (a folder with one is never moved)
        File.Delete(Path.Combine(target, "package.json.meta"));
        var plan = LegacyInstall.Detect(project, CoreProbe());
        Equal(1, plan.Items.Count);
        Require(!plan.Items[0].Blocked, string.Join("\n", plan.Items[0].Unmatched.Take(10)));
        Put(project, Folder + "/Presets/Mine.asset", "user preset");
        plan = LegacyInstall.Detect(project, CoreProbe());
        Equal(Folder + "/Presets/Mine.asset", string.Join(",", plan.Items[0].Unmatched));
    }

    // ---------- add-ons wait for MingToon Core ----------

    private static void CoreFirstOrder()
    {
        Equal(Core, CoreAddons.CoreOf(Urp, ""));
        Equal(Core, CoreAddons.CoreOf("", Mlc));
        Equal("", CoreAddons.CoreOf(Core, ""));
        Equal("", CoreAddons.CoreOf("com.studioraming.mingtoonx", ""));
        Equal("", CoreAddons.CoreOf("com.other.tool", "some-license"));
        var items = new List<string[]>
        {
            new[] { Urp, "" }, new[] { "com.other.tool", "" }, new[] { "", Mlc }, new[] { Core, "" }, new[] { "", "texture-tool" }
        };
        var ordered = CoreAddons.OrderCoreFirst(items, x => x[0], x => x[1]).Select(x => x[0].Length > 0 ? x[0] : x[1]).ToList();
        Equal(string.Join(",", new[] { Core, "com.other.tool", "texture-tool", Urp, Mlc }), string.Join(",", ordered));
    }

    private static void AddonStateAndNest()
    {
        Equal(AddonState.Ready, CoreAddons.StateOf("", false, false));
        Equal(AddonState.Ready, CoreAddons.StateOf(Core, true, false));
        Equal(AddonState.InstallCoreFirst, CoreAddons.StateOf(Core, false, true));
        Equal(AddonState.CoreMissing, CoreAddons.StateOf(Core, false, false));

        Func<string, string, string, ListLine> row = (name, package, license) => new ListLine
        {
            Entry = new CatalogEntry { Key = "l:" + name, Name = name, Kind = FavoriteKind.License, PackageName = package, License = new LicenseEntry { license_id = license } }
        };
        var lines = new List<ListLine>
        {
            new ListLine { Header = true, Title = "all" },
            row("URP Addon", Urp, "u"), row("MaskMaker", "", "m"), row("MLC", "", Mlc), row("MingToon Core", Core, "c"), row("Face SDF", "", "f")
        };
        var nested = CoreAddons.Nest(lines);
        Equal("all|MaskMaker|MingToon Core|URP Addon|MLC|Face SDF", string.Join("|", nested.Select(l => l.Header ? l.Title : l.Entry.Name)));
        Equal("False|False|False|True|True|False", string.Join("|", nested.Select(l => l.Child.ToString())));
        // No core row in the group (core not owned, or filtered out): the add-ons keep their place, not indented.
        var alone = CoreAddons.Nest(new List<ListLine> { row("URP Addon", Urp, "u"), row("MaskMaker", "", "m") });
        Equal("URP Addon|MaskMaker", string.Join("|", alone.Select(l => l.Entry.Name)));
        Require(alone.All(l => !l.Child));
    }

    // ---------- what a script reload must not drop ----------

    // Core + MLC in one run, the run cut by a reload after the core: only the SessionState text is left. After the "reload" a fresh parse
    // decides: core in = MLC installs; core not in = MLC reported, never silently gone; a record from long ago starts nothing.
    private static void AddonSurvivesReload()
    {
        var now = 1793520000L;
        var text = ReloadCarry.AddAddon("", new PendingAddon { AddonKey = "license:mlc", LicenseId = Mlc, Core = Core, Name = "Ming\tLight Controller", SinceUnix = now });
        text = ReloadCarry.AddAddon(text, new PendingAddon { AddonKey = "license:mlc", LicenseId = Mlc, Core = Core, Name = "Ming Light Controller", SinceUnix = now + 5 });
        text = ReloadCarry.AddAddon(text, new PendingAddon { AddonKey = "license:old", LicenseId = "x", Core = Core, Name = "Old", SinceUnix = now - ReloadCarry.MaxAgeSeconds - 10 });
        var parsed = ReloadCarry.ParseAddons(text);   // the "reload": nothing but the text
        Equal(2, parsed.Count);
        Equal("Ming Light Controller", parsed.Single(a => a.AddonKey == "license:mlc").Name);

        var project = NewProject();
        var install = new List<PendingAddon>();
        var missing = new List<PendingAddon>();
        ReloadCarry.Split(parsed, now + 60, core => CoreAddons.CoreIsHere(project, core), install, missing);
        Equal(0, install.Count);
        Equal("license:mlc", missing.Single().AddonKey);   // core not in: said, not dropped silently; the old one is dropped

        Put(project, "Packages/" + Core + "/package.json", "{\"name\":\"" + Core + "\",\"version\":\"0.1.15\"}");
        install.Clear(); missing.Clear();
        ReloadCarry.Split(parsed, now + 60, core => CoreAddons.CoreIsHere(project, core), install, missing);
        Equal("license:mlc", install.Single().AddonKey);
        Equal(Mlc, install[0].LicenseId);
        Equal(0, missing.Count);
        Equal("", ReloadCarry.RemoveAddon(ReloadCarry.RemoveAddon(text, "license:mlc"), "license:old"));

        // The batch's row reasons (multi-line, tabs) and the retry selection come back once, not from long ago.
        var rows = new List<CarriedRow>
        {
            new CarriedRow { Key = "license:core", Cleanup = true, Text = "MingToon Core: 이전 설치 정리 필요\nAssets/StudioRaming/MingToon/Presets/Mine.asset\t(직접 만든 파일)" },
            new CarriedRow { Key = "license:mlc", Text = "MingToon Core가 이 프로젝트에 없어서 설치하지 않았어요.\\ 다음: ..." }
        };
        var carried = ReloadCarry.FormatRows(now, new[] { "license:core", "license:mlc" }, rows);
        var retry = new List<string>();
        var back = new List<CarriedRow>();
        Require(ReloadCarry.ParseRows(carried, now + 30, retry, back));
        Equal("license:core,license:mlc", string.Join(",", retry));
        Equal(2, back.Count);
        Require(back[0].Cleanup && !back[1].Cleanup);
        Equal(rows[0].Text, back[0].Text);
        Equal(rows[1].Text, back[1].Text);
        Require(!ReloadCarry.ParseRows(carried, now + ReloadCarry.RowsMaxAgeSeconds + 1, new List<string>(), new List<CarriedRow>()), "stale rows restored");
    }

    // Review r1 m5: the VCC switch is kept when something it moved got the Manager's copy; the URP add-on's item is not the core's.
    private static void ItemNamesExact()
    {
        Require(BatchQueue.ItemIsPackage(Core + " 0.1.15", Core));
        Require(!BatchQueue.ItemIsPackage(Urp + " 0.1.15", Core), "URP counted as the core");
        Require(BatchQueue.ItemIsPackage(Urp + " 0.1.15", Urp));
        Require(!BatchQueue.ItemIsPackage("file:v-mlc", Core));
        var reason = InstallResultText.Reason(0, new AddonNeedsCoreException(Core, "Needs MingToon Core"));
        Equal("MingToon Core가 이 프로젝트에 없어서 설치하지 않았어요.", reason);
        Equal(InstallFailureKind.Dependency, InstallResultText.Classify(new AddonNeedsCoreException(Core, "x")));
    }

    // ---------- helpers ----------

    private static T Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T e) { return e; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string detail = "") { if (!condition) throw new Exception("Assertion failed " + detail); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
