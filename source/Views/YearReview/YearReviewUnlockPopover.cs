using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.ViewModels.YearReview;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// A list of unlocks opened at the pointer over a clicked element. The window's click
    /// handler finds the element carrying <see cref="GroupProperty"/> and calls <see cref="Open"/>,
    /// so one decision covers opening a list, leaving a hovered element alone and moving on.
    /// One popup serves the whole window.
    /// </summary>
    public static class YearReviewUnlockPopover
    {
        private const string TemplateKey = "YearReview.UnlockListPopupTemplate";

        private static Popup _popup;

        public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
            "Group", typeof(YearReviewUnlockGroup), typeof(YearReviewUnlockPopover),
            new PropertyMetadata(null, OnGroupChanged));

        public static YearReviewUnlockGroup GetGroup(DependencyObject element) => (YearReviewUnlockGroup)element.GetValue(GroupProperty);

        public static void SetGroup(DependencyObject element, YearReviewUnlockGroup value) => element.SetValue(GroupProperty, value);

        private static void OnGroupChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            element.Cursor = e.NewValue is YearReviewUnlockGroup group && group.Count > 0 ? Cursors.Hand : null;
        }

        /// <summary>The nearest element from <paramref name="source"/> up that has a list to show.</summary>
        public static FrameworkElement FindTarget(DependencyObject source)
        {
            for (var current = source; current != null; current = Parent(current))
            {
                if (current is FrameworkElement element && GetGroup(element) is YearReviewUnlockGroup group && group.Count > 0)
                {
                    return element;
                }
            }

            return null;
        }

        private static DependencyObject Parent(DependencyObject current) =>
            current is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);

        /// <summary>Opens <paramref name="element"/>'s list at the pointer.</summary>
        public static void Open(FrameworkElement element)
        {
            if (!(GetGroup(element) is YearReviewUnlockGroup group))
            {
                return;
            }

            Close();
            _popup = new Popup
            {
                // At the pointer: the clicked element can be as large as a donut slice or a
                // month bar, so its own bounds say little about where the click was.
                PlacementTarget = element,
                Placement = PlacementMode.MousePoint,
                StaysOpen = false,
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = new ContentControl
                {
                    Content = group,
                    ContentTemplate = element.TryFindResource(TemplateKey) as DataTemplate,
                    Focusable = false
                }
            };
            _popup.Closed += (s, args) =>
            {
                if (ReferenceEquals(s, _popup))
                {
                    _popup = null;
                }
            };
            _popup.IsOpen = true;
        }

        /// <summary>Closes the open list, if any.</summary>
        public static void Close()
        {
            var popup = _popup;
            _popup = null;
            if (popup != null)
            {
                popup.IsOpen = false;
                popup.Child = null;
            }
        }
    }
}
