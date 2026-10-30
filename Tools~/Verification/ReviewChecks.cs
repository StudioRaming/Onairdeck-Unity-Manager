using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// Review fixes R1-R7 and contract v7 (device_released). Real sources: LicenseFile, LicenseFilePlan, PathSafety,
// DeviceLimitNotice, BatchQueue, ApiErrors, UpdateCheckLogic, PurchaseWatcherLogic. Unity-side plumbing (the header
// on the wire, LicenseService) is compile-checked only.
internal static class ReviewChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("R1: a license file issued to another account or PC is due, even when valid", LicenseOwner);
        check("R2: a background refusal keeps the paused batch's retry; resume runs both", RetryChain);
        check("R2: a repeated background report keeps one retry; discarding the batch's own leaves the rest", RetryKeyAndDiscard);
        check("R3: cancel while an item runs, then it is refused: the queue ends, nothing waits", CancelWhileRunning);
        check("R4: long license names keep their id suffix and stay apart", LongNames);
        check("R5: a purchase list is recorded only for the account that asked", ShouldApplyAccount);
        check("R6: Windows reserved names get a prefix", ReservedNames);
        check("R7: the seen list keeps the newest 1000 keys", SeenCap);
        check("v7: 409 device_released parses (licenses), device_limit wins, other 409 stays ApiException", ReleasedParse);
        check("v7: released notice, batch pauses on it, reactivate header and texts exist", ReleasedFlow);
        check("R12: a notice of another kind does not drop the paused batch's resume (device_released arrives while paused on device_limit)", CrossingKinds);
    }

    private static string B64(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string License(string json) { return B64(json) + ".sig"; }

    private static DeviceLimitException Limit()
    {
        return (DeviceLimitException)ApiErrorParser.ToException(409, "{\"error\":\"device_limit\",\"message\":\"Too many devices.\"}", "x", null);
    }

    private static DeviceReleasedException Released()
    {
        return (DeviceReleasedException)ApiErrorParser.ToException(409, "{\"error\":\"device_released\",\"message\":\"Released.\",\"licenses\":[{\"license_id\":\"L1\",\"name\":\"Pack\"}]}", "x", null);
    }

    private static void Wait(Task task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(10))) throw new Exception("Timed out.");
    }

    private static void WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for: " + what);
            Thread.Sleep(5);
        }
    }

    // ---------- R1 ----------

    private static void LicenseOwner()
    {
        const long now = 1792000000;
        var far = now + 10 * 86400;
        var forA = License("{\"user_id\":\"userA\",\"exp\":" + far + "}");
        Require(!LicenseFile.IsDue(forA, now, "userA", "hash1"));            // same account, no device field: keep
        Require(LicenseFile.IsDue(forA, now, "userB", "hash1"));             // switched to B: reissue
        Require(!LicenseFile.IsDue(forA, now, null, null));                  // nothing to compare against
        var withDevice = License("{\"user_id\":\"userA\",\"device\":\"hash1\",\"exp\":" + far + "}");
        Require(!LicenseFile.IsDue(withDevice, now, "userA", "hash1"));
        Require(LicenseFile.IsDue(withDevice, now, "userA", "hash2"));       // another PC: reissue
        Require(LicenseFile.IsDue(License("{\"user_id\":\"userA\",\"exp\":" + (now + 3600) + "}"), now, "userA", null)); // inside the 2 day window
        Require(LicenseFile.IsDue(License("{\"exp\":" + far + "}"), now, "userA", null)); // no user_id at all: cannot be this account's
        Require(LicenseFile.IsDue("garbage", now, "userA", null));
        Equal(false, LicenseFile.IsDue(License("{\"user_id\":\"userA\",\"exp\":" + far + "}"), now, "USERA", null)); // ids are compared without case
    }

    // ---------- R2 ----------

    private static void RetryChain()
    {
        DeviceLimitNotice.Clear();
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = "A", Run = r => { throw Limit(); } }
        });
        var calls = 0;
        var item = new BatchItem
        {
            Name = "B",
            Run = r => { calls++; if (calls == 1) throw Limit(); return Task.FromResult<object>(null); }
        };
        queue = new BatchQueue(new[] { item });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsPaused, "the queue to pause");
        // A background refresh reports the same kind of refusal with its own retry, after the batch's.
        var background = 0;
        Func<Task<object>> bg = () => { background++; return Task.FromResult<object>("bg"); };
        Require(DeviceLimitNotice.Report(Limit(), "Renewal", bg, "ensure"));
        Require(DeviceLimitNotice.Current.Retry != null);
        var result = DeviceLimitNotice.RetryAsync().GetAwaiter().GetResult();
        Equal("bg", result);                       // the chain ran the background retry after the batch's resume
        Equal(1, background);
        Wait(task);                                // the batch resumed: B ran again and finished
        Equal(2, calls);
        Equal(BatchStatus.Done, queue.Results[0].Status);
        Require(DeviceLimitNotice.Current == null);
    }

    private static void RetryKeyAndDiscard()
    {
        DeviceLimitNotice.Clear();
        var a = 0; var b = 0; var own = 0;
        Func<Task<object>> first = () => { a++; return Task.FromResult<object>(null); };
        Func<Task<object>> second = () => { b++; return Task.FromResult<object>(null); };
        Func<Task<object>> mine = () => { own++; return Task.FromResult<object>(null); };
        DeviceLimitNotice.Report(Limit(), "x", mine);
        DeviceLimitNotice.Report(Limit(), "x", first, "ensure");
        DeviceLimitNotice.Report(Limit(), "x", second, "ensure");           // same key: replaces `first`, no second copy
        DeviceLimitNotice.Current.Retry().GetAwaiter().GetResult();
        Equal(0, a); Equal(1, b); Equal(1, own);
        DeviceLimitNotice.Discard(mine);
        Require(DeviceLimitNotice.Current != null);                          // the background retry is still there
        DeviceLimitNotice.Current.Retry().GetAwaiter().GetResult();
        Equal(2, b); Equal(1, own);
        DeviceLimitNotice.Discard(second);
        Require(DeviceLimitNotice.Current == null);                          // last retry gone: the notice goes
        // Retries a service reported while a batch item ran are dropped when the batch pauses; earlier ones stay.
        DeviceLimitNotice.Report(Limit(), "early", first, "ensure");
        var mark = DeviceLimitNotice.Mark();
        DeviceLimitNotice.Report(Limit(), "late", second, "install");
        DeviceLimitNotice.DropSince(mark);
        DeviceLimitNotice.Current.Retry().GetAwaiter().GetResult();
        Equal(1, a); Equal(2, b);
        DeviceLimitNotice.Clear();
    }

    // ---------- R12 ----------

    private static void CrossingKinds()
    {
        DeviceLimitNotice.Clear();
        var calls = 0;
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = "B", Run = r => { calls++; if (calls == 1) throw Limit(); return Task.FromResult<object>(null); } }
        });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsPaused, "the queue to pause on device_limit");
        // A background refusal of ANOTHER kind arrives (no retry of its own): the notice changes, the batch's resume must stay reachable.
        Require(DeviceLimitNotice.Report(Released(), "Renewal", (Func<Task<object>>)null));
        Equal(NoticeKind.DeviceReleased, DeviceLimitNotice.Current.Kind);
        Require(DeviceLimitNotice.Current.Retry != null);
        DeviceLimitNotice.RetryAsync().GetAwaiter().GetResult();
        Wait(task);
        Equal(2, calls);                                                   // the paused item ran again
        Equal(BatchStatus.Done, queue.Results[0].Status);
        Require(DeviceLimitNotice.Current == null);
        // A background retry of another kind joins the carried one and runs after it.
        calls = 0;
        var queue2 = new BatchQueue(new[]
        {
            new BatchItem { Name = "C", Run = r => { calls++; if (calls == 1) throw Limit(); return Task.FromResult<object>(null); } }
        });
        var task2 = queue2.RunAsync();
        WaitFor(() => queue2.IsPaused, "the second queue to pause");
        var bg = 0;
        DeviceLimitNotice.Report(Released(), "Renewal", () => { bg++; return Task.FromResult<object>("bg"); }, "ensure");
        Equal("bg", DeviceLimitNotice.RetryAsync().GetAwaiter().GetResult());
        Wait(task2);
        Equal(1, bg);
        Equal(2, calls);
        DeviceLimitNotice.Clear();
    }

    // ---------- R3 ----------

    private static void CancelWhileRunning()
    {
        DeviceLimitNotice.Clear();
        var gate = new TaskCompletionSource<bool>();
        var ranC = false;
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = "A", Run = async r => { await gate.Task; throw Limit(); } },
            new BatchItem { Name = "C", Run = r => { ranC = true; return Task.FromResult<object>(null); } }
        });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsRunning, "the queue to start");
        queue.Cancel();
        gate.SetResult(true);                       // now A fails with a refusal
        Wait(task);                                 // before the fix this waited for a resume nobody would give
        Require(!queue.IsPaused);
        Equal(BatchStatus.Skipped, queue.Results[0].Status);
        Equal("Cancelled.", queue.Results[0].Message);
        Equal(BatchStatus.Skipped, queue.Results[1].Status);
        Require(!ranC);
        Require(DeviceLimitNotice.Current == null);
    }

    // ---------- R4 ----------

    private static LicenseEntry Entry(string id, string name)
    {
        return new LicenseEntry
        {
            license_id = id,
            name = name,
            versions = new[] { new LicenseVersionEntry { version_id = id + "v", package_name = "com.example." + id, package_version = "1.0.0" } }
        };
    }

    private static void LongNames()
    {
        var common = new string('x', 200);
        var one = Entry("11111111-aaaa", common + "A");
        var two = Entry("22222222-bbbb", common + "B");
        var folder = LicenseFilePlan.FolderFor(one, true);
        Require(folder.EndsWith(" (11111111)"));
        Require(folder.Length <= "Assets/StudioRaming_Onairdeck/".Length + 150);
        Require(LicenseFilePlan.FolderFor(two, true) != folder);
        var jobs = LicenseFilePlan.Select(new[] { one, two }, name => true, f => true);
        Equal(2, jobs.Count);
        Require(jobs[0].Folder != jobs[1].Folder);
        Require(jobs[0].Folder.EndsWith(" (11111111)") && jobs[1].Folder.EndsWith(" (22222222)"));
        Equal("Assets/StudioRaming_Onairdeck/Short", LicenseFilePlan.FolderFor(Entry("33333333", "Short"), false));
        Equal("Assets/StudioRaming_Onairdeck/Short (33333333)", LicenseFilePlan.FolderFor(Entry("33333333", "Short"), true));
        Equal("Assets/StudioRaming_Onairdeck/Product (33333333)", LicenseFilePlan.FolderFor(Entry("33333333", ".."), true));
    }

    // ---------- R5 ----------

    private static void ShouldApplyAccount()
    {
        Require(UpdateCheckLogic.ShouldApply("A", "A"));
        Require(!UpdateCheckLogic.ShouldApply("A", "B"));    // switched mid-request
        Require(!UpdateCheckLogic.ShouldApply("A", null));   // signed out mid-request
        Require(!UpdateCheckLogic.ShouldApply(null, "A"));   // the request was not made for a known account
        Require(!UpdateCheckLogic.ShouldApply("", ""));
    }

    // ---------- R6 ----------

    private static void ReservedNames()
    {
        Equal("_CON", PathSafety.SafeFolderName("CON", "Product"));
        Equal("_nul", PathSafety.SafeFolderName("nul", "Product"));
        Equal("_nul.txt", PathSafety.SafeFileName("nul.txt"));
        Equal("_Com1", PathSafety.SafeFileName("Com1"));
        Equal("_LPT9.log", PathSafety.SafeFileName("LPT9.log"));
        Equal("_Aux", PathSafety.SafeFileName("Aux."));      // the trailing dot goes first, then it is a device name
        Equal("Console", PathSafety.SafeFileName("Console"));
        Equal("COM10", PathSafety.SafeFileName("COM10"));    // only COM1-9 are devices
        Equal("Auxiliary.txt", PathSafety.SafeFileName("Auxiliary.txt"));
        Equal("a_b", PathSafety.SafeFileName("a:b".Replace(':', '_')));
        // A reserved folder name still gets the long-name suffix intact.
        Equal("_CON (12345678)", PathSafety.SafeFolderName("CON", "Product", " (12345678)"));
    }

    // ---------- R7 ----------

    private static void SeenCap()
    {
        var keys = new List<string>();
        for (var i = 0; i < 1500; i++) keys.Add("p:" + i.ToString("D4"));
        var text = PurchaseWatcherLogic.MarkSeen(null, keys);
        var parsed = new HashSet<string>(text.Split('\n'));
        Equal(PurchaseWatcherLogic.MaxSeen, parsed.Count);
        Require(parsed.Contains("p:1499") && parsed.Contains("p:0500"));
        Require(!parsed.Contains("p:0499") && !parsed.Contains("p:0000"));
        // Adding to a full list drops the oldest, not the new ones; adding a known key changes nothing.
        var next = PurchaseWatcherLogic.MarkSeen(text, new[] { "p:new", "p:1499" });
        var after = new HashSet<string>(next.Split('\n'));
        Equal(PurchaseWatcherLogic.MaxSeen, after.Count);
        Require(after.Contains("p:new") && !after.Contains("p:0500") && after.Contains("p:0501"));
        Equal(text, PurchaseWatcherLogic.MarkSeen(text, new[] { "p:1499" }));
        // Text an older Manager stored (sorted, no cap) still parses.
        Equal("p:a\np:b", PurchaseWatcherLogic.MarkSeen("p:a\np:b", new string[0]));
        // The first load is capped too.
        var purchases = new List<PurchaseItem>();
        for (var i = 0; i < 1200; i++) purchases.Add(new PurchaseItem { name = "n" + i, product_id = "id" + i.ToString("D4"), files = new[] { new PurchaseFile { file_id = "f" } } });
        var first = PurchaseWatcherLogic.Diff(null, new PurchasesResponse { purchases = purchases.ToArray(), licenses = new LicenseEntry[0] });
        Equal(PurchaseWatcherLogic.MaxSeen, first.FirstLoadText.Split('\n').Length);
    }

    // ---------- v7 ----------

    private static void ReleasedParse()
    {
        var released = Released();
        Equal(409L, released.Status);
        Equal("device_released", released.Code);
        Equal("Released.", released.Message);
        Equal(1, released.Licenses.Length);
        Equal("L1", released.Licenses[0].license_id);
        Equal("Pack", released.Licenses[0].name);
        // device_limit wins when both are in play, a plain 409 stays a plain ApiException, 426 is unchanged.
        var both = ApiErrorParser.ToException(409, "{\"error\":\"device_limit\",\"message\":\"m\",\"limit\":1,\"devices\":[]}", "x", null);
        Require(both is DeviceLimitException);
        var other = ApiErrorParser.ToException(409, "{\"error\":\"conflict\",\"message\":\"m\"}", "x", null);
        Require(!(other is DeviceReleasedException) && !(other is DeviceLimitException));
        Require(ApiErrorParser.ToException(426, "{\"error\":\"manager_update_required\",\"message\":\"m\"}", "x", null) is ManagerUpdateRequiredException);
        // No licenses array: empty list, not a crash.
        var bare = (DeviceReleasedException)ApiErrorParser.ToException(409, "{\"error\":\"device_released\"}", "x", null);
        Equal(0, bare.Licenses.Length);
    }

    private static void ReleasedFlow()
    {
        DeviceLimitNotice.Clear();
        var reactivated = 0;
        Func<Task<object>> retry = () => { reactivated++; return Task.FromResult<object>(null); };
        Require(DeviceLimitNotice.Report(Released(), "Renewal", retry, "ensure"));
        Equal(NoticeKind.DeviceReleased, DeviceLimitNotice.Current.Kind);
        Equal(1, DeviceLimitNotice.Current.Released.Licenses.Length);
        // A refusal of another kind replaces the notice instead of joining it.
        DeviceLimitNotice.Report(Limit(), "Renewal", retry, "ensure");
        Equal(NoticeKind.DeviceLimit, DeviceLimitNotice.Current.Kind);
        DeviceLimitNotice.Clear();

        // A batch pauses on a released device and resumes on the retry.
        var runs = 0;
        var queue = new BatchQueue(new[]
        {
            new BatchItem { Name = "A", Run = r => { runs++; if (runs == 1) throw Released(); return Task.FromResult<object>(null); } }
        });
        var task = queue.RunAsync();
        WaitFor(() => queue.IsPaused, "the queue to pause on device_released");
        Equal(NoticeKind.DeviceReleased, DeviceLimitNotice.Current.Kind);
        DeviceLimitNotice.RetryAsync().GetAwaiter().GetResult();
        Wait(task);
        Equal(2, runs);
        Equal(BatchStatus.Done, queue.Results[0].Status);

        Equal("X-Atelier-Reactivate", DeviceIdentityCore.ReactivateHeaderName);
        Equal("1", DeviceIdentityCore.ReactivateHeaderValue);
        var titles = new HashSet<string>();
        for (var language = 0; language < 3; language++)
        {
            Require(DeviceReleasedText.Title(language).Length > 0 && DeviceReleasedText.Body(language).Length > 0 && DeviceReleasedText.Button(language).Length > 0);
            titles.Add(DeviceReleasedText.Title(language));
        }
        Equal(3, titles.Count);
        Equal("이 PC에서 다시 사용", DeviceReleasedText.Button(0));
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
