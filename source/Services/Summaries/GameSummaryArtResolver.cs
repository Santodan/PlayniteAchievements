using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Images;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Summaries
{
    // Resolves the image a game summary item should display when the user selected a
    // category as the game's summary art: override art first, then provider-default
    // art, then null so callers fall back to the Playnite icon/cover.
    internal static class GameSummaryArtResolver
    {
        // Set by the plugin at startup. An accessor rather than a direct plugin reference keeps
        // this file compilable in the test project, which does not include the plugin entry point.
        internal static Func<ManagedCustomIconService> ManagedCustomIconServiceAccessor { get; set; }

        public static string Resolve(
            Guid? playniteGameId,
            GameSummaryCategoryData selection,
            IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides)
        {
            if (selection == null || string.IsNullOrWhiteSpace(selection.Label))
            {
                return null;
            }

            CategoryImageOverrideData imageOverride = null;
            imageOverrides?.TryGetValue(selection.Label, out imageOverride);

            return ResolveOverrideArtPath(imageOverride?.Art, playniteGameId) ??
                   CategoryDefaultImageResolver.Resolve(
                       playniteGameId,
                       string.IsNullOrWhiteSpace(selection.ProviderLabel) ? selection.Label : selection.ProviderLabel);
        }

        /// <summary>
        /// Fast-path variant for projection loops without hydrated data: one cached
        /// custom-data store load per game, returning null immediately for games
        /// without a summary category selection.
        /// </summary>
        public static string ResolveForGame(Guid? playniteGameId, GameCustomDataStore store = null)
        {
            if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
            {
                return null;
            }

            if (!GameCustomDataLookup.TryGetGameSummaryCategory(
                playniteGameId.Value,
                out var selection,
                out var imageOverrides,
                store))
            {
                return null;
            }

            return Resolve(playniteGameId, selection, imageOverrides);
        }

        private static string ResolveOverrideArtPath(string value, Guid? playniteGameId)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var managedCustomIconService = ManagedCustomIconServiceAccessor?.Invoke();
            var resolved = playniteGameId.HasValue && managedCustomIconService != null
                ? managedCustomIconService.ResolveManagedDisplayPath(normalized, playniteGameId.Value.ToString("D")) ?? normalized
                : normalized;
            // Category graphics are overwritten in place at a stable managed path, so the
            // display path needs a cache-bust token or stale bitmaps are served after replacement.
            return AchievementIconResolver.ApplyCacheBust(resolved);
        }
    }
}
