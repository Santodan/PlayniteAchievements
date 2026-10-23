using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>One tag as the layout sees it: a square icon, or text of a measured width.</summary>
    public struct TagChipBox
    {
        public TagChipBox(bool isIcon, double textWidth)
        {
            IsIcon = isIcon;
            TextWidth = textWidth;
        }

        public bool IsIcon { get; }

        public double TextWidth { get; }

        public static TagChipBox Icon() => new TagChipBox(true, 0);

        public static TagChipBox Text(double width) => new TagChipBox(false, width);
    }

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
    /// Wrapping and icon sizing for a Tags cell, used by TagChipsPanel: each cell's icons are as
    /// large as its own tags allow, up to a ceiling.
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
        /// True when <paramref name="chips"/> with icons <paramref name="iconSize"/> square fit in
        /// <paramref name="width"/> by <paramref name="height"/>. Text wider than the cell is trimmed
        /// to it, and a line is as tall as its tallest chip.
        /// </summary>
        public static bool Fits(IReadOnlyList<TagChipBox> chips, double iconSize, double textHeight, double width, double height)
        {
            var widths = new double[chips.Count];
            for (var i = 0; i < chips.Count; i++)
            {
                widths[i] = chips[i].IsIcon ? iconSize : Math.Min(chips[i].TextWidth, width);
            }

            if (iconSize > width)
            {
                return false;
            }

            var total = 0.0;
            var lines = BreakLines(widths, width);
            for (var l = 0; l < lines.Count; l++)
            {
                var lineHeight = 0.0;
                for (var i = lines[l].Start; i < lines[l].Start + lines[l].Count; i++)
                {
                    lineHeight = Math.Max(lineHeight, chips[i].IsIcon ? iconSize : textHeight);
                }

                total += (l > 0 ? LineGap : 0) + lineHeight;
            }

            return total <= height;
        }

        /// <summary>
        /// The largest whole-pixel icon size at which <paramref name="chips"/> fit the cell, never
        /// above <paramref name="ceiling"/> nor below <paramref name="floor"/>. Null when the chips
        /// have no icon, so they set no limit.
        /// </summary>
        public static double? LargestIconSize(IReadOnlyList<TagChipBox> chips, double textHeight, double width, double height, double floor, double ceiling = double.PositiveInfinity)
        {
            var hasIcon = false;
            foreach (var chip in chips)
            {
                hasIcon |= chip.IsIcon;
            }

            if (!hasIcon)
            {
                return null;
            }

            // Fits only gets harder as icons grow, so the largest fitting size is a binary search.
            var low = (int)Math.Ceiling(floor);
            var high = (int)Math.Floor(Math.Min(ceiling, Math.Min(width, height)));
            if (high <= low || !Fits(chips, low, textHeight, width, height))
            {
                return low;
            }

            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (Fits(chips, mid, textHeight, width, height))
                {
                    low = mid;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return low;
        }
    }
}
