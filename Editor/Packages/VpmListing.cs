using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>One version entry of a VPM repository listing.</summary>
    internal sealed class VpmVersion
    {
        public string Name = "";
        public string Version = "";
        public string Url = "";
        public string Sha256 = "";
    }

    /// <summary>
    /// A parsed VPM repository listing (index.json): packages.&lt;name&gt;.versions.&lt;version&gt; with url and zipSHA256.
    /// Only listings from <see cref="OnAirDeckConfig.TrustedVpmListings"/> are ever fetched.
    /// </summary>
    internal sealed class VpmListing
    {
        private readonly Dictionary<string, VpmVersion> _versions = new Dictionary<string, VpmVersion>(StringComparer.Ordinal);

        public static VpmListing Parse(string json)
        {
            var root = MiniJson.ParseObject(json);
            if (root == null) throw new PackageInstallException("The package listing is not valid JSON.");
            var listing = new VpmListing();
            var packages = Child(root, "packages");
            if (packages == null) return listing;
            foreach (var package in packages)
            {
                var versions = Child(package.Value as Dictionary<string, object>, "versions");
                if (versions == null) continue;
                foreach (var entry in versions)
                {
                    var fields = entry.Value as Dictionary<string, object>;
                    if (fields == null) continue;
                    var version = new VpmVersion
                    {
                        Name = package.Key,
                        Version = entry.Key,
                        Url = MiniJson.GetString(fields, "url") ?? "",
                        Sha256 = MiniJson.GetString(fields, "zipSHA256") ?? ""
                    };
                    listing._versions[Key(version.Name, version.Version)] = version;
                }
            }
            return listing;
        }

        /// <summary>The exact version of a package, or null when this listing does not have it.</summary>
        public VpmVersion Find(string name, string version)
        {
            VpmVersion found;
            return _versions.TryGetValue(Key(name, version), out found) ? found : null;
        }

        private static string Key(string name, string version) { return name + "@" + version; }

        private static Dictionary<string, object> Child(Dictionary<string, object> parent, string key)
        {
            object value;
            return parent != null && parent.TryGetValue(key, out value) ? value as Dictionary<string, object> : null;
        }
    }

    /// <summary>Transport and integrity checks shared by every package download.</summary>
    internal static class PackageDownloadChecks
    {
        /// <summary>Download URLs must be HTTPS: a presigned or release URL over plain HTTP could be swapped in transit.</summary>
        public static void RequireHttps(string url)
        {
            Uri uri;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new PackageInstallException("Refusing a package download that is not an HTTPS URL.");
#if ATELIER_DEV_SERVER
            if (uri.Scheme == Uri.UriSchemeHttp && DevServer.IsLoopback(uri)) return;   // a local stack only; never in a normal install
#endif
            if (uri.Scheme != Uri.UriSchemeHttps)
                throw new PackageInstallException("Refusing a package download that is not an HTTPS URL.");
        }

        public static string Sha256Hex(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(stream);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>Fails closed: an empty or different hash is a mismatch.</summary>
        public static void RequireSha256(string path, string expectedHex, string label)
        {
            if (string.IsNullOrEmpty(expectedHex))
                throw new PackageInstallException(label + ": the listing has no SHA-256 for this version.");
            var actual = Sha256Hex(path);
            if (!string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new PackageInstallException(label + ": the downloaded file does not match its SHA-256 (expected " + expectedHex + ", got " + actual + ").");
        }
    }
}
