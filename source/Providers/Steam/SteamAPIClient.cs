using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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
                language = string.IsNullOrWhiteSpace(language) ? "english" : language;
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?access_token={Uri.EscapeDataString(accessToken)}" +
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetGameAchievements API availability check failed for appId={appId}");
                return null;
            }
        }

        public async Task<IReadOnlyList<SteamOwnedGame>> GetOwnedGamesAsync(
            string accessToken,
            string steamId64,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(steamId64))
            {
                return null;
            }

            try
            {
                var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                          $"?access_token={Uri.EscapeDataString(accessToken)}" +
                          $"&steamid={Uri.EscapeDataString(steamId64.Trim())}" +
                          $"&include_appinfo=true" +
                          $"&include_played_free_games=true" +
                          $"&format=json";

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

                    var root = JsonConvert.DeserializeObject<GetOwnedGamesRoot>(json);
                    var response = root?.Response;
                    if (response == null)
                    {
                        return null;
                    }

                    if (response.Games != null)
                    {
                        return response.Games
                            .Where(game => game != null && game.AppId > 0)
                            .ToList();
                    }

                    return response.GameCount.GetValueOrDefault() == 0
                        ? (IReadOnlyList<SteamOwnedGame>)Array.Empty<SteamOwnedGame>()
                        : null;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"GetOwnedGames API request failed for steamId={steamId64}");
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
                language = string.IsNullOrWhiteSpace(language) ? "english" : language;
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?access_token={Uri.EscapeDataString(accessToken)}" +
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
                        var normalizedInternalName = NormalizeApiText(ach.InternalName);

                        schemaAchievements.Add(new SchemaAchievement
                        {
                            Name = normalizedInternalName,
                            DisplayName = NormalizeApiText(ach.LocalizedName),
                            Description = NormalizeApiText(ach.LocalizedDesc),
                            Icon = BuildAchievementIconUrl(appId, ach.Icon),
                            IconGray = BuildAchievementIconUrl(appId, ach.IconGray),
                            Hidden = ach.Hidden ? 1 : 0
                        });

                        if (!string.IsNullOrWhiteSpace(normalizedInternalName) &&
                            double.TryParse(
                                ach.PlayerPercentUnlocked,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out var percent))
                        {
                            globalPercentages[normalizedInternalName] = percent;
                        }
                    }

                    return new SchemaAndPercentages
                    {
                        Achievements = schemaAchievements,
                        GlobalPercentages = globalPercentages
                    };
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetGameAchievements API request failed for appId={appId}");
                return null;
            }
        }

        private static string NormalizeApiText(string value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }

        internal static string BuildAchievementIconUrl(int appId, string iconFile)
        {
            var normalizedIconFile = NormalizeApiText(iconFile);
            if (string.IsNullOrWhiteSpace(normalizedIconFile))
            {
                return string.Empty;
            }

            return $"https://shared.akamai.steamstatic.com/community_assets/images/apps/{appId}/{normalizedIconFile}";
        }
    }
}



