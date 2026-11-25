using System;
using System.Windows;
using System.Windows.Media;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.YearReview
{
    /// <summary>
    /// The Year in Review entry's icon: a five-point star that breathes and turns over a ring of
    /// turning rays, with two small stars twinkling beside it. It runs on the shared ray tick, so it
    /// pauses with every other glow while Playnite is in the background or a game runs. While
    /// <see cref="IsHot"/> everything turns faster and the rays reach further.
    /// </summary>
    public sealed class YearReviewSparkle : FrameworkElement, IRayAnimationTarget
    {
        private const int RayCount = 10;
        private const double RayDegreesPerSecond = 24;
        private const double StarDegreesPerSecond = 14;
        private const double BreathMs = 1600;
        private const double TwinkleMs = 1300;
        private const double HotSpeed = 3;
        private const double EaseMs = 220;

        private double _speed = 1;
        private double _lastMs = -1;
        private double _rayTurn;
        private double _starTurn;
        private double _hot;

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(YearReviewSparkle),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>A solid theme brush for the stars and rays.</summary>
        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public static readonly DependencyProperty OutlineProperty = DependencyProperty.Register(
            nameof(Outline), typeof(Brush), typeof(YearReviewSparkle),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>Edges the main star so it stays distinct from the rays behind it.</summary>
        public Brush Outline
        {
            get => (Brush)GetValue(OutlineProperty);
            set => SetValue(OutlineProperty, value);
        }

        public static readonly DependencyProperty IsHotProperty = DependencyProperty.Register(
            nameof(IsHot), typeof(bool), typeof(YearReviewSparkle), new PropertyMetadata(false));

        /// <summary>True while the button holding it is hovered.</summary>
        public bool IsHot
        {
            get => (bool)GetValue(IsHotProperty);
            set => SetValue(IsHotProperty, value);
        }

        public YearReviewSparkle()
        {
            IsHitTestVisible = false;
            Loaded += (s, e) => UpdateSubscription();
            Unloaded += (s, e) => RayAnimationDriver.Unsubscribe(this);
            IsVisibleChanged += (s, e) => UpdateSubscription();
        }

        public bool WantsRayFrames => IsLoaded && IsVisible;

        public bool IgnoresGlowGate => false;

        public bool TakesEveryHeldFrame => true;

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
            var color = (Fill as SolidColorBrush)?.Color;
            if (!color.HasValue || ActualWidth <= 0 || ActualHeight <= 0)
            {
                return;
            }

            // Advance by elapsed time at the current speed, so hovering speeds the turn up
            // without jumping it.
            var now = GlowAnimationClock.ElapsedMilliseconds;
            var dt = _lastMs < 0 ? 0 : Math.Min(100, now - _lastMs);
            _lastMs = now;
            var hotTarget = IsHot ? 1 : 0;
            _hot += Math.Sign(hotTarget - _hot) * Math.Min(Math.Abs(hotTarget - _hot), dt / EaseMs);
            _speed = 1 + (HotSpeed - 1) * _hot;
            _rayTurn += dt / 1000 * RayDegreesPerSecond * _speed;
            _starTurn += dt / 1000 * StarDegreesPerSecond * _speed;

            var c = color.Value;
            var size = Math.Min(ActualWidth, ActualHeight);
            var center = new Point(ActualWidth / 2, ActualHeight / 2);

            // Rays.
            var reach = size * (0.5 + 0.1 * _hot);
            var rays = new RadialGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                Center = center,
                GradientOrigin = center,
                RadiusX = reach,
                RadiusY = reach
            };
            rays.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0xD8 + 0x27 * _hot), c.R, c.G, c.B), 0.25));
            rays.GradientStops.Add(new GradientStop(Color.FromArgb((byte)(0x38 + 0x30 * _hot), c.R, c.G, c.B), 1));
            rays.Freeze();
            var half = Math.PI / RayCount * 0.6;
            var rayGeometry = new StreamGeometry();
            using (var ctx = rayGeometry.Open())
            {
                for (var i = 0; i < RayCount; i++)
                {
                    var angle = i * 2 * Math.PI / RayCount + _rayTurn * Math.PI / 180;
                    ctx.BeginFigure(center, true, true);
                    ctx.LineTo(Polar(center, reach, angle - half), false, false);
                    ctx.LineTo(Polar(center, reach, angle + half), false, false);
                }
            }

            rayGeometry.Freeze();
            dc.DrawGeometry(rays, null, rayGeometry);

            var star = new SolidColorBrush(c);
            star.Freeze();

            // Main star, breathing.
            var breath = 0.5 + 0.5 * Math.Sin(now / BreathMs * 2 * Math.PI);
            var mainRadius = size * (0.3 + 0.04 * breath + 0.04 * _hot);
            // Theme brushes can be unfreezable, so the pen takes a fresh brush of the same colour.
            var outlineColor = (Outline as SolidColorBrush)?.Color;
            Pen outline = null;
            if (outlineColor.HasValue)
            {
                var outlineBrush = new SolidColorBrush(outlineColor.Value);
                outlineBrush.Freeze();
                outline = new Pen(outlineBrush, Math.Max(1, size * 0.05)) { LineJoin = PenLineJoin.Round };
                outline.Freeze();
            }
            dc.DrawGeometry(star, outline, Star(center, mainRadius, _starTurn, 5, 0.42));

            // Two twinkles, out of step with each other.
            DrawTwinkle(dc, star, new Point(center.X + size * 0.32, center.Y - size * 0.3), size * 0.13, now / TwinkleMs);
            DrawTwinkle(dc, star, new Point(center.X - size * 0.3, center.Y + size * 0.3), size * 0.1, now / TwinkleMs + 0.5);
        }

        private static void DrawTwinkle(DrawingContext dc, Brush brush, Point at, double radius, double phase)
        {
            var t = phase - Math.Floor(phase);
            var scale = Math.Max(0, Math.Sin(t * 2 * Math.PI));
            if (scale <= 0.02)
            {
                return;
            }

            dc.PushOpacity(scale);
            dc.DrawGeometry(brush, null, Star(at, radius * scale, 45, 4, 0.28));
            dc.Pop();
        }

        /// <summary>
        /// A star of <paramref name="points"/> points reaching <paramref name="radius"/>, its notches at
        /// <paramref name="innerRatio"/> of that, turned by <paramref name="degrees"/>.
        /// </summary>
        private static Geometry Star(Point center, double radius, double degrees, int points, double innerRatio)
        {
            var inner = radius * innerRatio;
            var step = 180.0 / points;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < points * 2; i++)
                {
                    var angle = (degrees - 90 + i * step) * Math.PI / 180;
                    var point = Polar(center, i % 2 == 0 ? radius : inner, angle);
                    if (i == 0)
                    {
                        ctx.BeginFigure(point, true, true);
                    }
                    else
                    {
                        ctx.LineTo(point, true, true);
                    }
                }
            }

            geometry.Freeze();
            return geometry;
        }

        private static Point Polar(Point center, double radius, double angle) =>
            new Point(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
    }
}
