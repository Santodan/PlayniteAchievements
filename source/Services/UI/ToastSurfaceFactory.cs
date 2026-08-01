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

            return control;
        }
    }
}
