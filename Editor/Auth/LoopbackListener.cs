using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// One-shot HTTP listener on a random localhost port that receives the browser redirect
    /// (?code=…&amp;state=…) after the user approves the OnAirDeck consent page.
    /// Same approach as the StudioRaming Warudo plugin, which already works on users' PCs.
    /// </summary>
    internal sealed class LoopbackListener : IDisposable
    {
        private const string SuccessHtml =
            "<!doctype html><meta charset='utf-8'><title>OnAirDeck</title>" +
            "<style>body{font-family:system-ui,-apple-system,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#0f1116;color:#e6e6e6}" +
            ".c{text-align:center}.t{font-size:22px;margin:0 0 8px}.p{color:#9aa0a6;margin:0}</style>" +
            "<div class='c'><p class='t'>{TITLE}</p><p class='p'>You can close this tab and return to Unity.</p></div>";

        private readonly HttpListener _listener;

        public int Port { get; private set; }
        public string RedirectUri { get { return "http://localhost:" + Port + "/"; } }

        private LoopbackListener(HttpListener listener, int port)
        {
            _listener = listener;
            Port = port;
        }

        public static LoopbackListener Start()
        {
            var random = new Random();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var port = random.Next(38000, 48000);
                var listener = new HttpListener();
                listener.Prefixes.Add("http://localhost:" + port + "/");
                try
                {
                    listener.Start();
                    return new LoopbackListener(listener, port);
                }
                catch (HttpListenerException)
                {
                    listener.Close(); // port in use, try another
                }
            }
            throw new Exception("Could not open a local port for the sign-in callback.");
        }

        /// <summary>
        /// Waits for the redirect and returns the one-time code. Throws on deny, state mismatch,
        /// timeout or cancel.
        /// </summary>
        public async Task<string> WaitForCodeAsync(string expectedState, int timeoutMs, CancellationToken cancel)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var contextTask = _listener.GetContextAsync();
                var remaining = deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
                var finished = await Task.WhenAny(contextTask, Task.Delay(remaining, cancel));
                if (finished != contextTask)
                {
                    // Stopping the listener faults the pending GetContext; observe it so it isn't
                    // reported as an unobserved task exception.
                    _ = contextTask.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                    if (cancel.IsCancellationRequested) throw new OperationCanceledException("Sign-in was cancelled.");
                    throw new TimeoutException("Sign-in timed out. Please try again.");
                }

                var context = await contextTask;
                // Browsers may also ask for /favicon.ico; only the root path carries the result.
                if (context.Request.Url.AbsolutePath != "/")
                {
                    Respond(context, 404, "Not found");
                    continue;
                }

                var query = ParseQuery(context.Request.Url.Query);
                string error;
                if (query.TryGetValue("error", out error))
                {
                    Respond(context, 200, Page("Sign-in was cancelled"));
                    throw new Exception(error == "access_denied" ? "Sign-in was denied in the browser." : "Sign-in failed: " + error);
                }

                string state;
                if (!query.TryGetValue("state", out state) || state != expectedState)
                {
                    Respond(context, 400, Page("Sign-in could not be verified"));
                    throw new Exception("Sign-in could not be verified (state mismatch). Please try again.");
                }

                string code;
                if (!query.TryGetValue("code", out code) || string.IsNullOrEmpty(code))
                {
                    Respond(context, 400, Page("Sign-in failed"));
                    throw new Exception("No sign-in code was received. Please try again.");
                }

                Respond(context, 200, Page("✓ Signed in to OnAirDeck"));
                return code;
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _listener.Close(); } catch { /* already closed */ }
        }

        // The page's CSS contains braces, so fill in the title with Replace, not string.Format.
        private static string Page(string title)
        {
            return SuccessHtml.Replace("{TITLE}", WebUtility.HtmlEncode(title));
        }

        private static void Respond(HttpListenerContext context, int status, string html)
        {
            try
            {
                var bytes = Encoding.UTF8.GetBytes(html);
                context.Response.StatusCode = status;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Close(bytes, false);
            }
            catch
            {
                // The browser closed the connection; the result is already known.
            }
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return result;
            if (query.StartsWith("?")) query = query.Substring(1);
            foreach (var pair in query.Split('&'))
            {
                if (pair.Length == 0) continue;
                var eq = pair.IndexOf('=');
                var key = Uri.UnescapeDataString((eq < 0 ? pair : pair.Substring(0, eq)).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                result[key] = value;
            }
            return result;
        }
    }
}
