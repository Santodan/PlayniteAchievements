using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Single-value converter that returns a DropShadowEffect with color based on achievement rarity.
    /// Glow parameters: BlurRadius=20, ShadowDepth=0, Opacity=1.0
    /// Colors:
    /// - Ultra Rare: Light blue (#4FC3F7)
    /// - Rare: Gold (#FFD700)
    /// - Uncommon: Silver (#C0C0C0)
    /// - Common: No glow (null)
    /// </summary>
    public class PercentToRarityGlowConverter : IValueConverter
    {
        private static readonly DropShadowEffect UltraRareGlow = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 0,
            Color = Color.FromRgb(0x4F, 0xC3, 0xF7),
            Opacity = 1.0
        };

        private static readonly DropShadowEffect RareGlow = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 0,
            Color = Color.FromRgb(0xFF, 0xD7, 0x00),
            Opacity = 1.0
        };

        private static readonly DropShadowEffect UncommonGlow = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 0,
            Color = Color.FromRgb(0xC0, 0xC0, 0xC0),
            Opacity = 1.0
        };

        static PercentToRarityGlowConverter()
        {
            UltraRareGlow.Freeze();
            RareGlow.Freeze();
            UncommonGlow.Freeze();
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            RarityTier? tier = null;
            if (value is RarityTier rarityTier)
            {
                tier = rarityTier;
            }
            else if (value is double percent)
            {
                tier = PercentRarityHelper.GetRarityTier(percent);
            }

            if (!tier.HasValue)
            {
                return null;
            }

            if (tier.Value == RarityTier.UltraRare)
            {
                return UltraRareGlow;
            }

            if (tier.Value == RarityTier.Rare)
            {
                return RareGlow;
            }

            if (tier.Value == RarityTier.Uncommon)
            {
                return UncommonGlow;
            }

            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
