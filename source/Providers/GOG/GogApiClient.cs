using Newtonsoft.Json;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.GOG.Models;
using Playnite.SDK;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.GOG
{
    internal sealed class GogApiHttpException : Exception
    {
        public HttpStatusCode StatusCode { get; }

        public GogApiHttpException(HttpStatusCode statusCode, string message)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }

    internal sealed class GogTransientException : Exception
    {
        public GogTransientException(string message)
            : base(message)
        {
        }

        public GogTransientException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>
    /// HTTP client for GOG API calls.
    /// Handles achievements fetching and GOGDB client_id lookups.
    /// </summary>
    public sealed class GogApiClient
    {
        private const string AchievementsEndpoint = "https://gameplay.gog.com/clients/{0}/users/{1}/achievements";
        private const string GogDbProductEndpoint = "https://www.gogdb.org/data/products/{0}/product.json";
        private const string DefaultAchievementsLocale = "en-US";

        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly GogSessionManager _sessionManager;
        private readonly GogClientIdCacheStore _clientIdCacheStore;

        public GogApiClient(
            HttpClient httpClient,
            ILogger logger,
            GogSessionManager sessionManager,
            GogClientIdCacheStore clientIdCacheStore)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
            _clientIdCacheStore = clientIdCacheStore ?? throw new ArgumentNullException(nameof(clientIdCacheStore));
        }

        /// <summary>
        /// Fetches achievements for a specific game.
        /// </summary>
        public Task<List<GogAchievementItem>> GetAchievementsAsync(
            string clientId,
            string userId,
            CancellationToken ct)
        {
            return GetAchievementsAsync(clientId, userId, null, ct);
        }

        /// <summary>
        /// Fetches achievements for a specific game, requesting localized strings when supported by the API.
        /// </summary>
        public async Task<List<GogAchievementItem>> GetAchievementsAsync(
            string clientId,
            string userId,
            string globalLanguage,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                _logger?.Warn("[GogApi] ClientId is null or empty.");
                return new List<GogAchievementItem>();
            }

            if (string.IsNullOrWhiteSpace(userId))
            {
                _logger?.Warn("[GogApi] UserId is null or empty.");
                return new List<GogAchievementItem>();
            }

            var token = await _sessionManager.GetAccessTokenAsync(ct).ConfigureAwait(false);
            var locale = MapGlobalLanguageToGogLocale(globalLanguage);
            var url = BuildAchievementsUrl(clientId, userId, locale);

            // _logger?.Debug($"[GogApi] Fetching achievements from: {url.Replace(clientId, "***").Replace(userId, "***")} (locale={locale})");

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                request.Headers.TryAddWithoutValidation("Accept-Language", locale);

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var statusCode = (int)response.StatusCode;

                    if (statusCode == 401 || statusCode == 403)
                    {
                        _logger?.Warn($"[GogApi] Auth failed with status {statusCode}. Token may be expired.");
                        _sessionManager.InvalidateAccessToken();
                        throw new AuthRequiredException("GOG access token expired. Please re-authenticate.");
                    }

                    if (statusCode == 404)
                    {
                        _logger?.Debug($"[GogApi] Achievements not found for clientId={clientId}.");
                        return new List<GogAchievementItem>();
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowForStatusCode(response.StatusCode, "achievements");
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _logger?.Debug("[GogApi] API returned empty response.");
                        return new List<GogAchievementItem>();
                    }

                    GogAchievementResponse achievementResponse;
                    try
                    {
                        achievementResponse = JsonConvert.DeserializeObject<GogAchievementResponse>(json);
                    }
                    catch (Exception ex)
                    {
                        throw new GogTransientException("[GogApi] Failed to parse achievements payload.", ex);
                    }

                    var items = achievementResponse?.Items ?? new List<GogAchievementItem>();
                    if (items.Count > 0)
                    {
                        var titled = items.Count(x =>
                            !string.IsNullOrWhiteSpace(x.ResolvedTitle) &&
                            !string.Equals(x.ResolvedTitle, x.ResolvedAchievementId, StringComparison.OrdinalIgnoreCase));
                        var withRarity = items.Count(x => x.ResolvedRarityPercent.HasValue);
                        _logger?.Debug($"[GogApi] Parsed {items.Count} achievements (titled={titled}, with_rarity={withRarity}).");
                    }

                    return items;
                }
            }
        }

        private static string BuildAchievementsUrl(string clientId, string userId, string locale)
        {
            var safeLocale = string.IsNullOrWhiteSpace(locale) ? DefaultAchievementsLocale : locale.Trim();
            return string.Format(
                AchievementsEndpoint + "?locale={2}",
                Uri.EscapeDataString(clientId),
                Uri.EscapeDataString(userId),
                Uri.EscapeDataString(safeLocale));
        }

        private static string MapGlobalLanguageToGogLocale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return DefaultAchievementsLocale;
            }

            var normalizedRaw = globalLanguage.Trim();
            if (normalizedRaw.IndexOf('-') > 0)
            {
                return normalizedRaw;
            }

            var normalized = normalizedRaw.ToLowerInvariant();
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

            return DefaultAchievementsLocale;
        }

        /// <summary>
        /// Fetches the client_id for a game from GOGDB.
        /// Returns null only for definitive no-data outcomes.
        /// Throws for transient/network failures so caller retry logic can handle them.
        /// </summary>
        public async Task<string> GetClientIdAsync(
            string productId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(productId))
            {
                _logger?.Warn("[GogApi] ProductId is null or empty.");
                return null;
            }

            if (_clientIdCacheStore.TryGetClientId(productId, out var cachedClientId) &&
                !string.IsNullOrWhiteSpace(cachedClientId))
            {
                // _logger?.Debug($"[GogApi] client_id cache hit for productId={productId}.");
                return cachedClientId;
            }

            var url = string.Format(GogDbProductEndpoint, Uri.EscapeDataString(productId));
            _logger?.Debug($"[GogApi] Fetching client_id from GOGDB for productId={productId}");

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logger?.Debug($"[GogApi] GOGDB product not found for productId={productId}.");
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowForStatusCode(response.StatusCode, "gogdb product metadata");
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        throw new GogTransientException($"[GogApi] GOGDB returned empty product payload for productId={productId}.");
                    }

                    GogProductData productData;
                    try
                    {
                        productData = JsonConvert.DeserializeObject<GogProductData>(json);
                    }
                    catch (Exception ex)
                    {
                        throw new GogTransientException($"[GogApi] Failed to parse GOGDB product payload for productId={productId}.", ex);
                    }

                    if (productData == null)
                    {
                        throw new GogTransientException($"[GogApi] Parsed null GOGDB payload for productId={productId}.");
                    }

                    var directClientId = productData.ResolvedClientId?.Trim();
                    _logger?.Debug($"[GogApi] GOGDB direct client_id={directClientId}, build_count={productData.Builds?.Count ?? 0}");

                    if (!string.IsNullOrWhiteSpace(directClientId))
                    {
                        CacheClientId(productId, directClientId);
                        return directClientId;
                    }

                    var buildMetaUrl = productData.PreferredBuildMetaUrl;
                    if (string.IsNullOrWhiteSpace(buildMetaUrl))
                    {
                        _logger?.Debug($"[GogApi] No build metadata URL found for productId={productId}.");
                        return null;
                    }

                    var metaClientId = await GetClientIdFromBuildMetaAsync(buildMetaUrl, ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(metaClientId))
                    {
                        _logger?.Debug($"[GogApi] Resolved client_id from build metadata for productId={productId}.");
                        CacheClientId(productId, metaClientId);
                        return metaClientId;
                    }

                    _logger?.Debug($"[GogApi] Build metadata did not contain client_id for productId={productId}.");
                    return null;
                }
            }
        }

        private void CacheClientId(string productId, string clientId)
        {
            if (string.IsNullOrWhiteSpace(productId) || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            _clientIdCacheStore.SetClientId(productId, clientId);
            _clientIdCacheStore.Save();
        }

        private async Task<string> GetClientIdFromBuildMetaAsync(string buildMetaUrl, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, buildMetaUrl))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        ThrowForStatusCode(response.StatusCode, "build metadata");
                    }

                    var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (payload == null || payload.Length == 0)
                    {
                        throw new GogTransientException("[GogApi] Build metadata response was empty.");
                    }

                    if (!TryDecodeBuildMetaPayload(payload, out var json))
                    {
                        throw new GogTransientException("[GogApi] Failed to decode build metadata payload.");
                    }

                    GogBuildMetaResponse meta;
                    try
                    {
                        meta = JsonConvert.DeserializeObject<GogBuildMetaResponse>(json);
                    }
                    catch (Exception ex)
                    {
                        throw new GogTransientException("[GogApi] Failed to parse build metadata JSON payload.", ex);
                    }

                    return meta?.ClientId?.Trim();
                }
            }
        }

        private static void ThrowForStatusCode(HttpStatusCode statusCode, string endpointName)
        {
            var code = (int)statusCode;
            var message = $"[GogApi] {endpointName} request returned HTTP {code}.";
            throw new GogApiHttpException(statusCode, message);
        }

        private static bool TryDecodeBuildMetaPayload(byte[] payload, out string json)
        {
            json = null;

            var direct = Encoding.UTF8.GetString(payload);
            if (LooksLikeJson(direct))
            {
                json = direct;
                return true;
            }

            // Some metadata responses are zlib-wrapped deflate; DeflateStream expects raw deflate bytes.
            // Strip the 2-byte zlib header and 4-byte checksum trailer.
            if (payload.Length <= 6)
            {
                return false;
            }

            try
            {
                using (var input = new MemoryStream(payload, 2, payload.Length - 6))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream())
                {
                    inflate.CopyTo(output);
                    var decompressed = Encoding.UTF8.GetString(output.ToArray());
                    if (!LooksLikeJson(decompressed))
                    {
                        return false;
                    }

                    json = decompressed;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool LooksLikeJson(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsWhiteSpace(text[i]))
                {
                    continue;
                }

                return text[i] == '{' || text[i] == '[';
            }

            return false;
        }

        /// <summary>
        /// Determines if an HTTP status code represents a transient error.
        /// </summary>
        public static bool IsTransientError(HttpStatusCode statusCode)
        {
            return TransientErrorClassifier.IsTransientStatusCode((int)statusCode);
        }

        /// <summary>
        /// Determines if an exception represents a transient error.
        /// </summary>
        public static bool IsTransientError(Exception ex)
        {
            return TransientErrorClassifier.IsTransient(ex, e =>
                e is GogTransientException ? true :
                e is GogApiHttpException httpEx ? IsTransientError(httpEx.StatusCode) :
                (bool?)null);
        }
    }
}
