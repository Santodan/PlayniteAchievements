using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Epic
{
    public sealed class EpicDataProvider : DataProviderBase<EpicSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride, IRefreshAuthContextReceiver, IInGameProgressSource, IDisposable
    {
        /// <summary>
        /// Cadence for the remote player-record poll while an Epic game runs. Epic has no local
        /// unlock file to watch, so the fast prong is a single GraphQL request per tick — the same
        /// request count as the RetroAchievements recent feed at its cadence.
        /// </summary>
        private static readonly TimeSpan RemotePollInterval = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Resolved once at game start so the fast prong never repeats game-id discovery. The
        /// product context (asset namespace, productId) is resolved inside the API client through
        /// its in-memory caches on the first poll.
        /// </summary>
        private sealed class EpicInGameState
        {
            public string GameId { get; set; }
            public string GameName { get; set; }
        }

        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Epic",
            ProviderOverrideValidators.RequiredText);

        private readonly ILogger _logger;
        private readonly EpicSessionManager _sessionManager;
        private readonly EpicScanner _scanner;
        private readonly EpicApiClient _apiClient;
        private readonly HttpClient _httpClient;

        private static readonly Guid EpicPluginId = ResolveEpicPluginId();
        internal static readonly Guid LegendaryPluginId = Guid.Parse("EAD65C3B-2F8F-4E37-B4E6-B3DE6BE540C6");

        public EpicDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi)
        {
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (playniteApi == null) throw new ArgumentNullException(nameof(playniteApi));

            _logger = logger;
            _httpClient = HttpClientFactory.Create();
            _sessionManager = new EpicSessionManager(playniteApi, logger);

            _apiClient = new EpicApiClient(_httpClient, logger, _sessionManager, settings);
            _scanner = new EpicScanner(settings, _apiClient, _sessionManager, logger);
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Epic");

        public string ProviderKey => "Epic";

        public string ProviderIconKey => "ProviderIconEpic";

        public string ProviderColorHex => "#26BBFF";

        public bool IsAuthenticated => _sessionManager.IsAuthenticated;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        public bool IsCapable(Game game) => IsEpicCapable(game);

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
            var links = context?.Game?.Links;
            if (links == null)
            {
                return false;
            }

            foreach (var link in links)
            {
                if (TryGetEpicSlug(link?.Url, out var slug))
                {
                    url = $"https://store.epicgames.com/achievements/{Uri.EscapeDataString(slug)}";
                    return true;
                }
            }

            return false;
        }

        private static bool IsEpicCapable(Game game)
        {
            return game != null &&
                   (game.PluginId == EpicPluginId ||
                    game.PluginId == LegendaryPluginId ||
                    GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, "Epic", out _));
        }

        internal static bool TryGetEpicSlug(string linkUrl, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(linkUrl) ||
                !Uri.TryCreate(linkUrl.Trim(), UriKind.Absolute, out var uri) ||
                !IsEpicHost(uri.Host))
            {
                return false;
            }

            var segments = uri.AbsolutePath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length - 1; i++)
            {
                if (string.Equals(segments[i], "p", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(segments[i], "achievements", StringComparison.OrdinalIgnoreCase))
                {
                    slug = Uri.UnescapeDataString(segments[i + 1]).Trim();
                    return !string.IsNullOrWhiteSpace(slug);
                }
            }

            return false;
        }

        private static bool IsEpicHost(string host)
        {
            return string.Equals(host, "epicgames.com", StringComparison.OrdinalIgnoreCase) ||
                   host?.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase) == true;
        }

        public Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            return _scanner.RefreshAsync(gamesToRefresh, onGameStarting, onGameCompleted, cancel);
        }

        /// <summary>
        /// Extracts the effective Epic game id: a per-game provider override takes precedence over
        /// the library-supplied GameId.
        /// </summary>
        internal static bool TryGetEpicGameId(Game game, out string gameId)
        {
            gameId = null;
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetProviderOverrideValue(game.Id, "Epic", out var overrideId) &&
                !string.IsNullOrWhiteSpace(overrideId))
            {
                gameId = overrideId.Trim();
                return true;
            }

            if (string.IsNullOrWhiteSpace(game.GameId))
            {
                return false;
            }

            gameId = game.GameId.Trim();
            return true;
        }

        InGameProgressRegistration IInGameProgressSource.TryRegister(
            Game game,
            GameAchievementData cachedSchema)
        {
            if (game == null ||
                cachedSchema?.Achievements == null ||
                cachedSchema.Achievements.Count == 0 ||
                !string.Equals(cachedSchema.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase) ||
                !TryGetEpicGameId(game, out var gameId) ||
                !IsAuthenticated)
            {
                return null;
            }

            _logger?.Info(
                $"[EpicAch] In-game tracking for '{game.Name}' via player-record poll ({gameId}).");
            return new InGameProgressRegistration
            {
                ProviderKey = ProviderKey,
                IsRemote = true,
                PollInterval = RemotePollInterval,
                State = new EpicInGameState
                {
                    GameId = gameId,
                    GameName = game.Name
                }
            };
        }

        async Task<IReadOnlyList<InGameProgressQueryResult>> IInGameProgressSource.QueryAsync(
            IReadOnlyList<InGameTrackingContext> games,
            CancellationToken cancellationToken)
        {
            var results = new List<InGameProgressQueryResult>();
            foreach (var context in games ?? Array.Empty<InGameTrackingContext>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var gameId = context?.Game?.Id ?? Guid.Empty;
                var state = context?.Registration?.State as EpicInGameState;
                if (state == null)
                {
                    results.Add(InGameProgressQueryResult.Failed(gameId, "registration_missing"));
                    continue;
                }

                try
                {
                    var records = await _apiClient
                        .GetPlayerAchievementRecordsAsync(state.GameId, null, cancellationToken)
                        .ConfigureAwait(false);
                    if (records == null)
                    {
                        results.Add(InGameProgressQueryResult.Failed(gameId, "query_failed"));
                        continue;
                    }

                    var observations = records
                        .Where(record => record != null &&
                                         record.Unlocked &&
                                         !string.IsNullOrWhiteSpace(record.AchievementName))
                        .Select(record => new AchievementProgressObservation
                        {
                            ApiName = record.AchievementName,
                            Unlocked = true,
                            UnlockTimeUtc = record.UnlockTimeUtc
                        })
                        .ToList();
                    results.Add(InGameProgressQueryResult.Succeeded(gameId, observations));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[EpicAch] In-game player-record poll failed for '{state.GameName}'.");
                    results.Add(InGameProgressQueryResult.Failed(gameId, "query_failed"));
                }
            }

            return results;
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }

        public void BeginRefreshAuthContext(RefreshAuthContext context)
        {
            _scanner?.BeginRefreshAuthContext(context);
        }

        public void EndRefreshAuthContext(RefreshAuthContext context)
        {
            _scanner?.EndRefreshAuthContext(context);
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new EpicSettingsView(_sessionManager);

        private static Guid ResolveEpicPluginId()
        {
            try
            {
                return BuiltinExtensions.GetIdFromExtension(BuiltinExtension.EpicLibrary);
            }
            catch
            {
                return Guid.Empty;
            }
        }
    }
}






