using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// Full data provider for Exophase achievement tracking.
    /// Supports automatic game claiming by platform and per-game overrides.
    /// </summary>
    internal sealed class ExophaseDataProvider : DataProviderBase<ExophaseSettings>, IDataProvider, IAchievementPageLinkProvider, IProviderOverride
    {
        // Optional value: an empty slug means auto-detect via game-name/platform search.
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.Text(
            "LOCPlayAch_ManageAchievements_Overrides_ProviderValueLabel_Exophase",
            raw =>
            {
                var trimmed = (raw ?? string.Empty).Trim();
                return ProviderOverrideValidation.Valid(string.IsNullOrWhiteSpace(trimmed) ? null : trimmed);
            },
            valueOptional: true);

        #region Fields

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ExophaseApiClient _apiClient;
        private readonly ExophaseFriendsProvider _friendsProvider;
        // Session-scoped memo of resolved slugs, cleared at the start of each refresh.
        // Slug mappings are permanently stable; re-resolution only happens to recover
        // from a bad historical match, not because the mapping can expire.
        private readonly Dictionary<Guid, string> _slugMemo = new Dictionary<Guid, string>();
        private readonly object _slugMemoLock = new object();
        private static readonly string[] KnownExophasePlatformTokens =
        {
            "steam", "gog", "epic", "blizzard", "origin", "psn", "ps3", "ps4", "ps5", "vita",
            "xbox", "xbox-one", "xbox-360", "retro", "android", "apple", "ubisoft", "uplay"
        };

        #endregion

        #region Provider Metadata

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");
        public string ProviderKey => "Exophase";
        public string ProviderIconKey => "ProviderIconExophase";
        public string ProviderColorHex => "#2f8ab3";

        /// <summary>
        /// Checks if Exophase session is authenticated.
        /// </summary>
        public bool IsAuthenticated => _sessionManager?.IsAuthenticated ?? false;

        public ISessionManager AuthSession => _sessionManager;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => _friendsProvider;

        public bool CanResolveAchievementPageUrl(AchievementPageLinkContext context)
        {
            return TryBuildKnownAchievementPageUrl(context, out _) ||
                   CanAttemptSlugResolution(context?.Game);
        }

        public async Task<string> GetAchievementPageUrlAsync(
            AchievementPageLinkContext context,
            CancellationToken cancel)
        {
            if (TryBuildKnownAchievementPageUrl(context, out var url))
            {
                return url;
            }

            var game = context?.Game;
            if (!CanAttemptSlugResolution(game))
            {
                return null;
            }

            var slug = await ResolveExophaseSlugAsync(game, cancel).ConfigureAwait(false);
            return TryBuildAchievementPageUrlFromSlug(slug, out url)
                ? url
                : null;
        }

        private bool TryBuildKnownAchievementPageUrl(
            AchievementPageLinkContext context,
            out string url)
        {
            url = null;
            if (string.Equals(context?.ManualLink?.SourceKey, ProviderKey, StringComparison.OrdinalIgnoreCase) &&
                TryBuildAchievementPageUrlFromSlug(context.ManualLink.SourceGameId, out url))
            {
                return true;
            }

            var game = context?.Game;
            if (game == null)
            {
                return false;
            }

            if (GameCustomDataLookup.TryGetExophaseSlugOverride(game.Id, out var overrideSlug, ProviderSettings) &&
                TryBuildAchievementPageUrlFromSlug(overrideSlug, out url))
            {
                return true;
            }

            return TryGetCachedSlug(game.Id, out var cachedSlug) &&
                   TryBuildAchievementPageUrlFromSlug(cachedSlug, out url);
        }

        internal static bool TryBuildAchievementPageUrlFromSlug(string slugOrUrl, out string url)
        {
            url = null;
            var builtUrl = ExophaseApiClient.BuildUrlFromSlug(slugOrUrl);
            if (string.IsNullOrWhiteSpace(builtUrl) ||
                !Uri.TryCreate(builtUrl.Trim(), UriKind.Absolute, out var uri) ||
                !IsExophaseHost(uri.Host))
            {
                return false;
            }

            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1
            };
            url = builder.Uri.AbsoluteUri;
            return true;
        }

        private static bool IsExophaseHost(string host)
        {
            return string.Equals(host, "exophase.com", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(host, "www.exophase.com", StringComparison.OrdinalIgnoreCase);
        }

        private bool TryGetCachedSlug(Guid gameId, out string slug)
        {
            slug = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            lock (_slugMemoLock)
            {
                if (_slugMemo.TryGetValue(gameId, out var memoSlug) &&
                    !string.IsNullOrWhiteSpace(memoSlug))
                {
                    slug = memoSlug;
                    return true;
                }

                return false;
            }
        }

        private static bool CanAttemptSlugResolution(Game game)
        {
            return game != null &&
                   !string.IsNullOrWhiteSpace(game.Name) &&
                   !string.IsNullOrWhiteSpace(GetExophasePlatformSlug(game));
        }

        #endregion

        #region Construction

        public ExophaseDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _sessionManager = new ExophaseSessionManager(playniteApi, logger, pluginUserDataPath);
            _apiClient = new ExophaseApiClient(playniteApi, logger, _sessionManager.CookieSnapshotStore);

            _friendsProvider = new ExophaseFriendsProvider(_apiClient, ProviderSettings, settings, playniteApi, logger);
        }

        #endregion

        #region Capability and Refresh Flow

        /// <summary>
        /// Checks if this provider can handle a game.
        /// Game is claimed if:
        /// 1. Provider is enabled AND
        /// 2. Game is in IncludedGames OR game token is in ManagedProviders
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (!ProviderSettings.IsEnabled)
            {
                return false;
            }

            // Check explicit game inclusion first
            if (GameCustomDataLookup.IsExophaseIncluded(game.Id, ProviderSettings))
            {
                _logger.Debug($"Exophase IsCapable for '{game.Name}': true (explicitly included)");
                return true;
            }

            // Check managed provider/platform token inclusion
            var platformToken = GetExophasePlatformSlug(game);
            if (!string.IsNullOrWhiteSpace(platformToken) &&
                ProviderSettings.ManagedProviders.Contains(platformToken))
            {
                _logger.Debug($"Exophase IsCapable for '{game.Name}': true (token '{platformToken}' is managed)");
                return true;
            }

            // Log why it wasn't capable for debugging
            _logger.Debug($"Exophase IsCapable for '{game.Name}': false " +
                $"(Token={platformToken ?? "null"}, " +
                $"Source={game.Source?.Name ?? "null"}, " +
                $"Platforms={string.Join(", ", game.Platforms?.Select(p => p.Name) ?? Array.Empty<string>())})");
            return false;
        }

        /// <summary>
        /// Refreshes achievement data for games claimed by this provider.
        /// </summary>
        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            _logger?.Info($"[Exophase] === RefreshAsync START ===");
            _logger?.Info($"[Exophase] Games to refresh: {gamesToRefresh?.Count ?? 0}");

            var summary = new RebuildSummary();
            var payload = new RebuildPayload { Summary = summary };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                _logger?.Warn("[Exophase] RefreshAsync: No games to refresh");
                return payload;
            }

            var exophaseUserId = _sessionManager?.Username?.Trim();
            var isAuthenticated = _sessionManager?.IsAuthenticated == true;

            if (!isAuthenticated || string.IsNullOrWhiteSpace(exophaseUserId))
            {
                _logger?.Warn("[Exophase] Exophase session is not authenticated at refresh start. Refresh aborted.");
                payload.AuthRequired = true;
                return payload;
            }

            _logger?.Info($"[Exophase] Auth verified for user: {exophaseUserId}");

            var language = _settings.Persisted.GlobalLanguage ?? "english";
            _logger?.Debug($"[Exophase] Using language: {language}");

            // Fresh session: drop memoized slug resolutions so a bad historical match
            // gets one re-resolution opportunity per refresh.
            lock (_slugMemoLock)
            {
                _slugMemo.Clear();
            }

            // Load and validate the cookie snapshot once for the whole loop; every per-game fetch
            // reuses the cached cookies instead of decrypting the snapshot file per game.
            _apiClient.BeginCookieSession();
            try
            {
            foreach (var game in gamesToRefresh)
            {
                if (cancel.IsCancellationRequested)
                {
                    _logger?.Debug("[Exophase] RefreshAsync cancelled");
                    break;
                }

                if (game == null || game.Id == Guid.Empty)
                {
                    _logger?.Debug("[Exophase] Skipping null or empty game");
                    continue;
                }

                if (!IsCapable(game))
                {
                    _logger?.Debug($"[Exophase] Skipping game '{game.Name}' - not capable");
                    continue;
                }

                _logger?.Info($"[Exophase] >>> Starting refresh for game: '{game.Name}' (ID: {game.Id})");
                onGameStarting?.Invoke(game);

                try
                {
                    var data = await RefreshGameAsync(game, language, cancel).ConfigureAwait(false);

                    if (data != null)
                    {
                        _logger?.Info($"[Exophase] <<< Completed refresh for '{game.Name}': " +
                            $"HasAchievements={data.HasAchievements}, " +
                            $"AchievementCount={data.Achievements?.Count ?? 0}, " +
                            $"UnlockedCount={data.Achievements?.Count(a => a.Unlocked) ?? 0}");
                    }
                    else
                    {
                        _logger?.Warn($"[Exophase] <<< Completed refresh for '{game.Name}': data is null");
                    }

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, data).ConfigureAwait(false);
                    }

                    summary.GamesRefreshed++;
                    summary.RefreshedGameIds.Add(game.Id);
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
                    _logger?.Warn($"[Exophase] Refresh cancelled for game '{game.Name}'");
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"[Exophase] Failed to refresh game '{game.Name}' ({game.Id})");
                    summary.GamesWithoutAchievements++;

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, null).ConfigureAwait(false);
                    }
                }
            }

            _logger?.Info($"[Exophase] === RefreshAsync COMPLETE === " +
                $"GamesRefreshed={summary.GamesRefreshed}, " +
                $"WithAchievements={summary.GamesWithAchievements}, " +
                $"WithoutAchievements={summary.GamesWithoutAchievements}");

            return payload;
            }
            finally
            {
                _apiClient.EndCookieSession();
            }
        }

        /// <summary>
        /// Refreshes achievement data for a single game.
        /// </summary>
        private async Task<GameAchievementData> RefreshGameAsync(Game game, string language, CancellationToken cancel)
        {
            _logger?.Info($"[Exophase] === RefreshGameAsync START for '{game.Name}' (ID: {game.Id}) ===");
            _logger?.Debug($"[Exophase] Game details - Source: '{game.Source?.Name ?? "null"}', " +
                $"Platforms: [{string.Join(", ", game.Platforms?.Select(p => p.Name) ?? Array.Empty<string>())}], " +
                $"Language: {language}");

            // Resolve the Exophase slug deterministically.
            _logger?.Debug($"[Exophase] Resolving slug for game '{game.Name}'...");
            var slug = await ResolveExophaseSlugAsync(game, cancel).ConfigureAwait(false);
            _logger?.Info($"[Exophase] Resolved slug: '{slug ?? "null"}' for game '{game.Name}'");

            var providerPlatformKey = ResolveProviderPlatformKey(game, slug);
            _logger?.Debug($"[Exophase] Resolved providerPlatformKey: '{providerPlatformKey}'");

            if (string.IsNullOrWhiteSpace(slug))
            {
                _logger?.Warn($"[Exophase] Could not resolve slug for game '{game.Name}' - returning empty result");
                return CreateGameResult(game, providerPlatformKey, slug, false, new List<AchievementDetail>());
            }

            // Fetch achievement page (includes schema + user progress when authenticated). Pass the
            // resolved platform key so the endpoint is driven by the known platform instead of the
            // slug suffix, matching the friends path and removing the dependency on the search-API
            // slug carrying a recognized -ps*/-uplay suffix.
            var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(slug, providerPlatformKey);
            var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);
            _logger?.Info($"[Exophase] Fetching achievements from URL: {achievementUrl}");
            _logger?.Debug($"[Exophase] Accept-Language header: {acceptLanguage}");

            // Warm the CDN for award thumbnails (waitForImages) so the subsequent icon downloads for
            // this non-provider-owned game hit 200 instead of the initial cold-CDN 404. Thanks to the
            // stable per-game icon cache this warm cost is paid once per game rather than every refresh.
            var achievements = await _apiClient
                .FetchAchievementsAsync(achievementUrl, acceptLanguage, cancel, waitForImages: true)
                .ConfigureAwait(false);

            if (achievements == null || achievements.Count == 0)
            {
                _logger?.Warn($"[Exophase] No achievements found for slug: {slug}, URL: {achievementUrl}");
                return CreateGameResult(game, providerPlatformKey, slug, false, new List<AchievementDetail>());
            }

            _logger?.Info($"[Exophase] Fetched {achievements.Count} achievements for '{game.Name}'");

            // Log unlock statistics before applying rarity
            var unlockedBeforeRarity = achievements.Count(a => a.Unlocked);
            _logger?.Info($"[Exophase] Unlock stats BEFORE ApplyProviderOwnedRarity: {unlockedBeforeRarity}/{achievements.Count} unlocked");

            // Log first few achievements for debugging
            foreach (var ach in achievements.Take(5))
            {
                _logger?.Debug($"[Exophase] Sample achievement: '{ach.DisplayName}' | Unlocked: {ach.Unlocked} | " +
                    $"UnlockTime: {ach.UnlockTimeUtc?.ToString("o") ?? "null"} | GlobalPercent: {ach.GlobalPercentUnlocked}");
            }

            ApplyProviderOwnedRarity(achievements, providerPlatformKey);

            var unlockedAfterRarity = achievements.Count(a => a.Unlocked);
            _logger?.Info($"[Exophase] Unlock stats AFTER ApplyProviderOwnedRarity: {unlockedAfterRarity}/{achievements.Count} unlocked");

            _logger?.Info($"[Exophase] === RefreshGameAsync COMPLETE for '{game.Name}' ===");
            return CreateGameResult(game, providerPlatformKey, slug, true, achievements);
        }

        private GameAchievementData CreateGameResult(
            Game game,
            string providerPlatformKey,
            string slug,
            bool hasAchievements,
            List<AchievementDetail> achievements)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                ProviderPlatformKey = providerPlatformKey,
                LibrarySourceName = game?.Source?.Name,
                HasAchievements = hasAchievements,
                GameName = game.Name,
                ProviderGameKey = string.IsNullOrWhiteSpace(slug) ? null : slug.Trim(),
                PlayniteGameId = game.Id,
                Achievements = achievements ?? new List<AchievementDetail>()
            };
        }

        internal static void ApplyProviderOwnedRarity(IEnumerable<AchievementDetail> achievements, string providerPlatformKey)
        {
            if (achievements == null)
            {
                return;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var normalizedPercent = NormalizePercent(achievement.GlobalPercentUnlocked);
                achievement.GlobalPercentUnlocked = normalizedPercent;
                achievement.Rarity = ResolveProviderOwnedRarity(
                    providerPlatformKey,
                    normalizedPercent,
                    achievement.Points,
                    achievement.TrophyType,
                    achievement.Hidden,
                    achievement.ProgressNum,
                    achievement.ProgressDenom);
            }
        }

        internal static void ApplyManualSourceRarity(ManualAchievementLink link, IList<AchievementDetail> achievements)
        {
            if (link == null || achievements == null || achievements.Count == 0)
            {
                return;
            }

            ApplyProviderOwnedRarity(achievements, ResolveManualPlatformKey(link.SourceGameId));
        }

        internal static void GetManageAchievementsState(
            Game game,
            Guid gameId,
            out bool showToggle,
            out bool isManagedByPlatform,
            out bool useForGame,
            out string autoSlug,
            out bool hasSlugOverride,
            out string slugOverrideValue)
        {
            showToggle = false;
            isManagedByPlatform = false;
            useForGame = false;
            autoSlug = null;
            hasSlugOverride = false;
            slugOverrideValue = null;

            var settings = ProviderRegistry.Settings<ExophaseSettings>();
            if (settings?.IsEnabled != true || game == null)
            {
                return;
            }

            var gamePlatformSlug = GetExophasePlatformSlug(game);
            if (string.IsNullOrWhiteSpace(gamePlatformSlug))
            {
                return;
            }

            showToggle = true;
            isManagedByPlatform = settings.ManagedProviders.Contains(gamePlatformSlug);
            useForGame = GameCustomDataLookup.IsExophaseIncluded(gameId, settings);
            autoSlug = GeneratePreviewSlug(game);
            hasSlugOverride = GameCustomDataLookup.TryGetExophaseSlugOverride(gameId, out slugOverrideValue, settings);
        }

        internal static bool SetIncludedGame(Guid gameId, string gameName, bool include, Action persistSettingsForUi, ILogger logger)
        {
            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore != null)
            {
                var previous = customDataStore.TryLoad(gameId, out var customData) &&
                    customData?.ProviderOverride != null &&
                    string.Equals(customData.ProviderOverride.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase);
                if (previous == include)
                {
                    return true;
                }

                customDataStore.Update(gameId, data =>
                {
                    data.ProviderOverride = include
                        ? new ProviderOverrideData
                        {
                            ProviderKey = "Exophase"
                        }
                        : null;
                });
            }
            else
            {
                var settings = ProviderRegistry.Settings<ExophaseSettings>();
                if (settings == null)
                {
                    return false;
                }

                var changed = include
                    ? settings.IncludedGames.Add(gameId)
                    : settings.IncludedGames.Remove(gameId);
                if (!changed)
                {
                    return true;
                }

                ProviderRegistry.Write(settings);
            }

            persistSettingsForUi?.Invoke();

            if (include)
            {
                logger?.Info($"Added game '{gameName}' to Exophase included games");
            }
            else
            {
                logger?.Info($"Removed game '{gameName}' from Exophase included games");
            }

            return true;
        }

        internal static bool TrySetSlugOverride(Guid gameId, string gameName, string slug, Action persistSettingsForUi, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(slug))
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
                        ProviderKey = "Exophase",
                        Value = slug
                    };
                });
            }
            else
            {
                var settings = ProviderRegistry.Settings<ExophaseSettings>();
                if (settings == null)
                {
                    return false;
                }

                settings.SlugOverrides[gameId] = slug;
                settings.IncludedGames.Add(gameId);
                ProviderRegistry.Write(settings);
            }

            persistSettingsForUi?.Invoke();

            logger?.Info($"Set Exophase slug override for '{gameName}' to '{slug}'");
            return true;
        }

        internal static bool TryClearSlugOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            var customDataStore = PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
            if (customDataStore != null)
            {
                if (!customDataStore.TryLoad(gameId, out var customData) ||
                    customData?.ProviderOverride == null ||
                    !string.Equals(customData.ProviderOverride.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(customData.ProviderOverride.Value))
                {
                    return false;
                }

                customDataStore.Update(gameId, data =>
                {
                    data.ProviderOverride = new ProviderOverrideData
                    {
                        ProviderKey = "Exophase"
                    };
                });
            }
            else
            {
                var settings = ProviderRegistry.Settings<ExophaseSettings>();
                if (settings == null || !settings.SlugOverrides.Remove(gameId))
                {
                    return false;
                }

                ProviderRegistry.Write(settings);
            }

            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Exophase slug override for '{gameName}'");
            return true;
        }

        private static RarityTier ResolveProviderOwnedRarity(
            string providerPlatformKey,
            double? normalizedPercent,
            int? points,
            string trophyType,
            bool hidden,
            int? progressNum,
            int? progressDenom)
        {
            if (normalizedPercent.HasValue)
            {
                return PercentRarityHelper.GetRarityTier(normalizedPercent.Value);
            }

            if (string.Equals(providerPlatformKey, "Xbox", StringComparison.OrdinalIgnoreCase))
            {
                return GetRarityFromXboxPoints(points);
            }

            if (string.Equals(providerPlatformKey, "PSN", StringComparison.OrdinalIgnoreCase))
            {
                return GetRarityFromTrophyType(trophyType);
            }

            if (string.Equals(providerPlatformKey, "Epic", StringComparison.OrdinalIgnoreCase))
            {
                return GetRarityFromEpicXp(points);
            }

            return GetFallbackRarity(hidden, progressNum, progressDenom);
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static RarityTier GetRarityFromXboxPoints(int? points)
        {
            var value = Math.Max(0, points ?? 0);
            if (value >= 100)
            {
                return RarityTier.UltraRare;
            }

            if (value >= 50)
            {
                return RarityTier.Rare;
            }

            if (value >= 25)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }

        private static RarityTier GetRarityFromEpicXp(int? xp)
        {
            var value = Math.Max(0, xp ?? 0);
            if (value >= 200)
            {
                return RarityTier.UltraRare;
            }

            if (value >= 100)
            {
                return RarityTier.Rare;
            }

            if (value >= 50)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }

        private static RarityTier GetRarityFromTrophyType(string trophyType)
        {
            switch ((trophyType ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "platinum":
                case "p":
                    return RarityTier.UltraRare;
                case "gold":
                case "g":
                    return RarityTier.Rare;
                case "silver":
                case "s":
                    return RarityTier.Uncommon;
                default:
                    return RarityTier.Common;
            }
        }

        private static RarityTier GetFallbackRarity(bool hidden, int? progressNum, int? progressDenom)
        {
            if ((progressNum.HasValue || progressDenom.HasValue) &&
                progressDenom.HasValue &&
                progressDenom.Value > 0)
            {
                return RarityTier.Uncommon;
            }

            if (hidden)
            {
                return RarityTier.Rare;
            }

            return RarityTier.Common;
        }

        private static string ResolveManualPlatformKey(string sourceGameId)
        {
            var platformToken = ExtractPlatformTokenFromSlug(sourceGameId);
            return string.IsNullOrWhiteSpace(platformToken)
                ? "Unknown"
                : MapSlugToProviderPlatformKey(platformToken) ?? "Unknown";
        }

        private static string NormalizePlatformTokenForExophaseSlug(string platformToken)
        {
            if (string.IsNullOrWhiteSpace(platformToken))
            {
                return null;
            }

            switch (platformToken.Trim().ToLowerInvariant())
            {
                case "ubisoft":
                case "uplay":
                    return "uplay";
                case "xbox360":
                case "xbox 360":
                    return "xbox-360";
                case "xboxone":
                case "xbox one":
                    return "xbox-one";
                case "playstation 3":
                case "playstation3":
                    return "ps3";
                case "playstation 4":
                case "playstation4":
                    return "ps4";
                default:
                    return platformToken.Trim().ToLowerInvariant();
            }
        }

        #endregion

        #region Slug Resolution

        /// <summary>
        /// Resolves an Exophase game slug for a Playnite game using deterministic linking.
        /// Priority: Manual override -> Cache -> API search with a resolved platform token.
        /// </summary>
        public async Task<string> ResolveExophaseSlugAsync(Game game, CancellationToken ct)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            // Check for manual override first.
            if (GameCustomDataLookup.TryGetExophaseSlugOverride(game.Id, out var overrideSlug, ProviderSettings) &&
                !string.IsNullOrWhiteSpace(overrideSlug))
            {
                _logger?.Debug($"[Exophase] Using override slug for '{game.Name}': {overrideSlug}");
                return overrideSlug;
            }

            // Check the session memo.
            if (TryGetCachedSlug(game.Id, out var memoSlug))
            {
                return memoSlug;
            }

            var platformSlug = NormalizePlatformTokenForExophaseSlug(GetExophasePlatformSlug(game));
            var normalizedName = ExophaseGameNameMatcher.NormalizeGameName(game.Name);
            _logger?.Debug($"[Exophase] Resolving slug for '{game.Name}' (platform: {platformSlug ?? "unknown"})");

            if (string.IsNullOrWhiteSpace(platformSlug))
            {
                _logger?.Debug($"[Exophase] No platform token resolved for '{game.Name}' - skipping name-only fallback");
                return null;
            }

            try
            {
                // Search with platform filter.
                var games = await _apiClient.SearchGamesAsync(normalizedName, platformSlug, ct).ConfigureAwait(false);
                if (games == null || games.Count == 0)
                {
                    _logger?.Debug($"[Exophase] No games found for '{normalizedName}' on platform '{platformSlug}'");
                    return null;
                }

                var bestMatch = FindBestMatch(normalizedName, games, platformSlug);
                if (bestMatch == null)
                {
                    _logger?.Debug($"[Exophase] No confident match for '{normalizedName}'");
                    return null;
                }

                var slug = ExophaseApiClient.ExtractSlugFromUrl(bestMatch.EndpointAwards);
                if (string.IsNullOrWhiteSpace(slug))
                {
                    _logger?.Debug($"[Exophase] Could not extract slug from {bestMatch.EndpointAwards}");
                    return null;
                }

                lock (_slugMemoLock)
                {
                    _slugMemo[game.Id] = slug;
                }

                _logger?.Debug($"[Exophase] Resolved '{game.Name}' -> {slug}");
                return slug;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[Exophase] Failed to resolve slug for '{game.Name}'");
                return null;
            }
        }

        /// <summary>
        /// Finds the best matching game from search results.
        /// </summary>
        private ExophaseGame FindBestMatch(string gameName, List<ExophaseGame> games, string platformSlug)
        {
            if (games == null || games.Count == 0)
            {
                return null;
            }

            var normalizedSearch = ExophaseGameNameMatcher.NormalizeGameName(gameName);

            var scored = games.Select(g =>
            {
                var title = ExophaseGameNameMatcher.NormalizeGameName(g.Title ?? string.Empty);
                var score = ExophaseGameNameMatcher.ComputeMatchScore(normalizedSearch, title);

                if (score > 0 && !string.IsNullOrWhiteSpace(platformSlug) && !string.IsNullOrWhiteSpace(g.EndpointAwards))
                {
                    if (g.EndpointAwards.IndexOf($"-{platformSlug}", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 20;
                    }
                }

                return (Game: g, Score: score);
            }).ToList();

            var best = scored.OrderByDescending(x => x.Score).FirstOrDefault();
            return best.Score > 0 ? best.Game : null;
        }

        #endregion

        #region Platform and Provider Mapping

        /// <summary>
        /// Gets the Exophase platform slug for a game.
        /// Priority: Source (PC stores) -> Platform.SpecificationId -> Platform.Name
        /// </summary>
        public static string GetExophasePlatformSlug(Game game)
        {
            if (game == null) return null;

            // PC games: Source identifies the store (Steam, GOG, Epic, etc.)
            var sourceSlug = MapSourceToSlug(game.Source?.Name);
            if (!string.IsNullOrWhiteSpace(sourceSlug)) return sourceSlug;

            // Consoles: Check platform specification ID and name
            if (game.Platforms == null || game.Platforms.Count == 0) return null;

            foreach (var platform in game.Platforms)
            {
                if (platform == null) continue;

                // Try specification ID first (more precise)
                var slug = MapSpecificationIdToSlug(platform.SpecificationId);
                if (!string.IsNullOrWhiteSpace(slug)) return slug;

                // Fall back to platform name
                slug = MapPlatformNameToSlug(platform.Name);
                if (!string.IsNullOrWhiteSpace(slug)) return slug;
            }

            return null;
        }

        /// <summary>
        /// Maps a Playnite Source name to an Exophase slug.
        /// </summary>
        private static string MapSourceToSlug(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName)) return null;

            var name = sourceName.ToLowerInvariant();

            if (name.Contains("steam")) return "steam";
            if (name.Contains("gog") || name.Contains("good old games")) return "gog";
            if (name.Contains("epic")) return "epic";
            if (name.Contains("battle.net") || name.Contains("battlenet") || ContainsDelimitedToken(name, "blizzard")) return "blizzard";
            if (name.Contains("origin") || name.Contains("electronic arts") || name.Contains("ea app") || ContainsDelimitedToken(name, "ea")) return "origin";
            if (name.Contains("google play") || name.Contains("googleplay") || name.Contains("android") || ContainsDelimitedToken(name, "android")) return "android";
            if (name.Contains("apple arcade") || name.Contains("app store") || ContainsDelimitedToken(name, "ios") || ContainsDelimitedToken(name, "apple")) return "apple";
            if (name.Contains("ubisoft") || name.Contains("uplay") || name.Contains("ubisoft connect")) return "ubisoft";

            return null;
        }

        private static bool ContainsDelimitedToken(string value, string token)
        {
            if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var parts = value.Split(new[] { ' ', '-', '_', '.', ':', '/', '\\', '|', '(', ')', '[', ']' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Any(part => string.Equals(part, token, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Maps a Playnite platform specification ID to an Exophase slug.
        /// </summary>
        private static string MapSpecificationIdToSlug(string specId)
        {
            if (string.IsNullOrWhiteSpace(specId)) return null;

            var id = specId.ToLowerInvariant();

            // PlayStation platforms
            if (id.StartsWith("sony_playstation") || id == "sony_vita") return "psn";

            // Xbox platforms
            if (id.Contains("360")) return "xbox-360";
            if (id.StartsWith("xbox")) return "xbox";

            return null;
        }

        /// <summary>
        /// Maps a Playnite platform name to an Exophase slug.
        /// </summary>
        private static string MapPlatformNameToSlug(string platformName)
        {
            if (string.IsNullOrWhiteSpace(platformName)) return null;

            var name = platformName.ToLowerInvariant();

            // Reuse the source mapping rules for store names.
            var sourceLikeSlug = MapSourceToSlug(name);
            if (!string.IsNullOrWhiteSpace(sourceLikeSlug)) return sourceLikeSlug;

            // PlayStation (PS1-PS5, Vita, PSN)
            if (name.Contains("playstation") || name.Contains("psn") ||
                name.Contains("ps1") || name.Contains("ps2") || name.Contains("ps3") ||
                name.Contains("ps4") || name.Contains("ps5") || name.Contains("vita"))
            {
                return "psn";
            }

            // Xbox (360, One, Series)
            if (name.Contains("xbox 360") || name.Contains("xbox360")) return "xbox-360";
            if (name.Contains("xbox")) return "xbox";

            // RetroAchievements
            if (name.Contains("retro") || name.Contains("retroachievements")) return "retro";

            // Android / Google Play
            if (name.Contains("android") || name.Contains("google play") || name.Contains("googleplay")) return "android";

            // Apple / App Store
            if (name.Contains("apple arcade") || name.Contains("app store") || name.Contains("ios") || ContainsDelimitedToken(name, "apple")) return "apple";

            return null;
        }

        /// <summary>
        /// Maps an Exophase platform slug back to the corresponding PlayniteAchievements ProviderKey.
        /// </summary>
        public static string MapSlugToProviderPlatformKey(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;

            // Route through the single canonical family map so this and the friends path agree on
            // every platform. Unknown slugs keep the historical Title-cased fallback.
            var providerKey = ExophaseFriendPlatformMatcher.ResolveProviderPlatformKey(slug);
            if (!string.IsNullOrWhiteSpace(providerKey))
            {
                return providerKey;
            }

            var trimmed = slug.Trim();
            return trimmed.Length == 0 ? null : char.ToUpper(trimmed[0]) + trimmed.Substring(1);
        }

        private static string ExtractPlatformTokenFromSlug(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return null;
            }

            var normalized = slug.Trim().ToLowerInvariant();
            foreach (var token in KnownExophasePlatformTokens)
            {
                if (normalized == token || normalized.EndsWith("-" + token, StringComparison.Ordinal))
                {
                    return token;
                }
            }

            return null;
        }

        private string ResolveProviderPlatformKey(Game game, string resolvedSlug)
        {
            if (game != null &&
                GameCustomDataLookup.TryGetExophaseSlugOverride(game.Id, out var overrideSlug, ProviderSettings) &&
                !string.IsNullOrWhiteSpace(overrideSlug))
            {
                var overrideToken = ExtractPlatformTokenFromSlug(overrideSlug);
                if (!string.IsNullOrWhiteSpace(overrideToken))
                {
                    return MapSlugToProviderPlatformKey(overrideToken);
                }
            }

            var resolvedToken = ExtractPlatformTokenFromSlug(resolvedSlug);
            if (!string.IsNullOrWhiteSpace(resolvedToken))
            {
                return MapSlugToProviderPlatformKey(resolvedToken);
            }

            var gameToken = GetExophasePlatformSlug(game);
            var mappedFromGame = MapSlugToProviderPlatformKey(gameToken);
            if (!string.IsNullOrWhiteSpace(mappedFromGame))
            {
                return mappedFromGame;
            }

            // Exophase rows must always carry a platform key so UI grouping never falls back to provider key.
            return "Unknown";
        }

        /// <summary>
        /// Generates a preview slug for display purposes.
        /// Format: normalized-game-name-platform-slug.
        /// </summary>
        public static string GeneratePreviewSlug(Game game)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            var platformSlug = NormalizePlatformTokenForExophaseSlug(GetExophasePlatformSlug(game));
            if (string.IsNullOrWhiteSpace(platformSlug))
            {
                return null;
            }

            var normalizedName = ExophaseGameNameMatcher.NormalizeGameNameForSlug(game.Name);
            return $"{normalizedName}-{platformSlug}";
        }

        #endregion

        #region IDataProvider Settings Members

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new ExophaseSettingsView(_sessionManager);

        #endregion
    }
}






