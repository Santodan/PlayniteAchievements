using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Steam.Models;

namespace PlayniteAchievements.Providers.Manual
{
    /// <summary>
    /// Manual source implementation for Steam.
    /// Uses Steam Store API for search and Steam Web API for achievement schema.
    /// </summary>
    internal sealed class SteamManualSource : IManualSource
    {
        private const string StoreSearchUrl = "https://store.steampowered.com/api/storesearch/";
        private const string AppDetailsUrl = "https://store.steampowered.com/api/appdetails";
        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private static readonly string[] ExcludedNamePatterns = new[]
        {
            " Demo", " Soundtrack", " OST", " Artbook", " Art Book",
            " DLC", " Pack", " Bundle", " Season Pass", " Expansion",
            " Collector's Edition", " Pre-Order", " Preorder"
        };

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly Func<string> _getApiKey;

        public string SourceKey => "Steam";
        public string SourceName => ResourceProvider.GetString("LOCPlayAch_Provider_Steam");
        public bool IsAuthenticated => !string.IsNullOrWhiteSpace(_getApiKey?.Invoke());
        public ISessionManager AuthSession => null;

        public SteamManualSource(HttpClient httpClient, ILogger logger, Func<string> getApiKey)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
            _getApiKey = getApiKey ?? throw new ArgumentNullException(nameof(getApiKey));
        }

        public async Task<List<ManualGameSearchResult>> SearchGamesAsync(string query, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ManualGameSearchResult>();
            }

            try
            {
                await ManualSourceAuthentication.EnsureAuthenticatedAsync(this, ct).ConfigureAwait(false);

                // Map language to Steam Store language code
                var steamLanguage = MapLanguageToSteam(language);
                var cc = "US";

                var url = $"{StoreSearchUrl}?term={Uri.EscapeDataString(query)}&l={Uri.EscapeDataString(steamLanguage)}&cc={cc}";

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Error($"Steam Store search failed with status {(int)response.StatusCode}");
                            return new List<ManualGameSearchResult>();
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return new List<ManualGameSearchResult>();
                        }

                        var envelope = Serialization.FromJson<StoreSearchEnvelope>(json);
                        if (envelope == null)
                        {
                            _logger?.Warn($"Steam Store search returned unparseable JSON for query: {query}");
                            return new List<ManualGameSearchResult>();
                        }
                        var items = envelope?.Items;

                        if (items == null || items.Count == 0)
                        {
                            return new List<ManualGameSearchResult>();
                        }

                        // Filter by type using AppDetails API
                        var validAppIds = await FilterGamesByTypeAsync(items.Select(i => i.AppId).ToList(), ct).ConfigureAwait(false);

                        var results = new List<ManualGameSearchResult>(items.Count);
                        foreach (var item in items)
                        {
                            if (item == null || item.AppId <= 0)
                            {
                                continue;
                            }

                            // Skip if not a game type
                            if (validAppIds != null && !validAppIds.Contains(item.AppId))
                            {
                                continue;
                            }

                            // Fallback: filter by name patterns if AppDetails failed
                            if (validAppIds == null && IsExcludedByName(item.Name))
                            {
                                continue;
                            }

                            results.Add(new ManualGameSearchResult
                            {
                                SourceGameId = item.AppId.ToString(),
                                Name = item.Name ?? string.Empty,
                                IconUrl = GetTallestImageUrl(item),
                                HasAchievements = false // Will be determined on schema fetch
                            });
                        }

                        return results;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Steam Store search failed");
                return new List<ManualGameSearchResult>();
            }
        }

        public async Task<List<AchievementDetail>> GetAchievementsAsync(string sourceGameId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(sourceGameId) || !int.TryParse(sourceGameId, out var appId) || appId <= 0)
            {
                return null;
            }

            var apiKey = _getApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger?.Warn("Steam API key not configured; cannot fetch manual achievements");
                return null;
            }

            try
            {
                await ManualSourceAuthentication.EnsureAuthenticatedAsync(this, ct).ConfigureAwait(false);

                var steamLanguage = MapLanguageToSteam(language);
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?key={Uri.EscapeDataString(apiKey)}" +
                          $"&appid={appId}" +
                          $"&language={Uri.EscapeDataString(steamLanguage)}";

                using (var response = await _httpClient.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger?.Debug($"GetGameAchievements API non-success: {(int)response.StatusCode} for appId={appId}");
                        return null;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = Serialization.FromJson<GetGameAchievementsRoot>(json);
                    var achievements = root?.Response?.Achievements;

                    if (achievements == null || achievements.Count == 0)
                    {
                        return null;
                    }

                    var result = new List<AchievementDetail>(achievements.Count);

                    foreach (var ach in achievements)
                    {
                        if (ach == null || string.IsNullOrWhiteSpace(ach.InternalName))
                        {
                            continue;
                        }

                        double? globalPercent = null;
                        if (!string.IsNullOrWhiteSpace(ach.PlayerPercentUnlocked) &&
                            double.TryParse(ach.PlayerPercentUnlocked, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var percent))
                        {
                            globalPercent = percent;
                        }

                        var detail = new AchievementDetail
                        {
                            ApiName = ach.InternalName,
                            DisplayName = ach.LocalizedName ?? ach.InternalName,
                            Description = ach.LocalizedDesc ?? string.Empty,
                            UnlockedIconPath = $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{ach.Icon}",
                            LockedIconPath = $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{ach.IconGray}",
                            Hidden = ach.Hidden,
                            Unlocked = false,
                            UnlockTimeUtc = null,
                            Rarity = GetFallbackRarity(
                                ach.Hidden,
                                null,
                                null)
                        };

                        var normalizedPercent = NormalizePercent(globalPercent);
                        detail.GlobalPercentUnlocked = normalizedPercent;
                        if (normalizedPercent.HasValue)
                        {
                            detail.Rarity = PercentRarityHelper.GetRarityTier(normalizedPercent.Value);
                        }

                        result.Add(detail);
                    }

                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to fetch Steam achievements for appId={appId}");
                return null;
            }
        }

        private static string MapLanguageToSteam(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "english";
            }

            // Map common language codes to Steam Store language names
            var lower = language.ToLowerInvariant().Trim();
            return lower switch
            {
                "english" => "english",
                "german" or "deutsch" or "de" => "german",
                "french" or "français" or "fr" => "french",
                "spanish" or "español" or "es" => "spanish",
                "italian" or "italiano" or "it" => "italian",
                "portuguese" or "pt" => "portuguese",
                "brazilian" or "pt-br" or "brazilian portuguese" => "brazilian",
                "russian" or "русский" or "ru" => "russian",
                "polish" or "polski" or "pl" => "polish",
                "dutch" or "nederlands" or "nl" => "dutch",
                "swedish" or "svenska" or "sv" => "swedish",
                "finnish" or "suomi" or "fi" => "finnish",
                "danish" or "dansk" or "da" => "danish",
                "norwegian" or "norsk" or "no" => "norwegian",
                "hungarian" or "magyar" or "hu" => "hungarian",
                "czech" or "čeština" or "cs" => "czech",
                "romanian" or "română" or "ro" => "romanian",
                "turkish" or "türkçe" or "tr" => "turkish",
                "greek" or "ελληνικά" or "el" => "greek",
                "bulgarian" or "български" or "bg" => "bulgarian",
                "ukrainian" or "українська" or "uk" => "ukrainian",
                "thai" or "ไทย" or "th" => "thai",
                "vietnamese" or "tiếng việt" or "vi" => "vietnamese",
                "japanese" or "日本語" or "ja" => "japanese",
                "koreana" or "korean" or "한국어" or "ko" => "koreana",
                "schinese" or "simplified chinese" or "简体中文" or "zh-cn" => "schinese",
                "tchinese" or "traditional chinese" or "繁體中文" or "zh-tw" => "tchinese",
                "arabic" or "العربية" or "ar" => "arabic",
                _ => "english"
            };
        }

        /// <summary>
        /// Filters app IDs to only include games (not demos, DLC, soundtracks, etc.)
        /// using the Steam AppDetails API.
        /// Steam AppDetails API only accepts a single appid per request.
        /// </summary>
        /// <returns>Set of valid game app IDs, or null if the API call failed.</returns>
        private async Task<HashSet<int>> FilterGamesByTypeAsync(List<int> appIds, CancellationToken ct)
        {
            if (appIds == null || appIds.Count == 0)
            {
                return new HashSet<int>();
            }

            try
            {
                var validIds = new HashSet<int>();

                foreach (var appId in appIds)
                {
                    ct.ThrowIfCancellationRequested();

                    var url = $"{AppDetailsUrl}?appids={appId}";

                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");

                        using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                        {
                            if (!response.IsSuccessStatusCode)
                            {
                                _logger?.Debug($"AppDetails API returned {(int)response.StatusCode} for appId={appId}");
                                continue;
                            }

                            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                            try
                            {
                                var details = Serialization.FromJson<Dictionary<string, AppDetailsEnvelope>>(json);

                                if (details?.TryGetValue(appId.ToString(), out var envelope) == true &&
                                    envelope?.Success == true &&
                                    string.Equals(envelope.Data?.Type, "game", StringComparison.OrdinalIgnoreCase))
                                {
                                    validIds.Add(appId);
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                // Some apps return unexpected formats (e.g., data as array instead of object)
                                // Log and continue to next app rather than failing the entire operation
                                _logger?.Debug($"AppDetails JSON parse error for appId={appId}: {ex.Message}");
                            }
                        }
                    }

                    // Small delay to avoid rate limiting
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }

                return validIds;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "AppDetails API failed, using fallback name filter");
                return null;
            }
        }

        /// <summary>
        /// Checks if a game name matches patterns for non-game content.
        /// Used as a fallback when AppDetails API is unavailable.
        /// </summary>
        private static bool IsExcludedByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            foreach (var pattern in ExcludedNamePatterns)
            {
                if (name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetTallestImageUrl(StoreSearchItem item)
        {
            // Prefer the tallest image (usually box art) for better visibility
            if (!string.IsNullOrWhiteSpace(item.ImgHdrUrl))
            {
                return item.ImgHdrUrl;
            }
            if (!string.IsNullOrWhiteSpace(item.ImgGridUrl))
            {
                return item.ImgGridUrl;
            }
            return item.IconUrl ?? string.Empty;
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        private static RarityTier GetFallbackRarity(bool hidden, int? progressNum, int? progressDenom)
        {
            if ((progressNum.HasValue || progressDenom.HasValue) &&
                progressDenom.HasValue &&
                progressDenom.Value > 0)
            {
                return RarityTier.Uncommon;
            }

            if (hidden)
            {
                return RarityTier.Rare;
            }

            return RarityTier.Common;
        }

        #region Steam Store Search API Models

        [DataContract]
        private sealed class StoreSearchEnvelope
        {
            [DataMember(Name = "items")]
            public List<StoreSearchItem> Items { get; set; }

            [DataMember(Name = "total")]
            public int? Total { get; set; }
        }

        [DataContract]
        private sealed class StoreSearchItem
        {
            [DataMember(Name = "id")]
            public int AppId { get; set; }

            [DataMember(Name = "name")]
            public string Name { get; set; }

            [DataMember(Name = "tiny_image")]
            public string IconUrl { get; set; }

            [DataMember(Name = "header_image")]
            public string ImgHdrUrl { get; set; }

            [DataMember(Name = "boxart")]
            public string ImgGridUrl { get; set; }
        }

        [DataContract]
        private sealed class AppDetailsEnvelope
        {
            [DataMember(Name = "success")]
            public bool Success { get; set; }

            [DataMember(Name = "data")]
            public AppDetailsData Data { get; set; }
        }

        [DataContract]
        private sealed class AppDetailsData
        {
            [DataMember(Name = "type")]
            public string Type { get; set; }
        }

        #endregion
    }
}
