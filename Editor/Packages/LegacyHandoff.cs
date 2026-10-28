using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OnAirDeck.License;

namespace OnAirDeck.UnityManager
{
    /// <summary>The installer file of a license version (a sibling entry of the version that holds the package zip).</summary>
    internal sealed class LegacyInstallerFile
    {
        public string VersionId = "";
        public string FileName = "";
        /// <summary>Bytes the server lists for the file; the download must have exactly this size.</summary>
        public long Size;

        /// <summary>
        /// The entry of <paramref name="versions"/> with the same version label as <paramref name="wanted"/> whose file name ends with
        /// <paramref name="suffix"/>; null when there is none (the caller fails closed).
        /// </summary>
        /// <summary>
        /// The installer for <paramref name="package"/> when this project is in the legacy case (old folder present, package not in
        /// Packages/); null in every other case, so a normal install never downloads an installer.
        /// </summary>
        public static LegacyInstallerFile ForLegacyCase(string projectRoot, string package, LicenseVersionEntry wanted, LicenseVersionEntry[] versions)
        {
            LegacyInstallerSpec spec;
            if (!OnAirDeckConfig.LegacyInstallers.TryGetValue(package ?? "", out spec)) return null;
            if (!InstallerHandoffHandler.IsLegacyCase(projectRoot, spec, package)) return null;
            return Find(versions, wanted, spec.InstallerSuffix);
        }

        /// <summary>
        /// The downloaded file must be exactly the size the server listed when it lists one (seller_license_versions.file_size is nullable;
        /// 0 means unknown). An empty download never passes. The server gives no hash, so the content checks decide the rest
        /// (installer: managerCleanup true; zip: package.json name and version, full extraction), all before the request is written.
        /// </summary>
        public static bool SizeMatches(long expected, long actual) { return actual > 0 && (expected <= 0 || expected == actual); }

        /// <summary>The package zip entry itself (the version the user chose).</summary>
        public static LegacyInstallerFile OfVersion(LicenseVersionEntry version)
        {
            if (version == null || string.IsNullOrEmpty(version.version_id)) return null;
            return new LegacyInstallerFile { VersionId = version.version_id, FileName = version.file_name ?? "", Size = version.file_size };
        }

        public static LegacyInstallerFile Find(LicenseVersionEntry[] versions, LicenseVersionEntry wanted, string suffix)
        {
            if (versions == null || wanted == null || string.IsNullOrEmpty(suffix)) return null;
            foreach (var entry in versions)
            {
                if (entry == null || string.IsNullOrEmpty(entry.version_id) || string.IsNullOrEmpty(entry.file_name)) continue;
                if (entry.version_id == wanted.version_id) continue;
                if (!string.Equals(entry.version_label ?? "", wanted.version_label ?? "", StringComparison.Ordinal)) continue;
                if (!entry.file_name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                return new LegacyInstallerFile { VersionId = entry.version_id, FileName = entry.file_name, Size = entry.file_size };
            }
            return null;
        }
    }

    /// <summary>The user said yes: import the product's installer and wait. Nothing under the old folder is touched by the Manager.</summary>
    internal sealed class LegacyHandoffRequest
    {
        public string Package = "";
        public string Version = "";
        public string Product = "";
        public string Folder = "";
        public LegacyInstallerFile Installer;
        /// <summary>The package zip the installer is given (see <see cref="MingToonInstallerHandoff"/>).</summary>
        public LegacyInstallerFile Zip;
        /// <summary>The unmet dependencies of the install (already downloaded); staged with the package and installed together with it.</summary>
        public readonly List<LegacyHandoffDependency> Dependencies = new List<LegacyHandoffDependency>();
    }

    /// <summary>A dependency zip that goes into Packages/ with the hand-off's package (downloaded and checked by the install service).</summary>
    internal sealed class LegacyHandoffDependency
    {
        public string Name = "";
        public string Version = "";
        public string ZipPath = "";
        /// <summary>Absolute folder the package installs into; "" means Packages/&lt;Name&gt;.</summary>
        public string TargetFolder = "";
    }

    internal enum HandoffKind { None, Pending, Done, Stopped }

    internal sealed class HandoffStatus
    {
        public HandoffKind Kind;
        /// <summary>For Stopped: the installer's failure text, or why the wait ended ("restored", "no progress", "no journal").</summary>
        public string Reason = "";
        /// <summary>The newest journal phase seen ("" when there is none yet).</summary>
        public string Phase = "";

        public static HandoffStatus Of(HandoffKind kind, string reason, string phase)
        {
            return new HandoffStatus { Kind = kind, Reason = reason ?? "", Phase = phase ?? "" };
        }
    }

    /// <summary>A hand-off that must not be disturbed: no import, install, refresh or resolve until it ends.</summary>
    internal sealed class LegacyHandoffPendingException : PackageInstallException
    {
        public LegacyHandoffPendingException(string message) : base(message) { }
    }

    /// <summary>
    /// The old Assets install of a product is cleaned up by the product's own installer (OnAirDeckConfig.LegacyInstallers), and the Manager
    /// installs the package. The Manager writes a request file, imports the installer, then only watches (and installs the staged zip when
    /// the installer says the old files are gone). The installer compiles, reloads, deletes, and answers in a result file that is matched
    /// by the request's createdUtc (<see cref="MingToonLegacyCleanup"/>). The wait state is a small file under Library/OnAirDeck, so it
    /// survives domain reloads.
    /// <para>Done: the result for this request says "Complete" and the package is resolved. Stopped: the result says "Failed" (reason = its
    /// failure text); the Manager's own install failed; no matching result <see cref="NoProgressLimit"/> after the import finished while the
    /// editor is idle ("unsupported installer": nothing was written by the installer, nothing was deleted); or no new result
    /// <see cref="NoProgressLimit"/> after the Manager's install while idle ("needs check", with the last phase). Pending otherwise; the
    /// phase "LegacyRemovedForManager" is the "installing" stretch, not an error.</para>
    /// </summary>
    internal static class LegacyHandoff
    {
        public const string ReasonUnsupported = "unsupported installer";
        public const string ReasonNeedsCheck = "needs check";
        public const string ReasonNoProgress = "no progress";
        public static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(120);

        private const string StateFolder = "Library/OnAirDeck";
        private const string StatePrefix = "handoff-";

        private static string ProjectRoot { get { return Path.GetDirectoryName(UnityEngine.Application.dataPath); } }

        // ---------- state file ----------

        private sealed class State
        {
            public string Package = "";
            public string Version = "";
            public string CreatedUtc = "";
            /// <summary>InstallMoved: every staged folder is in Packages/. InstallStarted: Client.Resolve was requested (or the install failed, see Error).</summary>
            public long Started, ImportQueued, ImportDone, InstallMoved, InstallStarted, LastProgress;
            public string LastKey = "";
            public string Error = "";
        }

        private static string StatePath(string root, string package)
        {
            return Path.Combine(root, StateFolder.Replace('/', Path.DirectorySeparatorChar), StatePrefix + PathSafety.SafeFolderName(package, "package") + ".json");
        }

        private static State ReadState(string root, string package)
        {
            try
            {
                var path = StatePath(root, package);
                if (!File.Exists(path)) return null;
                var fields = MiniJson.ParseObject(File.ReadAllText(path));
                if (fields == null) return null;
                var state = new State
                {
                    Package = MiniJson.GetString(fields, "package") ?? package,
                    Version = MiniJson.GetString(fields, "version") ?? "",
                    CreatedUtc = MiniJson.GetString(fields, "createdUtc") ?? "",
                    LastKey = MiniJson.GetString(fields, "lastKey") ?? "",
                    Error = MiniJson.GetString(fields, "error") ?? ""
                };
                MiniJson.TryGetInt(fields, "started", out state.Started);
                MiniJson.TryGetInt(fields, "importQueued", out state.ImportQueued);
                MiniJson.TryGetInt(fields, "importDone", out state.ImportDone);
                MiniJson.TryGetInt(fields, "installMoved", out state.InstallMoved);
                MiniJson.TryGetInt(fields, "installStarted", out state.InstallStarted);
                MiniJson.TryGetInt(fields, "lastProgress", out state.LastProgress);
                return state;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // A state that cannot be written is an error, never a silent loss: the installer deletes without a backup, so the Manager must be
        // able to remember what it started. Written through a temp file and an atomic replace (a crash keeps the old file whole).
        private static void WriteState(string root, State state)
        {
            try
            {
                var map = new Dictionary<string, object>();
                map["format"] = 2L;
                map["package"] = state.Package;
                map["version"] = state.Version;
                map["createdUtc"] = state.CreatedUtc;
                map["started"] = state.Started;
                map["importQueued"] = state.ImportQueued;
                map["importDone"] = state.ImportDone;
                map["installMoved"] = state.InstallMoved;
                map["installStarted"] = state.InstallStarted;
                map["lastProgress"] = state.LastProgress;
                map["lastKey"] = state.LastKey;
                map["error"] = state.Error;
                AtomicFile.Write(StatePath(root, state.Package), JsonWriter.Write(map, "\n"));
                ClearWriteFailure(state.Package);
            }
            catch (Exception e)
            {
                throw new PackageInstallException("The hand-off state of " + state.Package + " could not be saved: " + e.Message, e);
            }
        }

        // The poller cannot throw to anyone: a state write that failed there is kept here and shown by Status as Stopped.
        private static readonly Dictionary<string, string> WriteFailures = new Dictionary<string, string>(StringComparer.Ordinal);

        public static void ReportWriteFailure(string package, string message)
        {
            lock (WriteFailures) WriteFailures[package ?? ""] = message ?? "";
        }

        public static bool HasWriteFailure(string package)
        {
            lock (WriteFailures) return WriteFailures.ContainsKey(package ?? "");
        }

        private static void ClearWriteFailure(string package)
        {
            lock (WriteFailures) WriteFailures.Remove(package ?? "");
        }

        /// <summary>
        /// Saves the start of the wait and reads it back; throws when either fails (the caller must not write the request then). Returns the
        /// request's createdUtc, which is kept in the state file and must be written into manager-request.json as is.
        /// </summary>
        public static string Begin(string root, LegacyHandoffRequest request, DateTime nowUtc)
        {
            var created = MingToonLegacyCleanup.CreatedUtc(nowUtc);
            WriteState(root, new State { Package = request.Package, Version = request.Version, CreatedUtc = created, Started = nowUtc.Ticks, LastProgress = nowUtc.Ticks });
            var back = ReadState(root, request.Package);
            if (back == null || back.CreatedUtc != created || back.Version != request.Version)
                throw new PackageInstallException("The hand-off state of " + request.Package + " could not be read back. Nothing was imported.");
            return created;
        }

        public static string Begin(LegacyHandoffRequest request, DateTime nowUtc) { return Begin(ProjectRoot, request, nowUtc); }

        /// <summary>
        /// The whole start, in the order that keeps the project safe: the state is saved and verified first, then the installer is marked as
        /// queued, then the request file is written. Any failure removes the state and the request and rethrows; no request exists without a
        /// state that can answer it. The caller imports the installer after this returns.
        /// </summary>
        public static string Start(string root, LegacyInstallerSpec spec, LegacyHandoffRequest request, DateTime nowUtc)
        {
            try
            {
                var created = Begin(root, request, nowUtc);
                MarkImportQueued(root, request.Package, nowUtc);
                MingToonLegacyCleanup.WriteRequest(root, spec, request.Package, request.Version, created);
                return created;
            }
            catch (Exception e)
            {
                try { File.Delete(StatePath(root, request.Package)); } catch (Exception) { }
                MingToonLegacyCleanup.DeleteRequest(root, spec);
                if (e is PackageInstallException) throw;
                throw new PackageInstallException("The hand-off request could not be written: " + e.Message, e);
            }
        }

        public static void MarkImportQueued(string root, string package, DateTime nowUtc)
        {
            var state = ReadState(root, package);
            if (state == null) throw new PackageInstallException("The hand-off state of " + package + " is missing.");
            state.ImportQueued = nowUtc.Ticks;
            state.LastProgress = nowUtc.Ticks;
            WriteState(root, state);
        }

        /// <summary>The installer's import finished (the queue is empty). Starts the "no result" clock.</summary>
        public static void MarkImportDone(string root, string package, DateTime nowUtc)
        {
            var state = ReadState(root, package);
            if (state == null || state.ImportDone > 0) return;   // a missing state surfaces through Status (None), not here
            state.ImportDone = nowUtc.Ticks;
            state.LastProgress = nowUtc.Ticks;
            WriteState(root, state);
        }

        /// <summary>Every staged folder is in Packages/ (the next step is Client.Resolve). A resume after a crash here continues at Resolve.</summary>
        public static void MarkInstallMoved(string root, string package, DateTime nowUtc)
        {
            var state = ReadState(root, package);
            if (state == null) throw new PackageInstallException("The hand-off state of " + package + " is missing.");
            state.InstallMoved = nowUtc.Ticks;
            state.LastProgress = nowUtc.Ticks;
            WriteState(root, state);
        }

        public static bool InstallMoved(string root, string package)
        {
            var state = ReadState(root, package);
            return state != null && state.InstallMoved > 0;
        }

        /// <summary>The Manager's own install (extract, move, resolve) ran; <paramref name="error"/> non-empty means it failed and the hand-off is Stopped.</summary>
        public static void MarkInstallStarted(string root, string package, DateTime nowUtc, string error)
        {
            var state = ReadState(root, package);
            if (state == null) throw new PackageInstallException("The hand-off state of " + package + " is missing.");
            state.InstallStarted = nowUtc.Ticks;
            state.LastProgress = nowUtc.Ticks;
            state.Error = error ?? "";
            WriteState(root, state);
        }

        /// <summary>True when the installer was queued and its import is not known to be finished.</summary>
        public static bool ImportOutstanding(string root, string package)
        {
            var state = ReadState(root, package);
            return state != null && state.ImportQueued > 0 && state.ImportDone == 0;
        }

        /// <summary>True when the installer removed the old install and the Manager has not installed the package yet.</summary>
        public static bool InstallDue(string root, string package)
        {
            var state = ReadState(root, package);
            LegacyInstallerSpec spec;
            if (state == null || state.InstallStarted > 0 || !OnAirDeckConfig.LegacyInstallers.TryGetValue(package, out spec)) return false;
            var result = MingToonLegacyCleanup.ReadResult(root, spec);
            return result != null && result.RequestCreatedUtc == state.CreatedUtc && result.Phase == MingToonLegacyCleanup.PhaseRemoved;
        }

        /// <summary>The version recorded at the start (what the Manager installs).</summary>
        public static string VersionOf(string root, string package)
        {
            var state = ReadState(root, package);
            return state == null ? "" : state.Version;
        }

        /// <summary>Forgets the hand-off: the state, the staged zip and the request (the window consumed Done or Stopped, or the user starts over).</summary>
        public static void Clear(string root, string package)
        {
            try { File.Delete(StatePath(root, package)); } catch (Exception) { }
            ClearWriteFailure(package);
            MingToonLegacyCleanup.Unstage(root, package);
            LegacyInstallerSpec spec;
            if (OnAirDeckConfig.LegacyInstallers.TryGetValue(package, out spec)) MingToonLegacyCleanup.DeleteRequest(root, spec);
        }

        public static void Clear(string package) { Clear(ProjectRoot, package); }

        /// <summary>Packages that have a hand-off state file right now.</summary>
        public static List<string> Packages(string root)
        {
            var result = new List<string>();
            try
            {
                var folder = Path.Combine(root, StateFolder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(folder)) return result;
                foreach (var file in Directory.GetFiles(folder, StatePrefix + "*.json"))
                {
                    var fields = MiniJson.ParseObject(File.ReadAllText(file));
                    var package = fields == null ? null : MiniJson.GetString(fields, "package");
                    if (!string.IsNullOrEmpty(package)) result.Add(package);
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        // ---------- status ----------

        /// <summary>None | Pending | Done | Stopped(reason) for one package. The window polls this; None means no hand-off was started.</summary>
        public static HandoffStatus Status(string packageName)
        {
            return Status(ProjectRoot, packageName, DateTime.UtcNow, false, false);
        }

        /// <param name="editorBusy">The editor is compiling or updating assets: that counts as progress.</param>
        /// <param name="update">Record progress (the poller); false for a read-only look.</param>
        public static HandoffStatus Status(string root, string package, DateTime nowUtc, bool editorBusy, bool update)
        {
            var state = ReadState(root, package);
            LegacyInstallerSpec spec;
            if (state == null || !OnAirDeckConfig.LegacyInstallers.TryGetValue(package, out spec)) return HandoffStatus.Of(HandoffKind.None, "", "");

            var result = MingToonLegacyCleanup.ReadResult(root, spec);
            var matched = result != null && state.CreatedUtc.Length > 0 && result.RequestCreatedUtc == state.CreatedUtc;
            var phase = matched ? result.Phase : "";

            if (state.Error.Length > 0) return HandoffStatus.Of(HandoffKind.Stopped, state.Error, phase);
            string writeFailure;
            lock (WriteFailures) WriteFailures.TryGetValue(package, out writeFailure);
            if (writeFailure != null && !(matched && phase == MingToonLegacyCleanup.PhaseComplete && InstalledPackages.Read(root, package) != null))
                return HandoffStatus.Of(HandoffKind.Stopped, "state: " + writeFailure, phase);
            if (matched && phase == MingToonLegacyCleanup.PhaseFailed)
                return HandoffStatus.Of(HandoffKind.Stopped, result.Failure.Length > 0 ? result.Failure : "The installer reported a failure.", phase);
            if (matched && phase == MingToonLegacyCleanup.PhaseComplete && InstalledPackages.Read(root, package) != null)
                return HandoffStatus.Of(HandoffKind.Done, "", phase);

            var key = matched ? result.AttemptId + "|" + result.UpdatedUtc + "|" + phase : "";
            var now = nowUtc.Ticks;
            var lastProgress = state.LastProgress;
            if (editorBusy || key != state.LastKey)
            {
                lastProgress = now;
                if (update)
                {
                    state.LastProgress = now;
                    state.LastKey = key;
                    try { WriteState(root, state); }
                    catch (Exception e) { ReportWriteFailure(package, e.Message); return HandoffStatus.Of(HandoffKind.Stopped, "state: " + e.Message, phase); }
                }
            }

            if (!editorBusy && now - lastProgress > NoProgressLimit.Ticks)
            {
                if (state.InstallStarted > 0) return HandoffStatus.Of(HandoffKind.Stopped, ReasonNeedsCheck, phase);
                if (state.ImportDone > 0 && !matched) return HandoffStatus.Of(HandoffKind.Stopped, ReasonUnsupported, "");
                return HandoffStatus.Of(HandoffKind.Stopped, ReasonNoProgress, phase);
            }
            return HandoffStatus.Of(HandoffKind.Pending, "", phase);
        }

        /// <summary>True while any hand-off is Pending: the Manager must not import, install, refresh or resolve (its own install step excepted).</summary>
        public static bool IsWaiting(string root)
        {
            foreach (var package in Packages(root))
                if (Status(root, package, DateTime.UtcNow, false, false).Kind == HandoffKind.Pending) return true;
            return false;
        }

        public static bool IsWaiting() { return IsWaiting(ProjectRoot); }

        /// <summary>Throws <see cref="LegacyHandoffPendingException"/> while a hand-off is Pending.</summary>
        public static void ThrowIfWaiting()
        {
            var root = ProjectRoot;
            foreach (var package in Packages(root))
                if (Status(root, package, DateTime.UtcNow, false, false).Kind == HandoffKind.Pending)
                    throw new LegacyHandoffPendingException(LegacyInstallText.HandoffWaiting(LegacyInstallText.Language(), package));
        }
    }
}
