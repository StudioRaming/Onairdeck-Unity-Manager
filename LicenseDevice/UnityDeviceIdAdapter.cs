using UnityEditor;

namespace OnAirDeck.License.Device
{
    /// <summary>
    /// Gives the engine-free license assembly this PC's raw device id. The id is read once on the main thread, because
    /// SystemInfo may not be read from other threads. It stays separate from the Manager's DeviceIdentity so the dev
    /// salt never applies: an offline file is bound to the plain hash the server stores.
    /// </summary>
    [InitializeOnLoad]
    internal static class UnityDeviceIdAdapter
    {
        static UnityDeviceIdAdapter()
        {
            var raw = UnityEngine.SystemInfo.deviceUniqueIdentifier;
            OnAirDeckLicense.RawDeviceIdProvider = () => raw;
            OnAirDeckLicense.ClearCache();
        }
    }
}
