using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam.Models;
using Playnite.SDK;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamApiClient
    {
        private readonly HttpClient _apiHttp;
        private readonly ILogger _logger;

        public SteamApiClient(HttpClient apiHttp, ILogger logger)
        {
            _apiHttp = apiHttp ?? throw new ArgumentNullException(nameof(apiHttp));
            _logger = logger;
        }

        public async Task<SchemaAndPercentages> GetSchemaForGameDetailedAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            language = NormalizeSteamLanguage(language);
            var result = await GetSchemaForGameDetailedInternalAsync(accessToken, appId, language, ct).ConfigureAwait(false);
            if (result == null && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Info($"Schema fetch failed for '{language}', retrying with 'english' for appId={appId}");
                result = await GetSchemaForGameDetailedInternalAsync(accessToken, appId, "english", ct).ConfigureAwait(false);
            }
            return result;
        }

        public async Task<bool?> GetGameHasAchievementsAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || appId <= 0)
            {
                return false;
            }

            try
            {
                language = NormalizeSteamLanguage(language);
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?key={Uri.EscapeDataString(accessToken)}" +
                          $"&appid={appId}" +
                          $"&language={Uri.EscapeDataString(language)}";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = JsonConvert.DeserializeObject<GetGameAchievementsRoot>(json);
                    var achievements = root?.Response?.Achievements;
                    return achievements != null && achievements.Count > 0;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetGameAchievements API availability check failed for appId={appId}");
                return null;
            }
        }

        private async Task<SchemaAndPercentages> GetSchemaForGameDetailedInternalAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || appId <= 0)
            {
                _logger?.Warn("GetSchemaForGameDetailedInternalAsync: Invalid accessToken or appId={appId}");
                return null;
            }

            try
            {
                language = NormalizeSteamLanguage(language);
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?key={Uri.EscapeDataString(accessToken)}" +
                          $"&appid={appId}" +
                          $"&language={Uri.EscapeDataString(language)}";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = JsonConvert.DeserializeObject<GetGameAchievementsRoot>(json);
                    var achievements = root?.Response?.Achievements;
                    if (achievements == null || achievements.Count == 0)
                    {
                        return null;
                    }

                    var schemaAchievements = new List<SchemaAchievement>();
                    var globalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                    foreach (var ach in achievements)
                    {
                        schemaAchievements.Add(new SchemaAchievement
                        {
                            Name = ach.InternalName,
                            DisplayName = ach.LocalizedName,
                            Description = ach.LocalizedDesc,
                            Icon = $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{ach.Icon}",
                            IconGray = $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{ach.IconGray}",
                            Hidden = ach.Hidden ? 1 : 0
                        });

                        if (!string.IsNullOrWhiteSpace(ach.InternalName) &&
                            double.TryParse(
                                ach.PlayerPercentUnlocked,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var percent))
                        {
                            globalPercentages[ach.InternalName] = percent;
                        }
                    }

                    if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
                    {
                        var localizedFallback = await GetSchemaForGameLocalizedTextsAsync(accessToken, appId, language, ct).ConfigureAwait(false);
                        MergeLocalizedSchemaTexts(schemaAchievements, localizedFallback);
                    }

                    return new SchemaAndPercentages
                    {
                        Achievements = schemaAchievements,
                        GlobalPercentages = globalPercentages
                    };
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetGameAchievements API request failed for appId={appId}");
                return null;
            }
        }

        private async Task<IReadOnlyDictionary<string, SchemaAchievement>> GetSchemaForGameLocalizedTextsAsync(string accessToken, int appId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || appId <= 0)
            {
                return null;
            }

            try
            {
                language = NormalizeSteamLanguage(language);
                var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/" +
                          $"?key={Uri.EscapeDataString(accessToken)}" +
                          $"&appid={appId}" +
                          $"&l={Uri.EscapeDataString(language)}";

                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = JObject.Parse(json);
                    var achievements = root["game"]?["availableGameStats"]?["achievements"] as JArray;
                    if (achievements == null || achievements.Count == 0)
                    {
                        return null;
                    }

                    return achievements
                        .OfType<JObject>()
                        .Select(achievement => new SchemaAchievement
                        {
                            Name = achievement["name"]?.Value<string>()?.Trim(),
                            DisplayName = achievement["displayName"]?.Value<string>()?.Trim(),
                            Description = achievement["description"]?.Value<string>()?.Trim()
                        })
                        .Where(achievement => !string.IsNullOrWhiteSpace(achievement.Name))
                        .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetSchemaForGame localized fallback request failed for appId={appId}");
                return null;
            }
        }

        private static void MergeLocalizedSchemaTexts(IReadOnlyList<SchemaAchievement> targetAchievements, IReadOnlyDictionary<string, SchemaAchievement> localizedFallback)
        {
            if (targetAchievements == null || localizedFallback == null || targetAchievements.Count == 0 || localizedFallback.Count == 0)
            {
                return;
            }

            foreach (var achievement in targetAchievements)
            {
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.Name) ||
                    !localizedFallback.TryGetValue(achievement.Name, out var localized) || localized == null)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(localized.DisplayName))
                {
                    achievement.DisplayName = localized.DisplayName;
                }

                if (!string.IsNullOrWhiteSpace(localized.Description))
                {
                    achievement.Description = localized.Description;
                }
            }
        }

        internal static string NormalizeSteamLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "english";
            }

            var normalized = language.Trim().ToLowerInvariant();
            return normalized switch
            {
                "english" or "en" or "en-us" or "en-gb" => "english",
                "german" or "de" or "de-de" => "german",
                "french" or "fr" or "fr-fr" => "french",
                "spanish" or "es" or "es-es" => "spanish",
                "latam" or "spanish-latin" or "es-419" => "latam",
                "italian" or "it" or "it-it" => "italian",
                "portuguese" or "pt" or "pt-pt" => "portuguese",
                "brazilian" or "pt-br" or "portuguese-brazil" => "brazilian",
                "russian" or "ru" or "ru-ru" => "russian",
                "polish" or "pl" or "pl-pl" => "polish",
                "dutch" or "nl" or "nl-nl" => "dutch",
                "swedish" or "sv" or "sv-se" => "swedish",
                "finnish" or "fi" or "fi-fi" => "finnish",
                "danish" or "da" or "da-dk" => "danish",
                "norwegian" or "no" or "nb" or "nb-no" => "norwegian",
                "hungarian" or "hu" or "hu-hu" => "hungarian",
                "czech" or "cs" or "cs-cz" => "czech",
                "romanian" or "ro" or "ro-ro" => "romanian",
                "turkish" or "tr" or "tr-tr" => "turkish",
                "greek" or "el" or "el-gr" => "greek",
                "bulgarian" or "bg" or "bg-bg" => "bulgarian",
                "ukrainian" or "uk" or "uk-ua" => "ukrainian",
                "thai" or "th" or "th-th" => "thai",
                "vietnamese" or "vi" or "vi-vn" => "vietnamese",
                "japanese" or "ja" or "ja-jp" => "japanese",
                "koreana" or "korean" or "ko" or "ko-kr" => "koreana",
                "schinese" or "zh-cn" or "zh-hans" => "schinese",
                "tchinese" or "zh-tw" or "zh-hant" => "tchinese",
                "arabic" or "ar" or "ar-sa" => "arabic",
                _ => normalized
            };
        }
    }
}



