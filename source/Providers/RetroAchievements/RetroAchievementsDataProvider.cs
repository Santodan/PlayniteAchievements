using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.RetroAchievements.EmulatorLog;
using PlayniteAchievements.Providers.RetroAchievements.Hashing;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsDataProvider : DataProviderBase<RetroAchievementsSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride, IInGameProgressSource, IDisposable
    {
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_RetroAchievements",
            raw =>
            {
                if (int.TryParse((raw ?? string.Empty).Trim(), out var gameId) && gameId > 0)
                {
                    return ProviderOverrideValidation.Valid(gameId.ToString(CultureInfo.InvariantCulture));
                }

                return ProviderOverrideValidation.Invalid(
                    "LOCPlayAch_Menu_RaGameId_InvalidId");
            });

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;
        private readonly RetroAchievementsPathResolver _pathResolver;

        private readonly object _initLock = new object();
        private RetroAchievementsApiClient _apiClient;
        private RetroAchievementsHashIndexStore _hashIndexStore;
        private RetroAchievementsHashCacheStore _hashCacheStore;
        private RetroAchievementsScanner _scanner;
        private RetroAchievementsFriendsProvider _friendsProvider;

        private string _clientUsername;
        private string _clientApiKey;
        private string _clientLanguage;
        private readonly object _recentLock = new object();
        private readonly Dictionary<string, DateTime> _recentSeen =
            new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public RetroAchievementsDataProvider(ILogger logger, PlayniteAchievementsSettings settings, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
            _pathResolver = new RetroAchievementsPathResolver(playniteApi);
        }
        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_RetroAchievements");
        public string ProviderKey => "RetroAchievements";
        public string ProviderIconKey => "ProviderIconRetroAchievements";
        public string ProviderColorHex => "#FFD700";
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends =>
            _friendsProvider ?? (_friendsProvider = new RetroAchievementsFriendsProvider(
                _logger,
                () =>
                {
                    EnsureInitialized();
                    return _apiClient;
                },
                () =>
                {
                    EnsureInitialized();
                    return _hashIndexStore;
                }));

        /// <summary>
        /// Checks if RetroAchievements authentication is properly configured.
        /// Requires RaUsername and RaWebApiKey to be present.
        /// Does NOT check RetroAchievementsEnabled - that is handled by ProviderRegistry.
        /// </summary>
        public bool IsAuthenticated
        {
            get
            {
                var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
                return !string.IsNullOrWhiteSpace(providerSettings.RaUsername) &&
                       !string.IsNullOrWhiteSpace(providerSettings.RaWebApiKey);
            }
        }

        public bool IsCapable(Game game)
        {
            if (game == null) return false;

            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (!RetroAchievementsCapabilityHelper.HasConfiguredCredentials(providerSettings))
            {
                return false;
            }

            // Manual overrides can bypass local platform and ROM detection.
            if (TryGetGameIdOverride(game.Id, out _))
            {
                return true;
            }

            var hasResolvedConsole = RaConsoleIdResolver.TryResolve(game, out var consoleId);
            if (RetroAchievementsCapabilityHelper.CanUseNameFallback(game, providerSettings, hasResolvedConsole))
            {
                return true;
            }

            if (!hasResolvedConsole)
            {
                return false;
            }

            // Standard path: require ROM file
            var hasher = RaHasherFactory.Create(consoleId, _settings, _logger);
            if (hasher == null)
            {
                return false;
            }

            return _pathResolver.ResolveCandidateFilePaths(game).Any(IsReadableHashCandidate);
        }

        private static bool IsReadableHashCandidate(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            if (CueTrackReader.IsCuePath(path))
            {
                return CueTrackReader.HasReadableDataTrack(path);
            }

            return File.Exists(path) || ArchiveUtils.IsArchivePath(path);
        }

        public bool CanResolveAchievementPageUrl(AchievementPageLinkContext context)
        {
            return TryBuildAchievementPageUrl(context, out _);
        }

        public Task<string> GetAchievementPageUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel)
        {
            return Task.FromResult(
                TryBuildAchievementPageUrl(context, out var url)
                    ? url
                    : null);
        }

        internal static bool TryBuildAchievementPageUrl(
            AchievementPageLinkContext context,
            out string url)
        {
            url = null;
            if (context?.Game != null &&
                TryGetGameIdOverride(context.Game.Id, out var overrideId) &&
                overrideId > 0)
            {
                url = BuildAchievementPageUrl(overrideId);
                return true;
            }

            if (string.Equals(context?.ManualLink?.SourceKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase) &&
                TryGetPositiveId(context.ManualLink.SourceGameId, out var manualId))
            {
                url = BuildAchievementPageUrl(manualId);
                return true;
            }

            var cachedId = context?.BestGameData?.AppId ?? 0;
            if (cachedId > 0)
            {
                url = BuildAchievementPageUrl(cachedId);
                return true;
            }

            if (TryGetPositiveId(context?.Game?.GameId, out var gameId))
            {
                url = BuildAchievementPageUrl(gameId);
                return true;
            }

            return false;
        }

        private static string BuildAchievementPageUrl(int gameId)
        {
            return $"https://retroachievements.org/game/{gameId.ToString(CultureInfo.InvariantCulture)}";
        }

        private static bool TryGetPositiveId(string value, out int id)
        {
            return int.TryParse(
                       (value ?? string.Empty).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out id) &&
                   id > 0;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            EnsureInitialized();
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(
            Game game,
            GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Prefer instant, offline unlocks by tailing the emulator's own log when one is usable;
            // otherwise fall back to the remote "recent achievements" feed for this game.
            var logRegistration = TryBuildLogRegistration(game, cachedSchema);
            if (logRegistration != null)
            {
                return logRegistration;
            }

            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                IsRemote = true,
                PollInterval = TimeSpan.FromSeconds(5)
            };
        }

        private InGameProgressRegistration TryBuildLogRegistration(
            Game game,
            GameAchievementData cachedSchema)
        {
            var entry = RaEmulatorLogRegistry.ResolveForGame(_playniteApi, game, out var emulator);
            if (entry == null)
            {
                return null;
            }

            var overrides = ProviderRegistry.Settings<RetroAchievementsSettings>()?.EmulatorLogPathOverrides;
            var logPath = RaEmulatorLogRegistry.ResolveEffectiveLogPath(entry, emulator, overrides);
            if (string.IsNullOrWhiteSpace(logPath))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(logPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return null;
            }

            var hasOverride = overrides != null &&
                overrides.TryGetValue(entry.Key, out var overridePath) &&
                !string.IsNullOrWhiteSpace(overridePath);

            // Without an explicit override, only take over from the remote feed once the log actually
            // exists, so a user who has not enabled emulator logging keeps live web-API notifications.
            if (!hasOverride && !File.Exists(logPath))
            {
                return null;
            }

            var achievementIds = cachedSchema.Achievements
                .Select(achievement => achievement?.ApiName)
                .Where(apiName => !string.IsNullOrWhiteSpace(apiName))
                .ToList();
            if (achievementIds.Count == 0)
            {
                return null;
            }

            _logger?.Info(
                $"[RetroAchievements] In-game log tracking for '{game.Name}' via {entry.DisplayName}: {logPath}");

            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                WatchTargets = new[] { logPath },
                PollInterval = TimeSpan.FromSeconds(60),
                State = new RaEmulatorLogSession(logPath, entry.Profile, achievementIds)
            };
        }

        async Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var contexts = (games ?? Array.Empty<InGameTrackingContext>())
                .Where(context => context?.Game != null && context.CachedSchema?.Achievements != null)
                .ToList();
            if (contexts.Count == 0)
            {
                return Array.Empty<InGameProgressQueryResult>();
            }

            var results = new List<InGameProgressQueryResult>(contexts.Count);
            var remoteContexts = new List<InGameTrackingContext>();

            foreach (var context in contexts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context.Registration?.State is RaEmulatorLogSession session)
                {
                    var gameId = context.Game.Id;
                    results.Add(RaEmulatorLogReader.TryRead(session, out var observations)
                        ? InGameProgressQueryResult.Succeeded(gameId, observations, isDelta: true)
                        : InGameProgressQueryResult.Failed(gameId, "file_unstable"));
                }
                else
                {
                    remoteContexts.Add(context);
                }
            }

            if (remoteContexts.Count > 0)
            {
                EnsureInitialized();
                var recent = await _apiClient
                    .GetUserRecentAchievementsAsync(lookbackMinutes: 2, cancellationToken)
                    .ConfigureAwait(false) ?? new List<Models.RaRecentAchievement>();

                cancellationToken.ThrowIfCancellationRequested();
                results.AddRange(RetroAchievementsRecentProgressMapper.Map(
                    recent,
                    remoteContexts,
                    MarkRecentSeen));
            }

            return results;
        }

        private bool MarkRecentSeen(string key, DateTime unlockUtc)
        {
            lock (_recentLock)
            {
                var cutoff = DateTime.UtcNow.AddMinutes(-10);
                foreach (var stale in _recentSeen
                    .Where(pair => pair.Value < cutoff)
                    .Select(pair => pair.Key)
                    .ToList())
                {
                    _recentSeen.Remove(stale);
                }

                if (_recentSeen.ContainsKey(key))
                {
                    return false;
                }

                _recentSeen[key] = unlockUtc;
                return true;
            }
        }

        private void EnsureInitialized()
        {
            var providerSettings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            var username = providerSettings.RaUsername?.Trim() ?? string.Empty;
            var apiKey = providerSettings.RaWebApiKey?.Trim() ?? string.Empty;
            var language = _settings.Persisted.GlobalLanguage?.Trim() ?? string.Empty;

            lock (_initLock)
            {
                if (_scanner != null && string.Equals(username, _clientUsername, StringComparison.Ordinal) &&
                    string.Equals(apiKey, _clientApiKey, StringComparison.Ordinal) &&
                    string.Equals(language, _clientLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                _apiClient?.Dispose();
                _apiClient = new RetroAchievementsApiClient(_logger, username, apiKey, language);
                _hashIndexStore = new RetroAchievementsHashIndexStore(_logger, _settings, _apiClient, _pluginUserDataPath);
                _hashCacheStore = new RetroAchievementsHashCacheStore(_logger, _pluginUserDataPath);
                _scanner = new RetroAchievementsScanner(
                    _logger,
                    _settings,
                    _apiClient,
                    _hashIndexStore,
                    _pathResolver,
                    _hashCacheStore,
                    () => PlayniteAchievementsPlugin.Instance?.DiskImageService);

                _clientUsername = username;
                _clientApiKey = apiKey;
                _clientLanguage = language;
            }
        }

        // private bool TryResolveConsoleId(Game game, out int consoleId)
        //     => RaConsoleIdResolver.TryResolve(game, out consoleId);

        public void Dispose()
        {
            _apiClient?.Dispose();
        }

        internal static bool TryGetGameIdOverride(Guid gameId, out int gameIdOverride)
        {
            return GameCustomDataLookup.TryGetRetroAchievementsGameIdOverride(
                gameId,
                out gameIdOverride,
                fallbackSettings: ProviderRegistry.Settings<RetroAchievementsSettings>());
        }

        /// <summary>
        /// Checks if a game's platform is supported by RetroAchievements.
        /// Used by UI to determine if RA override option should be shown.
        /// This is separate from IsCapable which also requires ROM files.
        /// </summary>
        public static bool CanSetOverride(Game game)
        {
            return RetroAchievementsCapabilityHelper.CanSetOverride(game);
        }

        internal static bool TrySetGameIdOverride(Guid gameId, int newId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (newId <= 0)
            {
                return false;
            }

            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore != null)
            {
                customDataStore.Update(gameId, customData =>
                {
                    customData.ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "RetroAchievements",
                        Value = newId.ToString(CultureInfo.InvariantCulture)
                    };
                });
            }
            else
            {
                var settings = ProviderRegistry.Settings<RetroAchievementsSettings>();
                settings.RaGameIdOverrides[gameId] = newId;
                ProviderRegistry.Write(settings);
            }

            persistSettingsForUi?.Invoke();

            logger?.Info($"Set RA game ID override for '{gameName}' to {newId}");
            return true;
        }

        internal static bool TryClearGameIdOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore != null)
            {
                if (!customDataStore.TryLoad(gameId, out var customData) ||
                    customData?.ProviderOverride == null ||
                    !string.Equals(customData.ProviderOverride.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                customDataStore.Update(gameId, data =>
                {
                    data.ProviderOverride = null;
                });
            }
            else
            {
                var settings = ProviderRegistry.Settings<RetroAchievementsSettings>();
                if (!settings.RaGameIdOverrides.Remove(gameId))
                {
                    return false;
                }

                ProviderRegistry.Write(settings);
            }

            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared RA game ID override for '{gameName}'");
            return true;
        }

        internal static bool UseScaledPoints(GameAchievementData gameData)
        {
            return string.Equals(gameData?.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ProviderRegistry.Settings<RetroAchievementsSettings>().RaPointsMode, "scaled", StringComparison.OrdinalIgnoreCase);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new RetroAchievementsSettingsView(_pluginUserDataPath);
    }
}






