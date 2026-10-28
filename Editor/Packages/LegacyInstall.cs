using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OnAirDeck.License;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>One old-install location that is moved out of Assets/ (a folder moves whole, or one file).</summary>
    internal sealed class LegacyItem
    {
        /// <summary>Project-relative, '/' separators, always under Assets/.</summary>
        public string Path = "";
        /// <summary>Files inside (without .meta); 1 for a single file.</summary>
        public int Files;
        /// <summary>Display name of the product this belongs to.</summary>
        public string Product = "";
        /// <summary>"config", "package", "marker" or "guid": which rule found it.</summary>
        public string Source = "";
        /// <summary>The probe whose package found this item (its inventory is the one that applies); null for a plan built by hand.</summary>
        public LegacyProbe Owner;
        /// <summary>True when sources 1-3 could not prove the folder is only the old install: the item must not move. See <see cref="BlockedReason"/>.</summary>
        public bool Blocked;
        /// <summary>"no-inventory", "inventory-unreadable" or "unmatched" (see <see cref="Unmatched"/>); "" when not blocked.</summary>
        public string BlockedReason = "";
        /// <summary>Project-relative files that stopped the move (user-added or changed), sorted.</summary>
        public readonly List<string> Unmatched = new List<string>();
        /// <summary>The product installer that validates and may detach this item, or null for the Manager's own move.</summary>
        public ILegacyHandler Handler;
        /// <summary>The product's own installer cleans this up: the Manager never moves it (see <see cref="ILegacyHandler.HandsOff"/>).</summary>
        public bool HandOff;
    }

    /// <summary>What a package install would move out of Assets/ first. Empty when there is nothing to move.</summary>
    internal sealed class LegacyPlan
    {
        public readonly List<LegacyItem> Items = new List<LegacyItem>();
        public bool IsEmpty { get { return Items.Count == 0; } }

        /// <summary>The items that must not move (sources 1-3 without a proving inventory or handler).</summary>
        public List<LegacyItem> BlockedItems()
        {
            return Items.FindAll(i => i.Blocked);
        }

        /// <summary>The item the product's own installer takes over, or null.</summary>
        public LegacyItem HandoffItem()
        {
            return Items.Find(i => i.HandOff);
        }

        public bool HasHandoff { get { return HandoffItem() != null; } }

        public string ProductNames()
        {
            var names = new List<string>();
            foreach (var item in Items) if (!names.Contains(item.Product)) names.Add(item.Product);
            return string.Join(", ", names.ToArray());
        }

        /// <summary>True when every path of this plan is also in <paramref name="approved"/> (case-insensitive).</summary>
        public bool IsCoveredBy(ISet<string> approved)
        {
            foreach (var item in Items) if (approved == null || !approved.Contains(item.Path)) return false;
            return true;
        }
    }

    /// <summary>One package of an install, as far as the legacy check needs to know it.</summary>
    internal sealed class LegacyProbe
    {
        public string Name = "";
        public string Version = "";
        public string Display = "";
        /// <summary>Downloaded zip; empty before the download (then only the config and marker rules can fire).</summary>
        public string ZipPath = "";
        /// <summary>The license this package belongs to, "" for a dependency.</summary>
        public string LicenseId = "";
        /// <summary>Absolute folder the package installs into; "" means Packages/&lt;Name&gt;.</summary>
        public string TargetFolder = "";
        /// <summary>The product installer file of this license version (OnAirDeckConfig.LegacyInstallers), or null when the version has none.</summary>
        public LegacyInstallerFile Installer;
        /// <summary>The package zip of this license version (the file the hand-off hands to the installer), set with <see cref="Installer"/>.</summary>
        public LegacyInstallerFile Zip;
    }

    /// <summary>The user said no: nothing was changed. An OperationCanceledException, so callers show their "cancelled" text.</summary>
    internal sealed class LegacyDeclinedException : OperationCanceledException
    {
        public LegacyDeclinedException() : base("Install cancelled. Nothing was changed.") { }
    }

    /// <summary>A background path found an old install: it never moves files, the item waits for a click.</summary>
    internal sealed class LegacyCleanupRequiredException : PackageInstallException
    {
        public LegacyCleanupRequiredException(string message) : base(message) { }
    }

    /// <summary>One backup folder that still holds an old install (state moving or moved: the install never finished).</summary>
    internal sealed class PendingRestore
    {
        public string ManifestPath = "";
        public string Package = "";
        public string Version = "";
        public string State = "";
        public string Created = "";
        /// <summary>Original project-relative paths (what [되돌리기] will bring back).</summary>
        public readonly List<string> Paths = new List<string>();
    }

    internal sealed class LegacyRestoreResult
    {
        public bool Ok;
        /// <summary>Written in the user's language (see LegacyInstallText).</summary>
        public string Message = "";
        /// <summary>Project-relative originals that are occupied now (the restore refused) or could not be moved.</summary>
        public readonly List<string> Blocked = new List<string>();
    }

    /// <summary>
    /// Items moved to the backup; <see cref="Restore"/> puts them back. The folder holds backup-manifest.json
    /// (from/to paths, package, state moving|moved|installed|restored, timestamps), written before the first move and
    /// updated as the move and install go on, so <see cref="PendingRestores()"/> can find an install that never finished.
    /// </summary>
    internal sealed class LegacyMove
    {
        public const string ManifestName = "backup-manifest.json";
        public const string StateMoving = "moving";
        public const string StateMoved = "moved";
        public const string StateInstalled = "installed";
        public const string StateRestored = "restored";
        /// <summary>A backup folder that holds files but whose manifest (and its last good copy) cannot be read: needs recovery by hand.</summary>
        public const string StateUnreadable = "unreadable";

        internal sealed class Moved { public string Original = ""; public string Backup = ""; public string Relative = ""; }

        public string BackupFolder = "";
        public string ProjectRoot = "";
        public string Package = "";
        public string Version = "";
        public string State = "";
        public readonly List<Moved> Entries = new List<Moved>();
        internal readonly List<string> Planned = new List<string>();
        internal readonly List<LegacyItem> Items = new List<LegacyItem>();   // parallel to Planned
        private string _created = "";

        public string ManifestPath { get { return Path.Combine(BackupFolder, ManifestName); } }

        /// <summary>The install finished: this backup is only a backup now.</summary>
        public void MarkInstalled() { WriteManifest(StateInstalled, DateTime.Now, false); }

        internal void WriteManifest(string state, DateTime now, bool mustSucceed)
        {
            try
            {
                if (_created.Length == 0) _created = now.ToString("o", CultureInfo.InvariantCulture);
                var items = new List<object>();
                foreach (var relative in Planned)
                {
                    var item = new Dictionary<string, object>();
                    item["from"] = relative;
                    item["to"] = ToRelative(ProjectRoot, Path.Combine(BackupFolder, relative.Replace('/', Path.DirectorySeparatorChar)));
                    items.Add(item);
                }
                var root = new Dictionary<string, object>();
                root["format"] = 1L;
                root["package"] = Package;
                root["version"] = Version;
                root["state"] = state;
                root["created"] = _created;
                root["updated"] = now.ToString("o", CultureInfo.InvariantCulture);
                root["items"] = items;
                // The folder is made by Move before the first write; a restore of a move that never started must not create it.
                if (!Directory.Exists(BackupFolder)) throw new DirectoryNotFoundException(BackupFolder);
                // Temp file + atomic replace; the previous good copy stays as backup-manifest.json.prev.
                AtomicFile.Write(ManifestPath, JsonWriter.Write(root, "\n"), true);
                State = state;
            }
            catch (Exception)
            {
                // A state update that cannot be written must not fail the install; the first write must, because nothing has moved yet.
                if (mustSucceed) throw;
            }
        }

        internal static string ToRelative(string root, string full)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Path.GetFullPath(full).Substring(rootFull.Length + 1).Replace('\\', '/');
        }

        /// <summary>Moves everything back, newest first. Returns what could not be put back (empty when all is back).</summary>
        public List<string> Restore()
        {
            var failed = new List<string>();
            for (var i = Entries.Count - 1; i >= 0; i--)
            {
                var entry = Entries[i];
                try
                {
                    if (!RestorePair(entry.Backup, entry.Original))
                    {
                        failed.Add(entry.Relative);
                        continue;
                    }
                    Entries.RemoveAt(i);
                }
                catch (Exception)
                {
                    failed.Add(entry.Relative);
                }
            }
            if (failed.Count == 0 && BackupFolder.Length > 0) WriteManifest(StateRestored, DateTime.Now, false);
            return failed;
        }

        // ---------- one asset and its .meta, restored as a pair ----------

        private static bool Present(string path) { return Directory.Exists(path) || File.Exists(path); }

        /// <summary>
        /// True when putting the pair back would run into something: with the asset in the backup both destinations (asset and .meta) must be
        /// free; with only the .meta left in the backup (an earlier restore got halfway) the asset must be back and its .meta destination free;
        /// with nothing left in the backup the asset must be in place. Nothing is moved.
        /// </summary>
        internal static bool PairBlocked(string backup, string original)
        {
            var assetBack = Present(backup);
            var metaBack = File.Exists(backup + ".meta");
            var assetHere = Present(original);
            var metaHere = File.Exists(original + ".meta");
            if (assetBack) return assetHere || metaHere;
            if (metaBack) return !assetHere || metaHere;
            return !assetHere;
        }

        /// <summary>
        /// Moves the asset and its .meta back together and returns true only when both are confirmed in place (a .meta that stayed in the
        /// backup is moved on a retry). False when blocked or when either part is still missing; may throw on an IO error.
        /// </summary>
        internal static bool RestorePair(string backup, string original)
        {
            if (PairBlocked(backup, original)) return false;
            var assetBack = Present(backup);
            var metaBack = File.Exists(backup + ".meta");
            if (assetBack)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(original));
                PackageTransaction.MoveEntry(backup, original);
            }
            if (metaBack) PackageTransaction.MoveEntry(backup + ".meta", original + ".meta");
            return Present(original) && (!metaBack || File.Exists(original + ".meta"));
        }

        // ---------- after a crash or a failed install: the manifests ----------

        /// <summary>Backups of the open project whose install never finished. Never throws.</summary>
        public static List<PendingRestore> PendingRestores()
        {
            var dataPath = Application.dataPath;
            return PendingRestores(string.IsNullOrEmpty(dataPath) ? "" : Path.GetDirectoryName(dataPath));
        }

        public static List<PendingRestore> PendingRestores(string projectRoot)
        {
            var found = new List<PendingRestore>();
            try
            {
                if (string.IsNullOrEmpty(projectRoot)) return found;
                var backups = PackageTransaction.BackupRoot(Path.GetFullPath(projectRoot));
                if (!Directory.Exists(backups)) return found;
                var folders = Directory.GetDirectories(backups, "legacy-*");
                Array.Sort(folders, StringComparer.Ordinal);
                foreach (var folder in folders)
                {
                    var manifest = Path.Combine(folder, ManifestName);
                    var pending = ReadPending(projectRoot, manifest);
                    if (pending != null && (pending.State == StateMoved || pending.State == StateMoving)) found.Add(pending);
                    else if (pending == null && HoldsBackedUpFiles(folder))
                        found.Add(new PendingRestore { ManifestPath = manifest, State = StateUnreadable, Created = Path.GetFileName(folder) });
                }
            }
            catch (Exception)
            {
            }
            return found;
        }

        // A backup folder with anything besides its manifest files holds moved assets, so an unreadable manifest must be shown, not skipped.
        private static bool HoldsBackedUpFiles(string folder)
        {
            try
            {
                foreach (var entry in Directory.GetFileSystemEntries(folder))
                {
                    var name = Path.GetFileName(entry);
                    if (name.StartsWith(ManifestName, StringComparison.Ordinal)) continue;
                    return true;
                }
            }
            catch (Exception)
            {
                return true;   // cannot look inside: assume there is something to recover
            }
            return false;
        }

        private static PendingRestore ReadPending(string projectRoot, string manifestPath)
        {
            Dictionary<string, object> root;
            List<KeyValuePair<string, string>> items;
            if (!TryRead(projectRoot, manifestPath, out root, out items)) return null;
            var pending = new PendingRestore
            {
                ManifestPath = manifestPath,
                Package = MiniJson.GetString(root, "package") ?? "",
                Version = MiniJson.GetString(root, "version") ?? "",
                State = MiniJson.GetString(root, "state") ?? "",
                Created = MiniJson.GetString(root, "created") ?? ""
            };
            foreach (var item in items) pending.Paths.Add(item.Key);
            return pending;
        }

        // Reads and validates a manifest: every "from" below Assets/ and every "to" inside this backup folder, so a
        // tampered file cannot move anything elsewhere.
        private static bool TryRead(string projectRoot, string manifestPath, out Dictionary<string, object> root, out List<KeyValuePair<string, string>> items)
        {
            // The last good copy (kept by the atomic write) stands in for a manifest that is missing or damaged.
            return TryReadFile(projectRoot, manifestPath, out root, out items) || TryReadFile(projectRoot, manifestPath + ".prev", out root, out items);
        }

        private static bool TryReadFile(string projectRoot, string manifestPath, out Dictionary<string, object> root, out List<KeyValuePair<string, string>> items)
        {
            root = null;
            items = new List<KeyValuePair<string, string>>();
            try
            {
                if (!File.Exists(manifestPath)) return false;
                root = MiniJson.ParseObject(File.ReadAllText(manifestPath, Encoding.UTF8));
                if (root == null) return false;
                object raw;
                var list = root.TryGetValue("items", out raw) ? raw as List<object> : null;
                if (list == null) return false;
                var rootFull = Path.GetFullPath(projectRoot);
                var assets = Path.Combine(rootFull, "Assets");
                var folder = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
                if (!PathSafety.IsInside(PackageTransaction.BackupRoot(rootFull), Path.Combine(folder, "x"))) return false;
                foreach (var entry in list)
                {
                    var map = entry as Dictionary<string, object>;
                    var source = MiniJson.GetString(map, "from");
                    var to = MiniJson.GetString(map, "to");
                    if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(to) || source.IndexOf("..", StringComparison.Ordinal) >= 0 || to.IndexOf("..", StringComparison.Ordinal) >= 0) return false;
                    if (!PathSafety.IsInside(assets, Path.Combine(rootFull, source.Replace('/', Path.DirectorySeparatorChar)))) return false;
                    if (!PathSafety.IsInside(folder, Path.Combine(rootFull, to.Replace('/', Path.DirectorySeparatorChar)))) return false;
                    items.Add(new KeyValuePair<string, string>(source, to));
                }
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Moves a pending backup back. Refuses (moves nothing) when any original place is occupied now; never throws.
        /// The manifest state becomes restored when everything is back.
        /// </summary>
        public static LegacyRestoreResult RestoreFromManifest(string manifestPath)
        {
            var dataPath = Application.dataPath;
            return RestoreFromManifest(manifestPath, string.IsNullOrEmpty(dataPath) ? "" : Path.GetDirectoryName(dataPath));
        }

        public static LegacyRestoreResult RestoreFromManifest(string manifestPath, string projectRoot)
        {
            var result = new LegacyRestoreResult();
            var language = LegacyInstallText.Language();
            try
            {
                Dictionary<string, object> root;
                List<KeyValuePair<string, string>> items;
                if (string.IsNullOrEmpty(projectRoot) || !TryRead(projectRoot, manifestPath, out root, out items))
                {
                    result.Message = LegacyInstallText.ManifestUnreadable(language);
                    return result;
                }
                var state = MiniJson.GetString(root, "state") ?? "";
                if (state != StateMoved && state != StateMoving)
                {
                    result.Message = LegacyInstallText.NothingToRestore(language);
                    return result;
                }
                var rootFull = Path.GetFullPath(projectRoot);
                // Check first, move after: a half-restored tree is worse than a refusal.
                foreach (var item in items)
                {
                    var backup = Path.Combine(rootFull, item.Value.Replace('/', Path.DirectorySeparatorChar));
                    var original = Path.Combine(rootFull, item.Key.Replace('/', Path.DirectorySeparatorChar));
                    if (PairBlocked(backup, original)) result.Blocked.Add(item.Key);
                }
                if (result.Blocked.Count > 0)
                {
                    result.Message = LegacyInstallText.RestoreOccupied(language, result.Blocked[0], result.Blocked.Count);
                    return result;
                }
                for (var i = items.Count - 1; i >= 0; i--)
                {
                    var backup = Path.Combine(rootFull, items[i].Value.Replace('/', Path.DirectorySeparatorChar));
                    var original = Path.Combine(rootFull, items[i].Key.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        if (!RestorePair(backup, original)) result.Blocked.Add(items[i].Key);
                    }
                    catch (Exception)
                    {
                        result.Blocked.Add(items[i].Key);
                    }
                }
                if (result.Blocked.Count > 0)
                {
                    result.Message = LegacyInstallText.RestoreIncomplete(language, Path.GetDirectoryName(Path.GetFullPath(manifestPath)));
                    return result;
                }
                root["state"] = StateRestored;
                root["updated"] = DateTime.Now.ToString("o", CultureInfo.InvariantCulture);
                try { AtomicFile.Write(manifestPath, JsonWriter.Write(root, "\n"), true); } catch (Exception) { }
                result.Ok = true;
                result.Message = LegacyInstallText.RestoredAll(language);
            }
            catch (Exception e)
            {
                result.Ok = false;
                result.Message = LegacyInstallText.RestoreIncomplete(language, e.Message);
            }
            return result;
        }
    }

    /// <summary>
    /// Contract v9, no UnityEditor: finds an older copy of a product under Assets/, moves it into
    /// Library/OnAirDeck/Backup/legacy-&lt;stamp&gt;/ after the caller's consent, and puts it back when the install
    /// does not happen. The caller refreshes the AssetDatabase.
    /// </summary>
    internal static class LegacyInstall
    {
        /// <summary>Test seam: called with a named point; may throw to fail the move.</summary>
        internal static Action<string> FaultHook;

        /// <summary>Product installers that validate and detach their own old install. The first that Handles an item owns it.</summary>
        internal static readonly List<ILegacyHandler> Handlers = new List<ILegacyHandler> { new InstallerHandoffHandler() };

        // ---------- detection ----------

        /// <summary>All four rules: config table, the zip's replacesAssetFolders, markers, GUID collisions under Assets/.</summary>
        public static LegacyPlan Detect(string projectRoot, IList<LegacyProbe> probes)
        {
            return Detect(projectRoot, probes, true);
        }

        /// <summary>Config table and markers only: usable before anything is downloaded (the batch's combined dialog).</summary>
        public static LegacyPlan DetectKnown(string projectRoot, IList<LegacyProbe> probes)
        {
            return Detect(projectRoot, probes, false);
        }

        private static LegacyPlan Detect(string projectRoot, IList<LegacyProbe> probes, bool withZips)
        {
            var plan = DetectUnvalidated(projectRoot, probes, withZips);
            foreach (var item in plan.Items) if (item.Source != "guid") Validate(Path.GetFullPath(projectRoot), item, withZips);
            return plan;
        }

        // Sources 1-3 move a known folder whole, so it must be proven to hold nothing but the old install: the product
        // installer's handler, or the inventory the incoming package declares. Otherwise the item is blocked (fail closed).
        private static void Validate(string root, LegacyItem item, bool withZips)
        {
            foreach (var handler in Handlers)
            {
                if (!handler.Handles(root, item.Owner, item)) continue;
                item.Handler = handler;
                if (handler.HandsOff(item.Owner, item))
                {
                    item.HandOff = true;
                    return;
                }
                IList<string> bad;
                try { bad = handler.Validate(root, item.Owner, item); }
                catch (Exception) { bad = new List<string> { item.Path }; }
                foreach (var path in bad) item.Unmatched.Add(path);
                item.Unmatched.Sort(StringComparer.Ordinal);
                item.Blocked = item.Unmatched.Count > 0;
                item.BlockedReason = item.Blocked ? "unmatched" : "";
                return;
            }
            item.Blocked = true;
            item.BlockedReason = "no-inventory";
            if (!withZips || item.Owner == null || string.IsNullOrEmpty(item.Owner.ZipPath)) return;
            try
            {
                var declared = PackageZip.Validate(item.Owner.ZipPath, item.Owner.Name, item.Owner.Version).LegacyInventory;
                if (string.IsNullOrEmpty(declared)) return;
                Dictionary<string, string> inventory;
                if (!LegacyInventory.Reader.TryRead(PackageZip.ReadEntry(item.Owner.ZipPath, declared, PackageZip.MaxInventoryBytes), out inventory))
                {
                    item.BlockedReason = "inventory-unreadable";
                    return;
                }
                item.Unmatched.AddRange(LegacyInventory.Unmatched(root, item, inventory));
                item.Blocked = item.Unmatched.Count > 0;
                item.BlockedReason = item.Blocked ? "unmatched" : "";
            }
            catch (Exception)
            {
                item.BlockedReason = "inventory-unreadable";
            }
        }

        private static LegacyPlan DetectUnvalidated(string projectRoot, IList<LegacyProbe> probes, bool withZips)
        {
            var root = Path.GetFullPath(projectRoot);
            var found = new List<LegacyItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guidOwner = new Dictionary<string, LegacyProbe>(StringComparer.Ordinal);
            var targets = new List<string>();

            foreach (var probe in probes)
            {
                var product = string.IsNullOrEmpty(probe.Display) ? probe.Name : probe.Display;
                string configured;
                if (OnAirDeckConfig.LegacyAssetFolders.TryGetValue(probe.Name, out configured)) Add(root, configured, product, "config", found, seen, probe);
                if (!string.IsNullOrEmpty(probe.LicenseId))
                    foreach (var folder in ProductMarkers.FoldersFor(root, probe.LicenseId))
                        Add(root, RelativeOf(root, folder), product, "marker", found, seen, probe);
                if (!withZips || string.IsNullOrEmpty(probe.ZipPath)) continue;

                var manifest = PackageZip.Validate(probe.ZipPath, probe.Name, probe.Version);
                foreach (var declared in manifest.ReplacesAssetFolders) Add(root, declared, product, "package", found, seen, probe);
                targets.Add(probe.TargetFolder.Length > 0 ? probe.TargetFolder : InstalledPackages.FolderOf(root, probe.Name));
                foreach (var guid in GuidCollisions.ReadZipGuids(probe.ZipPath))
                    if (!guidOwner.ContainsKey(guid)) guidOwner[guid] = probe;
            }

            if (withZips && guidOwner.Count > 0)
            {
                var guids = new HashSet<string>(guidOwner.Keys, StringComparer.Ordinal);
                var pure = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var collision in GuidCollisions.Scan(root, guids, targets))
                {
                    // Anything inside a package folder (another vendor's, or a stub) is not ours to move.
                    // A package imported into Assets (an ancestor with a package.json) is no old install either: never moved.
                    if (collision.Container.Length > 0 || collision.AssetsPackage.Length > 0) continue;
                    var owner = guidOwner[collision.Guid];
                    var asset = collision.Path.EndsWith(".meta", StringComparison.Ordinal) ? collision.Path.Substring(0, collision.Path.Length - 5) : collision.Path;
                    var isFolder = Directory.Exists(System.IO.Path.Combine(root, asset.Replace('/', System.IO.Path.DirectorySeparatorChar)));
                    // Walk up to the highest folder under Assets/ in which every asset collides: only product files move, the
                    // user's own files in a shared folder stay. No such folder: the colliding file moves alone (with its .meta).
                    string best = null;
                    var current = isFolder ? asset : ParentOf(asset);
                    while (current.Length > 0 && !string.Equals(current, "Assets", StringComparison.OrdinalIgnoreCase) && !IsNeverMove(current) && !ContainsNeverMove(current) && IsPure(root, current, guids, pure))
                    {
                        best = current;
                        current = ParentOf(current);
                    }
                    var entry = best ?? (isFolder ? null : asset);
                    if (entry != null && IsNeverMove(entry)) entry = null;
                    if (entry != null)
                        Add(root, entry, string.IsNullOrEmpty(owner.Display) ? owner.Name : owner.Display, "guid", found, seen, owner);
                }
            }

            // An item inside another item moves with it.
            var plan = new LegacyPlan();
            foreach (var item in found)
            {
                var nested = false;
                foreach (var other in found)
                    if (!ReferenceEquals(other, item) && item.Path.StartsWith(other.Path + "/", StringComparison.OrdinalIgnoreCase)) { nested = true; break; }
                if (!nested) plan.Items.Add(item);
            }
            return plan;
        }

        /// <summary>
        /// The guard rails: only a path strictly under Assets/ and inside the project, never Assets itself, never a
        /// folder that holds a package.json (or a package.json itself). Returns the normalized relative path.
        /// </summary>
        internal static bool TryGuard(string projectRoot, string raw, out string relative)
        {
            relative = null;
            var text = (raw ?? "").Trim().Replace('\\', '/').Trim('/');
            if (text.Length == 0 || text.IndexOf(':') >= 0) return false;
            if (IsNeverMove(text) || ContainsNeverMove(text)) return false;
            var segments = text.Split('/');
            foreach (var segment in segments)
                if (segment.Length == 0 || segment == "." || segment == "..") return false;
            if (segments.Length < 2 || !string.Equals(segments[0], "Assets", StringComparison.OrdinalIgnoreCase)) return false;
            text = "Assets/" + string.Join("/", segments, 1, segments.Length - 1);

            var assets = Path.Combine(Path.GetFullPath(projectRoot), "Assets");
            var full = Path.GetFullPath(Path.Combine(Path.GetFullPath(projectRoot), text.Replace('/', Path.DirectorySeparatorChar)));
            if (!PathSafety.IsInside(assets, full)) return false;
            // Nothing inside an external package folder (a package.json in any ancestor below Assets/) is ever moved.
            for (var ancestor = Path.GetDirectoryName(full); !string.IsNullOrEmpty(ancestor) && !string.Equals(Path.GetFullPath(ancestor).TrimEnd(Path.DirectorySeparatorChar), assets.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase); ancestor = Path.GetDirectoryName(ancestor))
                if (File.Exists(Path.Combine(ancestor, "package.json"))) return false;
            if (Directory.Exists(full))
            {
                if (File.Exists(Path.Combine(full, "package.json"))) return false;
            }
            else if (File.Exists(full))
            {
                if (string.Equals(Path.GetFileName(full), "package.json", StringComparison.OrdinalIgnoreCase)) return false;
            }
            else
            {
                return false;
            }
            relative = text;
            return true;
        }

        /// <summary>True when the path is one of <see cref="OnAirDeckConfig.NeverMoveAssetFolders"/> or inside one.</summary>
        internal static bool IsNeverMove(string relative)
        {
            var path = (relative ?? "").Replace('\\', '/').Trim('/');
            foreach (var folder in OnAirDeckConfig.NeverMoveAssetFolders)
                if (string.Equals(path, folder, StringComparison.OrdinalIgnoreCase) || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>True when a never-move folder is inside the path (moving it would take that folder along).</summary>
        internal static bool ContainsNeverMove(string relative)
        {
            var path = (relative ?? "").Replace('\\', '/').Trim('/');
            foreach (var folder in OnAirDeckConfig.NeverMoveAssetFolders)
                if (folder.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Add(string root, string raw, string product, string source, List<LegacyItem> found, HashSet<string> seen, LegacyProbe owner)
        {
            string relative;
            if (!TryGuard(root, raw, out relative) || !seen.Add(relative)) return;
            found.Add(new LegacyItem { Path = relative, Files = CountFiles(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))), Product = product, Source = source, Owner = owner });
        }

        private static string RelativeOf(string root, string folder)
        {
            var full = Path.GetFullPath(folder);
            var rootFull = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.Length <= rootFull.Length + 1) return "";
            return full.Substring(rootFull.Length + 1).Replace('\\', '/');
        }

        private static int CountFiles(string path)
        {
            try
            {
                if (File.Exists(path)) return 1;
                var count = 0;
                foreach (var file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                    if (!file.EndsWith(".meta", StringComparison.Ordinal)) count++;
                return count;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ---------- consent and move ----------

        /// <summary>
        /// Detects, asks, moves. Returns null when there is nothing to move. A background caller
        /// (<paramref name="userInitiated"/> false) or one without a confirm callback never moves anything: it gets
        /// <see cref="LegacyCleanupRequiredException"/>. A "no" gives <see cref="LegacyDeclinedException"/> and the tree is untouched.
        /// </summary>
        public static LegacyMove Prepare(string projectRoot, IList<LegacyProbe> probes, bool userInitiated, Func<LegacyPlan, bool> confirm, DateTime now)
        {
            LegacyHandoffRequest handoff;
            var move = Prepare(projectRoot, probes, userInitiated, confirm, now, out handoff);
            // A caller that cannot hand off to a product installer gets the same answer as a background caller.
            if (handoff != null) throw new LegacyCleanupRequiredException(LegacyInstallText.CleanupRequired(LegacyInstallText.Language(), handoff.Product));
            return move;
        }

        /// <summary>
        /// Like the overload above, plus the hand-off: when the product's own installer cleans up the old install
        /// (<see cref="ILegacyHandler.HandsOff"/>), <paramref name="handoff"/> is set after the user's "yes" and nothing is moved. A background
        /// caller, a missing confirm callback or a license version without an installer file fails closed with
        /// <see cref="LegacyCleanupRequiredException"/>; a "no" gives <see cref="LegacyDeclinedException"/>.
        /// </summary>
        public static LegacyMove Prepare(string projectRoot, IList<LegacyProbe> probes, bool userInitiated, Func<LegacyPlan, bool> confirm, DateTime now, out LegacyHandoffRequest handoff)
        {
            handoff = null;
            var plan = Detect(projectRoot, probes);
            if (plan.IsEmpty) return null;
            var handed = plan.HandoffItem();
            if (handed != null)
            {
                var owner = handed.Owner;
                if (owner == null || owner.Installer == null || owner.Zip == null || !userInitiated || confirm == null)
                    throw new LegacyCleanupRequiredException(LegacyInstallText.HandoffUnavailable(LegacyInstallText.Language(), handed.Product, handed.Path, owner != null && owner.Installer != null));
                var shown = new LegacyPlan();
                shown.Items.Add(handed);
                if (!confirm(shown)) throw new LegacyDeclinedException();
                handoff = new LegacyHandoffRequest { Package = owner.Name, Version = owner.Version, Product = handed.Product, Folder = handed.Path, Installer = owner.Installer, Zip = owner.Zip };
                // The other items of the install ride along: staged before the request, installed with the package, one Resolve.
                foreach (var probe in probes)
                    if (!ReferenceEquals(probe, owner) && probe.Name != owner.Name)
                        handoff.Dependencies.Add(new LegacyHandoffDependency { Name = probe.Name, Version = probe.Version, ZipPath = probe.ZipPath, TargetFolder = probe.TargetFolder });
                return null;
            }
            var blocked = plan.BlockedItems();
            if (blocked.Count > 0) throw new LegacyCleanupRequiredException(LegacyInstallText.BlockedBody(LegacyInstallText.Language(), blocked));
            if (!userInitiated || confirm == null)
                throw new LegacyCleanupRequiredException(LegacyInstallText.CleanupRequired(LegacyInstallText.Language(), plan.ProductNames()));
            if (!confirm(plan)) throw new LegacyDeclinedException();
            var main = probes[probes.Count - 1];
            return Move(projectRoot, plan, now, main.Name, main.Version);
        }

        private static string ParentOf(string relative)
        {
            var slash = relative.LastIndexOf('/');
            return slash > 0 ? relative.Substring(0, slash) : "";
        }

        // True when the folder holds at least one asset and every file asset in it (recursive, .meta-backed) has a GUID the
        // incoming package brings. A folder's own .meta does not count as an asset here.
        private static bool IsPure(string root, string relative, ISet<string> guids, Dictionary<string, bool> cache)
        {
            bool known;
            if (cache.TryGetValue(relative, out known)) return known;
            var result = false;
            try
            {
                var full = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
                var any = false;
                var allCollide = true;
                // Every file counts, not only the .meta files: a file without a .meta, or whose .meta GUID the package does not bring, is
                // not proven to be a product file (the user's own file next to the old install), so the folder is not moved as a whole.
                foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    var isMeta = file.EndsWith(".meta", StringComparison.Ordinal);
                    var meta = isMeta ? file : file + ".meta";
                    if (isMeta && Directory.Exists(file.Substring(0, file.Length - 5))) continue;
                    if (!isMeta && !File.Exists(meta)) { allCollide = false; break; }
                    if (!isMeta && string.Equals(System.IO.Path.GetFileName(file), "package.json", StringComparison.OrdinalIgnoreCase)) { allCollide = false; break; }
                    if (!isMeta) { any = true; }
                    var guid = GuidCollisions.GuidOfMeta(meta);
                    if (guid == null || !guids.Contains(guid)) { allCollide = false; break; }
                    if (isMeta) any = true;
                }
                result = any && allCollide;
            }
            catch (Exception)
            {
                result = false;
            }
            cache[relative] = result;
            return result;
        }

        /// <summary>
        /// Moves every item (and its .meta) to Library/OnAirDeck/Backup/legacy-&lt;stamp&gt;/&lt;relative path&gt;. The
        /// backup-manifest.json is written first (state "moving") so a crash can be undone. On a failure everything moved so far goes back.
        /// </summary>
        public static LegacyMove Move(string projectRoot, LegacyPlan plan, DateTime now, string package = "", string version = "")
        {
            var root = Path.GetFullPath(projectRoot);
            var stamp = "legacy-" + now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var folder = Path.Combine(PackageTransaction.BackupRoot(root), stamp);
            for (var n = 2; Directory.Exists(folder); n++) folder = Path.Combine(PackageTransaction.BackupRoot(root), stamp + "-" + n.ToString(CultureInfo.InvariantCulture));
            var move = new LegacyMove { BackupFolder = folder, ProjectRoot = root, Package = package ?? "", Version = version ?? "" };
            try
            {
                foreach (var item in plan.Items)
                {
                    string relative;
                    if (!TryGuard(root, item.Path, out relative)) throw new PackageInstallException("Refusing to move " + item.Path + ".");
                    if (item.HandOff) throw new PackageInstallException("Refusing to move " + item.Path + ": the product's own installer cleans it up.");
                    if (item.Blocked) throw new PackageInstallException("Refusing to move " + item.Path + ": it is not proven to hold only the old install.");
                    move.Planned.Add(relative);
                    move.Items.Add(item);
                }
                Directory.CreateDirectory(folder);
                move.WriteManifest(LegacyMove.StateMoving, now, true);
                for (var i = 0; i < move.Planned.Count; i++)
                {
                    var relative = move.Planned[i];
                    var original = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                    var backup = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    Fault("legacy-move:" + relative);
                    // A product handler may detach its own folder; it must leave it (and its .meta) at the same backup path.
                    var handler = move.Items[i].Handler;
                    var detached = handler != null && handler.Detach(root, move.Items[i], folder);
                    if (!detached) PackageTransaction.MoveEntry(original, backup);
                    move.Entries.Add(new LegacyMove.Moved { Original = original, Backup = backup, Relative = relative });
                    if (!detached && File.Exists(original + ".meta")) PackageTransaction.MoveEntry(original + ".meta", backup + ".meta");
                }
                move.WriteManifest(LegacyMove.StateMoved, DateTime.Now, false);
            }
            catch (Exception e)
            {
                var failed = move.Restore();
                var note = failed.Count > 0 ? "\n" + LegacyInstallText.RestoreIncomplete(LegacyInstallText.Language(), folder) : "";
                throw new PackageInstallException("The old install could not be moved: " + e.Message + note, e);
            }
            return move;
        }

        private static void Fault(string point)
        {
            var hook = FaultHook;
            if (hook != null) hook(point);
        }
    }
}
