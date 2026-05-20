using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    internal sealed class ExophaseRarityEnricher
    {
        private static readonly TimeSpan SlugCacheTtl = TimeSpan.FromHours(1);

        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ExophaseApiClient _apiClient;
        private readonly Dictionary<string, string> _slugCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _slugCacheTimestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private bool _authChecked;
        private bool _isReady;

        public ExophaseRarityEnricher(
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            string pluginUserDataPath)
        {
            _playniteApi = playniteApi;
            _logger = logger;
            _settings = settings;

            if (_playniteApi != null && !string.IsNullOrWhiteSpace(pluginUserDataPath))
            {
                _sessionManager = new ExophaseSessionManager(_playniteApi, _logger, pluginUserDataPath);
                _apiClient = new ExophaseApiClient(_playniteApi, _logger, _sessionManager.CookieSnapshotStore);
            }
        }

        public async Task InitializeAsync(CancellationToken ct)
        {
            if (_authChecked)
            {
                return;
            }

            _authChecked = true;

            if (_sessionManager == null || _apiClient == null)
            {
                _logger?.Warn("[ExophaseRarity] Missing Playnite API or plugin data path; rarity enrichment disabled for this scan.");
                return;
            }

            try
            {
                var result = await _sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
                _isReady = result?.IsSuccess == true;

                if (!_isReady)
                {
                    _logger?.Warn("[ExophaseRarity] Exophase authentication is required; native rarity will be kept.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[ExophaseRarity] Exophase auth probe failed; native rarity will be kept.");
            }
        }

        public async Task EnrichAsync(
            Game game,
            IList<AchievementDetail> achievements,
            string platformSlugHint,
            string providerPlatformKey,
            CancellationToken ct)
        {
            if (!_isReady || game == null || achievements == null || achievements.Count == 0)
            {
                return;
            }

            try
            {
                var platformSlug = ResolvePlatformSlug(game, platformSlugHint);
                var slugs = await ResolveSlugsAsync(game, platformSlug, ct).ConfigureAwait(false);
                if (slugs.Count == 0)
                {
                    _logger?.Debug($"[ExophaseRarity] No Exophase slug resolved for '{game.Name}'.");
                    return;
                }

                var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(_settings?.Persisted?.GlobalLanguage);
                IList<AchievementDetail> exophaseAchievements = null;
                string resolvedSlug = null;

                foreach (var slug in slugs)
                {
                    var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(slug);
                    var fetchedAchievements = await _apiClient
                        .FetchAchievementsAsync(achievementUrl, acceptLanguage, ct)
                        .ConfigureAwait(false);

                    if (fetchedAchievements == null || fetchedAchievements.Count == 0)
                    {
                        _logger?.Debug($"[ExophaseRarity] No Exophase achievements found for '{game.Name}' ({slug}).");
                        continue;
                    }

                    exophaseAchievements = fetchedAchievements;
                    resolvedSlug = slug;
                    break;
                }

                if (exophaseAchievements == null || exophaseAchievements.Count == 0)
                {
                    return;
                }

                ExophaseDataProvider.ApplyProviderOwnedRarity(exophaseAchievements, providerPlatformKey);

                var updated = ApplyRarity(achievements, exophaseAchievements);
                _logger?.Info($"[ExophaseRarity] Applied rarity to {updated}/{achievements.Count} achievements for '{game.Name}' using slug '{resolvedSlug}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[ExophaseRarity] Failed to enrich rarity for '{game?.Name}'. Native rarity will be kept.");
            }
        }

        private async Task<List<string>> ResolveSlugsAsync(Game game, string platformSlug, CancellationToken ct)
        {
            if (GameCustomDataLookup.TryGetExophaseSlugOverride(
                    game.Id,
                    out var overrideSlug,
                    ProviderRegistry.Settings<ExophaseSettings>()) &&
                !string.IsNullOrWhiteSpace(overrideSlug))
            {
                return new List<string> { overrideSlug.Trim() };
            }

            var platformSlugs = GetPlatformSlugCandidates(platformSlug);
            var cacheKey = $"{game.Id:N}:{string.Join("|", platformSlugs)}";
            if (TryGetCachedSlug(cacheKey, out var cachedSlug))
            {
                return new List<string> { cachedSlug };
            }

            var normalizedName = NormalizeGameName(game.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                foreach (var candidatePlatformSlug in platformSlugs)
                {
                    var games = await _apiClient.SearchGamesAsync(normalizedName, candidatePlatformSlug, ct).ConfigureAwait(false);
                    var match = FindBestSearchMatch(normalizedName, games, candidatePlatformSlug);
                    var resolvedSlug = ExophaseApiClient.ExtractSlugFromUrl(match?.EndpointAwards);
                    if (!string.IsNullOrWhiteSpace(resolvedSlug))
                    {
                        CacheSlug(cacheKey, resolvedSlug);
                        return new List<string> { resolvedSlug };
                    }
                }
            }

            return platformSlugs
                .Select(candidatePlatformSlug => GenerateDefaultSlug(game, candidatePlatformSlug))
                .Where(defaultSlug => !string.IsNullOrWhiteSpace(defaultSlug))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private bool TryGetCachedSlug(string cacheKey, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return false;
            }

            if (!_slugCache.TryGetValue(cacheKey, out var cachedSlug))
            {
                return false;
            }

            if (!_slugCacheTimestamps.TryGetValue(cacheKey, out var timestamp) ||
                DateTime.UtcNow - timestamp >= SlugCacheTtl)
            {
                _slugCache.Remove(cacheKey);
                _slugCacheTimestamps.Remove(cacheKey);
                return false;
            }

            slug = cachedSlug;
            return !string.IsNullOrWhiteSpace(slug);
        }

        private void CacheSlug(string cacheKey, string slug)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) || string.IsNullOrWhiteSpace(slug))
            {
                return;
            }

            _slugCache[cacheKey] = slug;
            _slugCacheTimestamps[cacheKey] = DateTime.UtcNow;
        }

        private static ExophaseGame FindBestSearchMatch(string gameName, IList<ExophaseGame> games, string platformSlug)
        {
            if (games == null || games.Count == 0 || string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            var normalizedSearch = NormalizeGameName(gameName);
            var scored = games
                .Where(game => game != null && !string.IsNullOrWhiteSpace(game.EndpointAwards))
                .Select(game =>
                {
                    var score = ScoreSearchMatch(normalizedSearch, game, platformSlug);
                    return new { Game = game, Score = score };
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToList();

            if (scored.Count == 0)
            {
                return null;
            }

            if (scored.Count > 1 && scored[0].Score == scored[1].Score)
            {
                return null;
            }

            return scored[0].Score >= 60 ? scored[0].Game : null;
        }

        private static int ScoreSearchMatch(string normalizedSearch, ExophaseGame game, string platformSlug)
        {
            var title = NormalizeGameName(game?.Title);
            if (string.IsNullOrWhiteSpace(title))
            {
                return 0;
            }

            var score = 0;
            if (string.Equals(title, normalizedSearch, StringComparison.OrdinalIgnoreCase))
            {
                score = 100;
            }
            else if (title.StartsWith(normalizedSearch, StringComparison.OrdinalIgnoreCase))
            {
                score = 80;
            }
            else if (title.IndexOf(normalizedSearch, StringComparison.OrdinalIgnoreCase) >= 0 ||
                     normalizedSearch.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score = 60;
            }
            else
            {
                var similarity = StringSimilarity.JaroWinklerSimilarityIgnoreCase(normalizedSearch, title);
                if (similarity >= 0.94)
                {
                    score = 70;
                }
                else if (similarity >= 0.88)
                {
                    score = 40;
                }
            }

            if (score > 0 &&
                !string.IsNullOrWhiteSpace(platformSlug) &&
                (game.EndpointAwards ?? string.Empty).IndexOf($"-{platformSlug}", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                score += 20;
            }

            return score;
        }

        private static int ApplyRarity(IList<AchievementDetail> nativeAchievements, IList<AchievementDetail> exophaseAchievements)
        {
            var exophaseByTitle = exophaseAchievements
                .Where(achievement => achievement != null && achievement.GlobalPercentUnlocked.HasValue)
                .GroupBy(achievement => NormalizeAchievementTitle(achievement.DisplayName), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            if (exophaseByTitle.Count == 0)
            {
                return 0;
            }

            var nativeTitleCounts = nativeAchievements
                .Where(achievement => achievement != null)
                .GroupBy(achievement => NormalizeAchievementTitle(achievement.DisplayName), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            var usedExophase = new HashSet<AchievementDetail>();
            var updated = 0;

            foreach (var nativeAchievement in nativeAchievements)
            {
                if (nativeAchievement == null)
                {
                    continue;
                }

                var title = NormalizeAchievementTitle(nativeAchievement.DisplayName);
                if (string.IsNullOrWhiteSpace(title) || !exophaseByTitle.TryGetValue(title, out var candidates))
                {
                    continue;
                }

                var match = ResolveAchievementMatch(nativeAchievement, title, nativeTitleCounts, candidates, usedExophase);
                if (match?.GlobalPercentUnlocked == null)
                {
                    continue;
                }

                nativeAchievement.GlobalPercentUnlocked = match.GlobalPercentUnlocked;
                nativeAchievement.Rarity = match.Rarity;
                usedExophase.Add(match);
                updated++;
            }

            return updated;
        }

        private static AchievementDetail ResolveAchievementMatch(
            AchievementDetail nativeAchievement,
            string title,
            IDictionary<string, int> nativeTitleCounts,
            IList<AchievementDetail> candidates,
            ISet<AchievementDetail> usedExophase)
        {
            var available = candidates
                .Where(candidate => candidate != null && !usedExophase.Contains(candidate))
                .ToList();

            if (available.Count == 0)
            {
                return null;
            }

            var nativeTitleCount = nativeTitleCounts.TryGetValue(title, out var count) ? count : 0;
            if (nativeTitleCount == 1 && available.Count == 1)
            {
                return available[0];
            }

            var scored = available
                .Select(candidate => new
                {
                    Candidate = candidate,
                    Score = ScoreAchievementMatch(nativeAchievement, candidate)
                })
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ToList();

            if (scored.Count == 0)
            {
                return null;
            }

            if (scored.Count > 1 && scored[0].Score == scored[1].Score)
            {
                return null;
            }

            return scored[0].Candidate;
        }

        private static int ScoreAchievementMatch(AchievementDetail nativeAchievement, AchievementDetail exophaseAchievement)
        {
            var score = 0;

            var nativeDescription = NormalizeAchievementText(nativeAchievement?.Description);
            var exophaseDescription = NormalizeAchievementText(exophaseAchievement?.Description);
            if (!string.IsNullOrWhiteSpace(nativeDescription) &&
                string.Equals(nativeDescription, exophaseDescription, StringComparison.OrdinalIgnoreCase))
            {
                score += 4;
            }

            if (!string.IsNullOrWhiteSpace(nativeAchievement?.TrophyType) &&
                string.Equals(nativeAchievement.TrophyType, exophaseAchievement?.TrophyType, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (nativeAchievement?.Points != null &&
                exophaseAchievement?.Points != null &&
                nativeAchievement.Points == exophaseAchievement.Points)
            {
                score += 2;
            }

            return score;
        }

        private static string ResolvePlatformSlug(Game game, string platformSlugHint)
        {
            return NormalizePlatformSlug(platformSlugHint) ??
                   NormalizePlatformSlug(ExophaseDataProvider.GetExophasePlatformSlug(game));
        }

        private static List<string> GetPlatformSlugCandidates(string platformSlug)
        {
            var normalized = NormalizePlatformSlug(platformSlug);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new List<string>();
            }

            switch (normalized)
            {
                case "xbox":
                    // Exophase uses both -xbox and -xbox-one for modern Xbox achievement pages.
                    return new List<string> { "xbox", "xbox-one" };
                case "xbox-one":
                    return new List<string> { "xbox-one", "xbox" };
                default:
                    return new List<string> { normalized };
            }
        }

        private static string NormalizePlatformSlug(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var normalized = value.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "ubisoft":
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
                    return normalized;
            }
        }

        private static string GenerateDefaultSlug(Game game, string platformSlug)
        {
            if (game == null || string.IsNullOrWhiteSpace(game.Name) || string.IsNullOrWhiteSpace(platformSlug))
            {
                return null;
            }

            var normalizedName = NormalizeGameNameForSlug(game.Name);
            return string.IsNullOrWhiteSpace(normalizedName)
                ? null
                : $"{normalizedName}-{platformSlug}";
        }

        private static string NormalizeGameNameForSlug(string name)
        {
            var normalized = NormalizeGameName(name);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            normalized = normalized.ToLowerInvariant();
            var chars = new char[normalized.Length];
            var index = 0;
            var lastWasHyphen = false;

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                {
                    chars[index++] = c;
                    lastWasHyphen = false;
                }
                else if (!lastWasHyphen)
                {
                    chars[index++] = '-';
                    lastWasHyphen = true;
                }
            }

            if (index > 0 && chars[index - 1] == '-')
            {
                index--;
            }

            return new string(chars, 0, index);
        }

        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
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

        private static string NormalizeAchievementTitle(string value)
        {
            return NormalizeAchievementText(value);
        }

        private static string NormalizeAchievementText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var chars = new char[value.Length];
            var index = 0;
            var lastWasSpace = false;

            foreach (var c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    chars[index++] = c;
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    chars[index++] = ' ';
                    lastWasSpace = true;
                }
            }

            if (index > 0 && chars[index - 1] == ' ')
            {
                index--;
            }

            return new string(chars, 0, index);
        }
    }
}
