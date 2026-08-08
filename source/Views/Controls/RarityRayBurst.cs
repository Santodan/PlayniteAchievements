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
    /// </summary>
    public class RarityRayBurst : Image
    {
        // Slow and fast ends of the rotation period, mapped from the shared 0-1
        // RarityGlowPulseSpeed setting. Deliberately not the pulse's own 10s-to-0.1s half-cycle
        // range, which reads as a blur rather than a rotation.
        private const double SlowRotationSeconds = 60.0;
        private const double FastRotationSeconds = 4.0;

        private readonly RotateTransform _rotation = new RotateTransform();
        private readonly ScaleTransform _scale = new ScaleTransform();
        private PropertyChangedEventHandler _settingsHandler;

        public RarityRayBurst()
        {
            Stretch = System.Windows.Media.Stretch.Uniform;
            IsHitTestVisible = false;
            Focusable = false;

            // RenderTransform is deliberate: it scales the burst past its layout slot without
            // enlarging that slot, so the rays overflow the icon's cell by a fixed proportion at
            // every call site — grid cells and toast icons alike — with no per-site sizing and no
            // converter-computed dimensions. The host Grid already sets ClipToBounds="False".
            RenderTransformOrigin = new Point(0.5, 0.5);
            var transforms = new TransformGroup();
            transforms.Children.Add(_scale);
            transforms.Children.Add(_rotation);
            RenderTransform = transforms;
            ApplyBurstScale();

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
        /// How far the burst renders beyond its layout slot, as a multiple of it. At the default the
        /// long rays reach roughly one icon-width past each edge.
        /// </summary>
        public static readonly DependencyProperty BurstScaleProperty =
            DependencyProperty.Register(
                nameof(BurstScale), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(2.8, OnBurstScaleChanged));

        public double BurstScale
        {
            get => (double)GetValue(BurstScaleProperty);
            set => SetValue(BurstScaleProperty, value);
        }

        /// <summary>
        /// Reports no desired size so the burst never drives layout. An Image measures to its
        /// source's natural size, which for this art is its 100x100 coordinate box — enough to
        /// inflate a 28px icon cell (and its whole DataGrid row) to 100px. Reporting zero leaves the
        /// icon to establish the cell; the Grid still arranges this Stretch child to the full cell,
        /// and the RenderTransform then scales the rays past it.
        /// </summary>
        protected override Size MeasureOverride(Size availableSize)
        {
            base.MeasureOverride(availableSize);
            return new Size(0, 0);
        }

        private static void OnArtSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as RarityRayBurst)?.ResolveArt();
        }

        private static void OnBurstScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            (d as RarityRayBurst)?.ApplyBurstScale();
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
            Source = UseCompletedColors
                ? RarityAppearanceHelper.GetCompletedRayBurstImage()
                : RarityAppearanceHelper.GetRayBurstImage(Rarity);

            // A tier with no art (Common) has nothing to turn.
            if (Source == null)
            {
                StopRotation();
            }
            else if (IsActive && IsLoaded)
            {
                StartRotation();
            }
        }

        private void ApplyBurstScale()
        {
            var scale = BurstScale;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            _scale.ScaleX = scale;
            _scale.ScaleY = scale;
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
            if (Source == null)
            {
                return;
            }

            var seconds = ResolveRotationSeconds();
            var cycleMilliseconds = seconds * 1000.0;
            var animation = new DoubleAnimation
            {
                From = 0.0,
                To = 360.0,
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
