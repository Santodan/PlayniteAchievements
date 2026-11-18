using System;
using System.Windows;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attached behavior that clips an element to a rounded rectangle matching its render size.
    /// Set it on the Image itself: an Image arranges to the size of the picture it draws, so the
    /// clip follows the picture rather than the cell around it. A radius of 0 removes the clip.
    /// </summary>
    public static class RoundedClip
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius",
            typeof(double),
            typeof(RoundedClip),
            new PropertyMetadata(0d, OnRadiusChanged));

        public static double GetRadius(DependencyObject element) =>
            (double)element.GetValue(RadiusProperty);

        public static void SetRadius(DependencyObject element, double value) =>
            element.SetValue(RadiusProperty, value);

        /// <summary>
        /// The radius actually drawn: the requested radius, at least 0 and at most half the
        /// shorter side, so a large value makes a circle or pill rather than overlapping corners.
        /// </summary>
        public static double ComputeRadius(double radius, Size size)
        {
            if (double.IsNaN(radius) || radius <= 0 || size.Width <= 0 || size.Height <= 0)
            {
                return 0;
            }

            return Math.Min(radius, Math.Min(size.Width, size.Height) / 2);
        }

        private static void OnRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            var radius = (double)e.NewValue;
            var wasActive = (double)e.OldValue > 0;
            var isActive = radius > 0;

            if (isActive && !wasActive)
            {
                element.SizeChanged += OnSizeChanged;
            }
            else if (!isActive && wasActive)
            {
                element.SizeChanged -= OnSizeChanged;
                element.ClearValue(UIElement.ClipProperty);
                return;
            }

            if (isActive)
            {
                ApplyClip(element, element.RenderSize);
            }
        }

        private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is FrameworkElement element)
            {
                ApplyClip(element, e.NewSize);
            }
        }

        private static void ApplyClip(FrameworkElement element, Size size)
        {
            var radius = ComputeRadius(GetRadius(element), size);
            if (radius <= 0)
            {
                element.ClearValue(UIElement.ClipProperty);
                return;
            }

            var clip = new RectangleGeometry(new Rect(size), radius, radius);
            clip.Freeze();
            element.Clip = clip;
        }
    }
}
