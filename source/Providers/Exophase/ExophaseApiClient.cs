using Playnite.SDK;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;

namespace PlayniteAchievements.Providers.Exophase
{
    /// <summary>
    /// API client for Exophase game search and achievement fetching.
    /// Uses WebView for all requests to bypass Cloudflare protection.
    /// </summary>
    public sealed class ExophaseApiClient
    {
        private const string SearchUrl = "https://api.exophase.com/public/archive/games";
        private const string AchievementPageBaseUrl = "https://www.exophase.com/game/{0}/achievements/";
        private const int AchievementDomReadyPollDelayMs = 250;
        private const int AchievementDomReadyPollAttempts = 8;

        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;

        public ExophaseApiClient(IPlayniteAPI playniteApi, ILogger logger)
        {
            _playniteApi = playniteApi ?? throw new ArgumentNullException(nameof(playniteApi));
            _logger = logger;
        }

        /// <summary>
        /// Extracts the game slug from an Exophase achievement/trophy URL.
        /// Examples:
        /// - https://www.exophase.com/game/shogun-showdown-steam/achievements/ -> shogun-showdown-steam
        /// - https://www.exophase.com/game/shogun-showdown-psn/trophies/ -> shogun-showdown-psn
        /// </summary>
        public static string ExtractSlugFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            // Match pattern: /game/{slug}/ followed by achievements, trophies, or end of URL
            var match = System.Text.RegularExpressions.Regex.Match(url, @"/game/([^/]+)(?:/(?:achievements|trophies))?/?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success && match.Groups.Count > 1)
            {
                return match.Groups[1].Value;
            }

            return null;
        }

        /// <summary>
        /// Builds the achievement page URL from a game slug or full URL.
        /// Supports both new format (slug only) and legacy format (full URL) for backward compatibility.
        /// PlayStation games use /trophies/, others use /achievements/
        /// </summary>
        public static string BuildUrlFromSlug(string slugOrUrl)
        {
            if (string.IsNullOrWhiteSpace(slugOrUrl))
            {
                return null;
            }

            // If it's already a full URL, extract the slug and rebuild (normalizes URL)
            if (slugOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                var extractedSlug = ExtractSlugFromUrl(slugOrUrl);
                if (!string.IsNullOrWhiteSpace(extractedSlug))
                {
                    // Check if original URL was for trophies (PlayStation) to preserve the endpoint type
                    var wasTrophies = slugOrUrl.IndexOf("/trophies", StringComparison.OrdinalIgnoreCase) >= 0;
                    return $"https://www.exophase.com/game/{extractedSlug}/{(wasTrophies ? "trophies" : "achievements")}/";
                }
                // If we can't extract, return as-is (legacy fallback)
                return slugOrUrl;
            }

            // Detect if this is a PlayStation game from the slug
            var isPlayStation = slugOrUrl.IndexOf("-psn", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               slugOrUrl.IndexOf("-ps4", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               slugOrUrl.IndexOf("-ps5", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               slugOrUrl.IndexOf("-vita", StringComparison.OrdinalIgnoreCase) >= 0 ||
                               slugOrUrl.IndexOf("-ps3", StringComparison.OrdinalIgnoreCase) >= 0;

            return $"https://www.exophase.com/game/{slugOrUrl}/{(isPlayStation ? "trophies" : "achievements")}/";
        }

        /// <summary>
        /// Searches for games on Exophase using WebView to bypass Cloudflare.
        /// </summary>
        public async Task<List<ExophaseGame>> SearchGamesAsync(string query, CancellationToken ct)
        {
            return await SearchGamesAsync(query, null, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Searches for games on Exophase with optional platform filter.
        /// </summary>
        /// <param name="query">Game name to search for.</param>
        /// <param name="platformSlug">Optional platform slug to filter by (e.g., "steam", "psn", "xbox").</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<List<ExophaseGame>> SearchGamesAsync(string query, string platformSlug, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ExophaseGame>();
            }

            try
            {
                var url = $"{SearchUrl}?q={Uri.EscapeDataString(query)}&sort=added";
                if (!string.IsNullOrWhiteSpace(platformSlug))
                {
                    url += $"&platform={Uri.EscapeDataString(platformSlug)}";
                }

                // Use WebView to bypass Cloudflare protection
                var json = await FetchJsonViaWebViewAsync(url, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ExophaseGame>();
                }

                if (Regex.IsMatch(json, "\"games\"\\s*:\\s*false\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    _logger?.Debug("[Exophase] Search returned games:false; treating as no matches");
                    return new List<ExophaseGame>();
                }

                var result = Serialization.FromJson<ExophaseSearchResult>(json);
                if (result?.Games?.List == null || result.Games.List.Count == 0)
                {
                    _logger?.Debug($"[Exophase] Parsed result: Games=null, or empty list");
                    return new List<ExophaseGame>();
                }

                _logger?.Debug($"[Exophase] Parsed {result.Games.List.Count} games from API");

                // Filter to only games with achievements (endpoint_awards URL)
                var filtered = result.Games.List
                    .Where(g => g != null && !string.IsNullOrWhiteSpace(g.EndpointAwards))
                    .ToList();

                _logger?.Debug($"[Exophase] After filtering: {filtered.Count} games with achievements");
                return filtered;
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
        /// Fetches JSON from a URL using offscreen WebView to bypass Cloudflare.
        /// </summary>
        private async Task<string> FetchJsonViaWebViewAsync(string url, CancellationToken ct)
        {
            var dispatchOperation = _playniteApi.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                using (var view = _playniteApi.WebViews.CreateOffscreenView())
                {
                    try
                    {
                        // Navigate and wait for page load (follows ExophaseSessionManager pattern)
                        await view.NavigateAndWaitAsync(url, timeoutMs: 15000);

                        // Get page text (JSON API response displayed as plain text)
                        var pageText = await view.GetPageTextAsync();

                        if (string.IsNullOrWhiteSpace(pageText))
                        {
                            _logger?.Debug("[Exophase] WebView returned empty page text");
                            return null;
                        }

                        // Check if we got a Cloudflare challenge page
                        if (pageText.Contains("Just a moment") ||
                            pageText.Contains("Cloudflare") ||
                            pageText.Contains("Verifying you are human"))
                        {
                            _logger?.Warn("[Exophase] Cloudflare challenge detected, search may fail");
                            return null;
                        }

                        _logger?.Debug($"[Exophase] WebView fetched {pageText.Length} chars");
                        return pageText;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "[Exophase] Failed to fetch JSON via WebView");
                        return null;
                    }
                }
            });

            var responseTask = await dispatchOperation.Task.ConfigureAwait(false);
            return await responseTask.ConfigureAwait(false);
        }

        /// <summary>
        /// Fetches and parses achievement page HTML to extract achievements.
        /// Uses WebView to bypass Cloudflare protection.
        /// </summary>
        /// <param name="achievementUrl">The achievement page URL (endpoint_awards value).</param>
        /// <param name="acceptLanguage">The Accept-Language header value for localization (not used with WebView).</param>
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
                var html = await FetchHtmlViaWebViewAsync(achievementUrl, ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    _logger?.Debug($"[Exophase] No HTML fetched for achievement URL: {achievementUrl}");
                    return null;
                }

                return ParseAchievementsHtml(html);
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
        /// Fetches an Exophase achievement page using an authenticated HTTP client so manual
        /// linking can send Accept-Language while reusing existing CEF session cookies.
        /// </summary>
        public async Task<List<AchievementDetail>> FetchAchievementsViaHttpAsync(
            string achievementUrl,
            string acceptLanguage,
            HttpClient httpClient,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(achievementUrl))
            {
                return null;
            }

            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, achievementUrl))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
                    if (!string.IsNullOrWhiteSpace(acceptLanguage))
                    {
                        request.Headers.TryAddWithoutValidation("Accept-Language", acceptLanguage);
                    }

                    using (var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger?.Debug($"[Exophase] HTTP fetch returned {(int)response.StatusCode} for {achievementUrl}");
                            return null;
                        }

                        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(html))
                        {
                            _logger?.Debug($"[Exophase] HTTP fetch returned empty HTML for {achievementUrl}");
                            return null;
                        }

                        if (html.Contains("Just a moment") ||
                            html.Contains("Cloudflare") ||
                            html.Contains("Verifying you are human"))
                        {
                            _logger?.Warn("[Exophase] Cloudflare challenge detected on authenticated HTTP achievement fetch");
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
                _logger?.Error(ex, $"Failed to fetch Exophase achievements via HTTP from {achievementUrl}");
                return null;
            }
        }

        /// <summary>
        /// Fetches HTML from a URL using offscreen WebView to bypass Cloudflare.
        /// </summary>
        private async Task<string> FetchHtmlViaWebViewAsync(string url, CancellationToken ct)
        {
            var dispatchOperation = _playniteApi.MainView.UIDispatcher.InvokeAsync(async () =>
            {
                using (var view = _playniteApi.WebViews.CreateOffscreenView())
                {
                    try
                    {
                        await view.NavigateAndWaitAsync(url, timeoutMs: 20000);

                        var html = await view.GetPageSourceAsync();

                        // Exophase can populate rows after initial load; briefly poll for achievement markers.
                        if (!ContainsAchievementMarkup(html))
                        {
                            for (var attempt = 0; attempt < AchievementDomReadyPollAttempts; attempt++)
                            {
                                ct.ThrowIfCancellationRequested();
                                await Task.Delay(AchievementDomReadyPollDelayMs, ct).ConfigureAwait(false);

                                html = await view.GetPageSourceAsync();
                                if (ContainsAchievementMarkup(html))
                                {
                                    break;
                                }
                            }
                        }

                        if (string.IsNullOrWhiteSpace(html))
                        {
                            _logger?.Debug("[Exophase] WebView returned empty HTML");
                            return null;
                        }

                        // Check if we got a Cloudflare challenge page
                        if (html.Contains("Just a moment") ||
                            html.Contains("Cloudflare") ||
                            html.Contains("Verifying you are human"))
                        {
                            _logger?.Warn("[Exophase] Cloudflare challenge detected on achievement page");
                            return null;
                        }

                        _logger?.Debug($"[Exophase] WebView fetched HTML: {html.Length} chars");
                        return html;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, "[Exophase] Failed to fetch HTML via WebView");
                        return null;
                    }
                }
            });

            var responseTask = await dispatchOperation.Task.ConfigureAwait(false);
            return await responseTask.ConfigureAwait(false);
        }

        private static bool ContainsAchievementMarkup(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            return html.IndexOf("award-title", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("award-average", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("award-earned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("data-earned", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   html.IndexOf("data-average", StringComparison.OrdinalIgnoreCase) >= 0;
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

            // Fallback for JS-rendered markup where percentage is text like "95.49% (37.00)".
            if (!globalPercent.HasValue)
            {
                var averageNode = node.SelectSingleNode(".//div[contains(@class,'award-average')]//span") ??
                                 node.SelectSingleNode(".//div[contains(@class,'award-average')]");
                var averageText = WebUtility.HtmlDecode(averageNode?.InnerText?.Trim() ?? "");
                if (!string.IsNullOrWhiteSpace(averageText))
                {
                    var percentMatch = System.Text.RegularExpressions.Regex.Match(
                        averageText,
                        @"([0-9]+(?:\.[0-9]+)?)\s*%",
                        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                    if (percentMatch.Success &&
                        double.TryParse(percentMatch.Groups[1].Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var parsedPercent))
                    {
                        globalPercent = parsedPercent;
                    }
                }
            }

            // Extract data-earned for unlock status (0 = locked, 1 = unlocked)
            var dataEarned = node.GetAttributeValue("data-earned", "0");
            var isUnlocked = dataEarned == "1";

            // JS-rendered pages may provide unlock state in award-earned text instead of data-earned.
            var earnedNode = node.SelectSingleNode(".//div[contains(@class,'award-earned')]");
            var earnedText = WebUtility.HtmlDecode(earnedNode?.InnerText?.Trim() ?? "");

            DateTime? unlockTimeUtc = null;
            if (!string.IsNullOrWhiteSpace(earnedText))
            {
                unlockTimeUtc = ParseExophaseTimestamp(earnedText);

                if (!isUnlocked)
                {
                    isUnlocked = unlockTimeUtc.HasValue ||
                                 earnedText.IndexOf("earned offline", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 earnedText.IndexOf("earned online", StringComparison.OrdinalIgnoreCase) >= 0;
                }
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

            // Extract platform-specific points/type from award-points section.
            var awardPointsNode = node.SelectSingleNode(".//div[contains(@class,'award-points')]");
            var parsedPoints = ParseAwardPointsValue(awardPointsNode);
            var trophyType = ParseTrophyType(awardPointsNode);
            var isCapstone = string.Equals(trophyType, "platinum", StringComparison.OrdinalIgnoreCase);

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
                Points = parsedPoints,
                TrophyType = trophyType,
                IsCapstone = isCapstone,
                Hidden = isHidden,
                GlobalPercentUnlocked = NormalizePercent(globalPercent),
                Rarity = RarityTier.Common,
                Unlocked = isUnlocked,
                UnlockTimeUtc = unlockTimeUtc
            };
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

        private static int? ParseAwardPointsValue(HtmlNode awardPointsNode)
        {
            if (awardPointsNode == null)
            {
                return null;
            }

            var valueNode = awardPointsNode.SelectSingleNode(".//span") ?? awardPointsNode;
            var text = WebUtility.HtmlDecode(valueNode?.InnerText?.Trim() ?? string.Empty);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var match = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)");
            if (!match.Success)
            {
                return null;
            }

            return int.TryParse(match.Groups[1].Value, out var points)
                ? (int?)points
                : null;
        }

        private static string ParseTrophyType(HtmlNode awardPointsNode)
        {
            if (awardPointsNode == null)
            {
                return null;
            }

            var iconNode = awardPointsNode.SelectSingleNode(".//i");
            var classNames = iconNode?.GetAttributeValue("class", string.Empty) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(classNames))
            {
                return null;
            }

            if (classNames.IndexOf("exo-icon-trophy-platinum", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "platinum";
            }

            if (classNames.IndexOf("exo-icon-trophy-gold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "gold";
            }

            if (classNames.IndexOf("exo-icon-trophy-silver", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "silver";
            }

            if (classNames.IndexOf("exo-icon-trophy-bronze", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "bronze";
            }

            return null;
        }

        /// <summary>
        /// Parses an Exophase timestamp string into UTC DateTime.
        /// Examples: "Jan 15, 2024, 8:30 PM", "January 15, 2024, 8:30 PM"
        /// </summary>
        private DateTime? ParseExophaseTimestamp(string timestamp)
        {
            if (string.IsNullOrWhiteSpace(timestamp))
            {
                return null;
            }

            // Exophase often appends timezone label in parentheses (e.g., "(UTC+0)").
            var normalizedTimestamp = timestamp.Trim();
            var timezoneSuffixIndex = normalizedTimestamp.IndexOf(" (UTC", StringComparison.OrdinalIgnoreCase);
            if (timezoneSuffixIndex > 0)
            {
                normalizedTimestamp = normalizedTimestamp.Substring(0, timezoneSuffixIndex).Trim();
            }

            // Try common Exophase formats
            var formats = new[]
            {
                "MMM d, yyyy, h:mm tt",      // Jan 15, 2024, 8:30 PM
                "MMM d, yyyy, h:mm:ss tt",   // Jan 15, 2024, 8:30:00 PM
                "MMMM d, yyyy, h:mm tt",     // January 15, 2024, 8:30 PM
                "MMMM d, yyyy, h:mm:ss tt",  // January 15, 2024, 8:30:00 PM
                "MMM d, yyyy",               // Jan 15, 2024
                "MMMM d, yyyy",              // January 15, 2024
                "d MMM yyyy, h:mm tt",       // 15 Jan 2024, 8:30 PM
                "d MMMM yyyy, h:mm tt",      // 15 January 2024, 8:30 PM
            };

            foreach (var format in formats)
            {
                if (DateTime.TryParseExact(normalizedTimestamp, format,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
                    out var parsed))
                {
                    // Convert to UTC
                    return parsed.ToUniversalTime();
                }
            }

            // Fallback to generic parsing
            if (DateTime.TryParse(normalizedTimestamp, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces | System.Globalization.DateTimeStyles.AssumeLocal,
                out var fallbackParsed))
            {
                return fallbackParsed.ToUniversalTime();
            }

            _logger?.Debug($"[Exophase] Failed to parse timestamp: {timestamp}");
            return null;
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
        [DataMember(Name = "o")]
        public string O { get; set; }

        [DataMember(Name = "l")]
        public string L { get; set; }

        [DataMember(Name = "m")]
        public string M { get; set; }
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
