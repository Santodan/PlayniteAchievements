using System;
using System.Collections.Generic;
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
        private const string Sc2ProfileUrl = "https://{0}.api.blizzard.com/sc2/legacy/profile/{1}/{2}/{3}?locale={4}";
        private const string Sc2AchievementsUrl = "https://{0}.api.blizzard.com/sc2/legacy/data/achievements/{1}?locale={2}";
        private const string TokenUrl = "https://{0}.battle.net/oauth/token";
        private const string WowBaseAchievementUrl = "https://worldofwarcraft.blizzard.com/{0}/character/{1}/{2}/{3}/achievements/{4}";
        private const string WowGraphQlUrl = "https://worldofwarcraft.blizzard.com/graphql";
        private const string WowStatusUrl = "https://worldofwarcraft.blizzard.com/game/status";
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

        public BattleNetApiClient(ILogger logger)
            : this(logger, CreateDefaultHttpClient())
        {
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

        // --- WoW ---

        public async Task<List<WowAchievementsData>> GetWowAllAchievementsAsync(
            string region, string realmSlug, string character, string locale, CancellationToken ct)
        {
            var results = new List<WowAchievementsData>();
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-us" : locale;
            _logger?.Info($"[BattleNet/API] WoW achievement categories requested. region={region ?? "<none>"}, realmSlug={realmSlug ?? "<none>"}, character={Presence(character)}, locale={effectiveLocale}");

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
                }
            }

            _logger?.Info($"[BattleNet/API] Completed WoW achievement category fetch. region={region ?? "<none>"}, realmSlug={realmSlug ?? "<none>"}, fetched={results.Count}/{WowCategories.Length}");
            return results;
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
            if (ex is BattleNetTransientException) return true;
            if (ex is HttpRequestException) return true;
            if (ex is WebException) return true;
            if (ex is OperationCanceledException) return false;

            if (ex.GetBaseException() is WebException webEx)
            {
                if (webEx.Status == WebExceptionStatus.Timeout ||
                    webEx.Status == WebExceptionStatus.ConnectionClosed ||
                    webEx.Status == WebExceptionStatus.ConnectFailure)
                    return true;
            }

            return false;
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

            var tokenUrl = BuildTokenUrl(normalizedRegion);

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
            catch (Exception)
            {
            }

            return null;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _httpClient?.Dispose();
        }

        private static string Bool(bool value) => value ? "true" : "false";

        private static string MaskId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "<empty>";
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= 4)
            {
                return "****";
            }

            return $"{new string('*', Math.Min(8, trimmed.Length - 4))}{trimmed.Substring(trimmed.Length - 4)}";
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

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
