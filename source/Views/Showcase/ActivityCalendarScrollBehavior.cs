using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Scroll handling for the activity calendar's horizontal ScrollViewer: pins the view to the
    /// right end (the most recent weeks) whenever the calendar's extent width changes, and maps
    /// the vertical mouse wheel to horizontal scrolling, which a ScrollViewer with vertical
    /// scrolling disabled otherwise swallows.
    /// </summary>
    public static class ActivityCalendarScrollBehavior
    {
        public static readonly DependencyProperty EnabledProperty =
            DependencyProperty.RegisterAttached(
                "Enabled",
                typeof(bool),
                typeof(ActivityCalendarScrollBehavior),
                new PropertyMetadata(false, OnEnabledChanged));

        public static bool GetEnabled(DependencyObject element) =>
            (bool)element.GetValue(EnabledProperty);

        public static void SetEnabled(DependencyObject element, bool value) =>
            element.SetValue(EnabledProperty, value);

        private static void OnEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
        {
            if (!(sender is ScrollViewer viewer))
            {
                return;
            }

            viewer.Loaded -= OnLoaded;
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.PreviewMouseWheel -= OnPreviewMouseWheel;
            if (e.NewValue is bool enabled && enabled)
            {
                viewer.Loaded += OnLoaded;
                viewer.ScrollChanged += OnScrollChanged;
                viewer.PreviewMouseWheel += OnPreviewMouseWheel;
            }
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!(sender is ScrollViewer viewer))
            {
                return;
            }

            // Deferred so the pin lands after the first layout pass has produced the calendar's
            // extent. Loaded also re-fires when the widget host is re-parented during layout
            // edits, re-pinning the view to the latest weeks in that case too.
            viewer.Dispatcher.BeginInvoke(
                new Action(viewer.ScrollToRightEnd),
                DispatcherPriority.Loaded);
        }

        private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            // The calendar ends at today, so any extent width change (first layout, a new range,
            // a font size change) re-pins the view to the most recent weeks. User scrolling only
            // changes the offset, never the extent, so it is left alone.
            if (e.ExtentWidthChange != 0 && sender is ScrollViewer viewer)
            {
                viewer.ScrollToRightEnd();
            }
        }

        private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(sender is ScrollViewer viewer) || viewer.ScrollableWidth <= 0)
            {
                return;
            }

            // The heatmap's day popup routes its events through the heatmap and so through this
            // viewer; a wheel over the popup's list belongs to that list, not the calendar.
            if (!(e.OriginalSource is Visual source) || !viewer.IsAncestorOf(source))
            {
                return;
            }

            viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset - e.Delta);
            e.Handled = true;
        }
    }
}
