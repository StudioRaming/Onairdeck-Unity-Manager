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
        private const string CheckedKey = "OnAirDeck.UnityManager.UpdateChecked";

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

        /// <summary>Checks at most once per Editor session; a failed check is retried on the next call.</summary>
        public static Task<UpdateState> CheckOncePerSessionAsync()
        {
            if (Last != null) return Task.FromResult(Last);
            if (_inFlight != null) return _inFlight;
            if (SessionState.GetBool(CheckedKey, false)) return Task.FromResult<UpdateState>(null);
            _inFlight = CheckAsync();
            return _inFlight;
        }

        public static async Task<UpdateState> CheckAsync()
        {
            var state = new UpdateState { CurrentVersion = CurrentVersion };
            try
            {
                state.Response = await OnAirDeckApi.GetVersionAsync(state.CurrentVersion);
                var info = PackageInfo.FindForAssembly(typeof(UpdateService).Assembly);
                var source = info != null ? info.source : PackageSource.Unknown;
                var hasGitUrl = state.HasLatest && !string.IsNullOrEmpty(state.Response.latest.git_url);
                var downloadUrl = state.HasLatest ? (state.Response.latest.download_url ?? "") : "";
                if (downloadUrl.Length == 0 && state.Response.download_url != null) downloadUrl = state.Response.download_url;
                if (source == PackageSource.Embedded && downloadUrl.Length > 0)
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
                SessionState.SetBool(CheckedKey, true);
                Last = state;
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Update check failed: " + e.Message);
                state.Response = null;
            }
            finally
            {
                _inFlight = null;
            }
            return state;
        }

        /// <summary>Switches the package to the published git URL. Package Manager does the rest (and reloads scripts).</summary>
        public static AddRequest Apply(UpdateState state)
        {
            if (state == null || !state.CanApply || state.UseEmbedded) return null;
            return Client.Add(state.Response.latest.git_url);
        }
    }
}
