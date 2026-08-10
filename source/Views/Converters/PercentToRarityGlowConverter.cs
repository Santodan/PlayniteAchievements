using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Converters
{
    /// <summary>
    /// Whether a <see cref="RaritySelection"/> includes game completion. Bound rather than read from
    /// settings directly so the completion glows re-evaluate when the selection changes, and used in
    /// trigger conditions, which can only compare a single value for equality and so cannot test a
    /// flag themselves.
    /// </summary>
    public class RaritySelectionIncludesCompletedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is RaritySelection selection && selection.IncludesCompleted();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns the soft rarity glow only when the achievement's tier is one of the selected soft-glow
    /// tiers, and null otherwise. Takes the tier and the selection as separate inputs rather than
    /// reading the setting directly, so that changing the selection re-evaluates the binding and
    /// on-screen glows update immediately instead of waiting for rows to be recycled.
    ///
    /// ConverterParameter is the blur radius, letting compact surfaces ask for a tighter glow than
    /// full-size icons.
    /// </summary>
    public class RarityGlowForTiersConverter : IMultiValueConverter
    {
        private const double DefaultBlurRadius = 20.0;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length < 2 || !(values[0] is RarityTier tier))
            {
                return null;
            }

            // Unset during initialization, before the host control's binding has produced a value.
            // Treated as every tier so the glow is never silently dropped mid-startup.
            var selection = values[1] is RaritySelection selected ? selected : RaritySelection.All;
            if (!selection.Contains(tier))
            {
                return null;
            }

            return RarityAppearanceHelper.GetGlow(tier, ResolveBlurRadius(parameter));
        }

        private static double ResolveBlurRadius(object parameter)
        {
            if (parameter == null)
            {
                return DefaultBlurRadius;
            }

            return double.TryParse(
                parameter.ToString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed) && parsed > 0
                ? parsed
                : DefaultBlurRadius;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

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
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is RarityTier tier))
            {
                return null;
            }

            return RarityAppearanceHelper.GetGlow(tier, 20);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Compact variant with smaller BlurRadius (8 instead of 20) for tight layouts.
    /// </summary>
    public class PercentToCompactRarityGlowConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is RarityTier tier))
            {
                return null;
            }

            return RarityAppearanceHelper.GetGlow(tier, 8);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Returns a glossy metallic gradient brush in the rarity color (matching the rarity badge
    /// sheen), used as a crisp shiny border for Hardcore RetroAchievements icons in place of the
    /// soft glow. Common has no rarity glow, but still gets a Hardcore border.
    /// </summary>
    public class RarityToShineBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (!(value is RarityTier tier))
            {
                return null;
            }

            return RarityAppearanceHelper.GetShineBrush(tier);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
