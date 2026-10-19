using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Exophase
{
    [Flags]
    internal enum ExophaseMetadataFields
    {
        None = 0,
        Rarity = 1,
        IconPaths = 2
    }

    internal sealed class ExophaseMetadataEnricher : IDisposable
    {
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ExophaseSessionManager _sessionManager;
        private readonly ExophaseApiClient _apiClient;
        // Slug mappings are permanently stable; memoized for the enricher's lifetime.
        private readonly Dictionary<string, string> _slugMemo = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private bool _authChecked;
        private bool _isReady;

        public ExophaseMetadataEnricher(
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
                _logger?.Warn("[ExophaseMetadata] Missing Playnite API or plugin data path; metadata enrichment disabled for this scan.");
                return;
            }

            try
            {
                var result = await _sessionManager.ProbeAuthStateAsync(ct).ConfigureAwait(false);
                _isReady = result?.IsSuccess == true;

                if (_isReady)
                {
                    // One cookie session spans all EnrichAsync calls of the owning scan: fetches
                    // reuse the cached cookie snapshot and one shared offscreen view instead of
                    // decrypting the snapshot and creating a view per game. Closed in Dispose.
                    _apiClient.BeginCookieSession();
                }
                else
                {
                    _logger?.Warn("[ExophaseMetadata] Exophase authentication is required; native metadata will be kept.");
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "[ExophaseMetadata] Exophase auth probe failed; native metadata will be kept.");
            }
        }

        /// <summary>
        /// Closes the cookie session opened by InitializeAsync, releasing the shared
        /// offscreen view. Owning scanners call this when their scan completes.
        /// </summary>
        public void Dispose()
        {
            _apiClient?.EndCookieSession();
        }

        public async Task EnrichAsync(
            Game game,
            IList<AchievementDetail> achievements,
            string platformSlugHint,
            string providerPlatformKey,
            CancellationToken ct,
            ExophaseMetadataFields fields = ExophaseMetadataFields.Rarity,
            string regionHint = null,
            string searchName = null,
            bool fallBackToGameName = true)
        {
            if (!_isReady ||
                fields == ExophaseMetadataFields.None ||
                game == null ||
                achievements == null ||
                achievements.Count == 0)
            {
                return;
            }

            // searchName is the platform's own title for the game (or, for a multipack
            // collection, one trophy set's title), which Exophase lists under that name where
            // the Playnite name may differ. The Playnite name is searched next unless the caller
            // opts out, as a collection does: its name belongs to no single set.
            var searchNames = BuildSearchNames(searchName, game.Name, fallBackToGameName);
            if (searchNames.Count == 0)
            {
                return;
            }

            var effectiveName = searchNames[0];

            try
            {
                var platformSlug = ResolvePlatformSlug(game, platformSlugHint);
                var slugs = await ResolveSlugsAsync(game, searchNames, platformSlug, regionHint, ct).ConfigureAwait(false);
                if (slugs.Count == 0)
                {
                    _logger?.Debug($"[ExophaseMetadata] No Exophase slug resolved for '{string.Join("' / '", searchNames)}'.");
                    return;
                }

                var acceptLanguage = ExophaseApiClient.MapLanguageToAcceptLanguage(_settings?.Persisted?.GlobalLanguage);
                IList<AchievementDetail> exophaseAchievements = null;
                string resolvedSlug = null;

                foreach (var slug in slugs)
                {
                    var achievementUrl = ExophaseApiClient.BuildUrlFromSlug(slug);

                    // Warm the CDN for award thumbnails so the subsequent icon downloads hit 200 rather
                    // than the initial cold-CDN 404 (paid once per game via the stable icon cache).
                    var fetchedAchievements = await _apiClient
                        .FetchAchievementsAsync(achievementUrl, acceptLanguage, ct, waitForImages: true)
                        .ConfigureAwait(false);

                    if (fetchedAchievements == null || fetchedAchievements.Count == 0)
                    {
                        _logger?.Debug($"[ExophaseMetadata] No Exophase achievements found for '{effectiveName}' ({slug}).");
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

                var updated = ApplyMetadata(achievements, exophaseAchievements, fields);
                _logger?.Info($"[ExophaseMetadata] Applied {DescribeFields(fields)} to {updated}/{achievements.Count} achievements for '{effectiveName}' using slug '{resolvedSlug}'.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[ExophaseMetadata] Failed to enrich metadata for '{game?.Name}'. Native metadata will be kept.");
            }
        }

        /// <summary>
        /// The names to search, in order: the caller's name, then the Playnite name when allowed and
        /// different once normalized. Blank names are skipped.
        /// </summary>
        internal static List<string> BuildSearchNames(string searchName, string gameName, bool fallBackToGameName)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string name)
            {
                var trimmed = name?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    return;
                }

                var key = ExophaseGameNameMatcher.NormalizeGameName(trimmed) ?? trimmed;
                if (seen.Add(key))
                {
                    names.Add(trimmed);
                }
            }

            Add(searchName);
            if (fallBackToGameName || string.IsNullOrWhiteSpace(searchName))
            {
                Add(gameName);
            }

            return names;
        }

        /// <summary>
        /// Overrides first; then each search name in turn, returning the first name's match; and
        /// only when every search misses, slugs generated from the first name.
        /// </summary>
        private async Task<List<string>> ResolveSlugsAsync(
            Game game,
            IReadOnlyList<string> searchNames,
            string platformSlug,
            string regionHint,
            CancellationToken ct)
        {
            if (GameCustomDataLookup.TryGetExophaseEnrichmentSlugOverride(game.Id, out var enrichmentSlug) &&
                !string.IsNullOrWhiteSpace(enrichmentSlug))
            {
                return new List<string> { enrichmentSlug.Trim() };
            }

            if (GameCustomDataLookup.TryGetExophaseSlugOverride(
                    game.Id,
                    out var overrideSlug,
                    ProviderRegistry.Settings<ExophaseSettings>()) &&
                !string.IsNullOrWhiteSpace(overrideSlug))
            {
                return new List<string> { overrideSlug.Trim() };
            }

            var platformSlugs = GetPlatformSlugCandidates(platformSlug);
            foreach (var searchName in searchNames)
            {
                var searched = await SearchSlugAsync(game, searchName, platformSlugs, regionHint, ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(searched))
                {
                    return new List<string> { searched };
                }
            }

            // PSN slugs on Exophase carry no platform suffix and regional variants get
            // opaque dedup suffixes, so a generated slug can never be right there; skip
            // the guess instead of fetching a guaranteed 404 every refresh.
            return platformSlugs
                .Where(candidatePlatformSlug => !PsnPlatformSlugs.Contains(candidatePlatformSlug))
                .Select(candidatePlatformSlug => GenerateDefaultSlug(searchNames[0], candidatePlatformSlug))
                .Where(defaultSlug => !string.IsNullOrWhiteSpace(defaultSlug))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>The slug of the best search match for one name, or null.</summary>
        private async Task<string> SearchSlugAsync(
            Game game,
            string searchName,
            IReadOnlyList<string> platformSlugs,
            string regionHint,
            CancellationToken ct)
        {
            // The name is part of the key because collections resolve one slug per trophy set
            // under the same game id.
            var cacheKey = $"{game.Id:N}:{searchName}:{string.Join("|", platformSlugs)}";
            if (TryGetCachedSlug(cacheKey, out var cachedSlug))
            {
                return cachedSlug;
            }

            var normalizedName = ExophaseGameNameMatcher.NormalizeGameName(searchName);
            if (!string.IsNullOrWhiteSpace(normalizedName))
            {
                foreach (var candidatePlatformSlug in platformSlugs)
                {
                    var games = await _apiClient.SearchGamesAsync(normalizedName, candidatePlatformSlug, ct).ConfigureAwait(false);
                    if (games.Count == 0 && !string.IsNullOrWhiteSpace(candidatePlatformSlug))
                    {
                        // The archive endpoint does not validate its platform parameter;
                        // an unrecognized value yields zero rows. The platform is enforced
                        // client-side from each row's platform slugs, so an unfiltered
                        // retry stays safe.
                        games = await _apiClient.SearchGamesAsync(normalizedName, null, ct).ConfigureAwait(false);
                    }

                    var match = ExophaseGameNameMatcher.SelectBestSearchMatch(normalizedName, games, candidatePlatformSlug, regionHint);
                    if (match == null && games.Count > 0)
                    {
                        var observedPlatforms = games
                            .SelectMany(row => row?.Platforms ?? Enumerable.Empty<ExophasePlatform>())
                            .Select(platform => platform?.Slug)
                            .Where(slug => !string.IsNullOrWhiteSpace(slug))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        _logger?.Debug(
                            $"[ExophaseMetadata] Search for '{normalizedName}' ({candidatePlatformSlug}) matched none of " +
                            $"{games.Count} rows; platforms seen: [{string.Join(", ", observedPlatforms)}].");
                    }

                    var resolvedSlug = ExophaseApiClient.ExtractSlugFromUrl(match?.EndpointAwards);
                    if (!string.IsNullOrWhiteSpace(resolvedSlug))
                    {
                        CacheSlug(cacheKey, resolvedSlug);
                        return resolvedSlug;
                    }
                }
            }

            return null;
        }

        private static readonly HashSet<string> PsnPlatformSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "psn", "ps3", "ps4", "ps5", "psvita", "vita"
        };

        private bool TryGetCachedSlug(string cacheKey, out string slug)
        {
            slug = null;
            if (string.IsNullOrWhiteSpace(cacheKey))
            {
                return false;
            }

            if (!_slugMemo.TryGetValue(cacheKey, out var cachedSlug))
            {
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

            _slugMemo[cacheKey] = slug;
        }

        private static int ApplyMetadata(
            IList<AchievementDetail> nativeAchievements,
            IList<AchievementDetail> exophaseAchievements,
            ExophaseMetadataFields fields)
        {
            var matches = CrossSourceAchievementMatcher.Match(
                nativeAchievements,
                exophaseAchievements,
                achievement => ShouldConsiderExophaseAchievement(achievement, fields));

            var updated = 0;
            foreach (var pair in matches)
            {
                var nativeAchievement = pair.Target;
                var match = pair.Source;
                var changed = false;

                if (fields.HasFlag(ExophaseMetadataFields.Rarity) &&
                    match.GlobalPercentUnlocked.HasValue)
                {
                    if (nativeAchievement.GlobalPercentUnlocked != match.GlobalPercentUnlocked ||
                        nativeAchievement.Rarity != match.Rarity)
                    {
                        nativeAchievement.GlobalPercentUnlocked = match.GlobalPercentUnlocked;
                        nativeAchievement.Rarity = match.Rarity;
                        changed = true;
                    }
                }

                if (fields.HasFlag(ExophaseMetadataFields.IconPaths))
                {
                    changed |= ApplyIconPaths(nativeAchievement, match);
                }

                if (changed)
                {
                    updated++;
                }
            }

            return updated;
        }

        private static bool ShouldConsiderExophaseAchievement(
            AchievementDetail achievement,
            ExophaseMetadataFields fields)
        {
            return
                (fields.HasFlag(ExophaseMetadataFields.Rarity) &&
                 achievement.GlobalPercentUnlocked.HasValue) ||
                (fields.HasFlag(ExophaseMetadataFields.IconPaths) &&
                 HasIconPath(achievement));
        }

        private static bool ApplyIconPaths(AchievementDetail nativeAchievement, AchievementDetail exophaseAchievement)
        {
            var updated = false;
            var unlockedIconPath = FirstNonBlank(exophaseAchievement?.UnlockedIconPath, exophaseAchievement?.LockedIconPath);
            var lockedIconPath = FirstNonBlank(exophaseAchievement?.LockedIconPath, exophaseAchievement?.UnlockedIconPath);

            if (string.IsNullOrWhiteSpace(nativeAchievement.UnlockedIconPath) &&
                !string.IsNullOrWhiteSpace(unlockedIconPath))
            {
                nativeAchievement.UnlockedIconPath = unlockedIconPath;
                updated = true;
            }

            if (string.IsNullOrWhiteSpace(nativeAchievement.LockedIconPath) &&
                !string.IsNullOrWhiteSpace(lockedIconPath))
            {
                nativeAchievement.LockedIconPath = lockedIconPath;
                updated = true;
            }

            return updated;
        }

        private static bool HasIconPath(AchievementDetail achievement)
        {
            return !string.IsNullOrWhiteSpace(achievement?.UnlockedIconPath) ||
                !string.IsNullOrWhiteSpace(achievement?.LockedIconPath);
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static string DescribeFields(ExophaseMetadataFields fields)
        {
            if (fields == ExophaseMetadataFields.Rarity)
            {
                return "rarity";
            }

            if (fields == ExophaseMetadataFields.IconPaths)
            {
                return "icons";
            }

            if ((fields & (ExophaseMetadataFields.Rarity | ExophaseMetadataFields.IconPaths)) ==
                (ExophaseMetadataFields.Rarity | ExophaseMetadataFields.IconPaths))
            {
                return "rarity/icons";
            }

            return "metadata";
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
                case "playstation vita":
                case "playstationvita":
                    return "psvita";
                default:
                    return normalized;
            }
        }

        private static string GenerateDefaultSlug(string gameName, string platformSlug)
        {
            if (string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(platformSlug))
            {
                return null;
            }

            var normalizedName = ExophaseGameNameMatcher.NormalizeGameNameForSlug(gameName);
            return string.IsNullOrWhiteSpace(normalizedName)
                ? null
                : $"{normalizedName}-{platformSlug}";
        }
    }
}
