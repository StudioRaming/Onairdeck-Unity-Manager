#if ATELIER_DEV_SERVER
using OnAirDeck.License;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>Connects <see cref="DevServer"/> to EditorPrefs and the license verifier. Exists only with ATELIER_DEV_SERVER.</summary>
    [InitializeOnLoad]
    internal static class DevServerHook
    {
        static DevServerHook()
        {
            DevServer.ReadPref = key => EditorPrefs.GetString(key, "");
            LicenseVerifier.DevPublicKeyPem = () => DevServer.LicensePublicKeyPem;
            Debug.LogWarning("[Atelier Manager] ATELIER_DEV_SERVER is defined: the Atelier.Dev.* EditorPrefs can redirect the server, the license key and the device identity. Remove the define for a real install.");
        }
    }
}
#endif
