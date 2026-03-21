using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services;
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
    internal sealed class ExophaseDataProvider : IDataProvider
    {
        #region Fields

        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ExophaseApiClient _apiClient;
        private readonly Dictionary<Guid, string> _slugCache = new Dictionary<Guid, string>();
        private readonly object _slugCacheLock = new object();
        private static readonly TimeSpan SlugCacheTtl = TimeSpan.FromHours(1);
        private static readonly string[] KnownExophasePlatformTokens =
        {
            "steam", "gog", "epic", "psn", "xbox", "retro"
        };
        private readonly Dictionary<Guid, DateTime> _slugCacheTimestamps = new Dictionary<Guid, DateTime>();
        private ExophaseSettings _providerSettings;

        #endregion

        #region Provider Metadata

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");
        public string ProviderKey => "Exophase";
        public string ProviderIconKey => "ProviderIconExophase";
        public string ProviderColorHex => "#FF6B35";

        /// <summary>
        /// Checks if Exophase session is authenticated.
        /// </summary>
        public bool IsAuthenticated => _sessionManager?.IsAuthenticated ?? false;

        #endregion

        #region Construction

        public ExophaseDataProvider(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            IPlayniteAPI playniteApi,
            ExophaseSessionManager sessionManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _apiClient = new ExophaseApiClient(playniteApi, logger);

            _providerSettings = ProviderSettingsHelper.Load<ExophaseSettings>(settings.Persisted, "Exophase");
        }

        #endregion

        #region Capability and Refresh Flow

        /// <summary>
        /// Checks if this provider can handle a game.
        /// Game is claimed if:
        /// 1. ExophaseEnabled is true AND
        /// 2. Game is in ExophaseIncludedGames OR game token is in ExophaseManagedProviders
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (!_settings.Persisted.ExophaseEnabled)
            {
                return false;
            }

            // Check explicit game inclusion first
            if (_settings.Persisted.ExophaseIncludedGames.Contains(game.Id))
            {
                _logger.Debug($"Exophase IsCapable for '{game.Name}': true (explicitly included)");
                return true;
            }

            // Check managed provider/platform token inclusion
            var platformToken = GetExophasePlatformSlug(game);
            if (!string.IsNullOrWhiteSpace(platformToken) &&
                _settings.Persisted.ExophaseManagedProviders.Contains(platformToken))
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
            var summary = new RebuildSummary();
            var payload = new RebuildPayload { Summary = summary };

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return payload;
            }

            if (!IsAuthenticated)
            {
                _logger?.Warn("[Exophase] Cannot refresh: not authenticated");
                return payload;
            }

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

                if (!IsCapable(game))
                {
                    continue;
                }

                onGameStarting?.Invoke(game);

                try
                {
                    var data = await RefreshGameAsync(game, language, cancel).ConfigureAwait(false);

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, data).ConfigureAwait(false);
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
                    _logger?.Error(ex, $"[Exophase] Failed to refresh game '{game.Name}' ({game.Id})");
                    summary.GamesWithoutAchievements++;

                    if (onGameCompleted != null)
                    {
                        await onGameCompleted(game, null).ConfigureAwait(false);
                    }
                }
            }

            return payload;
        }

        /// <summary>
        /// Refreshes achievement data for a single game.
        /// </summary>
        private async Task<GameAchievementData> RefreshGameAsync(Game game, string language, CancellationToken cancel)
        {
            // Resolve the Exophase slug deterministically.
            var slug = await ResolveExophaseSlugAsync(game, cancel).ConfigureAwait(false);
            var providerPlatformKey = ResolveProviderPlatformKey(game, slug);

            if (string.IsNullOrWhiteSpace(slug))
            {
                _logger?.Debug($"[Exophase] Could not resolve slug for game '{game.Name}'");
                return CreateGameResult(game, providerPlatformKey, false, new List<AchievementDetail>());
            }

            // Fetch achievement page (includes schema + user progress when authenticated).
            var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(slug);
            var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);
            var achievements = await _apiClient
                .FetchAchievementsAsync(achievementUrl, acceptLanguage, cancel)
                .ConfigureAwait(false);

            if (achievements == null || achievements.Count == 0)
            {
                _logger?.Debug($"[Exophase] No achievements found for slug: {slug}");
                return CreateGameResult(game, providerPlatformKey, false, new List<AchievementDetail>());
            }

            return CreateGameResult(game, providerPlatformKey, true, achievements);
        }

        private GameAchievementData CreateGameResult(
            Game game,
            string providerPlatformKey,
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
                PlayniteGameId = game.Id,
                Achievements = achievements ?? new List<AchievementDetail>()
            };
        }

        #endregion

        #region Slug Resolution

        /// <summary>
        /// Resolves an Exophase game slug for a Playnite game using deterministic linking.
        /// Priority: Manual override -> Cache -> API search.
        /// </summary>
        public async Task<string> ResolveExophaseSlugAsync(Game game, CancellationToken ct)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            // Check for manual override first.
            if (_settings.Persisted.ExophaseSlugOverrides.TryGetValue(game.Id, out var overrideSlug) &&
                !string.IsNullOrWhiteSpace(overrideSlug))
            {
                _logger?.Debug($"[Exophase] Using override slug for '{game.Name}': {overrideSlug}");
                return overrideSlug;
            }

            // Check cache.
            lock (_slugCacheLock)
            {
                if (_slugCache.TryGetValue(game.Id, out var cachedSlug))
                {
                    if (_slugCacheTimestamps.TryGetValue(game.Id, out var timestamp) &&
                        DateTime.UtcNow - timestamp < SlugCacheTtl)
                    {
                        return cachedSlug;
                    }

                    // Cache expired, remove.
                    _slugCache.Remove(game.Id);
                    _slugCacheTimestamps.Remove(game.Id);
                }
            }

            var platformSlug = GetExophasePlatformSlug(game);
            var normalizedName = NormalizeGameName(game.Name);
            _logger?.Debug($"[Exophase] Resolving slug for '{game.Name}' (platform: {platformSlug ?? "unknown"})");

            try
            {
                // Search with platform filter.
                var games = await _apiClient.SearchGamesAsync(normalizedName, platformSlug, ct).ConfigureAwait(false);
                if (games == null || games.Count == 0)
                {
                    // Fallback: try without platform filter.
                    games = await _apiClient.SearchGamesAsync(normalizedName, ct).ConfigureAwait(false);
                    if (games == null || games.Count == 0)
                    {
                        _logger?.Debug($"[Exophase] No games found for '{normalizedName}'");
                        return null;
                    }
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

                lock (_slugCacheLock)
                {
                    _slugCache[game.Id] = slug;
                    _slugCacheTimestamps[game.Id] = DateTime.UtcNow;
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

            var normalizedSearch = gameName.ToLowerInvariant().Trim();

            var scored = games.Select(g =>
            {
                var score = 0;
                var title = (g.Title ?? string.Empty).ToLowerInvariant().Trim();

                if (title == normalizedSearch)
                {
                    score += 100;
                }
                else if (title.StartsWith(normalizedSearch))
                {
                    score += 80;
                }
                else if (title.Contains(normalizedSearch))
                {
                    score += 60;
                }
                else if (normalizedSearch.Contains(title))
                {
                    score += 50;
                }
                else
                {
                    score += -100;
                }

                if (!string.IsNullOrWhiteSpace(platformSlug) && !string.IsNullOrWhiteSpace(g.EndpointAwards))
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

            return null;
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
            if (name.Contains("xbox")) return "xbox";

            // RetroAchievements
            if (name.Contains("retro") || name.Contains("retroachievements")) return "retro";

            return null;
        }

        /// <summary>
        /// Maps an Exophase platform slug back to the corresponding PlayniteAchievements ProviderKey.
        /// </summary>
        public static string MapSlugToProviderPlatformKey(string slug)
        {
            if (string.IsNullOrWhiteSpace(slug)) return null;

            switch (slug)
            {
                case "steam": return "Steam";
                case "gog": return "GOG";
                case "epic": return "Epic";
                case "xbox": return "Xbox";
                case "psn": return "PSN";
                case "retro": return "RetroAchievements";
                default:
                    return char.ToUpper(slug[0]) + slug.Substring(1);
            }
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
                _settings.Persisted.ExophaseSlugOverrides.TryGetValue(game.Id, out var overrideSlug) &&
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

            var platformSlug = GetExophasePlatformSlug(game);
            if (string.IsNullOrWhiteSpace(platformSlug))
            {
                return null;
            }

            var normalizedName = NormalizeGameNameForSlug(game.Name);
            return $"{normalizedName}-{platformSlug}";
        }

        #endregion

        #region Name Normalization

        /// <summary>
        /// Normalizes a game name for use in a slug.
        /// Lowercase, spaces/special chars to hyphens, remove consecutive hyphens.
        /// </summary>
        private static string NormalizeGameNameForSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalized = NormalizeGameName(name).ToLowerInvariant();

            var chars = new char[normalized.Length];
            var charIndex = 0;
            var lastWasHyphen = false;

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                {
                    chars[charIndex++] = c;
                    lastWasHyphen = false;
                }
                else if (!lastWasHyphen)
                {
                    chars[charIndex++] = '-';
                    lastWasHyphen = true;
                }
            }

            if (charIndex > 0 && chars[charIndex - 1] == '-')
            {
                charIndex--;
            }

            return new string(chars, 0, charIndex);
        }

        /// <summary>
        /// Normalizes a game name for searching.
        /// Removes edition suffixes and special characters.
        /// </summary>
        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name;
            }

            var normalized = name.Trim();

            var suffixes = new[]
            {
                " - Definitive Edition",
                " - Game of the Year Edition",
                " - Complete Edition",
                " - Collector's Edition",
                " - Deluxe Edition",
                " - Standard Edition",
                " - Ultimate Edition",
                " - Premium Edition",
                " Definitive Edition",
                " Game of the Year Edition",
                " Complete Edition",
                " Collector's Edition",
                " Deluxe Edition",
                " Standard Edition",
                " Ultimate Edition",
                " Premium Edition",
                " (Definitive Edition)",
                " (Game of the Year Edition)",
                " (Complete Edition)",
                " (Collector's Edition)",
                " (Deluxe Edition)",
                " (Standard Edition)",
                " (Ultimate Edition)",
                " (Premium Edition)"
            };

            foreach (var suffix in suffixes)
            {
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - suffix.Length);
                    break;
                }
            }

            return normalized.Trim();
        }

        #endregion

        #region IDataProvider Settings Members

        /// <inheritdoc />
        public IProviderSettings GetSettings() => _providerSettings;

        /// <inheritdoc />
        public IProviderSettings CreateDefaultSettings() => new ExophaseSettings();

        /// <inheritdoc />
        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is ExophaseSettings exophaseSettings)
            {
                _providerSettings = exophaseSettings;
                ProviderSettingsHelper.Save(_settings.Persisted, exophaseSettings);
            }
        }

        #endregion
    }
}
