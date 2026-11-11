using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Inputs of the shared completion badge style (CompletionBadgeImageStyle), which shows the
    /// completed badge, the platinum trophy or the mythic badge.
    /// </summary>
    /// <remarks>
    /// The badge follows the row's leading progress detail, the same priority the detail strip
    /// uses (<see cref="GameSummaryItem.CompletionBadgeFor"/>). Hosts set
    /// <see cref="DetailOrderProperty"/> once on their root and it inherits down to every badge;
    /// an element with <see cref="TracksKindProperty"/> set keeps <see cref="KindProperty"/>
    /// current from its row and that order, so styles switch on it with property triggers.
    /// </remarks>
    public static class CompletionBadge
    {
        /// <summary>The enabled progress detail styles in priority order. Inherits.</summary>
        public static readonly DependencyProperty DetailOrderProperty =
            DependencyProperty.RegisterAttached(
                "DetailOrder",
                typeof(IReadOnlyList<ProgressDetailStyle>),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnInputChanged));

        public static void SetDetailOrder(DependencyObject element, IReadOnlyList<ProgressDetailStyle> value) =>
            element?.SetValue(DetailOrderProperty, value);

        public static IReadOnlyList<ProgressDetailStyle> GetDetailOrder(DependencyObject element) =>
            element?.GetValue(DetailOrderProperty) as IReadOnlyList<ProgressDetailStyle>;

        /// <summary>What the badge shows for its row, kept current on elements that track it.</summary>
        public static readonly DependencyProperty KindProperty =
            DependencyProperty.RegisterAttached(
                "Kind",
                typeof(CompletionBadgeKind),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(CompletionBadgeKind.Completed));

        public static void SetKind(DependencyObject element, CompletionBadgeKind value) =>
            element?.SetValue(KindProperty, value);

        public static CompletionBadgeKind GetKind(DependencyObject element) =>
            element == null ? CompletionBadgeKind.Completed : (CompletionBadgeKind)element.GetValue(KindProperty);

        /// <summary>
        /// Set by a style to keep <see cref="KindProperty"/> current from the element's row
        /// (its DataContext) and the inherited <see cref="DetailOrderProperty"/>.
        /// </summary>
        public static readonly DependencyProperty TracksKindProperty =
            DependencyProperty.RegisterAttached(
                "TracksKind",
                typeof(bool),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(false, OnTracksKindChanged));

        public static void SetTracksKind(DependencyObject element, bool value) =>
            element?.SetValue(TracksKindProperty, value);

        public static bool GetTracksKind(DependencyObject element) =>
            element != null && (bool)element.GetValue(TracksKindProperty);

        // The element's subscription to its row. The weak event manager holds the handler weakly,
        // so the element holds it here to keep it alive for as long as the element is.
        private sealed class Tracker
        {
            private readonly WeakReference<FrameworkElement> _element;
            private GameSummaryItem _item;

            public Tracker(FrameworkElement element)
            {
                _element = new WeakReference<FrameworkElement>(element);
            }

            public void Attach(GameSummaryItem item)
            {
                if (ReferenceEquals(_item, item))
                {
                    return;
                }

                if (_item != null)
                {
                    PropertyChangedEventManager.RemoveHandler(_item, OnItemPropertyChanged, string.Empty);
                }

                _item = item;
                if (_item != null)
                {
                    PropertyChangedEventManager.AddHandler(_item, OnItemPropertyChanged, string.Empty);
                }
            }

            private void OnItemPropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                if (_element.TryGetTarget(out var element))
                {
                    Update(element);
                }
            }
        }

        private static readonly DependencyProperty TrackerProperty =
            DependencyProperty.RegisterAttached(
                "Tracker",
                typeof(Tracker),
                typeof(CompletionBadge),
                new FrameworkPropertyMetadata(null));

        private static void OnTracksKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                element.SetValue(TrackerProperty, new Tracker(element));
                element.DataContextChanged += OnDataContextChanged;
                Update(element);
            }
            else
            {
                element.DataContextChanged -= OnDataContextChanged;
                (element.GetValue(TrackerProperty) as Tracker)?.Attach(null);
                element.ClearValue(TrackerProperty);
            }
        }

        private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                Update(element);
            }
        }

        private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is FrameworkElement element && GetTracksKind(element))
            {
                Update(element);
            }
        }

        private static void Update(FrameworkElement element)
        {
            var item = element.DataContext as GameSummaryItem;
            (element.GetValue(TrackerProperty) as Tracker)?.Attach(item);
            element.SetValue(
                KindProperty,
                item?.CompletionBadgeFor(GetDetailOrder(element)) ?? CompletionBadgeKind.Completed);
        }

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
        /// Binds <see cref="DetailOrderProperty"/> on <paramref name="element"/> to the enabled
        /// styles of the global progress details list, through the settings wrapper so a
        /// cancelled edit's replaced Persisted instance is followed.
        /// </summary>
        public static void BindDetailOrderToSettings(FrameworkElement element)
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (element == null || settings?.Persisted == null)
            {
                return;
            }

            element.SetBinding(
                DetailOrderProperty,
                new Binding($"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.ProgressDetails)}")
                {
                    Source = settings,
                    Mode = BindingMode.OneWay,
                    Converter = EnabledStylesConverter.Instance
                });
        }

        private sealed class EnabledStylesConverter : IValueConverter
        {
            public static readonly EnabledStylesConverter Instance = new EnabledStylesConverter();

            public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
                ProgressDetailOrder.EnabledStyles(value as IEnumerable<ProgressDetailEntry>);

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
                Binding.DoNothing;
        }
    }
}
