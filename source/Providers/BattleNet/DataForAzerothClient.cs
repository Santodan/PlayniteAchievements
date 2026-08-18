using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.BattleNet.Models;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.BattleNet
{
    /// <summary>
    /// Raised when dataforazeroth.com answers with its bot-check interstitial instead of data.
    /// Carries enough context to diagnose from a log without ever printing a cookie value.
    /// </summary>
    internal sealed class DataForAzerothGatedException : Exception
    {
        public DataForAzerothGatedException(string message, int statusCode, bool cookieSent, string cookieNames)
            : base(message)
        {
            StatusCode = statusCode;
            CookieSent = cookieSent;
            CookieNames = cookieNames;
        }

        public int StatusCode { get; }

        /// <summary>Whether a site cookie was attached to the request that was refused.</summary>
        public bool CookieSent { get; }

        /// <summary>Names only, never values.</summary>
        public string CookieNames { get; }
    }

    internal enum DataForAzerothStatus
    {
        /// <summary>Data was served and looks complete.</summary>
        Ok,

        /// <summary>The site is asking for its human check; the user must clear it in a browser.</summary>
        Gated,

        /// <summary>Something else went wrong: transport, parsing, or an implausible payload.</summary>
        Failed
    }

    internal sealed class DataForAzerothRarityResult
    {
        public DataForAzerothRarityResult(DataForAzerothStatus status, Dictionary<string, double> rarity)
        {
            Status = status;
            Rarity = rarity ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }

        public DataForAzerothStatus Status { get; }

        public Dictionary<string, double> Rarity { get; }
    }

    /// <summary>
    /// Fetches World of Warcraft achievement rarity from dataforazeroth.com.
    ///
    /// The site gates its whole origin behind a bot check: every path answers HTTP 405 with an HTML
    /// interstitial until a "dfa-captcha" cookie is present, which its inline script writes once a
    /// visitor ticks a checkbox. This client replays whatever site cookies the shared browser store
    /// holds, so the credential is always one the user obtained themselves; it never mints one.
    ///
    /// It owns its own HttpClient rather than sharing the Blizzard one, because sending an explicit
    /// Cookie header on .NET Framework requires UseCookies=false and there is no reason to change
    /// cookie handling for the Blizzard endpoints. Keeping the header on a client that only ever
    /// talks to this one host also makes it structurally impossible to leak it elsewhere.
    /// </summary>
    internal sealed class DataForAzerothClient : IDisposable
    {
        public const string SiteDomain = "dataforazeroth.com";
        public const string BaseUrl = "https://dataforazeroth.com/";
        public const string IndexUrl = BaseUrl + "dynamic/index.json";

        /// <summary>The cookie the gate currently checks. Used for diagnostics only - cookies are
        /// selected by domain, so a renamed or added cookie keeps working.</summary>
        public const string GateCookieName = "dfa-captcha";

        public static readonly string[] CookieDomains =
        {
            "dataforazeroth.com",
            ".dataforazeroth.com",
            "www.dataforazeroth.com",
            ".www.dataforazeroth.com"
        };

        private const string LogPrefix = "[BattleNet/DFA]";
        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly TimeSpan GateBackoff = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan CookieMemoTtl = TimeSpan.FromSeconds(60);
        private static readonly RateLimiter RateLimiter = new RateLimiter(1000, 3);

        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly OffscreenViewLeaseSource _offscreenViews;
        private readonly Func<CancellationToken, Task<List<HttpCookie>>> _cookieSource;
        private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
        private readonly object _memoLock = new object();

        private List<HttpCookie> _memoCookies;
        private DateTime _memoCapturedUtc;
        private DateTime _gatedUntilUtc;
        private int _gateWarned;
        private bool _disposed;

        public DataForAzerothClient(IPlayniteAPI api, ILogger logger)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));
            _logger = logger;
            _httpClient = CreateDefaultHttpClient();
            _ownsHttpClient = true;
            _offscreenViews = new OffscreenViewLeaseSource(api, logger);
            _cookieSource = ct => CefCookieReader.ReadAsync(api, _offscreenViews, SiteDomain, logger, LogPrefix, ct);
        }

        /// <summary>
        /// Test seam: an injected handler and cookie source, so orchestration, gate classification,
        /// cookie attachment, and caching are all exercisable with no browser and no sockets.
        /// </summary>
        internal DataForAzerothClient(
            ILogger logger,
            HttpMessageHandler handler,
            Func<CancellationToken, Task<List<HttpCookie>>> cookieSource = null)
        {
            _logger = logger;
            _httpClient = new HttpClient(handler ?? throw new ArgumentNullException(nameof(handler)));
            _httpClient.DefaultRequestHeaders.Add("User-Agent", DefaultUserAgent);
            _ownsHttpClient = true;
            _cookieSource = cookieSource;
        }

        /// <summary>
        /// The smallest rarity map treated as complete. The live map holds tens of thousands of
        /// entries, so anything far below that is a truncated or substituted payload rather than
        /// data worth caching. Settable so tests can work with small fixtures.
        /// </summary>
        internal int MinimumPlausibleRarityEntries { get; set; } = 1000;

        /// <summary>
        /// Loads the global rarity map: resolve the rotating rarity document from the dynamic index,
        /// then fetch it. Never throws for a gated or broken site - the status says which.
        /// </summary>
        public async Task<DataForAzerothRarityResult> LoadRarityAsync(CancellationToken ct)
        {
            await _loadGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (DateTime.UtcNow < _gatedUntilUtc)
                {
                    _logger?.Debug($"{LogPrefix} Skipping rarity fetch; the site check is still outstanding.");
                    return new DataForAzerothRarityResult(DataForAzerothStatus.Gated, null);
                }

                var index = await RateLimiter.ExecuteWithRetryAsync(
                    async () => await GetJsonAsync<DataForAzerothDynamicIndex>(IndexUrl, ct).ConfigureAwait(false),
                    BattleNetApiClient.IsTransientError,
                    ct).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(index?.AchievementsRarity))
                {
                    _logger?.Warn($"{LogPrefix} The dynamic index did not include achievementsrarity; rarity unavailable this run.");
                    return new DataForAzerothRarityResult(DataForAzerothStatus.Failed, null);
                }

                var rarityUrl = BuildDynamicUrl(index.AchievementsRarity);
                var rarity = await RateLimiter.ExecuteWithRetryAsync(
                    async () => await GetJsonAsync<DataForAzerothAchievementRarityResponse>(rarityUrl, ct).ConfigureAwait(false),
                    BattleNetApiClient.IsTransientError,
                    ct).ConfigureAwait(false);

                var map = rarity?.Achievements;
                if (map == null || map.Count == 0)
                {
                    _logger?.Warn($"{LogPrefix} The rarity document carried no achievements; rarity unavailable this run.");
                    return new DataForAzerothRarityResult(DataForAzerothStatus.Failed, null);
                }

                if (map.Count < MinimumPlausibleRarityEntries)
                {
                    _logger?.Warn(
                        $"{LogPrefix} The rarity document held only {map.Count} entries, far below the expected size; " +
                        "treating it as incomplete rather than caching it.");
                    return new DataForAzerothRarityResult(DataForAzerothStatus.Failed, null);
                }

                Interlocked.Exchange(ref _gateWarned, 0);
                _gatedUntilUtc = DateTime.MinValue;
                return new DataForAzerothRarityResult(
                    DataForAzerothStatus.Ok,
                    new Dictionary<string, double>(map, StringComparer.Ordinal));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DataForAzerothGatedException ex)
            {
                NoteGated(ex);
                return new DataForAzerothRarityResult(DataForAzerothStatus.Gated, null);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"{LogPrefix} Rarity fetch failed; WoW achievements are unaffected.");
                return new DataForAzerothRarityResult(DataForAzerothStatus.Failed, null);
            }
            finally
            {
                _loadGate.Release();
            }
        }

        /// <summary>
        /// Single-request check of whether the site will serve data right now. Deliberately runs
        /// through the same client and cookie path production uses, so a cleared verdict means the
        /// replay genuinely works rather than that the browser store alone looks healthy.
        /// </summary>
        public async Task<DataForAzerothStatus> ProbeGateAsync(CancellationToken ct)
        {
            try
            {
                var index = await GetJsonAsync<DataForAzerothDynamicIndex>(IndexUrl, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(index?.AchievementsRarity))
                {
                    return DataForAzerothStatus.Failed;
                }

                _gatedUntilUtc = DateTime.MinValue;
                Interlocked.Exchange(ref _gateWarned, 0);
                return DataForAzerothStatus.Ok;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DataForAzerothGatedException)
            {
                return DataForAzerothStatus.Gated;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"{LogPrefix} Gate probe failed.");
                return DataForAzerothStatus.Failed;
            }
        }

        /// <summary>
        /// Forgets the cached cookies, the gate backoff, and the once-per-session warning, so a
        /// freshly cleared check takes effect on the next attempt instead of after a restart.
        /// </summary>
        public void ResetGateState()
        {
            lock (_memoLock)
            {
                _memoCookies = null;
                _memoCapturedUtc = DateTime.MinValue;
            }

            _gatedUntilUtc = DateTime.MinValue;
            Interlocked.Exchange(ref _gateWarned, 0);
        }

        /// <summary>
        /// Resolves a path from the dynamic index against the site root. Index entries are relative
        /// and carry a rotating content hash, so the URL cannot be hardcoded.
        /// </summary>
        internal static string BuildDynamicUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            return Uri.TryCreate(path, UriKind.Absolute, out var absolute)
                ? absolute.ToString()
                : new Uri(new Uri(BaseUrl), path).ToString();
        }

        /// <summary>
        /// 405 is the gate's current answer, but the site sits behind a CDN and the status code is
        /// the least stable part of its signature, so an HTML body from a JSON endpoint counts too.
        /// A genuine 404 is left alone.
        /// </summary>
        internal static bool IsGatedResponse(int statusCode, string mediaType)
        {
            if (statusCode == 405)
            {
                return true;
            }

            var isHtml = !string.IsNullOrEmpty(mediaType) &&
                mediaType.IndexOf("html", StringComparison.OrdinalIgnoreCase) >= 0;

            return isHtml && (statusCode == 200 || statusCode == 403 || statusCode == 406 || statusCode == 503);
        }

        /// <summary>Recognizes the interstitial by its own markers rather than localized prose.</summary>
        internal static bool LooksLikeBotCheck(string body)
        {
            if (string.IsNullOrEmpty(body))
            {
                return false;
            }

            return body.IndexOf(GateCookieName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   body.IndexOf("bot traffic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task<T> GetJsonAsync<T>(string url, CancellationToken ct) where T : class
        {
            var cookies = await GetCookiesAsync(ct).ConfigureAwait(false);
            var header = CefCookieReader.BuildCookieHeader(cookies);
            var cookieNames = CefCookieReader.DescribeNames(cookies);

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (!string.IsNullOrEmpty(header))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", header);
                }

                using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var status = (int)response.StatusCode;
                    var mediaType = response.Content?.Headers?.ContentType?.MediaType;

                    if (IsGatedResponse(status, mediaType))
                    {
                        throw new DataForAzerothGatedException(
                            $"HTTP {status} ({mediaType ?? "no content-type"}) from {SiteDomain}",
                            status,
                            !string.IsNullOrEmpty(header),
                            cookieNames);
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }

                    if (TransientErrorClassifier.IsTransientStatusCode(status))
                    {
                        throw new BattleNetTransientException($"HTTP {status} from {SiteDomain}");
                    }

                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (LooksLikeBotCheck(json))
                    {
                        throw new DataForAzerothGatedException(
                            $"HTTP {status} carried the bot-check interstitial from {SiteDomain}",
                            status,
                            !string.IsNullOrEmpty(header),
                            cookieNames);
                    }

                    return JsonConvert.DeserializeObject<T>(json);
                }
            }
        }

        private async Task<List<HttpCookie>> GetCookiesAsync(CancellationToken ct)
        {
            if (_cookieSource == null)
            {
                return new List<HttpCookie>();
            }

            lock (_memoLock)
            {
                if (_memoCookies != null && DateTime.UtcNow - _memoCapturedUtc < CookieMemoTtl)
                {
                    return _memoCookies;
                }
            }

            var cookies = CefCookieReader.Filter(
                await _cookieSource(ct).ConfigureAwait(false),
                SiteDomain);

            lock (_memoLock)
            {
                _memoCookies = cookies;
                _memoCapturedUtc = DateTime.UtcNow;
            }

            _logger?.Debug(
                $"{LogPrefix} Loaded {cookies.Count} site cookie(s) from the browser store. " +
                $"names={CefCookieReader.DescribeNames(cookies)}, gateCookie={HasGateCookie(cookies)}");

            return cookies;
        }

        private static bool HasGateCookie(IEnumerable<HttpCookie> cookies)
        {
            foreach (var cookie in cookies ?? new List<HttpCookie>())
            {
                if (cookie != null &&
                    string.Equals(cookie.Name, GateCookieName, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrEmpty(cookie.Value))
                {
                    return true;
                }
            }

            return false;
        }

        private void NoteGated(DataForAzerothGatedException ex)
        {
            _gatedUntilUtc = DateTime.UtcNow.Add(GateBackoff);

            var detail =
                $"{LogPrefix} {SiteDomain} answered with its bot check (HTTP {ex.StatusCode}) instead of rarity data. " +
                $"WoW achievements still synced; global rarity was skipped. cookieSent={ex.CookieSent}, cookies={ex.CookieNames}. " +
                "Open plugin settings > Battle.net and use the Data for Azeroth verify button to clear the check in a " +
                "browser window; it lasts about 7 days. Signing in to Data for Azeroth does not satisfy it.";

            if (Interlocked.CompareExchange(ref _gateWarned, 1, 0) == 0)
            {
                _logger?.Warn(detail);
            }
            else
            {
                _logger?.Debug(detail);
            }
        }

        private static HttpClient CreateDefaultHttpClient()
        {
            // UseCookies=false is required, not incidental: on .NET Framework the handler's own
            // cookie container overwrites a manually set Cookie header, which would silently drop
            // the credential this whole class exists to replay.
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };

            var client = new HttpClient(handler);
            client.DefaultRequestHeaders.Add("User-Agent", DefaultUserAgent);
            return client;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (_ownsHttpClient)
            {
                _httpClient?.Dispose();
            }

            _loadGate?.Dispose();
        }
    }
}
