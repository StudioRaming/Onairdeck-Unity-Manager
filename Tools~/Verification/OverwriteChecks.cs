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
        // A real MingToon zip (its Release/MingToon_ReleaseManifest.json lists Assets/StudioRaming/MingToon paths) unpacked as the old layout.
        var realZip = Environment.GetEnvironmentVariable("ONAIRDECK_MINGTOON_ZIP");
        if (!string.IsNullOrEmpty(realZip)) check("old BOOTH folder: the real MingToon " + Path.GetFileName(realZip) + " unpacked under " + Folder + " proves itself", () => RealZip(realZip));
        check("add-ons: URP (package name) and MLC (license table) need MingToon Core; core first, add-ons last in a run", CoreFirstOrder);
        check("add-ons: blocked state (core missing: install it first when offered, else explain); rows nest under the core", AddonStateAndNest);
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
        Equal(Folder + "/.git", plan.Items[0].Unmatched.Single());
        ko = LegacyInstallText.BlockedBody(0, plan.BlockedItems());
        Require(ko.Contains("개발용 git 작업 폴더라 아틀리에 매니저가 옮기지 않아요"), ko);
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
