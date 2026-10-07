using System;
using System.IO;
using System.IO.Compression;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Puts downloaded files into the project: zips are extracted and loose files copied under
    /// Assets/StudioRaming_Onairdeck/&lt;Product&gt;/. .unitypackage files are handed to PackageImportQueue
    /// by the caller, because importing can trigger a domain reload.
    /// </summary>
    internal static class Installer
    {
        public const string InstallRoot = "Assets/StudioRaming_Onairdeck";

        /// <summary>Project-relative folder for a product, e.g. Assets/StudioRaming_Onairdeck/My Prop.</summary>
        public static string ProductFolder(string productName)
        {
            return InstallRoot + "/" + PathSafety.SafeFolderName(productName, "Product");
        }

        /// <summary>Copies one loose file into the product folder (overwrites). Returns the asset path.</summary>
        public static string CopyFile(string cachedPath, string productFolder, string safeFileName)
        {
            var absoluteFolder = ToAbsolute(productFolder);
            Directory.CreateDirectory(absoluteFolder);
            var destination = Path.Combine(absoluteFolder, safeFileName);
            if (!PathSafety.IsInside(absoluteFolder, destination)) throw new Exception("Refusing to write outside " + productFolder);
            File.Copy(cachedPath, destination, true);
            return productFolder + "/" + safeFileName;
        }

        /// <summary>
        /// Extracts a zip into the product folder. Entries with traversal, rooted or drive paths
        /// are skipped (and counted). Returns the number of files written.
        /// </summary>
        public static int ExtractZip(string zipPath, string productFolder, out int skipped)
        {
            var absoluteFolder = ToAbsolute(productFolder);
            Directory.CreateDirectory(absoluteFolder);
            var written = 0;
            skipped = 0;

            using (var stream = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    var isDirectory = entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\");
                    var relative = PathSafety.SafeRelativePath(entry.FullName);
                    if (relative == null)
                    {
                        if (!isDirectory) skipped++;
                        continue;
                    }
                    if (relative.StartsWith("__MACOSX/")) continue; // Finder resource forks

                    var destination = Path.GetFullPath(Path.Combine(absoluteFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!PathSafety.IsInside(absoluteFolder, destination))
                    {
                        skipped++;
                        continue;
                    }

                    if (isDirectory)
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    using (var input = entry.Open())
                    using (var output = File.Create(destination))
                    {
                        input.CopyTo(output);
                    }
                    written++;
                }
            }

            if (skipped > 0)
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Skipped " + skipped + " unsafe entr" + (skipped == 1 ? "y" : "ies") + " in " + Path.GetFileName(zipPath));
            return written;
        }

        public static bool IsUnityPackage(string fileName)
        {
            return fileName.EndsWith(".unitypackage", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsZip(string fileName)
        {
            return fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        }

        private static string ToAbsolute(string projectRelative)
        {
            var projectRoot = Path.GetDirectoryName(Application.dataPath);
            return Path.GetFullPath(Path.Combine(projectRoot, projectRelative.Replace('/', Path.DirectorySeparatorChar)));
        }
    }
}
