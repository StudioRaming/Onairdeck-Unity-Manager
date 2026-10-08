using System;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OnAirDeck.UnityManager;

// Sign-in checks that need no Unity: the real LoopbackListener, Pkce and SavedSession sources
// are compiled into this program. Each check drives the listener the way a browser would, by
// requesting its localhost redirect URI. The OnAirDeck website and accounts are not contacted.
internal static class AuthChecks
{
    private const string State = "verification-state";
    private static readonly HttpClient Browser = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public static void Run(Action<string, Action> check)
    {
        check("PKCE verifier and S256 challenge", PkceShape);
        check("sign-in returns the approved code", ReturnsCode);
        check("favicon request does not end sign-in", IgnoresFavicon);
        check("Deny in the browser is reported", ReportsDeny);
        check("state mismatch is rejected", RejectsStateMismatch);
        check("missing code is rejected", RejectsMissingCode);
        check("sign-in times out", TimesOut);
        check("Cancel stops the wait", Cancels);
        check("listener releases its port", ReleasesPort);
        check("saved session expiry", SessionExpiry);
    }

    private static void PkceShape()
    {
        string verifier, challenge, other, ignored;
        Pkce.Create(out verifier, out challenge);
        Pkce.Create(out other, out ignored);
        Equal(43, verifier.Length);
        foreach (var c in verifier)
            Require((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_', "verifier has a non-base64url character");
        Require(verifier != other, "two verifiers were identical");
        string expected;
        using (var sha = SHA256.Create())
            expected = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Equal(expected, challenge);
    }

    private static void ReturnsCode()
    {
        using (var listener = LoopbackListener.Start())
        {
            var wait = listener.WaitForCodeAsync(State, 5000, CancellationToken.None);
            var page = Get(listener.RedirectUri + "?code=abc123&state=" + State);
            Equal("abc123", wait.GetAwaiter().GetResult());
            Equal(HttpStatusCode.OK, page.Item1);
            Require(page.Item2.Contains("Signed in to OnAirDeck"), "success page text missing");
        }
    }

    private static void IgnoresFavicon()
    {
        using (var listener = LoopbackListener.Start())
        {
            var wait = listener.WaitForCodeAsync(State, 5000, CancellationToken.None);
            Equal(HttpStatusCode.NotFound, Get(listener.RedirectUri + "favicon.ico").Item1);
            Require(!wait.IsCompleted, "favicon completed the sign-in");
            Get(listener.RedirectUri + "?code=after-favicon&state=" + State);
            Equal("after-favicon", wait.GetAwaiter().GetResult());
        }
    }

    private static void ReportsDeny()
    {
        using (var listener = LoopbackListener.Start())
        {
            var wait = listener.WaitForCodeAsync(State, 5000, CancellationToken.None);
            var page = Get(listener.RedirectUri + "?error=access_denied&state=" + State);
            Equal(HttpStatusCode.OK, page.Item1);
            Require(page.Item2.Contains("Sign-in was cancelled"), "deny page text missing");
            Equal("Sign-in was denied in the browser.", Failure(wait).Message);
        }
    }

    private static void RejectsStateMismatch()
    {
        using (var listener = LoopbackListener.Start())
        {
            var wait = listener.WaitForCodeAsync(State, 5000, CancellationToken.None);
            Equal(HttpStatusCode.BadRequest, Get(listener.RedirectUri + "?code=abc&state=forged").Item1);
            Require(Failure(wait).Message.Contains("state mismatch"), "state mismatch was not reported");
        }
    }

    private static void RejectsMissingCode()
    {
        using (var listener = LoopbackListener.Start())
        {
            var wait = listener.WaitForCodeAsync(State, 5000, CancellationToken.None);
            Equal(HttpStatusCode.BadRequest, Get(listener.RedirectUri + "?state=" + State).Item1);
            Require(Failure(wait).Message.Contains("No sign-in code"), "missing code was not reported");
        }
    }

    private static void TimesOut()
    {
        using (var listener = LoopbackListener.Start())
        {
            var error = Failure(listener.WaitForCodeAsync(State, 300, CancellationToken.None));
            Require(error is TimeoutException, "expected TimeoutException, got " + error.GetType().Name);
        }
    }

    private static void Cancels()
    {
        using (var listener = LoopbackListener.Start())
        using (var cancel = new CancellationTokenSource())
        {
            cancel.CancelAfter(200);
            var error = Failure(listener.WaitForCodeAsync(State, 5000, cancel.Token));
            Require(error is OperationCanceledException, "expected OperationCanceledException, got " + error.GetType().Name);
        }
    }

    private static void ReleasesPort()
    {
        int port;
        using (var listener = LoopbackListener.Start()) port = listener.Port;
        var again = new HttpListener();
        again.Prefixes.Add("http://localhost:" + port + "/");
        try { again.Start(); }
        finally { again.Close(); }
    }

    private static void SessionExpiry()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Require(new SavedSession { expiresAtUnix = now - 1 }.IsExpired, "past expiry was not expired");
        Require(new SavedSession { expiresAtUnix = now }.IsExpired, "expiry boundary was not expired");
        Require(!new SavedSession { expiresAtUnix = now + 60 }.IsExpired, "future expiry was expired");
    }

    private static Tuple<HttpStatusCode, string> Get(string url)
    {
        using (var response = Browser.GetAsync(url).GetAwaiter().GetResult())
            return Tuple.Create(response.StatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    private static Exception Failure(Task<string> wait)
    {
        try
        {
            var code = wait.GetAwaiter().GetResult();
            throw new Exception("expected a failure, got code " + code);
        }
        catch (Exception e) when (!e.Message.StartsWith("expected a failure", StringComparison.Ordinal))
        {
            return e;
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal(object expected, object actual) { if (!object.Equals(expected, actual)) throw new Exception("Expected " + expected + ", got " + actual); }
}
