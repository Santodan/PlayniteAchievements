using System;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    public enum AchievementRank
    {
        Bronze5,
        Bronze4,
        Bronze3,
        Bronze2,
        Bronze1,
        Silver5,
        Silver4,
        Silver3,
        Silver2,
        Silver1,
        Gold5,
        Gold4,
        Gold3,
        Gold2,
        Gold1,
        Plat5,
        Plat4,
        Plat3,
        Plat2,
        Plat1,
        Master5,
        Master4,
        Master3,
        Master2,
        Master1
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

            if (rankText.StartsWith("Master", StringComparison.Ordinal))
            {
                return FormatNumberedTier("Master", rankText.Substring("Master".Length));
            }

            return rankText;
        }

        public static string FormatRank(string rank)
        {
            return TryParseRank(rank, out var parsed)
                ? FormatRank(parsed)
                : FormatRank(AchievementRank.Bronze5);
        }

        public static string GetBadgeIconKey(
            AchievementRank rank,
            bool useUniformRarityBadges = false)
        {
            if (rank.ToString().StartsWith("Master", StringComparison.Ordinal))
            {
                return "BadgeCompletedGame";
            }

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
                : GetBadgeIconKey(AchievementRank.Bronze5, useUniformRarityBadges);
        }

        public static RarityTier GetRarityTier(AchievementRank rank)
        {
            var rankText = rank.ToString();
            if (rankText.StartsWith("Plat", StringComparison.Ordinal) ||
                rankText.StartsWith("Master", StringComparison.Ordinal))
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
            if (int.TryParse(tierNumber, out var parsedNumber))
            {
                tierNumber = ToRomanNumeral(parsedNumber);
            }

            return string.IsNullOrWhiteSpace(tierNumber)
                ? tierName
                : $"{tierName} {tierNumber}";
        }

        private static string ToRomanNumeral(int value)
        {
            switch (value)
            {
                case 1:
                    return "I";
                case 2:
                    return "II";
                case 3:
                    return "III";
                case 4:
                    return "IV";
                case 5:
                    return "V";
                default:
                    return value.ToString();
            }
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

            parsed = AchievementRank.Bronze5;
            return false;
        }
    }
}
