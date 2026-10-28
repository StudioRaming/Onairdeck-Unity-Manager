using System;
using System.Threading.Tasks;

namespace OnAirDeck.UnityManager
{
    internal enum DependencySource { Installed, LicenseVersion, TrustedListing }

    internal enum DependencyErrorKind
    {
        /// <summary>An account license lists this package, but not at the version that is needed.</summary>
        NotPublishedYet,
        /// <summary>No license of the account lists this package: the person has to get it on Atelier first.</summary>
        GetOnAtelierFirst
    }

    /// <summary>
    /// A needed dependency is in no source. <see cref="Kind"/> lets the window add an "open the website" button
    /// for <see cref="DependencyErrorKind.GetOnAtelierFirst"/> (and a plain message for NotPublishedYet).
    /// </summary>
    internal sealed class DependencyUnavailableException : PackageInstallException
    {
        public DependencyErrorKind Kind { get; private set; }
        public string PackageName { get; private set; }
        public string PackageVersion { get; private set; }

        public DependencyUnavailableException(DependencyErrorKind kind, string name, string version, string message) : base(message)
        {
            Kind = kind;
            PackageName = name;
            PackageVersion = version;
        }
    }

    internal sealed class DependencyResolution
    {
        public DependencySource Source;
        public LicenseVersionEntry LicenseVersion; // Source == LicenseVersion
        public VpmVersion Listed;                  // Source == TrustedListing
    }

    /// <summary>
    /// Where an exact dependency comes from, in this order: already installed at that version, a license version of
    /// the signed-in account (same package_name and package_version), then the trusted VPM listings. No UnityEngine,
    /// so the order and the error kinds are checked headless.
    /// </summary>
    internal static class DependencyResolver
    {
        public static string NotPublishedMessage(string name, string version)
        {
            return name + " " + version + " is not published yet.";
        }

        public static string GetOnAtelierMessage(string name)
        {
            return "Get " + name + " on Atelier first.";
        }

        public static LicenseVersionEntry FindLicenseVersion(LicenseEntry[] licenses, string name, string version)
        {
            foreach (var license in licenses ?? new LicenseEntry[0])
            {
                if (license == null || license.versions == null) continue;
                foreach (var candidate in license.versions)
                    if (candidate != null && candidate.IsPackage
                        && string.Equals(candidate.package_name, name, StringComparison.Ordinal)
                        && string.Equals(candidate.package_version, version, StringComparison.Ordinal))
                        return candidate;
            }
            return null;
        }

        /// <summary>True when some license of the account lists this package at any version.</summary>
        public static bool AnyVersionOffered(LicenseEntry[] licenses, string name)
        {
            foreach (var license in licenses ?? new LicenseEntry[0])
            {
                if (license == null || license.versions == null) continue;
                foreach (var candidate in license.versions)
                    if (candidate != null && candidate.IsPackage && string.Equals(candidate.package_name, name, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// <paramref name="findInListings"/> returns the listing entry, null when every listing was reachable and none has it,
        /// and throws when a listing could not be read (a network problem is not "not published"). It is not called when an
        /// earlier source has the dependency. <paramref name="licenses"/> null means the caller has no account data: the plain
        /// "not in the trusted listing" message is used then.
        /// </summary>
        public static async Task<DependencyResolution> ResolveAsync(
            string name, string version, string installedVersion, LicenseEntry[] licenses, Func<Task<VpmVersion>> findInListings)
        {
            if (installedVersion == version) return new DependencyResolution { Source = DependencySource.Installed };
            var fromLicense = FindLicenseVersion(licenses, name, version);
            if (fromLicense != null) return new DependencyResolution { Source = DependencySource.LicenseVersion, LicenseVersion = fromLicense };
            var listed = await findInListings();
            if (listed != null) return new DependencyResolution { Source = DependencySource.TrustedListing, Listed = listed };
            if (licenses == null)
                throw new PackageInstallException("Version " + version + " of " + name + " is not in the trusted package listing.");
            if (AnyVersionOffered(licenses, name))
                throw new DependencyUnavailableException(DependencyErrorKind.NotPublishedYet, name, version, NotPublishedMessage(name, version));
            throw new DependencyUnavailableException(DependencyErrorKind.GetOnAtelierFirst, name, version, GetOnAtelierMessage(name));
        }
    }
}
