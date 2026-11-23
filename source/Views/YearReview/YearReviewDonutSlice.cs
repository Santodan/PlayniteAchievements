using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// One slice of the platform donut. While <see cref="IsLifted"/> it slides outward along its
    /// middle, both edges moving together so it lifts off the hole, the way the pie chart
    /// widget's slices do.
    /// </summary>
    public sealed class YearReviewDonutSlice : FrameworkElement
    {
        /// <summary>How far a hovered slice moves out, scaled from the widget's pie to this donut's size.</summary>
        private const double HoverGrowth = 18;

        /// <summary>The widget pie's slide: 150 ms, easing out.</summary>
        private const double HoverMilliseconds = 150;

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(YearReviewDonutSlice),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public static readonly DependencyProperty StartAngleProperty = DependencyProperty.Register(
            nameof(StartAngle), typeof(double), typeof(YearReviewDonutSlice),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Where the slice begins, in degrees clockwise from the right.</summary>
        public double StartAngle
        {
            get => (double)GetValue(StartAngleProperty);
            set => SetValue(StartAngleProperty, value);
        }

        public static readonly DependencyProperty SweepProperty = DependencyProperty.Register(
            nameof(Sweep), typeof(double), typeof(YearReviewDonutSlice),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Sweep
        {
            get => (double)GetValue(SweepProperty);
            set => SetValue(SweepProperty, value);
        }

        public static readonly DependencyProperty InnerRatioProperty = DependencyProperty.Register(
            nameof(InnerRatio), typeof(double), typeof(YearReviewDonutSlice),
            new FrameworkPropertyMetadata(0.48, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The hole's radius as a share of the ring's.</summary>
        public double InnerRatio
        {
            get => (double)GetValue(InnerRatioProperty);
            set => SetValue(InnerRatioProperty, value);
        }

        public static readonly DependencyProperty IsLiftedProperty = DependencyProperty.Register(
            nameof(IsLifted), typeof(bool), typeof(YearReviewDonutSlice),
            new PropertyMetadata(false, (d, e) => ((YearReviewDonutSlice)d).SlideTo((bool)e.NewValue ? HoverGrowth : 0)));

        /// <summary>True while the slice or its platform's row is hovered.</summary>
        public bool IsLifted
        {
            get => (bool)GetValue(IsLiftedProperty);
            set => SetValue(IsLiftedProperty, value);
        }

        private static readonly DependencyProperty GrowthProperty = DependencyProperty.Register(
            "Growth", typeof(double), typeof(YearReviewDonutSlice),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public YearReviewDonutSlice()
        {
            Cursor = Cursors.Hand;
        }

        private void SlideTo(double growth)
        {
            if (!YearReviewMotion.Enabled)
            {
                BeginAnimation(GrowthProperty, null);
                SetValue(GrowthProperty, growth);
                return;
            }

            var animation = new DoubleAnimation(growth, TimeSpan.FromMilliseconds(HoverMilliseconds))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            BeginAnimation(GrowthProperty, AnimationFrameRate.Apply(animation));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Fill == null || Sweep <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            var growth = (double)GetValue(GrowthProperty);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            var outer = Math.Min(ActualWidth, ActualHeight) / 2 - HoverGrowth;
            var inner = outer * InnerRatio;
            dc.DrawGeometry(Fill, null, Ring(center, outer + growth, inner + growth, StartAngle, Sweep));
        }

        private static Geometry Ring(Point center, double outer, double inner, double start, double sweep)
        {
            if (sweep >= 359.99)
            {
                return new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new EllipseGeometry(center, outer, outer),
                    new EllipseGeometry(center, inner, inner));
            }

            Point At(double radius, double degrees)
            {
                var radians = degrees * Math.PI / 180;
                return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
            }

            var large = sweep > 180;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(At(outer, start), true, true);
                ctx.ArcTo(At(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
                ctx.LineTo(At(inner, start + sweep), true, false);
                ctx.ArcTo(At(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
            }

            geometry.Freeze();
            return geometry;
        }
    }
}
