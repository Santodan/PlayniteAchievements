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
        // Visible gap (DIP) between stacked cards in a wave. Without adjustment the gap is the sum
        // of the two touching cards' ToastGlowMargins (2 * glow), which reads as too far apart; a
        // negative container margin collapses that reserved glow room (translucent glows blend) to
        // this small gap. Tunable.
        private const double DesiredCardGapDip = 8d;

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

            // A multi-card wave stacks cards in the default vertical StackPanel; the inter-card gap
            // is the sum of both cards' ToastGlowMargins. Pull every card after the first up by that
            // doubled glow room minus the desired gap, so bodies sit DesiredCardGapDip apart while
            // the first card's top and last card's bottom keep full glow room (no outer clipping).
            // A single-item wave and the inline preview keep the untouched natural layout.
            if (items.Count > 1)
            {
                var glow = items[0].ToastGlowMargin.Top; // uniform; every card in a wave shares one style
                var pull = DesiredCardGapDip - (2 * glow);

                control.AlternationCount = items.Count; // assigns AlternationIndex per container
                var containerStyle = new Style(typeof(ContentPresenter));
                containerStyle.Setters.Add(
                    new Setter(FrameworkElement.MarginProperty, new Thickness(0, pull, 0, 0)));

                var firstCard = new Trigger
                {
                    Property = ItemsControl.AlternationIndexProperty,
                    Value = 0,
                };
                firstCard.Setters.Add(
                    new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
                containerStyle.Triggers.Add(firstCard);

                control.ItemContainerStyle = containerStyle;
            }

            return control;
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
        /// Layout rounding and device-pixel snapping are turned off for the same reason the slide moved
        /// off <c>SetWindowPos</c>: both quantise the rendered position to whole pixels, which is the
        /// sub-pixel precision this is here to gain.
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
                UseLayoutRounding = false,
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
