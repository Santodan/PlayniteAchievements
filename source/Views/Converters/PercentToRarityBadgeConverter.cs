using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Converts achievement percentage to the appropriate rarity badge icon from RarityIcons.xaml.
    /// Uses settings thresholds to determine rarity tier.
    ///
    /// Rarity tiers:
    /// - Ultra Rare (< UltraRareThreshold): BadgePlatinumHexagon
    /// - Rare (< RareThreshold): gold badge
    /// - Uncommon (< UncommonThreshold): silver badge
    /// - Common (>= UncommonThreshold): bronze badge
    /// </summary>
    public class PercentToRarityBadgeConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 4)
                return null;

            // values[0] = GlobalPercentUnlocked (double?)
            // values[1] = UltraRareThreshold (double)
            // values[2] = RareThreshold (double)
            // values[3] = UncommonThreshold (double)

            // Handle nullable double - null means no rarity data, so no badge
            if (values[0] == null)
                return null;

            double? percent = values[0] as double? ?? (values[0] is double d ? d : (double?)null);
            if (percent == null)
                return null;

            if (values[1] is double ultraRareThreshold &&
                values[2] is double rareThreshold &&
                values[3] is double uncommonThreshold)
            {
                RarityTier rarity;

                if (percent.Value < ultraRareThreshold)
                    rarity = RarityTier.UltraRare;
                else if (percent.Value < rareThreshold)
                    rarity = RarityTier.Rare;
                else if (percent.Value < uncommonThreshold)
                    rarity = RarityTier.Uncommon;
                else
                    rarity = RarityTier.Common;

                var useUniformRarityBadges =
                    PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.UseUniformRarityBadges ?? false;
                var badgeResourceKey = rarity.ToIconKey(useUniformRarityBadges);

                // Try to find the resource in the application resources
                try
                {
                    if (Application.Current.TryFindResource(badgeResourceKey) is DrawingImage badgeImage)
                    {
                        return badgeImage;
                    }
                }
                catch
                {
                    // Resource not found, return null
                }
            }

            return null;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
