using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Models.Achievements
{
    public static class RarityTierDisplayExtensions
    {
        public static string ToDisplayText(this RarityTier rarity)
        {
            switch (rarity)
            {
                case RarityTier.UltraRare:
                    return "Ultra Rare";
                case RarityTier.Rare:
                    return "Rare";
                case RarityTier.Uncommon:
                    return "Uncommon";
                default:
                    return "Common";
            }
        }
    }
}
