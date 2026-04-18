using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Steam.Models;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace PlayniteAchievements.Providers.Steam
{
    internal sealed class SteamApiClient
    {
        private const string DefaultUserAgent = 
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private const int PlayerSummariesBatchSize = 100;

        private readonly HttpClient _apiHttp;
        private readonly ILogger _logger;

        public SteamApiClient(HttpClient apiHttp, ILogger logger)
        {
            _apiHttp = apiHttp ?? throw new ArgumentNullException(nameof(apiHttp));
            _logger = logger;
        }

        public async Task<Dictionary<int, int>> GetOwnedGamesAsync(string apiKey, string steamId64, bool includePlayedFreeGames)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
                return new Dictionary<int, int>();

            var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                      $"?key={Uri.EscapeDataString(apiKey)}" +
                      $"&steamid={Uri.EscapeDataString(steamId64)}" +
                      $"&include_appinfo=0" +
                      $"&include_played_free_games={(includePlayedFreeGames ? "1" : "0")}";
            try
            {
                var json = await _apiHttp.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json)) return new Dictionary<int, int>();

                var env = Serialization.FromJson<OwnedGamesEnvelope>(json);
                var games = env?.Response?.Games;

                if (games == null) return new Dictionary<int, int>();

                var result = new Dictionary<int, int>();
                foreach (var g in games)
                {
                    if (!g.AppId.HasValue || g.AppId.Value <= 0) continue;
                    var mins = Math.Max(0, g.PlaytimeForever ?? 0);

                    if (!result.ContainsKey(g.AppId.Value) || mins > result[g.AppId.Value])
                        result[g.AppId.Value] = mins;
                }
                return result;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "OwnedGames API request failed steamId={steamId64}");
                return new Dictionary<int, int>();
            }
        }

        public async Task<bool> ValidateApiKeyAsync(string apiKey, string steamId64, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64) || !ulong.TryParse(steamId64.Trim(), out var steamId) || steamId <= 0)
            {
                return false;
            }

            try
            {
                var players = await GetPlayerSummariesAsync(apiKey, new[] { steamId }, ct).ConfigureAwait(false);
                return players.Any(player => string.Equals(player?.SteamId, steamId64.Trim(), StringComparison.Ordinal));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Steam Web API key validation failed.");
                return false;
            }
        }

        public async Task<UserUnlockedAchievements> GetPlayerAchievementsAsync(string apiKey, string steamId64, int appId, CancellationToken ct)
        {
            var result = new UserUnlockedAchievements
            {
                AppId = appId,
                LastUpdatedUtc = DateTime.UtcNow
            };

            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64) || appId <= 0)
            {
                return result;
            }

            try
            {
                var url = "https://api.steampowered.com/ISteamUserStats/GetPlayerAchievements/v0001/" +
                          $"?key={Uri.EscapeDataString(apiKey.Trim())}" +
                          $"&steamid={Uri.EscapeDataString(steamId64.Trim())}" +
                          $"&appid={appId}";

                using (var resp = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        return result;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        return result;
                    }

                    var root = JObject.Parse(json);
                    var playerStats = root["playerstats"] as JObject;
                    var success = playerStats?["success"]?.Value<bool?>();
                    if (success == false)
                    {
                        return result;
                    }

                    var achievements = playerStats?["achievements"] as JArray;
                    if (achievements == null || achievements.Count == 0)
                    {
                        return result;
                    }

                    foreach (var token in achievements.OfType<JObject>())
                    {
                        var apiName = token["apiname"]?.Value<string>()?.Trim();
                        if (string.IsNullOrWhiteSpace(apiName))
                        {
                            continue;
                        }

                        if (token["achieved"]?.Value<int?>() != 1)
                        {
                            continue;
                        }

                        result.UnlockedApiNames.Add(apiName);

                        var unlockTimeUnix = token["unlocktime"]?.Value<long?>();
                        if (unlockTimeUnix.HasValue && unlockTimeUnix.Value > 0)
                        {
                            result.UnlockTimesUtc[apiName] = DateTimeOffset.FromUnixTimeSeconds(unlockTimeUnix.Value).UtcDateTime;
                        }
                    }

                    result.LastUpdatedUtc = DateTime.UtcNow;
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"GetPlayerAchievements API request failed for appId={appId}");
                return result;
            }
        }

        /// <summary>
        /// Fetches owned games with full details including last played timestamps.
        /// Useful for quick refresh mode to sort games by recency.
        /// </summary>
        public async Task<List<OwnedGame>> GetOwnedGamesDetailedAsync(string apiKey, string steamId64, bool includePlayedFreeGames, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(steamId64))
                return new List<OwnedGame>();

            try
            {
                var url = $"https://api.steampowered.com/IPlayerService/GetOwnedGames/v1/" +
                          $"?key={Uri.EscapeDataString(apiKey)}" +
                          $"&steamid={Uri.EscapeDataString(steamId64)}" +
                          $"&include_appinfo=1" +
                          $"&include_played_free_games={(includePlayedFreeGames ? "1" : "0")}";

                using (var resp = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        _logger?.Debug("GetOwnedGamesDetailed API non-success: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                        return new List<OwnedGame>();
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(json))
                        return new List<OwnedGame>();

                    var env = Serialization.FromJson<OwnedGamesEnvelope>(json);
                    var games = env?.Response?.Games;

                    return games ?? new List<OwnedGame>();
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "GetOwnedGamesDetailed API request failed steamId={steamId64}");
                return new List<OwnedGame>();
            }
        }

        /// <summary>
        /// Checks if a game has achievements (simple boolean check).
        /// </summary>
        public async Task<bool> GetSchemaForGameAsync(string apiKey, int appId, string language = "english")
        {
            try
            {
                var url = $"https://api.steampowered.com/ISteamUserStats/GetSchemaForGame/v2/?key={apiKey}&appid={appId}&l={language ?? "english"}";
                var json = await _apiHttp.GetStringAsync(url).ConfigureAwait(false);
                var root = Serialization.FromJson<SchemaRoot>(json);
                var ach = root?.Response?.Game?.AvailableGameStats?.Achievements;
                return (ach != null && ach.Length > 0);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Fetches full achievement schema for a game (names, descriptions, icons, hidden flags).
        /// Returns null if the API call fails or the game has no achievements.
        /// </summary>
        public async Task<SchemaAndPercentages> GetSchemaForGameDetailedAsync(string apiKey, int appId, string language, CancellationToken ct)
        {
            var result = await GetSchemaForGameDetailedInternalAsync(apiKey, appId, language, ct).ConfigureAwait(false);
            if (result == null && !string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Info($"Schema fetch failed for '{language}', retrying with 'english' for appId={appId}");
                result = await GetSchemaForGameDetailedInternalAsync(apiKey, appId, "english", ct).ConfigureAwait(false);
            }
            return result;
        }

        private async Task<SchemaAndPercentages> GetSchemaForGameDetailedInternalAsync(string apiKey, int appId, string language, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || appId <= 0)
            {
                _logger?.Warn("GetSchemaForGameDetailedInternalAsync: Invalid apiKey or appId={appId}");
                return null;
            }

            try
            {
                language = string.IsNullOrWhiteSpace(language) ? "english" : language;
                var url = $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/" +
                          $"?key={Uri.EscapeDataString(apiKey)}" +
                          $"&appid={appId}" +
                          $"&language={Uri.EscapeDataString(language)}";

                // _logger?.Info("GetSchemaForGameDetailedAsync: Calling {url.Replace(apiKey, "***")}");

                using (var resp = await _apiHttp.GetAsync(url, ct).ConfigureAwait(false))
                {
                    // _logger?.Info("GetSchemaForGameDetailedAsync: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                    if (!resp.IsSuccessStatusCode)
                    {
                        // _logger?.Warn("GetGameAchievements API non-success: {(int)resp.StatusCode} {resp.ReasonPhrase} for appId={appId}");
                        return null;
                    }

                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    // _logger?.Info("GetSchemaForGameDetailedAsync: Response length={json?.Length ?? 0}");
                    // _logger?.Info("GetSchemaForGameDetailedAsync: Response JSON={json}");

                    if (string.IsNullOrWhiteSpace(json))
                    {
                        // _logger?.Warn("GetSchemaForGameDetailedAsync: JSON is null or empty");
                        return null;
                    }

                    var root = Serialization.FromJson<GetGameAchievementsRoot>(json);
                    // _logger?.Info("GetSchemaForGameDetailedAsync: Deserialized root={root != null}, response={root?.Response != null}");

                    var achievements = root?.Response?.Achievements;
                    // _logger?.Info("GetSchemaForGameDetailedAsync: achievements={achievements != null}, count={achievements?.Count ?? 0}");

                    if (achievements == null || achievements.Count == 0)
                    {
                        // _logger?.Info("GetSchemaForGameDetailedAsync: No achievements found for appId={appId}");
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

                        if (!string.IsNullOrWhiteSpace(ach.InternalName) && double.TryParse(ach.PlayerPercentUnlocked, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                        {
                            globalPercentages[ach.InternalName] = percent;
                        }
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

        public async Task<List<SteamPlayerSummaries>> GetPlayerSummariesAsync(string apiKey, IEnumerable<ulong> steamIds, CancellationToken ct)
        {
            var ids = steamIds?
                .Where(x => x > 0)
                .Distinct()
                .ToList() ?? new List<ulong>();

            if (ids.Count == 0)
                return new List<SteamPlayerSummaries>();

            if (string.IsNullOrWhiteSpace(apiKey))
                return new List<SteamPlayerSummaries>(); // Caller should handle fallback

            var byId = new Dictionary<ulong, SteamPlayerSummaries>();

            foreach (var batch in Batch(ids, PlayerSummariesBatchSize))
            {
                ct.ThrowIfCancellationRequested();

                var idParam = string.Join(",", batch);
                var url =
                    "https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/" +
                    $"?key={Uri.EscapeDataString(apiKey.Trim())}" +
                    $"&steamids={Uri.EscapeDataString(idParam)}";

                try
                {
                    using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        req.Headers.TryAddWithoutValidation("User-Agent", DefaultUserAgent);
                        req.Headers.TryAddWithoutValidation("Accept", "application/json");

                        using (var resp = await _apiHttp.SendAsync(req, ct).ConfigureAwait(false))
                        {
                            if (!resp.IsSuccessStatusCode)
                                continue;

                            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            if (string.IsNullOrWhiteSpace(json))
                                continue;

                            var root = Serialization.FromJson<PlayerSummariesRoot>(json);
                            var players = root?.Response?.Players;
                            if (players == null || players.Count == 0)
                                continue;

                            foreach (var p in players)
                            {
                                if (p == null) continue;
                                if (!ulong.TryParse(p.SteamId, out var sid) || sid <= 0) continue;

                                byId[sid] = new SteamPlayerSummaries
                                {
                                    SteamId = p.SteamId,
                                    PersonaName = p.PersonaName,
                                    Avatar = p.Avatar,
                                    AvatarMedium = p.AvatarMedium,
                                    AvatarFull = p.AvatarFull
                                };
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "GetPlayerSummaries API request failed (batch).");
                }
            }

            // Preserve original order where possible.
            var ordered = new List<SteamPlayerSummaries>(ids.Count);
            foreach (var id in ids)
            {
                if (byId.TryGetValue(id, out var s) && s != null)
                    ordered.Add(s);
            }

            return ordered;
        }

        private static IEnumerable<List<ulong>> Batch(IReadOnlyList<ulong> ids, int batchSize)
        {
            for (int i = 0; i < ids.Count; i += batchSize)
            {
                var size = Math.Min(batchSize, ids.Count - i);
                var chunk = new List<ulong>(size);
                for (int j = 0; j < size; j++)
                    chunk.Add(ids[i + j]);
                yield return chunk;
            }
        }
    }
}



