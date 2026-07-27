using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Refresh;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal sealed class HoyoverseScanner
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly HoyoverseSettings _providerSettings;
        private readonly IHoyoverseDefinitionClient _definitionClient;

        public HoyoverseScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            HoyoverseSettings providerSettings,
            string pluginUserDataPath,
            IHoyoverseDefinitionClient definitionClient = null)
        {
            _logger = logger;
            _settings = settings;
            _providerSettings = providerSettings ?? new HoyoverseSettings();
            _definitionClient = definitionClient ?? new HoyoverseDefinitionClient(null, logger, pluginUserDataPath);
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var targets = (gamesToRefresh ?? new List<Game>())
                .Where(game => HoyoverseGameCatalog.TryResolve(game, _providerSettings, out _))
                .ToList();

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                targets,
                onGameStarting,
                async (game, token) =>
                {
                    var data = await BuildGameDataAsync(game, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, _) =>
                {
                    _logger?.Warn(ex, $"[HoYoverse] Failed to refresh '{game?.Name}'.");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        private async Task<GameAchievementData> BuildGameDataAsync(Game game, CancellationToken cancel)
        {
            if (!HoyoverseGameCatalog.TryResolve(game, _providerSettings, out var kind))
            {
                return null;
            }

            var language = _settings?.Persisted?.GlobalLanguage ?? "english";
            var definitions = await _definitionClient.GetDefinitionsAsync(kind, language, cancel).ConfigureAwait(false)
                ?? new List<AchievementDetail>();

            var achievements = definitions
                .Where(detail => detail != null && !string.IsNullOrWhiteSpace(detail.ApiName))
                .Select(CloneLockedDefinition)
                .ToList();

            var exportPath = HoyoverseGameCatalog.GetExportPath(kind, _providerSettings);
            var unlockedIds = HoyoverseExportParser.ReadUnlockedIds(kind, exportPath, achievements, _logger);
            if (unlockedIds.Count > 0)
            {
                foreach (var achievement in achievements)
                {
                    if (unlockedIds.Contains(achievement.ApiName))
                    {
                        achievement.Unlocked = true;
                    }
                }
            }

            return new GameAchievementData
            {
                ProviderKey = HoyoverseDataProvider.Key,
                GameName = game?.Name ?? HoyoverseGameCatalog.GetCanonicalName(kind),
                AppId = HoyoverseGameCatalog.GetAppId(kind),
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                PlayniteGameId = game?.Id,
                Game = game,
                HasAchievements = achievements.Count > 0,
                Achievements = achievements
            };
        }

        private static AchievementDetail CloneLockedDefinition(AchievementDetail source)
        {
            return new AchievementDetail
            {
                ApiName = source.ApiName,
                DisplayName = source.DisplayName,
                Description = source.Description,
                UnlockedIconPath = source.UnlockedIconPath,
                LockedIconPath = source.LockedIconPath,
                Points = source.Points,
                ScaledPoints = source.ScaledPoints,
                CategoryType = source.CategoryType,
                Category = source.Category,
                TrophyType = source.TrophyType,
                IsCapstone = source.IsCapstone,
                Hidden = source.Hidden,
                GlobalPercentUnlocked = null,
                Rarity = source.Rarity,
                ProgressNum = source.ProgressNum,
                ProgressDenom = source.ProgressDenom,
                Unlocked = false,
                UnlockTimeUtc = null
            };
        }
    }
}
