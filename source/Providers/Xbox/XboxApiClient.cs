using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Xbox.Models;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Xbox
{
    /// <summary>
    /// HTTP client for Xbox Live achievement APIs.
    /// </summary>
    internal sealed class XboxApiClient : IDisposable
    {
        private readonly ILogger _logger;
        private readonly HttpClient _httpClient;
        private readonly string _globalLanguage;

        private const string AchievementsBaseUrl = "https://achievements.xboxlive.com/users/xuid({0})/achievements";
        private const string TitleAchievementsBaseUrl = "https://achievements.xboxlive.com/users/xuid({0})/titleachievements";
        private const string TitleHubBatchUrl = "https://titlehub.xboxlive.com/titles/batch/decoration/detail";

        public XboxApiClient(ILogger logger, string globalLanguage = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _globalLanguage = globalLanguage;
            _httpClient = HttpClientFactory.Create();
        }

        /// <summary>
        /// Gets achievements for Xbox One/Series X|S games.
        /// Uses contract version 2.
        /// </summary>
        public async Task<XboxOneAchievementResponse> GetXboxOneAchievementsAsync(
            string xuid,
            string titleId,
            AuthorizationData auth,
            CancellationToken ct)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (string.IsNullOrWhiteSpace(xuid)) throw new ArgumentNullException(nameof(xuid));

            ct.ThrowIfCancellationRequested();

            try
            {
                var url = string.Format(AchievementsBaseUrl, xuid);

                if (!string.IsNullOrWhiteSpace(titleId))
                {
                    url += $"?titleId={titleId}&maxItems=1000";
                }
                else
                {
                    url += "?maxItems=10000";
                }

                _logger?.Debug($"[XboxAch] Fetching Xbox One achievements: {url}");

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    XboxSessionManager.SetAuthenticationHeaders(request.Headers, auth, "2", GetLocale());

                    var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.Warn($"[XboxAch] Xbox One achievements request failed: {response.StatusCode}");
                        return null;
                    }

                    return Serialization.FromJson<XboxOneAchievementResponse>(content);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAch] Failed to get Xbox One achievements.");
                return null;
            }
        }

        /// <summary>
        /// Gets unlocked achievements for Xbox 360 games.
        /// Uses contract version 1.
        /// </summary>
        public async Task<Xbox360AchievementResponse> GetXbox360UnlockedAsync(
            string xuid,
            string titleId,
            AuthorizationData auth,
            CancellationToken ct)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (string.IsNullOrWhiteSpace(xuid)) throw new ArgumentNullException(nameof(xuid));
            if (string.IsNullOrWhiteSpace(titleId)) throw new ArgumentNullException(nameof(titleId));

            ct.ThrowIfCancellationRequested();

            try
            {
                var url = string.Format(AchievementsBaseUrl, xuid) + $"?titleId={titleId}&maxItems=1000";

                _logger?.Debug($"[XboxAch] Fetching Xbox 360 unlocked achievements: {url}");

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    XboxSessionManager.SetAuthenticationHeaders(request.Headers, auth, "1", GetLocale());

                    var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.Warn($"[XboxAch] Xbox 360 unlocked achievements request failed: {response.StatusCode}");
                        return null;
                    }

                    return Serialization.FromJson<Xbox360AchievementResponse>(content);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAch] Failed to get Xbox 360 unlocked achievements.");
                return null;
            }
        }

        /// <summary>
        /// Gets all achievements (locked and unlocked) for Xbox 360 games.
        /// Uses contract version 1.
        /// </summary>
        public async Task<Xbox360AchievementResponse> GetXbox360AllAsync(
            string xuid,
            string titleId,
            AuthorizationData auth,
            CancellationToken ct)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (string.IsNullOrWhiteSpace(xuid)) throw new ArgumentNullException(nameof(xuid));
            if (string.IsNullOrWhiteSpace(titleId)) throw new ArgumentNullException(nameof(titleId));

            ct.ThrowIfCancellationRequested();

            try
            {
                var url = string.Format(TitleAchievementsBaseUrl, xuid) + $"?titleId={titleId}&maxItems=1000";

                _logger?.Debug($"[XboxAch] Fetching Xbox 360 all achievements: {url}");

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    XboxSessionManager.SetAuthenticationHeaders(request.Headers, auth, "1", GetLocale());

                    var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.Warn($"[XboxAch] Xbox 360 all achievements request failed: {response.StatusCode}");
                        return null;
                    }

                    return Serialization.FromJson<Xbox360AchievementResponse>(content);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[XboxAch] Failed to get Xbox 360 all achievements.");
                return null;
            }
        }

        /// <summary>
        /// Gets title info by Package Family Name (PFN).
        /// Used to resolve PC Game Pass games to title IDs.
        /// </summary>
        public async Task<XboxTitle> GetTitleInfoByPfnAsync(
            string pfn,
            AuthorizationData auth,
            CancellationToken ct)
        {
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (string.IsNullOrWhiteSpace(pfn)) throw new ArgumentNullException(nameof(pfn));

            ct.ThrowIfCancellationRequested();

            try
            {
                var requestData = new TitleHubRequest
                {
                    pfns = new List<string> { pfn },
                    windowsPhoneProductIds = new List<string>()
                };

                _logger?.Debug($"[XboxAch] Fetching title info for PFN: {pfn}");

                using (var request = new HttpRequestMessage(HttpMethod.Post, TitleHubBatchUrl))
                {
                    XboxSessionManager.SetAuthenticationHeaders(request.Headers, auth, "2", GetLocale());
                    request.Content = new StringContent(
                        Serialization.ToJson(requestData),
                        Encoding.UTF8,
                        "application/json");

                    var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false);
                    var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (!response.IsSuccessStatusCode)
                    {
                        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            _logger?.Debug($"[XboxAch] Title info not found for PFN: {pfn}");
                            return null;
                        }

                        _logger?.Warn($"[XboxAch] Title info request failed: {response.StatusCode}");
                        return null;
                    }

                    var titleHistory = Serialization.FromJson<TitleHistoryResponse>(content);
                    return titleHistory?.titles?.Count > 0 ? titleHistory.titles[0] : null;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"[XboxAch] Failed to get title info for PFN: {pfn}");
                return null;
            }
        }

        /// <summary>
        /// Maps the global language setting to Xbox Live API locale format.
        /// Xbox uses BCP-47 style locale values (for example "en-US", "de-DE", "pt-BR").
        /// </summary>
        private static string MapGlobalLanguageToXboxLocale(string globalLanguage)
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

            // Map Steam-style language keys to Xbox locale values.
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
                { "arabic", "ar-SA" }
            };

            if (localeMap.TryGetValue(normalized, out var locale))
            {
                return locale;
            }

            // Fallback to English
            return "en-US";
        }

        /// <summary>
        /// Gets the mapped locale for the current global language setting.
        /// </summary>
        private string GetLocale()
        {
            return MapGlobalLanguageToXboxLocale(_globalLanguage);
        }

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
