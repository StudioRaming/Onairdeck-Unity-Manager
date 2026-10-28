using System;

// Fields are assigned by JsonUtility through reflection, which the compiler can't see (CS0649).
#pragma warning disable 0649

namespace OnAirDeck.UnityManager
{
    // Device-limit shapes (contract MGR-DEVICE-UI-20261027). System-only types so the error parser can fill them too.

    [Serializable]
    internal class DeviceLicenseRef
    {
        public string license_id = "";
        public string name = "";
    }

    /// <summary>A license that refused (409) or is limited (GET plugin-devices limits[]).</summary>
    [Serializable]
    internal class DeviceLimitLicense
    {
        public string license_id = "";
        public string name = "";
        public int max_devices;
        public int used;
        public bool this_device;
    }

    [Serializable]
    internal class DeviceEntry
    {
        public string device_id = "";
        public string name = "";
        public string platform = "";
        public string first_seen_at = "";
        public string last_seen_at = "";
        public bool this_device;
        public DeviceLicenseRef[] licenses = new DeviceLicenseRef[0];
    }

    // GET plugin-devices
    [Serializable]
    internal class DevicesResponse
    {
        public DeviceEntry[] devices;
        public DeviceLimitLicense[] limits;
    }

    // POST plugin-devices
    [Serializable]
    internal class ReleaseDeviceRequest
    {
        public string action = "release";
        public string device_id;
    }

    [Serializable]
    internal class ReleaseDeviceResponse
    {
        public bool ok;
        public int released;
    }
}
