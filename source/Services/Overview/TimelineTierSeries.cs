using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// Splits an overview snapshot's unlocks into one stacked timeline series per rarity tier or
    /// trophy type, read from its unlocked rows and colored like the Rarity and Trophy pies. The
    /// series keep the pies' order, rarest first.
    /// </summary>
    public static class TimelineTierSeries
    {
        private static readonly RarityTier[] RarityOrder =
        {
            RarityTier.UltraRare,
            RarityTier.Rare,
            RarityTier.Uncommon,
            RarityTier.Common
        };

        private static readonly (string Type, string ColorKey)[] TrophyOrder =
        {
            ("platinum", "TrophyPlatinum"),
            ("gold", "TrophyGold"),
            ("silver", "TrophySilver"),
            ("bronze", "TrophyBronze")
        };

        // The key of the unlocks with no trophy type, which stack last in the platform split's Unknown color.
        private const string NoTrophyKey = "Unknown";

        /// <summary>One series per rarity tier the snapshot's unlocks reach.</summary>
        public static IReadOnlyList<TimelineSeriesCounts> ByRarity(OverviewDataSnapshot snapshot)
        {
            var counts = CountRows(snapshot, row => row.Rarity.ToString());
            return RarityOrder
                .Select((tier, index) => Series(
                    counts,
                    tier.ToString(),
                    tier.ToDisplayText(),
                    RarityAppearanceHelper.GetPieColor(tier).ToString(),
                    index))
                .Where(series => series != null)
                .ToList();
        }

        /// <summary>
        /// One series per trophy type the snapshot's unlocks reach, and one for the unlocks with
        /// no trophy type, so the bars keep their totals.
        /// </summary>
        public static IReadOnlyList<TimelineSeriesCounts> ByTrophy(OverviewDataSnapshot snapshot)
        {
            var counts = CountRows(snapshot, row =>
            {
                var type = (row.TrophyType ?? string.Empty).Trim();
                return TrophyOrder.Any(choice => string.Equals(choice.Type, type, StringComparison.OrdinalIgnoreCase))
                    ? type.ToLowerInvariant()
                    : NoTrophyKey;
            });

            var series = new List<TimelineSeriesCounts>();
            for (var i = 0; i < TrophyOrder.Length; i++)
            {
                var choice = TrophyOrder[i];
                var label = RarityTrophyControlBarFilters.TrophyChoices.First(entry => entry.Type == choice.Type).Key;
                series.Add(Series(
                    counts,
                    choice.Type,
                    ResourceProvider.GetString(label),
                    RarityAppearanceHelper.GetTrophyPieColor(choice.ColorKey).ToString(),
                    i));
            }

            series.Add(Series(
                counts,
                NoTrophyKey,
                ResourceProvider.GetString("LOCPlayAch_Common_Unknown"),
                ProviderRegistry.ResolveProviderVisualsOrFallback(NoTrophyKey).colorHex,
                TrophyOrder.Length));
            return series.Where(item => item != null).ToList();
        }

        // Day counts of the snapshot's unlocked rows, grouped by the key each row maps to.
        private static Dictionary<string, Dictionary<DateTime, int>> CountRows(
            OverviewDataSnapshot snapshot,
            Func<AchievementDisplayItem, string> keyOf)
        {
            var counts = new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);
            var rows = snapshot?.Achievements;
            for (var i = 0; i < (rows?.Count ?? 0); i++)
            {
                var row = rows[i];
                if (row?.Unlocked != true || !row.UnlockTimeUtc.HasValue)
                {
                    continue;
                }

                var key = keyOf(row);
                if (!counts.TryGetValue(key, out var byDay))
                {
                    byDay = new Dictionary<DateTime, int>();
                    counts[key] = byDay;
                }

                var day = UnlockDayCounts.DayOf(row.UnlockTimeUtc.Value);
                byDay[day] = byDay.TryGetValue(day, out var count) ? count + 1 : 1;
            }

            return counts;
        }

        private static TimelineSeriesCounts Series(
            Dictionary<string, Dictionary<DateTime, int>> counts,
            string key,
            string title,
            string colorHex,
            int order) =>
            counts.TryGetValue(key, out var byDay)
                ? new TimelineSeriesCounts(key, title, colorHex, byDay, order)
                : null;
    }
}
