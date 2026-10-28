using System;
using System.Collections.Generic;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>Thrown when the server no longer accepts the saved session (401).</summary>
    internal sealed class SessionExpiredException : Exception
    {
        public SessionExpiredException(string message) : base(message) { }
    }

    /// <summary>Any failed plugin-* call except 401, 409 device_limit, 409 device_released and 426. Message is short plain English; the body is only for logs.</summary>
    internal class ApiException : Exception
    {
        public long Status { get; private set; }
        /// <summary>The server's "error" code, or "".</summary>
        public string Code { get; private set; }
        /// <summary>Raw response body (may be JSON or anything). Log it; do not show it.</summary>
        public string ResponseBody { get; private set; }

        public ApiException(string message, long status, string code, string responseBody) : base(message)
        {
            Status = status;
            Code = code ?? "";
            ResponseBody = responseBody ?? "";
        }
    }

    /// <summary>409 device_limit: this PC is the one too many on at least one limited license.</summary>
    internal sealed class DeviceLimitException : ApiException
    {
        /// <summary>Only the licenses that refused.</summary>
        public DeviceLimitLicense[] Licenses { get; private set; }
        /// <summary>The user's devices active on a refused license (this_device marks this PC if listed).</summary>
        public DeviceEntry[] Devices { get; private set; }

        public DeviceLimitException(string message, string body, DeviceLimitLicense[] licenses, DeviceEntry[] devices)
            : base(message, 409, "device_limit", body)
        {
            Licenses = licenses ?? new DeviceLimitLicense[0];
            Devices = devices ?? new DeviceEntry[0];
        }
    }

    /// <summary>
    /// 409 device_released: this PC was released for these licenses and a background request tried to use them.
    /// Only a request a user click started (header X-Atelier-Reactivate: 1) may take the seat again.
    /// </summary>
    internal sealed class DeviceReleasedException : ApiException
    {
        public DeviceLicenseRef[] Licenses { get; private set; }

        public DeviceReleasedException(string message, string body, DeviceLicenseRef[] licenses) : base(message, 409, "device_released", body)
        {
            Licenses = licenses ?? new DeviceLicenseRef[0];
        }
    }

    /// <summary>426 manager_update_required: the server needs a newer Manager (old Managers send no device header).</summary>
    internal sealed class ManagerUpdateRequiredException : ApiException
    {
        public ManagerUpdateRequiredException(string message, string body) : base(message, 426, "manager_update_required", body) { }
    }

    /// <summary>Turns an error response into the right exception. No UnityEngine, so the Verification suite runs it.</summary>
    internal static class ApiErrorParser
    {
        private const int MaxMessage = 200;

        /// <summary>The server's "error" code from a JSON body, or null (empty, not JSON, no such key).</summary>
        public static string ReadErrorCode(string body)
        {
            return MiniJson.GetString(Parse(body), "error");
        }

        /// <param name="status">HTTP status (0 for a transport failure).</param>
        /// <param name="transportError">UnityWebRequest.error, used when there is no HTTP answer.</param>
        public static ApiException ToException(long status, string body, string action, string transportError)
        {
            var obj = Parse(body);
            var code = MiniJson.GetString(obj, "error") ?? "";
            var serverMessage = Clean(MiniJson.GetString(obj, "message"));

            if (status == 409 && code == "device_limit")
            {
                return new DeviceLimitException(
                    serverMessage.Length > 0 ? serverMessage : "This license is already used on the maximum number of devices.",
                    body, ReadLimits(obj), ReadDevices(obj));
            }
            if (status == 409 && code == "device_released")
            {
                return new DeviceReleasedException(
                    serverMessage.Length > 0 ? serverMessage : "This PC was released for this license. Use it here again from the Manager.",
                    body, ReadReleased(obj));
            }
            if (status == 426 || code == "manager_update_required")
            {
                return new ManagerUpdateRequiredException(
                    serverMessage.Length > 0 ? serverMessage : "Update the Atelier Manager to continue.", body);
            }

            string reason;
            if (serverMessage.Length > 0) reason = serverMessage;
            else if (status > 0) reason = "HTTP " + status;
            else reason = Clean(transportError).Length > 0 ? Clean(transportError) : "no answer from the server";
            return new ApiException(action + " failed: " + reason, status, code, body);
        }

        private static Dictionary<string, object> Parse(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            return MiniJson.ParseObject(body.TrimStart('﻿', ' ', '\t', '\r', '\n'));
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Trim().Replace("\r", " ").Replace("\n", " ");
            return t.Length > MaxMessage ? t.Substring(0, MaxMessage) : t;
        }

        private static List<Dictionary<string, object>> Objects(Dictionary<string, object> obj, string key)
        {
            var result = new List<Dictionary<string, object>>();
            object raw;
            var list = obj != null && obj.TryGetValue(key, out raw) ? raw as List<object> : null;
            if (list == null) return result;
            foreach (var entry in list)
            {
                var item = entry as Dictionary<string, object>;
                if (item != null) result.Add(item);
            }
            return result;
        }

        private static int Int(Dictionary<string, object> obj, string key)
        {
            long value;
            return MiniJson.TryGetInt(obj, key, out value) ? (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, value)) : 0;
        }

        private static bool Bool(Dictionary<string, object> obj, string key)
        {
            object raw;
            return obj.TryGetValue(key, out raw) && raw is bool && (bool)raw;
        }

        private static string Str(Dictionary<string, object> obj, string key)
        {
            return MiniJson.GetString(obj, key) ?? "";
        }

        private static DeviceLimitLicense[] ReadLimits(Dictionary<string, object> obj)
        {
            var result = new List<DeviceLimitLicense>();
            foreach (var item in Objects(obj, "licenses"))
                result.Add(new DeviceLimitLicense
                {
                    license_id = Str(item, "license_id"),
                    name = Str(item, "name"),
                    max_devices = Int(item, "max_devices"),
                    used = Int(item, "used"),
                    this_device = Bool(item, "this_device")
                });
            return result.ToArray();
        }

        private static DeviceLicenseRef[] ReadReleased(Dictionary<string, object> obj)
        {
            var result = new List<DeviceLicenseRef>();
            foreach (var item in Objects(obj, "licenses"))
                result.Add(new DeviceLicenseRef { license_id = Str(item, "license_id"), name = Str(item, "name") });
            return result.ToArray();
        }

        private static DeviceEntry[] ReadDevices(Dictionary<string, object> obj)
        {
            var result = new List<DeviceEntry>();
            foreach (var item in Objects(obj, "devices"))
            {
                var licenses = new List<DeviceLicenseRef>();
                foreach (var l in Objects(item, "licenses"))
                    licenses.Add(new DeviceLicenseRef { license_id = Str(l, "license_id"), name = Str(l, "name") });
                result.Add(new DeviceEntry
                {
                    device_id = Str(item, "device_id"),
                    name = Str(item, "name"),
                    platform = Str(item, "platform"),
                    first_seen_at = Str(item, "first_seen_at"),
                    last_seen_at = Str(item, "last_seen_at"),
                    this_device = Bool(item, "this_device"),
                    licenses = licenses.ToArray()
                });
            }
            return result.ToArray();
        }
    }
}
