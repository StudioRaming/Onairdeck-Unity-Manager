using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Registers the editor half of <see cref="AtelierManagerApi"/> on every editor load.</summary>
    [InitializeOnLoad]
    internal static class AtelierManagerBridge
    {
        static AtelierManagerBridge()
        {
            AtelierManagerApi.Runner = id => LicenseRequestFlow.RunAsync(id, new EditorHost());
        }

        private sealed class EditorHost : ILicenseRequestHost
        {
            public void OpenWindow()
            {
                var open = AtelierManagerApi.OpenWindow;
                if (open == null) { Log("The Manager window is not registered yet."); return; }
                open();
                var select = AtelierManagerApi.SelectLicense;
                var pending = AtelierManagerApi.PendingLicenseId;
                if (select != null && !string.IsNullOrEmpty(pending)) select(pending);
            }

            public string SessionToken()
            {
                var session = SessionStore.Load();
                return session != null ? session.token : null;
            }

            public async Task<string> SignInAsync()
            {
                var session = await AuthService.SignInAsync(url => { }, CancellationToken.None);
                if (session == null) return null;
                SessionMarker.Write(session.userId);
                // The window read its session in OnEnable; reopening it shows the signed-in state.
                var close = AtelierManagerApi.CloseWindows;
                if (close != null) close();
                OpenWindow();
                return session.token;
            }

            public async Task IssueAsync(string token, string licenseId, bool reactivate)
            {
                var response = await OnAirDeckApi.GetPurchasesAndLicensesAsync(token);
                // An id the account does not own is left to the window's normal "not owned" state.
                await LicenseService.IssueSellerLicenseAsync(token, response.licenses, licenseId, reactivate);
            }

            public void Log(string message)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + message);
            }
        }
    }
}
