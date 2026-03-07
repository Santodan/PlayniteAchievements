using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// API client for Exophase game search.
    /// Uses public API for search and HTML parsing for achievement pages.
    /// </summary>
    public sealed class ExophaseApiClient
    {
        private const string SearchUrl = "https://api.exophase.com/public/archive/games";
        private const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;

        public ExophaseApiClient(HttpClient httpClient, ILogger logger)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
        }

        /// <summary>
        /// Searches for games on Exophase.
        /// </summary>
        public async Task<List<ExophaseGame>> SearchGamesAsync(string query, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ExophaseGame>();
            }

            try
            {
                var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&sort=added";

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Error($"Exophase search failed with status {(int)response.StatusCode}");
                            return new List<ExophaseGame>();
                        }

                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            return new List<ExophaseGame>();
                        }

                        var result = Serialization.FromJson<ExophaseSearchResult>(json);
                        if (result?.Games?.List == null || result.Games.List.Count == 0)
                        {
                            return new List<ExophaseGame>();
                        }

                        // Filter to only games with achievements (endpoint_awards URL)
                        return result.Games.List
                            .Where(g => g != null && !string.IsNullOrWhiteSpace(g.EndpointAwards))
                            .ToList();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Exophase search failed");
                return new List<ExophaseGame>();
            }
        }

        /// <summary>
        /// Fetches and parses achievement page HTML to extract achievements.
        /// </summary>
        /// <param name="achievementUrl">The achievement page URL (endpoint_awards value).</param>
        /// <param name="acceptLanguage">The Accept-Language header value for localization.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of AchievementDetail objects, or null if error.</returns>
        public async Task<List<AchievementDetail>> FetchAchievementsAsync(
            string achievementUrl,
            string acceptLanguage,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(achievementUrl))
            {
                return null;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, achievementUrl))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                    if (!string.IsNullOrWhiteSpace(acceptLanguage))
                    {
                        request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
                    }

                    using (var response = await _httpClient.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Debug($"Exophase achievement page returned {(int)response.StatusCode} for URL: {achievementUrl}");
                            return null;
                        }

                        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(html))
                        {
                            return null;
                        }

                        return ParseAchievementsHtml(html);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to fetch Exophase achievements from {achievementUrl}");
                return null;
            }
        }

        /// <summary>
        /// Parses achievement HTML to extract achievement details.
        /// </summary>
        private List<AchievementDetail> ParseAchievementsHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            try
            {
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                // XPath: //ul[contains(@class,'achievement') or contains(@class,'trophy') or contains(@class,'challenge')]/li
                var achievementNodes = doc.DocumentNode.SelectNodes(
                    "//ul[contains(@class,'achievement') or contains(@class,'trophy') or contains(@class,'challenge')]/li");

                if (achievementNodes == null || achievementNodes.Count == 0)
                {
                    _logger?.Debug("[Exophase] No achievement list found in HTML.");
                    return null;
                }

                var achievements = new List<AchievementDetail>(achievementNodes.Count);

                foreach (var node in achievementNodes)
                {
                    try
                    {
                        var achievement = ParseAchievementNode(node);
                        if (achievement != null)
                        {
                            achievements.Add(achievement);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, "[Exophase] Failed to parse achievement node.");
                    }
                }

                return achievements.Count > 0 ? achievements : null;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "[Exophase] Failed to parse achievements HTML.");
                return null;
            }
        }

        /// <summary>
        /// Parses a single achievement li node.
        /// </summary>
        private AchievementDetail ParseAchievementNode(HtmlNode node)
        {
            // Extract data-average for GlobalPercentUnlocked
            double? globalPercent = null;
            var dataAverage = node.GetAttributeValue("data-average", "");
            if (!string.IsNullOrWhiteSpace(dataAverage) &&
                double.TryParse(dataAverage, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var percent))
            {
                globalPercent = percent;
            }

            // Extract icon URL from img/@src
            var imgNode = node.SelectSingleNode(".//img");
            var iconUrl = imgNode?.GetAttributeValue("src", "") ?? "";

            // Extract display name from a text or heading
            var nameNode = node.SelectSingleNode(".//a") ?? node.SelectSingleNode(".//h3") ?? node.SelectSingleNode(".//strong");
            var displayName = WebUtility.HtmlDecode(nameNode?.InnerText?.Trim() ?? "");

            // Extract description from div.award-description/p or similar
            var descNode = node.SelectSingleNode(".//div[contains(@class,'award-description')]/p") ??
                           node.SelectSingleNode(".//div[contains(@class,'description')]") ??
                           node.SelectSingleNode(".//p");
            var description = WebUtility.HtmlDecode(descNode?.InnerText?.Trim() ?? "");

            // Check for hidden/secret class
            var isHidden = node.GetAttributeValue("class", "").Contains("secret");

            // Generate a stable API name from the display name
            var apiName = GenerateApiName(displayName);

            if (string.IsNullOrWhiteSpace(displayName))
            {
                return null;
            }

            return new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = displayName,
                Description = description,
                LockedIconPath = iconUrl,
                UnlockedIconPath = iconUrl,
                Hidden = isHidden,
                GlobalPercentUnlocked = globalPercent,
                Unlocked = false,
                UnlockTimeUtc = null
            };
        }

        /// <summary>
        /// Generates a stable API name from the display name for tracking purposes.
        /// </summary>
        private static string GenerateApiName(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return $"exophase_{Guid.NewGuid():N}";
            }

            // Create a stable identifier from the display name
            var normalized = displayName.ToLowerInvariant();
            var safeChars = new char[normalized.Length];
            var pos = 0;

            foreach (var c in normalized)
            {
                if (char.IsLetterOrDigit(c))
                {
                    safeChars[pos++] = c;
                }
                else if (char.IsWhiteSpace(c) || c == '_' || c == '-')
                {
                    safeChars[pos++] = '_';
                }
            }

            var result = new string(safeChars, 0, pos).Trim('_');
            return string.IsNullOrWhiteSpace(result) ? $"exophase_{Guid.NewGuid():N}" : $"exophase_{result}";
        }

        /// <summary>
        /// Maps GlobalLanguage setting to Accept-Language header value.
        /// </summary>
        public static string MapLanguageToAcceptLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "en-US,en;q=0.9";
            }

            var lower = language.ToLowerInvariant().Trim();
            return lower switch
            {
                "english" => "en-US,en;q=0.9",
                "french" or "français" or "fr" => "fr-FR,fr;q=0.9",
                "german" or "deutsch" or "de" => "de-DE,de;q=0.9",
                "spanish" or "español" or "es" => "es-ES,es;q=0.9",
                "italian" or "italiano" or "it" => "it-IT,it;q=0.9",
                "portuguese" or "pt" => "pt-PT,pt;q=0.9",
                "brazilian" or "pt-br" or "brazilian portuguese" => "pt-BR,pt-BR;q=0.9",
                "russian" or "русский" or "ru" => "ru-RU,ru;q=0.9",
                "polish" or "polski" or "pl" => "pl-PL,pl;q=0.9",
                "dutch" or "nederlands" or "nl" => "nl-NL,nl;q=0.9",
                "swedish" or "svenska" or "sv" => "sv-SE,sv;q=0.9",
                "finnish" or "suomi" or "fi" => "fi-FI,fi;q=0.9",
                "danish" or "dansk" or "da" => "da-DK,da;q=0.9",
                "norwegian" or "norsk" or "no" => "nb-NO,nb;q=0.9",
                "japanese" or "日本語" or "ja" => "ja-JP,ja;q=0.9",
                "korean" or "한국어" or "ko" => "ko-KR,ko;q=0.9",
                "schinese" or "simplified chinese" or "简体中文" or "zh-cn" => "zh-CN,zh;q=0.9",
                "tchinese" or "traditional chinese" or "繁體中文" or "zh-tw" => "zh-TW,zh;q=0.9",
                "arabic" or "العربية" or "ar" => "ar-SA,ar;q=0.9",
                "czech" or "čeština" or "cs" => "cs-CZ,cs;q=0.9",
                "hungarian" or "magyar" or "hu" => "hu-HU,hu;q=0.9",
                "turkish" or "türkçe" or "tr" => "tr-TR,tr;q=0.9",
                _ => "en-US,en;q=0.9"
            };
        }
    }

    #region API Response Models

    [DataContract]
    public sealed class ExophaseSearchResult
    {
        [DataMember(Name = "success")]
        public bool Success { get; set; }

        [DataMember(Name = "games")]
        public ExophaseGames Games { get; set; }
    }

    [DataContract]
    public sealed class ExophaseGames
    {
        [DataMember(Name = "list")]
        public List<ExophaseGame> List { get; set; }

        [DataMember(Name = "paging")]
        public ExophasePaging Paging { get; set; }
    }

    [DataContract]
    public sealed class ExophaseGame
    {
        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "platforms")]
        public List<ExophasePlatform> Platforms { get; set; }

        [DataMember(Name = "endpoint_awards")]
        public string EndpointAwards { get; set; }

        [DataMember(Name = "images")]
        public ExophaseImages Images { get; set; }
    }

    [DataContract]
    public sealed class ExophasePlatform
    {
        [DataMember(Name = "name")]
        public string Name { get; set; }

        [DataMember(Name = "slug")]
        public string Slug { get; set; }
    }

    [DataContract]
    public sealed class ExophaseImages
    {
        [DataMember(Name = "cover")]
        public string Cover { get; set; }

        [DataMember(Name = "banner")]
        public string Banner { get; set; }
    }

    [DataContract]
    public sealed class ExophasePaging
    {
        [DataMember(Name = "total")]
        public int Total { get; set; }

        [DataMember(Name = "page")]
        public int Page { get; set; }

        [DataMember(Name = "limit")]
        public int Limit { get; set; }
    }

    #endregion
}
