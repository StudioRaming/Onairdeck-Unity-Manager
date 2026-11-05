using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// The import behind the Offline licences panel, with the real verifier and a throwaway key. The check code is pinned by the server's vectors.
internal static class OfflineImportChecks
{
    private const string A = "00000000-0000-4000-8000-00000000d001";
    private const string B = "00000000-0000-4000-8000-00000000d002";
    private const string RawHere = "this-pc";

    private static string _project;
    private static RSA _key;
    private static string _kid;
    private static string _here;

    public static void Run(Action<string, Action> check, string project)
    {
        _project = project;
        _key = RSA.Create(2048);
        var modulus = _key.ExportParameters(false).Modulus;
        _kid = OfflineKey.KidOf(modulus);
        byte[] h;
        using (var sha = SHA256.Create()) h = sha.ComputeHash(modulus);
        OfflineKeys.TestKeys.Add(new OfflineKey(_kid, modulus, OfflineKey.Word(h, 0), OfflineKey.Word(h, 1), OfflineKey.Word(h, 2), OfflineKey.Word(h, 3)));
        _here = OfflineDeviceHash.Compute(RawHere);

        check("offline import: the bundle's matching files are written and each other kind is counted", ImportsOnlyMatching);
        check("offline import: a single signed string works; text that is neither is refused and writes nothing", SingleAndGarbage);
        check("offline import: forged and damaged entries are not written, and the file then passes Check", ForgedRefused);
        check("offline import: only the newest file of a licence stays, and an older bundle never replaces it", NewestWins);
        check("offline import: a list shows each file's state; Remove takes only the named file, and a path cannot escape", ListAndRemove);
        check("offline import: device code groups, check code vectors, file form with an escaped label", DeviceCodeForms);
    }

    private static string Folder(string name)
    {
        var folder = Path.Combine(_project, "offline-import-" + name);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        return folder;
    }

    private static string Signed(string product, string raw = RawHere, string client = "unity-manager", long iat = 1793836800, RSA key = null, string scheme = "unity-hash")
    {
        var json = "{\"v\":2,\"iss\":\"onairdeck\",\"client\":\"" + client + "\",\"mode\":\"perpetual-device\",\"kid\":\"" + _kid + "\",\"license_id\":\"" + product
            + "\",\"product_id\":\"" + product + "\",\"product_name\":\"Name " + product.Substring(product.Length - 1) + "\",\"kind\":\"license\",\"dev\":\"" + OfflineDeviceHash.Compute(raw)
            + "\",\"dev_scheme\":\"" + scheme + "\",\"reason\":\"closure\",\"slot\":\"s\",\"iat\":" + iat + ",\"nonce\":\"n\"}";
        var bytes = Encoding.UTF8.GetBytes(json);
        return Url(bytes) + "." + Url((key ?? _key).SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static string Bundle(params string[] licenses)
    {
        return "{\"type\":\"atelier-offline-licences\",\"issued_at\":\"x\",\"entries\":[" + string.Join(",", licenses.Select(l => "{\"client\":\"c\",\"license\":\"" + l + "\"}")) + "]}";
    }

    private static string[] Files(string folder) { return Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(f => f).ToArray(); }

    private static void ImportsOnlyMatching()
    {
        var folder = Folder("kinds");
        var r = OfflineLicenseStore.Import(Bundle(Signed(A), Signed(B, "another-pc"), Signed(B, client: "warudo-plugin", scheme: "device-key")), _here, folder);
        Equal(1, r.Imported.Count);
        Equal("Name 1", r.Imported[0]);
        Equal(1, r.OtherPc);
        Equal(1, r.OtherClient);
        Equal(0, r.Invalid);
        Equal(A + ".license", Files(folder).Single());
    }

    private static void SingleAndGarbage()
    {
        var folder = Folder("single");
        Equal(1, OfflineLicenseStore.Import("\n" + Signed(A) + "\n", _here, folder).Imported.Count);
        foreach (var text in new[] { "", "not a licence", "{\"type\":\"other\",\"entries\":[]}", "{broken", "{\"type\":\"atelier-offline-licences\"}" })
        {
            var empty = Folder("garbage");
            var r = OfflineLicenseStore.Import(text, _here, empty);
            Require(r.NotRecognized);
            Equal(0, Files(empty).Length);
        }
    }

    private static void ForgedRefused()
    {
        var folder = Folder("forged");
        using (var other = RSA.Create(2048))
        {
            var r = OfflineLicenseStore.Import(Bundle(Signed(A, key: other)), _here, folder);
            Equal(1, r.Invalid);
            Equal(0, Files(folder).Length);
        }
        var saved = new object[] { OnAirDeckLicense.OfflineFolderPath, OnAirDeckLicense.RawDeviceIdProvider, OnAirDeckLicense.SessionMarkerPath };
        try
        {
            OnAirDeckLicense.OfflineFolderPath = folder;
            OnAirDeckLicense.RawDeviceIdProvider = () => RawHere;
            OnAirDeckLicense.SessionMarkerPath = Path.Combine(folder, "none.json");
            OnAirDeckLicense.ClearCache();
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(A).Status);
            OfflineLicenseStore.Import(Bundle(Signed(A)), _here, folder);
            Equal(LicenseStatus.Valid, OnAirDeckLicense.Check(A).Status);
        }
        finally
        {
            OnAirDeckLicense.OfflineFolderPath = (string)saved[0];
            OnAirDeckLicense.RawDeviceIdProvider = (Func<string>)saved[1];
            OnAirDeckLicense.SessionMarkerPath = (string)saved[2];
            OnAirDeckLicense.ClearCache();
        }
    }

    private static void NewestWins()
    {
        var folder = Folder("newest");
        var newer = Signed(A, iat: 2000);
        OfflineLicenseStore.Import(Bundle(Signed(A, iat: 1000), newer, Signed(A, iat: 1500)), _here, folder);
        Equal(newer, File.ReadAllText(Path.Combine(folder, A + ".license")));
        var r = OfflineLicenseStore.Import(Bundle(Signed(A, iat: 1000)), _here, folder);
        Equal(0, r.Imported.Count);
        Equal(newer, File.ReadAllText(Path.Combine(folder, A + ".license")));
    }

    private static void ListAndRemove()
    {
        var folder = Folder("list");
        OfflineLicenseStore.Import(Bundle(Signed(A)), _here, folder);
        File.WriteAllText(Path.Combine(folder, B + ".license"), Signed(B, "another-pc"));
        var entries = OfflineLicenseStore.List(folder, _here);
        Equal(2, entries.Count);
        Equal(LicenseStatus.Valid, entries.Single(e => e.ProductId == A).Status);
        Equal(LicenseStatus.OfflineDeviceMismatch, entries.Single(e => e.ProductId == B).Status);

        var outside = Path.Combine(Path.GetDirectoryName(folder), "outside.license");
        File.WriteAllText(outside, "x");
        OfflineLicenseStore.Remove(folder, "..\\outside");
        OfflineLicenseStore.Remove(folder, "../outside");
        Require(File.Exists(outside));
        OfflineLicenseStore.Remove(folder, A);
        Equal(B + ".license", Files(folder).Single());
        Equal(0, OfflineLicenseStore.List(Path.Combine(folder, "missing"), _here).Count);
    }

    private static void DeviceCodeForms()
    {
        var dev = OfflineDeviceHash.Compute("verification-fake-device-0001");
        Equal(dev.Length + 7, OfflineDeviceCode.Groups(dev).Length);
        Equal(8, OfflineDeviceCode.Groups(dev).Split(' ').Length);
        var vectors = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "offline-license.vectors.json"));
        Require(vectors.Contains(dev));
        Equal("c8b7", OfflineDeviceCode.Check(dev));
        Equal("5417", OfflineDeviceCode.Check(new string('a', 64)));
        var json = MiniJson.ParseObject(OfflineDeviceCode.FileJson(dev, "My \"PC\""));
        Equal("atelier-device", MiniJson.GetString(json, "type"));
        Equal("unity-hash", MiniJson.GetString(json, "scheme"));
        Equal("My \"PC\"", MiniJson.GetString(json, "label"));
        Require(!MiniJson.ParseObject(OfflineDeviceCode.FileJson(dev, "")).ContainsKey("label"));
        Require(ClosedAccount.IsClosedAnswer("{\"error\":\"account_closed\"}"));
        Require(!ClosedAccount.IsClosedAnswer("{\"error\":\"session revoked\"}"));
        Require(!ClosedAccount.IsClosedAnswer("not json"));
        Require(!ClosedAccount.IsClosedAnswer(null));
    }

    private static string Url(byte[] bytes) { return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
