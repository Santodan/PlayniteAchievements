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
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Data provider for manually linked achievements.
    /// Implements IDataProvider to integrate with the achievement refresh system.
    /// </summary>
    public sealed class ManualAchievementsProvider : DataProviderBase<ManualSettings>, IDataProvider, IRefreshAuthContextReceiver
    {
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ManualSourceRegistry _manualSourceRegistry;
        private readonly Dictionary<string, AuthProbeResult> _sourceAuthProbeCache =
            new Dictionary<string, AuthProbeResult>(StringComparer.OrdinalIgnoreCase);
        private readonly List<IRefreshAuthContextReceiver> _sourceAuthContextReceivers =
            new List<IRefreshAuthContextReceiver>();

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Manual");
        public string ProviderKey => "Manual";
        public string ProviderIconKey => "ProviderIconManual";
        public string ProviderColorHex => "#ff652c";

        /// <summary>
        /// Manual provider is always authenticated (no credentials needed).
        /// </summary>
        public bool IsAuthenticated => true;

        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

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

            await EnsureManualSourceAuthenticatedAsync(source, link, cancel).ConfigureAwait(false);

            // Null when the source cannot resolve a platform; the game then displays as Manual.
            var providerPlatformKey = source.ResolveProviderPlatformKey(link.SourceGameId);

            // Fetch achievements directly as AchievementDetail list
            var achievements = await source.GetAchievementsAsync(link.SourceGameId, language, cancel);
            if (achievements == null || achievements.Count == 0)
            {
                _logger?.Debug($"No achievements found for manual link: source={link.SourceKey}, gameId={link.SourceGameId}");
                return new GameAchievementData
                {
                    LastUpdatedUtc = DateTime.UtcNow,
                    ProviderKey = ProviderKey,
                    ProviderPlatformKey = providerPlatformKey,
                    LibrarySourceName = game.PluginId.ToString(),
                    HasAchievements = false,
                    GameName = game.Name,
                    PlayniteGameId = game.Id,
                    Game = game,
                    Achievements = new List<AchievementDetail>()
                };
            }

            _manualSourceRegistry.GetPostProcessorByKey(link.SourceKey)?.Invoke(link, achievements);

            // Apply stored unlock state to each achievement via the shared resolver so the
            // manual-tracking window and this refresh path agree on flexible key matching.
            new ManualUnlockResolver(link).ApplyUnlockState(achievements);

            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                ProviderPlatformKey = providerPlatformKey,
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
        public ProviderSettingsViewBase CreateSettingsView() => new ManualSettingsView(_playniteApi, _logger, _settings);

        public void BeginRefreshAuthContext(RefreshAuthContext context)
        {
            _sourceAuthProbeCache.Clear();
            _sourceAuthContextReceivers.Clear();

            foreach (var receiver in _manualSourceRegistry
                .GetAllSources()
                .OfType<IRefreshAuthContextReceiver>())
            {
                receiver.BeginRefreshAuthContext(context);
                _sourceAuthContextReceivers.Add(receiver);
            }
        }

        public void EndRefreshAuthContext(RefreshAuthContext context)
        {
            for (var i = _sourceAuthContextReceivers.Count - 1; i >= 0; i--)
            {
                try
                {
                    _sourceAuthContextReceivers[i]?.EndRefreshAuthContext(context);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Manual source receiver failed while ending refresh auth context.");
                }
            }

            _sourceAuthContextReceivers.Clear();
            _sourceAuthProbeCache.Clear();
        }

        private async Task EnsureManualSourceAuthenticatedAsync(
            IManualSource source,
            ManualAchievementLink link,
            CancellationToken ct)
        {
            if (!ManualSourceAuthentication.ShouldRequireAuthentication(
                    source,
                    ProviderSettings.RequireExophaseAuthentication,
                    link))
            {
                return;
            }

            var sourceKey = string.IsNullOrWhiteSpace(source.SourceKey)
                ? source.GetType().FullName
                : source.SourceKey.Trim();

            if (_sourceAuthProbeCache.TryGetValue(sourceKey, out var cachedProbe))
            {
                if (cachedProbe?.IsSuccess == true)
                {
                    return;
                }

                throw ManualSourceAuthentication.CreateException(source, cachedProbe);
            }

            AuthProbeResult probeResult;
            if (source.AuthSession != null)
            {
                probeResult = await source.AuthSession.ProbeAuthStateAsync(ct).ConfigureAwait(false);
                _sourceAuthProbeCache[sourceKey] = probeResult;

                if (ct.IsCancellationRequested && probeResult?.Outcome == AuthOutcome.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                }

                if (probeResult?.IsSuccess == true)
                {
                    return;
                }

                throw ManualSourceAuthentication.CreateException(source, probeResult);
            }

            probeResult = source.IsAuthenticated
                ? AuthProbeResult.AlreadyAuthenticated()
                : AuthProbeResult.NotAuthenticated();
            _sourceAuthProbeCache[sourceKey] = probeResult;

            if (probeResult.IsSuccess)
            {
                return;
            }

            throw ManualSourceAuthentication.CreateException(source, probeResult);
        }

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
                return L("LOCPlayAch_ManageAchievements_Manual_LinkSummary_None");
            }

            return string.Format(
                L("LOCPlayAch_ManageAchievements_Manual_LinkSummary"),
                string.IsNullOrWhiteSpace(link.SourceKey) ? "Manual" : link.SourceKey,
                string.IsNullOrWhiteSpace(link.SourceGameId)
                    ? L("LOCPlayAch_ManageAchievements_Value_NotAvailable")
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
                string.Format(L("LOCPlayAch_Menu_UnlinkAchievements_Confirm"), gameName),
                L("LOCPlayAch_Title_PluginName"),
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
                L("LOCPlayAch_Status_Succeeded"),
                L("LOCPlayAch_Title_PluginName"),
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            return true;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}





