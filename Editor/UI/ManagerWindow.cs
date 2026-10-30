using System;
using System.Collections.Generic;
using System.Threading;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>
    /// Window ▸ Atelier Manager: sign in, list the products of the account, download or install them into the project.
    /// Two panes at wide widths (list 320px, detail), one pane below 640px. Drawn with the StudioRaming Editor Theme
    /// (Editor/UI/Theme); sizes the theme has no token for are in <see cref="ManagerLayout"/>, copy is in <see cref="ManagerText"/>.
    /// </summary>
    /// <remarks>
    /// Split by responsibility: core and routing (this file), Session (sign in/out, load), Screens (full-window screens),
    /// List, Detail, Bars, Dialogs, Devices (device manager, limit screen, devices tab), Hover (thumbnail preview),
    /// Batch, Actions (download/install), Feedback (the ask screen), Packages (entry points from other packages).
    /// Click handlers do their work through <see cref="Defer"/>, i.e. at the start of the next layout pass, so the control
    /// list of one OnGUI call never differs between the layout and the repaint event.
    /// </remarks>
    internal sealed partial class ManagerWindow : EditorWindow
    {
        private enum View { Main = 0, Devices = 1, Ask = 2 }

        // Unity serializes an EditorWindow's private fields across script reloads. Everything that describes in-flight work
        // is [NonSerialized]: the awaiting code is gone after a reload, so a restored "busy" flag would leave the window stuck.
        private SavedSession _session;
        private int _view;
        private string _selectedKey = "";
        private int _tab;
        private string _search = "";
        private int _sort;
        private int _filter;
        private string _sellerFilter = "";
        private bool _showDetail;
        private Vector2 _listScroll;
        private Vector2 _detailScroll;

        [NonSerialized] private bool _signingIn;
        [NonSerialized] private bool _signingOut;
        [NonSerialized] private string _signInUrl;
        [NonSerialized] private CancellationTokenSource _signInCancel;
        [NonSerialized] private double _signInStarted;
        [NonSerialized] private double _signInLastTick;
        [NonSerialized] private string _timeoutText;

        [NonSerialized] private PurchaseItem[] _purchases;
        [NonSerialized] private LicenseEntry[] _licenses;
        [NonSerialized] private List<CatalogEntry> _entries;
        [NonSerialized] private List<CatalogEntry> _visible = new List<CatalogEntry>();
        [NonSerialized] private List<ListLine> _lines = new List<ListLine>();
        [NonSerialized] private List<string> _sellers = new List<string>();
        [NonSerialized] private HashSet<string> _favorites = new HashSet<string>();
        [NonSerialized] private HashSet<string> _collapsed = new HashSet<string>();
        [NonSerialized] private HashSet<string> _markers = new HashSet<string>();
        [NonSerialized] private Dictionary<string, InstallRecord> _installRecords = new Dictionary<string, InstallRecord>();
        [NonSerialized] private bool _entriesDirty = true;
        [NonSerialized] private bool _linesDirty = true;
        [NonSerialized] private bool _loading;
        [NonSerialized] private bool _loadFailed;
        [NonSerialized] private string _loadError;
        [NonSerialized] private bool _licensesRefreshed;
        [NonSerialized] private string _pendingLicenseId;

        [NonSerialized] private UpdateState _update;
        [NonSerialized] private UnityEditor.PackageManager.Requests.AddRequest _applyingUpdate;

        private string _message;
        private MessageType _messageType = MessageType.None;

        private readonly Queue<Action> _deferred = new Queue<Action>();

        [MenuItem("Window/Atelier Manager")]
        public static void Open()
        {
            var window = GetWindow<ManagerWindow>();
            window.titleContent = new GUIContent(ManagerText.AppTitle);
            window.minSize = new Vector2(ManagerLayout.WindowMinWidth, ManagerLayout.WindowMinHeight);
            window.Show();
        }

        [InitializeOnLoadMethod]
        private static void RegisterSeams()
        {
            // The API and bridge files (Editor/Api) stay UI-free; the window registers what they call.
            AtelierManagerApi.OpenWindow = Open;
            AtelierManagerApi.CloseWindows = CloseAll;
            AtelierManagerApi.SelectLicense = OpenFor;
            SRLanguage.RegisterWindowType(typeof(ManagerWindow));
        }

        private static void CloseAll()
        {
            foreach (var window in Resources.FindObjectsOfTypeAll<ManagerWindow>())
                if (window != null) window.Close();
        }

        /// <summary>Opens the window and selects the product of this license id (lock-screen request, contract v8).</summary>
        internal static void OpenFor(string licenseId)
        {
            if (string.IsNullOrEmpty(licenseId)) return;
            Open();
            var window = GetWindow<ManagerWindow>();
            window._pendingLicenseId = licenseId;
            window._view = (int)View.Main;
            window.ApplyPendingSelection();
            window.Repaint();
        }

        private void OnEnable()
        {
            titleContent = new GUIContent(ManagerText.AppTitle);
            _session = SessionStore.Load();
            // The runtime license check reads this marker as proof of a signed-in Manager.
            if (_session != null) SessionMarker.Write(_session.userId); else SessionMarker.Clear();
            _licensesRefreshed = false;
            _entriesDirty = true;
            _linesDirty = true;
            wantsMouseMove = true;   // hover states of rows, buttons and the thumbnail preview
            ThumbnailCache.Changed += OnExternalChange;
            FavoritesStore.Changed += OnFavoritesChanged;
            PurchaseWatcher.Changed += OnExternalChange;
            PurchaseWatcher.Refreshed += OnWatcherRefreshed;
            UpdateCheckService.Changed += OnExternalChange;
            DeviceLimitNotice.Changed += OnExternalChange;
            InstallErrorReporter.Changed += OnExternalChange;
            PackageImportQueue.RecordSaved += RefreshInstalledState;
            SRLanguage.Changed += OnLanguageChanged;
            PurchaseWatcher.SetWindowOpen(true);
            OnEnablePackages();
            HookHandoff();
            CheckForUpdate();
        }

        private void OnDisable()
        {
            ThumbnailCache.Changed -= OnExternalChange;
            FavoritesStore.Changed -= OnFavoritesChanged;
            PurchaseWatcher.Changed -= OnExternalChange;
            PurchaseWatcher.Refreshed -= OnWatcherRefreshed;
            UpdateCheckService.Changed -= OnExternalChange;
            DeviceLimitNotice.Changed -= OnExternalChange;
            InstallErrorReporter.Changed -= OnExternalChange;
            PackageImportQueue.RecordSaved -= RefreshInstalledState;
            SRLanguage.Changed -= OnLanguageChanged;
            PurchaseWatcher.SetWindowOpen(false);
            // Closing the window (or a script reload) abandons in-progress work.
            CancelSignIn();
            CancelWork();
            EditorApplication.update -= PollApplyingUpdate;
            EditorApplication.update -= SignInTick;
            UnhookHandoff();
        }

        private void OnFocus()
        {
            // A package folder can change outside the window (VCC, another editor, a rollback).
            RefreshInstalledState();
            PurchaseWatcher.NotifyWindowFocused();
        }

        private void OnExternalChange()
        {
            Repaint();
        }

        private void OnFavoritesChanged()
        {
            _linesDirty = true;
            _entriesDirty = true;
            Repaint();
        }

        private void OnLanguageChanged()
        {
            titleContent = new GUIContent(ManagerText.AppTitle);
            _linesDirty = true;
            Repaint();
        }

        /// <summary>The background watcher fetched a fresh list: show it instead of loading again.</summary>
        private void OnWatcherRefreshed(PurchasesResponse response)
        {
            if (_session == null || response == null || _loading) return;
            ApplyResponse(response);
            Repaint();
        }

        // ------------------------------------------------------------ OnGUI

        private void OnGUI()
        {
            var e = Event.current;
            if (e.type == EventType.Layout)
            {
                RunDeferred();
                RebuildIfNeeded();
            }

            SRGui.WindowBackground(this);
            var tools = SRGui.Header(SRProduct.OnAirDeck, "A", ManagerText.AppTitle, ManagerText.Tagline);
            DrawHeaderTools(tools);

            var top = SRMetrics.Header + SRMetrics.AccentLine;
            var body = new Rect(0f, top, position.width, Mathf.Max(0f, position.height - top));
            BeginHoverFrame();
            DrawBody(body);
            // Drawn last, over everything else, and only while the pointer is over a thumbnail.
            DrawHoverPreview();
            UpdateHover(e);
        }

        private void DrawBody(Rect body)
        {
            var notice = DeviceLimitNotice.Current;
            var blocked = (_update != null && _update.MustUpdate) || (notice != null && notice.Kind == NoticeKind.UpdateRequired);
            var report = blocked ? null : InstallErrorReporter.Current;
            if (report != null)
            {
                // The install error card sits above every screen, so it is seen signed out, in the device manager and in the list.
                var used = DrawReportCard(body, report);
                body = new Rect(body.x, body.y + used, body.width, Mathf.Max(0f, body.height - used));
            }
            DrawScreen(body);
        }

        private void DrawScreen(Rect body)
        {
            var notice = DeviceLimitNotice.Current;
            if ((_update != null && _update.MustUpdate) || (notice != null && notice.Kind == NoticeKind.UpdateRequired))
                DrawRequiredUpdate(body);
            else if (_signingIn) DrawSigningIn(body);
            else if (_session == null)
            {
                if (_timeoutText != null) DrawTimeout(body); else DrawSignedOut(body);
            }
            else if (notice != null && notice.Kind == NoticeKind.DeviceLimit) DrawDeviceLimit(body, notice);
            else if (_view == (int)View.Devices) DrawDeviceManager(body);
            else if (_view == (int)View.Ask) DrawAsk(body);
            else if (_entries == null) { if (_loadFailed) DrawLoadError(body); else DrawLoading(body); }
            else if (_entries.Count == 0 && !_loading) DrawEmpty(body);
            else DrawMain(body);
        }

        // ------------------------------------------------------------ deferred clicks

        /// <summary>Runs <paramref name="action"/> at the start of the next layout pass (never in the middle of a draw).</summary>
        private void Defer(Action action)
        {
            _deferred.Enqueue(action);
            Repaint();
        }

        private void RunDeferred()
        {
            while (_deferred.Count > 0)
            {
                var action = _deferred.Dequeue();
                try { action(); }
                catch (ExitGUIException) { throw; }
                catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Window action failed: " + e.Message); }
            }
        }

        // ------------------------------------------------------------ data

        private void RebuildIfNeeded()
        {
            if (_entriesDirty && (_purchases != null || _licenses != null)) RebuildEntries();
            if (_linesDirty) RebuildLines();
        }

        private void RebuildEntries()
        {
            _entriesDirty = false;
            var root = PackageInstallService.ProjectRoot;
            _entries = ManagerCatalog.Build(_purchases, _licenses, name => InstalledPackages.InstalledVersion(root, name), _markers, _installRecords);
            _sellers = ManagerCatalog.Sellers(_entries);
            if (_sellerFilter.Length > 0 && !_sellers.Contains(_sellerFilter)) _sellerFilter = "";
            _favorites = new HashSet<string>();
            var userId = _session != null ? _session.userId : "";
            foreach (var entry in _entries)
            {
                var favorite = entry.Kind == FavoriteKind.Purchase ? FavoritesStore.IsFavorite(userId, entry.Purchase) : FavoritesStore.IsFavorite(userId, entry.License);
                if (favorite) _favorites.Add(entry.Key);
            }
            _linesDirty = true;
            ApplyPendingSelection();
        }

        private void RebuildLines()
        {
            _linesDirty = false;
            if (_entries == null) { _lines = new List<ListLine>(); _visible = new List<CatalogEntry>(); return; }
            var visible = new List<CatalogEntry>();
            var seller = _sellerFilter;
            foreach (var entry in _entries)
                if (ManagerCatalog.Matches(entry, _search, (FilterMode)_filter, seller, _favorites.Contains(entry.Key))) visible.Add(entry);
            ManagerCatalog.Sort(visible, (SortMode)_sort);
            _visible = visible;
            _lines = ManagerCatalog.Group(visible, (SortMode)_sort, entry => _favorites.Contains(entry.Key), _collapsed,
                ManagerText.FavGroup, ManagerText.AllGroup, ManagerText.OtherSeller);
        }

        private void MarkDirty()
        {
            _entriesDirty = true;
            _linesDirty = true;
            Repaint();
        }

        private void ApplyResponse(PurchasesResponse response)
        {
            _purchases = response.purchases ?? new PurchaseItem[0];
            _licenses = response.licenses ?? new LicenseEntry[0];
            _loadFailed = false;
            RefreshInstalledState();
        }

        /// <summary>Re-reads what this project has installed (package folders and product markers) and re-checks version mismatches.</summary>
        private void RefreshInstalledState()
        {
            var root = PackageInstallService.ProjectRoot;
            try { _markers = ProductMarkers.Scan(root); }
            catch (Exception e) { Debug.LogWarning(OnAirDeckConfig.LogPrefix + "Product marker scan failed: " + e.Message); _markers = new HashSet<string>(); }
            _installRecords = InstallRecords.Load(root);
            _mismatches = FindMismatches(root);
            _pendingRestores = LegacyMove.PendingRestores();
            MarkDirty();
        }

        private CatalogEntry FindEntry(string key)
        {
            if (_entries == null || string.IsNullOrEmpty(key)) return null;
            foreach (var entry in _entries) if (entry.Key == key) return entry;
            return null;
        }

        private CatalogEntry SelectedEntry()
        {
            var entry = FindEntry(_selectedKey);
            if (entry != null) return entry;
            // Wide window: the detail pane always shows something (the first row) so it is never an empty frame.
            return _visible.Count > 0 && !IsNarrow(position.width) ? _visible[0] : null;
        }

        private static bool IsNarrow(float width)
        {
            return width < ManagerLayout.NarrowBelow;
        }

        private void Select(CatalogEntry entry, bool markSeen)
        {
            _selectedKey = entry.Key;
            _tab = 0;
            _detailScroll = Vector2.zero;
            _showDetail = true;
            _view = (int)View.Main;
            if (markSeen && _session != null && PurchaseWatcher.IsUnseen(entry.Key))
                PurchaseWatcher.MarkSeen(_session.userId, new[] { entry.Key });
            Repaint();
        }

        private void ApplyPendingSelection()
        {
            if (_entries == null) return;
            if (!string.IsNullOrEmpty(_pendingLicenseId))
            {
                var id = _pendingLicenseId;
                _pendingLicenseId = null;
                var entry = FindEntry(FavoritesLogic.EntryKey(FavoriteKind.License, id));
                if (entry != null)
                {
                    _search = "";
                    _filter = (int)FilterMode.All;
                    _sellerFilter = "";
                    _linesDirty = true;
                    Select(entry, true);
                }
                else
                {
                    SetMessage(ManagerText.NotOwned, MessageType.Info);
                }
            }
            else if (_pendingFocus && !string.IsNullOrEmpty(_focusPackage))
            {
                _pendingFocus = false;
                foreach (var entry in _entries)
                {
                    if (entry.PackageName != _focusPackage) continue;
                    Select(entry, false);
                    break;
                }
            }
        }

        // ------------------------------------------------------------ messages

        private void SetMessage(string text, MessageType type)
        {
            _message = text;
            _messageType = type;
            _messageReport = null;
            Repaint();
        }

        private static SRTone Tone(MessageType type)
        {
            switch (type)
            {
                case MessageType.Info: return SRTone.Info;
                case MessageType.Warning: return SRTone.Warning;
                case MessageType.Error: return SRTone.Danger;
                default: return SRTone.Neutral;
            }
        }

        /// <summary>True for the refusals the device screens and bars already show (no second message for them).</summary>
        private static bool IsNoticeRefusal(Exception e)
        {
            return e is DeviceLimitException || e is DeviceReleasedException || e is ManagerUpdateRequiredException;
        }
    }
}
