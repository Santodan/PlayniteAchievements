using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Rotating sunburst layer for the Rays rarity glow style — the counterpart to the soft
    /// <see cref="System.Windows.Media.Effects.DropShadowEffect"/> halo that
    /// <see cref="RarityGlowPulse"/> fades. Drop it in as the first child of the same
    /// <c>ClipToBounds="False"</c> Grid that already holds a glow layer and a crisp front icon; it
    /// sizes and positions itself from that cell with no explicit dimensions.
    ///
    /// It stacks two filament layers from <see cref="RarityAppearanceHelper"/> and turns them in
    /// opposite directions at different speeds. A single rotating layer reads as one rigid picture
    /// spinning; two at different rates keep interfering, so the rim shimmers instead. Each layer is
    /// bitmap-cached, because the art is a few hundred filled paths and re-tessellating that per
    /// frame for every visible row would be far more expensive than compositing a cached texture
    /// under a transform.
    ///
    /// Rotation starts and stops with <see cref="IsActive"/> and with load/unload, so virtualized
    /// rows never leave forever-animations running off-screen, and it phase-locks to
    /// <see cref="GlowAnimationClock"/> so recycled rows resume mid-turn instead of snapping back to
    /// zero.
    /// </summary>
    public class RarityRayBurst : Panel
    {
        // Slow and fast ends of the base layer's rotation period, mapped from the shared 0-1
        // RarityGlowPulseSpeed setting. Deliberately not the pulse's own 10s-to-0.1s half-cycle
        // range, which reads as a blur rather than a rotation.
        private const double SlowRotationSeconds = 60.0;
        private const double FastRotationSeconds = 4.0;

        // The overlay turns the other way and slower by a non-integer ratio, so the two layers never
        // settle into a repeating pattern.
        private const double OverlayPeriodRatio = 1.6;
        private const double OverlayScaleRatio = 0.88;

        // The corona also swells and shrinks, on periods unrelated to the rotation and different per
        // layer, so it breathes unevenly instead of pumping like one balloon.
        private const double BaseBreathePeriodRatio = 0.55;
        private const double OverlayBreathePeriodRatio = 0.82;
        private const double BaseBreatheAmount = 0.10;
        private const double OverlayBreatheAmount = 0.13;

        private readonly Image _baseLayer;
        private readonly Image _overlayLayer;
        private readonly RotateTransform _baseRotation = new RotateTransform();
        private readonly RotateTransform _overlayRotation = new RotateTransform();
        private readonly ScaleTransform _baseBreathe = new ScaleTransform(1.0, 1.0);
        private readonly ScaleTransform _overlayBreathe = new ScaleTransform(1.0, 1.0);
        private readonly ScaleTransform _baseScale = new ScaleTransform(1.0, 1.0);
        private readonly ScaleTransform _overlayScale = new ScaleTransform(1.0, 1.0);
        private PropertyChangedEventHandler _settingsHandler;

        public RarityRayBurst()
        {
            IsHitTestVisible = false;
            Focusable = false;

            _baseLayer = CreateLayer(_baseRotation, _baseBreathe, _baseScale);
            _overlayLayer = CreateLayer(_overlayRotation, _overlayBreathe, _overlayScale);
            Children.Add(_baseLayer);
            Children.Add(_overlayLayer);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static Image CreateLayer(
            RotateTransform rotation,
            ScaleTransform breathe,
            ScaleTransform aspectScale)
        {
            // Deliberately not BitmapCache'd. A cache rasterizes at the element's own layout bounds
            // and the render transform then magnifies that texture, which both blurs the corona and
            // loses the part of it that the scale is meant to push outside the slot. The art is only
            // a handful of gradient ellipses per layer, so drawing it live is cheap enough.
            var layer = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5)
            };

            // Order matters. Rotation and the uniform breathe run in the art's own circular space;
            // the aspect scale, which fits the corona to a non-square slot, must come last so the
            // envelope stays put and the lumps sweep through it. Applying the aspect scale earlier
            // would tumble a squashed ellipse end over end instead.
            var transforms = new TransformGroup();
            transforms.Children.Add(rotation);
            transforms.Children.Add(breathe);
            transforms.Children.Add(aspectScale);
            layer.RenderTransform = transforms;

            return layer;
        }

        /// <summary>
        /// Rarity tier whose color the burst takes. Common resolves to no art, matching
        /// <see cref="RarityAppearanceHelper.GetGlow"/>.
        /// </summary>
        public static readonly DependencyProperty RarityProperty =
            DependencyProperty.Register(
                nameof(Rarity), typeof(RarityTier), typeof(RarityRayBurst),
                new PropertyMetadata(RarityTier.Common, OnArtSourceChanged));

        public RarityTier Rarity
        {
            get => (RarityTier)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        /// <summary>
        /// When true the burst uses the completed-game gradient colors instead of a rarity tier, for
        /// the completion glow on game and category art.
        /// </summary>
        public static readonly DependencyProperty UseCompletedColorsProperty =
            DependencyProperty.Register(
                nameof(UseCompletedColors), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false, OnArtSourceChanged));

        public bool UseCompletedColors
        {
            get => (bool)GetValue(UseCompletedColorsProperty);
            set => SetValue(UseCompletedColorsProperty, value);
        }

        /// <summary>
        /// Whether the burst rotates. A style trigger sets this from the global AnimateRarityGlows
        /// toggle; when false the burst still renders, just held still.
        /// </summary>
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false, OnIsActiveChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        /// <summary>
        /// When true (default) the rotation phase-locks to the shared epoch, so recreated elements
        /// resume mid-turn. Notification surfaces bind this to IsPreview and opt out, so every
        /// captured wave starts from the same angle.
        /// </summary>
        public static readonly DependencyProperty PhaseLockProperty =
            DependencyProperty.Register(
                nameof(PhaseLock), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(true));

        public bool PhaseLock
        {
            get => (bool)GetValue(PhaseLockProperty);
            set => SetValue(PhaseLockProperty, value);
        }

        /// <summary>
        /// How far the burst reaches beyond its layout slot, as a multiple of it. The default is tuned
        /// so the rays occupy about the same room as the soft glow they replace — on a 64px icon the
        /// longest filaments reach roughly 19px past the edge, against the soft glow's 20px blur.
        /// Because the reach is proportional, larger surfaces need a smaller value to stay in
        /// proportion; completed game art passes one explicitly.
        /// </summary>
        public static readonly DependencyProperty BurstScaleProperty =
            DependencyProperty.Register(
                nameof(BurstScale), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(1.6, OnBurstScaleChanged));

        public double BurstScale
        {
            get => (double)GetValue(BurstScaleProperty);
            set => SetValue(BurstScaleProperty, value);
        }

        private static void OnArtSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as RarityRayBurst)?.ResolveArt();
        }

        private static void OnBurstScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as RarityRayBurst)?.InvalidateArrange();
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is RarityRayBurst burst))
            {
                return;
            }

            if ((bool)e.NewValue)
            {
                if (burst.IsLoaded)
                {
                    burst.Activate();
                }
            }
            else
            {
                burst.Deactivate();
            }
        }

        /// <summary>
        /// Reports no desired size so the burst never drives layout. An Image measures to its
        /// source's natural size, which for this art is its 100x100 coordinate box — enough to
        /// inflate a 28px icon cell (and its whole DataGrid row) to 100px. Reporting zero leaves the
        /// subject to establish the cell; <see cref="ArrangeOverride"/> then stretches the layers to
        /// whatever that cell turned out to be, and their render transforms carry the corona past it.
        ///
        /// Measure and arrange are both done by hand rather than inherited. A Grid or similar panel
        /// derives its arrange rects from cell sizes computed during measure, so a measure that
        /// deliberately under-reports leaves those rects stale — children then get arranged to the
        /// size the panel was offered rather than the size it received, and the corona renders
        /// oversized and off-center.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            // Measured at zero, not at availableSize. A stretching Image reports whatever it is
            // offered as its desired size, and Arrange will not then render it any smaller — so
            // measuring against the offered space would lock the layers to the space the parent had
            // free rather than the cell the subject settles on.
            var empty = new Size(0, 0);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(empty);
            }

            return empty;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);
            foreach (UIElement child in InternalChildren)
            {
                // Re-measured here because the arranged cell is the first point at which the real
                // size is known, and a child's desired size bounds what Arrange will give it.
                child.Measure(finalSize);
                child.Arrange(bounds);
            }

            ApplyBurstScale(finalSize);
            return finalSize;
        }

        /// <summary>
        /// Fits the burst to the arranged slot. The art is square and drawn with Uniform stretch, so
        /// inside a non-square slot it would otherwise shrink to the smaller side — on wide game-logo
        /// art that leaves a small circle hidden behind the image instead of a corona around it.
        /// Scaling each axis by the slot's own extent spreads the burst into an ellipse that tracks
        /// the art's proportions, and reduces to a plain uniform scale when the slot is square.
        /// </summary>
        private void ApplyBurstScale(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            if (side <= 0 || double.IsNaN(side) || double.IsInfinity(side))
            {
                return;
            }

            var scale = BurstScale;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            var scaleX = scale * finalSize.Width / side;
            var scaleY = scale * finalSize.Height / side;

            _baseScale.ScaleX = scaleX;
            _baseScale.ScaleY = scaleY;
            _overlayScale.ScaleX = scaleX * OverlayScaleRatio;
            _overlayScale.ScaleY = scaleY * OverlayScaleRatio;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RarityAppearanceHelper.AppearanceChanged += OnAppearanceChanged;
            ResolveArt();

            if (IsActive)
            {
                Activate();
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            RarityAppearanceHelper.AppearanceChanged -= OnAppearanceChanged;
            Deactivate();
        }

        private void OnAppearanceChanged(object sender, EventArgs e)
        {
            // Tier colors changed, so the cached art is stale. Re-resolving on the element keeps
            // recolors immediate without the call sites having to know about it.
            ResolveArt();
        }

        private void ResolveArt()
        {
            if (UseCompletedColors)
            {
                _baseLayer.Source = RarityAppearanceHelper.GetCompletedRayBurstImage();
                _overlayLayer.Source = RarityAppearanceHelper.GetCompletedRayBurstOverlayImage();
            }
            else
            {
                var tier = Rarity;
                _baseLayer.Source = RarityAppearanceHelper.GetRayBurstImage(tier);
                _overlayLayer.Source = RarityAppearanceHelper.GetRayBurstOverlayImage(tier);
            }

            // A tier with no art (Common) has nothing to turn.
            if (_baseLayer.Source == null)
            {
                StopRotation();
            }
            else if (IsActive && IsLoaded)
            {
                StartRotation();
            }
        }

        private void Activate()
        {
            StartRotation();

            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            if (persisted == null || _settingsHandler != null)
            {
                return;
            }

            // Restart on a speed change so tuning the slider updates on-screen bursts immediately,
            // mirroring how RarityGlowPulse tracks its own settings.
            _settingsHandler = (s, args) =>
            {
                if (args.PropertyName == nameof(PersistedSettings.RarityGlowPulseSpeed) && IsActive)
                {
                    StartRotation();
                }
            };

            persisted.PropertyChanged += _settingsHandler;
        }

        private void Deactivate()
        {
            if (_settingsHandler != null)
            {
                var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
                if (persisted != null)
                {
                    persisted.PropertyChanged -= _settingsHandler;
                }

                _settingsHandler = null;
            }

            StopRotation();
        }

        private void StartRotation()
        {
            if (_baseLayer.Source == null)
            {
                return;
            }

            var seconds = ResolveRotationSeconds();
            StartLayerRotation(_baseRotation, seconds, clockwise: true);
            StartLayerRotation(_overlayRotation, seconds * OverlayPeriodRatio, clockwise: false);

            StartLayerBreathing(_baseBreathe, seconds * BaseBreathePeriodRatio, BaseBreatheAmount);
            StartLayerBreathing(_overlayBreathe, seconds * OverlayBreathePeriodRatio, OverlayBreatheAmount);
        }

        /// <summary>
        /// Swells the layer in and out. Uniform, so it rides ahead of the aspect fit without
        /// distorting it, and sine-eased with AutoReverse so the corona drifts rather than snapping
        /// back at the ends of the cycle.
        /// </summary>
        private void StartLayerBreathing(ScaleTransform breathe, double seconds, double amount)
        {
            if (seconds <= 0)
            {
                return;
            }

            var animation = new DoubleAnimation
            {
                From = 1.0 - amount,
                To = 1.0 + amount,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            // A full breathe cycle is out and back, so the phase-lock period is twice the duration.
            animation.BeginTime = PhaseLock
                ? GlowAnimationClock.PhaseLockBeginTime(seconds * 2000.0)
                : TimeSpan.Zero;

            breathe.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            breathe.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private void StartLayerRotation(RotateTransform rotation, double seconds, bool clockwise)
        {
            var animation = new DoubleAnimation
            {
                From = 0.0,
                To = clockwise ? 360.0 : -360.0,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Phase-lock opt-outs start at angle zero, which keeps repeated captures identical.
            animation.BeginTime = PhaseLock
                ? GlowAnimationClock.PhaseLockBeginTime(seconds * 1000.0)
                : TimeSpan.Zero;

            rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void StopRotation()
        {
            _baseRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _overlayRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _baseRotation.Angle = 0.0;
            _overlayRotation.Angle = 0.0;

            StopBreathing(_baseBreathe);
            StopBreathing(_overlayBreathe);
        }

        private static void StopBreathing(ScaleTransform breathe)
        {
            breathe.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            breathe.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            breathe.ScaleX = 1.0;
            breathe.ScaleY = 1.0;
        }

        private static double ResolveRotationSeconds()
        {
            var persisted = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted;
            var speed = persisted?.RarityGlowPulseSpeed ?? 0.5;
            speed = speed < 0.0 ? 0.0 : (speed > 1.0 ? 1.0 : speed);
            return SlowRotationSeconds - (speed * (SlowRotationSeconds - FastRotationSeconds));
        }
    }
}
