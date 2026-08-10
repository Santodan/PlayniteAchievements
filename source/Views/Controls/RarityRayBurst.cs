using System;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// The layer behind an achievement icon that carries the rays glow style. Currently draws nothing:
    /// the previous sunburst was removed so the effect can be designed again from scratch, and this is
    /// the seam it goes back into.
    ///
    /// Everything around it is still wired up, so a new implementation needs no changes elsewhere. Each
    /// call site already places this as the first child of a <c>ClipToBounds="False"</c> Grid that also
    /// holds the soft glow layer and the crisp icon, gated on the same conditions as the glow; the tier
    /// selection reaches it through <see cref="RayGlowTiers"/>; <see cref="IsActive"/> follows the
    /// global animation toggle; and the notification surfaces bind <see cref="PhaseLock"/> so captures
    /// can be made deterministic. Art comes from
    /// <see cref="RarityAppearanceHelper.GetRayBurstImage"/>, which returns nothing for now.
    ///
    /// Two findings from the removed attempts are worth not rediscovering. A layer that turns must not
    /// be bitmap-cached: WPF re-rasterizes a cache whenever the element's transform changes, so caching
    /// a rotating layer costs a full re-rasterization per row per frame and was what made a populated
    /// grid lag. And an animation must not be attached to rows that draw nothing, whether because their
    /// tier is unselected or because the art is absent — a shared transform invalidates every element
    /// referencing it on every tick, drawing or not.
    /// </summary>
    public class RarityRayBurst : Panel
    {
        public RarityRayBurst()
        {
            IsHitTestVisible = false;
            Focusable = false;

            RarityAppearanceHelper.BindRayGlowTiers(this, RayGlowTiersProperty);
        }

        /// <summary>Rarity tier whose color the rays take.</summary>
        public static readonly DependencyProperty RarityProperty =
            DependencyProperty.Register(
                nameof(Rarity), typeof(RarityTier), typeof(RarityRayBurst),
                new PropertyMetadata(RarityTier.Common));

        public RarityTier Rarity
        {
            get => (RarityTier)GetValue(RarityProperty);
            set => SetValue(RarityProperty, value);
        }

        /// <summary>
        /// When true the rays take the completed-game gradient colors instead of a rarity tier, for the
        /// completion glow on game and category art. Completed art has no tier of its own, so the call
        /// site gates it on the selection's completion entry rather than on <see cref="RayGlowTiers"/>.
        /// </summary>
        public static readonly DependencyProperty UseCompletedColorsProperty =
            DependencyProperty.Register(
                nameof(UseCompletedColors), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false));

        public bool UseCompletedColors
        {
            get => (bool)GetValue(UseCompletedColorsProperty);
            set => SetValue(UseCompletedColorsProperty, value);
        }

        /// <summary>
        /// Which rarity tiers show the rays. Self-bound to the global setting in the constructor, so the
        /// call sites need no per-tier markup and changing the selection reaches layers already on
        /// screen.
        /// </summary>
        public static readonly DependencyProperty RayGlowTiersProperty =
            DependencyProperty.Register(
                nameof(RayGlowTiers), typeof(RaritySelection), typeof(RarityRayBurst),
                new PropertyMetadata(RaritySelection.None));

        public RaritySelection RayGlowTiers
        {
            get => (RaritySelection)GetValue(RayGlowTiersProperty);
            set => SetValue(RayGlowTiersProperty, value);
        }

        /// <summary>
        /// Whether the effect animates. A style trigger sets this from the global AnimateRarityGlows
        /// toggle, so an implementation should render either way and only animate while it is set.
        /// </summary>
        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(
                nameof(IsActive), typeof(bool), typeof(RarityRayBurst),
                new PropertyMetadata(false));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        /// <summary>
        /// When true (default) any animation should phase-lock to the shared
        /// <see cref="Helpers.GlowAnimationClock"/> so recreated elements resume mid-cycle. The
        /// notification surfaces bind this to IsPreview and opt out, so every captured wave starts from
        /// the same point.
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
        /// How far the effect may reach beyond its layout slot, as a multiple of it. Completed game art
        /// passes its own value, being much larger than an icon.
        /// </summary>
        public static readonly DependencyProperty BurstScaleProperty =
            DependencyProperty.Register(
                nameof(BurstScale), typeof(double), typeof(RarityRayBurst),
                new PropertyMetadata(1.9));

        public double BurstScale
        {
            get => (double)GetValue(BurstScaleProperty);
            set => SetValue(BurstScaleProperty, value);
        }

        /// <summary>
        /// Reports no desired size, so this layer never drives layout however large it draws. Keep this:
        /// the subject has to be what establishes the cell. An Image measuring to its source's natural
        /// size was enough to inflate a 28px icon cell — and its whole DataGrid row — to the art's own
        /// dimensions.
        ///
        /// Children are measured at zero rather than at availableSize for the same reason: a stretching
        /// child reports whatever it is offered as its desired size, and Arrange will not then render it
        /// any smaller, so it would end up sized to the space the parent had free rather than to the
        /// cell the subject settles on. Measure children against the final size in ArrangeOverride
        /// instead.
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
            var bounds = new Rect(0, 0, finalSize.Width, finalSize.Height);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(finalSize);
                child.Arrange(bounds);
            }

            return finalSize;
        }
    }
}
