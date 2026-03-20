using System;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// Provider-specific rarity rules for providers that expose points but no unlock percentage.
    /// </summary>
    public static class PointsRarityHelper
    {
        private const string XboxProviderKey = "Xbox";
        private const int XboxUltraRareThreshold = 100;
        private const int XboxRareThreshold = 50;
        private const int XboxUncommonThreshold = 25;

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
            if (value >= XboxUltraRareThreshold)
            {
                return RarityTier.UltraRare;
            }

            if (value >= XboxRareThreshold)
            {
                return RarityTier.Rare;
            }

            if (value >= XboxUncommonThreshold)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }
    }
}
