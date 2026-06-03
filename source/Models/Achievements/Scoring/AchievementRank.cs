using System;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public enum AchievementRank
    {
        Bronze1,
        Bronze2,
        Bronze3,
        Silver1,
        Silver2,
        Silver3,
        Gold1,
        Gold2,
        Gold3,
        Plat1,
        Plat2,
        Plat3,
        Plat
    }

    public static class AchievementRankPresentation
    {
        public static string FormatRank(AchievementRank rank)
        {
            var rankText = rank.ToString();
            if (rankText.StartsWith("Bronze", StringComparison.Ordinal))
            {
                return FormatNumberedTier(
                    GetLocalizedTierName("LOCPlayAch_Trophy_Bronze", "Bronze"),
                    rankText.Substring("Bronze".Length));
            }

            if (rankText.StartsWith("Silver", StringComparison.Ordinal))
            {
                return FormatNumberedTier(
                    GetLocalizedTierName("LOCPlayAch_Trophy_Silver", "Silver"),
                    rankText.Substring("Silver".Length));
            }

            if (rankText.StartsWith("Gold", StringComparison.Ordinal))
            {
                return FormatNumberedTier(
                    GetLocalizedTierName("LOCPlayAch_Trophy_Gold", "Gold"),
                    rankText.Substring("Gold".Length));
            }

            if (rankText.StartsWith("Plat", StringComparison.Ordinal))
            {
                return FormatNumberedTier(
                    GetLocalizedTierName("LOCPlayAch_Trophy_Platinum", "Platinum"),
                    rankText.Substring("Plat".Length));
            }

            return rankText;
        }

        public static string FormatRank(string rank)
        {
            return TryParseRank(rank, out var parsed)
                ? FormatRank(parsed)
                : FormatRank(AchievementRank.Bronze1);
        }

        public static string GetBadgeIconKey(
            AchievementRank rank,
            bool useUniformRarityBadges = false)
        {
            switch (GetRarityTier(rank))
            {
                case RarityTier.UltraRare:
                    return "BadgePlatinumHexagon";
                case RarityTier.Rare:
                    return useUniformRarityBadges ? "BadgeGoldHexagon" : "BadgeGoldPentagon";
                case RarityTier.Uncommon:
                    return useUniformRarityBadges ? "BadgeSilverHexagon" : "BadgeSilverSquare";
                default:
                    return useUniformRarityBadges ? "BadgeBronzeHexagon" : "BadgeBronzeTriangle";
            }
        }

        public static string GetBadgeIconKey(
            string rank,
            bool useUniformRarityBadges = false)
        {
            return TryParseRank(rank, out var parsed)
                ? GetBadgeIconKey(parsed, useUniformRarityBadges)
                : GetBadgeIconKey(AchievementRank.Bronze1, useUniformRarityBadges);
        }

        public static RarityTier GetRarityTier(AchievementRank rank)
        {
            var rankText = rank.ToString();
            if (rankText.StartsWith("Plat", StringComparison.Ordinal))
            {
                return RarityTier.UltraRare;
            }

            if (rankText.StartsWith("Gold", StringComparison.Ordinal))
            {
                return RarityTier.Rare;
            }

            if (rankText.StartsWith("Silver", StringComparison.Ordinal))
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }

        public static RarityTier GetRarityTier(string rank)
        {
            return TryParseRank(rank, out var parsed)
                ? GetRarityTier(parsed)
                : RarityTier.Common;
        }

        private static string FormatNumberedTier(string tierName, string tierNumber)
        {
            return string.IsNullOrWhiteSpace(tierNumber)
                ? tierName
                : $"{tierName} {tierNumber}";
        }

        private static string GetLocalizedTierName(string resourceKey, string fallback)
        {
            var value = ResourceProvider.GetString(resourceKey);
            if (string.IsNullOrWhiteSpace(value))
            {
                return fallback;
            }

            if (value.Length > 4 &&
                value.StartsWith("<!", StringComparison.Ordinal) &&
                value.EndsWith("!>", StringComparison.Ordinal))
            {
                return fallback;
            }

            return value;
        }

        private static bool TryParseRank(string rank, out AchievementRank parsed)
        {
            if (!string.IsNullOrWhiteSpace(rank) &&
                Enum.TryParse(rank.Trim(), ignoreCase: true, out parsed))
            {
                return true;
            }

            parsed = AchievementRank.Bronze1;
            return false;
        }
    }
}
