using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace OnAirDeck.UnityManager
{
    /// <summary>The page the browser shows after the redirect.</summary>
    internal enum CallbackPage { SignedIn, Cancelled, Unverified, Failed }

    /// <summary>
    /// One-shot HTTP listener on a random localhost port that receives the browser redirect
    /// (?code=…&amp;state=…) after the user approves the Atelier consent page.
    /// Same approach as the StudioRaming Warudo plugin, which already works on users' PCs.
    /// </summary>
    internal sealed class LoopbackListener : IDisposable
    {
        private const string PageTemplate =
            "<!doctype html><html lang='{LANG}'><meta charset='utf-8'><title>{APP}</title>" +
            "<style>body{font-family:system-ui,-apple-system,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#0f1116;color:#e6e6e6}" +
            ".c{text-align:center;padding:0 16px}.t{font-size:22px;margin:0 0 8px}.p{color:#9aa0a6;margin:0}.e{color:#6f757b;font-size:13px;margin:16px 0 0}</style>" +
            "<div class='c'><p class='t'>{TITLE}</p><p class='p'>{BODY}</p>{ENGLISH}</div>";

        private readonly HttpListener _listener;
        private readonly int _language;

        public int Port { get; private set; }
        public string RedirectUri { get { return "http://localhost:" + Port + "/"; } }

        private LoopbackListener(HttpListener listener, int port, int language)
        {
            _listener = listener;
            Port = port;
            _language = language;
        }

        /// <summary><paramref name="language"/> is the Manager's language (0 ko, 1 en, 2 ja) for the page the browser shows.</summary>
        public static LoopbackListener Start(int language = 1)
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
                    return new LoopbackListener(listener, port, language);
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
                    Respond(context, 200, PageHtml(CallbackPage.Cancelled, _language));
                    throw new Exception(error == "access_denied" ? "Sign-in was denied in the browser." : "Sign-in failed: " + error);
                }

                string state;
                if (!query.TryGetValue("state", out state) || state != expectedState)
                {
                    Respond(context, 400, PageHtml(CallbackPage.Unverified, _language));
                    throw new Exception("Sign-in could not be verified (state mismatch). Please try again.");
                }

                string code;
                if (!query.TryGetValue("code", out code) || string.IsNullOrEmpty(code))
                {
                    Respond(context, 400, PageHtml(CallbackPage.Failed, _language));
                    throw new Exception("No sign-in code was received. Please try again.");
                }

                Respond(context, 200, PageHtml(CallbackPage.SignedIn, _language));
                return code;
            }
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch { /* already stopped */ }
            try { _listener.Close(); } catch { /* already closed */ }
        }

        /// <summary>
        /// The page in the Manager's language, named like the Manager (아틀리에 매니저 / Atelier Manager / アトリエマネージャー).
        /// A Korean or Japanese page also carries the English line. The CSS contains braces, so the parts go in with Replace.
        /// </summary>
        internal static string PageHtml(CallbackPage page, int language)
        {
            var html = PageTemplate
                .Replace("{LANG}", Pick(language, "ko", "en", "ja"))
                .Replace("{APP}", Pick(language, "아틀리에 매니저", "Atelier Manager", "アトリエマネージャー"))
                .Replace("{TITLE}", WebUtility.HtmlEncode(Title(page, language)))
                .Replace("{BODY}", WebUtility.HtmlEncode(Body(language)));
            var english = language == 1 ? "" : "<p class='e' lang='en'>" + WebUtility.HtmlEncode(Title(page, 1) + ". " + Body(1)) + "</p>";
            return html.Replace("{ENGLISH}", english);
        }

        private static string Title(CallbackPage page, int language)
        {
            switch (page)
            {
                case CallbackPage.SignedIn: return Pick(language, "✓ 아틀리에 매니저에 로그인했어요", "✓ Logged in to Atelier Manager", "✓ アトリエマネージャーにログインしました");
                case CallbackPage.Cancelled: return Pick(language, "로그인을 취소했어요", "The login was cancelled", "ログインをキャンセルしました");
                case CallbackPage.Unverified: return Pick(language, "로그인을 확인하지 못했어요", "The login could not be verified", "ログインを確認できませんでした");
                default: return Pick(language, "로그인하지 못했어요", "The login failed", "ログインできませんでした");
            }
        }

        private static string Body(int language)
        {
            return Pick(language, "이 탭을 닫고 Unity로 돌아가세요.", "You can close this tab and return to Unity.", "このタブを閉じてUnityに戻ってください。");
        }

        // The same 0 ko / 1 en / 2 ja order as ManagerText (that file needs UnityEditor; this one is checked headless).
        private static string Pick(int language, string ko, string en, string ja)
        {
            return language == 0 ? ko : language == 2 ? ja : en;
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
