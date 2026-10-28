using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>One license the server refused to issue or renew, and why.</summary>
    internal sealed class RenewalRefusal
    {
        public string LicenseId = "";
        /// <summary>The license's display name.</summary>
        public string Name = "";
        /// <summary>The server's "error" code ("license_expired", "device_limit", ...), or "http_&lt;status&gt;" when the body had none.</summary>
        public string Code = "";
        public long Status;
        /// <summary>Short plain English, from the server.</summary>
        public string Message = "";
        public DateTime At;
        /// <summary>True for device_limit, device_released and manager_update_required: <see cref="DeviceLimitNotice"/> already shows those, so a "renewal refused" card should skip them.</summary>
        public bool ShownByDeviceNotice;
    }

    /// <summary>
    /// Per-license state of the last background renewal (<see cref="LicenseService.EnsureLicenseFilesAsync"/>) or issue: a
    /// refusal is kept until the same license is issued or renewed again, so the window can show a "renewal refused" card
    /// for a refusal nobody was watching. Transport failures (no answer, status 0) are not refusals and are not recorded.
    /// <see cref="Changed"/> is raised on the editor main thread (every caller continues on the editor context).
    /// </summary>
    internal static class LicenseRenewalStatus
    {
        public static event Action Changed;

        /// <summary>A license was issued or renewed: the server may have counted this PC on it again, so device lists are stale.</summary>
        public static event Action Issued;

        private static readonly Dictionary<string, RenewalRefusal> Refused = new Dictionary<string, RenewalRefusal>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every license currently refused, in no particular order.</summary>
        public static List<RenewalRefusal> All() { return new List<RenewalRefusal>(Refused.Values); }

        /// <summary>The refusal for one license, or null when its last renewal worked (or none ran).</summary>
        public static RenewalRefusal For(string licenseId)
        {
            RenewalRefusal refusal;
            return licenseId != null && Refused.TryGetValue(licenseId, out refusal) ? refusal : null;
        }

        /// <summary>Records the error when it is a server refusal (an HTTP error answer). Returns true when it was recorded.</summary>
        public static bool Refuse(string licenseId, string name, Exception error)
        {
            var api = error as ApiException;
            if (string.IsNullOrEmpty(licenseId) || api == null || api.Status < 400) return false;
            var code = string.IsNullOrEmpty(api.Code) ? "http_" + api.Status : api.Code;
            Refused[licenseId] = new RenewalRefusal
            {
                LicenseId = licenseId,
                Name = name ?? "",
                Code = code,
                Status = api.Status,
                Message = api.Message,
                At = DateTime.UtcNow,
                ShownByDeviceNotice = api is DeviceLimitException || api is DeviceReleasedException || api is ManagerUpdateRequiredException
            };
            RaiseChanged();
            return true;
        }

        /// <summary>The license was issued or renewed: its refusal is gone.</summary>
        public static void Succeed(string licenseId)
        {
            if (licenseId != null && Refused.Remove(licenseId)) RaiseChanged();
            var issued = Issued;
            if (issued != null) issued();
        }

        /// <summary>Keeps only the refusals of licenses still in the account (a sign-out or a removed license drops its card).</summary>
        public static void Prune(ICollection<string> licenseIds)
        {
            var drop = new List<string>();
            foreach (var id in Refused.Keys)
            {
                var kept = false;
                if (licenseIds != null) foreach (var known in licenseIds) if (string.Equals(known, id, StringComparison.OrdinalIgnoreCase)) kept = true;
                if (!kept) drop.Add(id);
            }
            foreach (var id in drop) Refused.Remove(id);
            if (drop.Count > 0) RaiseChanged();
        }

        public static void Clear()
        {
            if (Refused.Count == 0) return;
            Refused.Clear();
            RaiseChanged();
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }
    }
}
