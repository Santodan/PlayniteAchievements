using System;
using System.Collections.Generic;
using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>A run of consecutive chips laid out on one line.</summary>
    public struct TagChipLine
    {
        public TagChipLine(int start, int count, double width)
        {
            Start = start;
            Count = count;
            Width = width;
        }

        public int Start { get; }

        public int Count { get; }

        public double Width { get; }
    }

    /// <summary>
    /// Wrapping and overflow for a Tags cell, used by TagChipsPanel: chips wrap onto lines, and
    /// the ones that do not fit the cell give way to a "+N" chip.
    /// </summary>
    public static class TagChipLayout
    {
        /// <summary>Horizontal space between neighboring chips.</summary>
        public const double Gap = 4;

        /// <summary>Vertical space between wrapped lines.</summary>
        public const double LineGap = 2;

        /// <summary>
        /// Breaks chips of the given widths into lines no wider than <paramref name="width"/>. A chip
        /// wider than that takes a line of its own.
        /// </summary>
        public static List<TagChipLine> BreakLines(IReadOnlyList<double> widths, double width)
        {
            var lines = new List<TagChipLine>();
            var start = 0;
            var lineWidth = 0.0;
            for (var i = 0; i < widths.Count; i++)
            {
                var count = i - start;
                var next = count == 0 ? widths[i] : lineWidth + Gap + widths[i];
                if (count > 0 && next > width)
                {
                    lines.Add(new TagChipLine(start, count, lineWidth));
                    start = i;
                    lineWidth = widths[i];
                }
                else
                {
                    lineWidth = next;
                }
            }

            if (widths.Count > start)
            {
                lines.Add(new TagChipLine(start, widths.Count - start, lineWidth));
            }

            return lines;
        }

        /// <summary>
        /// The size chips of the given sizes take when wrapped at <paramref name="width"/>, each
        /// no wider than it, with each line as tall as its tallest chip.
        /// </summary>
        public static Size Extent(IReadOnlyList<Size> chips, double width)
        {
            var widths = new double[chips.Count];
            for (var i = 0; i < chips.Count; i++)
            {
                widths[i] = Math.Min(chips[i].Width, width);
            }

            var extentWidth = 0.0;
            var extentHeight = 0.0;
            var lines = BreakLines(widths, width);
            for (var l = 0; l < lines.Count; l++)
            {
                var lineHeight = 0.0;
                for (var i = lines[l].Start; i < lines[l].Start + lines[l].Count; i++)
                {
                    lineHeight = Math.Max(lineHeight, chips[i].Height);
                }

                extentWidth = Math.Max(extentWidth, lines[l].Width);
                extentHeight += (l > 0 ? LineGap : 0) + lineHeight;
            }

            return new Size(extentWidth, extentHeight);
        }

        /// <summary>
        /// How many leading chips to show in <paramref name="width"/> by <paramref name="height"/>:
        /// all of them when they fit, else the most that fit followed by the "+N" chip that
        /// <paramref name="overflowSize"/> sizes for N hidden chips. None when even "+N" alone
        /// does not fit, which then shows by itself.
        /// </summary>
        public static int VisibleCount(IReadOnlyList<Size> chips, Func<int, Size> overflowSize, double width, double height)
        {
            if (Extent(chips, width).Height <= height)
            {
                return chips.Count;
            }

            var shown = new List<Size>(chips.Count);
            for (var count = chips.Count - 1; count > 0; count--)
            {
                shown.Clear();
                for (var i = 0; i < count; i++)
                {
                    shown.Add(chips[i]);
                }

                shown.Add(overflowSize(chips.Count - count));
                if (Extent(shown, width).Height <= height)
                {
                    return count;
                }
            }

            return 0;
        }
    }
}
