using System;
using System.Windows;
using System.Windows.Media;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// A slow sunburst of soft wedges around <see cref="Origin"/>, in <see cref="Color"/>. It turns
    /// on the shared ray tick, so it pauses with every other glow while Playnite is in the
    /// background or a game runs, and it grows in once each time <see cref="Restart"/> is called.
    /// </summary>
    public sealed class YearReviewSunburst : FrameworkElement, IRayAnimationTarget
    {
        private const int WedgeCount = 18;
        private const double DegreesPerSecond = 7;
        private const double GrowMs = 900;

        private double _startMs;

        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color), typeof(Brush), typeof(YearReviewSunburst),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The wedge colour: a solid theme or rarity brush.</summary>
        public Brush Color
        {
            get => (Brush)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        public static readonly DependencyProperty OriginProperty = DependencyProperty.Register(
            nameof(Origin), typeof(Point), typeof(YearReviewSunburst),
            new FrameworkPropertyMetadata(new Point(0.5, 0.5), FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>The centre, relative to the element's size.</summary>
        public Point Origin
        {
            get => (Point)GetValue(OriginProperty);
            set => SetValue(OriginProperty, value);
        }

        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            nameof(IsActive), typeof(bool), typeof(YearReviewSunburst),
            new PropertyMetadata(false, (d, e) => ((YearReviewSunburst)d).UpdateSubscription()));

        /// <summary>True while the slide holding it is shown.</summary>
        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public YearReviewSunburst()
        {
            IsHitTestVisible = false;
            Loaded += (s, e) => UpdateSubscription();
            Unloaded += (s, e) => RayAnimationDriver.Unsubscribe(this);
            IsVisibleChanged += (s, e) => UpdateSubscription();
        }

        /// <summary>Grows the burst in from its centre again.</summary>
        public void Restart()
        {
            _startMs = GlowAnimationClock.ElapsedMilliseconds;
            InvalidateVisual();
        }

        public bool WantsRayFrames => IsActive && IsLoaded && IsVisible;

        public bool IgnoresGlowGate => false;

        public bool TakesEveryHeldFrame => false;

        public void OnRayFrame() => InvalidateVisual();

        private void UpdateSubscription()
        {
            if (WantsRayFrames)
            {
                RayAnimationDriver.Subscribe(this);
            }
            else
            {
                RayAnimationDriver.Unsubscribe(this);
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var color = (Color as SolidColorBrush)?.Color;
            if (!color.HasValue || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            var now = GlowAnimationClock.ElapsedMilliseconds;
            var grow = Math.Min(1, Math.Max(0, (now - _startMs) / GrowMs));
            grow = 1 - Math.Pow(1 - grow, 3);
            var center = new Point(Origin.X * ActualWidth, Origin.Y * ActualHeight);
            var reach = Math.Sqrt(ActualWidth * ActualWidth + ActualHeight * ActualHeight) * grow;
            if (reach <= 1)
            {
                return;
            }

            var c = color.Value;
            var brush = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = reach,
                RadiusY = reach
            };
            brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0x50, c.R, c.G, c.B), 0));
            brush.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromArgb(0, c.R, c.G, c.B), 1));
            brush.Freeze();

            var turn = now / 1000.0 * DegreesPerSecond * Math.PI / 180;
            var half = Math.PI / WedgeCount * 0.55;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < WedgeCount; i++)
                {
                    var angle = i * 2 * Math.PI / WedgeCount + turn;
                    ctx.BeginFigure(center, true, true);
                    ctx.LineTo(Polar(center, reach, angle - half), false, false);
                    ctx.ArcTo(Polar(center, reach, angle + half), new Size(reach, reach), 0, false, SweepDirection.Clockwise, false, false);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(brush, null, geometry);
        }

        private static Point Polar(Point center, double radius, double angle) =>
            new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
    }
}
