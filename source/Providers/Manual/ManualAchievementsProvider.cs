using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Data provider for manually linked achievements.
    /// Implements IDataProvider to integrate with the achievement refresh system.
    /// </summary>
    public sealed class ManualAchievementsProvider : IDataProvider
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IManualSource _steamManualSource;
        private readonly IManualSource _exophaseManualSource;
        private readonly ExophaseSessionManager _exophaseSessionManager;
        private readonly DiskImageService _diskImageService;
        private ManualSettings _providerSettings;

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Manual");
        public string ProviderKey => "Manual";
        public string ProviderIconKey => "ProviderIconManual";
        public string ProviderColorHex => "#ff652c";

        /// <summary>
        /// Manual provider is always authenticated (no credentials needed).
        /// </summary>
        public bool IsAuthenticated => true;

        public ManualAchievementsProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath,
            IPlayniteAPI playniteApi,
            ExophaseSessionManager exophaseSessionManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _diskImageService = new DiskImageService(logger, pluginUserDataPath);
            _exophaseSessionManager = exophaseSessionManager ?? throw new ArgumentNullException(nameof(exophaseSessionManager));

            // Create Steam manual source with properly configured HTTP client
            var handler = new System.Net.Http.HttpClientHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                AllowAutoRedirect = true
            };
            var httpClient = new System.Net.Http.HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            _steamManualSource = new SteamManualSource(
                httpClient,
                logger,
                () => ProviderSettingsHelper.Load<SteamSettings>(settings.Persisted, "Steam").SteamApiKey);

            // Create Exophase manual source (uses WebView, no HTTP client needed)
            _exophaseManualSource = new ExophaseManualSource(
                playniteApi,
                exophaseSessionManager,
                logger,
                () => settings.Persisted.GlobalLanguage);

            _providerSettings = ProviderSettingsHelper.Load<ManualSettings>(settings.Persisted, "Manual");
        }

        /// <summary>
        /// Checks if a game has a manual achievement link configured.
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            return _settings.Persisted.ManualAchievementLinks.ContainsKey(game.Id);
        }

        /// <summary>
        /// Refreshes achievement data for all games with manual links.
        /// Fetches schema from the source and applies stored unlock times.
        /// </summary>
        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var summary = new RebuildSummary();
            var payload = new RebuildPayload { Summary = summary };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return payload;
            }

            var links = _settings.Persisted.ManualAchievementLinks;
            var language = _settings.Persisted.GlobalLanguage ?? "english";

            foreach (var game in gamesToRefresh)
            {
                if (cancel.IsCancellationRequested)
                {
                    break;
                }

                if (game == null || game.Id == Guid.Empty)
                {
                    continue;
                }

                if (!links.TryGetValue(game.Id, out var link) || link == null)
                {
                    continue;
                }

                onGameStarting?.Invoke(game);

                try
                {
                    var data = await BuildGameAchievementDataAsync(game, link, language, cancel);

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, data);
                    }

                    summary.GamesRefreshed++;
                    if (data != null && data.HasAchievements)
                    {
                        summary.GamesWithAchievements++;
                    }
                    else
                    {
                        summary.GamesWithoutAchievements++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to refresh manual achievements for game '{game.Name}' ({game.Id})");
                    summary.GamesWithoutAchievements++;

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, null);
                    }
                }
            }

            return payload;
        }

        /// <summary>
        /// Builds GameAchievementData for a game with a manual link.
        /// Fetches the achievement schema from the source and applies stored unlock times.
        /// </summary>
        private async Task<GameAchievementData> BuildGameAchievementDataAsync(
            Game game,
            ManualAchievementLink link,
            string language,
            CancellationToken cancel)
        {
            if (link == null || string.IsNullOrWhiteSpace(link.SourceGameId))
            {
                return null;
            }

            // Get the appropriate source
            IManualSource source = link.SourceKey switch
            {
                "Steam" => _steamManualSource,
                "Exophase" => _exophaseManualSource,
                _ => null
            };

            if (source == null)
            {
                _logger?.Warn($"Unknown manual source key: {link.SourceKey}");
                return null;
            }

            // Fetch achievements directly as AchievementDetail list
            var achievements = await source.GetAchievementsAsync(link.SourceGameId, language, cancel);
            if (achievements == null || achievements.Count == 0)
            {
                _logger?.Debug($"No achievements found for manual link: source={link.SourceKey}, gameId={link.SourceGameId}");
                return new GameAchievementData
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    ProviderKey = ProviderKey,
                    LibrarySourceName = game.PluginId.ToString(),
                    HasAchievements = false,
                    GameName = game.Name,
                    PlayniteGameId = game.Id,
                    Game = game,
                    Achievements = new List<AchievementDetail>()
                };
            }

            // Apply unlock times from link to each achievement
            foreach (var detail in achievements)
            {
                if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName))
                {
                    continue;
                }

                var unlockedState = false;
                var hasState = link.UnlockStates != null &&
                               link.UnlockStates.TryGetValue(detail.ApiName, out unlockedState);

                DateTime? unlockTime = null;
                var hasTime = link.UnlockTimes != null &&
                              link.UnlockTimes.TryGetValue(detail.ApiName, out unlockTime);

                var isUnlocked = hasState
                    ? unlockedState
                    : (hasTime && unlockTime.HasValue);

                if (isUnlocked)
                {
                    detail.Unlocked = true;
                    detail.UnlockTimeUtc = hasTime && unlockTime.HasValue
                        ? unlockTime
                        : null;
                }
            }

            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                LibrarySourceName = game.PluginId.ToString(),
                HasAchievements = true,
                GameName = game.Name,
                AppId = int.TryParse(link.SourceGameId, out var appId) ? appId : 0,
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = achievements
            };
        }

        /// <summary>
        /// Gets the Steam manual source for use in dialogs.
        /// </summary>
        public IManualSource GetSteamManualSource() => _steamManualSource;

        /// <summary>
        /// Gets the Exophase manual source for use in dialogs.
        /// </summary>
        public IManualSource GetExophaseManualSource() => _exophaseManualSource;

        /// <summary>
        /// Gets a manual source by its source key.
        /// </summary>
        /// <param name="sourceKey">The source key (e.g., "Steam", "Exophase").</param>
        /// <returns>The manual source, or null if not found.</returns>
        public IManualSource GetSourceByKey(string sourceKey)
        {
            if (string.IsNullOrWhiteSpace(sourceKey))
            {
                return null;
            }

            return sourceKey switch
            {
                "Steam" => _steamManualSource,
                "Exophase" => _exophaseManualSource,
                _ => null
            };
        }

        /// <summary>
        /// Gets all available manual sources.
        /// </summary>
        public IReadOnlyList<IManualSource> GetAllSources()
        {
            return new List<IManualSource> { _steamManualSource, _exophaseManualSource }
                .AsReadOnly();
        }

        /// <summary>
        /// Gets the Exophase session manager for settings UI.
        /// </summary>
        public ExophaseSessionManager GetExophaseSessionManager() => _exophaseSessionManager;

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new ManualSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is ManualSettings manualSettings)
            {
                _providerSettings = manualSettings;
                ProviderSettingsHelper.Save(_settings.Persisted, manualSettings);
            }
        }
    }
}
