using System.Windows.Media;
using Playnite.SDK;

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
        public static string ToIconKey(this RarityTier tier) => tier switch
        {
            RarityTier.UltraRare => "BadgePlatinumHexagon",
            RarityTier.Rare => "BadgeGoldPentagon",
            RarityTier.Uncommon => "BadgeSilverSquare",
            _ => "BadgeBronzeTriangle"
        };

        /// <summary>
        /// Gets the rarity brush for this rarity tier.
        /// </summary>
        public static SolidColorBrush ToBrush(this RarityTier tier) => tier switch
        {
            RarityTier.UltraRare => PercentRarityHelper.UltraRareBrush,
            RarityTier.Rare => PercentRarityHelper.RareBrush,
            RarityTier.Uncommon => PercentRarityHelper.UncommonBrush,
            _ => PercentRarityHelper.CommonBrush
        };

        public static string ToDisplayText(this RarityTier tier)
        {
            return tier switch
            {
                RarityTier.UltraRare => ResourceProvider.GetString("LOCPlayAch_Rarity_UltraRare") ?? "Ultra Rare",
                RarityTier.Rare => ResourceProvider.GetString("LOCPlayAch_Rarity_Rare") ?? "Rare",
                RarityTier.Uncommon => ResourceProvider.GetString("LOCPlayAch_Rarity_Uncommon") ?? "Uncommon",
                _ => ResourceProvider.GetString("LOCPlayAch_Rarity_Common") ?? "Common"
            };
        }
    }

    /// <summary>
    /// Helper for determining achievement rarity based on configurable thresholds.
    /// </summary>
    public static class PercentRarityHelper
    {
        // Default thresholds
        private static double _ultraRareThreshold = 5;
        private static double _rareThreshold = 10;
        private static double _uncommonThreshold = 50;

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

        /// <summary>
        /// Configure the rarity thresholds from settings.
        /// </summary>
        public static void Configure(double ultraRare, double rare, double uncommon)
        {
            _ultraRareThreshold = ultraRare;
            _rareThreshold = rare;
            _uncommonThreshold = uncommon;
        }

        public static double UltraRareThreshold => _ultraRareThreshold;
        public static double RareThreshold => _rareThreshold;
        public static double UncommonThreshold => _uncommonThreshold;

        /// <summary>
        /// Gets the rarity tier for a given global unlock percentage.
        /// </summary>
        public static RarityTier GetRarityTier(double globalPercent)
        {
            if (globalPercent <= _ultraRareThreshold) return RarityTier.UltraRare;
            if (globalPercent <= _rareThreshold) return RarityTier.Rare;
            if (globalPercent <= _uncommonThreshold) return RarityTier.Uncommon;
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
