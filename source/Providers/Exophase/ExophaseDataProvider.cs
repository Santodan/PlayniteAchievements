using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
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
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ExophaseApiClient _apiClient;
        private readonly Dictionary<Guid, string> _slugCache = new Dictionary<Guid, string>();
        private readonly object _slugCacheLock = new object();
        private static readonly TimeSpan SlugCacheTtl = TimeSpan.FromHours(1);
        private readonly Dictionary<Guid, DateTime> _slugCacheTimestamps = new Dictionary<Guid, DateTime>();

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Exophase");
        public string ProviderKey => "Exophase";
        public string ProviderIconKey => "ProviderIconExophase";
        public string ProviderColorHex => "#FF6B35";

        /// <summary>
        /// Checks if Exophase session is authenticated.
        /// </summary>
        public bool IsAuthenticated => _sessionManager?.IsAuthenticated ?? false;

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
        }

        /// <summary>
        /// Checks if this provider can handle a game.
        /// Game is claimed if:
        /// 1. ExophaseEnabled is true AND
        /// 2. Game is in ExophaseIncludedGames OR game's platform is in ExophaseManagedPlatforms
        /// </summary>
        public bool IsCapable(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            // Check master toggle
            if (!_settings.Persisted.ExophaseEnabled)
            {
                return false;
            }

            // Check explicit game inclusion (for games on non-auto-claimed platforms)
            if (_settings.Persisted.ExophaseIncludedGames.Contains(game.Id))
            {
                return true;
            }

            // Check platform-based auto-claim
            var platformSlug = GetExophasePlatformSlug(game);
            if (!string.IsNullOrWhiteSpace(platformSlug) &&
                _settings.Persisted.ExophaseManagedPlatforms.Contains(platformSlug))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets the Exophase platform slug for a game based on its Playnite platforms.
        /// Returns the first matching platform slug.
        /// </summary>
        private string GetExophasePlatformSlug(Game game)
        {
            if (game?.Platforms == null || game.Platforms.Count == 0)
            {
                return null;
            }

            foreach (var platform in game.Platforms)
            {
                if (platform == null) continue;
                var slug = MapPlaynitePlatformToExophaseSlug(platform);
                if (!string.IsNullOrWhiteSpace(slug))
                {
                    return slug;
                }
            }

            return null;
        }

        /// <summary>
        /// Maps a Playnite platform to an Exophase platform slug.
        /// </summary>
        private static string MapPlaynitePlatformToExophaseSlug(Platform platform)
        {
            if (platform == null || string.IsNullOrWhiteSpace(platform.Name))
            {
                return null;
            }

            var name = platform.Name.ToLowerInvariant();

            // Steam
            if (name.Contains("steam") || name.Contains("pc") && name.Contains("steam"))
            {
                return "steam";
            }

            // PlayStation
            if (name.Contains("playstation") || name.Contains("psn") ||
                name.Contains("ps1") || name.Contains("ps2") || name.Contains("ps3") ||
                name.Contains("ps4") || name.Contains("ps5") || name.Contains("vita"))
            {
                return "psn";
            }

            // Xbox
            if (name.Contains("xbox") || name.Contains("xbox360") ||
                name.Contains("xbox one") || name.Contains("xbox series"))
            {
                return "xbox";
            }

            // GOG
            if (name.Contains("gog") || name.Contains("good old games"))
            {
                return "gog";
            }

            // Epic
            if (name.Contains("epic") || name.Contains("epic games"))
            {
                return "epic";
            }

            // EA / Electronic Arts
            if (name.Contains("ea") || name.Contains("origin") || name.Contains("electronic arts"))
            {
                return "ea";
            }

            // Blizzard / Battle.net
            if (name.Contains("blizzard") || name.Contains("battle.net") || name.Contains("battlenet"))
            {
                return "blizzard";
            }

            // Nintendo
            if (name.Contains("nintendo") || name.Contains("switch") ||
                name.Contains("wii") || name.Contains("gamecube") ||
                name.Contains("3ds") || name.Contains("ds"))
            {
                return "nintendo";
            }

            // RetroAchievements (via Exophase)
            if (name.Contains("retro") || name.Contains("retroachievements"))
            {
                return "retro";
            }

            return null;
        }

        /// <summary>
        /// Resolves an Exophase game slug for a Playnite game using deterministic linking.
        /// Searches by game name filtered by platform to get the correct variant.
        /// </summary>
        public async Task<string> ResolveExophaseSlugAsync(Game game, CancellationToken ct)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            // Check cache first
            lock (_slugCacheLock)
            {
                if (_slugCache.TryGetValue(game.Id, out var cachedSlug))
                {
                    if (_slugCacheTimestamps.TryGetValue(game.Id, out var timestamp) &&
                        DateTime.UtcNow - timestamp < SlugCacheTtl)
                    {
                        return cachedSlug;
                    }
                    // Cache expired, remove
                    _slugCache.Remove(game.Id);
                    _slugCacheTimestamps.Remove(game.Id);
                }
            }

            var platformSlug = GetExophasePlatformSlug(game);
            var normalizedName = NormalizeGameName(game.Name);

            _logger?.Debug($"[Exophase] Resolving slug for '{game.Name}' (platform: {platformSlug ?? "unknown"})");

            try
            {
                // Search with platform filter
                var games = await _apiClient.SearchGamesAsync(normalizedName, platformSlug, ct).ConfigureAwait(false);
                if (games == null || games.Count == 0)
                {
                    // Fallback: try without platform filter
                    games = await _apiClient.SearchGamesAsync(normalizedName, ct).ConfigureAwait(false);
                    if (games == null || games.Count == 0)
                    {
                        _logger?.Debug($"[Exophase] No games found for '{normalizedName}'");
                        return null;
                    }
                }

                // Find best match
                var bestMatch = FindBestMatch(normalizedName, games, platformSlug);
                if (bestMatch == null)
                {
                    _logger?.Debug($"[Exophase] No confident match for '{normalizedName}'");
                    return null;
                }

                // Extract slug from endpoint_awards URL
                var slug = ExophaseApiClient.ExtractSlugFromUrl(bestMatch.EndpointAwards);
                if (string.IsNullOrWhiteSpace(slug))
                {
                    _logger?.Debug($"[Exophase] Could not extract slug from {bestMatch.EndpointAwards}");
                    return null;
                }

                // Cache result
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

            // Remove common edition suffixes
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

            // Score each game
            var scored = games.Select(g =>
            {
                var score = 0;
                var title = (g.Title ?? "").ToLowerInvariant().Trim();

                // Exact match
                if (title == normalizedSearch)
                {
                    score += 100;
                }
                // Starts with search term
                else if (title.StartsWith(normalizedSearch))
                {
                    score += 80;
                }
                // Contains search term
                else if (title.Contains(normalizedSearch))
                {
                    score += 60;
                }
                // Search term contains title
                else if (normalizedSearch.Contains(title))
                {
                    score += 50;
                }
                // No match
                else
                {
                    score += -100;
                }

                // Bonus for platform match in slug
                if (!string.IsNullOrWhiteSpace(platformSlug) && !string.IsNullOrWhiteSpace(g.EndpointAwards))
                {
                    if (g.EndpointAwards.IndexOf($"-{platformSlug}", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        score += 20;
                    }
                }

                return (Game: g, Score: score);
            }).ToList();

            // Return highest scoring game if score is positive
            var best = scored.OrderByDescending(x => x.Score).FirstOrDefault();
            return best.Score > 0 ? best.Game : null;
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
                    var data = await RefreshGameAsync(game, language, cancel);

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
                    _logger?.Error(ex, $"[Exophase] Failed to refresh game '{game.Name}' ({game.Id})");
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
        /// Refreshes achievement data for a single game.
        /// </summary>
        private async Task<GameAchievementData> RefreshGameAsync(Game game, string language, CancellationToken cancel)
        {
            // Resolve the Exophase slug deterministically
            var slug = await ResolveExophaseSlugAsync(game, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(slug))
            {
                _logger?.Debug($"[Exophase] Could not resolve slug for game '{game.Name}'");
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

            // Fetch achievement page (includes schema + user progress when authenticated)
            var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(slug);
            var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(language);

            var achievements = await _apiClient.FetchAchievementsAsync(achievementUrl, acceptLanguage, cancel).ConfigureAwait(false);
            if (achievements == null || achievements.Count == 0)
            {
                _logger?.Debug($"[Exophase] No achievements found for slug: {slug}");
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

            return new GameAchievementData
            {
                LastUpdatedUtc = DateTime.UtcNow,
                ProviderKey = ProviderKey,
                LibrarySourceName = game.PluginId.ToString(),
                HasAchievements = true,
                GameName = game.Name,
                PlayniteGameId = game.Id,
                Game = game,
                Achievements = achievements
            };
        }
    }
}
