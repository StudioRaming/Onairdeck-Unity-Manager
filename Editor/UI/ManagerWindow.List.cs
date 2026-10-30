using System;
using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEditor;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The main screen: bars, the two panes (or one), the footer; and the left list with its controls, rows and groups.</summary>
    internal sealed partial class ManagerWindow
    {
        private void DrawMain(Rect body)
        {
            var y = body.y + DrawBars(body);
            var footer = new Rect(body.x, body.yMax - ManagerLayout.FooterHeight, body.width, ManagerLayout.FooterHeight);
            var panes = new Rect(body.x, y, body.width, Mathf.Max(0f, footer.y - y));
            var narrow = IsNarrow(position.width);
            var entry = SelectedEntry();

            if (narrow)
            {
                if (_showDetail && entry != null) DrawDetailPaneWithBack(panes, entry);
                else DrawListPane(panes);
            }
            else
            {
                var list = new Rect(panes.x, panes.y, ManagerLayout.ListPaneWidth, panes.height);
                var detail = new Rect(list.xMax, panes.y, Mathf.Max(0f, panes.width - list.width), panes.height);
                DrawListPane(list);
                if (Event.current.type == EventType.Repaint) SRTheme.Fill(new Rect(list.xMax - 1f, panes.y, 1f, panes.height), SRTheme.P.LineSubtle);
                DrawDetailPane(detail, entry);
            }
            DrawFooter(footer);
        }

        private void DrawFooter(Rect r)
        {
            if (Event.current.type == EventType.Repaint)
            {
                SRTheme.Fill(r, SRTheme.P.SurfaceSection);
                SRTheme.Fill(new Rect(r.x, r.y, r.width, 1f), SRTheme.P.LineSubtle);
            }
            var button = new Rect(r.xMax - SRMetrics.Space8 - 96f, r.y + (r.height - SRMetrics.ButtonMini) * 0.5f, 96f, SRMetrics.ButtonMini);
            var label = new Rect(r.x + SRMetrics.Space8, r.y, Mathf.Max(0f, button.x - r.x - 2f * SRMetrics.Space8), r.height);
            var account = _session != null ? _session.email : "";
            SRGui.Ink(label, SRGui.Fit(ManagerText.LoggedIn + "  " + account, SRStyles.Mini, label.width), SRStyles.Mini);
            if (SRGui.Button(button, new GUIContent(ManagerText.Contact), SRButtonKind.Ghost, SRButtonSize.Mini))
                Defer(OpenAsk);
        }

        // ------------------------------------------------------------ list pane

        private void DrawListPane(Rect r)
        {
            DrawListControls(new Rect(r.x, r.y, r.width, ManagerLayout.ListControlsHeight));

            var batchHeight = BatchBarHeightNow();
            var listRect = new Rect(r.x, r.y + ManagerLayout.ListControlsHeight, r.width,
                Mathf.Max(0f, r.height - ManagerLayout.ListControlsHeight - batchHeight));
            DrawListRows(listRect);
            if (batchHeight > 0f) DrawBatchBar(new Rect(r.x, listRect.yMax, r.width, batchHeight));
        }

        private string[] SortLabels()
        {
            return new[] { ManagerText.SRecent, ManagerText.SName, ManagerText.SSeller, ManagerText.STodo };
        }

        private string[] FilterLabels()
        {
            return new[] { ManagerText.FAll, ManagerText.ChipNew, ManagerText.ChipNone, ManagerText.ChipInst, ManagerText.FFav, ManagerText.FOwn };
        }

        private void DrawListControls(Rect r)
        {
            var inner = new Rect(r.x + SRMetrics.Space8, r.y + SRMetrics.Space6, r.width - 2f * SRMetrics.Space8, r.height - SRMetrics.Space6);
            GUILayout.BeginArea(inner);
            var search = SRGui.SearchField(_search ?? "", ManagerText.Search, GUILayout.ExpandWidth(true));
            GUILayout.Space(SRMetrics.Space4);
            GUILayout.BeginHorizontal();
            var sort = SRGui.Popup(_sort, SortLabels(), GUILayout.ExpandWidth(true));
            var filter = SRGui.Popup(_filter, FilterLabels(), GUILayout.ExpandWidth(true));
            var sellerOptions = new List<string> { ManagerText.SellerShort };
            sellerOptions.AddRange(_sellers);
            var sellerIndex = _sellerFilter.Length == 0 ? 0 : Mathf.Max(0, _sellers.IndexOf(_sellerFilter) + 1);
            var pickedSeller = SRGui.Popup(sellerIndex, sellerOptions.ToArray(), GUILayout.ExpandWidth(true));
            GUILayout.EndHorizontal();
            GUILayout.EndArea();

            if (search != _search || sort != _sort || filter != _filter || pickedSeller != sellerIndex)
            {
                _search = search;
                _sort = sort;
                _filter = filter;
                _sellerFilter = pickedSeller <= 0 || pickedSeller > _sellers.Count ? "" : _sellers[pickedSeller - 1];
                _linesDirty = true;
                Repaint();
            }
        }

        private void DrawListRows(Rect r)
        {
            if (_lines.Count == 0)
            {
                var message = new Rect(r.x + SRMetrics.Space12, r.y + SRMetrics.Space16, r.width - 2f * SRMetrics.Space12, 40f);
                SRGui.Ink(message, ManagerText.NoMatch, SRStyles.Muted);
                return;
            }

            var contentHeight = 0f;
            foreach (var line in _lines) contentHeight += line.Header ? ManagerLayout.GroupRowHeight : ManagerLayout.ListRowHeight;
            var scrollbar = contentHeight > r.height ? 14f : 0f;
            var view = new Rect(0f, 0f, r.width - scrollbar, contentHeight);
            var e = Event.current;

            _listViewport = r;
            _listScroll = GUI.BeginScrollView(r, _listScroll, view);
            var y = 0f;
            var index = 0;
            foreach (var line in _lines)
            {
                if (line.Header)
                {
                    DrawGroupHeader(new Rect(0f, y, view.width, ManagerLayout.GroupRowHeight), line);
                    y += ManagerLayout.GroupRowHeight;
                }
                else
                {
                    var rect = new Rect(0f, y, view.width, ManagerLayout.ListRowHeight);
                    var visible = rect.yMax >= _listScroll.y && rect.y <= _listScroll.y + r.height;
                    DrawListRow(rect, line.Entry, index, visible, r);
                    y += ManagerLayout.ListRowHeight;
                    index++;
                }
            }
            GUI.EndScrollView();
        }

        private void DrawGroupHeader(Rect r, ListLine line)
        {
            var e = Event.current;
            var hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint)
            {
                SRTheme.Fill(r, hover ? SRTheme.P.SurfaceHover : SRTheme.P.SurfaceSection);
            }
            var glyph = new Rect(r.x + SRMetrics.Space8, r.y + (r.height - 10f) * 0.5f, 10f, 10f);
            if (e.type == EventType.Repaint)
                SRGui.Glyph(glyph, line.Collapsed ? SRGlyph.ChevronRight : SRGlyph.ChevronDown, SRTheme.P.TextMuted);
            var text = new Rect(glyph.xMax + SRMetrics.Space6, r.y, r.width - glyph.xMax - SRMetrics.Space8, r.height);
            SRGui.Ink(text, SRGui.Fit(line.Title + "  " + line.Count, SRStyles.Strong, text.width), SRStyles.Strong);
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                e.Use();
                var id = line.GroupId;
                Defer(() => { if (!_collapsed.Remove(id)) _collapsed.Add(id); _linesDirty = true; });
            }
        }

        private void DrawListRow(Rect r, CatalogEntry entry, int index, bool visible, Rect viewport)
        {
            var e = Event.current;
            var selected = entry.Key == _selectedKey || (string.IsNullOrEmpty(_selectedKey) && !IsNarrow(position.width) && _visible.Count > 0 && _visible[0] == entry);
            var hover = r.Contains(e.mousePosition);
            if (e.type == EventType.Repaint) SRGui.RowBackground(r, selected, hover, false);

            var x = r.x + SRMetrics.Space6;
            var centerY = r.y + r.height * 0.5f;

            // checkbox (only for what a batch can handle)
            var batchable = CanBatch(entry);
            if (batchable)
            {
                var box = new Rect(x, centerY - ManagerLayout.Checkbox * 0.5f, ManagerLayout.Checkbox, ManagerLayout.Checkbox);
                var was = _selected.Contains(entry.Key);
                var now = SRGui.Toggle(box, GUIContent.none, was);
                if (now != was)
                {
                    var key = entry.Key;
                    Defer(() => { if (now) _selected.Add(key); else _selected.Remove(key); });
                }
            }
            x += ManagerLayout.Checkbox + SRMetrics.Space6;

            // thumbnail
            var thumb = new Rect(x, centerY - ManagerLayout.ListThumb * 0.5f, ManagerLayout.ListThumb, ManagerLayout.ListThumb);
            if (visible) DrawThumbnail(thumb, entry, true, viewport);
            x = thumb.xMax + SRMetrics.Space8;

            // star at the right edge
            var star = new Rect(r.xMax - SRMetrics.Space6 - ManagerLayout.Star, centerY - ManagerLayout.Star * 0.5f, ManagerLayout.Star, ManagerLayout.Star);
            DrawStarButton(star, entry);

            // chip
            string chipText;
            SRTone chipTone;
            var hasChip = RowChip(entry, out chipText, out chipTone);
            var right = star.x - SRMetrics.Space4;
            if (hasChip)
            {
                var chipWidth = SRGui.BadgeWidth(chipText);
                var chip = new Rect(right - chipWidth, centerY - ManagerLayout.RowChipHeight * 0.5f, chipWidth, ManagerLayout.RowChipHeight);
                if (visible) SRGui.Badge(chip, chipText, chipTone);
                right = chip.x - SRMetrics.Space4;
            }

            var textWidth = Mathf.Max(0f, right - x);
            if (visible)
            {
                var name = new Rect(x, r.y + 3f, textWidth, SRMetrics.Line);
                var seller = new Rect(x, name.yMax - 1f, textWidth, 14f);
                SRGui.Ink(name, SRGui.Fit(entry.Name, SRStyles.Body, textWidth), SRStyles.Body);
                SRGui.Ink(seller, SRGui.Fit(entry.IsOwn ? ManagerText.OwnChip : entry.SellerName, SRStyles.Mini, textWidth), SRStyles.Mini);
            }

            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition) && viewport.Contains(ToWindow(e.mousePosition)))
            {
                e.Use();
                Defer(() => Select(entry, true));
            }
        }

        /// <summary>Position inside the list scroll view to window coordinates.</summary>
        private Vector2 ToWindow(Vector2 inScroll)
        {
            return _listViewport.position + inScroll - _listScroll;
        }

        /// <summary>The short word of a row, or none: work in progress, a new purchase, or the state. "Latest" has no chip.</summary>
        private bool RowChip(CatalogEntry entry, out string text, out SRTone tone)
        {
            text = "";
            tone = SRTone.Neutral;
            var work = WorkState(entry.Key);
            if (work == 1) { text = ManagerText.RcDling; tone = SRTone.Info; return true; }
            if (work == 2) { text = ManagerText.RcWait; tone = SRTone.Neutral; return true; }
            if (PurchaseWatcher.IsUnseen(entry.Key)) { text = ManagerText.RcNewBuy; tone = SRTone.Brand; return true; }
            switch (entry.State)
            {
                case EntryState.New: text = ManagerText.RcNew; tone = SRTone.Warning; return true;
                case EntryState.None: text = ManagerText.RcNone; tone = SRTone.Neutral; return true;
                case EntryState.Got: text = ManagerText.RcGot; tone = SRTone.Success; return true;
                case EntryState.Installed: text = ManagerText.RcInst; tone = SRTone.Success; return true;
                case EntryState.Unknown: text = ManagerText.RcUnknown; tone = SRTone.Info; return true;
                default: return false;
            }
        }

        // ------------------------------------------------------------ thumbnail and star

        /// <summary>Thumbnail from the cache, or a first-letter tile. Registers the hover rect when asked (list rows only).</summary>
        private void DrawThumbnail(Rect r, CatalogEntry entry, bool hoverable, Rect viewport)
        {
            var e = Event.current;
            if (e.type == EventType.Repaint)
            {
                DrawThumbnailImage(r, entry);
                if (hoverable)
                {
                    var window = new Rect(ToWindow(r.position), r.size);
                    var clipped = Rect.MinMaxRect(Mathf.Max(window.xMin, viewport.xMin), Mathf.Max(window.yMin, viewport.yMin),
                        Mathf.Min(window.xMax, viewport.xMax), Mathf.Min(window.yMax, viewport.yMax));
                    if (clipped.width > 0f && clipped.height > 0f) _thumbHits.Add(new ThumbHit { Rect = clipped, Entry = entry });
                }
            }
        }

        private static void DrawThumbnailImage(Rect r, CatalogEntry entry)
        {
            var texture = string.IsNullOrEmpty(entry.ThumbUrl) ? null : ThumbnailCache.TryGet(entry.ThumbUrl);
            if (texture != null)
            {
                SRTheme.Fill(r, SRTheme.P.SurfaceSunken, SRMetrics.RadiusControl);
                GUI.DrawTexture(r, texture, ScaleMode.ScaleAndCrop);
                return;
            }
            SRTheme.Fill(r, SRTheme.P.BrandTint, SRMetrics.RadiusControl);
            SRGui.Ink(r, ManagerCatalog.FirstLetter(entry.Name), SRStyles.Center);
        }

        private void DrawStarButton(Rect r, CatalogEntry entry)
        {
            var favorite = _favorites.Contains(entry.Key);
            var e = Event.current;
            if (e.type == EventType.Repaint)
                DrawStar(r, favorite, favorite ? SRTheme.P.Warning : SRTheme.P.TextMuted);
            var tooltip = favorite ? ManagerText.FavOff : ManagerText.FavOn;
            if (GUI.Button(r, new GUIContent("", tooltip), GUIStyle.none))
            {
                Defer(() =>
                {
                    var userId = _session != null ? _session.userId : "";
                    if (entry.Kind == FavoriteKind.Purchase) FavoritesStore.Toggle(userId, entry.Purchase);
                    else FavoritesStore.Toggle(userId, entry.License);
                });
            }
        }

        /// <summary>A five-point star from convex triangles (filled) or a closed line (outline); the theme has no star glyph.</summary>
        private static void DrawStar(Rect r, bool filled, Color color)
        {
            var center = r.center;
            var outer = Mathf.Min(r.width, r.height) * 0.5f;
            var inner = outer * 0.45f;
            var points = new Vector3[10];
            for (var i = 0; i < 10; i++)
            {
                var angle = Mathf.Deg2Rad * (-90f + i * 36f);
                var radius = i % 2 == 0 ? outer : inner;
                points[i] = new Vector3(center.x + Mathf.Cos(angle) * radius, center.y + Mathf.Sin(angle) * radius, 0f);
            }
            Handles.color = color;
            if (filled)
            {
                var middle = new Vector3(center.x, center.y, 0f);
                for (var i = 0; i < 10; i++)
                    Handles.DrawAAConvexPolygon(middle, points[i], points[(i + 1) % 10]);
            }
            else
            {
                var line = new Vector3[11];
                Array.Copy(points, line, 10);
                line[10] = points[0];
                Handles.DrawAAPolyLine(1.4f, line);
            }
        }
    }
}
