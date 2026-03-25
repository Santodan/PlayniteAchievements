using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Resolves display and sort values from stored percent and stored rarity.
    /// </summary>
    public static class AchievementRarityResolver
    {
        public static string GetDisplayText(double? rawPercent, RarityTier rarity)
        {
            if (rawPercent.HasValue)
            {
                return $"{rawPercent.Value:F1}%";
            }

            return rarity.ToDisplayText();
        }

        public static string GetDetailText(double? rawPercent, RarityTier rarity)
        {
            if (rawPercent.HasValue)
            {
                return $"{rawPercent.Value:F1}% - {rarity.ToDisplayText()}";
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
