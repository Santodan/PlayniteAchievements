using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The rarity-colored halo around the notification card, drawn as a generated bitmap.
    ///
    /// It replaced the border DropShadowEffect because an effect this wide bands: 8-bit
    /// composition turns the smooth falloff into 2-3 px flat rings that show on dark
    /// backgrounds, and an effect cannot dither (see RarityAppearanceHelper.HaloGlow for the
    /// measurement). The bitmap is the same falloff computed in float and dithered at the
    /// quantization step. Two things come with the change: the glow pulse animates this
    /// element's Opacity, which is compositor-only work instead of the per-frame effect
    /// re-blur the slide had to pause; and the capture pipeline treats this control like an
    /// effect — hidden for the per-tick renders, baked once into the shadow difference layer,
    /// its Opacity driving the per-sample glow scale.
    ///
    /// Layout mirrors <see cref="RarityRayBurst"/>: the control fills the card's slot and
    /// draws past it by the glow margin; the toast window's ToastGlowMargin reserves that
    /// room, so nothing here must be clipped or bitmap-cached.
    /// </summary>
    public class RarityBorderHalo : FrameworkElement
    {
        private BitmapSource _halo;
        private bool _appearanceHooked;

        public RarityBorderHalo()
        {
            IsHitTestVisible = false;
            Focusable = false;

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>Rarity tier whose color the halo takes. Common draws nothing, matching the
        /// effect it replaced (GetGlow returned null for Common).</summary>
        public static readonly DependencyProperty RarityProperty =
            DependencyProperty.Register(
                nameof(Rarity), typeof(RarityTier), typeof(RarityBorderHalo),
                new FrameworkPropertyMetadata(RarityTier.Common, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

        public RarityTier Rarity
        {
            get => (RarityTier)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        /// <summary>
        /// When true the halo takes the completed-game gradient end color instead of a rarity
        /// tier, for completion notifications — the same color the border glow effect used.
        /// </summary>
        public static readonly DependencyProperty UseCompletedColorsProperty =
            DependencyProperty.Register(
                nameof(UseCompletedColors), typeof(bool), typeof(RarityBorderHalo),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

        public bool UseCompletedColors
        {
            get => (bool)GetValue(UseCompletedColorsProperty);
            set => SetValue(UseCompletedColorsProperty, value);
        }

        /// <summary>
        /// The falloff's reach in DIPs, matching the BlurRadius of the effect it replaced.
        /// The drawn margin adds the same 6 DIP of slack the view model's ToastGlowMargin
        /// derives from it, so the halo always fits the room the window reserves.
        /// </summary>
        public static readonly DependencyProperty GlowRadiusProperty =
            DependencyProperty.Register(
                nameof(GlowRadius), typeof(double), typeof(RarityBorderHalo),
                new FrameworkPropertyMetadata(36.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

        public double GlowRadius
        {
            get => (double)GetValue(GlowRadiusProperty);
            set => SetValue(GlowRadiusProperty, value);
        }

        /// <summary>Corner radius of the card the halo wraps. Kept in step with the card
        /// Border's CornerRadius in the toast template.</summary>
        public static readonly DependencyProperty CardCornerRadiusProperty =
            DependencyProperty.Register(
                nameof(CardCornerRadius), typeof(double), typeof(RarityBorderHalo),
                new FrameworkPropertyMetadata(8.0, FrameworkPropertyMetadataOptions.AffectsRender, OnAppearanceChanged));

        public double CardCornerRadius
        {
            get => (double)GetValue(CardCornerRadiusProperty);
            set => SetValue(CardCornerRadiusProperty, value);
        }

        // Matches the slack ToastGlowMargin adds over the glow radius.
        private const double MarginSlack = 6.0;

        /// <summary>
        /// The bitmap the last render drew, for the capture pipeline's shadow-layer signature:
        /// a changed instance means the baked layer is stale.
        /// </summary>
        internal ImageSource CurrentHalo => _halo;

        private static void OnAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((RarityBorderHalo)d)._halo = null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_appearanceHooked)
            {
                RarityAppearanceHelper.AppearanceChanged += OnGlobalAppearanceChanged;
                _appearanceHooked = true;
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_appearanceHooked)
            {
                RarityAppearanceHelper.AppearanceChanged -= OnGlobalAppearanceChanged;
                _appearanceHooked = false;
            }
        }

        private void OnGlobalAppearanceChanged(object sender, EventArgs e)
        {
            _halo = null;
            InvalidateVisual();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _halo = null;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var width = RenderSize.Width;
            var height = RenderSize.Height;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (!UseCompletedColors && Rarity == RarityTier.Common)
            {
                return;
            }

            if (_halo == null)
            {
                var color = UseCompletedColors
                    ? RarityAppearanceHelper.GetCompletedEndColor()
                    : RarityAppearanceHelper.GetBaseColor(Rarity);
                _halo = RarityAppearanceHelper.GetBorderHaloBitmap(
                    color, width, height, CardCornerRadius, GlowRadius, GlowRadius + MarginSlack);
            }

            if (_halo == null)
            {
                return;
            }

            var margin = GlowRadius + MarginSlack;
            drawingContext.DrawImage(
                _halo,
                new Rect(-margin, -margin, width + (2 * margin), height + (2 * margin)));
        }
    }
}
