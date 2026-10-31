using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;
// UnityEditor also has an unrelated legacy PackageInfo type; name the Package Manager one explicitly.
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// The project check (U3). It runs on a worker thread when the window opens, when Refresh is clicked, when the account's
    /// package licenses change and after every fix; a bar names what it found and the "Project check" screen lists it with the fixes
    /// (move to backup, reinstall, remove a manifest.json entry) and the clean-ups that can still be undone.
    /// </summary>
    internal sealed partial class ManagerWindow
    {
        private const int ShownConflictPaths = 5;
        private const string CheckCacheKey = "OnAirDeck.UnityManager.ProjectCheck";
        /// <summary>A script reload reuses a check this young instead of reading the whole project again.</summary>
        private const long CheckCacheSeconds = 600;

        [Serializable]
        private sealed class CheckCache
        {
            public long At;
            public string Licenses = "";
            public List<ProjectConflict> Items = new List<ProjectConflict>();
        }

        [NonSerialized] private List<ProjectConflict> _conflicts = new List<ProjectConflict>();
        [NonSerialized] private List<PendingRestore> _checkMoves = new List<PendingRestore>();
        [NonSerialized] private List<ManifestRemoval> _checkRemovals = new List<ManifestRemoval>();
        [NonSerialized] private bool _scanning;
        [NonSerialized] private bool _scanAgain;
        [NonSerialized] private string _scanError;
        [NonSerialized] private string _scannedLicenses;
        [NonSerialized] private CancellationTokenSource _scanCancel;
        private Vector2 _conflictScroll;

        private int CheckItems { get { return _conflicts.Count + _checkMoves.Count + _checkRemovals.Count; } }

        /// <summary>Starts the check on a worker thread; a request while one runs starts one more afterwards.</summary>
        private async void StartConflictScan()
        {
            if (_scanning) { _scanAgain = true; return; }
            _scanning = true;
            _scanError = null;
            var input = new ConflictScanInput
            {
                ProjectRoot = PackageInstallService.ProjectRoot, LicenseOfPackage = LicenseOfPackage(),
                ResolvedCacheFolders = ResolvedCacheFolders(), EditorOs = EditorOs(),
            };
            _scannedLicenses = LicenseKey(input.LicenseOfPackage);
            if (_scanCancel != null) _scanCancel.Dispose();
            _scanCancel = new CancellationTokenSource();
            var cancel = _scanCancel.Token;
            try
            {
                var found = await Task.Run(() => ProjectConflictScan.Run(input, cancel), cancel);
                _conflicts = found;
                SessionState.SetString(CheckCacheKey, JsonUtility.ToJson(new CheckCache { At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Licenses = _scannedLicenses, Items = found }));
                _checkMoves = ProjectConflictFixes.PendingMoves(input.ProjectRoot);
                _checkRemovals = ProjectConflictFixes.PendingRemovals(input.ProjectRoot);
            }
            catch (OperationCanceledException)
            {
                // The window closed or reloaded.
            }
            catch (Exception e)
            {
                _scanError = FeedbackText.Redact(e.Message, FeedbackClient.LocalContext());
                ManagerErrorWatch.OfferCaught("Project check", e);
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "The project check failed: " + _scanError);
            }
            finally
            {
                _scanning = false;
                Repaint();
            }
            if (_scanAgain && !cancel.IsCancellationRequested)
            {
                _scanAgain = false;
                StartConflictScan();
            }
        }

        /// <summary>On window open (also after a script reload): the last check of this editor session when it is young enough, else a new one.</summary>
        private void StartOrReuseConflictScan()
        {
            try
            {
                var cache = JsonUtility.FromJson<CheckCache>(SessionState.GetString(CheckCacheKey, ""));
                if (cache != null && cache.Items != null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - cache.At < CheckCacheSeconds)
                {
                    _conflicts = cache.Items;
                    _scannedLicenses = cache.Licenses ?? "";
                    var root = PackageInstallService.ProjectRoot;
                    _checkMoves = ProjectConflictFixes.PendingMoves(root);
                    _checkRemovals = ProjectConflictFixes.PendingRemovals(root);
                    return;
                }
            }
            catch (Exception)
            {
                // An unreadable cache is no cache.
            }
            StartConflictScan();
        }

        /// <summary>Library/PackageCache folders of the packages Unity resolved for this project (main thread).</summary>
        private static List<string> ResolvedCacheFolders()
        {
            var folders = new List<string>();
#if UNITY_2021_1_OR_NEWER
            try
            {
                foreach (var info in PackageInfo.GetAllRegisteredPackages())
                    if (info != null && !string.IsNullOrEmpty(info.resolvedPath)) folders.Add(info.resolvedPath);
            }
            catch (Exception e)
            {
                Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Could not list the resolved packages: " + e.Message);
            }
#endif
            return folders;
        }

        private static string EditorOs()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor: return "OSX";
                case RuntimePlatform.LinuxEditor: return "Linux";
                default: return "Windows";
            }
        }

        private void StopConflictScan()
        {
            if (_scanCancel != null) _scanCancel.Cancel();
        }

        /// <summary>A new list can make another package the account's (an old copy is told by its license): check again only then.</summary>
        private void ScanIfLicensesChanged()
        {
            if (LicenseKey(LicenseOfPackage()) != _scannedLicenses) StartConflictScan();
        }

        /// <summary>Package name → license id of every package version the account's licenses offer.</summary>
        private Dictionary<string, string> LicenseOfPackage()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (_licenses == null) return map;
            foreach (var license in _licenses)
            {
                if (license == null || license.versions == null) continue;
                foreach (var version in license.versions)
                    if (version != null && version.IsPackage && !string.IsNullOrEmpty(version.package_name) && !map.ContainsKey(version.package_name))
                        map[version.package_name] = license.license_id ?? "";
            }
            return map;
        }

        private static string LicenseKey(Dictionary<string, string> map)
        {
            var keys = new List<string>();
            foreach (var pair in map) keys.Add(pair.Key + "=" + pair.Value);
            keys.Sort(StringComparer.Ordinal);
            return string.Join(";", keys.ToArray());
        }

        // ------------------------------------------------------------ the screen

        private void OpenProjectCheck()
        {
            _view = (int)View.Conflicts;
            StartConflictScan();
        }

        private void DrawConflicts(Rect body)
        {
            BeginScreen(body);
            GUILayout.BeginHorizontal();
            GUILayout.Label(ManagerText.CfTitle, SRStyles.Title, GUILayout.ExpandWidth(true));
            using (new GUIEnabledScope(!_scanning && !Busy))
            {
                if (SRGui.Button(new GUIContent(ManagerText.CfRescan), SRButtonKind.Secondary, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                    Defer(StartConflictScan);
            }
            if (SRGui.Button(new GUIContent(ManagerText.Back), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => { _view = (int)View.Main; });
            GUILayout.EndHorizontal();
            GUILayout.Space(SRMetrics.Space8);

            _conflictScroll = GUILayout.BeginScrollView(_conflictScroll);
            if (_scanning) GUILayout.Label(ManagerText.CfScanning, SRStyles.MutedWrap);
            else if (_scanError != null) SRGui.HelpBox(ManagerText.CfFailed.Replace("{m}", _scanError), SRTone.Danger);
            else if (_conflicts.Count == 0) GUILayout.Label(ManagerText.CfNone, SRStyles.MutedWrap);
            foreach (var conflict in _conflicts)
            {
                DrawConflict(conflict);
                GUILayout.Space(SRMetrics.Space8);
            }
            if (_checkMoves.Count + _checkRemovals.Count > 0)
            {
                GUILayout.Space(SRMetrics.Space8);
                GUILayout.Label(ManagerText.CfUndoTitle, SRStyles.Strong);
                foreach (var move in _checkMoves) DrawUndoMove(move);
                foreach (var removal in _checkRemovals) DrawUndoRemoval(removal);
            }
            GUILayout.EndScrollView();
            EndScreen();
        }

        private void DrawConflict(ProjectConflict conflict)
        {
            string title, text;
            switch (conflict.Kind)
            {
                case ConflictKind.DuplicateGuid:
                    title = ManagerText.CfGuidTitle.Replace("{n}", conflict.Count.ToString());
                    text = ManagerText.CfGuidBody;
                    break;
                case ConflictKind.OldCopyBesidePackage:
                    title = ManagerText.CfOldTitle.Replace("{p}", conflict.Package);
                    text = ManagerText.CfOldBody;
                    break;
                case ConflictKind.DuplicateAssembly:
                    title = ManagerText.CfAsmTitle.Replace("{a}", conflict.Subject);
                    text = ManagerText.CfAsmBody;
                    break;
                default:
                    title = ManagerText.CfManTitle.Replace("{p}", conflict.Package);
                    text = ManagerText.CfManBody.Replace("{v}", conflict.ManifestValue);
                    break;
            }
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.Label(title, SRStyles.Strong);
            GUILayout.Label(text, SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.Label(PathList(conflict.Paths), SRStyles.MutedWrap);
            GUILayout.Space(SRMetrics.Space6);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            var current = conflict;
            if (conflict.Paths.Count > 0 && SRGui.Button(new GUIContent(ManagerText.CfShow), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                Defer(() => Ping(current.Paths));
            using (new GUIEnabledScope(!Busy && !_scanning))
            {
                if (conflict.Kind == ConflictKind.EmbeddedAndManifest)
                {
                    if (SRGui.Button(new GUIContent(ManagerText.CfRemoveEntry), SRButtonKind.Primary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                        Defer(() => RemoveManifestEntry(current));
                }
                else
                {
                    if (conflict.NeedsReinstall && conflict.Package.Length > 0)
                    {
                        var target = ReinstallTarget(conflict.Package);
                        using (new GUIEnabledScope(GUI.enabled && target != null))
                        {
                            if (SRGui.Button(new GUIContent(ManagerText.CfReinstall, target == null ? ManagerText.CfNoLicense : ""), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                                Defer(() => Reinstall(current));
                        }
                    }
                    if (conflict.Movable.Count > 0 && SRGui.Button(new GUIContent(ManagerText.CfMove), SRButtonKind.Primary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                        Defer(() => MoveConflict(current));
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawUndoMove(PendingRestore move)
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(ManagerText.CfUndoMove.Replace("{when}", When(move.Created)).Replace("{list}", string.Join(", ", move.Paths.ToArray())), SRStyles.MutedWrap, GUILayout.ExpandWidth(true));
            var current = move;
            using (new GUIEnabledScope(!Busy && !_scanning))
            {
                if (SRGui.Button(new GUIContent(ManagerText.CfKeep), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(() => { ProjectConflictFixes.KeepMove(current.ManifestPath); StartConflictScan(); });
                if (SRGui.Button(new GUIContent(ManagerText.CfUndo), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(() => UndoMove(current));
            }
            GUILayout.EndHorizontal();
        }

        private void DrawUndoRemoval(ManifestRemoval removal)
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            GUILayout.Label(ManagerText.CfUndoEntry.Replace("{when}", When(removal.Created)).Replace("{p}", removal.Package), SRStyles.MutedWrap, GUILayout.ExpandWidth(true));
            var current = removal;
            using (new GUIEnabledScope(!Busy && !_scanning))
            {
                if (SRGui.Button(new GUIContent(ManagerText.CfKeep), SRButtonKind.Ghost, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(() => { ProjectConflictFixes.KeepManifestRemoval(current); StartConflictScan(); });
                if (SRGui.Button(new GUIContent(ManagerText.CfUndo), SRButtonKind.Secondary, SRButtonSize.Default, GUILayout.ExpandWidth(false)))
                    Defer(() => UndoRemoval(current));
            }
            GUILayout.EndHorizontal();
        }

        private static string PathList(List<string> paths)
        {
            var shown = new List<string>();
            for (var i = 0; i < paths.Count && i < ShownConflictPaths; i++) shown.Add(paths[i]);
            var text = string.Join("\n", shown.ToArray());
            if (paths.Count > ShownConflictPaths) text += "\n" + ManagerText.CfMore.Replace("{n}", (paths.Count - ShownConflictPaths).ToString());
            return text;
        }

        private static string When(string iso)
        {
            DateTime time;
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out time) ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : iso;
        }

        // ------------------------------------------------------------ the fixes

        private void MoveConflict(ProjectConflict conflict)
        {
            if (Busy) return;
            if (!EditorUtility.DisplayDialog(ManagerText.CfTitle, ManagerText.CfMoveAsk.Replace("{list}", PathList(conflict.Movable)), ManagerText.CfMove, ManagerText.Cancel)) return;
            try
            {
                ProjectConflictFixes.MoveToBackup(PackageInstallService.ProjectRoot, conflict, DateTime.Now);
                SetMessage(ManagerText.CfMoved, MessageType.Info);
            }
            catch (ConflictChangedException e)
            {
                SetMessage(e.Message, MessageType.Warning);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Moving a project check item to the backup", e);
            }
            AssetDatabase.Refresh();
            RefreshInstalledState();
            StartConflictScan();
        }

        private void RemoveManifestEntry(ProjectConflict conflict)
        {
            if (Busy) return;
            var ask = ManagerText.CfRemoveAsk.Replace("{p}", conflict.Package).Replace("{v}", conflict.ManifestValue);
            if (!EditorUtility.DisplayDialog(ManagerText.CfTitle, ask, ManagerText.CfRemoveEntry, ManagerText.Cancel)) return;
            try
            {
                ProjectConflictFixes.RemoveManifestEntry(PackageInstallService.ProjectRoot, conflict.Package, conflict.ManifestValue, conflict.EmbeddedFolder, DateTime.Now);
                SetMessage(ManagerText.CfRemoved, MessageType.Info);
                ResolvePackages();
            }
            catch (ConflictChangedException e)
            {
                SetMessage(e.Message, MessageType.Warning);
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Error);
                _messageReport = ManagerErrorWatch.OfferCaught("Removing a manifest.json entry", e);
            }
            StartConflictScan();
        }

        private void UndoMove(PendingRestore move)
        {
            if (Busy) return;
            var result = LegacyMove.RestoreFromManifest(move.ManifestPath, PackageInstallService.ProjectRoot);
            SetMessage(result.Ok ? ManagerText.CfRestored : result.Message, result.Ok ? MessageType.Info : MessageType.Warning);
            AssetDatabase.Refresh();
            RefreshInstalledState();
            StartConflictScan();
        }

        private void UndoRemoval(ManifestRemoval removal)
        {
            if (Busy) return;
            try
            {
                ProjectConflictFixes.RestoreManifestEntry(PackageInstallService.ProjectRoot, removal);
                SetMessage(ManagerText.CfRestored, MessageType.Info);
                ResolvePackages();
            }
            catch (Exception e)
            {
                SetMessage(e.Message, MessageType.Warning);
            }
            StartConflictScan();
        }

        /// <summary>The license entry and package version a reinstall uses: the installed version when the account offers it, else the offered one.</summary>
        private KeyValuePair<CatalogEntry, LicenseVersionEntry>? ReinstallTarget(string package)
        {
            if (_entries == null) return null;
            var installed = InstalledPackages.InstalledVersion(PackageInstallService.ProjectRoot, package);
            foreach (var entry in _entries)
            {
                if (!entry.IsLicense || entry.PackageName != package) continue;
                foreach (var row in entry.Versions)
                    if (row.License != null && row.License.IsPackage && row.License.package_version == installed)
                        return new KeyValuePair<CatalogEntry, LicenseVersionEntry>(entry, row.License);
                var offered = OfferedPackage(entry);
                if (offered != null) return new KeyValuePair<CatalogEntry, LicenseVersionEntry>(entry, offered);
            }
            return null;
        }

        /// <summary>The install path a click would take: its own confirm, the old-copy clean-up (or hand-off) and backup.</summary>
        private async void Reinstall(ProjectConflict conflict)
        {
            var target = ReinstallTarget(conflict.Package);
            if (target == null) return;
            await DoInstallAsync(target.Value.Key, target.Value.Value, false, null);
            StartConflictScan();
        }

        private static void Ping(List<string> paths)
        {
            foreach (var path in paths)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null) continue;
                Selection.activeObject = asset;
                EditorGUIUtility.PingObject(asset);
                return;
            }
        }

        private static void ResolvePackages()
        {
#if UNITY_2021_1_OR_NEWER
            UnityEditor.PackageManager.Client.Resolve();
#endif
            AssetDatabase.Refresh();
        }
    }
}
