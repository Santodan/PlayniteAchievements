using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// Writes whether the mouse is over an element into a two-way bound flag, so two elements
    /// that stand for the same thing, such as a platform's row and its donut slice, light up
    /// together whichever one is hovered.
    /// </summary>
    public static class YearReviewHoverSync
    {
        public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
            "IsHovered", typeof(bool), typeof(YearReviewHoverSync),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

        public static bool GetIsHovered(DependencyObject element) => (bool)element.GetValue(IsHoveredProperty);

        public static void SetIsHovered(DependencyObject element, bool value) => element.SetValue(IsHoveredProperty, value);

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(YearReviewHoverSync), new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

        public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            element.MouseEnter -= OnEnter;
            element.MouseLeave -= OnLeave;
            if ((bool)e.NewValue)
            {
                element.MouseEnter += OnEnter;
                element.MouseLeave += OnLeave;
            }
        }

        private static void OnEnter(object sender, MouseEventArgs e) => Set((DependencyObject)sender, true);

        private static void OnLeave(object sender, MouseEventArgs e) => Set((DependencyObject)sender, false);

        private static void Set(DependencyObject element, bool hovered)
        {
            element.SetCurrentValue(IsHoveredProperty, hovered);
            BindingOperations.GetBindingExpression(element, IsHoveredProperty)?.UpdateSource();
        }
    }
}
