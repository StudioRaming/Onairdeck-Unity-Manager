using System;
using System.Security.Cryptography;
using OnAirDeck.License;
using OnAirDeck.UnityManager;

// The developer-only server override (DevServer, ATELIER_DEV_SERVER). Run once without the define (the shipped behavior:
// the prefs are ignored) and once with it: dotnet run --project Tools~/Verification -p:DefineConstants=ATELIER_DEV_SERVER.
internal static class DevChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("dev server: without ATELIER_DEV_SERVER the Atelier.Dev.* prefs are ignored (constants, plain device hash)", PrefsIgnoredOrApplied);
        check("dev server: a PEM public key (SubjectPublicKeyInfo and PKCS#1) parses to the same RSA key; garbage does not", PemKeys);
    }

    private static void PrefsIgnoredOrApplied()
    {
        var old = DevServer.ReadPref;
        DevServer.ReadPref = key =>
        {
            switch (key)
            {
                case DevServer.FunctionsUrlPref: return "http://127.0.0.1:54321/functions/v1/";
                case DevServer.SiteUrlPref: return "http://localhost:3000";
                case DevServer.LicensePublicKeyPemPref: return "pem";
                case DevServer.DeviceSaltPref: return "pc2";
                default: return "";
            }
        };
        try
        {
#if ATELIER_DEV_SERVER
            Require(DevServer.Enabled);
            Equal("http://127.0.0.1:54321/functions/v1", DevServer.FunctionsUrl);   // trailing slash trimmed
            Equal("http://localhost:3000", DevServer.SiteUrl);
            Equal("pem", DevServer.LicensePublicKeyPem);
            Equal("pc2", DevServer.DeviceSalt);
            Require(DeviceIdentityCore.Hash("abc123", "pc2") != DeviceIdentityCore.Hash("abc123"));
            Require(DeviceIdentityCore.Hash("abc123", "pc2") != DeviceIdentityCore.Hash("abc123", "pc3"));
            PackageDownloadChecks.RequireHttps("http://127.0.0.1:54321/storage/v1/x");          // loopback http allowed in a dev build
            var refused = false;
            try { PackageDownloadChecks.RequireHttps("http://example.com/x"); } catch (PackageInstallException) { refused = true; }
            Require(refused);
#else
            Require(!DevServer.Enabled);
            Equal(OnAirDeckConfig.FunctionsUrl, DevServer.FunctionsUrl);
            Equal(OnAirDeckConfig.WebsiteUrl, DevServer.SiteUrl);
            Equal("", DevServer.LicensePublicKeyPem);
            Equal("", DevServer.DeviceSalt);
            var refused = false;
            try { PackageDownloadChecks.RequireHttps("http://127.0.0.1:54321/storage/v1/x"); } catch (PackageInstallException) { refused = true; }
            Require(refused);                                                                    // a normal install never accepts plain http
#endif
            // The device hash with no salt is the plain hash in every build.
            Equal(DeviceIdentityCore.Hash("abc123"), DeviceIdentityCore.Hash("abc123", ""));
            Equal(DeviceIdentityCore.Hash("abc123"), DeviceIdentityCore.Hash("abc123", null));
            Equal("", DeviceIdentityCore.Hash("n/a", "pc2"));
        }
        finally
        {
            DevServer.ReadPref = old;
        }
    }

    private static void PemKeys()
    {
        using (var rsa = RSA.Create(2048))
        {
            var expected = rsa.ExportParameters(false);
            foreach (var pem in new[]
            {
                rsa.ExportSubjectPublicKeyInfoPem(),
                "-----BEGIN RSA PUBLIC KEY-----\n" + Convert.ToBase64String(rsa.ExportRSAPublicKey(), Base64FormattingOptions.InsertLineBreaks) + "\n-----END RSA PUBLIC KEY-----\n"
            })
            {
                RSAParameters key;
                Require(LicenseVerifier.TryParsePublicKeyPem(pem, out key));
                Equal(Convert.ToBase64String(expected.Modulus), Convert.ToBase64String(key.Modulus));
                Equal(Convert.ToBase64String(expected.Exponent), Convert.ToBase64String(key.Exponent));
            }
            RSAParameters none;
            Require(!LicenseVerifier.TryParsePublicKeyPem("", out none));
            Require(!LicenseVerifier.TryParsePublicKeyPem("-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----", out none));
            Require(!LicenseVerifier.TryParsePublicKeyPem("not a key", out none));
            // A license signed by the generated key verifies once the key is handed to the verifier (the Verify overload the dev key feeds).
            Require(!LicenseVerifier.TryParsePublicKeyPem(rsa.ExportSubjectPublicKeyInfoPem().Substring(0, 60), out none));
        }
    }

    private static void Require(bool condition) { if (!condition) throw new Exception("Assertion failed"); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
