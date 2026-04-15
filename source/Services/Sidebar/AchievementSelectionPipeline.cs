using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.Sidebar
{
    internal sealed class AchievementSelectionPipeline
    {
        private const int MaxCachedGames = 24;
        private static readonly (List<AchievementDisplayItem> Items, bool HasCustomOrder) EmptyResult =
            (new List<AchievementDisplayItem>(), false);

        private readonly AchievementDataService _achievementDataService;
        private readonly PlayniteAchievementsSettings _settings;

        private readonly object _cacheSync = new object();
        private readonly Dictionary<Guid, (List<AchievementDisplayItem> Items, bool HasCustomOrder)> _cache =
            new Dictionary<Guid, (List<AchievementDisplayItem> Items, bool HasCustomOrder)>();

        public AchievementSelectionPipeline(
            AchievementDataService achievementDataService,
            PlayniteAchievementsSettings settings)
        {
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _settings = settings;
        }

        public void InvalidateAll()
        {
            lock (_cacheSync)
            {
                _cache.Clear();
            }
        }

        public void Invalidate(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            lock (_cacheSync)
            {
                _cache.Remove(gameId);
            }
        }

        public bool TryGetCached(Guid gameId, out (List<AchievementDisplayItem> Items, bool HasCustomOrder) result)
        {
            lock (_cacheSync)
            {
                if (_cache.TryGetValue(gameId, out var cached) && cached.Items != null)
                {
                    result = CloneResult(cached);
                    return true;
                }
            }

            result = EmptyResult;
            return false;
        }

        public async Task<(List<AchievementDisplayItem> Items, bool HasCustomOrder)> LoadAsync(
            Guid gameId,
            ISet<string> revealedKeys,
            CancellationToken cancellationToken)
        {
            if (gameId == Guid.Empty)
            {
                return EmptyResult;
            }

            if (TryGetCached(gameId, out var cached))
            {
                return cached;
            }

            var loaded = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                var gameData = _achievementDataService.GetGameAchievementData(gameId);
                if (gameData?.Achievements == null)
                {
                    return EmptyResult;
                }

                var appearanceSettings = AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _settings,
                    gameId,
                    gameData.UseSeparateLockedIconsWhenAvailable);

                var achievements = new List<AchievementDisplayItem>(gameData.Achievements.Count);
                foreach (var ach in gameData.Achievements)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var item = AchievementDisplayItem.Create(
                        gameData,
                        ach,
                        _settings,
                        revealedKeys,
                        gameId,
                        appearanceSettings);
                    if (item != null)
                    {
                        achievements.Add(item);
                    }
                }

                var hasCustomOrder = gameData.AchievementOrder != null && gameData.AchievementOrder.Count > 0;
                var orderedItems = hasCustomOrder
                    ? AchievementOrderHelper.ApplyOrder(
                        achievements,
                        a => a.ApiName,
                        gameData.AchievementOrder)
                    : AchievementGridSortHelper.CreateDefaultSortedList(
                        achievements,
                        AchievementGridSortScope.GameAchievements);

                return (orderedItems, hasCustomOrder);
            }, cancellationToken).ConfigureAwait(false);

            var cloned = CloneResult(loaded);
            lock (_cacheSync)
            {
                _cache[gameId] = cloned;
                if (_cache.Count > MaxCachedGames)
                {
                    _cache.Clear();
                    _cache[gameId] = cloned;
                }
            }

            return CloneResult(cloned);
        }

        private static (List<AchievementDisplayItem> Items, bool HasCustomOrder) CloneResult(
            (List<AchievementDisplayItem> Items, bool HasCustomOrder) source)
        {
            if (source.Items == null)
            {
                return EmptyResult;
            }

            return (new List<AchievementDisplayItem>(source.Items), source.HasCustomOrder);
        }
    }
}