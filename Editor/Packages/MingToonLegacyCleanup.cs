using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>What the installer wrote to manager-result.json.</summary>
    internal sealed class CleanupResult
    {
        public string RequestCreatedUtc = "";
        public string AttemptId = "";
        public string TargetVersion = "";
        public string Phase = "";
        public string Failure = "";
        public string UpdatedUtc = "";
    }

    /// <summary>
    /// Everything that depends on the MingToon installer's file contract (mingtoon-6f, final 2026-10-29), in one class. The installer only
    /// cleans up the old Assets install (a one-way delete, no backup); the Manager installs the package. The Manager must not reference any
    /// MingToon assembly (between the delete and the install its types are gone), so the exchange is plain files:
    /// <list type="number">
    /// <item>request: &lt;project&gt;/ProjectSettings/MingToonInstaller/manager-request.json (UTF-8, no BOM), see <see cref="BuildRequestJson"/>;</item>
    /// <item>result: manager-result.json next to it, accepted only when requestCreatedUtc equals the request's createdUtc; phases
    /// <see cref="PhaseRemoved"/> (the Manager installs now), <see cref="PhaseComplete"/>, <see cref="PhaseFailed"/> (failure text with a
    /// <see cref="FailureKind"/> prefix);</item>
    /// <item>the installer .unitypackage proves it supports this by "managerCleanup": true in its mingtoon-bootstrap-config.json.</item>
    /// </list>
    /// </summary>
    internal static class MingToonLegacyCleanup
    {
        public const string ConfigEntry = "mingtoon-bootstrap-config.json";
        public const string CleanupFlag = "managerCleanup";
        public const string PhaseRemoved = "LegacyRemovedForManager";
        public const string PhaseComplete = "Complete";
        public const string PhaseFailed = "Failed";
        public const string KindRejected = "manager-request-rejected:";
        public const string KindPreflight = "preflight:";
        public const string KindRemoval = "removal:";
        public const string KindVerify = "verify:";
        private const int MaxConfigBytes = 64 * 1024;
        private const string StageRoot = "Library/OnAirDeck";

        private static string Abs(string root, string relative) { return Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); }

        // ---------- request ----------

        public static string CreatedUtc(DateTime nowUtc)
        {
            return nowUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>manager-request.json: schemaVersion 1, mode "legacy-cleanup", requestedBy "atelier-manager", packageId, targetVersion, createdUtc.</summary>
        public static string BuildRequestJson(string package, string targetVersion, string createdUtc)
        {
            var map = new Dictionary<string, object>();
            map["schemaVersion"] = 1L;
            map["mode"] = "legacy-cleanup";
            map["requestedBy"] = "atelier-manager";
            map["packageId"] = package;
            map["targetVersion"] = targetVersion;
            map["createdUtc"] = createdUtc;
            return JsonWriter.Write(map, "\n");
        }

        public static void WriteRequest(string root, LegacyInstallerSpec spec, string package, string targetVersion, string createdUtc)
        {
            AtomicFile.Write(Abs(root, spec.RequestPath), BuildRequestJson(package, targetVersion, createdUtc));
        }

        /// <summary>Deletes the request (the import failed or the hand-off ended). Never throws.</summary>
        public static void DeleteRequest(string root, LegacyInstallerSpec spec)
        {
            try { File.Delete(Abs(root, spec.RequestPath)); } catch (Exception) { }
        }

        // ---------- result ----------

        public static CleanupResult ParseResult(string json)
        {
            try
            {
                var fields = MiniJson.ParseObject(json);
                if (fields == null) return null;
                return new CleanupResult
                {
                    RequestCreatedUtc = MiniJson.GetString(fields, "requestCreatedUtc") ?? "",
                    AttemptId = MiniJson.GetString(fields, "attemptId") ?? "",
                    TargetVersion = MiniJson.GetString(fields, "targetVersion") ?? "",
                    Phase = MiniJson.GetString(fields, "phase") ?? "",
                    Failure = MiniJson.GetString(fields, "failure") ?? "",
                    UpdatedUtc = MiniJson.GetString(fields, "updatedUtc") ?? ""
                };
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The result file, or null when it is missing or half written (read again on the next poll).</summary>
        public static CleanupResult ReadResult(string root, LegacyInstallerSpec spec)
        {
            try
            {
                var path = Abs(root, spec.ResultPath);
                return File.Exists(path) ? ParseResult(File.ReadAllText(path)) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The failure prefix of a Failed result: rejected | preflight | removal | verify | other.</summary>
        public static string FailureKind(string failure)
        {
            var text = failure ?? "";
            if (text.StartsWith(KindRejected, StringComparison.Ordinal)) return "rejected";
            if (text.StartsWith(KindPreflight, StringComparison.Ordinal)) return "preflight";
            if (text.StartsWith(KindRemoval, StringComparison.Ordinal)) return "removal";
            if (text.StartsWith(KindVerify, StringComparison.Ordinal)) return "verify";
            return "other";
        }

        // ---------- the installer package ----------

        /// <summary>True when the installer's mingtoon-bootstrap-config.json (inside the .unitypackage) says managerCleanup is true. Anything unreadable is false.</summary>
        public static bool AllowsCleanup(string unityPackagePath)
        {
            try { return AllowsCleanup(ReadConfig(unityPackagePath)); }
            catch (Exception) { return false; }
        }

        public static bool AllowsCleanup(byte[] configJson)
        {
            try
            {
                if (configJson == null) return false;
                var fields = MiniJson.ParseObject(new UTF8Encoding(false).GetString(configJson));
                object value;
                return fields != null && fields.TryGetValue(CleanupFlag, out value) && value is bool && (bool)value;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads mingtoon-bootstrap-config.json out of a .unitypackage (a tar.gz: &lt;guid&gt;/pathname holds the asset path, &lt;guid&gt;/asset
        /// the content). Null when there is no such entry or it is larger than 64 KB.
        /// </summary>
        public static byte[] ReadConfig(string unityPackagePath)
        {
            var pathNames = new Dictionary<string, string>();
            var assets = new Dictionary<string, byte[]>();
            using (var file = File.OpenRead(unityPackagePath))
            using (var gz = new GZipStream(file, CompressionMode.Decompress))
            {
                var header = new byte[512];
                while (ReadFully(gz, header, 512) == 512)
                {
                    var allZero = true;
                    for (var i = 0; i < 512; i++) if (header[i] != 0) { allZero = false; break; }
                    if (allZero) break;
                    var name = Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0').Replace('\\', '/');
                    if (name.StartsWith("./", StringComparison.Ordinal)) name = name.Substring(2);
                    long size = 0;
                    foreach (var ch in Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' '))
                    {
                        if (ch < '0' || ch > '7') { size = -1; break; }
                        size = size * 8 + (ch - '0');
                    }
                    if (size < 0) return null;
                    var typeFlag = (char)header[156];
                    var padded = (size + 511) / 512 * 512;
                    var slash = name.IndexOf('/');
                    var leaf = slash < 0 ? "" : name.Substring(slash + 1);
                    var guid = slash < 0 ? "" : name.Substring(0, slash);
                    var wanted = (typeFlag == '0' || typeFlag == '\0') && size <= MaxConfigBytes && (leaf == "pathname" || leaf == "asset");
                    if (wanted)
                    {
                        var data = new byte[padded];
                        if (ReadFully(gz, data, (int)padded) != padded) return null;
                        var content = new byte[size];
                        Array.Copy(data, content, (int)size);
                        if (leaf == "pathname") pathNames[guid] = Encoding.UTF8.GetString(content).Split('\n')[0].Trim();
                        else assets[guid] = content;
                    }
                    else
                    {
                        Skip(gz, padded);
                    }
                }
            }
            foreach (var pair in pathNames)
            {
                var path = pair.Value.Replace('\\', '/');
                byte[] content;
                if ((path.EndsWith("/" + ConfigEntry, StringComparison.OrdinalIgnoreCase) || string.Equals(path, ConfigEntry, StringComparison.OrdinalIgnoreCase))
                    && assets.TryGetValue(pair.Key, out content)) return content;
            }
            return null;
        }

        private static int ReadFully(Stream stream, byte[] buffer, int count)
        {
            var total = 0;
            while (total < count)
            {
                var read = stream.Read(buffer, total, count - total);
                if (read <= 0) break;
                total += read;
            }
            return total;
        }

        private static void Skip(Stream stream, long count)
        {
            var buffer = new byte[16384];
            while (count > 0)
            {
                var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
                if (read <= 0) throw new EndOfStreamException();
                count -= read;
            }
        }

        // ---------- the staged packages, kept until the installer has deleted the old install ----------

        /// <summary>Library/OnAirDeck/handoff-&lt;package&gt;: outside Assets and Packages, survives domain reloads, same volume as the project.</summary>
        public static string StageFolder(string root, string package)
        {
            return Path.Combine(Abs(root, StageRoot), "handoff-" + PathSafety.SafeFolderName(package, "package"));
        }

        private static string StageManifestPath(string root, string package) { return Path.Combine(StageFolder(root, package), "stage.json"); }

        private static string PackagesFolder(string root) { return Abs(root, "Packages"); }

        private static string TargetOf(string root, string name) { return Path.Combine(PackagesFolder(root), PathSafety.SafeFolderName(name, "package")); }

        public static void Unstage(string root, string package)
        {
            try { var folder = StageFolder(root, package); if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception) { }
        }

        /// <summary>One package of the hand-off, fully extracted into the stage folder and hashed at staging time.</summary>
        internal sealed class StagedPackage
        {
            public string Name = "";
            public string Version = "";
            /// <summary>Folder name under the stage folder; the package moves from there to Packages/&lt;Name&gt;.</summary>
            public string Folder = "";
            /// <summary>Package-relative path ('/') to lowercase SHA-256 of every extracted file.</summary>
            public Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        /// <summary>Dependencies first, the requested package last (the order they are moved in).</summary>
        internal sealed class StageSet
        {
            public string Main = "";
            public List<StagedPackage> Packages = new List<StagedPackage>();
        }

        /// <summary>
        /// Before the request: extracts the requested package and its dependencies completely into the stage folder (PackageZip's traversal
        /// and identity guards; a file/directory clash or an unreadable entry fails here), records every file's SHA-256 in stage.json, and
        /// refuses a GUID that any file outside the old product folder already uses. Any failure removes the stage and throws a
        /// <see cref="PackageInstallException"/>; nothing else has been touched.
        /// </summary>
        public static StageSet Stage(string root, LegacyHandoffRequest request, string mainZip)
        {
            try
            {
                Unstage(root, request.Package);
                var set = new StageSet { Main = request.Package };
                var work = new List<LegacyHandoffDependency>(request.Dependencies);
                work.Add(new LegacyHandoffDependency { Name = request.Package, Version = request.Version, ZipPath = mainZip });
                foreach (var item in work)
                {
                    if (string.IsNullOrEmpty(item.ZipPath) || !File.Exists(item.ZipPath) || item.TargetFolder.Length > 0)
                        throw new PackageInstallException("The dependency " + item.Name + " " + item.Version + " cannot be staged for the hand-off. Nothing was imported.");
                    var folderName = PathSafety.SafeFolderName(item.Name, "package");
                    var destination = Path.Combine(StageFolder(root, request.Package), "stage", folderName);
                    try { PackageZip.Extract(item.ZipPath, destination, item.Name, item.Version); }
                    catch (PackageInstallException) { throw; }
                    catch (Exception e) { throw new PackageInstallException("The package " + item.Name + " " + item.Version + " could not be extracted: " + e.Message, e); }
                    set.Packages.Add(new StagedPackage { Name = item.Name, Version = item.Version, Folder = folderName, Files = HashFolder(destination) });
                }
                AtomicFile.Write(StageManifestPath(root, request.Package), BuildStageJson(set));
                RequireNoForeignGuids(root, request.Package, set, request.Folder);
                return set;
            }
            catch (Exception)
            {
                Unstage(root, request.Package);
                throw;
            }
        }

        private static string StagedFolder(string root, string package, StagedPackage item)
        {
            return Path.Combine(StageFolder(root, package), "stage", item.Folder);
        }

        // GUIDs of the staged packages against everything outside the target package folders (and, before the clean-up, the old product folder):
        // protected folders, external packages and any other place count.
        private static void RequireNoForeignGuids(string root, string package, StageSet set, string legacyFolder)
        {
            var guids = new HashSet<string>(StringComparer.Ordinal);
            var excluded = new List<string>();
            foreach (var item in set.Packages)
            {
                var staged = StagedFolder(root, package, item);
                if (Directory.Exists(staged)) guids.UnionWith(GuidCollisions.ReadFolderGuids(staged));
                else if (Directory.Exists(TargetOf(root, item.Name))) guids.UnionWith(GuidCollisions.ReadFolderGuids(TargetOf(root, item.Name)));
                excluded.Add(TargetOf(root, item.Name));
            }
            if (!string.IsNullOrEmpty(legacyFolder)) excluded.Add(Abs(root, legacyFolder));
            var collisions = GuidCollisions.Scan(root, guids, excluded);
            if (collisions.Count > 0) throw new PackageInstallException(GuidCollisions.Refusal(collisions));
        }

        internal static Dictionary<string, string> HashFolder(string folder)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                result[file.Substring(full.Length + 1).Replace('\\', '/')] = HashFile(file);
            return result;
        }

        private static string HashFile(string file)
        {
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var stream = File.OpenRead(file))
            {
                var bytes = sha.ComputeHash(stream);
                var sb = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string BuildStageJson(StageSet set)
        {
            var packages = new List<object>();
            foreach (var item in set.Packages)
            {
                var files = new Dictionary<string, object>();
                foreach (var pair in item.Files) files[pair.Key] = pair.Value;
                var map = new Dictionary<string, object>();
                map["name"] = item.Name;
                map["version"] = item.Version;
                map["folder"] = item.Folder;
                map["files"] = files;
                packages.Add(map);
            }
            var root = new Dictionary<string, object>();
            root["format"] = 1L;
            root["main"] = set.Main;
            root["packages"] = packages;
            return JsonWriter.Write(root, "\n");
        }

        /// <summary>The stage manifest written at staging; null when missing or unreadable.</summary>
        public static StageSet ReadStage(string root, string package)
        {
            try
            {
                var path = StageManifestPath(root, package);
                if (!File.Exists(path)) return null;
                var fields = MiniJson.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                object node;
                var list = fields != null && fields.TryGetValue("packages", out node) ? node as List<object> : null;
                if (list == null || list.Count == 0) return null;
                var set = new StageSet { Main = MiniJson.GetString(fields, "main") };
                foreach (var entry in list)
                {
                    var map = entry as Dictionary<string, object>;
                    object filesNode;
                    var files = map != null && map.TryGetValue("files", out filesNode) ? filesNode as Dictionary<string, object> : null;
                    if (files == null) return null;
                    var item = new StagedPackage { Name = MiniJson.GetString(map, "name"), Version = MiniJson.GetString(map, "version"), Folder = MiniJson.GetString(map, "folder") };
                    if (string.IsNullOrEmpty(item.Name) || string.IsNullOrEmpty(item.Folder)) return null;
                    foreach (var pair in files)
                    {
                        var hash = pair.Value as string;
                        if (!JsonInventoryReader.IsSha256(hash)) return null;
                        item.Files[pair.Key] = hash;
                    }
                    set.Packages.Add(item);
                }
                return set;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// True when <paramref name="folder"/> holds exactly the staged files (same list, same hashes); extra ".meta" files Unity wrote for
        /// folders are allowed. Any other extra file, a missing file or a different content is a different folder.
        /// </summary>
        internal static bool MatchesStage(string folder, StagedPackage item)
        {
            try
            {
                if (!Directory.Exists(folder)) return false;
                var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var seen = 0;
                foreach (var file in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(full.Length + 1).Replace('\\', '/');
                    string hash;
                    if (!item.Files.TryGetValue(relative, out hash))
                    {
                        if (relative.EndsWith(".meta", StringComparison.Ordinal)) continue;
                        return false;
                    }
                    if (!string.Equals(HashFile(file), hash, StringComparison.Ordinal)) return false;
                    seen++;
                }
                return seen == item.Files.Count;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The Manager's own install after the installer reported <see cref="PhaseRemoved"/>: re-checks GUIDs (something may have appeared since
        /// the start), then moves each staged folder, dependencies first and the requested package last, into Packages/&lt;name&gt;. A target
        /// that already matches the stage manifest was moved by an earlier run and is kept (resume). A differing requested-package folder is
        /// refused; a differing dependency folder is moved to Library/OnAirDeck/replaced first. The caller resolves afterwards (Client.Resolve,
        /// never Client.Add). Returns the folders in Packages/ in move order.
        /// </summary>
        public static List<string> InstallStaged(string root, string package)
        {
            var set = ReadStage(root, package);
            if (set == null) throw new PackageInstallException("The staged " + package + " package is missing or unreadable.");
            RequireNoForeignGuids(root, package, set, "");
            // The requested package's own folder is checked before anything moves: a refusal leaves Packages/ as it was.
            foreach (var item in set.Packages)
            {
                var existing = TargetOf(root, item.Name);
                if (item.Name == set.Main && (Directory.Exists(existing) || File.Exists(existing)) && !MatchesStage(existing, item))
                    throw new PackageInstallException("Packages/" + item.Name + " already exists with other content; nothing was installed.");
            }
            var result = new List<string>();
            foreach (var item in set.Packages)
            {
                var target = TargetOf(root, item.Name);
                var staged = StagedFolder(root, package, item);
                if (Directory.Exists(target) || File.Exists(target))
                {
                    if (MatchesStage(target, item)) { result.Add(target); continue; }
                    if (item.Name == set.Main)
                        throw new PackageInstallException("Packages/" + item.Name + " already exists with other content; nothing was installed.");
                    var aside = Path.Combine(Abs(root, StageRoot), "replaced", PathSafety.SafeFolderName(item.Name, "package") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(Path.GetDirectoryName(aside));
                    if (Directory.Exists(target)) Directory.Move(target, aside); else File.Move(target, aside);
                    var meta = target + ".meta";
                    if (File.Exists(meta)) File.Move(meta, aside + ".meta");
                }
                if (!Directory.Exists(staged)) throw new PackageInstallException("The staged " + item.Name + " folder is missing.");
                Directory.CreateDirectory(PackagesFolder(root));
                Directory.Move(staged, target);
                result.Add(target);
            }
            return result;
        }
    }
}
