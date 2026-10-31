using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
// UnityEditor also has an unrelated legacy PackageInfo type; name the Package Manager one explicitly.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    internal enum FeedbackKind { Bug, Feature, Question }

    // POST plugin-feedback (JsonUtility field names = JSON keys).
    [Serializable]
    internal class FeedbackRequest
    {
        public string kind;            // "bug" | "feature" | "question"
        public string message;         // 1–4000 characters
        public string errorCode;       // ≤ 200 characters, may be ""
        public string consoleLog;      // recent Console errors, may be ""
        public string unityVersion;
        public string managerVersion;
        public string os;
        public string[] packages;      // "name@version"
    }

    [Serializable]
    internal class FeedbackResponse
    {
        public bool ok;
        public string id;
        public string error;
    }

    /// <summary>What a feedback message reports about this machine; shown read-only before sending.</summary>
    internal sealed class FeedbackEnvironment
    {
        public string UnityVersion = "";
        public string ManagerVersion = "";
        public string Os = "";
        public string[] Packages = new string[0];

        /// <param name="packages">When given, these "name@version" entries are reported instead of the installed StudioRaming packages.</param>
        public static FeedbackEnvironment Collect(string[] packages = null)
        {
            var env = new FeedbackEnvironment
            {
                UnityVersion = Application.unityVersion ?? "",
                ManagerVersion = UpdateService.CurrentVersion ?? "",
                Os = SystemInfo.operatingSystem ?? "",
            };
            if (packages != null)
            {
                env.Packages = packages;
                return env;
            }
            var list = new List<string>();
            try
            {
#if UNITY_2021_1_OR_NEWER
                foreach (var info in PackageInfo.GetAllRegisteredPackages())
                {
                    if (info == null || string.IsNullOrEmpty(info.name)) continue;
                    if (!IsOnAirDeckPackage(info.name)) continue;
                    list.Add(info.name + "@" + info.version);
                }
#else
                // Unity 2019.4 / 2020 have no GetAllRegisteredPackages: read the folders under Packages/ (embedded and VPM packages).
                var root = PackageInstallService.ProjectRoot;
                var packagesFolder = System.IO.Path.Combine(root, "Packages");
                if (System.IO.Directory.Exists(packagesFolder))
                    foreach (var dir in System.IO.Directory.GetDirectories(packagesFolder))
                    {
                        var name = System.IO.Path.GetFileName(dir);
                        if (!IsOnAirDeckPackage(name)) continue;
                        list.Add(name + "@" + InstalledPackages.InstalledVersion(root, name));
                    }
#endif
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list installed packages: " + e.Message);
            }
            list.Sort(StringComparer.Ordinal);
            env.Packages = list.ToArray();
            return env;
        }

        /// <summary>StudioRaming / OnAirDeck packages (the Manager itself included).</summary>
        internal static bool IsOnAirDeckPackage(string name)
        {
            return name.StartsWith("com.studioraming.", StringComparison.Ordinal)
                || name.IndexOf("onairdeck", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    /// <summary>Sends "Feedback / 문의" to OnAirDeck (plugin-feedback); it lands in the admin bug-report list.</summary>
    internal static class FeedbackClient
    {
        public const int MaxMessage = 4000;
        public const int MaxErrorCode = 200;
        /// <summary>The server cuts the log at 20 KB; stay under it so whole entries arrive.</summary>
        public const int MaxConsoleLogBytes = 20 * 1024 - 256;

        public static string KindValue(FeedbackKind kind)
        {
            switch (kind)
            {
                case FeedbackKind.Feature: return "feature";
                case FeedbackKind.Question: return "question";
                default: return "bug";
            }
        }

        /// <summary>This PC as <see cref="FeedbackText.Redact"/> needs it: the project folder, the Windows user name and the host name.</summary>
        public static RedactContext LocalContext()
        {
            string user = "", host = "";
            try
            {
                user = Environment.UserName;
                host = Environment.MachineName;
            }
            catch (Exception)
            {
                // Redact what can be known.
            }
            return new RedactContext(PackageInstallService.ProjectRoot, user, host);
        }

        /// <summary>
        /// The one request both senders build: the 문의하기 screen and the install error report card. Every field that carries text
        /// of the person, the product or the Console (message, error code, log and each package entry) is redacted here, whole,
        /// BEFORE it is cut to its limit (<see cref="FeedbackText.Redact"/>), so no caller can send an unredacted note, product
        /// name or log, and a cut never splits a secret in two.
        /// </summary>
        public static FeedbackRequest Build(FeedbackKind kind, string message, string errorCode, string consoleLog, FeedbackEnvironment env)
        {
            env = env ?? FeedbackEnvironment.Collect();
            var context = LocalContext();
            return new FeedbackRequest
            {
                kind = KindValue(kind),
                message = FeedbackText.CutToChars(FeedbackText.Redact((message ?? "").Trim(), context), MaxMessage),
                errorCode = FeedbackText.CutToChars(FeedbackText.Redact((errorCode ?? "").Trim(), context), MaxErrorCode),
                consoleLog = ConsoleLogAsSent(consoleLog, context),
                unityVersion = env.UnityVersion,
                managerVersion = env.ManagerVersion,
                os = env.Os,
                packages = FeedbackText.RedactEach(env.Packages, context),
            };
        }

        /// <summary>The consoleLog field as it is sent: redacted whole, then cut to <see cref="MaxConsoleLogBytes"/>. The ask screen previews this.</summary>
        public static string ConsoleLogAsSent(string consoleLog, RedactContext context)
        {
            return FeedbackText.CutToBytes(FeedbackText.Redact(consoleLog ?? "", context), MaxConsoleLogBytes);
        }

        /// <summary>Why the message cannot be sent yet, or null when it can.</summary>
        public static string Problem(string message, string errorCode)
        {
            var text = (message ?? "").Trim();
            if (text.Length == 0) return "Write a message first.";
            if (text.Length > MaxMessage) return "The message is longer than " + MaxMessage + " characters.";
            if ((errorCode ?? "").Trim().Length > MaxErrorCode) return "The error code is longer than " + MaxErrorCode + " characters.";
            return null;
        }

        /// <summary>Sends the message and returns the report id. 401 → <see cref="SessionExpiredException"/>.</summary>
        public static async Task<string> SendAsync(string sessionToken, FeedbackRequest body)
        {
            using (var request = OnAirDeckApi.Post("plugin-feedback", JsonUtility.ToJson(body), sessionToken))
            {
                await request.SendAsync();
                if (request.responseCode == 429)
                    throw new Exception("You have sent several messages in the last few minutes. Please try again in about 10 minutes.");
                if (request.responseCode == 400 || request.responseCode == 413)
                {
                    var error = ReadError(request.downloadHandler != null ? request.downloadHandler.text : null);
                    throw new Exception("OnAirDeck did not accept the message" + (string.IsNullOrEmpty(error) ? "." : ": " + error + "."));
                }
                OnAirDeckApi.ThrowIfFailed(request, "Sending feedback");
                var response = JsonUtility.FromJson<FeedbackResponse>(request.downloadHandler.text);
                if (response == null || !response.ok) throw new Exception("Sending feedback failed: the server did not confirm it.");
                return response.id ?? "";
            }
        }

        private static string ReadError(string json)
        {
            return ApiErrorParser.ReadErrorCode(json);
        }
    }
}
