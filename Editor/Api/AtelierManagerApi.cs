using System;
using System.Threading.Tasks;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The one public entry for lock screens of DRM add-ons (contract v8). They call it by reflection:
    /// <c>OnAirDeck.UnityManager.AtelierManagerApi.RequestLicense(string licenseId)</c>. Keep the type and method names exact.
    /// It returns at once, never throws, and the work (open the window, sign in if needed, issue that license as a user click)
    /// runs in <see cref="Runner"/>, which the editor side registers (<see cref="AtelierManagerBridge"/>).
    /// </summary>
    public static class AtelierManagerApi
    {
        internal static Func<string, Task> Runner;

        // Window seams: the window code (Editor/UI) registers these on load, so this file and the bridge stay UI-free.
        /// <summary>Opens (or focuses) the Manager window.</summary>
        internal static Action OpenWindow;
        /// <summary>Closes every open Manager window (a sign-in reopens it so it reads the new session).</summary>
        internal static Action CloseWindows;
        /// <summary>Selects the product of this license id in the open window (the id is also in <see cref="PendingLicenseId"/>).</summary>
        internal static Action<string> SelectLicense;
        /// <summary>
        /// Runs the install path of the window for a claimed import request (the person already confirmed it). The second argument
        /// is told the package and version just before a package install starts (a reload may end the awaiting code).
        /// </summary>
        internal static Func<ImportRequest, Action<string, string>, Task<ImportOutcome>> RunImport;
        /// <summary>True while the window works on something (an install, a batch, a self-update, a hand-off).</summary>
        internal static Func<bool> WindowBusy;

        /// <summary>The license id of the last lock-screen request; the window selects that product (read-only for the UI).</summary>
        internal static string PendingLicenseId { get; private set; }

        public static void RequestLicense(string licenseId)
        {
            try
            {
                if (string.IsNullOrEmpty(licenseId)) return;
                PendingLicenseId = licenseId;
                var runner = Runner;
                if (runner == null)
                {
                    Debug.LogWarning("[Atelier Manager] RequestLicense: the Manager is not ready yet.");
                    return;
                }
                var task = runner(licenseId);
                if (task != null)
                    task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Atelier Manager] RequestLicense failed: " + e.Message);
            }
        }
    }

    /// <summary>What <see cref="LicenseRequestFlow"/> needs from the editor (faked in the headless checks).</summary>
    internal interface ILicenseRequestHost
    {
        void OpenWindow();
        /// <summary>The signed-in session token, or null.</summary>
        string SessionToken();
        /// <summary>Runs the browser sign-in; returns the new token, or null when it did not finish.</summary>
        Task<string> SignInAsync();
        /// <summary>Issues the license file. <paramref name="reactivate"/> sends X-Atelier-Reactivate: 1.</summary>
        Task IssueAsync(string token, string licenseId, bool reactivate);
        void Log(string message);
    }

    internal static class LicenseRequestFlow
    {
        /// <summary>Open the window, sign in when needed, issue as a user click. Never throws.</summary>
        public static async Task RunAsync(string licenseId, ILicenseRequestHost host)
        {
            try
            {
                if (string.IsNullOrEmpty(licenseId)) return;
                host.OpenWindow();
                var token = host.SessionToken();
                if (string.IsNullOrEmpty(token)) token = await host.SignInAsync();
                if (string.IsNullOrEmpty(token)) return;
                await host.IssueAsync(token, licenseId, true);
            }
            catch (Exception e)
            {
                try { host.Log("License request for " + licenseId + " failed: " + e.Message); } catch (Exception) { }
            }
        }
    }
}
