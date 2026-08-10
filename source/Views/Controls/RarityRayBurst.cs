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
    /// The art comes frozen from <see cref="RarityAppearanceHelper.GetRayBurstImage"/>, so rotation
    /// costs a render-thread transform rather than re-rasterizing an effect each frame. Rotation
    /// starts and stops with <see cref="IsActive"/> and with load/unload, so virtualized rows never
    /// leave forever-animations running off-screen, and it phase-locks to
    /// <see cref="GlowAnimationClock"/> so recycled rows resume mid-turn instead of snapping back to
    /// zero.
    ///
    /// It also carries the bright rim that hugs the subject's edge (<see cref="ShowRim"/>). The rim
    /// lives here rather than in each template so all the call sites pick it up from one place and it
    /// cannot drift out of alignment with the rays; unlike them it neither turns nor scales.
    /// </summary>
    public class RarityRayBurst : Panel
    {
        // Slow and fast ends of the rotation period, mapped from the shared 0-1
        // RarityGlowPulseSpeed setting. Deliberately not the pulse's own 10s-to-0.1s half-cycle
        // range, which reads as a blur rather than a rotation. Both ends are stretched together so
        // the whole slider turns slower rather than only its middle: at the default speed a
        // revolution takes about 38s.
        private const double SlowRotationSeconds = 72.0;
        private const double FastRotationSeconds = 5.0;

        // How far the burst may be stretched to follow a non-square slot's proportions. Matching them
        // exactly makes the rays look visibly pulled on wide or tall art, so past this the burst stays
        // this shape and simply does not reach the long edges.
        private const double MaxAspectStretch = 1.5;

        // Outward bloom on the rim. Tighter than the icon halo's 20px, so it hugs the edge instead of
        // washing back over the whole subject.
        private const double RimGlowBlurRadius = 9.0;

        private readonly RotateTransform _rotation = new RotateTransform();
        private readonly ScaleTransform _scale = new ScaleTransform();
        private readonly Image _rays;
        private readonly Border _rim;
        private PropertyChangedEventHandler _settingsHandler;

        public RarityRayBurst()
        {
            IsHitTestVisible = false;
            Focusable = false;

            // RenderTransform is deliberate: it scales the burst past its layout slot without
            // enlarging that slot, so the rays overflow the icon's cell by a fixed proportion at
            // every call site — grid cells and toast icons alike — with no per-site sizing and no
            // converter-computed dimensions. The host Grid already sets ClipToBounds="False".
            //
            // Rotation comes first and the scale second. The scale is what fits the burst to a
            // non-square slot, so applying it last keeps that envelope fixed and lets the rays sweep
            // through it; scaling first would instead tumble a squashed ellipse end over end.
            var transforms = new TransformGroup();
            transforms.Children.Add(_rotation);
            transforms.Children.Add(_scale);

            _rays = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transforms
            };

            _rim = new Border
            {
                Background = null,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed,

                // Bitmap-cached because the rim carries a DropShadowEffect and never moves. Without
                // the cache its effect is re-rasterized whenever this panel is redrawn — which the
                // rotating sibling makes happen every frame — and one uncached effect per visible row
                // is enough to stall a full grid. Caching is lossless here: the rim takes no transform,
                // so it rasterizes once at its real size.
                CacheMode = new BitmapCache()
            };

            Children.Add(_rays);
            Children.Add(_rim);

            RarityAppearanceHelper.BindRayGlowTiers(this, RayGlowTiersProperty);

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
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
        /// How far the burst renders beyond its layout slot, as a multiple of it. The default is
        /// tuned so the rays occupy about the same room as the soft glow they sit behind — on a 64px
        /// icon the long rays reach roughly 19px past the edge, against the soft glow's 20px blur.
        ///
        /// The reach is proportional to the slot and applied per axis, so a value that suits a square
        /// icon also keeps the rays clear of a wide or tall image. Completed game art still passes its
        /// own value, because it is much larger than an icon and its glow is a tighter bloom.
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

        /// <summary>
        /// Reports no desired size so the burst never drives layout. An Image measures to its source's
        /// natural size, which for this art is its 100x100 coordinate box — enough to inflate a 28px
        /// icon cell (and its whole DataGrid row) to 100px. Reporting zero leaves the subject to
        /// establish the cell; <see cref="ArrangeOverride"/> then stretches the layers to whatever that
        /// cell turned out to be.
        ///
        /// Children are measured at zero here, not at availableSize: a stretching Image reports
        /// whatever it is offered as its desired size, and Arrange will not then render it any
        /// smaller, so measuring against the offered space would lock the layers to the space the
        /// parent had free rather than the cell the subject settles on.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            var empty = new Size(0, 0);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(empty);
            }

            return empty;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            // Re-measured against the arranged cell, which is the first point the real size is known
            // and which bounds what Arrange will grant each child.
            _rays.Measure(finalSize);
            _rays.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));

            // The rim sits just outside the subject rather than over it, so it never eats into the
            // artwork's own edge pixels.
            var inset = Math.Max(0.0, RimThickness);
            var rimBounds = new Rect(
                -inset,
                -inset,
                Math.Max(0.0, finalSize.Width + (inset * 2.0)),
                Math.Max(0.0, finalSize.Height + (inset * 2.0)));
            _rim.Measure(new Size(rimBounds.Width, rimBounds.Height));
            _rim.Arrange(rimBounds);

            ApplyBurstScale(finalSize);
            return finalSize;
        }

        /// <summary>
        /// Which rarity tiers show the rays. Self-bound to the global setting in the constructor, so
        /// the call sites need no per-tier markup and changing the selection updates bursts already on
        /// screen. Ignored when <see cref="UseCompletedColors"/> is set, since completed art has no
        /// tier of its own.
        /// </summary>
        public static readonly DependencyProperty RayGlowTiersProperty =
            DependencyProperty.Register(
                nameof(RayGlowTiers), typeof(RaritySelection), typeof(RarityRayBurst),
                new PropertyMetadata(RaritySelection.None, OnArtSourceChanged));

        public RaritySelection RayGlowTiers
        {
            get => (RaritySelection)GetValue(RayGlowTiersProperty);
            set => SetValue(RayGlowTiersProperty, value);
        }

        /// <summary>
        /// Whether the bright rim is drawn tight around the subject's edge, for the extra emphasis a
        /// soft halo alone does not give. Off for surfaces whose subject is not a plain rectangle —
        /// completed game art, whose corners are rounded and whose proportions vary — where a straight
        /// rim would not follow the image.
        /// </summary>
        public static readonly DependencyProperty ShowRimProperty =
            DependencyProperty.Register(
                nameof(ShowRim), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(true, OnRimChanged));

        public bool ShowRim
        {
            get => (bool)GetValue(ShowRimProperty);
            set => SetValue(ShowRimProperty, value);
        }

        /// <summary>Rim line thickness. The rim is drawn just outside the subject, so it never eats
        /// into the artwork.</summary>
        public static readonly DependencyProperty RimThicknessProperty =
            DependencyProperty.Register(
                nameof(RimThickness), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(2.0, OnRimChanged));

        public double RimThickness
        {
            get => (double)GetValue(RimThicknessProperty);
            set => SetValue(RimThicknessProperty, value);
        }

        /// <summary>Corner rounding for the rim, for subjects that are not square-cornered.</summary>
        public static readonly DependencyProperty RimCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(RimCornerRadius), typeof(CornerRadius), typeof(RarityRayBurst),
                new PropertyMetadata(default(CornerRadius), OnRimChanged));

        public CornerRadius RimCornerRadius
        {
            get => (CornerRadius)GetValue(RimCornerRadiusProperty);
            set => SetValue(RimCornerRadiusProperty, value);
        }

        private static void OnArtSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as RarityRayBurst)?.ResolveArt();
        }

        private static void OnRimChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is RarityRayBurst burst)
            {
                burst.ResolveArt();
                burst.InvalidateArrange();
            }
        }

        private static void OnBurstScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            // The scale depends on the arranged slot, so recompute it there rather than inline.
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
            // Completed art carries no rarity of its own, so it is not filtered by tier; the call site
            // decides whether it shows at all.
            var tierSelected = UseCompletedColors || RayGlowTiers.Contains(Rarity);

            _rays.Source = !tierSelected
                ? null
                : UseCompletedColors
                    ? RarityAppearanceHelper.GetCompletedRayBurstImage()
                    : RarityAppearanceHelper.GetRayBurstImage(Rarity);

            ResolveRim();

            // A tier with no art (Common) has nothing to turn.
            if (_rays.Source == null)
            {
                StopRotation();
            }
            else if (IsActive && IsLoaded)
            {
                StartRotation();
            }
        }

        private void ResolveRim()
        {
            // The rim belongs to the ray effect, so it follows the same tier selection: a tier with
            // rays switched off gets no rim either.
            var brush = _rays.Source == null
                ? null
                : UseCompletedColors
                    ? RarityAppearanceHelper.GetCompletedRimBrush()
                    : RarityAppearanceHelper.GetRimBrush(Rarity);

            // No rim where there is no glow either: Common has neither, so the tiers stay distinct.
            if (!ShowRim || brush == null)
            {
                _rim.Visibility = Visibility.Collapsed;
                _rim.BorderBrush = null;
                _rim.Effect = null;
                return;
            }

            _rim.Visibility = Visibility.Visible;
            _rim.BorderBrush = brush;
            _rim.BorderThickness = new Thickness(Math.Max(0.0, RimThickness));
            _rim.CornerRadius = RimCornerRadius;

            // The rim's own outward bloom, on top of the halo the templates already draw. This is
            // what makes the edge read as lit rather than as a drawn outline.
            _rim.Effect = UseCompletedColors
                ? RarityAppearanceHelper.GetCompletedGlow(useEndColor: true)
                : RarityAppearanceHelper.GetGlow(Rarity, RimGlowBlurRadius);
        }

        /// <summary>
        /// Fits the burst to the arranged slot. The art is square and drawn with Uniform stretch, so
        /// inside a non-square slot it would otherwise shrink to the smaller side — on wide game-logo
        /// art that leaves the rays buried inside the image instead of reaching past it. Scaling each
        /// axis by that slot's own extent spreads the burst into an ellipse tracking the art's
        /// proportions, and reduces to a plain uniform scale whenever the slot is square, which is
        /// every icon site.
        ///
        /// The stretch is capped by <see cref="MaxAspectStretch"/>. Following the proportions exactly
        /// visibly distorts the rays on strongly non-square art — category art especially — so past
        /// that ratio the burst stops widening and simply sits inside the long axis instead.
        /// </summary>
        private void ApplyBurstScale(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            var longSide = Math.Max(finalSize.Width, finalSize.Height);
            if (side <= 0 || double.IsNaN(side) || double.IsInfinity(side))
            {
                return;
            }

            var scale = BurstScale;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            var stretch = Math.Min(longSide / side, MaxAspectStretch);
            var isWide = finalSize.Width >= finalSize.Height;

            _scale.ScaleX = scale * (isWide ? stretch : 1.0);
            _scale.ScaleY = scale * (isWide ? 1.0 : stretch);
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
            if (_rays.Source == null)
            {
                return;
            }

            var seconds = ResolveRotationSeconds();
            var cycleMilliseconds = seconds * 1000.0;
            var animation = new DoubleAnimation
            {
                From = 0.0,
                To = -360.0,
                Duration = new Duration(TimeSpan.FromSeconds(seconds)),
                RepeatBehavior = RepeatBehavior.Forever
            };

            // Phase-lock opt-outs start at angle zero, which keeps repeated captures identical.
            animation.BeginTime = PhaseLock
                ? GlowAnimationClock.PhaseLockBeginTime(cycleMilliseconds)
                : TimeSpan.Zero;

            _rotation.BeginAnimation(RotateTransform.AngleProperty, animation);
        }

        private void StopRotation()
        {
            _rotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _rotation.Angle = 0.0;
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
