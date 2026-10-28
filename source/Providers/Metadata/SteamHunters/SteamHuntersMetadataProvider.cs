using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.GameSearch;
using PlayniteAchievements.Services.Metadata;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Metadata
{
    /// <summary>
    /// SteamHunters categories, types and category art. A game another provider services is
    /// linked to a Steam appId, the Steam schema supplies the titles the shared matcher pairs with
    /// the provider's achievements, and SteamHunters groups those schema rows. A Steam game is
    /// linked by its own appId without storing the link, and SteamHunters groups its own
    /// achievements, so they join by ApiName.
    /// </summary>
    internal sealed class SteamHuntersMetadataProvider : IMetadataProvider, IMetadataPageLinks, IMetadataImplicitLink, IMetadataLinkSearch
    {
        public const string MetadataKey = "SteamHunters";

        private readonly ManualSourceRegistry _manualSources;
        private readonly SteamDataProvider _steam;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public SteamHuntersMetadataProvider(
            ManualSourceRegistry manualSources,
            SteamDataProvider steam,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            _manualSources = manualSources;
            _steam = steam;
            _settings = settings;
            _logger = logger;
        }

        public string Key => MetadataKey;

        public string DisplayNameKey => "LOCPlayAch_Metadata_Source_SteamHunters";

        /// <summary>The Steam store's search; a link names one app per game, several for a collection.</summary>
        public IGameSearchSource CreateLinkSearch(Guid gameId, string servicingProviderKey) =>
            SteamSchema == null
                ? null
                : new ManualSourceGameSearch(SteamSchema, Language, allowsMultiple: true, displayName: ResourceProvider.GetString(DisplayNameKey));

        /// <summary>The app's achievement page; the link is the Steam app id.</summary>
        public IReadOnlyList<MetadataPageLink> PageLinks(Guid gameId, string linkId, string servicingProviderKey) =>
            AppId(linkId) is int appId
                ? new[] { new MetadataPageLink("SteamHunters", $"https://steamhunters.com/apps/{appId}/achievements") }
                : Array.Empty<MetadataPageLink>();

        /// <summary>The Steam app id one list of a link names: the id, or a store or community app address.</summary>
        internal static int? AppId(string linkId) => SteamAppIds.TryParse(linkId);

        public MetadataField SupportedFields => MetadataField.Categories | MetadataField.CategoryArt | MetadataField.Tags | MetadataField.Text;

        private IManualSource SteamSchema => _manualSources?.GetSourceByKey("Steam");

        private string Language => _settings?.Persisted?.GlobalLanguage;

        public bool IsNativeFor(string servicingProviderKey) => false;

        public Task<bool> BeginRunAsync(CancellationToken ct)
        {
            var enricher = _steam?.SteamHuntersEnricher;
            // Each run starts with an empty per-app cache and a fresh failure backoff.
            enricher?.ClearCache();
            return Task.FromResult(enricher != null);
        }

        private static bool IsSteamServiced(MetadataLinkRequest request) =>
            string.Equals(request?.ServicingProviderKey, "Steam", StringComparison.OrdinalIgnoreCase) &&
            request.Data?.AppId > 0;

        /// <summary>A Steam game's own appId.</summary>
        public string ImplicitLinkId(MetadataLinkRequest request) =>
            IsSteamServiced(request) ? request.Data.AppId.ToString(CultureInfo.InvariantCulture) : null;

        /// <summary>A Steam game's own appId, from its cached data.</summary>
        public string ImplicitLinkId(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return null;
            }

            var cached = PlayniteAchievementsPlugin.Instance?.CacheManager?.LoadGameData(gameId.ToString());
            return string.Equals(cached?.ProviderKey, "Steam", StringComparison.OrdinalIgnoreCase) && cached.AppId > 0
                ? cached.AppId.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        public void EndRun()
        {
            lock (_schemaRows)
            {
                _schemaRows.Clear();
            }

            // The enricher's per-app responses are not needed between runs.
            _steam?.SteamHuntersEnricher?.ClearCache();
        }

        public async Task<MetadataSourceLink> ResolveLinkAsync(MetadataLinkRequest request, CancellationToken ct)
        {
            var game = request?.Game;
            if (game == null || SteamSchema == null)
            {
                return null;
            }

            if (TryGetManualSteamAppId(game.Id, out var manualAppId))
            {
                return new MetadataSourceLink(manualAppId);
            }

            if (GameCustomDataLookup.TryGetSteamAppIdOverride(game.Id, out var overrideAppId) && overrideAppId > 0)
            {
                return new MetadataSourceLink(overrideAppId.ToString(CultureInfo.InvariantCulture));
            }

            var linked = game.Links?
                .Select(link => SteamAppIds.TryParse(link?.Url))
                .FirstOrDefault(appId => appId != null);
            if (linked != null)
            {
                return new MetadataSourceLink(linked.Value.ToString(CultureInfo.InvariantCulture));
            }

            if (string.IsNullOrWhiteSpace(game.Name))
            {
                return null;
            }

            // With the game's achievements at hand, each app the store search offers is judged by
            // how many of them it matches, and a collection is also looked for game by game.
            var targets = request.Data?.Achievements?.Where(row => row != null).ToList();
            if (targets?.Count > 0)
            {
                var search = new MetadataPageSearch(StoreCandidatesAsync, SchemaRowsAsync, englishRows: EnglishRowsFor);
                var names = request.SearchNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var choice = await search.ChooseAsync(names, targets, MetadataSubGame.From(targets), ct, request.CategoryNames, sharedNames: request.AlternateNames, englishTitles: request.EnglishTitles).ConfigureAwait(false);
                if (choice == null)
                {
                    _logger?.Debug($"[SteamHuntersMetadata] '{game.Name}': no Steam app matches its achievements.");
                    return null;
                }

                _logger?.Debug($"[SteamHuntersMetadata] '{game.Name}' -> {MetadataLinkIds.Join(choice.Ids)} ({choice.Matched}/{targets.Count} matched; {choice.Trace}).");
                return new MetadataSourceLink(MetadataLinkIds.Join(choice.Ids), choice.Label ?? game.Name, foundByName: true) { Trace = choice.Trace, Matched = choice.Matched };
            }

            return await SearchByNameAsync(game.Name, ct).ConfigureAwait(false);
        }

        // Store search results judged per name; each costs a schema request the first time in a run.
        private const int StoreCandidatesJudged = 6;

        // Schemas are shared while a game is looked up and fetched, so only the recent ones are kept.
        private readonly LruCache<string, List<AchievementDetail>> _schemaRows =
            new LruCache<string, List<AchievementDetail>>(64, StringComparer.OrdinalIgnoreCase);

        private async Task<IReadOnlyList<MetadataPageCandidate>> StoreCandidatesAsync(string name, CancellationToken ct)
        {
            var results = await SteamSchema.SearchGamesAsync(name, Language, ct).ConfigureAwait(false) ?? new List<ManualGameSearchResult>();
            return results
                .Where(result => !string.IsNullOrWhiteSpace(result?.SourceGameId))
                .GroupBy(result => result.SourceGameId)
                .Take(StoreCandidatesJudged)
                .Select((group, index) => new MetadataPageCandidate(group.Key, group.First().Name, $"store #{index + 1}"))
                .ToList();
        }

        /// <summary>An app's schema rows, loaded once per run.</summary>
        private Task<IList<AchievementDetail>> SchemaRowsAsync(string appId, CancellationToken ct) =>
            SchemaRowsAsync(appId, Language, ct);

        /// <summary>An app's schema rows in <paramref name="language"/>, loaded once per run.</summary>
        private async Task<IList<AchievementDetail>> SchemaRowsAsync(string appId, string language, CancellationToken ct)
        {
            var cacheKey = appId + "|" + language;
            lock (_schemaRows)
            {
                if (_schemaRows.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var rows = await SteamSchema.GetAchievementsAsync(appId, language, ct).ConfigureAwait(false) ?? new List<AchievementDetail>();
            lock (_schemaRows)
            {
                _schemaRows.Set(cacheKey, rows);
            }

            return rows;
        }

        /// <summary>An app's English schema rows, null when the plugin's language already is English.</summary>
        private Task<IList<AchievementDetail>> EnglishRowsFor(string appId, CancellationToken ct) =>
            EnglishTitlePairing.IsEnglish(Language)
                ? Task.FromResult<IList<AchievementDetail>>(null)
                : SchemaRowsAsync(appId, "english", ct);

        public async Task<AchievementSourceSet> FetchAsync(MetadataLinkRequest request, string linkId, CancellationToken ct)
        {
            if (!(AppId(linkId) is int appId))
            {
                return null;
            }

            if (IsSteamServiced(request) && appId == request.Data.AppId)
            {
                return await FetchOwnAsync(request, appId, ct).ConfigureAwait(false);
            }

            if (SteamSchema == null)
            {
                return null;
            }

            var appIdText = appId.ToString(CultureInfo.InvariantCulture);
            var rows = await SteamSchema.GetAchievementsAsync(appIdText, Language, ct).ConfigureAwait(false);
            if (rows == null || rows.Count == 0)
            {
                return null;
            }

            var gameId = request?.Game?.Id;
            var applied = await _steam.SteamHuntersEnricher
                .EnrichAsync(appId, request?.Game?.Name, rows, gameId, ct)
                .ConfigureAwait(false);
            if (!applied)
            {
                return null;
            }

            return new AchievementSourceSet(Key, linkId, rows)
            {
                GameArtUrl = SteamImageUrls.Header(appId),
                ApiNamesMayBeShared = true,
                // Steam's schema is read in the plugin's language.
                TextInPluginLanguage = true,
                EnglishRows =EnglishTitlePairing.IsEnglish(Language) ? null : (Func<CancellationToken, Task<IList<AchievementDetail>>>)(token => EnglishRowsFor(appIdText, token)),
                SharesApiNames = gameId.HasValue && TryGetManualSteamAppId(gameId.Value, out var manualAppId) &&
                                 string.Equals(manualAppId, appIdText, StringComparison.Ordinal),
                ExtendedLinkId = string.Equals(appIdText, linkId?.Trim(), StringComparison.Ordinal) ? null : appIdText
            };
        }

        /// <summary>
        /// SteamHunters' grouping of a Steam game's own achievements. The rows are copies of the
        /// refreshed payload, so no schema is fetched (and no Steam sign-in is needed), the titles
        /// are already in the plugin's language, and every row joins its achievement by ApiName.
        /// </summary>
        private async Task<AchievementSourceSet> FetchOwnAsync(MetadataLinkRequest request, int appId, CancellationToken ct)
        {
            var rows = SteamHuntersCategoryEnricher.OwnRowsFor(request.Data.Achievements);
            if (rows.Count == 0)
            {
                return null;
            }

            // The enricher saves category art for the game it is given, so it gets the game only
            // when SteamHunters is the category source.
            var wantsArt = request.WantedFields == MetadataField.None ||
                           (request.WantedFields & MetadataField.CategoryArt) != 0;
            var applied = await _steam.SteamHuntersEnricher
                .EnrichAsync(appId, request.Data.GameName ?? request.Game?.Name, rows, wantsArt ? request.Game?.Id : null, ct)
                .ConfigureAwait(false);
            if (!applied)
            {
                return null;
            }

            return new AchievementSourceSet(Key, appId.ToString(CultureInfo.InvariantCulture), rows)
            {
                SharesApiNames = true,
                KeepsNoMatchList = true
            };
        }

        private static bool TryGetManualSteamAppId(Guid gameId, out string appId)
        {
            appId = null;
            if (GameCustomDataLookup.TryGetManualLink(gameId, out var link) &&
                string.Equals(link?.SourceKey, "Steam", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(link.SourceGameId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                appId = parsed.ToString(CultureInfo.InvariantCulture);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Accepts a store search only when exactly one result has the game's name. When none does,
        /// searches again with the loose name ("Deus Ex - Mankind Divided" as "deus ex mankind
        /// divided"), since the store finds nothing for some punctuated titles and names editions
        /// differently. Either way the link is marked found by name, so the overlay confirms it
        /// against the achievements.
        /// </summary>
        private async Task<MetadataSourceLink> SearchByNameAsync(string name, CancellationToken ct)
        {
            var normalized = GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(name));
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            var exact = await SearchAsync(
                name,
                result => string.Equals(
                    GameNameNormalizer.StripDiacritics(ExophaseGameNameMatcher.NormalizeGameName(result)),
                    normalized,
                    StringComparison.OrdinalIgnoreCase),
                ct).ConfigureAwait(false);
            if (exact.Count == 1)
            {
                return new MetadataSourceLink(exact[0].SourceGameId, exact[0].Name, foundByName: true);
            }

            var loose = GameNameNormalizer.ToLooseKey(name);
            if (exact.Count > 1 || loose.Length == 0)
            {
                _logger?.Debug($"[SteamHuntersMetadata] Name search for '{name}' found {exact.Count} exact matches; no link.");
                return null;
            }

            var close = await SearchAsync(
                loose,
                result => string.Equals(GameNameNormalizer.ToLooseKey(result), loose, StringComparison.Ordinal),
                ct).ConfigureAwait(false);
            if (close.Count != 1)
            {
                _logger?.Debug($"[SteamHuntersMetadata] Name search for '{name}' found {close.Count} close matches; no link.");
                return null;
            }

            return new MetadataSourceLink(close[0].SourceGameId, close[0].Name, foundByName: true);
        }

        private async Task<List<ManualGameSearchResult>> SearchAsync(string query, Func<string, bool> accepts, CancellationToken ct)
        {
            var results = await SteamSchema.SearchGamesAsync(query, Language, ct).ConfigureAwait(false);
            return (results ?? new List<ManualGameSearchResult>())
                .Where(result => result != null &&
                                 !string.IsNullOrWhiteSpace(result.SourceGameId) &&
                                 accepts(result.Name))
                .GroupBy(result => result.SourceGameId)
                .Select(group => group.First())
                .ToList();
        }
    }
}
