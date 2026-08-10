using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Sunburst layer for the rays rarity glow style, sitting behind the soft halo. Drop it in as the
    /// first child of the same <c>ClipToBounds="False"</c> Grid that already holds a glow layer and a
    /// crisp front icon; it sizes and positions itself from that cell with no explicit dimensions.
    ///
    /// Every burst turns on one shared clock. A burst used to own its own rotation animation, and that
    /// is what made a populated grid lag: thirty visible rows meant thirty independent timelines, each
    /// ticking and dirtying its own layer. One animated transform shared by all of them is a single
    /// timeline for the whole application no matter how many bursts are on screen, and it has the side
    /// benefit that every burst turns in step. Beyond that the layer follows the soft glow's pattern —
    /// frozen art rasterized once into a bitmap cache — so a frame costs a matrix change on a cached
    /// texture rather than re-rendering the art.
    /// </summary>
    public class RarityRayBurst : Panel
    {
        // How far the burst may be stretched to follow a non-square slot's proportions. Matching them
        // exactly makes the rays look visibly pulled on wide or tall art, so past this the burst stays
        // this shape and simply does not reach the long edges.
        private const double MaxAspectStretch = 1.5;

        private readonly ScaleTransform _scale = new ScaleTransform();
        private readonly Image _rays;

        public RarityRayBurst()
        {
            IsHitTestVisible = false;
            Focusable = false;

            // Shared rotation first, then this element's own aspect correction, which is 1:1 for every
            // square icon. The reach comes from the arranged size instead (see ArrangeRays), so the
            // cache is rasterized at the size actually drawn rather than magnified and blurred.
            var transforms = new TransformGroup();
            transforms.Children.Add(RayBurstRotation.Transform);
            transforms.Children.Add(_scale);

            _rays = new Image
            {
                Stretch = System.Windows.Media.Stretch.Uniform,
                IsHitTestVisible = false,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = transforms,

                // Rasterized once, then composited under the transform each frame.
                CacheMode = new BitmapCache()
            };

            Children.Add(_rays);

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
        /// Which rarity tiers show the rays. Self-bound to the global setting in the constructor, so
        /// the call sites need no per-tier markup and changing the selection updates bursts already on
        /// screen. Ignored when <see cref="UseCompletedColors"/> is set, since completed art has no
        /// tier of its own and the call site gates it on the selection's completion entry.
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
        /// How far the burst reaches beyond its layout slot, as a multiple of it. The default is tuned
        /// so the rays occupy about the same room as the soft glow they sit behind — on a 64px icon the
        /// longest rays reach roughly 22px past the edge, against the soft glow's 20px blur.
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

        /// <summary>
        /// Reports no desired size so the burst never drives layout. An Image measures to its source's
        /// natural size, which for this art is its 100x100 coordinate box — enough to inflate a 28px
        /// icon cell (and its whole DataGrid row) to 100px. Reporting zero leaves the subject to
        /// establish the cell; <see cref="ArrangeOverride"/> then sizes the layer to whatever that cell
        /// turned out to be.
        ///
        /// Children are measured at zero here, not at availableSize: a stretching Image reports
        /// whatever it is offered as its desired size, and Arrange will not then render it any smaller,
        /// so measuring against the offered space would lock the layer to the space the parent had free
        /// rather than the cell the subject settles on.
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
            ArrangeRays(finalSize);
            return finalSize;
        }

        /// <summary>
        /// Arranges the ray layer at its full reach, centered on the subject, rather than arranging it
        /// to the cell and scaling it up — a scale would magnify the bitmap cache and blur the rays.
        /// The square side comes from the shorter axis, so the art stays circular and only the aspect
        /// correction is left to the transform.
        /// </summary>
        private void ArrangeRays(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            if (side <= 0 || double.IsNaN(side) || double.IsInfinity(side))
            {
                _rays.Arrange(new Rect(0, 0, 0, 0));
                return;
            }

            var scale = BurstScale;
            if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
            {
                scale = 1.0;
            }

            var reach = side * scale;
            _rays.Measure(new Size(reach, reach));
            _rays.Arrange(new Rect(
                (finalSize.Width - reach) / 2.0,
                (finalSize.Height - reach) / 2.0,
                reach,
                reach));

            ApplyAspectStretch(finalSize);
        }

        /// <summary>
        /// Stretches the burst along the slot's longer axis so it reaches past a wide or tall image
        /// rather than sitting inside it. This is 1:1 for every square icon, which is why the reach
        /// itself is applied as layout size instead: leaving only the aspect correction here means the
        /// icon sites take no scale at all, and so no cache blur.
        ///
        /// Capped by <see cref="MaxAspectStretch"/>, because following the proportions exactly visibly
        /// distorts the rays on strongly non-square art — category art especially.
        /// </summary>
        private void ApplyAspectStretch(Size finalSize)
        {
            var side = Math.Min(finalSize.Width, finalSize.Height);
            var longSide = Math.Max(finalSize.Width, finalSize.Height);
            if (side <= 0)
            {
                return;
            }

            var stretch = Math.Min(longSide / side, MaxAspectStretch);
            var isWide = finalSize.Width >= finalSize.Height;

            _scale.ScaleX = isWide ? stretch : 1.0;
            _scale.ScaleY = isWide ? 1.0 : stretch;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RarityAppearanceHelper.AppearanceChanged += OnAppearanceChanged;
            ResolveArt();
            RayBurstRotation.Ensure();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            RarityAppearanceHelper.AppearanceChanged -= OnAppearanceChanged;
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
        }
    }
}
