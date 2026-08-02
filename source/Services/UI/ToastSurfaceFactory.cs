using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
    }
}
