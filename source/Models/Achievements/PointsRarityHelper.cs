using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Provider-specific rarity rules for providers that expose points but no unlock percentage.
    /// </summary>
    public static class PointsRarityHelper
    {
        private const string XboxProviderKey = "Xbox";

        private static int _xboxUltraRareThreshold = 100;
        private static int _xboxRareThreshold = 50;
        private static int _xboxUncommonThreshold = 25;

        public static void Configure(int xboxUltraRareThreshold, int xboxRareThreshold, int xboxUncommonThreshold)
        {
            _xboxUltraRareThreshold = Math.Max(1, xboxUltraRareThreshold);
            _xboxRareThreshold = Math.Max(1, xboxRareThreshold);
            _xboxUncommonThreshold = Math.Max(0, xboxUncommonThreshold);
        }

        public static bool SupportsPointsDerivedRarity(string providerKey)
        {
            return string.Equals(providerKey, XboxProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        public static RarityTier? GetRarityTier(string providerKey, int? points)
        {
            if (!SupportsPointsDerivedRarity(providerKey) || !points.HasValue)
            {
                return null;
            }

            var value = points.Value;
            if (value >= _xboxUltraRareThreshold)
            {
                return RarityTier.UltraRare;
            }

            if (value >= _xboxRareThreshold)
            {
                return RarityTier.Rare;
            }

            if (value >= _xboxUncommonThreshold)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }
    }
}
