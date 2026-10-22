using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Achievements
{
    /// <summary>
    /// One stacked segment of an unlocks-over-time chart: per-day counts (keys are local calendar
    /// days) for a single platform, rarity tier or trophy type, with the title and color its
    /// segments and tooltip row use.
    /// </summary>
    public sealed class TimelineSeriesCounts
    {
        public TimelineSeriesCounts(
            string key,
            string title,
            string colorHex,
            IReadOnlyDictionary<DateTime, int> countsByDate,
            int? order = null)
        {
            Key = key ?? string.Empty;
            Title = title ?? string.Empty;
            ColorHex = colorHex;
            CountsByDate = countsByDate ?? new Dictionary<DateTime, int>();
            Order = order;
        }

        /// <summary>Stable identity across feeds: the effective provider key, rarity tier or trophy type.</summary>
        public string Key { get; }

        /// <summary>
        /// Where the segment stacks among the others, lowest first, for series with a fixed order
        /// (rarity tiers, trophy types); null stacks by total, largest first.
        /// </summary>
        public int? Order { get; }

        public string Title { get; }

        /// <summary>Segment color as #RRGGBB or #AARRGGBB.</summary>
        public string ColorHex { get; }

        public IReadOnlyDictionary<DateTime, int> CountsByDate { get; }
    }
}
