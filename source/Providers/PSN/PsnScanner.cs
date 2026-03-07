using Newtonsoft.Json;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.PSN.Models;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.PSN
{
    internal sealed class PsnScanner
    {
        private readonly ILogger _logger;
        private readonly PsnSessionManager _sessionManager;
        private readonly PlayniteAchievementsSettings _settings;

        private const string UrlBase = "https://m.np.playstation.com/api/trophy/v1";
        private const string UrlTrophiesDetailsAll = UrlBase + "/npCommunicationIds/{0}/trophyGroups/all/trophies";
        private const string UrlTrophiesUserAll = UrlBase + "/users/me/npCommunicationIds/{0}/trophyGroups/all/trophies";
        private const string UrlTitlesWithIdsMobile = UrlBase + "/users/me/titles/trophyTitles?npTitleIds={0}";
        private const string UrlAllTrophyTitles = UrlBase + "/users/me/trophyTitles";

        public PsnScanner(
            ILogger logger,
            PlayniteAchievementsSettings settings,
            PsnSessionManager sessionManager)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var providerName = GetProviderName();

            string token;
            try
            {
                token = await _sessionManager.GetAccessTokenAsync(cancel).ConfigureAwait(false);
            }
            catch (PsnAuthRequiredException)
            {
                _logger?.Warn("[PSNAch] Not authenticated (PSN token missing).");
                return new RebuildPayload
                {
                    Summary = new RebuildSummary(),
                    AuthRequired = true
                };
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[PSNAch] Failed to acquire PSN token.");
                return new RebuildPayload
                {
                    Summary = new RebuildSummary(),
                    AuthRequired = true
                };
            }

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            using (var http = new HttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(45);
                SetAuthorizationHeader(http, token);

                var acceptLanguage = MapGlobalLanguageToPsnLocale(_settings?.Persisted?.GlobalLanguage);
                if (!string.IsNullOrWhiteSpace(acceptLanguage))
                {
                    http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", acceptLanguage);
                }

                return await RefreshPipeline.RunProviderGamesAsync(
                    gamesToRefresh,
                    onGameStarting,
                    async (game, tokenCancel) =>
                    {
                        var data = await FetchGameDataAsync(http, game, providerName, tokenCancel).ConfigureAwait(false);
                        return new RefreshPipeline.ProviderGameResult
                        {
                            Data = data
                        };
                    },
                    onGameCompleted,
                    isAuthRequiredException: ex => ex is PsnAuthRequiredException,
                    onGameError: (game, ex, consecutiveErrors) =>
                    {
                        _logger?.Debug(ex, $"[PSNAch] Failed to scan {game?.Name}");
                    },
                    delayBetweenGamesAsync: null,
                    delayAfterErrorAsync: null,
                    cancel).ConfigureAwait(false);
            }
        }

        private async Task<GameAchievementData> FetchGameDataAsync(HttpClient http, Game game, string providerName, CancellationToken cancel)
        {
            var npCommId = await ResolveNpCommunicationIdAsync(http, game, cancel).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(npCommId))
            {
                return null;
            }

            var normalizedId = NormalizeGameId(game?.GameId);
            var serviceSuffixCandidates = PsnSuffixRetryHelper.BuildSuffixCandidates(normalizedId);

            string userJson = null;
            try
            {
                userJson = await PsnSuffixRetryHelper.GetStringWithSuffixRetryAsync(
                    suffix => GetStringWithAuthRetryAsync(
                        http,
                        string.Format(UrlTrophiesUserAll, npCommId) + suffix,
                        $"user trophies for '{game?.Name}'",
                        cancel),
                    serviceSuffixCandidates).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            catch (PsnAuthRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Private profile or no earned progress data is non-fatal.
                _logger?.Debug(ex, $"[PSNAch] User trophies fetch failed for '{game?.Name}'");
                userJson = null;
            }

            string detailsJson;
            try
            {
                detailsJson = await PsnSuffixRetryHelper.GetStringWithSuffixRetryAsync(
                    suffix => GetStringWithAuthRetryAsync(
                        http,
                        string.Format(UrlTrophiesDetailsAll, npCommId) + suffix,
                        $"trophy details for '{game?.Name}'",
                        cancel),
                    serviceSuffixCandidates).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
            }
            catch (PsnAuthRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[PSNAch] Failed to fetch trophy details for '{game?.Name}' (npCommId='{npCommId}')");
                return null;
            }

            var user = string.IsNullOrWhiteSpace(userJson)
                ? null
                : JsonConvert.DeserializeObject<PsnTrophiesUserResponse>(userJson);

            var details = JsonConvert.DeserializeObject<PsnTrophiesDetailResponse>(detailsJson);

            var userByKey = PsnTrophyMatchHelper.BuildUserTrophyLookupByGroupAndId(user?.Trophies);
            var userByTrophyId = PsnTrophyMatchHelper.BuildUserTrophyLookupById(user?.Trophies);

            var achievements = new List<AchievementDetail>();
            var unlockedCount = 0;
            var fallbackMatchCount = 0;
            var fallbackNonDefaultGroupCount = 0;
            foreach (var detail in (details?.Trophies ?? new List<PsnTrophyDetail>())
                .GroupBy(PsnTrophyMatchHelper.GetTrophyKey)
                .Select(g => g.First()))
            {
                PsnTrophyMatchHelper.TryResolveUserTrophy(
                    detail,
                    userByKey,
                    userByTrophyId,
                    out var userEntry,
                    out var usedIdFallback);

                if (usedIdFallback)
                {
                    fallbackMatchCount++;
                    var detailGroup = PsnTrophyMatchHelper.NormalizeGroupId(detail?.TrophyGroupId);
                    if (!string.Equals(detailGroup, "default", StringComparison.OrdinalIgnoreCase))
                    {
                        fallbackNonDefaultGroupCount++;
                    }
                }

                DateTime? unlockUtc = null;
                var unlocked = userEntry != null && userEntry.Earned;
                if (unlocked)
                {
                    unlockedCount++;

                    if (!string.IsNullOrWhiteSpace(userEntry.EarnedDateTime))
                    {
                        if (DateTime.TryParse(
                            userEntry.EarnedDateTime,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var parsed))
                        {
                            unlockUtc = parsed;
                        }
                        else
                        {
                            unlockUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                        }
                    }
                    else
                    {
                        unlockUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc);
                    }
                }

                achievements.Add(new AchievementDetail
                {
                    ApiName = PsnTrophyMatchHelper.GetTrophyKey(detail),
                    DisplayName = detail.TrophyName,
                    Description = detail.TrophyDetail,
                    UnlockedIconPath = detail.TrophyIconUrl,
                    CategoryType = MapTrophyGroupToCategoryType(detail.TrophyGroupId),
                    Category = null,
                    Hidden = detail.Hidden,
                    Unlocked = unlocked,
                    UnlockTimeUtc = unlockUtc,
                    GlobalPercentUnlocked = userEntry?.TrophyEarnedRate,
                    TrophyType = detail.TrophyType,
                    IsCapstone = string.Equals(detail.TrophyType, "platinum", StringComparison.OrdinalIgnoreCase)
                });
            }

            if (fallbackMatchCount > 0)
            {
                _logger?.Debug(
                    $"[PSNAch] User trophy fallback-by-id matched {fallbackMatchCount} trophies for '{game?.Name}' (non-default groups: {fallbackNonDefaultGroupCount}).");
            }

            if (fallbackNonDefaultGroupCount > 0)
            {
                _logger?.Debug(
                    $"[PSNAch] User trophy fallback-by-id used for non-default groups in '{game?.Name}' ({fallbackNonDefaultGroupCount} trophies).");
            }

            return new GameAchievementData
            {
                ProviderName = providerName,
                LibrarySourceName = game?.Source?.Name,
                GameName = game?.Name,
                PlayniteGameId = game?.Id,
                HasAchievements = achievements.Count > 0,
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow
            };
        }

        private void SetAuthorizationHeader(HttpClient http, string token)
        {
            if (http == null)
            {
                return;
            }

            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        private async Task<string> GetStringWithAuthRetryAsync(
            HttpClient http,
            string url,
            string operationLabel,
            CancellationToken cancel)
        {
            try
            {
                return await GetStringCoreAsync(http, url, cancel).ConfigureAwait(false);
            }
            catch (PsnUnauthorizedHttpException ex)
            {
                _logger?.Debug($"[PSNAch] Unauthorized ({(int)ex.StatusCode}) during {operationLabel}; forcing PSN token refresh and retry.");

                _sessionManager.InvalidateAccessToken();
                string refreshedToken;
                try
                {
                    refreshedToken = await _sessionManager.GetAccessTokenAsync(cancel, forceRefresh: true).ConfigureAwait(false);
                }
                catch (PsnAuthRequiredException)
                {
                    _logger?.Warn($"[PSNAch] Token refresh failed while recovering unauthorized response for {operationLabel}.");
                    throw;
                }

                SetAuthorizationHeader(http, refreshedToken);

                try
                {
                    var retryJson = await GetStringCoreAsync(http, url, cancel).ConfigureAwait(false);
                    _logger?.Debug($"[PSNAch] Unauthorized retry succeeded for {operationLabel}.");
                    return retryJson;
                }
                catch (PsnUnauthorizedHttpException retryEx)
                {
                    _logger?.Warn($"[PSNAch] Unauthorized retry failed ({(int)retryEx.StatusCode}) for {operationLabel}.");
                    throw new PsnAuthRequiredException(
                        $"PlayStation authentication required after unauthorized retry failure ({(int)retryEx.StatusCode}).");
                }
                catch (Exception retryEx)
                {
                    _logger?.Debug(retryEx, $"[PSNAch] Retry failed for {operationLabel}.");
                    throw;
                }
            }
        }

        private static async Task<string> GetStringCoreAsync(HttpClient http, string url, CancellationToken cancel)
        {
            using (var response = await http.GetAsync(url, cancel).ConfigureAwait(false))
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized ||
                    response.StatusCode == HttpStatusCode.Forbidden)
                {
                    throw new PsnUnauthorizedHttpException(response.StatusCode, url);
                }

                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private sealed class PsnUnauthorizedHttpException : Exception
        {
            public HttpStatusCode StatusCode { get; }

            public string Url { get; }

            public PsnUnauthorizedHttpException(HttpStatusCode statusCode, string url)
                : base($"PSN request unauthorized: {(int)statusCode} ({statusCode})")
            {
                StatusCode = statusCode;
                Url = url;
            }
        }

        private static string GetProviderName()
        {
            var value = ResourceProvider.GetString("LOCPlayAch_Provider_PlayStation");
            return string.IsNullOrWhiteSpace(value) ? "PlayStation" : value;
        }

        private static string MapTrophyGroupToCategoryType(string trophyGroupId)
        {
            var normalized = (trophyGroupId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, "default", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "000", StringComparison.OrdinalIgnoreCase))
            {
                return "Base";
            }

            return "DLC";
        }

        private static string NormalizeGameId(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var parts = raw.Split('#');
            var normalized = parts.Length > 0 ? parts[parts.Length - 1] : raw;
            return (normalized ?? string.Empty).Trim();
        }

        /// <summary>
        /// Normalizes a game name for comparison by removing common variations.
        /// </summary>
        private static string NormalizeGameName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            // Remove common suffixes and special characters, convert to lowercase
            var normalized = name.ToLowerInvariant()
                .Replace(":", "")
                .Replace("-", "")
                .Replace("_", " ")
                .Replace("®", "")
                .Replace("™", "")
                .Replace("©", "")
                .Trim();

            // Remove multiple spaces
            while (normalized.Contains("  "))
            {
                normalized = normalized.Replace("  ", " ");
            }

            return normalized;
        }

        /// <summary>
        /// Maps the global language setting to a PSN-compatible Accept-Language header value.
        /// PSN uses standard HTTP Accept-Language format (e.g., "en-US", "fr-FR").
        /// </summary>
        private static string MapGlobalLanguageToPsnLocale(string globalLanguage)
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

            return "en-US";
        }

        private async Task<string> ResolveNpCommunicationIdAsync(HttpClient http, Game game, CancellationToken cancel)
        {
            var raw = game?.GameId?.Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                _logger?.Warn($"[PSNAch] GameId is empty for '{game?.Name}'");
                return null;
            }

            var normalized = NormalizeGameId(raw);
            if (normalized.IndexOf("NPWR", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return normalized;
            }

            try
            {
                var url = string.Format(UrlTitlesWithIdsMobile, Uri.EscapeDataString(normalized));
                var json = await GetStringWithAuthRetryAsync(
                    http,
                    url,
                    $"npCommunicationId lookup for '{game?.Name}'",
                    cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                var titles = JsonConvert.DeserializeObject<PsnTrophyTitleLookup>(json);
                var comm = titles?.Titles?.FirstOrDefault()?.TrophyTitles?.FirstOrDefault()?.NpCommunicationId;
                if (!string.IsNullOrWhiteSpace(comm))
                {
                    return comm;
                }
            }
            catch (PsnAuthRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[PSNAch] Lookup failed for '{normalized}' ({game?.Name})");
            }

            if (!normalized.EndsWith("_00", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var withSuffix = normalized + "_00";
                    var url2 = string.Format(UrlTitlesWithIdsMobile, Uri.EscapeDataString(withSuffix));
                    var json2 = await GetStringWithAuthRetryAsync(
                        http,
                        url2,
                        $"npCommunicationId lookup for '{game?.Name}' with _00 suffix",
                        cancel).ConfigureAwait(false);
                    cancel.ThrowIfCancellationRequested();

                    var titles2 = JsonConvert.DeserializeObject<PsnTrophyTitleLookup>(json2);
                    var comm2 = titles2?.Titles?.FirstOrDefault()?.TrophyTitles?.FirstOrDefault()?.NpCommunicationId;
                    if (!string.IsNullOrWhiteSpace(comm2))
                    {
                        return comm2;
                    }
                }
                catch (PsnAuthRequiredException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, $"[PSNAch] Lookup failed for '{normalized}_00' ({game?.Name})");
                }
            }

            // Fallback: search user's trophy titles by game name
            var nameBasedResult = await ResolveByNameAsync(http, game?.Name, cancel).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(nameBasedResult))
            {
                _logger?.Info($"[PSNAch] Resolved '{game?.Name}' via name search to '{nameBasedResult}'");
                return nameBasedResult;
            }

            _logger?.Warn($"[PSNAch] Unable to resolve npCommunicationId for '{game?.Name}' (GameId='{normalized}')");
            return null;
        }

        /// <summary>
        /// Fallback method that searches the user's trophy titles by game name.
        /// This matches SuccessStory's GetNPWR_2 approach.
        /// </summary>
        private async Task<string> ResolveByNameAsync(HttpClient http, string gameName, CancellationToken cancel)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return null;
            }

            try
            {
                var json = await GetStringWithAuthRetryAsync(
                    http,
                    UrlAllTrophyTitles,
                    $"name-based npCommunicationId lookup for '{gameName}'",
                    cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();

                var response = JsonConvert.DeserializeObject<PsnAllTrophyTitlesResponse>(json);
                if (response?.TrophyTitles == null || response.TrophyTitles.Count == 0)
                {
                    return null;
                }

                var normalizedSearch = NormalizeGameName(gameName);
                var match = response.TrophyTitles.FirstOrDefault(t =>
                    NormalizeGameName(t?.TrophyTitleName) == normalizedSearch);

                return match?.NpCommunicationId;
            }
            catch (PsnAuthRequiredException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[PSNAch] Name-based lookup failed for '{gameName}'");
                return null;
            }
        }
    }
}

