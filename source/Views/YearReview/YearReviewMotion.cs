using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>How an element enters when its slide is shown.</summary>
    public enum YearReviewReveal
    {
        None,

        /// <summary>Fades in while rising a short way.</summary>
        Rise,

        /// <summary>Fades in while growing from 85% with a slight overshoot.</summary>
        Zoom,

        /// <summary>Falls in from above, turning to its final angle.</summary>
        Drop,

        /// <summary>Springs out from a fifth of its size.</summary>
        Pop,

        /// <summary>Lands from 170% size, like a stamp.</summary>
        Slam,

        /// <summary>Slides in from the left.</summary>
        SlideIn,

        /// <summary>Grows up from its bottom edge.</summary>
        Grow,

        /// <summary>Fades in only.</summary>
        Fade,

        /// <summary>Winds in, turning and growing.</summary>
        Spin,

        /// <summary>A shape's stroke draws itself along its length.</summary>
        Draw
    }

    /// <summary>
    /// Attached entrance motion for the Year in Review slides. Every element rests in its final
    /// pose; <see cref="Play"/> runs each entrance from its start pose back to that rest, holding
    /// the start pose until the element's <see cref="DelayProperty"/> has passed. So a slide shown
    /// without motion, captured, or rendered to an image is always complete.
    /// </summary>
    public static class YearReviewMotion
    {
        public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached(
            "Reveal", typeof(YearReviewReveal), typeof(YearReviewMotion),
            new PropertyMetadata(YearReviewReveal.None, OnPoseChanged));

        public static YearReviewReveal GetReveal(DependencyObject element) => (YearReviewReveal)element.GetValue(RevealProperty);

        public static void SetReveal(DependencyObject element, YearReviewReveal value) => element.SetValue(RevealProperty, value);

        /// <summary>Milliseconds after the slide appears before the entrance starts.</summary>
        public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
            "Delay", typeof(double), typeof(YearReviewMotion), new PropertyMetadata(0d));

        public static double GetDelay(DependencyObject element) => (double)element.GetValue(DelayProperty);

        public static void SetDelay(DependencyObject element, double value) => element.SetValue(DelayProperty, value);

        /// <summary>The element's resting tilt in degrees.</summary>
        public static readonly DependencyProperty AngleProperty = DependencyProperty.RegisterAttached(
            "Angle", typeof(double), typeof(YearReviewMotion), new PropertyMetadata(0d, OnPoseChanged));

        public static double GetAngle(DependencyObject element) => (double)element.GetValue(AngleProperty);

        public static void SetAngle(DependencyObject element, double value) => element.SetValue(AngleProperty, value);

        /// <summary>The length of a <see cref="YearReviewReveal.Draw"/> shape's stroke, in device-independent pixels.</summary>
        public static readonly DependencyProperty DrawLengthProperty = DependencyProperty.RegisterAttached(
            "DrawLength", typeof(double), typeof(YearReviewMotion), new PropertyMetadata(1200d));

        public static double GetDrawLength(DependencyObject element) => (double)element.GetValue(DrawLengthProperty);

        public static void SetDrawLength(DependencyObject element, double value) => element.SetValue(DrawLengthProperty, value);

        /// <summary>When true, the element lifts slightly and comes to the front under the mouse.</summary>
        public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached(
            "Hover", typeof(bool), typeof(YearReviewMotion), new PropertyMetadata(false, OnHoverChanged));

        public static bool GetHover(DependencyObject element) => (bool)element.GetValue(HoverProperty);

        public static void SetHover(DependencyObject element, bool value) => element.SetValue(HoverProperty, value);

        private const double HoverScale = 1.07;

        private static void OnHoverChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is UIElement element))
            {
                return;
            }

            element.MouseEnter -= OnHoverEnter;
            element.MouseLeave -= OnHoverLeave;
            if ((bool)e.NewValue)
            {
                EnsureTransforms(element);
                element.MouseEnter += OnHoverEnter;
                element.MouseLeave += OnHoverLeave;
            }
        }

        private static void OnHoverEnter(object sender, System.Windows.Input.MouseEventArgs e) => Lift((UIElement)sender, true);

        private static void OnHoverLeave(object sender, System.Windows.Input.MouseEventArgs e) => Lift((UIElement)sender, false);

        private static void Lift(UIElement element, bool lifted)
        {
            var parts = EnsureTransforms(element);
            System.Windows.Controls.Panel.SetZIndex(element, lifted ? 1 : 0);
            var to = lifted ? HoverScale : 1;
            if (!Enabled)
            {
                parts.Hover.ScaleX = to;
                parts.Hover.ScaleY = to;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var animation = AnimationFrameRate.Apply(new DoubleAnimation(to, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            parts.Hover.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            parts.Hover.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        /// <summary>
        /// True: the review always plays its entrances, like the plugin's notifications and glows,
        /// which do not follow the Windows animation switch either. The rest poses stay complete,
        /// so a slide that is captured or shown mid-entrance still reads correctly.
        /// </summary>
        public static bool Enabled => true;

        private static void OnPoseChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is UIElement element)
            {
                var parts = EnsureTransforms(element);
                parts.Rotate.Angle = GetAngle(element);
            }
        }

        /// <summary>Plays every entrance under <paramref name="root"/>.</summary>
        public static void Play(DependencyObject root)
        {
            foreach (var element in Walk(root))
            {
                if (element is CountUpText counter)
                {
                    counter.Play(GetDelay(counter));
                }
                else if (element is YearReviewSunburst sunburst)
                {
                    sunburst.Restart();
                }

                var reveal = GetReveal(element);
                if (reveal != YearReviewReveal.None)
                {
                    if (Enabled)
                    {
                        Start(element, reveal, GetDelay(element));
                    }
                    else
                    {
                        Stop(element);
                    }
                }
            }
        }

        /// <summary>Drops every entrance under <paramref name="root"/>, leaving each element at rest.</summary>
        public static void Reset(DependencyObject root)
        {
            foreach (var element in Walk(root))
            {
                if (element is CountUpText counter)
                {
                    counter.Stop();
                }

                if (GetReveal(element) != YearReviewReveal.None)
                {
                    Stop(element);
                }
            }
        }

        private static IEnumerable<UIElement> Walk(DependencyObject root)
        {
            if (root == null)
            {
                yield break;
            }

            var pending = new Stack<DependencyObject>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current is UIElement element)
                {
                    yield return element;
                }

                if (!(current is Visual) && !(current is System.Windows.Media.Media3D.Visual3D))
                {
                    continue;
                }

                var count = VisualTreeHelper.GetChildrenCount(current);
                for (var i = count - 1; i >= 0; i--)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, i));
                }
            }
        }

        private sealed class Parts
        {
            public ScaleTransform Hover;
            public ScaleTransform Scale;
            public RotateTransform Rotate;
            public TranslateTransform Translate;
        }

        private static readonly DependencyProperty PartsProperty = DependencyProperty.RegisterAttached(
            "Parts", typeof(Parts), typeof(YearReviewMotion), new PropertyMetadata(null));

        private static Parts EnsureTransforms(UIElement element)
        {
            if (element.GetValue(PartsProperty) is Parts existing)
            {
                return existing;
            }

            var parts = new Parts
            {
                Hover = new ScaleTransform(),
                Scale = new ScaleTransform(),
                Rotate = new RotateTransform(),
                Translate = new TranslateTransform()
            };
            var group = new TransformGroup();
            group.Children.Add(parts.Hover);
            group.Children.Add(parts.Scale);
            group.Children.Add(parts.Rotate);
            group.Children.Add(parts.Translate);
            element.RenderTransform = group;
            element.RenderTransformOrigin = GetReveal(element) == YearReviewReveal.Grow ? new Point(0.5, 1) : new Point(0.5, 0.5);
            element.SetValue(PartsProperty, parts);
            return parts;
        }

        private static void Start(UIElement element, YearReviewReveal reveal, double delay)
        {
            var parts = EnsureTransforms(element);
            var angle = GetAngle(element);
            if (reveal == YearReviewReveal.Grow)
            {
                element.RenderTransformOrigin = new Point(0.5, 1);
            }

            var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
            var cubic = new CubicEase { EasingMode = EasingMode.EaseOut };
            switch (reveal)
            {
                case YearReviewReveal.Rise:
                    Fade(element, delay, 500);
                    Run(parts.Translate, TranslateTransform.YProperty, 24, 0, delay, 600, cubic);
                    break;
                case YearReviewReveal.Zoom:
                    Fade(element, delay, 350);
                    Scale(parts, 0.85, delay, 550, back);
                    break;
                case YearReviewReveal.Drop:
                    Fade(element, delay, 250);
                    Run(parts.Translate, TranslateTransform.YProperty, -160, 0, delay, 600, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 });
                    Run(parts.Rotate, RotateTransform.AngleProperty, angle * 3, angle, delay, 600, cubic);
                    break;
                case YearReviewReveal.Pop:
                    Fade(element, delay, 200);
                    Scale(parts, 0.2, delay, 500, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
                    break;
                case YearReviewReveal.Slam:
                    Fade(element, delay, 150);
                    Scale(parts, 1.7, delay, 350, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
                    Run(parts.Rotate, RotateTransform.AngleProperty, angle - 10, angle, delay, 350, cubic);
                    break;
                case YearReviewReveal.SlideIn:
                    Fade(element, delay, 500);
                    Run(parts.Translate, TranslateTransform.XProperty, -140, 0, delay, 600, cubic);
                    break;
                case YearReviewReveal.Grow:
                    Run(parts.Scale, ScaleTransform.ScaleYProperty, 0, 1, delay, 700, cubic);
                    break;
                case YearReviewReveal.Fade:
                    Fade(element, delay, 250);
                    break;
                case YearReviewReveal.Spin:
                    Fade(element, delay, 300);
                    Run(parts.Rotate, RotateTransform.AngleProperty, angle - 120, angle, delay, 1000, cubic);
                    Scale(parts, 0.7, delay, 1000, cubic);
                    break;
                case YearReviewReveal.Draw:
                    if (element is Shape shape && shape.StrokeThickness > 0)
                    {
                        var dashes = GetDrawLength(shape) / shape.StrokeThickness;
                        shape.StrokeDashArray = new DoubleCollection { dashes, dashes };
                        Run(shape, Shape.StrokeDashOffsetProperty, dashes, 0, delay, 1400, new CubicEase { EasingMode = EasingMode.EaseInOut });
                    }

                    break;
            }
        }

        private static void Stop(UIElement element)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            if (element.GetValue(PartsProperty) is Parts parts)
            {
                parts.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                parts.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                parts.Rotate.BeginAnimation(RotateTransform.AngleProperty, null);
                parts.Translate.BeginAnimation(TranslateTransform.XProperty, null);
                parts.Translate.BeginAnimation(TranslateTransform.YProperty, null);
            }

            if (element is Shape shape)
            {
                shape.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            }
        }

        private static void Fade(UIElement element, double delay, double duration) =>
            Run(element, UIElement.OpacityProperty, 0, 1, delay, duration, null);

        private static void Scale(Parts parts, double from, double delay, double duration, IEasingFunction ease)
        {
            Run(parts.Scale, ScaleTransform.ScaleXProperty, from, 1, delay, duration, ease);
            Run(parts.Scale, ScaleTransform.ScaleYProperty, from, 1, delay, duration, ease);
        }

        /// <summary>
        /// Holds <paramref name="from"/> until <paramref name="delay"/>, then eases to
        /// <paramref name="to"/>, which is the property's resting value, so the animation can
        /// end without a jump.
        /// </summary>
        private static void Run(
            IAnimatable target,
            DependencyProperty property,
            double from,
            double to,
            double delay,
            double duration,
            IEasingFunction ease)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, delay)))));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(Math.Max(0, delay) + duration)), ease));
            target.BeginAnimation(property, AnimationFrameRate.Apply(animation));
        }
    }
}
