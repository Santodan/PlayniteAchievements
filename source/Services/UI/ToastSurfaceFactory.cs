using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Single construction point for the achievement-toast surface. The live toast wave and the
    /// settings inline preview both build their surface here so the two cannot drift: same template
    /// decision, same host element. Fit-scale and window layout-rounding are intentionally NOT
    /// applied here -- the live toast applies fit-scale against its host window; the inline preview
    /// renders at natural size (its result is the reference the toast is expected to match).
    /// </summary>
    internal static class ToastSurfaceFactory
    {
        /// <summary>
        /// Visible gap (DIP) between the bodies of two stacked cards in a wave. Without adjustment
        /// the gap is the sum of the transparent room the two touching cards reserve around
        /// themselves, which reads as too far apart; a negative container margin collapses that
        /// room (translucent glows blend) to this small gap. Tunable.
        /// </summary>
        public const double DesiredCardGapDip = 8d;

        /// <summary>
        /// The one template decision shared by the wave and the preview: a fire-test view model
        /// carries a forced <see cref="AchievementToastViewModel.PreviewTemplateSource"/> (plugin
        /// style or a theme A/B override) and resolves through
        /// <see cref="AchievementToastTemplateResolver.ResolvePreviewTemplate"/>; a real unlock and
        /// the inline mockup carry none and resolve through
        /// <see cref="AchievementToastTemplateResolver.ResolveTemplate"/>.
        /// </summary>
        public static DataTemplate ResolveToastTemplate(
            AchievementToastTemplateResolver resolver,
            IReadOnlyList<AchievementToastViewModel> items,
            bool themeStylingEnabled,
            string providerKey,
            Guid scopeGameId)
        {
            var previewSource = items
                .Select(vm => vm.PreviewTemplateSource)
                .FirstOrDefault(source => source.HasValue);

            return previewSource.HasValue
                ? resolver.ResolvePreviewTemplate(previewSource.Value, isFrame: false, providerKey, scopeGameId)
                : resolver.ResolveTemplate(themeStylingEnabled, providerKey, scopeGameId);
        }

        /// <summary>
        /// Builds the host element for one or more toast cards. A wave stacks several cards; the
        /// inline preview passes a single-item list, which renders identically.
        /// </summary>
        public static ItemsControl BuildToastSurface(
            IReadOnlyList<AchievementToastViewModel> items,
            DataTemplate itemTemplate)
        {
            var control = new ItemsControl
            {
                ItemsSource = items,
                IsHitTestVisible = false,
            };

            if (itemTemplate != null)
            {
                control.ItemTemplate = itemTemplate;
            }

            // A multi-card wave stacks cards in the default vertical StackPanel. The inter-card gap
            // is whatever transparent room the two touching cards reserve, which is a property of
            // the template and cannot be known before layout; ApplyMeasuredCardGaps collapses it
            // once the containers are realized. A single-item wave and the inline preview keep the
            // untouched natural layout.
            return control;
        }

        /// <summary>
        /// The transparent room a realized card reserves inside its container: the offset of the
        /// template's root element on each side. For the bundled template this is
        /// <c>ToastGlowMargin</c>, the room the border glow and the ray burst reach into; for a
        /// theme template it is whatever margin that template's root carries.
        ///
        /// Measured rather than read off the view model because only the bundled template binds
        /// <c>ToastGlowMargin</c>. Deriving the layout from a value the active template may simply
        /// ignore is what let a theme card be pulled into the one above it.
        ///
        /// Taken from the root's arranged rect (layout offset and size), not its rendered bounds.
        /// A theme template may animate its own root with a RenderTransform (a slide, a pop); read
        /// through that transform, a card measured mid-entry reports its start position as reserved
        /// room and the corner placement shifts the window inward by the animation's travel.
        ///
        /// An empty thickness when the container is not realized or has no visual child — every
        /// caller then falls back to "no reserved room", which is the natural layout.
        /// </summary>
        public static Thickness MeasureCardInset(FrameworkElement container)
        {
            if (container == null ||
                container.RenderSize.Width <= 0 || container.RenderSize.Height <= 0 ||
                VisualTreeHelper.GetChildrenCount(container) == 0)
            {
                return default(Thickness);
            }

            var root = VisualTreeHelper.GetChild(container, 0) as FrameworkElement;
            if (root == null || root.RenderSize.Width <= 0 || root.RenderSize.Height <= 0)
            {
                return default(Thickness);
            }

            var offset = VisualTreeHelper.GetOffset(root);
            var bounds = new Rect(new Point(offset.X, offset.Y), root.RenderSize);
            return new Thickness(
                bounds.Left,
                bounds.Top,
                container.RenderSize.Width - bounds.Right,
                container.RenderSize.Height - bounds.Bottom);
        }

        /// <summary>
        /// Collapses the reserved room between stacked cards to <see cref="DesiredCardGapDip"/>,
        /// measured from the cards themselves: every container after the first is pulled up by the
        /// room its own top and its predecessor's bottom reserve, less the desired gap. The first
        /// card's top and the last card's bottom keep their full room, so nothing outside the
        /// bodies is clipped and the corner inset stays derivable from the same measurement.
        ///
        /// Runs after the surface has been laid out, since the room is a laid-out quantity. One
        /// pass converges: a container's own Margin sits outside the inset it was derived from, so
        /// re-measuring after the pull would return the same numbers.
        ///
        /// Returns the outer inset of the whole stack — the first card's left/top and the last
        /// card's right/bottom — which is what the corner placement insets by.
        /// </summary>
        public static Thickness ApplyMeasuredCardGaps(ItemsControl surface)
        {
            if (surface == null || surface.Items.Count == 0)
            {
                return default(Thickness);
            }

            var containers = new FrameworkElement[surface.Items.Count];
            var insets = new Thickness[surface.Items.Count];
            for (var i = 0; i < containers.Length; i++)
            {
                containers[i] = surface.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
                insets[i] = MeasureCardInset(containers[i]);
            }

            for (var i = 1; i < containers.Length; i++)
            {
                if (containers[i] == null)
                {
                    continue;
                }

                containers[i].Margin = new Thickness(
                    0, DesiredCardGapDip - (insets[i - 1].Bottom + insets[i].Top), 0, 0);
            }

            if (containers.Length > 1)
            {
                surface.UpdateLayout();
            }

            var first = insets[0];
            var last = insets[insets.Length - 1];
            return new Thickness(first.Left, first.Top, last.Right, last.Bottom);
        }

        /// <summary>
        /// Wraps the card surface in the element the slide animates. The live toast slides by
        /// translating this host inside a stationary window; the settings inline preview does not move
        /// and keeps using the bare surface.
        ///
        /// The transform sits here, outside the surface, on purpose. The surface carries the fit and DPI
        /// compensation as a <c>LayoutTransform</c>, and a <c>RenderTransform</c> on the same element
        /// composes inside that scale — so an identical translate would travel a different distance at
        /// every display scale. On the host it is plain window DIPs.
        ///
        /// Device-pixel snapping is off for the same reason the slide moved off <c>SetWindowPos</c>: it
        /// quantises the rendered position to whole pixels, which is the sub-pixel precision this is
        /// here to gain.
        ///
        /// Layout rounding is deliberately left ON, unlike snapping. It rounds measure/arrange results,
        /// not render-time output, and the slide is a <c>RenderTransform</c> on this host — applied
        /// after layout — so it keeps its sub-pixel travel either way. Turning rounding off here turned
        /// it off for the whole card, because the property inherits: every text element laid out on
        /// fractional device pixels, which reads as blurry type for the seconds the card sits at rest.
        /// The two rows that genuinely need unrounded layout (a clamped description whose last line the
        /// rounded height would shave) carry their own <c>UseLayoutRounding="False"</c>.
        /// </summary>
        public static Grid BuildSlideHost(ItemsControl surface, out TranslateTransform slide)
        {
            // A group rather than the bare translate, so a theme storyboard can animate a scale
            // (a pop or a shrink-away) alongside — or instead of — the slide. The order is fixed and
            // the plugin's slide is index 1; see ToastNotificationService.SlideTargetPath.
            slide = new TranslateTransform();
            var transforms = new TransformGroup();
            transforms.Children.Add(new ScaleTransform(1d, 1d));
            transforms.Children.Add(slide);

            var host = new Grid
            {
                IsHitTestVisible = false,
                UseLayoutRounding = true,
                SnapsToDevicePixels = false,
                RenderTransform = transforms,
                RenderTransformOrigin = new Point(0.5, 0.5),
            };

            if (surface != null)
            {
                host.Children.Add(surface);
            }

            return host;
        }

        /// <summary>
        /// Reserves <paramref name="travelDip"/> of empty room past the card on the side the card slides
        /// in from, so the window is large enough to contain it at both ends of the slide. An HWND clips
        /// its content unconditionally, so without this the card is simply cut off mid-slide.
        ///
        /// The room is transparent and hit-test-invisible like the rest of the window, and the card's
        /// resting offset inside the window becomes the top pad — which placement reads back by
        /// measurement rather than recomputing (see <c>ToastWindowPlacer.TryMeasureCardPhysical</c>).
        /// </summary>
        public static void ApplySlideTravel(ItemsControl surface, double travelDip, bool fromBottom)
        {
            if (surface == null || double.IsNaN(travelDip) || travelDip <= 0)
            {
                return;
            }

            surface.Margin = fromBottom
                ? new Thickness(0, 0, 0, travelDip)
                : new Thickness(0, travelDip, 0, 0);
        }
    }
}
