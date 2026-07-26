using System;
using System.Collections.Generic;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using Playnite.SDK;

namespace PlayniteAchievements.Services.Summaries
{
    /// <summary>
    /// Builds a single <see cref="GameSummaryItem"/> from one game's achievement data.
    /// Depends only on the providers list and Playnite presentation, so any surface
    /// (Overview, Start Page, View Achievements) can produce a summary row without
    /// coupling to the Overview aggregation pipeline.
    /// </summary>
    public sealed class GameSummaryItemBuilder
    {
        private sealed class GamePresentation
        {
            public string DisplayName { get; set; }
            public string SortingName { get; set; }
            public string IconPath { get; set; }
            public string CoverPath { get; set; }
            public DateTime? LastPlayed { get; set; }
            public string PlatformText { get; set; }
            public IReadOnlyList<string> Platforms { get; set; }
            public string RegionText { get; set; }
            public ulong PlaytimeSeconds { get; set; }
            public Playnite.SDK.Models.Game Game { get; set; }
        }

        private readonly IReadOnlyList<IDataProvider> _providers;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private Dictionary<string, (string iconKey, string colorHex)> _providerLookup;

        public GameSummaryItemBuilder(
            IReadOnlyList<IDataProvider> providers,
            IPlayniteAPI playniteApi,
            ILogger logger)
        {
            _providers = providers ?? new List<IDataProvider>();
            _playniteApi = playniteApi;
            _logger = logger;
        }

        /// <summary>
        /// Projects a single game's data into a <see cref="GameSummaryItem"/>.
        /// Returns null when the game is excluded from summaries or has no achievements
        /// (unless <paramref name="allowEmpty"/> is true, in which case a minimal
        /// zero-count item is returned so a single-game surface can still show the row).
        /// </summary>
        public GameSummaryItem Build(
            GameAchievementData gameData,
            PlayniteAchievementsSettings settings,
            bool allowEmpty = false)
        {
            if (gameData == null || gameData.ExcludedFromSummaries)
            {
                return null;
            }

            var hasAchievements = gameData.Achievements != null &&
                                  gameData.HasAchievements &&
                                  gameData.Achievements.Count > 0;
            if (!hasAchievements && !allowEmpty)
            {
                return null;
            }

            var playniteGame = gameData.Game;
            if (playniteGame == null && gameData.PlayniteGameId.HasValue)
            {
                playniteGame = _playniteApi?.Database?.Games?.Get(gameData.PlayniteGameId.Value);
            }

            var presentation = CreateGamePresentation(playniteGame);
            var (providerName, providerKey, providerMetadata) = ResolveProvider(gameData);
            var summaryArt = GameSummaryArtResolver.Resolve(
                gameData.PlayniteGameId,
                gameData.GameSummaryCategory,
                gameData.AchievementCategoryImageOverrides);

            var item = new GameSummaryItem
            {
                GameName = presentation.DisplayName ?? gameData.GameName ?? "Unknown",
                SortingName = presentation.SortingName ?? presentation.DisplayName ?? gameData.GameName ?? "Unknown",
                GameLogo = summaryArt ?? presentation.IconPath,
                GameCoverPath = summaryArt ?? presentation.CoverPath,
                PlatformText = presentation.PlatformText,
                Platforms = presentation.Platforms,
                RegionText = presentation.RegionText,
                PlaytimeSeconds = presentation.PlaytimeSeconds,
                AppId = gameData.AppId,
                ProviderGameKey = gameData.ProviderGameKey,
                PlayniteGameId = gameData.PlayniteGameId,
                LastPlayed = presentation.LastPlayed,
                IsCompleted = gameData.IsCompleted,
                Provider = providerName,
                ProviderKey = providerKey,
                ProviderIconKey = providerMetadata.iconKey,
                ProviderColorHex = providerMetadata.colorHex
            };

            if (!hasAchievements)
            {
                return item;
            }

            AchievementStatsAccumulator
                .FromAchievements(gameData.Achievements)
                .ApplyTo(item);

            return item;
        }

        private (string providerName, string providerKey, (string iconKey, string colorHex) metadata) ResolveProvider(
            GameAchievementData gameData)
        {
            var providerKey = gameData.EffectiveProviderKey;
            providerKey = string.IsNullOrWhiteSpace(providerKey) ? "Unknown" : providerKey;

            var providerName = ProviderRegistry.GetLocalizedName(providerKey);
            if (string.IsNullOrWhiteSpace(providerName))
            {
                providerName = providerKey;
            }

            var lookup = _providerLookup ?? (_providerLookup = BuildProviderLookup());
            if (!lookup.TryGetValue(providerKey, out var metadata))
            {
                metadata = ("ProviderIcon" + providerKey, "#888888");
            }

            return (providerName, providerKey, metadata);
        }

        private Dictionary<string, (string iconKey, string colorHex)> BuildProviderLookup()
        {
            var lookup = new Dictionary<string, (string iconKey, string colorHex)>(StringComparer.OrdinalIgnoreCase);
            if (_providers != null)
            {
                foreach (var provider in _providers)
                {
                    if (provider == null || string.IsNullOrWhiteSpace(provider.ProviderKey))
                    {
                        continue;
                    }

                    lookup[provider.ProviderKey] = (provider.ProviderIconKey, provider.ProviderColorHex);
                }
            }

            return lookup;
        }

        private GamePresentation CreateGamePresentation(Playnite.SDK.Models.Game playniteGame)
        {
            return new GamePresentation
            {
                Game = playniteGame,
                DisplayName = playniteGame?.Name,
                SortingName = playniteGame?.SortingName,
                IconPath = !string.IsNullOrEmpty(playniteGame?.Icon)
                    ? ResolveGameAssetPath(playniteGame.Icon)
                    : null,
                CoverPath = !string.IsNullOrEmpty(playniteGame?.CoverImage)
                    ? ResolveGameAssetPath(playniteGame.CoverImage)
                    : null,
                LastPlayed = playniteGame?.LastActivity,
                PlatformText = PlayniteGameMetadataFormatter.GetPlatformText(playniteGame),
                Platforms = PlayniteGameMetadataFormatter.GetPlatformNames(playniteGame),
                RegionText = PlayniteGameMetadataFormatter.GetRegionText(playniteGame),
                PlaytimeSeconds = playniteGame?.Playtime ?? 0
            };
        }

        private string ResolveGameAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return _playniteApi?.Database?.GetFullFilePath(path) ?? path;
        }

    }
}
