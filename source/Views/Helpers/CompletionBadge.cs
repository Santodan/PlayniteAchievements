using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Inputs of the shared completion badge style (CompletionBadgeImageStyle), which picks
    /// between the completed badge and the platinum trophy.
    /// </summary>
    /// <remarks>
    /// Both are attached properties so the style can test them with property trigger conditions
    /// rather than ancestor bindings, which lets any host carry the badge: the game summaries grid
    /// and the showcase mosaic each set <see cref="PreferTrophyBadgesProperty"/> once on their root,
    /// and it inherits down to every badge in their trees.
    /// </remarks>
    public static class CompletionBadge
    {
        /// <summary>
        /// Whether the host shows trophy badges in place of rarity badges, so a row whose capstones
        /// are its platinums shows the platinum in the completion spot. Inherits.
        /// </summary>
        public static readonly DependencyProperty PreferTrophyBadgesProperty =
            DependencyProperty.RegisterAttached(
                "PreferTrophyBadges",
                typeof(bool),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

        public static void SetPreferTrophyBadges(DependencyObject element, bool value) =>
            element?.SetValue(PreferTrophyBadgesProperty, value);

        public static bool GetPreferTrophyBadges(DependencyObject element) =>
            element == null || (bool)element.GetValue(PreferTrophyBadgesProperty);

        /// <summary>
        /// The badge's own copy of its row's ShowPlatinumInCompletionSpot, set by a style setter
        /// binding so the platinum swap can be a property trigger.
        /// </summary>
        public static readonly DependencyProperty ShowsPlatinumProperty =
            DependencyProperty.RegisterAttached(
                "ShowsPlatinum",
                typeof(bool),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(false));

        public static void SetShowsPlatinum(DependencyObject element, bool value) =>
            element?.SetValue(ShowsPlatinumProperty, value);

        public static bool GetShowsPlatinum(DependencyObject element) =>
            element != null && (bool)element.GetValue(ShowsPlatinumProperty);

        /// <summary>
        /// Size of the badge in the completion frame (CompletionFrameTemplate), set by each host on
        /// the frame's Control to suit its cover size.
        /// </summary>
        public static readonly DependencyProperty FrameBadgeSizeProperty =
            DependencyProperty.RegisterAttached(
                "FrameBadgeSize",
                typeof(double),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(24d));

        public static void SetFrameBadgeSize(DependencyObject element, double value) =>
            element?.SetValue(FrameBadgeSizeProperty, value);

        public static double GetFrameBadgeSize(DependencyObject element) =>
            element == null ? 24d : (double)element.GetValue(FrameBadgeSizeProperty);

        /// <summary>
        /// Binds <see cref="PreferTrophyBadgesProperty"/> on <paramref name="element"/> to whether the
        /// trophy style is enabled in the global progress details list, through the settings wrapper
        /// so a cancelled edit's replaced Persisted instance is followed.
        /// </summary>
        public static void BindPreferTrophyBadgesToSettings(FrameworkElement element)
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (element == null || settings?.Persisted == null)
            {
                return;
            }

            element.SetBinding(
                PreferTrophyBadgesProperty,
                new Binding($"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.ProgressDetails)}")
                {
                    Source = settings,
                    Mode = BindingMode.OneWay,
                    Converter = PreferTrophyConverter.Instance
                });
        }

        private sealed class PreferTrophyConverter : IValueConverter
        {
            public static readonly PreferTrophyConverter Instance = new PreferTrophyConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                ProgressDetailOrder.IsTrophyEnabled(value as IEnumerable<ProgressDetailEntry>);

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                Binding.DoNothing;
        }
    }
}
