using Playnite.SDK;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.RetroAchievements
{
    internal sealed class RetroAchievementsApiClient : IDisposable
    {
        private const int MaxAttempts = 5;
        private static readonly Uri ApiBase = new Uri("https://retroachievements.org/API/");

        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;

        private readonly string _username;
        private readonly string _apiKey;
        private readonly string _acceptLanguage;

        public RetroAchievementsApiClient(ILogger logger, string username, string apiKey, string globalLanguage = null)
        {
            _logger = logger;
            _username = username?.Trim() ?? string.Empty;
            _apiKey = apiKey?.Trim() ?? string.Empty;
            _acceptLanguage = MapGlobalLanguageToRetroAchievementsLocale(globalLanguage);

            _handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                AllowAutoRedirect = true,
                UseCookies = false
            };

            _http = new HttpClient(_handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
        }

        public void Dispose()
        {
            _http?.Dispose();
            _handler?.Dispose();
        }

        public Task<string> GetGameListPageAsync(int consoleId, int offset, int count, CancellationToken cancel)
        {
            var uri = new Uri(ApiBase, $"API_GetGameList.php?y={Uri.EscapeDataString(_apiKey)}&i={consoleId}&h=1&f=1&o={offset}&c={count}");
            return GetRawJsonAsync(uri, cancel);
        }

        public Task<Models.RaGameListWithTitleResponse> GetGameListAsync(int consoleId, CancellationToken cancel)
        {
            var uri = new Uri(ApiBase, $"API_GetGameList.php?y={Uri.EscapeDataString(_apiKey)}&i={consoleId}&h=1");
            return GetJsonAsync<Models.RaGameListWithTitleResponse>(uri, cancel);
        }

        public Task<RaGameInfoUserProgress> GetGameInfoAndUserProgressAsync(int gameId, CancellationToken cancel)
        {
            return GetGameInfoAndUserProgressAsync(gameId, _username, cancel);
        }

        public Task<RaGameInfoUserProgress> GetGameInfoAndUserProgressAsync(
            int gameId,
            string usernameOrUlid,
            CancellationToken cancel)
        {
            var user = string.IsNullOrWhiteSpace(usernameOrUlid) ? _username : usernameOrUlid.Trim();
            var uri = new Uri(ApiBase, $"API_GetGameInfoAndUserProgress.php?y={Uri.EscapeDataString(_apiKey)}&u={Uri.EscapeDataString(user)}&g={gameId}");
            return GetJsonAsync<RaGameInfoUserProgress>(uri, cancel);
        }

        public Task<RaGameInfoUserProgress> GetGameExtendedAsync(int gameId, CancellationToken cancel)
        {
            var uri = new Uri(ApiBase, $"API_GetGameExtended.php?y={Uri.EscapeDataString(_apiKey)}&i={gameId}");
            return GetJsonAsync<RaGameInfoUserProgress>(uri, cancel);
        }

        public Task<RaUsersIFollowResponse> GetUsersIFollowAsync(int offset, int count, CancellationToken cancel)
        {
            var uri = new Uri(ApiBase, $"API_GetUsersIFollow.php?y={Uri.EscapeDataString(_apiKey)}&o={Math.Max(0, offset)}&c={Math.Max(1, count)}");
            return GetJsonAsync<RaUsersIFollowResponse>(uri, cancel);
        }

        public Task<RaUserCompletionProgressResponse> GetUserCompletionProgressAsync(
            string usernameOrUlid,
            int offset,
            int count,
            CancellationToken cancel)
        {
            var user = string.IsNullOrWhiteSpace(usernameOrUlid) ? _username : usernameOrUlid.Trim();
            var uri = new Uri(ApiBase, $"API_GetUserCompletionProgress.php?y={Uri.EscapeDataString(_apiKey)}&u={Uri.EscapeDataString(user)}&o={Math.Max(0, offset)}&c={Math.Max(1, count)}");
            return GetJsonAsync<RaUserCompletionProgressResponse>(uri, cancel);
        }

        private async Task<T> GetJsonAsync<T>(Uri uri, CancellationToken cancel)
        {
            var json = await GetRawJsonAsync(uri, cancel).ConfigureAwait(false);
            return JsonHelper.Deserialize<T>(json);
        }

        private async Task<string> GetRawJsonAsync(Uri uri, CancellationToken cancel)
        {
            var response = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", "PlayniteAchievements/RetroAchievements");
                if (!string.IsNullOrWhiteSpace(_acceptLanguage))
                {
                    // RetroAchievements API does not document a locale query parameter.
                    // Send Accept-Language as best-effort localization hint.
                    req.Headers.TryAddWithoutValidation("Accept-Language", _acceptLanguage);
                }
                return req;
            }, cancel).ConfigureAwait(false);

            if (response == null)
            {
                throw new HttpRequestException("No response.");
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancel)
        {
            Exception lastException = null;
            TimeSpan backoff = TimeSpan.FromSeconds(1);

            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    using (var req = requestFactory())
                    {
                        var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);

                        if (resp.StatusCode == (HttpStatusCode)429)
                        {
                            var delay = GetRetryAfterDelay(resp) ?? backoff;
                            _logger?.Warn($"[RA] 429 received. Backing off for {delay.TotalSeconds:0.0}s (attempt {attempt}/{MaxAttempts}).");
                            resp.Dispose();
                            await Task.Delay(delay, cancel).ConfigureAwait(false);
                            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                            continue;
                        }

                        if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.RequestTimeout)
                        {
                            _logger?.Warn($"[RA] Server error {(int)resp.StatusCode} on {req.RequestUri} (attempt {attempt}/{MaxAttempts}).");
                            resp.Dispose();
                            await Task.Delay(backoff, cancel).ConfigureAwait(false);
                            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                            continue;
                        }

                        return resp;
                    }
                }
                catch (TaskCanceledException ex) when (!cancel.IsCancellationRequested)
                {
                    lastException = ex;
                    _logger?.Warn($"[RA] Timeout on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                }
                catch (HttpRequestException ex)
                {
                    lastException = ex;
                    _logger?.Warn(ex, $"[RA] HTTP error on attempt {attempt}/{MaxAttempts}.");
                    await Task.Delay(backoff, cancel).ConfigureAwait(false);
                    backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
                }
            }

            throw new HttpRequestException("Request failed after retries.", lastException);
        }

        private static TimeSpan? GetRetryAfterDelay(HttpResponseMessage resp)
        {
            try
            {
                if (resp?.Headers?.RetryAfter == null) return null;
                if (resp.Headers.RetryAfter.Delta.HasValue) return resp.Headers.RetryAfter.Delta.Value;
                if (resp.Headers.RetryAfter.Date.HasValue)
                {
                    var delta = resp.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
                }
            }
            catch
            {
                // ignore
            }

            return null;
        }

        private static string MapGlobalLanguageToRetroAchievementsLocale(string globalLanguage)
        {
            if (string.IsNullOrWhiteSpace(globalLanguage))
            {
                return "en-US";
            }

            var normalizedRaw = globalLanguage.Trim();
            if (normalizedRaw.IndexOf('-') > 0)
            {
                return normalizedRaw;
            }

            var normalized = normalizedRaw.ToLowerInvariant();
            switch (normalized)
            {
                case "english": return "en-US";
                case "german": return "de-DE";
                case "french": return "fr-FR";
                case "spanish": return "es-ES";
                case "latam": return "es-419";
                case "italian": return "it-IT";
                case "portuguese": return "pt-PT";
                case "brazilian":
                case "brazilianportuguese":
                    return "pt-BR";
                case "russian": return "ru-RU";
                case "polish": return "pl-PL";
                case "dutch": return "nl-NL";
                case "swedish": return "sv-SE";
                case "finnish": return "fi-FI";
                case "danish": return "da-DK";
                case "norwegian": return "nb-NO";
                case "hungarian": return "hu-HU";
                case "czech": return "cs-CZ";
                case "romanian": return "ro-RO";
                case "turkish": return "tr-TR";
                case "greek": return "el-GR";
                case "bulgarian": return "bg-BG";
                case "ukrainian": return "uk-UA";
                case "thai": return "th-TH";
                case "vietnamese": return "vi-VN";
                case "japanese": return "ja-JP";
                case "koreana":
                case "korean":
                    return "ko-KR";
                case "schinese": return "zh-CN";
                case "tchinese": return "zh-Hant";
                case "arabic": return "ar";
                default: return "en-US";
            }
        }
    }
}
