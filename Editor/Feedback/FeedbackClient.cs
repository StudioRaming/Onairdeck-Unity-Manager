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

        public static FeedbackEnvironment Collect()
        {
            var env = new FeedbackEnvironment
            {
                UnityVersion = Application.unityVersion ?? "",
                ManagerVersion = UpdateService.CurrentVersion ?? "",
                Os = SystemInfo.operatingSystem ?? "",
            };
            var packages = new List<string>();
            try
            {
                foreach (var info in PackageInfo.GetAllRegisteredPackages())
                {
                    if (info == null || string.IsNullOrEmpty(info.name)) continue;
                    if (!IsOnAirDeckPackage(info.name)) continue;
                    packages.Add(info.name + "@" + info.version);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list installed packages: " + e.Message);
            }
            packages.Sort(StringComparer.Ordinal);
            env.Packages = packages.ToArray();
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

        public static FeedbackRequest Build(FeedbackKind kind, string message, string errorCode, bool attachConsoleErrors, FeedbackEnvironment env)
        {
            env = env ?? FeedbackEnvironment.Collect();
            return new FeedbackRequest
            {
                kind = KindValue(kind),
                message = (message ?? "").Trim(),
                errorCode = (errorCode ?? "").Trim(),
                consoleLog = attachConsoleErrors ? ConsoleErrorBuffer.Format(MaxConsoleLogBytes) : "",
                unityVersion = env.UnityVersion,
                managerVersion = env.ManagerVersion,
                os = env.Os,
                packages = env.Packages ?? new string[0],
            };
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
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var parsed = JsonUtility.FromJson<ErrorResponse>(json);
                return parsed != null ? parsed.error : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
