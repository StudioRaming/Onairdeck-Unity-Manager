using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;

// The perpetual branch of the license check. The real verifier and Check() are compiled in; the raw device id comes from a
// fake provider, the way the Editor-only adapter would give it. The server's own output is pinned through the shared vectors.
internal static class OfflineChecks
{
    private const string Product = "00000000-0000-4000-8000-00000000c002";
    private const string FakeRaw = "verification-fake-device-0001";

    private static string _project;
    private static RSA _key;
    private static string _kid;
    private static string _vectorsJson;

    public static void Run(Action<string, Action> check, string project)
    {
        _project = project;
        _key = RSA.Create(2048);
        var modulus = _key.ExportParameters(false).Modulus;
        _kid = OfflineKey.KidOf(modulus);
        OfflineKeys.TestKeys.Add(EntryFor(modulus));
        _vectorsJson = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "offline-license.vectors.json"));

        check("offline: the server's vector file verifies, kid and device hash match", Vectors);
        check("offline: valid with no session marker, no clock and a far-future iat", ValidWithoutSession);
        check("offline: another PC's file reports OfflineDeviceMismatch", WrongDevice);
        check("offline: a kid outside the table reports OfflineKeyUnknown; the online key is not in the table", UnknownKey);
        check("offline: signature by another key under a table kid, an edited payload, a damaged table entry", BadSignatures);
        check("offline: exp present, wrong client, version, mode, scheme, product are refused", BadFields);
        check("offline: a null, empty, n/a or throwing provider fails closed and is not cached", NoDeviceId);
        check("offline: online Valid wins; offline serves a signed-in PC with no online file; its state is shown when both fail", OnlineAndOffline);
        check("offline: no session and no file still reports NotSignedIn", NoFileNoSession);
        check("offline: a product id cannot leave the offline folder", NoTraversal);
        check("offline: online and perpetual strings never pass the other verifier", ModesDoNotCross);
        check("offline: what the tool gates read by name still exists, with the new statuses", GateContract);
        check("offline: production key table is well formed, and refuses a release when empty", ProductionTable);
    }

    private static OfflineKey EntryFor(byte[] modulus)
    {
        byte[] h;
        using (var sha = SHA256.Create()) h = sha.ComputeHash(modulus);
        return new OfflineKey(OfflineKey.KidOf(modulus), modulus, OfflineKey.Word(h, 0), OfflineKey.Word(h, 1), OfflineKey.Word(h, 2), OfflineKey.Word(h, 3));
    }

    private static string Field(string name)
    {
        var marker = "\"" + name + "\": \"";
        var at = _vectorsJson.IndexOf(marker, StringComparison.Ordinal);
        Require(at >= 0);
        at += marker.Length;
        return _vectorsJson.Substring(at, _vectorsJson.IndexOf('"', at) - at);
    }

    private static void Vectors()
    {
        var modulus = LicenseVerifier.FromBase64Url(Field("test_modulus_b64url"));
        Equal(Field("kid"), OfflineKey.KidOf(modulus));
        Equal(Field("hash"), OfflineDeviceHash.Compute(FakeRaw));
        OfflineKeys.TestKeys.Add(EntryFor(modulus));
        try
        {
            var r = OfflineLicenseVerifier.Verify(Field("license"), Field("license_id"), OfflineDeviceHash.Compute(FakeRaw));
            Equal(LicenseStatus.Valid, r.Status);
            Equal(Field("license_id"), r.ProductId);
            Equal(LicenseStatus.OfflineDeviceMismatch, OfflineLicenseVerifier.Verify(Field("license"), Field("license_id"), OfflineDeviceHash.Compute("other")).Status);
        }
        finally { OfflineKeys.TestKeys.RemoveAll(k => k.Kid == Field("kid")); }
    }

    private static string Payload(string overrides = null, string[] without = null, string product = Product, string raw = FakeRaw, string kid = null)
    {
        var parts = new[]
        {
            "\"v\":2", "\"iss\":\"onairdeck\"", "\"client\":\"unity-manager\"", "\"mode\":\"perpetual-device\"",
            "\"kid\":\"" + (kid ?? _kid) + "\"", "\"license_id\":\"" + product + "\"", "\"product_id\":\"" + product + "\"",
            "\"product_name\":\"Test\"", "\"kind\":\"license\"", "\"dev\":\"" + OfflineDeviceHash.Compute(raw) + "\"",
            "\"dev_scheme\":\"unity-hash\"", "\"reason\":\"closure\"", "\"slot\":\"s\"", "\"iat\":1793836800", "\"nonce\":\"n\""
        }.ToList();
        if (without != null) parts.RemoveAll(p => without.Any(w => p.StartsWith("\"" + w + "\"", StringComparison.Ordinal)));
        if (overrides != null) parts.Insert(0, overrides);
        return "{" + string.Join(",", parts) + "}";
    }

    private static string Signed(string payloadJson, RSA key = null)
    {
        var bytes = Encoding.UTF8.GetBytes(payloadJson);
        return Base64Url(bytes) + "." + Base64Url((key ?? _key).SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static LicenseStatus Status(string license) { return OfflineLicenseVerifier.Verify(license, Product, OfflineDeviceHash.Compute(FakeRaw)).Status; }

    private static string Folder(string name)
    {
        var root = Path.Combine(_project, "offline-" + name);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WithSetup(string name, Action<string> body)
    {
        var saved = new object[] { OnAirDeckLicense.ProjectRoot, OnAirDeckLicense.SessionMarkerPath, OnAirDeckLicense.OfflineFolderPath, OnAirDeckLicense.RawDeviceIdProvider };
        var root = Folder(name);
        try
        {
            OnAirDeckLicense.ProjectRoot = Path.Combine(root, "project");
            OnAirDeckLicense.SessionMarkerPath = Path.Combine(root, "no-session.json");
            OnAirDeckLicense.OfflineFolderPath = Path.Combine(root, "offline");
            OnAirDeckLicense.RawDeviceIdProvider = () => FakeRaw;
            Directory.CreateDirectory(OnAirDeckLicense.OfflineFolderPath);
            OnAirDeckLicense.ClearCache();
            body(root);
        }
        finally
        {
            OnAirDeckLicense.ProjectRoot = (string)saved[0];
            OnAirDeckLicense.SessionMarkerPath = (string)saved[1];
            OnAirDeckLicense.OfflineFolderPath = (string)saved[2];
            OnAirDeckLicense.RawDeviceIdProvider = (Func<string>)saved[3];
            OnAirDeckLicense.ClearCache();
        }
    }

    private static void WriteOffline(string license)
    {
        File.WriteAllText(Path.Combine(OnAirDeckLicense.OfflineFolderPath, Product + ".license"), license);
    }

    private static void ValidWithoutSession()
    {
        WithSetup("valid", _ =>
        {
            WriteOffline(Signed(Payload()));
            var r = OnAirDeckLicense.Check(Product);
            Equal(LicenseStatus.Valid, r.Status);
            Require(r.LicensePath.EndsWith(Product + ".license", StringComparison.Ordinal));
            Equal(0L, r.ExpiresAtUnix);
            // A clock far before iat changes nothing: this mode reads no time.
            var clock = OnAirDeckLicense.Clock;
            try
            {
                OnAirDeckLicense.Clock = () => 1000;
                OnAirDeckLicense.ClearCache();
                Equal(LicenseStatus.Valid, OnAirDeckLicense.Check(Product).Status);
            }
            finally { OnAirDeckLicense.Clock = clock; }
        });
    }

    private static void WrongDevice()
    {
        WithSetup("device", _ =>
        {
            WriteOffline(Signed(Payload(raw: "another-pc")));
            Equal(LicenseStatus.OfflineDeviceMismatch, OnAirDeckLicense.Check(Product).Status);
        });
    }

    private static void UnknownKey()
    {
        using (var other = RSA.Create(2048))
        {
            var kid = OfflineKey.KidOf(other.ExportParameters(false).Modulus);
            Equal(LicenseStatus.OfflineKeyUnknown, Status(Signed(Payload(kid: kid), other)));
        }
        var online = LicenseVerifier.PublicKey.Modulus;
        Require(OfflineKeys.Find(OfflineKey.KidOf(online)) == null);
        Require(OfflineKeys.Production.All(k => !k.Modulus.SequenceEqual(online)));
    }

    private static void BadSignatures()
    {
        using (var other = RSA.Create(2048)) Equal(LicenseStatus.InvalidSignature, Status(Signed(Payload(), other)));

        var good = Signed(Payload());
        var edited = Base64Url(Encoding.UTF8.GetBytes(Payload(product: "00000000-0000-4000-8000-00000000c003"))) + good.Substring(good.IndexOf('.'));
        Equal(LicenseStatus.InvalidSignature, Status(edited));

        var entry = OfflineKeys.Find(_kid);
        var bad = new OfflineKey("badbadbadbadbad1", entry.Modulus, entry.H0 ^ 1, entry.H1, entry.H2, entry.H3);
        OfflineKeys.TestKeys.Add(bad);
        try { Equal(LicenseStatus.InvalidSignature, Status(Signed(Payload(kid: bad.Kid)))); }
        finally { OfflineKeys.TestKeys.Remove(bad); }
    }

    private static void BadFields()
    {
        Equal(LicenseStatus.LicenseMalformed, Status(Signed(Payload("\"exp\":4000000000"))));
        Equal(LicenseStatus.WrongIssuer, Status(Signed(Payload("\"client\":\"warudo-plugin\"", new[] { "client" }))));
        Equal(LicenseStatus.WrongIssuer, Status(Signed(Payload("\"iss\":\"someone\"", new[] { "iss" }))));
        Equal(LicenseStatus.WrongIssuer, Status(Signed(Payload("\"v\":1", new[] { "v" }))));
        Equal(LicenseStatus.WrongIssuer, Status(Signed(Payload(null, new[] { "mode" }))));
        Equal(LicenseStatus.WrongIssuer, Status(Signed(Payload("\"dev_scheme\":\"device-key\"", new[] { "dev_scheme" }))));
        Equal(LicenseStatus.WrongProduct, Status(Signed(Payload(product: "00000000-0000-4000-8000-00000000c003"))));
        Equal(LicenseStatus.OfflineDeviceMismatch, Status(Signed(Payload(null, new[] { "dev" }))));
    }

    private static void NoDeviceId()
    {
        WithSetup("provider", _ =>
        {
            WriteOffline(Signed(Payload()));
            foreach (var provider in new Func<string>[] { null, () => "", () => "n/a", () => { throw new InvalidOperationException("not on main thread"); } })
            {
                OnAirDeckLicense.RawDeviceIdProvider = provider;
                OnAirDeckLicense.ClearCache();
                Equal(LicenseStatus.OfflineDeviceUnavailable, OnAirDeckLicense.Check(Product).Status);
            }
            // The adapter arrives a moment later: the next call must not see a kept refusal.
            OnAirDeckLicense.RawDeviceIdProvider = () => FakeRaw;
            Equal(LicenseStatus.Valid, OnAirDeckLicense.Check(Product).Status);
        });
    }

    private static string OnlineLicense(string user, long exp)
    {
        // Signed by a throwaway key: Verify with the embedded key reports InvalidSignature, which is a real online failure.
        return Signed("{\"v\":1,\"iss\":\"onairdeck\",\"client\":\"unity-manager\",\"user_id\":\"" + user + "\",\"product_id\":\"" + Product + "\",\"iat\":1,\"exp\":" + exp + "}");
    }

    private static void OnlineAndOffline()
    {
        WithSetup("both", root =>
        {
            File.WriteAllText(OnAirDeckLicense.SessionMarkerPath, "{\"user_id\":\"u1\"}");

            // Signed in, no online file: the offline file serves it.
            WriteOffline(Signed(Payload()));
            Equal(LicenseStatus.Valid, OnAirDeckLicense.Check(Product).Status);

            // Signed in, a bad online file and a bad offline file: the offline state is the reported one.
            var folder = Path.Combine(OnAirDeckLicense.ProjectRoot, OnAirDeckLicense.InstallRoot.Replace('/', Path.DirectorySeparatorChar), "p");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, OnAirDeckLicense.LicenseFileName), OnlineLicense("u1", 4000000000));
            WriteOffline(Signed(Payload(raw: "another-pc")));
            OnAirDeckLicense.ClearCache();
            Equal(LicenseStatus.OfflineDeviceMismatch, OnAirDeckLicense.Check(Product).Status);

            // No offline file: the online problem stays what it was.
            File.Delete(Path.Combine(OnAirDeckLicense.OfflineFolderPath, Product + ".license"));
            OnAirDeckLicense.ClearCache();
            Equal(LicenseStatus.InvalidSignature, OnAirDeckLicense.Check(Product).Status);
        });
        OnlineValidWins();
    }

    private static void OnlineValidWins()
    {
        // The genuine fixture is the only online file that passes the embedded key.
        const string product = "2d5f83b7-bacf-4104-b352-6c84180830c1";
        WithSetup("onlinewins", _ =>
        {
            var clock = OnAirDeckLicense.Clock;
            try
            {
                OnAirDeckLicense.Clock = () => LicenseChecks.RealIatForOffline + 10;
                File.WriteAllText(OnAirDeckLicense.SessionMarkerPath, "{\"user_id\":\"" + LicenseChecks.RealUserForOffline + "\"}");
                var folder = Path.Combine(OnAirDeckLicense.ProjectRoot, OnAirDeckLicense.InstallRoot.Replace('/', Path.DirectorySeparatorChar), "p");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, OnAirDeckLicense.LicenseFileName), LicenseChecks.RealLicenseForOffline);
                File.WriteAllText(Path.Combine(OnAirDeckLicense.OfflineFolderPath, product + ".license"), Signed(Payload(raw: "another-pc", product: product)));
                var r = OnAirDeckLicense.Check(product);
                Equal(LicenseStatus.Valid, r.Status);
                Equal(LicenseChecks.RealExpForOffline, r.ExpiresAtUnix);
            }
            finally { OnAirDeckLicense.Clock = clock; }
        });
    }

    private static void NoFileNoSession()
    {
        WithSetup("nofile", _ => Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(Product).Status));
    }

    private static void NoTraversal()
    {
        WithSetup("traversal", root =>
        {
            // A valid file for this id sits just outside the folder; the id must never reach it.
            const string id = "..\\outside";
            File.WriteAllText(Path.Combine(root, "outside.license"), Signed(Payload(product: id.Replace("\\", "\\\\"))));
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(id).Status);
            Require(!OnAirDeckLicense.IsSafeFileId("a/b"));
            Require(!OnAirDeckLicense.IsSafeFileId(".."));
            Require(OnAirDeckLicense.IsSafeFileId(Product));
        });
    }

    private static void ModesDoNotCross()
    {
        var perpetual = Signed(Payload());
        Require(!LicenseVerifier.Verify(perpetual, Product, "u1", 2000000000).IsValid);
        var key = _key.ExportParameters(false);
        Equal(LicenseStatus.LicenseMalformed, LicenseVerifier.Verify(perpetual, Product, "u1", 2000000000, key).Status);
        Equal(LicenseStatus.LicenseMalformed, Status(OnlineLicense("u1", 4000000000)));
    }

    private static void GateContract()
    {
        var type = typeof(LicenseCheckResult);
        foreach (var name in new[] { "IsValid", "Status", "Message" }) Require(type.GetProperty(name) != null);
        foreach (var name in new[] { "OfflineKeyUnknown", "OfflineDeviceMismatch", "OfflineDeviceUnavailable" })
        {
            var status = (LicenseStatus)Enum.Parse(typeof(LicenseStatus), name);
            Require((int)status > (int)LicenseStatus.Expired);
        }
        Equal(0, (int)LicenseStatus.Valid);
        Equal(9, (int)LicenseStatus.Expired);
    }

    private static void ProductionTable()
    {
        foreach (var key in OfflineKeys.Production)
        {
            Equal(OfflineKey.KidOf(key.Modulus), key.Kid);
            Require(key.Intact());
            Equal(256, key.Modulus.Length);
        }
        // Closed members hold files signed with the first production key for good: removing it would strand them.
        Require(OfflineKeys.Production.Any(k => k.Kid == "61ee82969f30f1aa"));
        // Set by a release run: a build with no production key could never accept a closed member's file.
        if (Environment.GetEnvironmentVariable("ATELIER_RELEASE_CHECK") == "1") Require(OfflineKeys.Production.Length > 0);
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
