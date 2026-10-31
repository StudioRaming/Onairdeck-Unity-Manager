using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// The Manager's own error report (U2): which exception is the Manager's bug and opens the card by itself, which is a state the
// window already explains, and the card it makes (redacted, deduplicated per step).
internal static class ManagerErrorChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("manager error attribution: the first non-engine frame decides (Console and .NET formats, async state machines); a product frame first, the license check or engine-only stacks are not the Manager's", Attribution);
        check("manager error attribution: a real exception thrown in the Manager namespace (also through await and a task) is the Manager's, one thrown elsewhere is not", RealStacks);
        check("manager error kinds: cancel, timeout, expired sign-in, server/network (plain Exception, ApiException) are not bugs; NullReference, IO, install refusals and a task's one inner bug are", Kinds);
        check("manager error card: Atelier Manager with manager.exception, step and frames in the text, redacted before it is shown, same step with other digits and paths deduplicates, another step does not", Card);
    }

    private static void Attribution()
    {
        // Console format (Unity's stackTrace argument): "Type:Method (args) (at path:line)".
        Require(ManagerErrorLogic.IsManagerStack(
            "UnityEngine.Debug:LogException (System.Exception)\n" +
            "OnAirDeck.UnityManager.ManagerWindow:DrawBody (UnityEngine.Rect) (at Packages/com.studioraming.manager/Editor/UI/ManagerWindow.cs:220)\n" +
            "OnAirDeck.UnityManager.ManagerWindow:OnGUI () (at Packages/com.studioraming.manager/Editor/UI/ManagerWindow.cs:205)\n" +
            "UnityEditor.HostView:OldOnGUI ()"));
        // .NET format with an async state machine and the runtime frames before it.
        Require(ManagerErrorLogic.IsManagerStack(
            "  at System.Collections.Generic.Dictionary`2[TKey,TValue].get_Item (TKey key) [0x0001e] in <hash>:0\n" +
            "  at OnAirDeck.UnityManager.ManagerWindow+<LoadEntries>d__41.MoveNext () [0x000b2] in C:\\p\\ManagerWindow.Session.cs:150\n" +
            "--- End of stack trace from previous location where exception was thrown ---\n" +
            "  at System.Runtime.CompilerServices.AsyncMethodBuilderCore+<>c.<ThrowAsync>b__6_0 (System.Object state) [0x00000] in <hash>:0"));
        // A product's code first: the product's error, even when the Manager called it.
        Require(!ManagerErrorLogic.IsManagerStack(
            "StudioRaming.MaskMaker.MaskWindow:OnGUI () (at Assets/StudioRaming/MaskMaker/MaskWindow.cs:10)\n" +
            "OnAirDeck.UnityManager.PackageImportQueue:Next () (at Packages/com.studioraming.manager/Editor/Download/PackageImportQueue.cs:80)"));
        // The runtime license check is the products' code; engine-only and empty stacks belong to nobody.
        Require(!ManagerErrorLogic.IsManagerStack("OnAirDeck.License.OnAirDeckLicense:Check (string) (at Assets/x.cs:1)"));
        Require(!ManagerErrorLogic.IsManagerStack("UnityEditor.EditorApplication:Internal_CallUpdateFunctions ()"));
        Require(!ManagerErrorLogic.IsManagerStack(""));
        Require(!ManagerErrorLogic.IsManagerStack(null));
        // A namespace that only starts the same is not the Manager's.
        Require(!ManagerErrorLogic.IsManagerStack("OnAirDeck.UnityManagerExtras.Tool:Run () (at Assets/t.cs:1)"));
    }

    private static void RealStacks()
    {
        Require(ManagerErrorLogic.ThrownByManager(Catch(ManagerProbe.Throw)));
        Require(!ManagerErrorLogic.ThrownByManager(Catch(OtherProbe.Throw)));
        // Through await: the frames are the state machine's.
        var faulted = ManagerProbe.ThrowLaterAsync();
        try { faulted.Wait(); } catch (AggregateException) { }
        Require(faulted.Exception != null && ManagerErrorLogic.ThrownByManager(faulted.Exception));
        // A wrapper thrown elsewhere around the Manager's exception still names the Manager.
        Exception wrapped = null;
        try { try { ManagerProbe.Throw(); } catch (Exception inner) { throw new InvalidOperationException("outer", inner); } }
        catch (Exception e) { wrapped = e; }
        Require(ManagerErrorLogic.ThrownByManager(wrapped));
        Require(!ManagerErrorLogic.ThrownByManager(null));
    }

    private static void Kinds()
    {
        Require(!ManagerErrorLogic.IsUnexpected(null));
        Require(!ManagerErrorLogic.IsUnexpected(new Exception("Sign-in failed: Cannot resolve destination host")));
        Require(!ManagerErrorLogic.IsUnexpected(new ApiException("Loading failed (500).", 500, "internal", "{}")));
        Require(!ManagerErrorLogic.IsUnexpected(new DeviceReleasedException("released", "{}", null)));
        Require(!ManagerErrorLogic.IsUnexpected(new SessionExpiredException("expired")));
        Require(!ManagerErrorLogic.IsUnexpected(new OperationCanceledException()));
        Require(!ManagerErrorLogic.IsUnexpected(new TaskCanceledException()));
        Require(!ManagerErrorLogic.IsUnexpected(new TimeoutException()));
        Require(!ManagerErrorLogic.IsUnexpected(new AggregateException(new Exception("offline"))));
        Require(ManagerErrorLogic.IsUnexpected(new NullReferenceException()));
        Require(ManagerErrorLogic.IsUnexpected(new IOException("locked")));
        Require(ManagerErrorLogic.IsUnexpected(new FormatException()));
        Require(ManagerErrorLogic.IsUnexpected(new PackageInstallException("The package hash does not match.")));
        Require(ManagerErrorLogic.IsUnexpected(new AggregateException(new NullReferenceException())));
        // Two failures of a task are reported as the aggregate (a bug: not a plain Exception).
        Require(ManagerErrorLogic.IsUnexpected(new AggregateException(new Exception("a"), new Exception("b"))));
    }

    private static void Card()
    {
        var ctx = new RedactContext(@"C:\proj\My Game", "zmnbn", "DESKTOP-K1");
        var e = Catch(ManagerProbe.Throw);
        var text = ManagerErrorLogic.Describe("Loading the product list", e);
        Require(text.StartsWith("Loading the product list: NullReferenceException: probe at C:\\Users\\zmnbn\\cache.json", StringComparison.Ordinal));
        Require(text.Contains("OnAirDeck.UnityManager.ManagerProbe"));
        Require(text.Split('\n').Length <= ManagerErrorLogic.MaxStackLines + 1);

        var report = ManagerErrorLogic.NewReport("0.1.3", "Loading the product list: probe", text);
        Require(ManagerErrorLogic.IsManagerReport(report));
        Equal("Atelier Manager", report.Product);
        Equal(ManagerErrorLogic.ExceptionCode, report.ErrorCode);
        Equal(1, report.Count);
        var shot = report.TakeSnapshot(20000, 4000, ctx);
        Require(shot.Excerpt.Contains("<path>/cache.json"));
        Require(!shot.Excerpt.Contains("zmnbn"));
        Require(!shot.Summary.Contains("zmnbn"));
        Require(ManagerErrorLogic.NewReport("0.1.3", "x", " ") == null);
        Require(!ManagerErrorLogic.IsManagerReport(new InstallErrorReport { Product = "MaskMaker", ProductId = "lic-1" }));

        // The same failure with other digits and paths has one signature; another step has its own.
        var again = ManagerErrorLogic.NewReport("0.1.3", "r", "Loading the product list: NullReferenceException: probe at D:\\other\\cache2.json\n  at OnAirDeck.UnityManager.ManagerProbe.Throw () [0x00011] in <x>:0");
        var first = ManagerErrorLogic.NewReport("0.1.3", "r", "Loading the product list: NullReferenceException: probe at C:\\Users\\zmnbn\\cache.json\n  at OnAirDeck.UnityManager.ManagerProbe.Throw () [0x00001] in <y>:0");
        Equal(first.Items[0].Signature, again.Items[0].Signature);
        var other = ManagerErrorLogic.NewReport("0.1.3", "r", "Sign-in: NullReferenceException: probe at C:\\Users\\zmnbn\\cache.json\n  at OnAirDeck.UnityManager.ManagerProbe.Throw () [0x00001] in <y>:0");
        Require(first.Items[0].Signature != other.Items[0].Signature);
        // Every Manager error of one version joins one card.
        Require(first.SameProduct(other) && first.Merge(other) && first.Count == 2);

        Require(ManagerErrorLogic.DescribeLog("NullReferenceException: x", "A.B:C ()").StartsWith("Exception: NullReferenceException: x\nA.B:C ()", StringComparison.Ordinal));
    }

    private static Exception Catch(Action action)
    {
        try { action(); }
        catch (Exception e) { return e; }
        throw new Exception("Nothing was thrown");
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}

namespace OnAirDeck.UnityManager
{
    internal static class ManagerProbe
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Throw() { throw new NullReferenceException(@"probe at C:\Users\zmnbn\cache.json"); }

        public static async Task ThrowLaterAsync()
        {
            await Task.Yield();
            Throw();
        }
    }
}

internal static class OtherProbe
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Throw() { throw new NullReferenceException("elsewhere"); }
}
