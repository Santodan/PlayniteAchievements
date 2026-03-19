using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Resolves effective achievement rarity from either unlock percentage or provider-specific points.
    /// </summary>
    public static class AchievementRarityResolver
    {
        public static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (value > 0 && value <= 1)
            {
                return value * 100.0;
            }

            return value;
        }

        public static bool HasRarityPercent(double? rawPercent)
        {
            return NormalizePercent(rawPercent).HasValue;
        }

        public static RarityTier? GetRarityTier(string providerKey, double? rawPercent, int? points)
        {
            var percent = NormalizePercent(rawPercent);
            if (percent.HasValue)
            {
                return PercentRarityHelper.GetRarityTier(percent.Value);
            }

            return PointsRarityHelper.GetRarityTier(providerKey, points);
        }

        public static string GetDisplayText(string providerKey, double? rawPercent, int? points)
        {
            var percent = NormalizePercent(rawPercent);
            if (percent.HasValue)
            {
                return $"{percent.Value:F1}%";
            }

            var tier = GetRarityTier(providerKey, rawPercent, points);
            return tier?.ToDisplayText() ?? "-";
        }

        public static string GetDetailText(string providerKey, double? rawPercent, int? points)
        {
            var tier = GetRarityTier(providerKey, rawPercent, points);
            if (!tier.HasValue)
            {
                return "-";
            }

            var percent = NormalizePercent(rawPercent);
            if (percent.HasValue)
            {
                return $"{percent.Value:F1}% - {tier.Value.ToDisplayText()}";
            }

            return tier.Value.ToDisplayText();
        }

        public static double GetSortValue(string providerKey, double? rawPercent, int? points)
        {
            var tier = GetRarityTier(providerKey, rawPercent, points);
            if (!tier.HasValue)
            {
                return 5_000_000;
            }

            var band = tier.Value switch
            {
                RarityTier.UltraRare => 0,
                RarityTier.Rare => 1_000_000,
                RarityTier.Uncommon => 2_000_000,
                _ => 3_000_000
            };

            var percent = NormalizePercent(rawPercent);
            if (percent.HasValue)
            {
                return band + Math.Round(percent.Value * 1000, MidpointRounding.AwayFromZero);
            }

            var pointValue = Math.Max(0, points ?? 0);
            return band + (999_999 - Math.Min(999_999, pointValue));
        }
    }
}
