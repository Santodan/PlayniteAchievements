using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Extension methods for RarityTier enum.
    /// </summary>
    public static class RarityTierExtensions
    {
        /// <summary>
        /// Gets the badge icon resource key for this rarity tier.
        /// </summary>
        public static string ToIconKey(this RarityTier tier, bool useUniformRarityBadges = false) => tier switch
        {
            RarityTier.UltraRare => "BadgePlatinumHexagon",
            RarityTier.Rare => useUniformRarityBadges ? "BadgeGoldHexagon" : "BadgeGoldPentagon",
            RarityTier.Uncommon => useUniformRarityBadges ? "BadgeSilverHexagon" : "BadgeSilverSquare",
            _ => useUniformRarityBadges ? "BadgeBronzeHexagon" : "BadgeBronzeTriangle"
        };

        /// <summary>
        /// Gets the dynamic application resource key for this rarity tier.
        /// </summary>
        public static string ToDynamicIconKey(this RarityTier tier) => tier switch
        {
            RarityTier.UltraRare => "BadgeRarityUltraRare",
            RarityTier.Rare => "BadgeRarityRare",
            RarityTier.Uncommon => "BadgeRarityUncommon",
            _ => "BadgeRarityCommon"
        };

        /// <summary>
        /// Gets the rarity brush for this rarity tier.
        /// </summary>
        public static SolidColorBrush ToBrush(this RarityTier tier) => tier switch
        {
            RarityTier.UltraRare => RarityAppearanceHelper.GetBrush(RarityTier.UltraRare),
            RarityTier.Rare => RarityAppearanceHelper.GetBrush(RarityTier.Rare),
            RarityTier.Uncommon => RarityAppearanceHelper.GetBrush(RarityTier.Uncommon),
            _ => RarityAppearanceHelper.GetBrush(RarityTier.Common)
        };

        public static string ToDisplayText(this RarityTier tier)
        {
            return tier switch
            {
                RarityTier.UltraRare => ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare"),
                RarityTier.Rare => ResourceProvider.GetString("LOCPlayAch_Rarity_Rare"),
                RarityTier.Uncommon => ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon"),
                _ => ResourceProvider.GetString("LOCPlayAch_Rarity_Common")
            };
        }

        public static bool TryParse(string value, out RarityTier tier)
        {
            if (Enum.TryParse(value, true, out tier))
            {
                return true;
            }

            tier = RarityTier.Common;
            return false;
        }
    }

    /// <summary>
    /// Helper for determining achievement rarity based on configurable thresholds.
    /// </summary>
    public static class PercentRarityHelper
    {
        // Fixed thresholds for global unlock percentage rarity.
        private const double UltraRareThresholdValue = 5;
        private const double RareThresholdValue = 20;
        private const double UncommonThresholdValue = 50;

        // Rarity brushes (public for extension method access)
        public static readonly SolidColorBrush CommonBrush = new SolidColorBrush(Color.FromRgb(0x8B, 0xC3, 0x4A));
        public static readonly SolidColorBrush UncommonBrush = new SolidColorBrush(Color.FromRgb(0x03, 0xA9, 0xF4));
        public static readonly SolidColorBrush RareBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x98, 0x00));
        public static readonly SolidColorBrush UltraRareBrush = new SolidColorBrush(Color.FromRgb(0xE9, 0x1E, 0x63));

        static PercentRarityHelper()
        {
            CommonBrush.Freeze();
            UncommonBrush.Freeze();
            RareBrush.Freeze();
            UltraRareBrush.Freeze();
        }

        public static double UltraRareThreshold => UltraRareThresholdValue;
        public static double RareThreshold => RareThresholdValue;
        public static double UncommonThreshold => UncommonThresholdValue;

        public static void ApplyBadgeApplicationResources(bool useUniformRarityBadges)
        {
            var fallback = new PersistedSettings { UseUniformRarityBadges = useUniformRarityBadges };
            RarityAppearanceHelper.ApplyBadgeApplicationResources(fallback);
        }

        /// <summary>
        /// Gets the rarity tier for a given global unlock percentage.
        /// </summary>
        public static RarityTier GetRarityTier(double globalPercent)
        {
            if (globalPercent <= UltraRareThresholdValue) return RarityTier.UltraRare;
            if (globalPercent <= RareThresholdValue) return RarityTier.Rare;
            if (globalPercent <= UncommonThresholdValue) return RarityTier.Uncommon;
            return RarityTier.Common;
        }

    }

    public enum RarityTier
    {
        Common,
        Uncommon,
        Rare,
        UltraRare
    }
}
