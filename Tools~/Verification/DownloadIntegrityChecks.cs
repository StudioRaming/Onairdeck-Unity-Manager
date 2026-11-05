using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.UnityManager;

// The server's checksum of a plugin-download file (Vdesk audit 2026-11-06 F2): plugin-download sends sha256 with every
// file, 64 lower-case hex digits or null. Real source: DownloadIntegrity. Downloader.DownloadVerifiedAsync (UnityWebRequest,
// the cache folder delete on a refusal, the warning on null) and the JsonUtility read of the field are compile-checked only.
internal static class DownloadIntegrityChecks
{
    private static string _dir;

    public static void Run(Action<string, Action> check, string project)
    {
        _dir = Path.Combine(project, "integrity");
        Directory.CreateDirectory(_dir);
        check("download integrity: the server's SHA-256 matches (either case) and the file is accepted", Matches);
        check("download integrity: another file than the store recorded is refused before anything is extracted", Mismatch);
        check("download integrity: no checksum from the server (null or empty, older servers) is accepted during the transition", NoChecksum);
        check("download integrity: a checksum that is not 64 hex digits is refused, never read as missing", Malformed);
        check("download integrity: the refusal is a PackageInstallException, so installs report it like other install errors", RefusalKind);
    }

    private static string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text, new UTF8Encoding(false));
        return path;
    }

    private static string Hex(string text)
    {
        using (var sha = SHA256.Create())
        {
            var sb = new StringBuilder(64);
            foreach (var b in sha.ComputeHash(new UTF8Encoding(false).GetBytes(text))) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Refused(Action action, string expectedPart)
    {
        try { action(); }
        catch (DownloadIntegrityException e)
        {
            Require(e.Message.Contains(expectedPart), "unexpected message: " + e.Message);
            return;
        }
        throw new Exception("expected a refusal");
    }

    private static void Matches()
    {
        var path = Write("ok.zip", "package bytes");
        Require(DownloadIntegrity.Check(path, Hex("package bytes"), "ok.zip") == DownloadIntegrity.Outcome.Verified, "lower case");
        Require(DownloadIntegrity.Check(path, Hex("package bytes").ToUpperInvariant(), "ok.zip") == DownloadIntegrity.Outcome.Verified, "upper case");
    }

    private static void Mismatch()
    {
        var path = Write("changed.zip", "changed bytes");
        Refused(() => DownloadIntegrity.Check(path, Hex("package bytes"), "changed.zip"), "does not match the store's checksum");
    }

    private static void NoChecksum()
    {
        var path = Write("legacy.zip", "old upload");
        Require(DownloadIntegrity.Check(path, null, "legacy.zip") == DownloadIntegrity.Outcome.NoServerChecksum, "null");
        Require(DownloadIntegrity.Check(path, "", "legacy.zip") == DownloadIntegrity.Outcome.NoServerChecksum, "empty");
    }

    private static void Malformed()
    {
        var path = Write("odd.zip", "package bytes");
        foreach (var bad in new[] { "abc", Hex("package bytes") + "0", Hex("package bytes").Substring(1) + "g", " " + Hex("package bytes").Substring(1) })
            Refused(() => DownloadIntegrity.Check(path, bad, "odd.zip"), "unreadable checksum");
        Require(!DownloadIntegrity.IsSha256Hex(null) && DownloadIntegrity.IsSha256Hex(new string('a', 64)) && !DownloadIntegrity.IsSha256Hex(new string('a', 63)), "IsSha256Hex");
    }

    private static void RefusalKind()
    {
        var path = Write("kind.zip", "x");
        try { DownloadIntegrity.Check(path, new string('0', 64), "kind.zip"); }
        catch (Exception e)
        {
            Require(e is PackageInstallException, "a PackageInstallException");
            return;
        }
        throw new Exception("expected a refusal");
    }
}
