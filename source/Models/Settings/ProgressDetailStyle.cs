using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// A kind of detail the progress column footer can show beneath the bar.
    /// </summary>
    public enum ProgressDetailStyle
    {
        /// <summary>Unlocked counts per rarity tier.</summary>
        Rarity = 0,

        /// <summary>Unlocked counts per PlayStation trophy grade.</summary>
        Trophy = 1,

        /// <summary>Earned points of the game's points family, with that family's platform icon.</summary>
        Points = 2
    }

    /// <summary>
    /// One row of the progress details priority list: a style and whether it is enabled.
    /// </summary>
    public sealed class ProgressDetailEntry
    {
        public ProgressDetailStyle Style { get; set; }

        public bool Enabled { get; set; }

        public ProgressDetailEntry Clone() => new ProgressDetailEntry { Style = Style, Enabled = Enabled };
    }

    /// <summary>
    /// Defaults and normalization for the progress details priority list.
    /// </summary>
    /// <remarks>
    /// The footer shows the first enabled styles, in list order, that a game has data for, up to
    /// the persisted shown count.
    /// </remarks>
    public static class ProgressDetailOrder
    {
        private static readonly ProgressDetailStyle[] AllStyles =
        {
            ProgressDetailStyle.Rarity,
            ProgressDetailStyle.Trophy,
            ProgressDetailStyle.Points
        };

        /// <summary>The fewest details a game shows: the first enabled style it has data for.</summary>
        public const int MinShownCount = 1;

        /// <summary>The most details a game shows: one of each style.</summary>
        public static int MaxShownCount => AllStyles.Length;

        /// <summary>Limits a shown count to <see cref="MinShownCount"/> through <see cref="MaxShownCount"/>.</summary>
        public static int ClampShownCount(int count) =>
            count < MinShownCount ? MinShownCount : count > MaxShownCount ? MaxShownCount : count;

        /// <summary>Rarity, Trophy, Points, with only Rarity enabled.</summary>
        public static List<ProgressDetailEntry> Default() =>
            AllStyles
                .Select(style => new ProgressDetailEntry { Style = style, Enabled = style == ProgressDetailStyle.Rarity })
                .ToList();

        /// <summary>
        /// Exactly one entry per style: nulls, unknown styles and repeats are dropped (the first
        /// occurrence wins), and missing styles are appended disabled in default order. Rarity
        /// is always enabled.
        /// </summary>
        public static List<ProgressDetailEntry> Normalize(IEnumerable<ProgressDetailEntry> entries)
        {
            var result = new List<ProgressDetailEntry>(AllStyles.Length);
            var seen = new HashSet<ProgressDetailStyle>();
            if (entries != null)
            {
                foreach (var entry in entries)
                {
                    if (entry != null && AllStyles.Contains(entry.Style) && seen.Add(entry.Style))
                    {
                        var copy = entry.Clone();
                        copy.Enabled |= copy.Style == ProgressDetailStyle.Rarity;
                        result.Add(copy);
                    }
                }
            }

            foreach (var style in AllStyles)
            {
                if (seen.Add(style))
                {
                    result.Add(new ProgressDetailEntry { Style = style, Enabled = style == ProgressDetailStyle.Rarity });
                }
            }

            return result;
        }

        /// <summary>The enabled styles in priority order.</summary>
        public static IReadOnlyList<ProgressDetailStyle> EnabledStyles(IEnumerable<ProgressDetailEntry> entries) =>
            (entries ?? Enumerable.Empty<ProgressDetailEntry>())
                .Where(entry => entry != null && entry.Enabled)
                .Select(entry => entry.Style)
                .ToList();

        /// <summary>
        /// The style an inline detail spot shows: the first of <paramref name="styles"/> for which
        /// <paramref name="hasData"/> is true, or null when there is none.
        /// </summary>
        public static ProgressDetailStyle? PickInline(
            IEnumerable<ProgressDetailStyle> styles,
            System.Func<ProgressDetailStyle, bool> hasData)
        {
            if (styles == null || hasData == null)
            {
                return null;
            }

            foreach (var style in styles)
            {
                if (hasData(style))
                {
                    return style;
                }
            }

            return null;
        }
    }
}
