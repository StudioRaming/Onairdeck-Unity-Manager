// Copy of MingPainter-dev Editor/UI/Widgets/FitTextCache.cs (namespace renamed); SRGui.Fit caches its clipping with it.
using System;
using System.Collections.Generic;

namespace OnAirDeck.UnityManager.Theme
{
    /// <summary>
    /// Remembers what <c>SRGui.Fit</c> clipped a text to, per (text, style, look of the style, width). Fitting measures the whole text and, for a
    /// text that is too long, binary-searches the cut with a native measurement and a new string per step: every sub tool row of the brush list
    /// did that on every repaint, so a pack of long imported names cost hundreds of measurements per repaint. The same row text in the same style at
    /// the same width always clips the same way, so the result is computed once. Pure so the offline harness counts the computations.
    /// </summary>
    internal sealed class FitTextCache<TStyle> where TStyle : class
    {
        /// <summary>Entries kept; a full cache starts over, so a long session never grows without bound.</summary>
        public const int MaxEntries = 4096;

        private struct Key : IEquatable<Key>
        {
            public string Text;
            public TStyle Style;
            public int Look;
            public float Width;

            public bool Equals(Key o)
            {
                return ReferenceEquals(Style, o.Style) && Look == o.Look && Width == o.Width && string.Equals(Text, o.Text, StringComparison.Ordinal);
            }

            public override bool Equals(object obj)
            {
                return obj is Key && Equals((Key)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Text.GetHashCode();
                    h = h * 31 + Style.GetHashCode();
                    h = h * 31 + Look;
                    return h * 31 + Width.GetHashCode();
                }
            }
        }

        private readonly Dictionary<Key, string> results = new Dictionary<Key, string>();

        /// <summary>Times <c>compute</c> ran (a cache hit does not count).</summary>
        public int Computes { get; private set; }

        /// <summary>
        /// The fitted text. <paramref name="look"/> stands for every style property that changes a measurement (font, size, weight, padding, screen
        /// scale): a style that changes it is a different entry. Empty text is returned as is.
        /// </summary>
        public string Fit(string text, TStyle style, int look, float width, Func<string, TStyle, float, string> compute)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var key = new Key { Text = text, Style = style, Look = look, Width = width };
            string fitted;
            if (results.TryGetValue(key, out fitted)) return fitted;
            if (results.Count >= MaxEntries) results.Clear();
            Computes++;
            fitted = compute(text, style, width);
            results[key] = fitted;
            return fitted;
        }
    }
}
