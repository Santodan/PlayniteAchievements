using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// A soft highlight that sweeps across a loading skeleton (a placeholder row's cells, a score
    /// card or widget block). One mask brush is shared by every skeleton, so all of them sweep in step,
    /// and it moves on <see cref="RayAnimationDriver"/>'s tick only while a skeleton is visible.
    /// The mask holds a skeleton at two thirds of its opacity outside the band.
    /// </summary>
    public static class SkeletonShimmer
    {
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.RegisterAttached(
                "IsActive",
                typeof(bool),
                typeof(SkeletonShimmer),
                new PropertyMetadata(false, OnIsActiveChanged));

        public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);

        public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                element.OpacityMask = Ticker.Instance.Mask;
                element.IsVisibleChanged += OnIsVisibleChanged;
                if (element.IsVisible)
                {
                    Ticker.Instance.Add(element);
                }
            }
            else
            {
                element.IsVisibleChanged -= OnIsVisibleChanged;
                element.ClearValue(UIElement.OpacityMaskProperty);
                Ticker.Instance.Remove(element);
            }
        }

        private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is UIElement element)
            {
                if ((bool)e.NewValue)
                {
                    Ticker.Instance.Add(element);
                }
                else
                {
                    Ticker.Instance.Remove(element);
                }
            }
        }

        private sealed class Ticker : IRayAnimationTarget
        {
            internal static readonly Ticker Instance = new Ticker();

            // One sweep takes SweepMs, then the band rests past the right edge for the rest of
            // the cycle.
            private const double CycleMs = 2400.0;
            private const double SweepMs = 1600.0;

            // The band spans 0.25..0.75 of the brush; shifting the brush from -0.75 to +0.75 of
            // the element's width carries it from fully left of the element to fully right.
            private const double Travel = 0.75;

            private readonly List<WeakReference<UIElement>> _elements = new List<WeakReference<UIElement>>();
            private readonly TranslateTransform _shift = new TranslateTransform(-Travel, 0);

            internal readonly Brush Mask;

            private Ticker()
            {
                var rest = Color.FromArgb(0xAA, 0, 0, 0);
                var peak = Color.FromArgb(0xFF, 0, 0, 0);
                Mask = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    SpreadMethod = GradientSpreadMethod.Pad,
                    RelativeTransform = _shift,
                    GradientStops =
                    {
                        new GradientStop(rest, 0.25),
                        new GradientStop(peak, 0.5),
                        new GradientStop(rest, 0.75)
                    }
                };
            }

            internal void Add(UIElement element)
            {
                if (IndexOf(element) < 0)
                {
                    _elements.Add(new WeakReference<UIElement>(element));
                }

                RayAnimationDriver.Subscribe(this);
            }

            internal void Remove(UIElement element)
            {
                var index = IndexOf(element);
                if (index >= 0)
                {
                    _elements.RemoveAt(index);
                }

                if (_elements.Count == 0)
                {
                    RayAnimationDriver.Unsubscribe(this);
                }
            }

            private int IndexOf(UIElement element)
            {
                for (var i = 0; i < _elements.Count; i++)
                {
                    if (_elements[i].TryGetTarget(out var existing) && ReferenceEquals(existing, element))
                    {
                        return i;
                    }
                }

                return -1;
            }

            public bool WantsRayFrames
            {
                get
                {
                    _elements.RemoveAll(reference => !reference.TryGetTarget(out var element) || !element.IsVisible);
                    return _elements.Count > 0;
                }
            }

            public bool IgnoresGlowGate => false;

            public bool TakesEveryHeldFrame => false;

            public void OnRayFrame()
            {
                var progress = Math.Min(1.0, (GlowAnimationClock.ElapsedMilliseconds % CycleMs) / SweepMs);
                _shift.X = -Travel + (2 * Travel * progress);
            }
        }
    }
}
