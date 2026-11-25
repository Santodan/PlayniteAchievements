using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// Picks out the bar under the mouse the way the timeline widget marks its selected column:
    /// the other bars of the same chart fade, and the bar itself takes a text-colored outline.
    /// </summary>
    public static class YearReviewBarHighlight
    {
        /// <summary>The fade the timeline widget gives unselected columns.</summary>
        private const double FadedOpacity = 0.18;

        /// <summary>The timeline widget's outline around the selected column.</summary>
        private const double OutlineThickness = 1.5;

        private const double FadeMilliseconds = 150;

        public static readonly DependencyProperty IsBarProperty = DependencyProperty.RegisterAttached(
            "IsBar", typeof(bool), typeof(YearReviewBarHighlight), new PropertyMetadata(false, OnIsBarChanged));

        public static bool GetIsBar(DependencyObject element) => (bool)element.GetValue(IsBarProperty);

        public static void SetIsBar(DependencyObject element, bool value) => element.SetValue(IsBarProperty, value);

        private static void OnIsBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Border bar))
            {
                return;
            }

            bar.MouseEnter -= OnEnter;
            bar.MouseLeave -= OnLeave;
            if ((bool)e.NewValue)
            {
                bar.MouseEnter += OnEnter;
                bar.MouseLeave += OnLeave;
            }
        }

        private static void OnEnter(object sender, MouseEventArgs e) => Highlight((Border)sender, true);

        private static void OnLeave(object sender, MouseEventArgs e) => Highlight((Border)sender, false);

        private static void Highlight(Border hovered, bool on)
        {
            foreach (var bar in BarsOf(hovered))
            {
                var picked = ReferenceEquals(bar, hovered);
                Fade(bar, !on || picked ? 1 : FadedOpacity);
                if (picked && on)
                {
                    bar.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Text");
                    bar.BorderThickness = new Thickness(OutlineThickness);
                }
                else
                {
                    bar.ClearValue(Border.BorderBrushProperty);
                    bar.ClearValue(Border.BorderThicknessProperty);
                }
            }
        }

        private static void Fade(UIElement bar, double to)
        {
            if (!YearReviewMotion.Enabled)
            {
                bar.BeginAnimation(UIElement.OpacityProperty, null);
                bar.Opacity = to;
                return;
            }

            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(FadeMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            bar.BeginAnimation(UIElement.OpacityProperty, AnimationFrameRate.Apply(animation));
        }

        /// <summary>Every bar in the same chart: the bars under the nearest ItemsControl.</summary>
        private static IEnumerable<Border> BarsOf(Border bar)
        {
            DependencyObject chart = bar;
            while (chart != null && !(chart is ItemsControl))
            {
                chart = VisualTreeHelper.GetParent(chart);
            }

            if (chart == null)
            {
                yield return bar;
                yield break;
            }

            var pending = new Stack<DependencyObject>();
            pending.Push(chart);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current is Border border && GetIsBar(border))
                {
                    yield return border;
                }

                for (var i = VisualTreeHelper.GetChildrenCount(current) - 1; i >= 0; i--)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }
    }
}
