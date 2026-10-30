using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    internal enum UpdateKind { Purchase, Package }

    /// <summary>One thing with a newer version than this project has.</summary>
    internal sealed class UpdateCandidate
    {
        public UpdateKind Kind;
        public string Name = "";
        public string Installed = "";   // packages only
        public string Latest = "";      // packages only
        public PurchaseItem Purchase;   // Kind == Purchase
        public LicenseEntry License;    // Kind == Package: the license that offers the newest version
        public LicenseVersionEntry Version; // Kind == Package: the newest version
    }

    /// <summary>The pure rules of <see cref="UpdateCheckService"/> (no UnityEngine, headless-checked).</summary>
    internal static class UpdateCheckLogic
    {
        public const long IntervalSeconds = 6L * 60 * 60;

        /// <summary>
        /// Whether the automatic check should run now: signed in, not yet this editor session, and the last successful
        /// check is at least 6 hours old (or never happened, or lies in the future because the clock moved back).
        /// </summary>
        public static bool ShouldRun(bool signedIn, bool alreadyThisSession, long nowUnix, long lastCheckUnix)
        {
            if (!signedIn || alreadyThisSession) return false;
            if (lastCheckUnix <= 0 || lastCheckUnix > nowUnix) return true;
            return nowUnix - lastCheckUnix >= IntervalSeconds;
        }

        /// <summary>
        /// Whether a purchase list fetched for <paramref name="requestUserId"/> may still be recorded when the response
        /// arrives: the signed-in account must still be the one the request was made for.
        /// </summary>
        public static bool ShouldApply(string requestUserId, string currentUserId)
        {
            return !string.IsNullOrEmpty(requestUserId) && string.Equals(requestUserId, currentUserId, StringComparison.Ordinal);
        }

        /// <summary>
        /// Purchases the server marks update_available, plus installed packages that a license offers in a newer
        /// version. A package that is not installed is not an update. A prerelease is only offered to a project that
        /// already runs a prerelease of that package.
        /// </summary>
        public static List<UpdateCandidate> Find(PurchaseItem[] purchases, LicenseEntry[] licenses, Func<string, string> installedVersion)
        {
            var found = new List<UpdateCandidate>();
            foreach (var item in purchases ?? new PurchaseItem[0])
                if (item != null && item.update_available && item.HasUnityFiles)
                    found.Add(new UpdateCandidate { Kind = UpdateKind.Purchase, Name = item.name ?? "", Purchase = item });

            var best = new Dictionary<string, UpdateCandidate>(StringComparer.Ordinal);
            foreach (var license in licenses ?? new LicenseEntry[0])
            {
                if (license == null || license.versions == null) continue;
                foreach (var version in license.versions)
                {
                    if (version == null || !version.IsPackage) continue;
                    var installed = installedVersion(version.package_name) ?? "";
                    if (installed.Length == 0) continue;
                    if (PackageVersion.IsPrerelease(version.package_version) && !PackageVersion.IsPrerelease(installed)) continue;
                    if (PackageVersion.Compare(version.package_version, installed) <= 0) continue;
                    UpdateCandidate current;
                    if (best.TryGetValue(version.package_name, out current) && PackageVersion.Compare(version.package_version, current.Latest) <= 0) continue;
                    best[version.package_name] = new UpdateCandidate
                    {
                        Kind = UpdateKind.Package,
                        Name = version.package_name,
                        Installed = installed,
                        Latest = version.package_version,
                        License = license,
                        Version = version
                    };
                }
            }
            var names = new List<string>(best.Keys);
            names.Sort(StringComparer.Ordinal);
            foreach (var name in names) found.Add(best[name]);
            return found;
        }
    }
}
