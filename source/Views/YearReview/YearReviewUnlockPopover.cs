using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.ViewModels.YearReview;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// Opens a list of unlocks under an element when it is clicked: a month bar or a day of the
    /// year. One popup serves the whole window; the click is handled here, so it does not also
    /// move to the next slide.
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

            element.MouseLeftButtonUp -= OnClick;
            if (e.NewValue is YearReviewUnlockGroup group && group.Count > 0)
            {
                element.MouseLeftButtonUp += OnClick;
                element.Cursor = Cursors.Hand;
            }
        }

        private static void OnClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(GetGroup(element) is YearReviewUnlockGroup group))
            {
                return;
            }

            e.Handled = true;
            Close();
            _popup = new Popup
            {
                PlacementTarget = element,
                Placement = PlacementMode.Bottom,
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
