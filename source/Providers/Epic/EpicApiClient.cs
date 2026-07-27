using Newtonsoft.Json;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Epic
{
    internal sealed class EpicApiHttpException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public EpicApiHttpException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal sealed class EpicTransientException : Exception
    {
        public EpicTransientException(string message)
            : base(message)
        {
        }

        public EpicTransientException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }

    internal sealed class EpicApiNotAvailableException : Exception
    {
        public EpicApiNotAvailableException(string message)
            : base(message)
        {
        }
    }

    public sealed class EpicApiClient
    {
        private const string UrlAsset = "https://library-service.live.use1a.on.epicgames.com/library/api/public/items?includeMetadata=true&platform=Windows";
        private const string UrlGraphQl = "https://launcher.store.epicgames.com/graphql";
        private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) EpicGamesLauncher";

        private const string AchievementQuery = @"
query Achievement($SandboxId: String!, $Locale: String!) {
  Achievement {
    productAchievementsRecordBySandbox(sandboxId: $SandboxId, locale: $Locale) {
      productId
      achievements {
        achievement {
          name
          hidden
          unlockedDisplayName
          unlockedDescription
          unlockedIconLink
          lockedIconLink
          XP
          rarity {
            percent
          }
        }
      }
    }
  }
}";

        private const string PlayerAchievementQuery = @"
query playerProfileAchievementsByProductId($EpicAccountId: String!, $ProductId: String!) {
  PlayerProfile {
    playerProfile(epicAccountId: $EpicAccountId) {
      productAchievements(productId: $ProductId) {
        ... on PlayerProductAchievementsResponseSuccess {
          data {
            playerAchievements {
              playerAchievement {
                achievementName
                unlocked
                unlockDate
              }
            }
          }
        }
      }
    }
  }
}";

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly EpicSessionManager _sessionManager;
        private readonly PersistedSettings _settings;
        private readonly SemaphoreSlim _cacheSemaphore = new SemaphoreSlim(1, 1);

        private string _cachedAssetsToken;
        private List<AssetResponse> _cachedAssets;
        private readonly Dictionary<string, AchievementSchemaResponse> _schemaCache =
            new Dictionary<string, AchievementSchemaResponse>(StringComparer.OrdinalIgnoreCase);

        public EpicApiClient(HttpClient httpClient, ILogger logger, EpicSessionManager sessionManager, PersistedSettings settings)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async Task<List<EpicAchievementItem>> GetAchievementsAsync(
            string gameId,
            string accountId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return new List<EpicAchievementItem>();
            }

            var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new EpicAuthRequiredException("Epic access token is missing.");
            }

            if (string.IsNullOrWhiteSpace(accountId))
            {
                accountId = _sessionManager.GetAccountId();
            }

            if (string.IsNullOrWhiteSpace(accountId))
            {
                throw new EpicAuthRequiredException("Epic account context is required for API calls.");
            }

            var assets = await GetCachedAssetsAsync(token, ct).ConfigureAwait(false);
            var asset = assets.FirstOrDefault(a =>
                string.Equals(a.AppName, gameId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(a.Namespace, gameId, StringComparison.OrdinalIgnoreCase));

            if (asset == null || string.IsNullOrWhiteSpace(asset.Namespace))
            {
                _logger?.Debug($"[EpicApi] No asset matched gameId={gameId}.");
                return new List<EpicAchievementItem>();
            }

            var locale = MapGlobalLanguageToEpicLocale(_settings?.GlobalLanguage);
            var schema = await GetCachedAchievementSchemaAsync(asset.Namespace, locale, token, ct).ConfigureAwait(false);
            if (schema?.Data?.Achievement?.ProductAchievementsRecordBySandbox?.Achievements == null)
            {
                _logger?.Debug($"[EpicApi] No achievement schema found for namespace={asset.Namespace}.");
                return new List<EpicAchievementItem>();
            }

            var items = schema.Data.Achievement.ProductAchievementsRecordBySandbox.Achievements
                .Where(x => x?.Achievement != null && !string.IsNullOrWhiteSpace(x.Achievement.Name))
                .Select(x => new EpicAchievementItem
                {
                    AchievementId = x.Achievement.Name,
                    Title = x.Achievement.UnlockedDisplayName,
                    Description = x.Achievement.UnlockedDescription,
                    UnlockedIconUrl = x.Achievement.UnlockedIconLink,
                    LockedIconUrl = x.Achievement.LockedIconLink,
                    XP = x.Achievement.XP,
                    Hidden = x.Achievement.Hidden,
                    RarityPercent = x.Achievement.Rarity?.Percent
                })
                .ToList();

            var productId = schema.Data.Achievement.ProductAchievementsRecordBySandbox.ProductId;
            if (string.IsNullOrWhiteSpace(productId) || items.Count == 0)
            {
                return items;
            }

            var progress = await QueryPlayerAchievementsAsync(accountId, productId, token, ct).ConfigureAwait(false);
            var playerAchievements = progress?.Data?.PlayerProfile?.PlayerProfileInfo?.ProductAchievements?.Data?.PlayerAchievements;
            if (playerAchievements == null || playerAchievements.Count == 0)
            {
                return items;
            }

            var unlockedMap = playerAchievements
                .Where(x => x?.PlayerAchievement != null && !string.IsNullOrWhiteSpace(x.PlayerAchievement.AchievementName))
                .ToDictionary(
                    x => x.PlayerAchievement.AchievementName,
                    x => x.PlayerAchievement,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.AchievementId))
                {
                    continue;
                }

                if (!unlockedMap.TryGetValue(item.AchievementId, out var progressItem) || !progressItem.Unlocked)
                {
                    continue;
                }

                item.UnlockTimeUtc = ParseUnlockDate(progressItem.UnlockDate);
            }

            return items;
        }

        private async Task<List<AssetResponse>> GetAssetsAsync(string token, CancellationToken ct)
        {
            var result = new List<AssetResponse>();
            var nextUrl = UrlAsset;

            while (!string.IsNullOrWhiteSpace(nextUrl))
            {
                var page = await SendGetAsync<LibraryItemsResponse>(nextUrl, token, ct).ConfigureAwait(false);
                if (page?.Records != null)
                {
                    result.AddRange(page.Records);
                }

                var nextCursor = page?.ResponseMetadata?.NextCursor;
                nextUrl = string.IsNullOrWhiteSpace(nextCursor)
                    ? null
                    : UrlAsset + "&cursor=" + Uri.EscapeDataString(nextCursor);
            }

            return result;
        }

        private async Task<List<AssetResponse>> GetCachedAssetsAsync(string token, CancellationToken ct)
        {
            if (_cachedAssets != null &&
                _cachedAssetsToken == token)
            {
                return _cachedAssets;
            }

            await _cacheSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_cachedAssets != null &&
                    _cachedAssetsToken == token)
                {
                    return _cachedAssets;
                }

                var assets = await GetAssetsAsync(token, ct).ConfigureAwait(false);
                _cachedAssets = assets ?? new List<AssetResponse>();
                _cachedAssetsToken = token;
                _schemaCache.Clear();
                return _cachedAssets;
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        private async Task<AchievementSchemaResponse> GetCachedAchievementSchemaAsync(string sandboxId, string locale, string token, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sandboxId))
            {
                return null;
            }

            var cacheKey = BuildSchemaCacheKey(sandboxId, locale);

            if (_schemaCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            var fetched = await QueryAchievementSchemaAsync(sandboxId, locale, token, ct).ConfigureAwait(false);

            await _cacheSemaphore.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!_schemaCache.ContainsKey(cacheKey))
                {
                    _schemaCache[cacheKey] = fetched;
                }

                return _schemaCache[cacheKey];
            }
            finally
            {
                _cacheSemaphore.Release();
            }
        }

        private static string BuildSchemaCacheKey(string sandboxId, string locale)
        {
            var safeSandboxId = string.IsNullOrWhiteSpace(sandboxId) ? string.Empty : sandboxId.Trim();
            var safeLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale.Trim();
            return safeSandboxId + "|" + safeLocale;
        }

        private Task<AchievementSchemaResponse> QueryAchievementSchemaAsync(string sandboxId, string locale, string token, CancellationToken ct)
        {
            var variables = new
            {
                SandboxId = sandboxId,
                Locale = locale
            };

            return QueryGraphQlAsync<AchievementSchemaResponse>(AchievementQuery, variables, token, ct);
        }

        /// <summary>
        /// Maps the global language setting to Epic Games API locale format.
        /// Epic GraphQL locale is typically a BCP-47 style value (for example "en-US", "de-DE", "pt-BR").
        /// </summary>
        private static string MapGlobalLanguageToEpicLocale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return "en-US";
            }

            var normalizedRaw = globalLanguage.Trim();

            // If caller already provided an explicit locale, pass it through.
            if (normalizedRaw.IndexOf('-') > 0)
            {
                return normalizedRaw;
            }

            var normalized = normalizedRaw.ToLowerInvariant();

            // Map Steam-style language keys to Epic locale values.
            var localeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "english", "en-US" },
                { "german", "de-DE" },
                { "french", "fr-FR" },
                { "spanish", "es-ES" },
                { "latam", "es-419" },
                { "italian", "it-IT" },
                { "portuguese", "pt-PT" },
                { "brazilian", "pt-BR" },
                { "brazilianportuguese", "pt-BR" },
                { "russian", "ru-RU" },
                { "polish", "pl-PL" },
                { "dutch", "nl-NL" },
                { "swedish", "sv-SE" },
                { "finnish", "fi-FI" },
                { "danish", "da-DK" },
                { "norwegian", "nb-NO" },
                { "hungarian", "hu-HU" },
                { "czech", "cs-CZ" },
                { "romanian", "ro-RO" },
                { "turkish", "tr-TR" },
                { "greek", "el-GR" },
                { "bulgarian", "bg-BG" },
                { "ukrainian", "uk-UA" },
                { "thai", "th-TH" },
                { "vietnamese", "vi-VN" },
                { "japanese", "ja-JP" },
                { "koreana", "ko-KR" },
                { "korean", "ko-KR" },
                { "schinese", "zh-CN" },
                { "tchinese", "zh-Hant" },
                { "arabic", "ar" }
            };

            if (localeMap.TryGetValue(normalized, out var locale))
            {
                return locale;
            }

            // Fallback to English
            return "en-US";
        }

        private Task<PlayerAchievementsResponse> QueryPlayerAchievementsAsync(string epicAccountId, string productId, string token, CancellationToken ct)
        {
            var variables = new
            {
                EpicAccountId = epicAccountId,
                ProductId = productId
            };

            return QueryGraphQlAsync<PlayerAchievementsResponse>(PlayerAchievementQuery, variables, token, ct);
        }

        private async Task<T> QueryGraphQlAsync<T>(string query, object variables, string token, CancellationToken ct)
            where T : class
        {
            var result = await TryQueryGraphQlAsync<T>(query, variables, token, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result.Value;
            }

            // On auth error, try token refresh and retry once
            if (result.IsAuthError)
            {
                _logger?.Debug("[EpicApi] GraphQL request returned auth error, attempting token refresh.");
                var refreshed = await _sessionManager.TryRefreshTokenAsync(ct).ConfigureAwait(false);
                if (refreshed)
                {
                    var newToken = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    var retryResult = await TryQueryGraphQlAsync<T>(query, variables, newToken, ct).ConfigureAwait(false);
                    if (retryResult.IsSuccess)
                    {
                        return retryResult.Value;
                    }
                }
            }

            if (result.IsAuthError)
            {
                throw new EpicAuthRequiredException("Epic authorization failed while querying achievements.");
            }

            ThrowForStatusCode(result.StatusCode.Value, "graphql");
            return null;
        }

        private async Task<RequestResult<T>> TryQueryGraphQlAsync<T>(string query, object variables, string token, CancellationToken ct)
            where T : class
        {
            var payload = new
            {
                query,
                variables
            };

            var json = JsonConvert.SerializeObject(payload);
            var locale = MapGlobalLanguageToEpicLocale(_settings?.GlobalLanguage);
            using (var request = new HttpRequestMessage(HttpMethod.Post, UrlGraphQl))
            {
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                AddStandardHeaders(request, token, locale);

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return RequestResult<T>.AuthError(response.StatusCode);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return RequestResult<T>.Error(response.StatusCode);
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return RequestResult<T>.Ok(default(T));
                    }

                    try
                    {
                        return RequestResult<T>.Ok(JsonConvert.DeserializeObject<T>(body));
                    }
                    catch (Exception ex)
                    {
                        throw new EpicTransientException("[EpicApi] Failed to parse GraphQL payload.", ex);
                    }
                }
            }
        }

        private async Task<T> SendGetAsync<T>(string url, string token, CancellationToken ct)
            where T : class
        {
            var result = await TrySendGetAsync<T>(url, token, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result.Value;
            }

            // On auth error, try token refresh and retry once
            if (result.IsAuthError)
            {
                _logger?.Debug("[EpicApi] API request returned auth error, attempting token refresh.");
                var refreshed = await _sessionManager.TryRefreshTokenAsync(ct).ConfigureAwait(false);
                if (refreshed)
                {
                    var newToken = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
                    var retryResult = await TrySendGetAsync<T>(url, newToken, ct).ConfigureAwait(false);
                    if (retryResult.IsSuccess)
                    {
                        return retryResult.Value;
                    }
                }
            }

            if (result.IsAuthError)
            {
                throw new EpicAuthRequiredException("Epic authorization failed while calling API.");
            }

            ThrowForStatusCode(result.StatusCode.Value, "api");
            return null;
        }

        private async Task<RequestResult<T>> TrySendGetAsync<T>(string url, string token, CancellationToken ct)
            where T : class
        {
            var locale = MapGlobalLanguageToEpicLocale(_settings?.GlobalLanguage);
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                AddStandardHeaders(request, token, locale);

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        return RequestResult<T>.AuthError(response.StatusCode);
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        return RequestResult<T>.Error(response.StatusCode);
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return RequestResult<T>.Ok(default(T));
                    }

                    try
                    {
                        return RequestResult<T>.Ok(JsonConvert.DeserializeObject<T>(body));
                    }
                    catch (Exception ex)
                    {
                        throw new EpicTransientException("[EpicApi] Failed to parse API payload.", ex);
                    }
                }
            }
        }

        private static DateTime? ParseUnlockDate(string unlockDate)
        {
            if (string.IsNullOrWhiteSpace(unlockDate))
            {
                return null;
            }

            if (DateTime.TryParseExact(
                unlockDate,
                "yyyy-MM-ddTHH:mm:ss.fffK",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return parsed;
            }

            if (DateTime.TryParse(unlockDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
            {
                return parsed.ToUniversalTime();
            }

            return null;
        }

        private static void AddStandardHeaders(HttpRequestMessage request, string token, string locale = null)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", "bearer " + token);
            if (!string.IsNullOrWhiteSpace(locale))
            {
                request.Headers.TryAddWithoutValidation("Accept-Language", locale);
            }
        }

        private static void ThrowForStatusCode(HttpStatusCode statusCode, string endpointName)
        {
            var code = (int)statusCode;
            throw new EpicApiHttpException(statusCode, $"[EpicApi] {endpointName} request returned HTTP {code}.");
        }

        public static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex, e =>
                e is EpicTransientException ? true :
                e is TaskCanceledException ? true :
                e is EpicApiHttpException httpEx ? TransientErrorClassifier.IsTransientStatusCode((int)httpEx.StatusCode) :
                (bool?)null);
        }

        private sealed class RequestResult<T>
            where T : class
        {
            public bool IsSuccess { get; }
            public T Value { get; }
            public HttpStatusCode? StatusCode { get; }
            public bool IsAuthError { get; }

            private RequestResult(bool isSuccess, T value, HttpStatusCode? statusCode, bool isAuthError)
            {
                IsSuccess = isSuccess;
                Value = value;
                StatusCode = statusCode;
                IsAuthError = isAuthError;
            }

            public static RequestResult<T> Ok(T value) => new RequestResult<T>(true, value, null, false);
            public static RequestResult<T> Error(HttpStatusCode statusCode) => new RequestResult<T>(false, null, statusCode, false);
            public static RequestResult<T> AuthError(HttpStatusCode statusCode) => new RequestResult<T>(false, null, statusCode, true);
        }

        private sealed class LibraryItemsResponse
        {
            [JsonProperty("responseMetadata")]
            public ResponseMetadata ResponseMetadata { get; set; }

            [JsonProperty("records")]
            public List<AssetResponse> Records { get; set; }
        }

        private sealed class ResponseMetadata
        {
            [JsonProperty("nextCursor")]
            public string NextCursor { get; set; }
        }

        private sealed class AssetResponse
        {
            [JsonProperty("namespace")]
            public string Namespace { get; set; }

            [JsonProperty("appName")]
            public string AppName { get; set; }
        }

        private sealed class AchievementSchemaResponse
        {
            [JsonProperty("data")]
            public AchievementDataRoot Data { get; set; }
        }

        private sealed class AchievementDataRoot
        {
            [JsonProperty("Achievement")]
            public AchievementNode Achievement { get; set; }
        }

        private sealed class AchievementNode
        {
            [JsonProperty("productAchievementsRecordBySandbox")]
            public ProductAchievementRecord ProductAchievementsRecordBySandbox { get; set; }
        }

        private sealed class ProductAchievementRecord
        {
            [JsonProperty("productId")]
            public string ProductId { get; set; }

            [JsonProperty("achievements")]
            public List<AchievementWrapper> Achievements { get; set; }
        }

        private sealed class AchievementWrapper
        {
            [JsonProperty("achievement")]
            public AchievementNodeDetail Achievement { get; set; }
        }

        private sealed class AchievementNodeDetail
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("hidden")]
            public bool Hidden { get; set; }

            [JsonProperty("unlockedDisplayName")]
            public string UnlockedDisplayName { get; set; }

            [JsonProperty("unlockedDescription")]
            public string UnlockedDescription { get; set; }

            [JsonProperty("unlockedIconLink")]
            public string UnlockedIconLink { get; set; }

            [JsonProperty("lockedIconLink")]
            public string LockedIconLink { get; set; }

            [JsonProperty("XP")]
            public int? XP { get; set; }

            [JsonProperty("rarity")]
            public RarityNode Rarity { get; set; }
        }

        private sealed class RarityNode
        {
            [JsonProperty("percent")]
            public double? Percent { get; set; }
        }

        private sealed class PlayerAchievementsResponse
        {
            [JsonProperty("data")]
            public PlayerAchievementsDataRoot Data { get; set; }
        }

        private sealed class PlayerAchievementsDataRoot
        {
            [JsonProperty("PlayerProfile")]
            public PlayerProfileNode PlayerProfile { get; set; }
        }

        private sealed class PlayerProfileNode
        {
            [JsonProperty("playerProfile")]
            public PlayerProfileInfo PlayerProfileInfo { get; set; }
        }

        private sealed class PlayerProfileInfo
        {
            [JsonProperty("productAchievements")]
            public ProductAchievementsNode ProductAchievements { get; set; }
        }

        private sealed class ProductAchievementsNode
        {
            [JsonProperty("data")]
            public ProductAchievementsData Data { get; set; }
        }

        private sealed class ProductAchievementsData
        {
            [JsonProperty("playerAchievements")]
            public List<PlayerAchievementWrapper> PlayerAchievements { get; set; }
        }

        private sealed class PlayerAchievementWrapper
        {
            [JsonProperty("playerAchievement")]
            public PlayerAchievementDetail PlayerAchievement { get; set; }
        }

        private sealed class PlayerAchievementDetail
        {
            [JsonProperty("achievementName")]
            public string AchievementName { get; set; }

            [JsonProperty("unlocked")]
            public bool Unlocked { get; set; }

            [JsonProperty("unlockDate")]
            public string UnlockDate { get; set; }
        }
    }

    public sealed class EpicAchievementItem
    {
        public string AchievementId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string UnlockedIconUrl { get; set; }
        public string LockedIconUrl { get; set; }
        public int? XP { get; set; }
        public bool Hidden { get; set; }
        public DateTime? UnlockTimeUtc { get; set; }
        public double? RarityPercent { get; set; }
    }
}
