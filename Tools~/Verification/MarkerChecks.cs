using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// Contract v8: DRM products shipped as .unitypackage. Real sources: ProductMarkers, LicenseFilePlan, LicenseFile,
// AtelierManagerApi (+ LicenseRequestFlow). The Unity half (window, sign-in, the header on the wire) is compile-checked only.
internal static class MarkerChecks
{
    private static string _root;

    public static void Run(Action<string, Action> check, string project)
    {
        _root = Path.Combine(project, "v8-project");
        check("v8: markers found by license_id; wrong id, missing id, malformed JSON, hidden and ~ folders ignored", MarkerScan);
        check("v8: a marker alone makes a license installed; no marker, no package, no file: nothing is issued", PlanWithMarker);
        check("v8: an existing license file keeps renewing without a package, by the id inside it", RenewalWithoutPackage);
        check("v8: marker without a license file is a first issue (background)", FirstIssue);
        check("v8: AtelierManagerApi.RequestLicense resolves by reflection, never throws, issues with the reactivate flag", ApiBridge);
    }

    private static void Put(string relative, string text)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static string B64(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static LicenseEntry Entry(string id, string name, params string[] packages)
    {
        var versions = new List<LicenseVersionEntry>();
        foreach (var package in packages) versions.Add(new LicenseVersionEntry { package_name = package, package_version = "1.0.0" });
        return new LicenseEntry { license_id = id, name = name, versions = versions.ToArray() };
    }

    private static void MarkerScan()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        Put("Assets/StudioRaming/MaskMaker/atelier.product.json", "{\"license_id\":\"L-1\",\"name\":\"Mask Maker\"}");
        Put("Assets/Deep/er/than/that/atelier.product.json", "﻿{ \"name\": \"With BOM\", \"license_id\": \"L-2\" }");
        Put("Packages/com.vendor.tool/atelier.product.json", "{\"license_id\":\"P-1\"}");
        Put("Assets/NoId/atelier.product.json", "{\"name\":\"No id\"}");
        Put("Assets/EmptyId/atelier.product.json", "{\"license_id\":\"  \",\"name\":\"x\"}");
        Put("Assets/Broken/atelier.product.json", "{\"license_id\":");
        Put("Assets/NotJson/atelier.product.json", "license_id L-9");
        Put("Assets/.hidden/atelier.product.json", "{\"license_id\":\"hid\"}");
        Put("Assets/Skipped~/atelier.product.json", "{\"license_id\":\"tilde\"}");
        Put("Library/PackageCache/atelier.product.json", "{\"license_id\":\"lib\"}");   // outside Assets and Packages
        var ids = ProductMarkers.Scan(_root);
        Equal(3, ids.Count);
        Require(ids.Contains("L-1") && ids.Contains("L-2") && ids.Contains("P-1"));
        Require(!ids.Contains("L-3") && !ids.Contains("hid") && !ids.Contains("tilde") && !ids.Contains("lib") && !ids.Contains("L-9"));
        Equal(0, ProductMarkers.Scan(Path.Combine(_root, "does-not-exist")).Count);   // missing project: empty, no throw
        Equal(0, ProductMarkers.Scan(null).Count);
        string id;
        Require(!ProductMarkers.TryReadLicenseId("", out id) && !ProductMarkers.TryReadLicenseId(null, out id));
        Require(ProductMarkers.TryReadLicenseId("{\"license_id\":\"abc\"}", out id) && id == "abc");
    }

    private static void PlanWithMarker()
    {
        var withMarker = Entry("L-1", "Mask Maker");                  // no VPM versions at all
        var other = Entry("L-3", "Other", "com.missing");
        var licenses = new[] { withMarker, other };
        Func<string, bool> never = name => false;
        var markers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "L-1" };
        var jobs = LicenseFilePlan.Select(licenses, never, folder => true, markers.Contains, null);
        Equal(1, jobs.Count);
        Equal("L-1", jobs[0].License.license_id);
        Equal("Assets/StudioRaming_Onairdeck/Mask Maker", jobs[0].Folder);
        // A marker with another id does not install this license.
        Equal(0, LicenseFilePlan.Select(new[] { other }, never, folder => true, markers.Contains, null).Count);
        // Neither marker, nor package, nor file: nothing is issued (also with the older three-argument call).
        Equal(0, LicenseFilePlan.Select(licenses, never, folder => true).Count);
        Equal(0, LicenseFilePlan.Select(licenses, never, folder => true, id => false, new List<ExistingLicenseFile>()).Count);
        // The marker route and the VPM route together give one job, not two.
        var both = LicenseFilePlan.Select(new[] { Entry("L-1", "Mask Maker", "com.installed") }, name => name == "com.installed", folder => true, markers.Contains, null);
        Equal(1, both.Count);
    }

    private static void RenewalWithoutPackage()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
        var payload = "{\"license_id\":\"L-9\",\"user_id\":\"u\",\"exp\":1}";
        Put("Assets/StudioRaming_Onairdeck/Renamed By User/onairdeck.license", B64(payload) + ".sig");
        Put("Assets/StudioRaming_Onairdeck/Other Account/onairdeck.license", B64("{\"license_id\":\"L-X\",\"exp\":1}") + ".sig");
        Put("Assets/StudioRaming_Onairdeck/Garbage/onairdeck.license", "not a license");
        Put("Assets/StudioRaming_Onairdeck/Empty/readme.txt", "no license here");
        var existing = LicenseFilePlan.ExistingFiles(_root);
        Equal(2, existing.Count);                                       // Garbage and Empty are ignored
        var licenses = new[] { Entry("L-9", "Original Name", "com.missing") };
        Func<string, bool> never = name => false;
        var jobs = LicenseFilePlan.Select(licenses, never, folder => true, null, existing);
        Equal(1, jobs.Count);                                           // L-X is not in the account: no job
        Equal("L-9", jobs[0].License.license_id);
        Equal("Assets/StudioRaming_Onairdeck/Renamed By User", jobs[0].Folder);   // renews where the file is, no second folder
        // A file that does not need renewal is left alone.
        Equal(0, LicenseFilePlan.Select(licenses, never, folder => false, null, existing).Count);
        Equal("Assets/StudioRaming_Onairdeck/Renamed By User", LicenseFilePlan.FolderOf(licenses, licenses[0], existing));
        Equal("Assets/StudioRaming_Onairdeck/Original Name", LicenseFilePlan.FolderOf(licenses, licenses[0], null));
        Equal(0, LicenseFilePlan.ExistingFiles(Path.Combine(_root, "missing")).Count);
    }

    private static void FirstIssue()
    {
        // No license file in the folder: NeedsRefresh is true, so the background ensure issues one (v7 lets a free seat in).
        var license = Entry("L-5", "Fresh Product " + Guid.NewGuid().ToString("N"));
        var markers = new HashSet<string> { "L-5" };
        var jobs = LicenseFilePlan.Select(new[] { license }, name => false, folder => LicenseFile.NeedsRefresh(folder, 1792000000), markers.Contains, null);
        Equal(1, jobs.Count);
    }

    // ---------- the lock-screen entry ----------

    private sealed class FakeHost : ILicenseRequestHost
    {
        public string Token;
        public string SignedInToken = "fresh";
        public bool Opened;
        public int SignIns;
        public string IssuedId;
        public string IssuedToken;
        public bool? Reactivate;
        public bool Throw;
        public readonly List<string> Logs = new List<string>();

        public void OpenWindow() { Opened = true; }
        public string SessionToken() { return Token; }
        public Task<string> SignInAsync() { SignIns++; return Task.FromResult(SignedInToken); }
        public Task IssueAsync(string token, string licenseId, bool reactivate)
        {
            IssuedToken = token; IssuedId = licenseId; Reactivate = reactivate;
            if (Throw) throw new InvalidOperationException("boom");
            return Task.FromResult(0);
        }
        public void Log(string message) { Logs.Add(message); }
    }

    private static void ApiBridge()
    {
        // The lock template calls it by reflection: the exact names are the contract.
        var type = Type.GetType("OnAirDeck.UnityManager.AtelierManagerApi");
        Require(type != null && type.IsPublic && type.IsAbstract && type.IsSealed);   // public static class
        var method = type.GetMethod("RequestLicense", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
        Require(method != null && method.ReturnType == typeof(void));

        // Not registered yet: a call must not throw.
        AtelierManagerApi.Runner = null;
        method.Invoke(null, new object[] { "L-1" });
        method.Invoke(null, new object[] { null });

        // Signed in: opens the window and issues with the reactivate flag, without a second sign-in.
        var host = new FakeHost { Token = "tok" };
        AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, host);
        method.Invoke(null, new object[] { "L-1" });
        Equal(true, host.Opened);
        Equal(0, host.SignIns);
        Equal("L-1", host.IssuedId);
        Equal("tok", host.IssuedToken);
        Equal(true, host.Reactivate);

        // Signed out: sign-in first, then the same issue as a user click.
        var signedOut = new FakeHost { Token = null };
        AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, signedOut);
        method.Invoke(null, new object[] { "L-2" });
        Equal(1, signedOut.SignIns);
        Equal("fresh", signedOut.IssuedToken);
        Equal(true, signedOut.Reactivate);

        // A sign-in that did not finish issues nothing; an exception in the host is logged, never thrown.
        var cancelled = new FakeHost { Token = null, SignedInToken = null };
        AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, cancelled);
        method.Invoke(null, new object[] { "L-3" });
        Equal(null, cancelled.IssuedId);
        var failing = new FakeHost { Token = "tok", Throw = true };
        AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, failing);
        method.Invoke(null, new object[] { "L-4" });
        Equal(1, failing.Logs.Count);
        // A runner that itself throws synchronously still does not reach the caller.
        AtelierManagerApi.Runner = id => { throw new InvalidOperationException("sync"); };
        method.Invoke(null, new object[] { "L-5" });
        // An empty id is ignored.
        var none = new FakeHost { Token = "tok" };
        AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, none);
        method.Invoke(null, new object[] { "" });
        Equal(false, none.Opened);
        AtelierManagerApi.Runner = null;
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
