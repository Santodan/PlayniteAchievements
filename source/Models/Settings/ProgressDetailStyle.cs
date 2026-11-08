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
    /// The footer shows the first enabled style, in list order, that a game has data for.
    /// </remarks>
    public static class ProgressDetailOrder
    {
        private static readonly ProgressDetailStyle[] AllStyles =
        {
            ProgressDetailStyle.Rarity,
            ProgressDetailStyle.Trophy,
            ProgressDetailStyle.Points
        };

        /// <summary>Rarity, Trophy, Points, with only Rarity enabled.</summary>
        public static List<ProgressDetailEntry> Default() =>
            AllStyles
                .Select(style => new ProgressDetailEntry { Style = style, Enabled = style == ProgressDetailStyle.Rarity })
                .ToList();

        /// <summary>
        /// Exactly one entry per style: nulls, unknown styles and repeats are dropped (the first
        /// occurrence wins), and missing styles are appended disabled in default order.
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
                        result.Add(entry.Clone());
                    }
                }
            }

            foreach (var style in AllStyles)
            {
                if (seen.Add(style))
                {
                    result.Add(new ProgressDetailEntry { Style = style, Enabled = false });
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

        /// <summary>Whether the trophy style is enabled, which also moves a capstone platinum into the completion spot.</summary>
        public static bool IsTrophyEnabled(IEnumerable<ProgressDetailEntry> entries) =>
            entries != null && entries.Any(entry => entry != null && entry.Enabled && entry.Style == ProgressDetailStyle.Trophy);
    }
}
