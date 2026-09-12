using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using System;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Projects a saved <see cref="ManualAchievementLink"/> onto the game's cached achievement data,
    /// so the unlock state the user just edited is visible to every surface that reads the cache
    /// rather than only after the next provider refresh.
    /// </summary>
    /// <remarks>
    /// The link is the source of truth and lives in the per-game custom data blob; this rewrites the
    /// derived copy. Both the manual-tracking window and the merged editor call it, so the two agree
    /// on what a saved link means without either owning the projection.
    /// </remarks>
    public sealed class ManualLinkCacheApplier
    {
        private readonly AchievementDataService _achievementDataService;
        private readonly ICacheManager _cacheManager;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public ManualLinkCacheApplier(
            AchievementDataService achievementDataService,
            ICacheManager cacheManager,
            PlayniteAchievementsSettings settings = null,
            ILogger logger = null)
        {
            _achievementDataService = achievementDataService;
            _cacheManager = cacheManager;
            _settings = settings;
            _logger = logger;
        }

        /// <summary>
        /// Rewrites the cached unlock state for <paramref name="playniteGameId"/> from
        /// <paramref name="link"/>. Returns false when there is nothing cached to rewrite.
        /// </summary>
        public bool Apply(Guid playniteGameId, ManualAchievementLink link, IManualSource source)
        {
            if (playniteGameId == Guid.Empty || link == null)
            {
                return false;
            }

            try
            {
                var cachedData = _achievementDataService?.GetRawGameAchievementData(playniteGameId);
                if (cachedData?.Achievements == null)
                {
                    return false;
                }

                // Keep the display platform in sync so the game attributes to its real platform
                // (e.g. Steam/PSN) instead of "Manual" right after an edit, without waiting for a
                // full provider refresh. Resolves through the same helper the provider uses, so a
                // user override applies here too.
                cachedData.ProviderPlatformKey = ManualDisplayPlatformResolver.Resolve(source, link);

                // Reset then re-apply through the shared resolver, so the cached unlocked set
                // exactly reflects the just-saved link (and never carries stale unlocks).
                foreach (var achievement in cachedData.Achievements)
                {
                    if (achievement == null)
                    {
                        continue;
                    }

                    achievement.Unlocked = false;
                    achievement.UnlockTimeUtc = null;
                }

                new ManualUnlockResolver(link).ApplyUnlockState(cachedData.Achievements);

                // Force a new snapshot version so theme update coalescing does not skip this save.
                cachedData.LastUpdatedUtc = DateTime.UtcNow;

                _cacheManager?.SaveGameData(playniteGameId.ToString(), cachedData);
                _cacheManager?.NotifyCacheInvalidated(new[] { playniteGameId });

                // Ensure immediate theme refresh for this game after manual edits.
                if (_settings?.SelectedGame?.Id == playniteGameId)
                {
                    PlayniteAchievementsPlugin.Instance?.ThemeUpdateService?.RequestUpdate(playniteGameId);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed applying manual link to cached data for game {playniteGameId}.");
                return false;
            }
        }
    }
}
