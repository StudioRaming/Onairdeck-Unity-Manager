using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
// UnityEditor also has an unrelated legacy PackageInfo type; name the Package Manager one explicitly.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OnAirDeck.UnityManager
{
    internal sealed class UpdateState
    {
        public string CurrentVersion;
        public VersionResponse Response;   // null when the check failed
        public bool CanApply;              // installed from git/registry → Client.Add(git_url) switches to the new tag
        public string ApplyBlockedReason;  // why the Update button is disabled
        public bool UseEmbedded;           // embedded install + a download_url: update by replacing the package folder (PackageInstallService)
        public string PackageName;
        public string PackageFolder;
        public string DownloadUrl = "";
        /// <summary>The Package Manager id of what is installed now (name@source), the way back for a git update that does not compile.</summary>
        public string PackageId = "";

        public bool HasLatest
        {
            get { return Response != null && Response.latest != null && !string.IsNullOrEmpty(Response.latest.version); }
        }

        public bool UpdateAvailable { get { return HasLatest && Response.update_available; } }
        public bool MustUpdate { get { return HasLatest && Response.must_update; } }
    }

    /// <summary>
    /// Self-update: asks plugin-version once per Editor session whether a newer Manager is
    /// published, and can switch the package to the published git URL. Git-URL packages never
    /// update on their own, so this is the only way users learn about a new version in Unity.
    /// </summary>
    internal static class UpdateService
    {
        // "<installed version>\n<plugin-version answer as JSON>": the answer of this Editor session, kept across script reloads.
        private const string CachedKey = "OnAirDeck.UnityManager.UpdateCheck";

        /// <summary>Result of the last successful check in this Editor session.</summary>
        public static UpdateState Last;
        private static Task<UpdateState> _inFlight;

        public static string CurrentVersion
        {
            get
            {
                var info = PackageInfo.FindForAssembly(typeof(UpdateService).Assembly);
                return info != null && !string.IsNullOrEmpty(info.version) ? info.version : "0.0.0";
            }
        }

        /// <summary>
        /// Asks the server at most once per Editor session and installed version; a failed check is retried on the next call.
        /// A script reload drops <see cref="Last"/>, so the session's answer comes back from SessionState (the update banner
        /// stays after any recompile); a different installed version (after a self-update) asks again.
        /// </summary>
        public static Task<UpdateState> CheckOncePerSessionAsync()
        {
            if (Last != null) return Task.FromResult(Last);
            if (_inFlight != null) return _inFlight;
            var version = CurrentVersion;
            var saved = SessionState.GetString(CachedKey, "");
            var cut = saved.IndexOf('\n');
            if (cut > 0 && saved.Substring(0, cut) == version)
            {
                VersionResponse response = null;
                try { response = JsonUtility.FromJson<VersionResponse>(saved.Substring(cut + 1)); }
                catch (ArgumentException) { }
                if (response != null)
                {
                    Last = Describe(version, response);
                    return Task.FromResult(Last);
                }
            }
            _inFlight = CheckAsync();
            return _inFlight;
        }

        public static async Task<UpdateState> CheckAsync()
        {
            var version = CurrentVersion;
            try
            {
                var response = await OnAirDeckApi.GetVersionAsync(version);
                var state = Describe(version, response);
                SessionState.SetString(CachedKey, version + "\n" + JsonUtility.ToJson(response));
                Last = state;
                return state;
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Update check failed: " + e.Message);
                return new UpdateState { CurrentVersion = version };
            }
            finally
            {
                _inFlight = null;
            }
        }

        /// <summary>How this install can take the answer's release: embedded folder swap, Package Manager git URL, or not at all.</summary>
        private static UpdateState Describe(string version, VersionResponse response)
        {
            var state = new UpdateState { CurrentVersion = version, Response = response };
            var info = PackageInfo.FindForAssembly(typeof(UpdateService).Assembly);
            if (info != null) state.PackageId = info.packageId ?? "";
            var source = info != null ? info.source : PackageSource.Unknown;
            var hasGitUrl = state.HasLatest && !string.IsNullOrEmpty(response.latest.git_url);
            var downloadUrl = state.HasLatest ? (response.latest.download_url ?? "") : "";
            if (downloadUrl.Length == 0 && response.download_url != null) downloadUrl = response.download_url;
            if (source == PackageSource.Embedded && IsWorkingCopy(info.resolvedPath))
            {
                // A developer's checkout (or a folder linked to one): never replaced, by a click or automatically.
                state.ApplyBlockedReason = "This copy of the Manager is a git working copy. Update it with git instead.";
            }
            else if (source == PackageSource.Embedded && downloadUrl.Length > 0)
            {
                // Installed without Git (the unitypackage or zip route): replace the package folder in place.
                state.UseEmbedded = true;
                state.CanApply = true;
                state.DownloadUrl = downloadUrl;
                state.PackageName = info.name;
                state.PackageFolder = info.resolvedPath;
            }
            else
            {
                state.CanApply = hasGitUrl && (source == PackageSource.Git || source == PackageSource.Registry);
                if (!hasGitUrl) state.ApplyBlockedReason = "This release has no install URL yet.";
                else if (!state.CanApply) state.ApplyBlockedReason = "Installed from disk (" + source + "); update your local copy instead.";
            }
            return state;
        }

        private static bool IsWorkingCopy(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return false;
            return System.IO.Directory.Exists(System.IO.Path.Combine(folder, ".git")) || System.IO.File.Exists(System.IO.Path.Combine(folder, ".git"));
        }

        /// <summary>Switches the package to the published git URL. Package Manager does the rest (and reloads scripts).</summary>
        public static AddRequest Apply(UpdateState state)
        {
            if (state == null || !state.CanApply || state.UseEmbedded) return null;
            return Client.Add(state.Response.latest.git_url);
        }
    }
}
