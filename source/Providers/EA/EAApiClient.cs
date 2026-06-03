using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.EA
{
    public sealed class EAApiClient
    {
        private const string GraphQlEndpoint = "https://service-aggregation-layer.juno.ea.com/graphql";

        private const string OwnedGamesQuery = @"
query GetOwnedGameProducts(
    $locale: Locale!,
    $entitlementEnabled: Boolean!,
    $storefronts: [UserGameProductStorefront!]!,
    $type: [GameProductType!]!,
    $platforms: [GamePlatform!]!,
    $limit: Int!
) {
  me {
    ownedGameProducts(
      locale: $locale
      entitlementEnabled: $entitlementEnabled
      storefronts: $storefronts
      type: $type
      platforms: $platforms
      paging: { limit: $limit }
    ) {
      items {
        originOfferId
        product {
          id
          name
          gameSlug
          baseItem {
            gameType
          }
        }
      }
    }
  }
}";

        private const string AchievementsQuery = @"
query GetAchievements($offerId: String!, $playerPsd: String!, $locale: Locale!) {
  achievements(
    offerId: $offerId
    playerPsd: $playerPsd
    showHidden: true
    locale: $locale
  ) {
    id
    achievements {
      id
      name
      description
      awardCount
      date
    }
  }
}";

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly EASessionManager _sessionManager;

        private List<EaOwnedGame> _cachedOwnedGames;
        private string _cachedTokenForOwnedGames;
        private readonly SemaphoreSlim _cacheSemaphore = new SemaphoreSlim(1, 1);

        public EAApiClient(
            HttpClient httpClient,
            ILogger logger,
            EASessionManager sessionManager)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        internal async Task<EaPlayerIdentity> GetPlayerIdentityAsync(CancellationToken ct)
        {
            var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var response = await QueryGraphQlAsync<GraphQlIdentityResponse>(
                IdentityQueryBody, null, token, ct).ConfigureAwait(false);

            var player = response?.Data?.Me?.Player;
            if (player == null)
            {
                _logger?.Warn("[EAApi] Identity query returned no player data.");
                return null;
            }

            return new EaPlayerIdentity
            {
                Pd = player.Pd,
                Psd = player.Psd,
                DisplayName = player.DisplayName
            };
        }

        public async Task<List<EaOwnedGame>> GetOwnedGamesAsync(CancellationToken ct)
        {
            var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);

            if (_cachedOwnedGames != null && _cachedTokenForOwnedGames == token)
            {
                return _cachedOwnedGames;
            }

            await _cacheSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedOwnedGames != null && _cachedTokenForOwnedGames == token)
                {
                    return _cachedOwnedGames;
                }

                var variables = new
                {
                    locale = "DEFAULT",
                    entitlementEnabled = false,
                    storefronts = new[] { "EA" },
                    type = new[] { "DIGITAL_FULL_GAME", "PACKAGED_FULL_GAME", "DIGITAL_EXTRA_CONTENT", "PACKAGED_EXTRA_CONTENT" },
                    platforms = new[] { "PC" },
                    limit = 9999
                };

                var response = await QueryGraphQlAsync<GraphQlOwnedGamesResponse>(
                    OwnedGamesQuery, variables, token, ct).ConfigureAwait(false);

                var items = response?.Data?.Me?.OwnedGameProducts?.Items;
                if (items == null || items.Count == 0)
                {
                    _logger?.Debug("[EAApi] No owned games returned from EA API.");
                    _cachedOwnedGames = new List<EaOwnedGame>();
                    _cachedTokenForOwnedGames = token;
                    return _cachedOwnedGames;
                }

                var result = items
                    .Where(i => i.Product?.BaseItem?.GameType == "BASE_GAME")
                    .Select(i => new EaOwnedGame
                    {
                        OriginOfferId = i.OriginOfferId,
                        GameSlug = i.Product?.GameSlug,
                        ProductName = i.Product?.Name
                    })
                    .Where(g => !string.IsNullOrWhiteSpace(g.OriginOfferId))
                    .ToList();

                _logger?.Debug($"[EAApi] Owned games returned {items.Count} item(s); {result.Count} base game offer ID(s) after filtering.");

                _cachedOwnedGames = result;
                _cachedTokenForOwnedGames = token;
                return result;
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        public async Task<List<EaAchievementItem>> GetAchievementsAsync(
            string offerId,
            string playerPsd,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(offerId))
            {
                return new List<EaAchievementItem>();
            }

            if (string.IsNullOrWhiteSpace(playerPsd))
            {
                throw new EaAuthRequiredException("EA player sub ID is required for achievement queries.");
            }

            var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);

            var variables = new
            {
                offerId = offerId,
                playerPsd = playerPsd,
                locale = "US"
            };

            var response = await QueryGraphQlAsync<GraphQlAchievementsResponse>(
                AchievementsQuery, variables, token, ct).ConfigureAwait(false);

            var achievementSets = response?.Data?.Achievements;
            if (achievementSets == null || achievementSets.Count == 0)
            {
                _logger?.Debug($"[EAApi] No achievement sets returned for offer ID={offerId}.");
                return new List<EaAchievementItem>();
            }

            var items = achievementSets
                .SelectMany(s => s.AchievementsData ?? new List<EaAchievement>())
                .Select(a => new EaAchievementItem
                {
                    AchievementId = a.Id,
                    Title = a.Name,
                    Description = a.Description,
                    IsUnlocked = a.AwardCount > 0,
                    UnlockTimeUtc = a.AwardCount > 0 && a.Date != default
                        ? EAProviderSupport.NormalizeUtc(a.Date)
                        : null
                })
                .Where(a => !string.IsNullOrWhiteSpace(a.AchievementId))
                .ToList();

            if (items.Count == 0)
            {
                _logger?.Debug($"[EAApi] Achievement sets contained no achievement items for offer ID={offerId}.");
            }

            return items;
        }

        public static bool IsTransientError(Exception ex)
        {
            return EAProviderSupport.IsTransientError(ex);
        }

        private const string IdentityQueryBody = @"query { me { player { pd psd displayName } } }";

        private async Task<T> QueryGraphQlAsync<T>(
            string query,
            object variables,
            string token,
            CancellationToken ct) where T : class
        {
            var body = variables != null
                ? new { query, variables }
                : (object)new { query };

            var json = JsonConvert.SerializeObject(body);

            using (var request = new HttpRequestMessage(HttpMethod.Post, GraphQlEndpoint))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new EaAuthRequiredException($"EA API returned HTTP {(int)response.StatusCode}.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var statusCode = response.StatusCode;
                        if ((int)statusCode == 429 || (int)statusCode >= 500)
                        {
                            throw new EaTransientException($"EA API transient error: HTTP {(int)statusCode}");
                        }

                        throw new EaApiHttpException(statusCode, $"EA API returned HTTP {(int)statusCode}: {responseBody}");
                    }

                    ThrowIfGraphQlErrors(responseBody);
                    return JsonConvert.DeserializeObject<T>(responseBody);
                }
            }
        }

        private static void ThrowIfGraphQlErrors(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
            {
                return;
            }

            var parsed = JObject.Parse(responseBody);
            var errors = parsed["errors"] as JArray;
            if (errors == null || errors.Count == 0)
            {
                return;
            }

            var message = string.Join("; ", errors
                .Select(error => error?["message"]?.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value)));

            if (message.IndexOf("auth", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("token", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("forbidden", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                throw new EaAuthRequiredException($"EA GraphQL auth error: {message}");
            }

            throw new EaApiHttpException(HttpStatusCode.OK, $"EA GraphQL error: {message}");
        }

        private sealed class GraphQlIdentityResponse
        {
            [JsonProperty("data")]
            public IdentityData Data { get; set; }
        }

        private sealed class IdentityData
        {
            [JsonProperty("me")]
            public IdentityMe Me { get; set; }
        }

        private sealed class IdentityMe
        {
            [JsonProperty("player")]
            public IdentityPlayer Player { get; set; }
        }

        private sealed class IdentityPlayer
        {
            [JsonProperty("pd")]
            public string Pd { get; set; }

            [JsonProperty("psd")]
            public string Psd { get; set; }

            [JsonProperty("displayName")]
            public string DisplayName { get; set; }
        }

        private sealed class GraphQlOwnedGamesResponse
        {
            [JsonProperty("data")]
            public OwnedGamesData Data { get; set; }
        }

        private sealed class OwnedGamesData
        {
            [JsonProperty("me")]
            public OwnedGamesMe Me { get; set; }
        }

        private sealed class OwnedGamesMe
        {
            [JsonProperty("ownedGameProducts")]
            public OwnedGamesProducts OwnedGameProducts { get; set; }
        }

        private sealed class OwnedGamesProducts
        {
            [JsonProperty("items")]
            public List<OwnedGameItem> Items { get; set; }
        }

        private sealed class OwnedGameItem
        {
            [JsonProperty("originOfferId")]
            public string OriginOfferId { get; set; }

            [JsonProperty("product")]
            public OwnedGameProduct Product { get; set; }
        }

        private sealed class OwnedGameProduct
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("gameSlug")]
            public string GameSlug { get; set; }

            [JsonProperty("baseItem")]
            public BaseItem BaseItem { get; set; }
        }

        private sealed class BaseItem
        {
            [JsonProperty("gameType")]
            public string GameType { get; set; }
        }

        private sealed class GraphQlAchievementsResponse
        {
            [JsonProperty("data")]
            public AchievementsData Data { get; set; }
        }

        private sealed class AchievementsData
        {
            [JsonProperty("achievements")]
            public List<AchievementSet> Achievements { get; set; }
        }

        private sealed class AchievementSet
        {
            [JsonProperty("id")]
            public string Id { get; set; }

            [JsonProperty("achievements")]
            public List<EaAchievement> AchievementsData { get; set; }
        }
    }
}
