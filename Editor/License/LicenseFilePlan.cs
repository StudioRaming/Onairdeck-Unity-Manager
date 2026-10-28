using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager
{
    /// <summary>One license file to issue or renew.</summary>
    internal sealed class LicenseFileJob
    {
        public LicenseEntry License;
        /// <summary>Project-relative folder, Assets/StudioRaming_Onairdeck/&lt;safe license name&gt;.</summary>
        public string Folder = "";
    }

    /// <summary>An onairdeck.license that already exists under Assets/StudioRaming_Onairdeck, and the license it was issued for.</summary>
    internal sealed class ExistingLicenseFile
    {
        public string Folder = "";
        public string LicenseId = "";
    }

    /// <summary>
    /// Which license files <see cref="LicenseService.EnsureLicenseFilesAsync"/> writes (no UnityEngine, headless-checked):
    /// licenses with a package installed in this project, whose file is missing or within 2 days of expiry.
    /// </summary>
    internal static class LicenseFilePlan
    {
        /// <summary>
        /// The license files that already exist in the install root (by license_id inside the file), so they keep renewing
        /// without a package (contract v8). Never throws. <paramref name="projectRoot"/> is the absolute project folder.
        /// </summary>
        public static List<ExistingLicenseFile> ExistingFiles(string projectRoot)
        {
            var result = new List<ExistingLicenseFile>();
            try
            {
                if (string.IsNullOrEmpty(projectRoot)) return result;
                var root = System.IO.Path.Combine(projectRoot, Installer.InstallRoot.Replace('/', System.IO.Path.DirectorySeparatorChar));
                if (!System.IO.Directory.Exists(root)) return result;
                foreach (var dir in System.IO.Directory.GetDirectories(root))
                {
                    try
                    {
                        var file = System.IO.Path.Combine(dir, LicenseFile.FileName);
                        if (!System.IO.File.Exists(file)) continue;
                        string id;
                        if (!LicenseFile.TryReadPayloadString(System.IO.File.ReadAllText(file), "license_id", out id) || id.Length == 0) continue;
                        result.Add(new ExistingLicenseFile { Folder = Installer.InstallRoot + "/" + System.IO.Path.GetFileName(dir), LicenseId = id });
                    }
                    catch (Exception)
                    {
                        // An unreadable file is the same as no file.
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        /// <summary>
        /// The folder a license file belongs in: where one already exists for this license, else the name-based folder
        /// (with the id suffix when another license of the account has the same name).
        /// </summary>
        public static string FolderOf(LicenseEntry[] licenses, LicenseEntry license, IList<ExistingLicenseFile> existing)
        {
            var found = ExistingFolder(existing, license.license_id);
            if (found != null) return found;
            var key = FolderFor(license, false);
            var same = 0;
            if (licenses != null)
                foreach (var other in licenses)
                    if (other != null && string.Equals(FolderFor(other, false), key, StringComparison.OrdinalIgnoreCase)) same++;
            return FolderFor(license, same > 1);
        }

        private static string ExistingFolder(IList<ExistingLicenseFile> existing, string licenseId)
        {
            if (existing == null || string.IsNullOrEmpty(licenseId)) return null;
            foreach (var file in existing)
                if (file != null && string.Equals(file.LicenseId, licenseId, StringComparison.OrdinalIgnoreCase)) return file.Folder;
            return null;
        }

        /// <param name="hasMarker">license_id to "an atelier.product.json with this id exists" (contract v8); null: no markers.</param>
        /// <param name="existing">License files already on disk: they keep renewing when no package is installed.</param>
        public static List<LicenseFileJob> Select(LicenseEntry[] licenses, Func<string, bool> isInstalled, Func<string, bool> needsRefresh,
            Func<string, bool> hasMarker = null, IList<ExistingLicenseFile> existing = null)
        {
            var jobs = new List<LicenseFileJob>();
            if (licenses == null) return jobs;
            // Two licenses can share one safe name; their folders then get the start of the license id so each file stays its own.
            var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var license in licenses)
                if (license != null)
                {
                    var key = FolderFor(license, false);
                    int n;
                    names[key] = names.TryGetValue(key, out n) ? n + 1 : 1;
                }
            foreach (var license in licenses)
            {
                if (license == null || string.IsNullOrEmpty(license.license_id)) continue;
                var existingFolder = ExistingFolder(existing, license.license_id);
                var present = HasInstalledPackage(license, isInstalled) || (hasMarker != null && hasMarker(license.license_id)) || existingFolder != null;
                if (!present) continue;
                var folder = existingFolder ?? FolderFor(license, names[FolderFor(license, false)] > 1);
                if (!needsRefresh(folder)) continue;
                jobs.Add(new LicenseFileJob { License = license, Folder = folder });
            }
            return jobs;
        }

        /// <summary>Assets/StudioRaming_Onairdeck/&lt;safe name&gt;, with " (&lt;first 8 of the id&gt;)" when the name is not unique.</summary>
        public static string FolderFor(LicenseEntry license, bool addIdSuffix)
        {
            var id = license.license_id ?? "";
            var suffix = "";
            if (addIdSuffix)
            {
                var shortId = PathSafety.SafeFileName(id.Length > 8 ? id.Substring(0, 8) : id);
                suffix = " (" + (shortId.Length > 0 ? shortId : "id") + ")";
            }
            // The name is cut to leave room for the suffix, so two long names that share their first characters stay apart.
            return Installer.InstallRoot + "/" + PathSafety.SafeFolderName(license.name ?? "", "Product", suffix);
        }

        private static bool HasInstalledPackage(LicenseEntry license, Func<string, bool> isInstalled)
        {
            if (license.versions == null) return false;
            foreach (var version in license.versions)
                if (version != null && !string.IsNullOrEmpty(version.package_name) && isInstalled(version.package_name)) return true;
            return false;
        }
    }
}
