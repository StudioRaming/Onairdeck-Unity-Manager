using System;
using System.Collections.Generic;
using System.IO;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The right pane: problem cards, header, actions, progress and the tabs (description, version history, devices).</summary>
    internal sealed partial class ManagerWindow
    {
        [NonSerialized] private readonly HashSet<string> _unfolded = new HashSet<string>();
        [NonSerialized] private List<PackageMismatch> _mismatches = new List<PackageMismatch>();
        [NonSerialized] private List<PendingRestore> _pendingRestores = new List<PendingRestore>();

        private void RestoreBackup(PendingRestore pending)
        {
            var result = LegacyMove.RestoreFromManifest(pending.ManifestPath);
            SetMessage(result.Message, result.Ok ? MessageType.Info : MessageType.Warning);
            RefreshInstalledState();
        }

        private void DrawDetailPaneWithBack(Rect r, CatalogEntry entry)
        {
            var top = new Rect(r.x, r.y, r.width, 28f);
            var back = new Rect(top.x + SRMetrics.Space8, top.y + 5f, 80f, SRMetrics.ButtonMini);
            if (SRGui.Button(back, new GUIContent(ManagerText.BackList, ManagerText.BackList), SRButtonKind.Ghost, SRButtonSize.Mini))
                Defer(() => { _showDetail = false; });
            DrawDetailPane(new Rect(r.x, top.yMax, r.width, Mathf.Max(0f, r.height - top.height)), entry);
        }

        private void DrawDetailPane(Rect r, CatalogEntry entry)
        {
            if (entry == null)
            {
                var hint = new Rect(r.x + SRMetrics.Space12, r.y + SRMetrics.Space16, r.width - 2f * SRMetrics.Space12, 40f);
                SRGui.Ink(hint, ManagerText.PickProduct, SRStyles.Muted);
                return;
            }

            var pad = ManagerLayout.DetailPad;
            var width = Mathf.Max(60f, r.width - 2f * pad - 16f);
            GUILayout.BeginArea(r);
            _detailScroll = GUILayout.BeginScrollView(_detailScroll);
            GUILayout.Space(pad);
            GUILayout.BeginHorizontal();
            GUILayout.Space(pad);
            GUILayout.BeginVertical();

            DrawProblemCards(entry);
            DrawDetailHeader(entry, width);
            GUILayout.Space(SRMetrics.Space8);
            DrawDetailActions(entry);
            DrawDetailProgress(entry);
            GUILayout.Space(SRMetrics.Space8);

            var tabs = new List<GUIContent> { new GUIContent(ManagerText.TabDesc), new GUIContent(ManagerText.TabVer) };
            if (entry.HasDeviceLimit) tabs.Add(new GUIContent(ManagerText.TabDev));
            _tab = Mathf.Clamp(_tab, 0, tabs.Count - 1);
            _tab = SRGui.Tabs(_tab, tabs.ToArray());
            GUILayout.Space(SRMetrics.Space8);
            if (_tab == 0) DrawDescriptionTab(entry, width);
            else if (_tab == 1) DrawVersionsTab(entry, width);
            else DrawDevicesTab(entry);

            GUILayout.EndVertical();
            GUILayout.Space(pad);
            GUILayout.EndHorizontal();
            GUILayout.Space(pad);
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        // ------------------------------------------------------------ problem cards

        private void DrawProblemCards(CatalogEntry entry)
        {
            foreach (var pending in _pendingRestores)
            {
                if (pending.Package != entry.PackageName) continue;
                var restore = pending;
                if (SRGui.HelpBox(ManagerText.BackupLeft, SRTone.Warning, ManagerText.Restore) && !Busy)
                    Defer(() => RestoreBackup(restore));
                GUILayout.Space(SRMetrics.Space6);
            }

            foreach (var mismatch in _mismatches)
            {
                if (mismatch.Package != entry.PackageName) continue;
                var have = mismatch.Installed.Length == 0
                    ? ManagerText.MisNone.Replace("{dep}", mismatch.Dependency)
                    : ManagerText.MisHave.Replace("{dep}", mismatch.Dependency).Replace("{i}", mismatch.Installed);
                var text = ManagerText.MisBody.Replace("{pkg}", mismatch.Package).Replace("{ver}", mismatch.Version)
                    .Replace("{dep}", mismatch.Dependency).Replace("{req}", mismatch.Required).Replace("{have}", have);
                var current = mismatch;
                if (SRGui.HelpBox(text, SRTone.Warning, ManagerText.PMisB) && !Busy) Defer(() => InstallMatching(current));
                GUILayout.Space(SRMetrics.Space6);
            }

            string dependency;
            if (_depProblem.TryGetValue(entry.Key, out dependency))
            {
                var text = dependency.Length > 0 ? ManagerText.DepBody.Replace("{p}", dependency) : ManagerText.PDepB;
                if (SRGui.HelpBox(text, SRTone.Warning, ManagerText.PDepB)) OpenWeb(OnAirDeckConfig.WebsiteUrl);
                GUILayout.Space(SRMetrics.Space6);
            }

            DrawHandoffCard(entry);
            DrawRenewalCard(entry);

            if (_cleanupRequired.Contains(entry.Key))
            {
                var version = NewestPackage(entry);
                if (SRGui.HelpBox(ManagerText.CleanupNeeded, SRTone.Warning, ManagerText.BInst) && version != null && !Busy)
                    Defer(() => DoInstall(entry, version));
                GUILayout.Space(SRMetrics.Space6);
            }

            string folder;
            if (_doneFolder.TryGetValue(entry.Key, out folder))
            {
                var name = Path.GetFileName(folder.TrimEnd('/', '\\'));
                if (SRGui.HelpBox(ManagerText.DoneFolder + name + ManagerText.DoneFolderEnd, SRTone.Success, ManagerText.OpenFolder)) OpenEntryFolder(entry);
                GUILayout.Space(SRMetrics.Space6);
            }
        }

        // ------------------------------------------------------------ header

        private static SRTone StateTone(EntryState state)
        {
            switch (state)
            {
                case EntryState.New: return SRTone.Warning;
                case EntryState.Got:
                case EntryState.Installed:
                case EntryState.Latest: return SRTone.Success;
                default: return SRTone.Neutral;
            }
        }

        private static string StateChip(EntryState state)
        {
            switch (state)
            {
                case EntryState.New: return ManagerText.ChipNew;
                case EntryState.Got: return ManagerText.ChipGot;
                case EntryState.Installed: return ManagerText.ChipInst;
                case EntryState.Latest: return ManagerText.ChipLatest;
                default: return ManagerText.ChipNone;
            }
        }

        private static string StatusLine(CatalogEntry e)
        {
            var latest = e.LatestVersion ?? "";
            if (e.HasPackageVersions)
            {
                switch (e.State)
                {
                    case EntryState.None: return ManagerText.StPkgNot.Replace("{l}", latest);
                    case EntryState.New: return ManagerText.StNew.Replace("{i}", e.InstalledVersion).Replace("{l}", latest);
                    case EntryState.Installed: return ManagerText.StInst.Replace("{v}", e.InstalledVersion);
                    default: return ManagerText.StLatest.Replace("{v}", e.InstalledVersion);
                }
            }
            if (e.Kind == FavoriteKind.Purchase)
            {
                switch (e.State)
                {
                    case EntryState.None: return ManagerText.StNone;
                    case EntryState.New: return ManagerText.StNewFile.Replace("{l}", latest);
                    default: return ManagerText.StGot.Replace("{v}", latest).Replace("{d}", e.BoughtDate);
                }
            }
            return "";
        }

        private void DrawDetailHeader(CatalogEntry entry, float width)
        {
            GUILayout.BeginHorizontal();
            var thumb = GUILayoutUtility.GetRect(ManagerLayout.DetailThumb, ManagerLayout.DetailThumb, GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(false));
            if (Event.current.type == EventType.Repaint) DrawThumbnailImage(thumb, entry);
            GUILayout.Space(SRMetrics.Space12);

            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(entry.Name, SRStyles.Title, GUILayout.ExpandWidth(true));
            var star = GUILayoutUtility.GetRect(ManagerLayout.Star + 4f, ManagerLayout.Star + 4f, GUILayout.ExpandWidth(false));
            DrawStarButton(new Rect(star.x + 2f, star.y + 2f, ManagerLayout.Star, ManagerLayout.Star), entry);
            GUILayout.EndHorizontal();
            if (entry.SellerName.Length > 0) GUILayout.Label(entry.SellerName, SRStyles.Muted);
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.BeginHorizontal();
            if (entry.HasPackageVersions || entry.Kind == FavoriteKind.Purchase) SRGui.BadgeLayout(StateChip(entry.State), StateTone(entry.State));
            if (entry.IsOwn) SRGui.BadgeLayout(ManagerText.OwnChip, SRTone.Brand);
            if (entry.HasPackageVersions && entry.PackageName == _focusPackage) SRGui.BadgeLayout(ManagerText.Requested, SRTone.Brand);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            var status = StatusLine(entry);
            if (status.Length > 0) GUILayout.Label(status, SRStyles.MutedWrap);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
        }

        // ------------------------------------------------------------ actions and progress

        private void DrawDetailActions(CatalogEntry entry)
        {
            var busy = Busy;
            var tip = busy ? ManagerText.WaitTip : "";
            GUILayout.BeginHorizontal();
            string primary = null;
            var primaryKind = SRButtonKind.Primary;
            if (entry.Kind == FavoriteKind.Purchase)
            {
                if (entry.Purchase != null && entry.Purchase.CanDownload)
                {
                    primary = entry.State == EntryState.Got ? ManagerText.BAgain : ManagerText.BGet;
                    primaryKind = entry.State == EntryState.Got ? SRButtonKind.Secondary : SRButtonKind.Primary;
                }
            }
            else if (entry.HasPackageVersions)
            {
                switch (entry.State)
                {
                    case EntryState.None: primary = ManagerText.BInst; break;
                    case EntryState.New: primary = ManagerText.BUpd; break;
                    case EntryState.Installed: primary = ManagerText.BAgain; primaryKind = SRButtonKind.Secondary; break;
                }
            }

            if (primary != null)
            {
                using (new GUIEnabledScope(!busy))
                {
                    if (SRGui.Button(new GUIContent(primary, tip), primaryKind, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                        Defer(() => PrimaryAction(entry));
                }
            }
            if (primary == null && entry.IsLicense && !entry.HasPackageVersions && NewestFileVersion(entry) != null)
            {
                using (new GUIEnabledScope(!busy))
                {
                    if (SRGui.Button(new GUIContent(ManagerText.BInst, tip), SRButtonKind.Primary, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                        Defer(() => PrimaryAction(entry));
                }
            }
            if (entry.IsLicense && !entry.HasPackageVersions && !string.IsNullOrEmpty(entry.LicenseId))
            {
                using (new GUIEnabledScope(!busy))
                {
                    if (SRGui.Button(new GUIContent(ManagerText.ReissueLicense, tip), SRButtonKind.Secondary, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                        Defer(() => ReissueLicense(entry));
                }
            }
            if (entry.State != EntryState.None && HasFolder(entry))
            {
                if (SRGui.Button(new GUIContent(ManagerText.OpenFolder), SRButtonKind.Secondary, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                    OpenEntryFolder(entry);
            }
            if (SRGui.Button(new GUIContent(ManagerText.ViewAt), SRButtonKind.Ghost, SRButtonSize.Large, GUILayout.ExpandWidth(false)))
                OpenAtelier(entry);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }

        private bool HasFolder(CatalogEntry entry)
        {
            return entry.HasPackageVersions || entry.Kind == FavoriteKind.Purchase;
        }

        private void DrawDetailProgress(CatalogEntry entry)
        {
            if (_workKey == entry.Key)
            {
                GUILayout.Space(SRMetrics.Space8);
                SRGui.ProgressBar(_progress, _progressLabel, SRTone.Info);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (SRGui.Button(new GUIContent(ManagerText.Cancel), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                    Defer(CancelWork);
                GUILayout.EndHorizontal();
            }
            else if (Busy)
            {
                GUILayout.Space(SRMetrics.Space4);
                GUILayout.Label(ManagerText.WaitOthers, SRStyles.MutedWrap);
            }
        }

        // ------------------------------------------------------------ tabs

        /// <summary>Text folded to a number of lines with a "more" button. Clipped inside a group because labels overflow by default.</summary>
        private void FoldedText(string id, string text, int lines, float width)
        {
            var style = SRStyles.Wrap;
            var full = style.CalcHeight(new GUIContent(text), width);
            var lineHeight = style.CalcHeight(new GUIContent("A"), 10000f);
            var folded = lineHeight * lines;
            if (_unfolded.Contains(id) || full <= folded + 1f)
            {
                GUILayout.Label(text, style);
                return;
            }
            var rect = GUILayoutUtility.GetRect(0f, folded, GUILayout.ExpandWidth(true));
            GUI.BeginGroup(rect);
            GUI.Label(new Rect(0f, 0f, rect.width, full), text, style);
            GUI.EndGroup();
            if (SRGui.Button(new GUIContent(ManagerText.More), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                Defer(() => _unfolded.Add(id));
        }

        private void DrawDescriptionTab(CatalogEntry entry, float width)
        {
            if (entry.Description.Length > 0)
            {
                FoldedText("d|" + entry.Key, entry.Description, ManagerLayout.DescriptionLines, width);
                GUILayout.Space(SRMetrics.Space12);
            }
            InfoRow(ManagerText.InfoSeller, entry.IsOwn ? ManagerText.InfoMine : entry.SellerName);
            if (entry.BoughtDate.Length > 0 && !entry.IsOwn) InfoRow(ManagerText.InfoBought, entry.BoughtDate);
            InfoRow(ManagerText.InfoKind, entry.HasPackageVersions ? ManagerText.InfoKindPkg : ManagerText.InfoKindFile);
            if (entry.Versions.Count > 0)
            {
                InfoRow(ManagerText.InfoLatestVersion, entry.LatestVersion);
                if (entry.Versions[0].Date.Length > 0) InfoRow(ManagerText.InfoLatest, entry.Versions[0].Date);
            }
        }

        private static void InfoRow(string label, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, SRStyles.Muted, GUILayout.Width(SRMetrics.LabelMin));
            GUILayout.Label(value, SRStyles.Body, GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
        }

        private void DrawVersionsTab(CatalogEntry entry, float width)
        {
            if (entry.Versions.Count == 0)
            {
                GUILayout.Label(ManagerText.NoVersions, SRStyles.Muted);
                return;
            }
            for (var i = 0; i < entry.Versions.Count; i++)
            {
                var row = entry.Versions[i];
                SRGui.BeginCard();
                GUILayout.BeginHorizontal();
                GUILayout.Label(row.Label, SRStyles.Strong, GUILayout.ExpandWidth(false));
                if (row.Installed) SRGui.BadgeLayout(ManagerText.ChipInst, SRTone.Success);
                GUILayout.Label(row.Date, SRStyles.Muted, GUILayout.ExpandWidth(true));
                if (row.License != null && !row.Installed)
                {
                    using (new GUIEnabledScope(!Busy))
                    {
                        if (SRGui.Button(new GUIContent(ManagerText.VerInstall, Busy ? ManagerText.WaitTip : ""), SRButtonKind.Secondary, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                        {
                            var version = row.License;
                            if (version.IsPackage) Defer(() => DoInstall(entry, version));
                            else Defer(() => DoInstallFile(entry, version));
                        }
                    }
                }
                GUILayout.EndHorizontal();
                if (!string.IsNullOrEmpty(row.Notes))
                {
                    var id = "n|" + entry.Key + "|" + row.Label;
                    if (i == 0 || _unfolded.Contains(id + "|open"))
                    {
                        FoldedText(id, row.Notes, ManagerLayout.NoteLines, width - 2f * SRMetrics.Space12);
                    }
                    else if (SRGui.Button(new GUIContent(ManagerText.RelNotes), SRButtonKind.Ghost, SRButtonSize.Mini, GUILayout.ExpandWidth(false)))
                    {
                        Defer(() => _unfolded.Add(id + "|open"));
                    }
                }
                SRGui.EndCard();
                GUILayout.Space(SRMetrics.Space4);
            }
        }
    }
}
