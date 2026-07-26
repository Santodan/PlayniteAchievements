using System;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Resolves display and sort values from stored percent and stored rarity.
    /// </summary>
    public static class AchievementRarityResolver
    {
        /// <summary>
        /// Display-only rounding option mirrored from PersistedSettings.RoundRarityPercentages at
        /// plugin startup and on settings changes. When true, rarity percentages render as whole
        /// percents and values under 1% render as "&lt;1%". Stored percent values and sort
        /// ordering are unaffected.
        /// </summary>
        public static bool RoundDisplayPercentages { get; set; }

        /// <summary>
        /// Formats a rarity percent for display. Default format is one decimal place; when
        /// RoundDisplayPercentages is enabled, rounds to the nearest whole percent.
        /// </summary>
        public static string FormatPercent(double rawPercent)
        {
            if (!RoundDisplayPercentages)
            {
                return PercentFormatter.Format(rawPercent, 1);
            }

            return FormatRoundedPercent(rawPercent);
        }

        /// <summary>
        /// Formats a rarity percent for compact surfaces that always show whole percents.
        /// When RoundDisplayPercentages is enabled, values under 1% render as "&lt;1%".
        /// </summary>
        public static string FormatWholePercent(double rawPercent)
        {
            if (!RoundDisplayPercentages)
            {
                return PercentFormatter.FormatWhole(rawPercent);
            }

            return FormatRoundedPercent(rawPercent);
        }

        private static string FormatRoundedPercent(double rawPercent)
        {
            if (rawPercent < 1)
            {
                return PercentFormatter.FormatLessThanWhole(1);
            }

            return PercentFormatter.FormatWhole(Math.Round(rawPercent, MidpointRounding.AwayFromZero));
        }

        public static string GetDisplayText(double? rawPercent, RarityTier rarity)
        {
            if (rawPercent.HasValue)
            {
                return FormatPercent(rawPercent.Value);
            }

            return rarity.ToDisplayText();
        }

        public static string GetDetailText(double? rawPercent, RarityTier rarity)
        {
            if (rawPercent.HasValue)
            {
                return $"{FormatPercent(rawPercent.Value)} - {rarity.ToDisplayText()}";
            }

            return rarity.ToDisplayText();
        }

        public static double GetSortValue(double? rawPercent, RarityTier rarity)
        {
            var band = rarity switch
            {
                RarityTier.UltraRare => 0,
                RarityTier.Rare => 1_000_000,
                RarityTier.Uncommon => 2_000_000,
                _ => 3_000_000
            };

            if (rawPercent.HasValue)
            {
                return band + Math.Round(rawPercent.Value * 1000, MidpointRounding.AwayFromZero);
            }

            return band + 999_999;
        }
    }
}
