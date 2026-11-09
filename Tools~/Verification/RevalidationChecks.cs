using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using OnAirDeck.UnityManager;

// Online re-check of license files on disk (Editor/License/LicenseRevalidation.cs): the answer rules, the scan, which files
// are asked again, and a pass over a temp project with a fake server. The requests and AssetDatabase removal in
// LicenseService.RevalidateAsync need a real Editor; this composes the same pure parts with File.Delete.
internal static class RevalidationChecks
{
    private const string User = "11111111-1111-4111-8111-111111111111";
    private const string Other = "22222222-2222-4222-8222-222222222222";
    private const string Refunded = "aaaaaaaa-0000-4000-8000-000000000001";
    private const string Kept = "aaaaaaaa-0000-4000-8000-000000000002";
    private const string Offline = "aaaaaaaa-0000-4000-8000-000000000003";
    private const string RefundedItem = "bbbbbbbb-0000-4000-8000-000000000001";
    private const long Now = 1800000000;

    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "revalidation-project");
        check("revalidation: delete on 404, 403 refunded / not paid / no active grant, 409 device_released; keep otherwise", ClassifyStatus);
        check("revalidation: exceptions map the same way; offline, 401, 426, 429, 5xx, device_limit and a 200 keep the file", ClassifyExceptions);
        check("revalidation: the scan reads license_id, item_id and user_id; own-product and unreadable files are left out", ScanFiles);
        check("revalidation: a right missing from the list is asked hourly, every file every 6 h, another account's never", SelectFiles);
        check("revalidation: at most MaxPerPass per pass, most overdue first; one request per right", SelectCap);
        check("revalidation pass: the refunded file is removed, the kept file of the same account and an offline answer stay", Pass);
        check("revalidation: the last-checked map round-trips and skips bad lines", CheckedMap);
        check("revalidation: a license that comes back renews the file (same account and right, never an earlier exp)", Renew);
        check("license file write: swapped in whole, no temp left; a failed write keeps the old file", AtomicWrite);
    }

    private static void AtomicWrite()
    {
        Reset();
        var path = Put("Atomic Tool", License(User, Kept, ""));
        var dir = Path.GetDirectoryName(path);
        LicenseFile.WriteFileAtomic(path, "new.c2ln\n");
        Equal("new.c2ln\n", File.ReadAllText(path));
        Equal(1, Directory.GetFiles(dir).Length);
        var fresh = Path.Combine(dir, "fresh.license");
        LicenseFile.WriteFileAtomic(fresh, "first.c2ln\n");
        Equal("first.c2ln\n", File.ReadAllText(fresh));
        // A temp path that cannot be written (a folder stands in its place): the old file stays as it was.
        var blocker = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp");
        Directory.CreateDirectory(blocker);
        var threw = false;
        try { LicenseFile.WriteFileAtomic(path, "lost.c2ln\n"); } catch (Exception) { threw = true; }
        Require(threw, "a failed write did not report the error");
        Equal("new.c2ln\n", File.ReadAllText(path));
        Directory.Delete(blocker);
    }

    private static string LicenseExp(string user, string licenseId, string itemId, long exp)
    {
        var payload = "{\"v\":1,\"iss\":\"onairdeck\",\"client\":\"unity-manager\",\"user_id\":\"" + user + "\",\"item_id\":\"" + itemId
            + "\",\"product_id\":\"p\"," + (licenseId != null ? "\"license_id\":\"" + licenseId + "\",\"kind\":\"license\"," : "")
            + "\"iat\":" + (exp - 14 * 86400) + ",\"exp\":" + exp + ",\"nonce\":\"r\"}";
        return B64(payload) + ".c2ln";
    }

    private static void Renew()
    {
        Reset();
        Put("Kept Tool", License(User, Kept, ""));
        Put("Kept Tool/Nested", License(User, Kept, ""));
        Put("Refunded Line", License(User, null, RefundedItem));
        var files = LicenseRevalidation.Scan(_root);
        var kept = files.Find(f => f.LicenseId == Kept);
        var line = files.Find(f => f.ItemId == RefundedItem);
        var fresh = LicenseExp(User, Kept, "", Now + 14 * 86400);
        // Every folder holding the right is renewed; the file's own exp is Now + 6 days.
        Equal(2, LicenseRevalidation.SameRight(files, kept).FindAll(f => LicenseRevalidation.CanRenew(f, kept, fresh)).Count);
        Require(LicenseRevalidation.CanRenew(line, line, LicenseExp(User, null, RefundedItem, Now + 14 * 86400)), "an order-line file was not renewed");
        Require(!LicenseRevalidation.CanRenew(kept, kept, LicenseExp(Other, Kept, "", Now + 14 * 86400)), "another account's license replaced the file");
        Require(!LicenseRevalidation.CanRenew(kept, kept, LicenseExp(User, Offline, "", Now + 14 * 86400)), "another license replaced the file");
        Require(!LicenseRevalidation.CanRenew(line, line, LicenseExp(User, null, Offline, Now + 14 * 86400)), "another order line replaced the file");
        Require(!LicenseRevalidation.CanRenew(kept, kept, LicenseExp(User, Kept, "", Now + 86400)), "a license expiring earlier replaced the file");
        Require(!LicenseRevalidation.CanRenew(kept, line, fresh), "a file of another right was renewed");
        Require(!LicenseRevalidation.CanRenew(kept, kept, ""), "an empty answer renewed the file");
        Require(!LicenseRevalidation.CanRenew(kept, kept, "not-a-license"), "an unreadable answer renewed the file");
    }

    private static string B64(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string License(string user, string licenseId, string itemId)
    {
        var payload = "{\"v\":1,\"iss\":\"onairdeck\",\"client\":\"unity-manager\",\"user_id\":\"" + user + "\",\"item_id\":\"" + itemId
            + "\",\"product_id\":\"" + (licenseId ?? "p") + "\"," + (licenseId != null ? "\"license_id\":\"" + licenseId + "\",\"kind\":\"license\"," : "")
            + "\"iat\":" + (Now - 100) + ",\"exp\":" + (Now + 6 * 86400) + ",\"nonce\":\"n\"}";
        return B64(payload) + ".c2ln";
    }

    private static string Put(string folder, string text)
    {
        var dir = Path.Combine(_root, "Assets", "StudioRaming_Onairdeck", folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, LicenseFile.FileName);
        File.WriteAllText(path, text + "\n", new UTF8Encoding(false));
        return path;
    }

    private static void Reset()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        Directory.CreateDirectory(_root);
    }

    private static void ClassifyStatus()
    {
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(404, "not found"));
        // A gateway 404 (no such function, wrong URL) is not plugin-license's answer.
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(404, ""));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(ApiErrorParser.ToException(404, "{\"code\":\"NOT_FOUND\",\"message\":\"Requested function was not found\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(403, "forbidden: refunded"));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(403, "forbidden: not paid"));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(403, "forbidden: no active license grant"));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(403, "forbidden: not your purchase"));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(409, "device_released"));
        // A license outlives the download window; an unknown 403 proves nothing.
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(403, "forbidden: download period expired"));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(403, "forbidden: something new"));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(403, null));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(409, "device_limit"));
        foreach (var status in new long[] { 0, 200, 400, 401, 426, 429, 500, 502, 503 })
            Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(status, "forbidden: refunded"));
    }

    private static void ClassifyExceptions()
    {
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify((Exception)null));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(ApiErrorParser.ToException(403, "{\"error\":\"forbidden: refunded\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(ApiErrorParser.ToException(403, "{\"error\":\"forbidden: no active license grant\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(ApiErrorParser.ToException(404, "{\"error\":\"not found\"}", "Issuing the license", null)));
        var released = ApiErrorParser.ToException(409, "{\"error\":\"device_released\",\"licenses\":[]}", "Issuing the license", null);
        Require(released is DeviceReleasedException);
        Equal(RevalidationAction.Delete, LicenseRevalidation.Classify(released));
        var limit = ApiErrorParser.ToException(409, "{\"error\":\"device_limit\",\"licenses\":[],\"devices\":[]}", "Issuing the license", null);
        Require(limit is DeviceLimitException);
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(limit));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(ApiErrorParser.ToException(426, "{\"error\":\"manager_update_required\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(ApiErrorParser.ToException(0, "", "Issuing the license", "Cannot resolve destination host")));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(ApiErrorParser.ToException(429, "{\"error\":\"Rate limit exceeded\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(ApiErrorParser.ToException(503, "{\"error\":\"forbidden: refunded\"}", "Issuing the license", null)));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(new SessionExpiredException("expired")));
        Equal(RevalidationAction.Keep, LicenseRevalidation.Classify(new Exception("Issuing the license failed: the server returned no license.")));
    }

    private static void ScanFiles()
    {
        Reset();
        Put("Refunded Tool", License(User, Refunded, ""));
        Put("Item File", License(User, null, RefundedItem));
        Put("Own Product", License(User, null, ""));
        Put("Broken", "not a license");
        // A folder the user moved into a group: the runtime check still finds it, so the re-check must too.
        Put("Group/Nested Tool", License(User, Kept, ""));
        Directory.CreateDirectory(Path.Combine(_root, "Assets", "StudioRaming_Onairdeck", "Empty"));
        // A file directly in the install root: the runtime check reads it too.
        File.WriteAllText(Path.Combine(_root, "Assets", "StudioRaming_Onairdeck", LicenseFile.FileName), License(User, Offline, ""));
        var files = LicenseRevalidation.Scan(_root);
        Equal(4, files.Count);
        var byFolder = new Dictionary<string, RevalidationFile>();
        foreach (var f in files) byFolder[f.Folder] = f;
        var lic = byFolder["Assets/StudioRaming_Onairdeck/Refunded Tool"];
        Equal(Refunded, lic.LicenseId);
        Equal("", lic.ItemId);
        Equal(User, lic.UserId);
        var item = byFolder["Assets/StudioRaming_Onairdeck/Item File"];
        Equal("", item.LicenseId);
        Equal(RefundedItem, item.ItemId);
        var nested = byFolder["Assets/StudioRaming_Onairdeck/Group/Nested Tool"];
        Equal(Kept, nested.LicenseId);
        Equal(Offline, byFolder["Assets/StudioRaming_Onairdeck"].LicenseId);
        Require(LicenseRevalidation.Scan(Path.Combine(_root, "missing")).Count == 0);
        Require(LicenseRevalidation.Scan(null).Count == 0);
    }

    private static RevalidationFile File_(string folder, string user, string licenseId, string itemId)
    {
        return new RevalidationFile { Folder = folder, UserId = user, LicenseId = licenseId ?? "", ItemId = itemId ?? "" };
    }

    private static void SelectFiles()
    {
        var refunded = File_("a", User, Refunded, null);
        var kept = File_("b", User, Kept, null);
        var other = File_("c", Other, Refunded, null);
        var item = File_("d", User, null, RefundedItem);
        var files = new List<RevalidationFile> { refunded, kept, other, item };
        var licenses = new[] { Kept.ToUpperInvariant() };
        var items = new string[0];

        // Never checked in this editor session: every file of the account, never another account's.
        var first = LicenseRevalidation.Select(files, User, licenses, items, null, Now);
        Equal(3, first.Count);
        Require(!first.Contains(other));

        // Checked 2 h ago: only the rights missing from the list.
        var map = new Dictionary<string, long>();
        foreach (var f in files) map[f.Key] = Now - 2 * 3600;
        var later = LicenseRevalidation.Select(files, User, licenses, items, map, Now);
        Equal(2, later.Count);
        Require(later.Contains(refunded) && later.Contains(item) && !later.Contains(kept));

        // Checked 30 min ago: nothing; 6 h ago: everything of the account again.
        foreach (var f in files) map[f.Key] = Now - 30 * 60;
        Equal(0, LicenseRevalidation.Select(files, User, licenses, items, map, Now).Count);
        foreach (var f in files) map[f.Key] = Now - LicenseRevalidation.AllFilesSeconds;
        Equal(3, LicenseRevalidation.Select(files, User, licenses, items, map, Now).Count);

        // A list that was not sent (null) counts as listed: no hourly checks from it.
        foreach (var f in files) map[f.Key] = Now - 2 * 3600;
        Equal(0, LicenseRevalidation.Select(files, User, null, null, map, Now).Count);

        // No signed-in user: nothing; the account's id compares case-insensitively.
        Equal(0, LicenseRevalidation.Select(files, "", licenses, items, null, Now).Count);
        Equal(3, LicenseRevalidation.Select(files, User.ToUpperInvariant(), licenses, items, null, Now).Count);

        // The key is per account and per right kind.
        Require(refunded.Key != other.Key);
        Require(File_("x", User, Refunded, null).Key != File_("x", User, null, Refunded).Key);
    }

    private static void SelectCap()
    {
        var files = new List<RevalidationFile>();
        var map = new Dictionary<string, long>();
        for (var i = 0; i < 25; i++)
        {
            var f = File_("f" + i, User, "aaaaaaaa-0000-4000-8000-" + i.ToString("D12"), null);
            files.Add(f);
            map[f.Key] = Now - LicenseRevalidation.AllFilesSeconds - i; // larger i = more overdue
        }
        var picked = LicenseRevalidation.Select(files, User, null, null, map, Now);
        Equal(LicenseRevalidation.MaxPerPass, picked.Count);
        Equal("f24", picked[0].Folder);
        // Two folders holding the same right: one request, both folders follow its answer.
        var twin = File_("twin", User, files[24].LicenseId, null);
        files.Add(twin);
        picked = LicenseRevalidation.Select(files, User, null, null, map, Now);
        var count = 0;
        foreach (var p in picked) if (p.Key == twin.Key) count++;
        Equal(1, count);
        Equal(2, LicenseRevalidation.SameRight(files, twin).Count);

        // Fifteen held licenses never checked, then a refunded one last in the scan: the refunded one goes first, so the
        // cap never leaves it behind; the next pass (after the first ten were recorded) takes the remaining held ones.
        var held = new List<RevalidationFile>();
        var heldIds = new List<string>();
        for (var i = 0; i < 15; i++)
        {
            var id = "cccccccc-0000-4000-8000-" + i.ToString("D12");
            held.Add(File_("h" + i, User, id, null));
            heldIds.Add(id);
        }
        var refunded = File_("refunded", User, Refunded, null);
        held.Add(refunded);
        var firstPass = LicenseRevalidation.Select(held, User, heldIds, null, null, Now);
        Equal(LicenseRevalidation.MaxPerPass, firstPass.Count);
        Require(firstPass[0] == refunded, "the refunded file was not asked first");
        var rotation = new Dictionary<string, long>();
        foreach (var f in firstPass) rotation[f.Key] = Now;
        var secondPass = LicenseRevalidation.Select(held, User, heldIds, null, rotation, Now + 60);
        Equal(6, secondPass.Count);
        foreach (var f in secondPass) Require(!rotation.ContainsKey(f.Key), "a file asked a minute ago was asked again");
    }

    private static void Pass()
    {
        Reset();
        var refundedPath = Put("Refunded Tool", License(User, Refunded, ""));
        var keptPath = Put("Kept Tool", License(User, Kept, ""));
        var offlinePath = Put("Offline Answer", License(User, Offline, ""));
        var itemPath = Put("Refunded Line", License(User, null, RefundedItem));
        var otherPath = Put("Other Account", License(Other, Refunded, ""));
        // The refunded license and line left the list; the kept one is still in it; the offline one is not, but the
        // server cannot be reached for it.
        var server = new Dictionary<string, Exception>
        {
            { Refunded, ApiErrorParser.ToException(403, "{\"error\":\"forbidden: no active license grant\"}", "Issuing the license", null) },
            { RefundedItem, ApiErrorParser.ToException(403, "{\"error\":\"forbidden: refunded\"}", "Issuing the license", null) },
            { Kept, null },
            { Offline, ApiErrorParser.ToException(0, "", "Issuing the license", "Cannot connect to destination host") },
        };
        var files = LicenseRevalidation.Scan(_root);
        var map = new Dictionary<string, long>();
        var asked = new List<string>();
        foreach (var job in LicenseRevalidation.Select(files, User, new[] { Kept }, new string[0], map, Now))
        {
            var id = job.LicenseId.Length > 0 ? job.LicenseId : job.ItemId;
            asked.Add(id);
            map[job.Key] = Now;
            if (LicenseRevalidation.Classify(server[id]) != RevalidationAction.Delete) continue;
            foreach (var f in LicenseRevalidation.SameRight(files, job))
                File.Delete(Path.Combine(_root, f.Folder.Replace('/', Path.DirectorySeparatorChar), LicenseFile.FileName));
        }
        Equal(4, asked.Count);
        Require(!File.Exists(refundedPath), "the refunded license file was not removed");
        Require(!File.Exists(itemPath), "the refunded line file was not removed");
        Require(File.Exists(keptPath), "the kept license file of the same account was removed");
        Require(File.Exists(offlinePath), "an offline answer removed a file");
        Require(File.Exists(otherPath), "another account's file was judged by this account");
        // An hour later only the offline one (still missing from the list) is asked again; the kept one waits for 6 h.
        files = LicenseRevalidation.Scan(_root);
        var again = LicenseRevalidation.Select(files, User, new[] { Kept }, new string[0], map, Now + LicenseRevalidation.OutsideListSeconds);
        Equal(1, again.Count);
        Equal(Offline, again[0].LicenseId);
    }

    private static void CheckedMap()
    {
        var map = new Dictionary<string, long> { { User + "|L:" + Refunded, Now }, { User + "|I:" + RefundedItem, Now - 5 } };
        var back = LicenseRevalidation.ParseChecked(LicenseRevalidation.FormatChecked(map));
        Equal(2, back.Count);
        Equal(Now, back[User + "|L:" + Refunded]);
        Equal(Now - 5, back[User + "|I:" + RefundedItem]);
        var bad = LicenseRevalidation.ParseChecked("no equals\n=5\nk=notanumber\nk2=7\n");
        Equal(1, bad.Count);
        Equal(7L, bad["k2"]);
        Equal(0, LicenseRevalidation.ParseChecked(null).Count);
        // Prune drops what no longer changes a decision: 6 h old or more, or in the future after a clock change.
        var pruned = LicenseRevalidation.Prune(new Dictionary<string, long>
        {
            { "fresh", Now - 60 }, { "old", Now - LicenseRevalidation.AllFilesSeconds }, { "future", Now + 3600 }
        }, Now);
        Equal(1, pruned.Count);
        Require(pruned.ContainsKey("fresh"));
    }

    private static void Require(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
