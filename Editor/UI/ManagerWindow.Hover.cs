using System.Collections.Generic;
using OnAirDeck.UnityManager.Theme;
using UnityEngine;

namespace OnAirDeck.UnityManager
{
    /// <summary>The 208px preview card shown while the pointer is over a list thumbnail. Drawn last, clamped inside the window.</summary>
    internal sealed partial class ManagerWindow
    {
        private sealed class ThumbHit
        {
            public Rect Rect;   // window coordinates, clipped to the visible list
            public CatalogEntry Entry;
        }

        [System.NonSerialized] private List<ThumbHit> _thumbHits = new List<ThumbHit>();
        [System.NonSerialized] private List<ThumbHit> _lastThumbHits = new List<ThumbHit>();
        [System.NonSerialized] private string _hoverKey = "";
        [System.NonSerialized] private Rect _listViewport;

        /// <summary>Repaint starts a fresh list of the thumbnails that are on screen; the previous one answers mouse moves.</summary>
        private void BeginHoverFrame()
        {
            if (Event.current.type != EventType.Repaint) return;
            _thumbHits = new List<ThumbHit>();
        }

        private ThumbHit HitAt(Vector2 mouse)
        {
            foreach (var hit in _lastThumbHits)
                if (hit.Rect.Contains(mouse)) return hit;
            return null;
        }

        private void UpdateHover(Event e)
        {
            if (e.type == EventType.Repaint)
            {
                _lastThumbHits = _thumbHits;
                // The list moved under a still pointer (scroll, filter): drop a card whose thumbnail is gone.
                if (_hoverKey.Length > 0 && HitAt(e.mousePosition) == null) _hoverKey = "";
                return;
            }
            if (e.type == EventType.MouseLeaveWindow)
            {
                if (_hoverKey.Length > 0) { _hoverKey = ""; Repaint(); }
                return;
            }
            if (e.type != EventType.MouseMove && e.type != EventType.ScrollWheel) return;

            var hit = HitAt(e.mousePosition);
            var key = hit != null ? hit.Entry.Key : "";
            var changed = key != _hoverKey;
            _hoverKey = key;
            // Only while over a thumbnail (or when leaving one): other mouse moves cost nothing.
            if (hit != null || changed) Repaint();
        }

        private void DrawHoverPreview()
        {
            if (Event.current.type != EventType.Repaint || _hoverKey.Length == 0) return;
            ThumbHit hit = null;
            foreach (var candidate in _thumbHits) if (candidate.Entry.Key == _hoverKey) { hit = candidate; break; }
            if (hit == null) return;

            var entry = hit.Entry;
            var width = ManagerLayout.PreviewCard;
            var pad = SRMetrics.Space8;
            var image = width - 2f * pad;
            var height = pad + image + SRMetrics.Space6 + SRMetrics.Line + 14f + pad;

            // To the right of the thumbnail; to the left when there is no room; always inside the window.
            var x = hit.Rect.xMax + SRMetrics.Space8;
            if (x + width > position.width - SRMetrics.Space4) x = hit.Rect.x - SRMetrics.Space8 - width;
            x = Mathf.Clamp(x, SRMetrics.Space4, Mathf.Max(SRMetrics.Space4, position.width - width - SRMetrics.Space4));
            var y = Mathf.Clamp(hit.Rect.center.y - height * 0.5f, SRMetrics.Space4, Mathf.Max(SRMetrics.Space4, position.height - height - SRMetrics.Space4));
            var card = new Rect(x, y, width, height);

            var shadow = SRTheme.P.TextStrong;
            shadow.a = 0.18f;
            SRTheme.Fill(new Rect(card.x + 1f, card.y + 3f, card.width, card.height), shadow, SRMetrics.RadiusCard);
            SRTheme.Box(card, SRTheme.P.SurfaceCard, SRTheme.P.LineStrong, SRMetrics.RadiusCard);

            var imageRect = new Rect(card.x + pad, card.y + pad, image, image);
            DrawThumbnailImage(imageRect, entry);
            var name = new Rect(imageRect.x, imageRect.yMax + SRMetrics.Space6, image, SRMetrics.Line);
            var seller = new Rect(imageRect.x, name.yMax, image, 14f);
            SRGui.Ink(name, SRGui.Fit(entry.Name, SRStyles.Strong, image), SRStyles.Strong);
            SRGui.Ink(seller, SRGui.Fit(entry.IsOwn ? ManagerText.OwnChip : entry.SellerName, SRStyles.Mini, image), SRStyles.Mini);
        }
    }
}
