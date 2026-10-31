using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager;

// The one-button import (contract v1): when the Manager polls, which request it takes, what the confirm and the install end in,
// what a held request becomes after a reload, and which error answers drop a request.
internal static class ImportChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("import poll: never within 15 s of the last poll, focus triggers at once after that, the 60 s poll only with the window open", PollTiming);
        check("import poll: blocked while signed out, compiling, in play mode, during a legacy hand-off or while the window is busy", Blocked);
        check("import pick: newest usable Unity request; another target, unknown kind, missing subject, expired or skipped ones are passed over", Pick);
        check("import confirm: the refund warning only for a never-downloaded purchase with Unity files; Import goes on, Cancel declines, Not this project releases", Confirm);
        check("import resume: a recorded report is posted again, an open confirm is released, a package install is done only when that version is in, else interrupted; old records are forgotten", Resume);
        check("import waiting: a queued .unitypackage is done only when every import is in; cancelled, failed or forgotten imports fail with their code; a hand-off is done when its version is in; an hour without an end is interrupted", Waiting);
        check("import errors: 429, 5xx and no answer keep the request for later; 400, 401, 404, 409 and 410 drop it; failure codes match [a-z0-9_]{1,40}", Errors);
    }

    private static readonly DateTime Now = new DateTime(2026, 10, 31, 3, 20, 0, DateTimeKind.Utc);

    private static void PollTiming()
    {
        const long t = 1000000;
        Require(ImportRequestLogic.Due(ImportRequestLogic.Trigger.EditorFocus, false, t, 0));
        Require(!ImportRequestLogic.Due(ImportRequestLogic.Trigger.EditorFocus, true, t, t - 14));
        Require(ImportRequestLogic.Due(ImportRequestLogic.Trigger.EditorFocus, false, t, t - 15));
        Require(!ImportRequestLogic.Due(ImportRequestLogic.Trigger.WindowFocus, true, t, t - 10));
        Require(ImportRequestLogic.Due(ImportRequestLogic.Trigger.WindowFocus, true, t, t - 16));
        Require(!ImportRequestLogic.Due(ImportRequestLogic.Trigger.Load, true, t, t - 5));
        Require(!ImportRequestLogic.Due(ImportRequestLogic.Trigger.Periodic, true, t, t - 59));
        Require(ImportRequestLogic.Due(ImportRequestLogic.Trigger.Periodic, true, t, t - 60));
        Require(!ImportRequestLogic.Due(ImportRequestLogic.Trigger.Periodic, false, t, t - 600));
    }

    private static void Blocked()
    {
        Equal(null, ImportRequestLogic.Blocked(true, false, false, false, false));
        Equal("signed_out", ImportRequestLogic.Blocked(false, true, true, true, true));
        Equal("compiling", ImportRequestLogic.Blocked(true, true, false, false, false));
        Equal("play_mode", ImportRequestLogic.Blocked(true, false, true, false, false));
        Equal("legacy_handoff", ImportRequestLogic.Blocked(true, false, false, true, false));
        Equal("busy", ImportRequestLogic.Blocked(true, false, false, false, true));
    }

    private static ImportRequest Item(string id, string expires = "2026-10-31T03:40:00.000Z")
    {
        return new ImportRequest
        {
            request_id = id, target = "unity", kind = "item", item_id = "item-" + id, product_id = "p-" + id, expires_at = expires,
            purchase = new PurchaseItem { name = "Product " + id, item_id = "item-" + id, files = new[] { new PurchaseFile() }, downloaded = false },
            licenses = new LicenseEntry[0],
        };
    }

    private static void Pick()
    {
        var expired = Item("a", "2026-10-31T03:19:59.000Z");
        var warudo = Item("b"); warudo.target = "warudo";
        var odd = Item("c"); odd.kind = "bundle";
        var noItem = Item("d"); noItem.item_id = "";
        var noId = Item("");
        var skipped = Item("e");
        var good = Item("f");
        var older = Item("g");
        var list = new[] { expired, warudo, odd, noItem, noId, skipped, good, older };
        Equal("f", ImportRequestLogic.Pick(list, Now, new HashSet<string> { "e" }).request_id);
        Equal("e", ImportRequestLogic.Pick(list, Now, null).request_id);
        Equal(null, ImportRequestLogic.Pick(null, Now, null));
        // A license request needs its license id; an unreadable expiry is left to the server.
        var license = new ImportRequest { request_id = "l", target = "unity", kind = "license", license_id = "", expires_at = "soon" };
        Require(!ImportRequestLogic.IsUsable(license, Now));
        license.license_id = "lic-1";
        Require(ImportRequestLogic.IsUsable(license, Now));
        // An older server without a target field is still the Unity list (the query asked for unity).
        var untargeted = Item("h"); untargeted.target = "";
        Require(ImportRequestLogic.IsUsable(untargeted, Now));
    }

    private static void Confirm()
    {
        var item = Item("a");
        Require(ImportRequestLogic.WarnsRefund(item));
        Equal("Product a", ImportRequestLogic.ProductName(item));
        item.purchase.downloaded = true;
        Require(!ImportRequestLogic.WarnsRefund(item));
        // Files through a license (no Unity files on the purchase) are not the purchase's first download.
        var viaLicense = Item("b"); viaLicense.purchase.files = new PurchaseFile[0];
        Require(!ImportRequestLogic.WarnsRefund(viaLicense));
        // A license request: no purchase (JsonUtility leaves an empty object), the license names the product.
        var license = new ImportRequest
        {
            request_id = "l", target = "unity", kind = "license", license_id = "lic-1",
            purchase = new PurchaseItem(), licenses = new[] { new LicenseEntry { license_id = "lic-1", name = "Licensed Tool" } },
        };
        Require(!ImportRequestLogic.HasPurchase(license));
        Require(!ImportRequestLogic.WarnsRefund(license));
        Equal("Licensed Tool", ImportRequestLogic.ProductName(license));

        Equal(null, ImportRequestLogic.FromChoice((int)ImportChoice.Import));
        Equal(ImportReport.Declined, ImportRequestLogic.FromChoice((int)ImportChoice.Cancel).Report);
        Equal(ImportReport.Release, ImportRequestLogic.FromChoice((int)ImportChoice.NotThisProject).Report);
        Equal("release", ImportRequestLogic.ActionOf(ImportReport.Release));
        Equal("", ImportRequestLogic.ActionOf(ImportReport.None));
    }

    private static void Resume()
    {
        const long t = 2000000;
        Func<string, string> installed = name => name == "com.seller.tool" ? "1.2.0" : "";
        var claim = new ImportClaim { request_id = "r", claim_id = "c", claimed_at = t - 60 };

        claim.phase = ImportRequestLogic.PhaseReport; claim.action = "failed"; claim.code = "download_failed";
        var o = ImportRequestLogic.Resume(claim, t, installed);
        Equal(ImportReport.Failed, o.Report); Equal("download_failed", o.Code);
        claim.action = "done";
        Equal(ImportReport.Done, ImportRequestLogic.Resume(claim, t, installed).Report);
        claim.action = "bogus";
        Equal(null, ImportRequestLogic.Resume(claim, t, installed));

        claim.phase = ImportRequestLogic.PhaseClaimed;
        Equal(ImportReport.Release, ImportRequestLogic.Resume(claim, t, installed).Report);

        claim.phase = ImportRequestLogic.PhaseRunning; claim.package_name = "com.seller.tool"; claim.package_version = "1.2.0";
        Equal(ImportReport.Done, ImportRequestLogic.Resume(claim, t, installed).Report);
        claim.package_version = "1.3.0";
        o = ImportRequestLogic.Resume(claim, t, installed);
        Equal(ImportReport.Failed, o.Report); Equal("interrupted", o.Code);
        claim.package_name = ""; claim.package_version = "";
        Equal("interrupted", ImportRequestLogic.Resume(claim, t, installed).Code);

        claim.claimed_at = t - ImportRequestLogic.MaxRecordAgeSeconds - 1;
        Equal(null, ImportRequestLogic.Resume(claim, t, installed));
        Equal(null, ImportRequestLogic.Resume(new ImportClaim { request_id = "r", phase = ImportRequestLogic.PhaseClaimed }, t, installed));
        Equal(null, ImportRequestLogic.Resume(null, t, installed));
    }

    private static void Waiting()
    {
        const long t = 3000000;
        var results = new Dictionary<string, string> { { "a", "imported" }, { "b", "pending" }, { "c", "cancelled" }, { "d", "failed" }, { "e", "missing" } };
        Func<string, string> result = p => results.ContainsKey(p) ? results[p] : "";
        Func<string, string> installed = name => name == "com.seller.tool" ? "2.0.0" : "";
        var claim = new ImportClaim { request_id = "r", claim_id = "c", phase = ImportRequestLogic.PhaseWaiting, claimed_at = t - 10, wait_since = t - 10 };

        claim.wait_imports = new[] { "a" };
        Equal(ImportReport.Done, ImportRequestLogic.Settle(claim, t, result, installed).Report);
        claim.wait_imports = new[] { "a", "b" };
        Equal(ImportReport.Waiting, ImportRequestLogic.Settle(claim, t, result, installed).Report);
        claim.wait_imports = new[] { "a", "c" };
        Equal("cancelled", ImportRequestLogic.Settle(claim, t, result, installed).Code);
        claim.wait_imports = new[] { "d" };
        Equal("import_failed", ImportRequestLogic.Settle(claim, t, result, installed).Code);
        claim.wait_imports = new[] { "e" };
        Equal("import_failed", ImportRequestLogic.Settle(claim, t, result, installed).Code);
        claim.wait_imports = new[] { "unknown" };
        Equal("interrupted", ImportRequestLogic.Settle(claim, t, result, installed).Code);
        claim.wait_imports = new[] { "b" };
        claim.wait_since = t - ImportRequestLogic.MaxWaitSeconds - 1;
        Equal("interrupted", ImportRequestLogic.Settle(claim, t, result, installed).Code);
        // Resume after a reload goes through the same rules.
        claim.wait_since = t - 10;
        Equal(ImportReport.Waiting, ImportRequestLogic.Resume(claim, t, installed, result).Report);

        var handoff = new ImportClaim { request_id = "r", claim_id = "c", phase = ImportRequestLogic.PhaseWaiting, claimed_at = t, wait_since = t, wait_package = true, package_name = "com.seller.tool", package_version = "2.0.0" };
        Equal(ImportReport.Done, ImportRequestLogic.Settle(handoff, t, result, installed).Report);
        // The installer's own state decides: in place but still checking waits, stopped fails, done needs the version too.
        Equal(ImportReport.Waiting, ImportRequestLogic.Settle(handoff, t, result, installed, p => "pending").Report);
        Equal("install_failed", ImportRequestLogic.Settle(handoff, t, result, installed, p => "stopped").Code);
        Equal(ImportReport.Done, ImportRequestLogic.Settle(handoff, t, result, installed, p => "done").Report);
        handoff.package_version = "2.1.0";
        Equal("install_failed", ImportRequestLogic.Settle(handoff, t, result, installed, p => "done").Code);
        Equal(ImportReport.Waiting, ImportRequestLogic.Settle(handoff, t, result, installed).Report);
        handoff.wait_since = t - ImportRequestLogic.MaxWaitSeconds - 1;
        Equal("interrupted", ImportRequestLogic.Settle(handoff, t, result, installed).Code);
        Equal(ImportReport.Waiting, ImportOutcome.WaitForImports(new List<string> { "x" }).Report);

        // A reload ended the install path after it queued imports (the queue told the record first): the imports decide.
        var running = new ImportClaim { request_id = "r", claim_id = "c", phase = ImportRequestLogic.PhaseRunning, claimed_at = t - 10, wait_imports = new[] { "a" } };
        Equal(ImportReport.Done, ImportRequestLogic.Resume(running, t, installed, result).Report);
        running.wait_imports = new[] { "b" };
        Equal(ImportReport.Waiting, ImportRequestLogic.Resume(running, t, installed, result).Report);
        running.wait_imports = new string[0];
        Equal("interrupted", ImportRequestLogic.Resume(running, t, installed, result).Code);
    }

    private static void Errors()
    {
        foreach (var status in new long[] { 0, 429, 500, 503 }) Equal(ImportErrorHandling.RetryLater, ImportRequestLogic.OnError(status));
        foreach (var status in new long[] { 400, 401, 404, 409, 410 }) Equal(ImportErrorHandling.Drop, ImportRequestLogic.OnError(status));
        Require(ImportRequestLogic.IsValidCode("device_limit"));
        Require(!ImportRequestLogic.IsValidCode("Device-Limit"));
        Require(!ImportRequestLogic.IsValidCode(new string('a', 41)));
        Equal("install_failed", ImportOutcome.Failed("bad code!").Code);
        Equal("cancelled", ImportOutcome.Failed("cancelled").Code);
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
