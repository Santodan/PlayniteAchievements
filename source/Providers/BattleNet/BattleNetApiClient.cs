using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.BattleNet.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class BattleNetTransientException : Exception
    {
        public BattleNetTransientException(string message) : base(message) { }
    }

    public sealed class BattleNetApiClient : IDisposable
    {
        private bool _disposed;
        private const string Sc2ProfileUrl = "https://{0}.api.blizzard.com/sc2/profile/{1}/{2}/{3}?locale={4}";
        private const string Sc2AchievementsUrl = "https://{0}.api.blizzard.com/sc2/static/profile/{1}?locale={2}";
        private const string Sc2PlayerUrl = "https://{0}.api.blizzard.com/sc2/player/{1}";
        private const string TokenUrl = "https://{0}.battle.net/oauth/token";
        private const string AuthorizeUrl = "https://{0}.battle.net/oauth/authorize";
        private const string UserInfoUrl = "https://{0}.battle.net/oauth/userinfo";
        private const string WowBaseAchievementUrl = "https://worldofwarcraft.blizzard.com/{0}/character/{1}/{2}/{3}/achievements/{4}";
        private const string WowOfficialAchievementIndexUrl = "https://{0}.api.blizzard.com/data/wow/achievement/index?namespace=static-{2}{0}&locale={1}";
        private const string WowOfficialAchievementUrl = "https://{0}.api.blizzard.com/data/wow/achievement/{1}?namespace=static-{3}{0}&locale={2}";
        private const string WowAchievementCategoryIndexUrl = "https://{0}.api.blizzard.com/data/wow/achievement-category/index?namespace=static-{2}{0}&locale={1}";
        private const string WowAchievementCategoryUrl = "https://{0}.api.blizzard.com/data/wow/achievement-category/{1}?namespace=static-{3}{0}&locale={2}";
        private const string WowOfficialCharacterAchievementsUrl = "https://{0}.api.blizzard.com/profile/wow/character/{1}/{2}/achievements?namespace=profile-{4}{0}&locale={3}";
        private const string WowRealmIndexUrl = "https://{0}.api.blizzard.com/data/wow/realm/index?namespace=dynamic-{2}{0}&locale={1}";
        private const string WowOfficialAccountProfileUrl = "https://{0}.api.blizzard.com/profile/user/wow?namespace=profile-{2}{0}&locale={1}";
        private const string WowGraphQlUrl = "https://worldofwarcraft.blizzard.com/graphql";
        private const string WowStatusUrl = "https://worldofwarcraft.blizzard.com/game/status";
        // One source of truth for the site lives on DataForAzerothClient; these aliases keep the
        // no-browser fallback path below readable.
        private const string DataForAzerothBaseUrl = DataForAzerothClient.BaseUrl;
        private const string DefaultApiLocale = "en_US";

        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly string[] WowCategories =
        {
            "characters/model.json",
            "player-vs-player/model.json",
            "quests/model.json",
            "exploration/model.json",
            "world-events/model.json",
            "dungeons-raids/model.json",
            "professions/model.json",
            "reputation/model.json",
            "pet-battles/model.json",
            "collections/model.json",
            "expansion-features/model.json",
            "delves/model.json",
            "housing/model.json",
            "feats-of-strength/model.json",
            "legacy/model.json"
        };

        private static readonly RateLimiter RateLimiter = new RateLimiter(1000, 3);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private string _wowSha256Hash;
        private string _cachedTokenClientId;
        private string _cachedTokenRegion;
        private string _cachedAccessToken;
        private DateTime _cachedAccessTokenExpiresUtc;
        private readonly ConcurrentDictionary<WowVersion, Dictionary<string, double>> _cachedDataForAzerothWowRarity =
            new ConcurrentDictionary<WowVersion, Dictionary<string, double>>();
        private Dictionary<string, HashSet<int>> _cachedWowGuildCategoryIds;
        private readonly DataForAzerothClient _dataForAzeroth;

        public BattleNetApiClient(ILogger logger)
            : this(logger, CreateDefaultHttpClient())
        {
        }

        /// <summary>
        /// Production constructor. The browser API is needed only for Data for Azeroth, whose bot
        /// check is cleared by the user in a browser window and replayed from the shared cookie store.
        /// </summary>
        public BattleNetApiClient(ILogger logger, IPlayniteAPI playniteApi)
            : this(logger, CreateDefaultHttpClient())
        {
            if (playniteApi != null)
            {
                _dataForAzeroth = new DataForAzerothClient(playniteApi, logger);
            }
        }

        /// <summary>The Data for Azeroth site session, or null when no browser is available.</summary>
        internal DataForAzerothClient DataForAzeroth => _dataForAzeroth;

        /// <summary>
        /// Drops the cached rarity map and any outstanding gate backoff, so a check the user just
        /// cleared takes effect on the next refresh.
        /// </summary>
        internal void InvalidateDataForAzerothRarityCache()
        {
            _cachedDataForAzerothWowRarity.Clear();
            _dataForAzeroth?.ResetGateState();
        }

        internal BattleNetApiClient(ILogger logger, HttpClient httpClient)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                _httpClient.DefaultRequestHeaders.Add("User-Agent", DefaultUserAgent);
            }
        }

        // --- SC2 ---

        public async Task<Sc2ProfileResponse> GetSc2ProfileAsync(
            int regionId,
            int realmId,
            int profileId,
            string clientId,
            string clientSecret,
            string locale,
            CancellationToken ct)
        {
            var apiRegion = MapSc2RegionIdToApiRegion(regionId);
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale;

            var token = await GetClientCredentialsTokenAsync(apiRegion, clientId, clientSecret, ct).ConfigureAwait(false);
            var url = BuildSc2ProfileUrl(apiRegion, regionId, realmId, profileId, effectiveLocale);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<Sc2ProfileResponse>(url, ct, token).ConfigureAwait(false),
                IsTransientError, ct);
        }

        public async Task<Sc2AchievementDefinitionsResponse> GetSc2AchievementDefinitionsAsync(
            int regionId,
            string clientId,
            string clientSecret,
            string locale,
            CancellationToken ct)
        {
            var apiRegion = MapSc2RegionIdToApiRegion(regionId);
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale;

            var token = await GetClientCredentialsTokenAsync(apiRegion, clientId, clientSecret, ct).ConfigureAwait(false);
            var url = BuildSc2AchievementsUrl(apiRegion, regionId, effectiveLocale);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<Sc2AchievementDefinitionsResponse>(url, ct, token).ConfigureAwait(false),
                IsTransientError, ct);
        }

        /// <summary>
        /// Lists the StarCraft II profiles bound to the authenticated Battle.net account. The account
        /// identifier is the OAuth <c>sub</c> claim; the call is account-bound and uses the user bearer
        /// token rather than client credentials.
        /// </summary>
        public async Task<List<Sc2PlayerProfile>> GetSc2PlayerProfilesAsync(
            string apiRegion,
            string accountId,
            string bearerToken,
            CancellationToken ct)
        {
            var normalizedRegion = NormalizeApiRegion(apiRegion);
            var url = string.Format(Sc2PlayerUrl, normalizedRegion, Uri.EscapeDataString(accountId ?? string.Empty));
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<List<Sc2PlayerProfile>>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct);
        }

        // --- WoW ---

        public async Task<List<WowAchievementsData>> GetWowAllAchievementsAsync(
            string region, string realmSlug, string character, string locale, CancellationToken ct)
        {
            var results = new List<WowAchievementsData>();
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-us" : locale;
            _logger?.Info($"[BattleNet/API] WoW public achievement categories requested. region={region ?? "<none>"}, realmSlug={realmSlug ?? "<none>"}, character={Presence(character)}, locale={effectiveLocale}");

            var baseUrl = string.Format(WowBaseAchievementUrl,
                effectiveLocale, region, realmSlug, Uri.EscapeDataString(character), "{0}");

            foreach (var category in WowCategories)
            {
                ct.ThrowIfCancellationRequested();
                var url = string.Format(baseUrl, category);
                try
                {
                    var data = await RateLimiter.ExecuteWithRetryAsync(
                        async () => await GetJsonAsync<WowAchievementsData>(url, ct).ConfigureAwait(false),
                        IsTransientError, ct);
                    if (data != null)
                    {
                        results.Add(data);
                    }
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"[BattleNet/API] WoW public achievement category fetch failed for {UrlHostAndPath(url)}.");
                }
            }

            _logger?.Info($"[BattleNet/API] Completed WoW public achievement category fetch. region={region ?? "<none>"}, realmSlug={realmSlug ?? "<none>"}, fetched={results.Count}/{WowCategories.Length}");
            return results;
        }

        public Task<string> GetClientCredentialsAccessTokenAsync(
            string apiRegion,
            string clientId,
            string clientSecret,
            CancellationToken ct)
        {
            return GetClientCredentialsTokenAsync(apiRegion, clientId, clientSecret, ct);
        }

        public async Task<BattleNetApiTokenResponse> ExchangeAuthorizationCodeAsync(
            string apiRegion,
            string clientId,
            string clientSecret,
            string authorizationCode,
            string redirectUri,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(authorizationCode))
            {
                throw new BattleNetTransientException("Battle.net authorization code is missing.");
            }

            var formBody = BuildFormData(new Dictionary<string, string>
            {
                { "grant_type", "authorization_code" },
                { "code", authorizationCode },
                { "redirect_uri", redirectUri ?? string.Empty }
            });

            return await PostTokenAsync(apiRegion, clientId, clientSecret, formBody, ct).ConfigureAwait(false);
        }

        public async Task<BattleNetUserInfoResponse> GetUserInfoAsync(
            string apiRegion,
            string bearerToken,
            CancellationToken ct)
        {
            var url = BuildUserInfoUrl(apiRegion);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<BattleNetUserInfoResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowAccountProfileResponse> GetWowAccountProfileAsync(
            string region,
            string bearerToken,
            string locale,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowOfficialAccountProfileUrl(region, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowAccountProfileResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowCharacterAchievementsResponse> GetWowOfficialCharacterAchievementsAsync(
            string region,
            string realmSlug,
            string character,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowOfficialCharacterAchievementsUrl(region, realmSlug, character, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowCharacterAchievementsResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Character achievements addressed through the profile link an account entry carries, for
        /// entries that do not spell out a realm slug and name.
        /// </summary>
        public async Task<WowCharacterAchievementsResponse> GetWowCharacterAchievementsByProfileHrefAsync(
            string profileHref,
            string locale,
            string bearerToken,
            CancellationToken ct)
        {
            var url = BuildCharacterAchievementsUrlFromProfileHref(profileHref, locale);
            if (url == null)
            {
                return null;
            }

            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowCharacterAchievementsResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowAchievementCategoryIndexResponse> GetWowAchievementCategoryIndexAsync(
            string region,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowAchievementCategoryIndexUrl(region, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowAchievementCategoryIndexResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowAchievementCategoryResource> GetWowAchievementCategoryAsync(
            string region,
            int categoryId,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowAchievementCategoryUrl(region, categoryId, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowAchievementCategoryResource>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<List<WowOfficialAchievementDefinition>> GetWowOfficialAchievementCatalogAsync(
            string region,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var index = await GetWowOfficialAchievementIndexAsync(region, locale, bearerToken, ct, namespacePrefix).ConfigureAwait(false);
            var definitions = new List<WowOfficialAchievementDefinition>();

            foreach (var reference in index?.Achievements ?? new List<WowOfficialAchievementReference>())
            {
                ct.ThrowIfCancellationRequested();

                var id = reference?.Id ?? 0;
                if (id <= 0)
                {
                    continue;
                }

                definitions.Add(new WowOfficialAchievementDefinition
                {
                    Id = id,
                    Name = reference?.Name,
                    Description = reference?.Description,
                    Points = reference?.Points ?? 0,
                    IsHidden = reference?.IsHidden == true,
                    IsObtainable = reference?.IsObtainable,
                    IsObtainableInGame = reference?.IsObtainableInGame,
                    Category = reference?.Category,
                    Media = reference?.Media
                });
            }

            _logger?.Info($"[BattleNet/WoW] Loaded official achievement index. count={definitions.Count}");
            return definitions;
        }

        public async Task<WowOfficialAchievementIndexResponse> GetWowOfficialAchievementIndexAsync(
            string region,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowOfficialAchievementIndexUrl(region, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowOfficialAchievementIndexResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowOfficialAchievementDefinition> GetWowOfficialAchievementDefinitionByIdAsync(
            string region,
            int achievementId,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            if (achievementId <= 0)
            {
                return null;
            }

            var url = BuildWowOfficialAchievementDefinitionUrl(region, achievementId, locale, namespacePrefix);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowOfficialAchievementDefinition>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<HashSet<int>> GetWowGuildCategoryIdsAsync(
            string region,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var apiRegion = NormalizeApiRegion(region);
            var cacheKey = (namespacePrefix ?? string.Empty) + apiRegion;
            if (_cachedWowGuildCategoryIds != null &&
                _cachedWowGuildCategoryIds.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var indexUrl = BuildWowAchievementCategoryIndexUrl(apiRegion, locale, namespacePrefix);
            var index = await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowAchievementCategoryIndexResponse>(indexUrl, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);

            var guildIds = new HashSet<int>();
            var pending = new Queue<WowAchievementCategoryReference>();
            foreach (var reference in index?.GuildCategories ?? new List<WowAchievementCategoryReference>())
            {
                if (reference != null && reference.Id > 0 && guildIds.Add(reference.Id))
                {
                    pending.Enqueue(reference);
                }
            }

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var reference = pending.Dequeue();
                var resourceUrl = !string.IsNullOrWhiteSpace(reference.Key?.Href)
                    ? AddLocaleToUrl(reference.Key.Href, locale)
                    : BuildWowAchievementCategoryUrl(apiRegion, reference.Id, locale, namespacePrefix);

                WowAchievementCategoryResource resource = null;
                try
                {
                    resource = await RateLimiter.ExecuteWithRetryAsync(
                        async () => await GetJsonAsync<WowAchievementCategoryResource>(resourceUrl, ct, bearerToken).ConfigureAwait(false),
                        IsTransientError, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    _logger?.Debug(ex, $"[BattleNet/WoW] Failed to expand guild achievement category id={reference.Id}.");
                }

                foreach (var sub in resource?.Subcategories ?? new List<WowAchievementCategoryReference>())
                {
                    if (sub != null && sub.Id > 0 && guildIds.Add(sub.Id))
                    {
                        pending.Enqueue(sub);
                    }
                }
            }

            if (_cachedWowGuildCategoryIds == null)
            {
                _cachedWowGuildCategoryIds = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
            }

            _cachedWowGuildCategoryIds[cacheKey] = guildIds;
            _logger?.Info($"[BattleNet/WoW] Loaded guild achievement category ids. count={guildIds.Count}, scope={cacheKey}");
            return guildIds;
        }

        public async Task<WowOfficialAchievementDefinition> GetWowOfficialAchievementDefinitionAsync(
            string href,
            string locale,
            string bearerToken,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            var url = AddLocaleToUrl(href, locale);
            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowOfficialAchievementDefinition>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        public async Task<WowOfficialAchievementMediaResponse> GetWowOfficialAchievementMediaAsync(
            string href,
            string bearerToken,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            return await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowOfficialAchievementMediaResponse>(href, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Global WoW achievement rarity, cached for the life of this client. Returns an empty map
        /// when the site is gated or unreachable; only a complete result is cached, so a user who
        /// clears the site check mid-session gets rarity on their next refresh rather than after a
        /// Playnite restart.
        /// </summary>
        public Task<Dictionary<string, double>> GetDataForAzerothWowAchievementRarityAsync(CancellationToken ct) =>
            GetDataForAzerothWowAchievementRarityAsync(WowVersion.Retail, ct);

        /// <summary>One version's rarity map, cached per version on the same terms as retail.</summary>
        internal async Task<Dictionary<string, double>> GetDataForAzerothWowAchievementRarityAsync(WowVersion version, CancellationToken ct)
        {
            version = version ?? WowVersion.Retail;
            if (string.IsNullOrEmpty(version.DataForAzerothPath))
            {
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }

            if (_cachedDataForAzerothWowRarity.TryGetValue(version, out var cached))
            {
                return cached;
            }

            if (_dataForAzeroth != null)
            {
                var result = await _dataForAzeroth.LoadRarityAsync(version, ct).ConfigureAwait(false);
                if (result.Status != DataForAzerothStatus.Ok)
                {
                    return new Dictionary<string, double>(StringComparer.Ordinal);
                }

                _cachedDataForAzerothWowRarity[version] = result.Rarity;
                _logger?.Info($"{version.LogTag} Loaded Data for Azeroth achievement rarity. count={result.Rarity.Count}");
                return result.Rarity;
            }

            return await LoadDataForAzerothRarityViaHttpAsync(version, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Direct HTTP path, used when no browser is available to supply the site's cookies (headless
        /// contexts and unit tests). It is also the path that would light up on its own if the site
        /// ever drops its bot check.
        /// </summary>
        private async Task<Dictionary<string, double>> LoadDataForAzerothRarityViaHttpAsync(WowVersion version, CancellationToken ct)
        {
            var indexUrl = DataForAzerothClient.BuildIndexUrl(version);
            var index = await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<DataForAzerothDynamicIndex>(indexUrl, ct).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(index?.AchievementsRarity))
            {
                // Not cached: an unusable index is a condition that can clear on its own, and caching
                // the empty result would keep rarity blank for the rest of the session.
                _logger?.Warn($"{version.LogTag} Data for Azeroth dynamic index did not include achievementsrarity.");
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }

            var rarityUrl = BuildDataForAzerothDynamicUrl(index.AchievementsRarity);
            var rarity = await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<DataForAzerothAchievementRarityResponse>(rarityUrl, ct).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);

            if (rarity?.Achievements == null)
            {
                _logger?.Warn($"{version.LogTag} Data for Azeroth rarity document carried no achievements.");
                return new Dictionary<string, double>(StringComparer.Ordinal);
            }

            var map = new Dictionary<string, double>(rarity.Achievements, StringComparer.Ordinal);
            _cachedDataForAzerothWowRarity[version] = map;
            _logger?.Info($"{version.LogTag} Loaded Data for Azeroth achievement rarity. count={map.Count}");
            return map;
        }

        /// <summary>
        /// Realms from the official API in a version's dynamic namespace, sorted by name.
        /// </summary>
        public async Task<List<WowRealm>> GetWowOfficialRealmsAsync(
            string region,
            string locale,
            string bearerToken,
            CancellationToken ct,
            string namespacePrefix = "")
        {
            var url = BuildWowRealmIndexUrl(region, locale, namespacePrefix);
            var index = await RateLimiter.ExecuteWithRetryAsync(
                async () => await GetJsonAsync<WowRealmIndexResponse>(url, ct, bearerToken).ConfigureAwait(false),
                IsTransientError, ct).ConfigureAwait(false);
            return (index?.Realms ?? new List<WowRealm>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Slug))
                .OrderBy(r => r.Name ?? r.Slug, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public async Task<List<WowRealm>> GetWowRealmsAsync(string region, CancellationToken ct)
        {
            var hash = await GetWowSha256HashAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(hash))
            {
                _logger?.Warn("[BattleNet/API] Could not obtain WoW GraphQL SHA256 hash.");
                return new List<WowRealm>();
            }

            var payload = $"{{\"operationName\":\"GetRealmStatusData\",\"variables\":{{\"input\":{{\"compoundRegionGameVersionSlug\":\"{region}\"}}}},\"extensions\":{{\"persistedQuery\":{{\"version\":1,\"sha256Hash\":\"{hash}\"}}}}}}";
            var response = await _httpClient.PostAsync(WowGraphQlUrl, new StringContent(payload, Encoding.UTF8, "application/json"), ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonConvert.DeserializeObject<WowRegionResult>(json);
            var realms = result?.Data?.Realms ?? new List<WowRealm>();
            _logger?.Info($"[BattleNet/API] Loaded WoW realms. region={region ?? "<none>"}, count={realms.Count}");
            return realms;
        }

        // --- Transient error detection ---

        public static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex, e =>
                // A gated site is a standing condition the user has to clear, not a blip. Without
                // this rule the 405 arrives as an HttpRequestException, which the canonical
                // classifier treats as retryable, so every WoW game burns the full backoff ladder
                // waiting on an answer that cannot change until someone ticks a checkbox.
                e is DataForAzerothGatedException ? false :
                e is BattleNetTransientException ? true : (bool?)null);
        }

        // --- Private ---

        internal static string BuildSc2ProfileUrl(string apiRegion, int regionId, int realmId, int profileId, string locale)
        {
            return string.Format(
                Sc2ProfileUrl,
                NormalizeApiRegion(apiRegion),
                regionId,
                realmId,
                profileId,
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale));
        }

        internal static string BuildSc2AchievementsUrl(string apiRegion, int regionId, string locale)
        {
            return string.Format(
                Sc2AchievementsUrl,
                NormalizeApiRegion(apiRegion),
                regionId,
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale));
        }

        internal static string BuildTokenUrl(string apiRegion)
        {
            return string.Format(TokenUrl, NormalizeApiRegion(apiRegion));
        }

        internal static string BuildAuthorizeUrl(string apiRegion)
        {
            return string.Format(AuthorizeUrl, NormalizeApiRegion(apiRegion));
        }

        internal static string BuildUserInfoUrl(string apiRegion)
        {
            return string.Format(UserInfoUrl, NormalizeApiRegion(apiRegion));
        }

        internal static string BuildWowRealmIndexUrl(string region, string locale, string namespacePrefix = "")
        {
            return string.Format(
                WowRealmIndexUrl,
                NormalizeApiRegion(region),
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildWowOfficialAccountProfileUrl(string region, string locale, string namespacePrefix = "")
        {
            return string.Format(
                WowOfficialAccountProfileUrl,
                NormalizeApiRegion(region),
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildWowOfficialCharacterAchievementsUrl(
            string region,
            string realmSlug,
            string character,
            string locale,
            string namespacePrefix = "")
        {
            return string.Format(
                WowOfficialCharacterAchievementsUrl,
                NormalizeApiRegion(region),
                Uri.EscapeDataString((realmSlug ?? string.Empty).Trim().ToLowerInvariant()),
                Uri.EscapeDataString((character ?? string.Empty).Trim().ToLowerInvariant()),
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        /// <summary>
        /// Turns a character profile link (".../profile/wow/character/{realm}/{name}?namespace=...")
        /// into its achievements URL, keeping the link's namespace.
        /// </summary>
        internal static string BuildCharacterAchievementsUrlFromProfileHref(string profileHref, string locale)
        {
            if (string.IsNullOrWhiteSpace(profileHref) ||
                !Uri.TryCreate(profileHref, UriKind.Absolute, out var uri) ||
                uri.AbsolutePath.IndexOf("/profile/wow/character/", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var path = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            if (!path.EndsWith("/achievements", StringComparison.OrdinalIgnoreCase))
            {
                path += "/achievements";
            }

            return AddLocaleToUrl(path + uri.Query, string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale);
        }

        internal static string BuildWowOfficialAchievementIndexUrl(string region, string locale, string namespacePrefix = "")
        {
            return string.Format(
                WowOfficialAchievementIndexUrl,
                NormalizeApiRegion(region),
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildWowOfficialAchievementDefinitionUrl(
            string region,
            int achievementId,
            string locale,
            string namespacePrefix = "")
        {
            return string.Format(
                WowOfficialAchievementUrl,
                NormalizeApiRegion(region),
                achievementId,
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildWowAchievementCategoryIndexUrl(string region, string locale, string namespacePrefix = "")
        {
            return string.Format(
                WowAchievementCategoryIndexUrl,
                NormalizeApiRegion(region),
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildWowAchievementCategoryUrl(string region, int categoryId, string locale, string namespacePrefix = "")
        {
            return string.Format(
                WowAchievementCategoryUrl,
                NormalizeApiRegion(region),
                categoryId,
                Uri.EscapeDataString(string.IsNullOrWhiteSpace(locale) ? DefaultApiLocale : locale),
                namespacePrefix ?? string.Empty);
        }

        internal static string BuildDataForAzerothDynamicUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                ? absolute.ToString()
                : new Uri(new Uri(DataForAzerothBaseUrl), path).ToString();
        }

        internal static string MapSc2RegionIdToApiRegion(int regionId)
        {
            switch (regionId)
            {
                case 2: return "eu";
                case 3: return "kr";
                case 5: return "cn";
                case 1:
                default:
                    return "us";
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            return new HttpClient(new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            });
        }

        private async Task<string> GetClientCredentialsTokenAsync(
            string apiRegion,
            string clientId,
            string clientSecret,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new BattleNetTransientException("Battle.net API credentials are missing.");
            }

            var normalizedRegion = NormalizeApiRegion(apiRegion);
            if (!string.IsNullOrWhiteSpace(_cachedAccessToken) &&
                string.Equals(_cachedTokenClientId, clientId, StringComparison.Ordinal) &&
                string.Equals(_cachedTokenRegion, normalizedRegion, StringComparison.OrdinalIgnoreCase) &&
                _cachedAccessTokenExpiresUtc > DateTime.UtcNow.AddMinutes(1))
            {
                return _cachedAccessToken;
            }

            var token = await PostTokenAsync(
                normalizedRegion,
                clientId,
                clientSecret,
                "grant_type=client_credentials",
                ct).ConfigureAwait(false);

            _cachedTokenClientId = clientId;
            _cachedTokenRegion = normalizedRegion;
            _cachedAccessToken = token.AccessToken;
            _cachedAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(Math.Max(token.ExpiresIn, 60));
            return _cachedAccessToken;
        }

        internal static string BuildAuthorizationUrl(
            string apiRegion,
            string clientId,
            string redirectUri,
            string state,
            string scope)
        {
            var query = BuildFormData(new Dictionary<string, string>
            {
                { "client_id", clientId ?? string.Empty },
                { "redirect_uri", redirectUri ?? string.Empty },
                { "response_type", "code" },
                { "scope", scope ?? string.Empty },
                { "state", state ?? string.Empty }
            });

            return BuildAuthorizeUrl(apiRegion) + "?" + query;
        }

        private async Task<BattleNetApiTokenResponse> PostTokenAsync(
            string region,
            string clientId,
            string clientSecret,
            string formBody,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            {
                throw new BattleNetTransientException("Battle.net API credentials are missing.");
            }

            var apiRegion = NormalizeApiRegion(region);
            var tokenUrl = BuildTokenUrl(apiRegion);

            using (var request = new HttpRequestMessage(HttpMethod.Post, tokenUrl))
            {
                var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(clientId.Trim() + ":" + clientSecret.Trim()));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                request.Content = new StringContent(formBody ?? string.Empty, Encoding.UTF8, "application/x-www-form-urlencoded");

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500)
                    {
                        throw new BattleNetTransientException($"HTTP {(int)response.StatusCode} from {UrlHostAndPath(tokenUrl)}");
                    }

                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var token = JsonConvert.DeserializeObject<BattleNetApiTokenResponse>(json);
                    if (string.IsNullOrWhiteSpace(token?.AccessToken))
                    {
                        throw new BattleNetTransientException("Battle.net API token response did not include an access token.");
                    }

                    return token;
                }
            }
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct, string bearerToken = null) where T : class
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (!string.IsNullOrWhiteSpace(bearerToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                }

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    if ((int)response.StatusCode == 429 ||
                        (int)response.StatusCode >= 500)
                    {
                        throw new BattleNetTransientException($"HTTP {(int)response.StatusCode} from {UrlHostAndPath(url)}");
                    }

                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync();
                    var value = JsonConvert.DeserializeObject<T>(json);
                    return value;
                }
            }
        }

        private async Task<string> GetWowSha256HashAsync(CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(_wowSha256Hash))
            {
                return _wowSha256Hash;
            }

            try
            {
                var statusHtml = await _httpClient.GetStringAsync(WowStatusUrl);
                var scriptMatch = Regex.Match(statusHtml, @"<script\s+src=""([^""]+realm-status.\w*.js)"">", RegexOptions.IgnoreCase);
                if (!scriptMatch.Success)
                {
                    _logger?.Warn("[BattleNet/API] Could not find WoW realm-status script on status page.");
                    return null;
                }

                var scriptUrl = new Uri(new Uri(WowStatusUrl), scriptMatch.Groups[1].Value).ToString();
                var scriptContent = await _httpClient.GetStringAsync(scriptUrl);

                var hashMatch = Regex.Match(scriptContent, @"""GetRealmStatusData""\)[^,]*,\w*\.documentId=""(\w*)""");
                if (hashMatch.Success)
                {
                    _wowSha256Hash = hashMatch.Groups[1].Value;
                    return _wowSha256Hash;
                }

                _logger?.Warn("[BattleNet/API] WoW realm-status script did not contain the expected GraphQL hash.");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "[BattleNet/API] Failed to fetch WoW realm-status GraphQL hash.");
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient?.Dispose();
            _dataForAzeroth?.Dispose();
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

        private static string BuildFormData(Dictionary<string, string> data)
        {
            var parts = new List<string>();
            foreach (var kvp in data ?? new Dictionary<string, string>())
            {
                parts.Add($"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? string.Empty)}");
            }

            return string.Join("&", parts);
        }

        private static string AddLocaleToUrl(string url, string locale)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(locale))
            {
                return url;
            }

            if (Regex.IsMatch(url, @"[?&]locale=", RegexOptions.IgnoreCase))
            {
                return url;
            }

            return url + (url.Contains("?") ? "&" : "?") + "locale=" + Uri.EscapeDataString(locale);
        }

        private static string NormalizeApiRegion(string apiRegion)
        {
            var value = string.IsNullOrWhiteSpace(apiRegion) ? "us" : apiRegion.Trim().ToLowerInvariant();
            switch (value)
            {
                case "eu":
                case "kr":
                case "tw":
                case "cn":
                case "us":
                    return value;
                default:
                    return "us";
            }
        }

        private static string UrlHostAndPath(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return "<empty>";
            }

            try
            {
                var uri = new Uri(url);
                var path = RedactKnownSensitivePathSegments(uri.AbsolutePath);
                return uri.GetLeftPart(UriPartial.Authority) + path;
            }
            catch
            {
                return "<invalid-url>";
            }
        }

        private static string RedactKnownSensitivePathSegments(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            var segments = path.Split('/');
            for (var i = 0; i < segments.Length; i++)
            {
                if (string.Equals(segments[i], "character", StringComparison.OrdinalIgnoreCase) &&
                    i + 4 < segments.Length &&
                    string.Equals(segments[i + 4], "achievements", StringComparison.OrdinalIgnoreCase))
                {
                    segments[i + 3] = "<character>";
                }

                if (string.Equals(segments[i], "profile", StringComparison.OrdinalIgnoreCase) &&
                    i + 3 < segments.Length &&
                    PathContainsSegment(segments, "sc2"))
                {
                    segments[i + 3] = "<profile>";
                }
            }

            return string.Join("/", segments);
        }

        private static bool PathContainsSegment(string[] segments, string value)
        {
            foreach (var segment in segments)
            {
                if (string.Equals(segment, value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
