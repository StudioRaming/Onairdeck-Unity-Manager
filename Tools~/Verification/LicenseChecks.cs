using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;

// Runtime license check (License/*.cs, the OnAirDeck.License assembly sellers call). The real
// verifier, JSON reader and file lookup are compiled into this program. One fixture is a genuine
// license issued by plugin-license for the test account's test product, so the embedded public key
// is exercised; everything else is signed on the fly with a throwaway key. Nothing is contacted.
internal static class LicenseChecks
{
    // Issued 2026-10-14 for the Phase A test fixture (test account, product "Unity Texture Color Tool 1.0").
    private const string RealLicense =
        "eyJ2IjoxLCJpc3MiOiJvbmFpcmRlY2siLCJjbGllbnQiOiJ1bml0eS1tYW5hZ2VyIiwidXNlcl9pZCI6IjJhYmVlODI1LTVmODYtNDY1My04YTk5LTRhYTA5OTMyMzFhZiIsIml0ZW1faWQiOiI4ZWE0M2UzOC0wZjU2LTRmYjItOWE3YS03MWM5YzJiOWJiZWEiLCJwcm9kdWN0X2lkIjoiMmQ1ZjgzYjctYmFjZi00MTA0LWIzNTItNmM4NDE4MDgzMGMxIiwicHJvZHVjdF9uYW1lIjoiVW5pdHkgVGV4dHVyZSBDb2xvciBUb29sIDEuMCIsImlhdCI6MTc5MTk2MTgwMCwiZXhwIjoxNzkyNTY2NjAwLCJub25jZSI6ImJjZGNhODk4NTY4MGI2NmZiMjlmY2EwYjhmMWQ3MzBhIn0"
        + ".sznJUKAV3ANy6ScaXqV27fBuUS2XMX4k8MurYee1M5mPrLvGdwn6Wjnlepcy5jQnBP3DvWsDcllLvv9SgoHaak5g5D__eZdyQqw4l_zhn664mFYQQyiZesl76ta9xdmXFdzSq-HQeAVhuGvVpo4Qib_4AYncZ0mm3bmA0Ze4KhxxVpvTKMpdMyizPmWD1lf7OIgLq1yKu-ahQGxTTxlv5VGd20g1WjFxvYczBNGI0gPbYK6ZPhN4Jfym856Z1qstX2HEub2272hqsCATAIXjx-KnA1OWTDDVChLXfZUgVcNPg3IaPaY63ZLyAH5mD4pGlkboPu1cM8hHPYNKrqfzNQ";
    private const string RealUser = "2abee825-5f86-4653-8a99-4aa0993231af";
    private const string RealProduct = "2d5f83b7-bacf-4104-b352-6c84180830c1";
    private const long RealIat = 1791961800;
    private const long RealExp = 1792566600;

    private static string _project;

    public static void Run(Action<string, Action> check, string project)
    {
        _project = project;
        check("real license verifies with the embedded OnAirDeck key", RealVerifies);
        check("real license: expired, future, other product, other account, not signed in", RealRejections);
        check("edited payload or signature is rejected", Tampering);
        check("malformed license strings", Malformed);
        check("self-signed licenses: JSON escapes, issuer, version, missing fields", SelfSigned);
        check("Check() finds the product license and reads the session marker", CheckFindsLicense);
        check("Check() failure reasons and result cache", CheckFailures);
    }

    private static void RealVerifies()
    {
        var r = LicenseVerifier.Verify(RealLicense, RealProduct, RealUser, RealIat + 10);
        Equal(LicenseStatus.Valid, r.Status);
        Require(r.IsValid);
        Equal(RealUser, r.UserId);
        Equal(RealProduct, r.ProductId);
        Equal("Unity Texture Color Tool 1.0", r.ProductName);
        Equal(RealIat, r.IssuedAtUnix);
        Equal(RealExp, r.ExpiresAtUnix);
        Equal(DateTimeOffset.FromUnixTimeSeconds(RealExp), r.ExpiresAt.Value);
        // Ids compare case-insensitively and trimmed; whitespace around the file text is ignored.
        Equal(LicenseStatus.Valid, LicenseVerifier.Verify("  " + RealLicense + "\n", RealProduct.ToUpperInvariant(), " " + RealUser + " ", RealIat + 10).Status);
    }

    private static void RealRejections()
    {
        Equal(LicenseStatus.Expired, LicenseVerifier.Verify(RealLicense, RealProduct, RealUser, RealExp).Status);
        Equal(LicenseStatus.Valid, LicenseVerifier.Verify(RealLicense, RealProduct, RealUser, RealExp - 1).Status);
        Equal(LicenseStatus.NotYetValid, LicenseVerifier.Verify(RealLicense, RealProduct, RealUser, RealIat - LicenseVerifier.ClockSkewSeconds - 1).Status);
        Equal(LicenseStatus.Valid, LicenseVerifier.Verify(RealLicense, RealProduct, RealUser, RealIat - LicenseVerifier.ClockSkewSeconds).Status);
        var wrongProduct = LicenseVerifier.Verify(RealLicense, "00000000-0000-0000-0000-000000000000", RealUser, RealIat + 10);
        Equal(LicenseStatus.WrongProduct, wrongProduct.Status);
        Require(wrongProduct.Message.Contains("Unity Texture Color Tool 1.0"));
        Equal(LicenseStatus.WrongProduct, LicenseVerifier.Verify(RealLicense, "", RealUser, RealIat + 10).Status);
        Equal(LicenseStatus.UserMismatch, LicenseVerifier.Verify(RealLicense, RealProduct, "someone-else", RealIat + 10).Status);
        var notSignedIn = LicenseVerifier.Verify(RealLicense, RealProduct, null, RealIat + 10);
        Equal(LicenseStatus.NotSignedIn, notSignedIn.Status);
        Equal(OnAirDeckLicense.SignInHint, notSignedIn.Message);
        Equal(LicenseStatus.NotSignedIn, LicenseVerifier.Verify(RealLicense, RealProduct, "", RealIat + 10).Status);
    }

    private static void Tampering()
    {
        var dot = RealLicense.IndexOf('.');
        var payloadJson = Encoding.UTF8.GetString(LicenseVerifier.FromBase64Url(RealLicense.Substring(0, dot)));
        var signature = RealLicense.Substring(dot + 1);
        // Same signature over a payload with a longer expiry.
        var longer = Base64Url(Encoding.UTF8.GetBytes(payloadJson.Replace("\"exp\":" + RealExp, "\"exp\":" + (RealExp + 86400 * 365)))) + "." + signature;
        Equal(LicenseStatus.InvalidSignature, LicenseVerifier.Verify(longer, RealProduct, RealUser, RealIat + 10).Status);
        // Same payload, one signature character changed.
        var last = signature[signature.Length - 1];
        var edited = RealLicense.Substring(0, RealLicense.Length - 1) + (last == 'A' ? 'B' : 'A');
        Equal(LicenseStatus.InvalidSignature, LicenseVerifier.Verify(edited, RealProduct, RealUser, RealIat + 10).Status);
        // Same payload, signature truncated.
        Equal(LicenseStatus.InvalidSignature, LicenseVerifier.Verify(RealLicense.Substring(0, RealLicense.Length - 8), RealProduct, RealUser, RealIat + 10).Status);
        // Rejected before the payload is read: no fields are exposed.
        Equal(null, LicenseVerifier.Verify(longer, RealProduct, RealUser, RealIat + 10).UserId);
    }

    private static void Malformed()
    {
        foreach (var text in new[] { null, "", "   ", "no-dot", ".sig", "payload.", "a.b.c", "!!!.sig", "abc.!!!", "a.b" })
        {
            var r = LicenseVerifier.Verify(text, RealProduct, RealUser, RealIat + 10);
            if (r.Status != LicenseStatus.LicenseMalformed && r.Status != LicenseStatus.InvalidSignature)
                throw new Exception("'" + text + "' gave " + r.Status);
        }
        // Valid base64url halves that are not a signed JSON object.
        Equal(LicenseStatus.InvalidSignature, LicenseVerifier.Verify(Base64Url(Encoding.UTF8.GetBytes("[1,2]")) + "." + Base64Url(new byte[256]), RealProduct, RealUser, RealIat + 10).Status);
    }

    private static void SelfSigned()
    {
        using (var rsa = RSA.Create(2048))
        {
            var key = rsa.ExportParameters(false);
            Func<string, string> sign = json =>
                Base64Url(Encoding.UTF8.GetBytes(json)) + "." + Base64Url(rsa.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            Func<string, string, string> payload = (name, extra) =>
                "{\"v\":1,\"iss\":\"onairdeck\",\"client\":\"unity-manager\",\"user_id\":\"u-1\",\"item_id\":\"i-1\",\"product_id\":\"p-1\",\"product_name\":\"" + name + "\",\"iat\":1000,\"exp\":2000,\"nonce\":\"n\"" + extra + "}";

            // Escapes and a nested value are read correctly; the embedded key rejects the throwaway signature.
            var fancy = sign(payload("Caf\\u00e9 \\\"Pro\\\" \\\\ 1:2", ",\"meta\":{\"a\":[1,2,{\"b\":null}],\"c\":true}"));
            var r = LicenseVerifier.Verify(fancy, "p-1", "u-1", 1500, key);
            Equal(LicenseStatus.Valid, r.Status);
            Equal("Café \"Pro\" \\ 1:2", r.ProductName);
            Equal(LicenseStatus.InvalidSignature, LicenseVerifier.Verify(fancy, "p-1", "u-1", 1500).Status);

            Equal(LicenseStatus.WrongIssuer, LicenseVerifier.Verify(sign(payload("n", "").Replace("\"iss\":\"onairdeck\"", "\"iss\":\"other\"")), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.WrongIssuer, LicenseVerifier.Verify(sign(payload("n", "").Replace("\"client\":\"unity-manager\"", "\"client\":\"warudo-plugin\"")), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.WrongIssuer, LicenseVerifier.Verify(sign(payload("n", "").Replace("\"v\":1", "\"v\":2")), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.LicenseMalformed, LicenseVerifier.Verify(sign(payload("n", "").Replace(",\"exp\":2000", "")), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.LicenseMalformed, LicenseVerifier.Verify(sign(payload("n", "").Replace("\"iat\":1000", "\"iat\":\"1000\"")), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.LicenseMalformed, LicenseVerifier.Verify(sign("[\"not\",\"an\",\"object\"]"), "p-1", "u-1", 1500, key).Status);
            Equal(LicenseStatus.LicenseMalformed, LicenseVerifier.Verify(sign("{\"v\":1"), "p-1", "u-1", 1500, key).Status);
        }
    }

    private static string RootFor(string name) { return Path.Combine(_project, name); }

    private static string WriteLicense(string root, string folder, string text)
    {
        var dir = Path.Combine(root, "Assets", "StudioRaming_Onairdeck", folder);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, OnAirDeckLicense.LicenseFileName);
        File.WriteAllText(path, text + "\n");
        return path;
    }

    private static void WriteMarker(string path, string userId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "{\"user_id\":\"" + userId + "\",\"client\":\"unity-manager\",\"issued_at\":1792000000}\n");
    }

    private static void CheckFindsLicense()
    {
        var root = RootFor("license-check");
        var marker = Path.Combine(root, "marker", "session.json");
        var saved = Snapshot();
        try
        {
            OnAirDeckLicense.ProjectRoot = root;
            OnAirDeckLicense.SessionMarkerPath = marker;
            OnAirDeckLicense.Clock = () => RealIat + 10;
            OnAirDeckLicense.CacheSeconds = 0;
            WriteMarker(marker, RealUser);
            Equal(RealUser, OnAirDeckLicense.ReadSignedInUserId());

            var real = WriteLicense(root, "Unity Texture Color Tool 1.0", RealLicense);
            WriteLicense(root, "garbage", "not a license");
            WriteLicense(root, "other-product", RealLicense.Replace("eyJ2IjoxLCJpc3Mi", "eyJ2IjoyLCJpc3Mi")); // payload edited: different bytes, same product id
            var r = OnAirDeckLicense.Check(RealProduct);
            Equal(LicenseStatus.Valid, r.Status);
            Equal(real, r.LicensePath);
            Equal("Unity Texture Color Tool 1.0", r.ProductName);

            // A forged copy with a later expiry sorts first but fails; the genuine one still wins.
            var dot = RealLicense.IndexOf('.');
            var json = Encoding.UTF8.GetString(LicenseVerifier.FromBase64Url(RealLicense.Substring(0, dot)));
            var forged = Base64Url(Encoding.UTF8.GetBytes(json.Replace("\"exp\":" + RealExp, "\"exp\":" + (RealExp + 86400 * 30)))) + RealLicense.Substring(dot);
            WriteLicense(root, "nested/deeper", forged);
            Equal(LicenseStatus.InvalidSignature, OnAirDeckLicense.Check(RealProduct, Path.Combine(root, "Assets", "StudioRaming_Onairdeck", "nested", "deeper", OnAirDeckLicense.LicenseFileName)).Status);
            r = OnAirDeckLicense.Check(RealProduct);
            Equal(LicenseStatus.Valid, r.Status);
            Equal(real, r.LicensePath);

            // Explicit path overload.
            Equal(LicenseStatus.Valid, OnAirDeckLicense.Check(RealProduct, real).Status);
            Equal(LicenseStatus.WrongProduct, OnAirDeckLicense.Check("p-other", real).Status);
            Equal(LicenseStatus.LicenseMissing, OnAirDeckLicense.Check(RealProduct, Path.Combine(root, "missing.license")).Status);
        }
        finally { Restore(saved); }
    }

    private static void CheckFailures()
    {
        var root = RootFor("license-failures");
        var marker = Path.Combine(root, "marker", "session.json");
        var saved = Snapshot();
        try
        {
            OnAirDeckLicense.ProjectRoot = root;
            OnAirDeckLicense.SessionMarkerPath = marker;
            OnAirDeckLicense.Clock = () => RealIat + 10;
            OnAirDeckLicense.CacheSeconds = 30;
            OnAirDeckLicense.ClearCache();

            // No marker, no install folder: not signed in comes first.
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(RealProduct).Status);
            Equal(null, OnAirDeckLicense.ReadSignedInUserId());
            WriteMarker(marker, RealUser);
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(RealProduct).Status); // cached
            OnAirDeckLicense.ClearCache();
            Equal(LicenseStatus.LicenseMissing, OnAirDeckLicense.Check(RealProduct).Status);

            OnAirDeckLicense.ClearCache();
            var path = WriteLicense(root, "Unity Texture Color Tool 1.0", RealLicense);
            var r = OnAirDeckLicense.Check(RealProduct);
            Equal(LicenseStatus.Valid, r.Status);

            // Cache: a removed marker is not noticed until the entry expires or the cache is cleared.
            File.Delete(marker);
            Require(OnAirDeckLicense.Check(RealProduct).IsValid);
            OnAirDeckLicense.Clock = () => RealIat + 10 + 31;
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(RealProduct).Status);

            OnAirDeckLicense.ClearCache();
            WriteMarker(marker, "another-user");
            Equal(LicenseStatus.UserMismatch, OnAirDeckLicense.Check(RealProduct).Status);
            Equal(path, OnAirDeckLicense.Check(RealProduct).LicensePath);

            OnAirDeckLicense.ClearCache();
            WriteMarker(marker, RealUser);
            OnAirDeckLicense.Clock = () => RealExp + 1;
            Equal(LicenseStatus.Expired, OnAirDeckLicense.Check(RealProduct).Status);

            // A marker without a user id counts as signed out.
            OnAirDeckLicense.ClearCache();
            File.WriteAllText(marker, "{\"client\":\"unity-manager\"}");
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(RealProduct).Status);
            File.WriteAllText(marker, "{broken");
            OnAirDeckLicense.ClearCache();
            Equal(LicenseStatus.NotSignedIn, OnAirDeckLicense.Check(RealProduct).Status);
        }
        finally { Restore(saved); }
    }

    private static object[] Snapshot()
    {
        return new object[] { OnAirDeckLicense.ProjectRoot, OnAirDeckLicense.SessionMarkerPath, OnAirDeckLicense.Clock, OnAirDeckLicense.CacheSeconds };
    }

    private static void Restore(object[] saved)
    {
        OnAirDeckLicense.ProjectRoot = (string)saved[0];
        OnAirDeckLicense.SessionMarkerPath = (string)saved[1];
        OnAirDeckLicense.Clock = (Func<long>)saved[2];
        OnAirDeckLicense.CacheSeconds = (int)saved[3];
        OnAirDeckLicense.ClearCache();
    }

    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
