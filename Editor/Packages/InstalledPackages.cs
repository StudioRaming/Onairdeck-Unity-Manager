using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace OnAirDeck.UnityManager
{
    /// <summary>An installed package whose exact vpmDependencies disagree with what is installed.</summary>
    internal sealed class PackageMismatch
    {
        public string Package = "";
        public string Version = "";
        public string Dependency = "";
        public string Required = "";
        /// <summary>Installed version of the dependency, "" when it is not installed at all.</summary>
        public string Installed = "";
    }

    /// <summary>What is installed under &lt;project&gt;/Packages, read from the folders (no Unity API, so it runs in plain tests).</summary>
    internal static class InstalledPackages
    {
        public static string FolderOf(string projectRoot, string packageName)
        {
            return Path.Combine(projectRoot, "Packages", packageName);
        }

        /// <summary>The package.json of Packages/&lt;name&gt;, or null when the folder or file is missing or unreadable.</summary>
        public static PackageManifest Read(string projectRoot, string packageName)
        {
            try
            {
                var path = Path.Combine(FolderOf(projectRoot, packageName), "package.json");
                if (!File.Exists(path)) return null;
                return PackageZip.ParseManifest(File.ReadAllText(path, Encoding.UTF8), false);
            }
            catch (Exception e)
            {
                if (e is PackageInstallException || e is IOException || e is UnauthorizedAccessException) return null;
                throw;
            }
        }

        public static string InstalledVersion(string projectRoot, string packageName)
        {
            var manifest = Read(projectRoot, packageName);
            return manifest == null ? "" : manifest.Version;
        }

        /// <summary>
        /// A message when the package was installed the old way (a plain folder under Assets/) and so
        /// must be migrated first, otherwise null.
        /// </summary>
        public static string LegacyRefusal(string projectRoot, string packageName)
        {
            string legacy;
            if (!OnAirDeckConfig.LegacyAssetFolders.TryGetValue(packageName, out legacy)) return null;
            if (!Directory.Exists(Path.Combine(projectRoot, legacy.Replace('/', Path.DirectorySeparatorChar)))) return null;
            return packageName + " is installed as a folder under " + legacy + ". Migrate the old Assets install first (remove that folder or convert it to the package), then install again.";
        }

        /// <summary>Installed packages among <paramref name="names"/> whose exact dependencies are missing or at another version.</summary>
        public static List<PackageMismatch> FindMismatches(string projectRoot, IEnumerable<string> names)
        {
            var result = new List<PackageMismatch>();
            foreach (var name in names)
            {
                var manifest = Read(projectRoot, name);
                if (manifest == null) continue;
                foreach (var dependency in manifest.VpmDependencies)
                {
                    var installed = InstalledVersion(projectRoot, dependency.Key);
                    if (installed == dependency.Value) continue;
                    result.Add(new PackageMismatch
                    {
                        Package = name,
                        Version = manifest.Version,
                        Dependency = dependency.Key,
                        Required = dependency.Value,
                        Installed = installed
                    });
                }
            }
            return result;
        }

        /// <summary>
        /// Which version of an add-on to install to fix a mismatch. Add-on and dependency versions match
        /// by convention, so the version equal to the installed dependency is preferred; without it the
        /// newest one is offered (its own dependencies are resolved from the zip at install time).
        /// </summary>
        public static string ChooseTarget(IEnumerable<string> versions, string installedDependencyVersion, out bool matchesInstalled)
        {
            string newest = null;
            matchesInstalled = false;
            foreach (var version in versions)
            {
                if (!string.IsNullOrEmpty(installedDependencyVersion) && version == installedDependencyVersion)
                {
                    matchesInstalled = true;
                    return version;
                }
                if (newest == null || PackageVersion.Compare(version, newest) > 0) newest = version;
            }
            return newest;
        }
    }
}
