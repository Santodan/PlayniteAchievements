using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Ffxiv
{
    /// <summary>
    /// Final Fantasy XIV achievement provider backed by the FFXIV Collect API.
    /// FFXIV achievements are account/character-wide, so the resolved set is
    /// attached to the matched FFXIV entry in the Playnite library.
    /// </summary>
    internal sealed class FfxivDataProvider : DataProviderBase<FfxivSettings>, IDataProvider, IProviderOverride, IDisposable
    {
        // Presence-only binding: forces a game to be treated as FFXIV (account/character-wide data).
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.None();

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly string _pluginUserDataPath;

        private readonly object _initLock = new object();
        private FfxivApiClient _apiClient;
        private FfxivCatalogCache _catalogCache;

        public FfxivDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_FFXIV");
        public string ProviderKey => "FFXIV";
        public string ProviderIconKey => "ProviderIconFFXIV";
        public string ProviderColorHex => "#C0392B";
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        /// <summary>
        /// FFXIV is authenticated once a character name and world are configured.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                var s = ProviderRegistry.Settings<FfxivSettings>();
                return !string.IsNullOrWhiteSpace(s.CharacterName) &&
                       !string.IsNullOrWhiteSpace(s.World);
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null)
            {
                return false;
            }

            if (!IsAuthenticated)
            {
                return false;
            }

            // Manual override binding takes precedence.
            if (GameCustomDataLookup.TryGetProviderOverride(game.Id, out var providerOverride) &&
                string.Equals(providerOverride.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return IsFinalFantasyXivGame(game);
        }

        private static bool IsFinalFantasyXivGame(Game game)
        {
            return FfxivParsing.IsFinalFantasyXivTitle(game?.Name);
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var providerSettings = ProviderRegistry.Settings<FfxivSettings>();

            if (string.IsNullOrWhiteSpace(providerSettings.CharacterName) ||
                string.IsNullOrWhiteSpace(providerSettings.World))
            {
                _logger?.Warn("[FFXIV] Missing character name/world - cannot fetch achievements.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            EnsureInitialized();

            var characterId = await ResolveCharacterIdAsync(providerSettings, cancel).ConfigureAwait(false);
            if (characterId <= 0)
            {
                _logger?.Warn($"[FFXIV] Could not resolve character '{providerSettings.CharacterName}' @ '{providerSettings.World}'.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            var catalog = await _catalogCache.GetCatalogAsync(_apiClient, cancel).ConfigureAwait(false);
            var character = await _apiClient.FetchCharacterAsync(characterId, cancel).ConfigureAwait(false);

            var obtained = BuildObtainedMap(character);
            if (character?.Achievements?.Public == false)
            {
                _logger?.Warn($"[FFXIV] Character '{providerSettings.CharacterName}' has achievements hidden on the Lodestone; all will appear locked.");
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) =>
                {
                    if (!IsCapable(game))
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    var data = BuildGameData(game, catalog, obtained);
                    return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult { Data = data });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[FFXIV] Failed to build achievements for '{game?.Name}' after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        private async Task<long> ResolveCharacterIdAsync(FfxivSettings providerSettings, CancellationToken cancel)
        {
            if (providerSettings.ResolvedCharacterId > 0)
            {
                return providerSettings.ResolvedCharacterId;
            }

            var resolved = await _apiClient.ResolveCharacterIdAsync(
                providerSettings.CharacterName,
                providerSettings.World,
                providerSettings.Region,
                cancel).ConfigureAwait(false);

            if (resolved.HasValue && resolved.Value > 0)
            {
                providerSettings.ResolvedCharacterId = resolved.Value;
                ProviderRegistry.Write(providerSettings);
                return resolved.Value;
            }

            return 0;
        }

        private GameAchievementData BuildGameData(
            Game game,
            IReadOnlyList<FfxivAchievement> catalog,
            IReadOnlyDictionary<int, DateTime?> obtained)
        {
            var achievements = new List<AchievementDetail>(catalog?.Count ?? 0);

            if (catalog != null)
            {
                foreach (var achievement in catalog)
                {
                    if (achievement == null) continue;
                    achievements.Add(BuildAchievementDetail(achievement, obtained));
                }
            }

            return new GameAchievementData
            {
                ProviderKey = ProviderKey,
                GameName = game?.Name,
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements.Count > 0,
                PlayniteGameId = game?.Id,
                Achievements = achievements
            };
        }

        private static AchievementDetail BuildAchievementDetail(
            FfxivAchievement achievement,
            IReadOnlyDictionary<int, DateTime?> obtained)
        {
            DateTime? unlockTimeUtc = null;
            var unlocked = obtained != null && obtained.TryGetValue(achievement.Id, out unlockTimeUtc);
            if (unlockTimeUtc.HasValue && unlockTimeUtc.Value.Kind != DateTimeKind.Utc)
            {
                unlockTimeUtc = unlockTimeUtc.Value.ToUniversalTime();
            }

            var globalPercent = FfxivParsing.ParseOwnedPercent(achievement.Owned);

            return new AchievementDetail
            {
                ApiName = achievement.Id.ToString(CultureInfo.InvariantCulture),
                DisplayName = achievement.Name,
                Description = achievement.Description,
                UnlockedIconPath = achievement.Icon,
                Points = achievement.Points,
                Category = achievement.Category?.Name,
                CategoryType = FfxivParsing.ResolveCategoryType(achievement.Category?.Name),
                UnlockTimeUtc = unlockTimeUtc,
                Unlocked = unlocked,
                Hidden = false,
                GlobalPercentUnlocked = globalPercent,
                Rarity = globalPercent.HasValue
                    ? PercentRarityHelper.GetRarityTier(globalPercent.Value)
                    : RarityTier.Common
            };
        }

        private static Dictionary<int, DateTime?> BuildObtainedMap(FfxivCharacter character)
        {
            var map = new Dictionary<int, DateTime?>();
            var obtained = character?.Achievements?.Obtained;
            if (obtained == null)
            {
                return map;
            }

            foreach (var entry in obtained)
            {
                if (entry == null) continue;
                map[entry.Id] = entry.Time;
            }

            return map;
        }

        private void EnsureInitialized()
        {
            lock (_initLock)
            {
                if (_apiClient == null)
                {
                    _apiClient = new FfxivApiClient(_logger);
                }

                if (_catalogCache == null)
                {
                    _catalogCache = new FfxivCatalogCache(_logger, _pluginUserDataPath);
                }
            }
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new FfxivSettingsView();
    }
}
