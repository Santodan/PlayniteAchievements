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
using PlayniteAchievements.Services;

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
        private readonly IPlayniteAPI _playniteApi;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private ManualSettings _providerSettings;

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Manual");
        public string ProviderKey => "Manual";
        public string ProviderIconKey => "ProviderIconManual";
        public string ProviderColorHex => "#ff652c";

        /// <summary>
        /// Manual provider is always authenticated (no credentials needed).
        /// </summary>
        public bool IsAuthenticated => true;

        public ISessionManager AuthSession => null;

        public ManualAchievementsProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            ManualSourceRegistry manualSourceRegistry)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _manualSourceRegistry = manualSourceRegistry ?? throw new ArgumentNullException(nameof(manualSourceRegistry));
            _providerSettings = ProviderRegistry.Settings<ManualSettings>();
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

            return TryGetManualLink(game.Id, out _);
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
            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var linkedGames = gamesToRefresh
                .Where(game => game != null && game.Id != Guid.Empty && TryGetManualLink(game.Id, out _))
                .ToList();

            if (linkedGames.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            var language = _settings.Persisted.GlobalLanguage ?? "english";

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                linkedGames,
                onGameStarting,
                async (game, token) =>
                {
                    if (!TryGetManualLink(game.Id, out var link) || link == null)
                    {
                        return ProviderRefreshExecutor.ProviderGameResult.Skipped();
                    }

                    var data = await BuildGameAchievementDataAsync(game, link, language, token).ConfigureAwait(false);
                    return new ProviderRefreshExecutor.ProviderGameResult
                    {
                        Data = data
                    };
                },
                onGameCompleted,
                isAuthRequiredException: ex => ex is ManualSourceAuthenticationException,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Error(ex, $"Failed to refresh manual achievements for game '{game?.Name}' ({game?.Id})");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
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

            var source = _manualSourceRegistry.GetSourceByKey(link.SourceKey);
            if (source == null)
            {
                _logger?.Warn($"Unknown manual source key: {link.SourceKey}");
                return null;
            }

            await ManualSourceAuthentication
                .EnsureAuthenticatedIfRequiredAsync(source, _providerSettings.RequireExophaseAuthentication, link, cancel)
                .ConfigureAwait(false);

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

            _manualSourceRegistry.GetPostProcessorByKey(link.SourceKey)?.Invoke(link, achievements);

            var unlockStateLookup = BuildCaseInsensitiveLookup(link.UnlockStates);
            var unlockTimeLookup = BuildCaseInsensitiveLookup(link.UnlockTimes);

            // Apply unlock times from link to each achievement
            foreach (var detail in achievements)
            {
                if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName))
                {
                    continue;
                }

                var lookupKeys = BuildUnlockLookupKeys(link, detail);

                var unlockedState = false;
                var hasState = TryGetLookupValue(unlockStateLookup, lookupKeys, out unlockedState);

                DateTime? unlockTime = null;
                var hasTime = TryGetLookupValue(unlockTimeLookup, lookupKeys, out unlockTime);

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

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is ManualSettings manualSettings)
            {
                _providerSettings.CopyFrom(manualSettings);
            }
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new ManualSettingsView(_playniteApi, _logger, _settings);

        internal static bool IsTrackingOverrideEnabled()
        {
            return ProviderRegistry.Settings<ManualSettings>().ManualTrackingOverrideEnabled;
        }

        internal static bool TryGetManualLink(Guid gameId, out ManualAchievementLink link)
        {
            return GameCustomDataLookup.TryGetManualLink(
                gameId,
                out link,
                fallbackSettings: ProviderRegistry.Settings<ManualSettings>());
        }

        internal static string GetManageAchievementsLinkSummary(ManualAchievementLink link)
        {
            if (link == null)
            {
                return L("LOCPlayAch_ManageAchievements_Manual_LinkSummary_None", "No manual link configured.");
            }

            return string.Format(
                L("LOCPlayAch_ManageAchievements_Manual_LinkSummary", "{0} ({1})"),
                string.IsNullOrWhiteSpace(link.SourceKey) ? "Manual" : link.SourceKey,
                string.IsNullOrWhiteSpace(link.SourceGameId)
                    ? L("LOCPlayAch_ManageAchievements_Value_NotAvailable", "N/A")
                    : link.SourceGameId);
        }

        internal static bool TryUnlinkManageAchievementsLink(
            Guid gameId,
            string gameName,
            IPlayniteAPI playniteApi,
            AchievementOverridesService achievementOverridesService)
        {
            if (!TryGetManualLink(gameId, out _))
            {
                return false;
            }

            var result = playniteApi?.Dialogs?.ShowMessage(
                string.Format(L("LOCPlayAch_Menu_UnlinkAchievements_Confirm", "Remove the manual achievement link for \"{0}\"?"), gameName),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) ?? System.Windows.MessageBoxResult.None;
            if (result != System.Windows.MessageBoxResult.Yes)
            {
                return false;
            }

            if (achievementOverridesService == null)
            {
                return false;
            }

            achievementOverridesService.ClearGameData(gameId, gameName);

            playniteApi?.Dialogs?.ShowMessage(
                L("LOCPlayAch_Status_Succeeded", "Success!"),
                L("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            return true;
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static Dictionary<string, T> BuildCaseInsensitiveLookup<T>(IReadOnlyDictionary<string, T> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            var lookup = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                var key = pair.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                lookup[key] = pair.Value;
            }

            return lookup;
        }

        private static bool TryGetLookupValue<T>(
            IDictionary<string, T> lookup,
            IReadOnlyList<string> keys,
            out T value)
        {
            value = default(T);
            if (lookup == null || keys == null || keys.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (lookup.TryGetValue(keys[i], out value))
                {
                    return true;
                }
            }

            return false;
        }

        private static List<string> BuildUnlockLookupKeys(ManualAchievementLink link, AchievementDetail detail)
        {
            var keys = new List<string>(5);
            AddLookupKey(keys, detail?.ApiName);

            if (string.Equals(link?.SourceKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                AddLookupKey(keys, ExophaseApiClient.NormalizeLegacyManualApiName(detail?.ApiName));
                AddLookupKey(keys, detail?.DisplayName);
                AddLookupKey(keys, ExophaseApiClient.NormalizeLegacyManualApiName(detail?.DisplayName));

                var apiName = detail?.ApiName?.Trim();
                if (!string.IsNullOrWhiteSpace(apiName) &&
                    apiName.StartsWith(ExophaseApiClient.ExophaseApiNamePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    AddLookupKey(keys, apiName.Substring(ExophaseApiClient.ExophaseApiNamePrefix.Length));
                }
            }

            return keys;
        }

        private static void AddLookupKey(IList<string> keys, string value)
        {
            var normalized = value?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return;
            }

            for (var i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            keys.Add(normalized);
        }
    }
}





