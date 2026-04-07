using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Providers.Local
{
    public class LocalSavesProvider : IDataProvider
    {
        private static readonly HashSet<Guid> ReportedAmbiguousFolderGames = new HashSet<Guid>();

        public sealed class ExpectedAchievementsDownloadResult
        {
            public bool Success { get; set; }
            public string FilePath { get; set; }
            public string Message { get; set; }
            public int AppId { get; set; }
            public bool UsedOverride { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _pluginSettings;
        // private readonly string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Local_Debug.txt");
        private readonly Dictionary<int, SchemaAndPercentages> _steamSchemaCache = new Dictionary<int, SchemaAndPercentages>();

        public string ProviderKey => "Local";
        public string ProviderName => "Local"; 
        public string ProviderIconKey => null;
        public string ProviderColorHex => "#FFD700";

        public bool IsAuthenticated => true;
        public ISessionManager AuthSession => null;

        private void Log(string msg)
        {
            // Debug logging disabled to avoid creating Local_Debug.txt
        }

        public LocalSavesProvider(IPlayniteAPI playniteApi, ILogger logger, PlayniteAchievementsSettings settings)
        {
            _api = playniteApi;
            _logger = logger;
            _pluginSettings = settings; // Store the full settings object
            Log("=== Provider Starting V9 (Discovery Mode) ===");
        }

        public bool IsCapable(Game game) => true;

        public async Task<GameAchievementData> GetAchievementsAsync(Game game, RefreshRequest request)
        {
            var appId = GetAppId(game, out var isAppIdOverridden);
            if (string.IsNullOrEmpty(appId)) return null;

            var hasResolvedLocalFolder = TryResolveLocalFolder(game, appId, out var localFolderPath, out _, out _, out _);

            string jsonPath = null;
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                jsonPath = Path.Combine(localFolderPath, "achievements.json");
                if (!File.Exists(jsonPath))
                {
                    jsonPath = null;
                }
            }

            string iniPath = null;
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                iniPath = Path.Combine(localFolderPath, "achievements.ini");
                if (!File.Exists(iniPath))
                {
                    iniPath = null;
                }
            }

            var hasAchievementsFile = !string.IsNullOrWhiteSpace(jsonPath) || !string.IsNullOrWhiteSpace(iniPath);

            SchemaAndPercentages steamSchema = null;
            var apiNameMap = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            int appIdInt = 0;
            if (int.TryParse(appId, out appIdInt))
            {
                steamSchema = await TryGetSteamSchemaAsync(appIdInt).ConfigureAwait(false);
                if (steamSchema?.Achievements != null)
                {
                    apiNameMap = steamSchema.Achievements
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                        .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
                }
            }

            SteamLocalProgressSummary steamLocalProgress = null;
            if (appIdInt > 0)
            {
                steamLocalProgress = TryGetSteamLocalProgressSummary(appIdInt);
            }

            Dictionary<string, LocalEntry> steamAppCacheEntries = null;
            if (appIdInt > 0)
            {
                steamAppCacheEntries = TryGetSteamAppCacheEntries(appIdInt);
            }

            Dictionary<string, LocalEntry> steamLibraryCacheEntries = null;
            if (appIdInt > 0)
            {
                steamLibraryCacheEntries = TryGetSteamLibraryCacheEntries(appIdInt);
            }

            if (!hasResolvedLocalFolder &&
                steamLocalProgress == null &&
                (steamAppCacheEntries == null || steamAppCacheEntries.Count == 0) &&
                (steamLibraryCacheEntries == null || steamLibraryCacheEntries.Count == 0))
            {
                return null;
            }

            var data = new GameAchievementData
            {
                PlayniteGameId = game.Id,
                ProviderKey = ProviderKey,
                GameName = game.Name,
                Achievements = new List<AchievementDetail>()
            };

            if (appIdInt > 0)
            {
                data.AppId = appIdInt;
                data.IsAppIdOverridden = isAppIdOverridden;
            }

            try
            {
                if (hasAchievementsFile)
                {
                    var raw = await LoadLocalEntriesAsync(jsonPath, iniPath).ConfigureAwait(false);
                    if (raw.Count > 0)
                    {
                        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                        {
                            foreach (var schemaAch in steamSchema.Achievements)
                            {
                                if (string.IsNullOrWhiteSpace(schemaAch?.Name))
                                {
                                    continue;
                                }

                                raw.TryGetValue(schemaAch.Name, out var entry);
                                data.Achievements.Add(CreateAchievementDetail(schemaAch.Name, entry, schemaAch, steamSchema));
                                added.Add(schemaAch.Name);
                            }
                        }

                        foreach (var kv in raw.Where(kv => !added.Contains(kv.Key)))
                        {
                            apiNameMap.TryGetValue(kv.Key, out var schemaAch);
                            data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        }

                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from local save data.");
                        return data;
                    }
                }

                if (steamAppCacheEntries != null && steamAppCacheEntries.Count > 0)
                {
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamAppCacheEntries)
                    {
                        apiNameMap.TryGetValue(kv.Key, out var schemaAch);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        added.Add(kv.Key);
                    }

                    Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from Steam appcache stats.");
                    return data;
                }

                if (steamLibraryCacheEntries != null && steamLibraryCacheEntries.Count > 0)
                {
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamLibraryCacheEntries)
                    {
                        apiNameMap.TryGetValue(kv.Key, out var schemaAch);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        added.Add(kv.Key);
                    }

                    Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from Steam local library cache.");
                    return data;
                }

                if (steamLocalProgress != null && steamLocalProgress.TotalCount > 0)
                {
                    data.AggregateAchievementCount = steamLocalProgress.TotalCount;
                    data.AggregateUnlockedCount = steamLocalProgress.UnlockedCount;
                    data.HasAchievements = true;
                    Log($"INFO: {game.Name} - Loaded Steam local aggregate progress {data.UnlockedCount}/{data.AchievementCount} from {steamLocalProgress.SourcePath}.");
                    return data;
                }

                if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                {
                    foreach (var schemaAch in steamSchema.Achievements)
                    {
                        if (string.IsNullOrWhiteSpace(schemaAch.Name))
                        {
                            continue;
                        }

                        var detail = new AchievementDetail
                        {
                            ApiName = schemaAch.Name,
                            DisplayName = schemaAch.DisplayName ?? schemaAch.Name,
                            Description = schemaAch.Description ?? "Local achievement from " + ProviderName,
                            UnlockedIconPath = schemaAch.Icon ?? "Resources/UnlockedAchIcon.png",
                            LockedIconPath = schemaAch.IconGray ?? "Resources/HiddenAchIcon.png",
                            Unlocked = false,
                            Hidden = schemaAch.Hidden == 1
                        };

                        if (schemaAch.GlobalPercent.HasValue)
                        {
                            var normalized = NormalizePercent(schemaAch.GlobalPercent.Value);
                            detail.GlobalPercentUnlocked = normalized;
                            if (normalized.HasValue)
                            {
                                detail.Rarity = PercentRarityHelper.GetRarityTier(normalized.Value);
                            }
                        }

                        data.Achievements.Add(detail);
                    }

                    Log($"INFO: {game.Name} - Local folder found, loaded {data.Achievements.Count} achievement definitions from Steam schema.");
                    return data;
                }

                Log($"INFO: {game.Name} - Local folder found, but no achievements.json and no Steam schema available.");
                return data;
            }
            catch (Exception ex)
            {
                Log($"ERROR: {game.Name} - {ex.Message}");
                return null;
            }
        }

        public async Task<RebuildPayload> RefreshAsync(IReadOnlyList<Game> games, Action<Game> onGameProcessed,
            Func<Game, GameAchievementData, Task> onAchievementsUpdated, System.Threading.CancellationToken token)
        {
            // If we are in 'None' library, the plugin usually sends 0 games.
            // We override this to check every game in your library.
            var targetGames = (games != null && games.Count > 0) ? games : _api.Database.Games.ToList();
            
            foreach (var game in targetGames)
            {
                    if (token.IsCancellationRequested) break;

                var data = await GetAchievementsAsync(game, null);
                if (data != null)
                {
                    // Update the internal provider cache so the UI knows we own this game
                    _pluginSettings.Persisted.ProviderSettings[ProviderKey] = Newtonsoft.Json.Linq.JObject.FromObject(new { IsEnabled = true });
                    
                    await onAchievementsUpdated(game, data);
                    Log($"DATABASE: Submitted {game.Name} (Count: {data.Achievements.Count})");
                }
                
                if (games?.Count > 0) onGameProcessed(game);
            }
            return new RebuildPayload();
        }

        internal static bool TryGetAppIdOverride(Guid gameId, out int appId)
        {
            appId = 0;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            return settings?.SteamAppIdOverrides != null &&
                   settings.SteamAppIdOverrides.TryGetValue(gameId, out appId) &&
                   appId > 0;
        }

        internal static bool TryGetFolderOverride(Guid gameId, out string folderPath)
        {
            folderPath = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.LocalFolderOverrides == null || !settings.LocalFolderOverrides.TryGetValue(gameId, out var configuredPath))
            {
                return false;
            }

            folderPath = configuredPath?.Trim();
            return !string.IsNullOrWhiteSpace(folderPath);
        }

        internal static bool TrySetAppIdOverride(Guid gameId, int appId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || appId <= 0)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.SteamAppIdOverrides[gameId] = appId;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local Steam App ID override for '{gameName}' to {appId}");
            return true;
        }

        internal static bool TryClearAppIdOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (!settings.SteamAppIdOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local Steam App ID override for '{gameName}'");
            return true;
        }

        internal static bool TrySetFolderOverride(Guid gameId, string folderPath, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            var normalizedPath = folderPath.Trim();
            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.LocalFolderOverrides[gameId] = normalizedPath;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local folder override for '{gameName}' to '{normalizedPath}'");
            return true;
        }

        internal static bool TryClearFolderOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (!settings.LocalFolderOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local folder override for '{gameName}'");
            return true;
        }

        internal static bool TryResolveAppId(Game game, out int appId, out bool isOverridden)
        {
            appId = 0;
            isOverridden = false;

            if (game == null)
            {
                return false;
            }

            if (TryGetAppIdOverride(game.Id, out var overriddenAppId))
            {
                appId = overriddenAppId;
                isOverridden = true;
                return true;
            }

            var detected = DetectAppId(game);
            return int.TryParse(detected, out appId) && appId > 0;
        }

        private string GetAppId(Game game, out bool isOverridden)
        {
            isOverridden = false;
            if (!TryResolveAppId(game, out var appId, out isOverridden))
            {
                return null;
            }

            return appId.ToString();
        }

        private static string DetectAppId(Game game)
        {
            if (game == null) return null;
            if (!string.IsNullOrEmpty(game.GameId) && Regex.IsMatch(game.GameId, @"^\d+$")) return game.GameId;
            if (game.Links != null)
            {
                foreach (var link in game.Links)
                {
                    var match = Regex.Match(link.Url ?? "", @"/app/(\d+)");
                    if (match.Success) return match.Groups[1].Value;
                }
            }
            if (!string.IsNullOrEmpty(game.Notes))
            {
                var match = Regex.Match(game.Notes, @"SteamID[:\s]+(\d+)", RegexOptions.IgnoreCase);
                if (match.Success) return match.Groups[1].Value;
            }
            return null;
        }

        private Dictionary<string, LocalEntry> TryGetSteamAppCacheEntries(int appId)
        {
            if (appId <= 0)
            {
                return null;
            }

            var schema = TryGetSteamAppCacheSchema(appId);
            if (schema == null || schema.Count == 0)
            {
                return null;
            }

            var unlockTimes = TryGetSteamAppCacheUnlockTimes(appId);
            var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var schemaEntry in schema)
            {
                unlockTimes.TryGetValue(schemaEntry.Index, out var timestamp);
                entries[schemaEntry.ApiName] = new LocalEntry
                {
                    earned = timestamp > 0,
                    earned_time = timestamp,
                    displayName = string.IsNullOrWhiteSpace(schemaEntry.DisplayName) ? schemaEntry.ApiName : schemaEntry.DisplayName,
                    description = schemaEntry.Description ?? string.Empty,
                    icon = BuildSteamAchievementIconUrl(appId, schemaEntry.IconHash),
                    iconGray = BuildSteamAchievementIconUrl(appId, schemaEntry.IconGrayHash),
                    hidden = schemaEntry.Hidden
                };
            }

            return entries.Count > 0 ? entries : null;
        }

        private List<SteamAppCacheSchemaEntry> TryGetSteamAppCacheSchema(int appId)
        {
            foreach (var schemaPath in GetSteamAppCacheSchemaFilePaths(appId))
            {
                try
                {
                    if (!File.Exists(schemaPath))
                    {
                        continue;
                    }

                    var bytes = File.ReadAllBytes(schemaPath);
                    var tokens = ExtractNullDelimitedTokens(bytes);
                    if (tokens.Count == 0)
                    {
                        continue;
                    }

                    var bitsIndex = tokens.FindIndex(token => string.Equals(token, "bits", StringComparison.OrdinalIgnoreCase));
                    if (bitsIndex < 0)
                    {
                        continue;
                    }

                    var entries = new List<SteamAppCacheSchemaEntry>();
                    var index = bitsIndex + 1;

                    while (index < tokens.Count)
                    {
                        if (!int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var entryIndex) ||
                            index + 1 >= tokens.Count ||
                            !string.Equals(tokens[index + 1], "name", StringComparison.OrdinalIgnoreCase))
                        {
                            index++;
                            continue;
                        }

                        var entry = new SteamAppCacheSchemaEntry { Index = entryIndex };
                        index += 2;
                        if (index >= tokens.Count)
                        {
                            break;
                        }

                        entry.ApiName = tokens[index];
                        index++;

                        while (index < tokens.Count)
                        {
                            if (int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nextIndex) &&
                                index + 1 < tokens.Count &&
                                string.Equals(tokens[index + 1], "name", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

                            var token = tokens[index];
                            if (string.Equals(token, "display", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.DisplayName = ParseLocalizedTokenValue(tokens, ref index);
                                continue;
                            }

                            if (string.Equals(token, "desc", StringComparison.OrdinalIgnoreCase))
                            {
                                entry.Description = ParseLocalizedTokenValue(tokens, ref index);
                                continue;
                            }

                            if (string.Equals(token, "hidden", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count)
                            {
                                entry.Hidden = string.Equals(tokens[index + 1], "1", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(tokens[index + 1], "true", StringComparison.OrdinalIgnoreCase);
                                index += 2;
                                continue;
                            }

                            if (string.Equals(token, "icon", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count)
                            {
                                entry.IconHash = tokens[index + 1];
                                index += 2;
                                continue;
                            }

                            if (string.Equals(token, "icon_gray", StringComparison.OrdinalIgnoreCase) && index + 1 < tokens.Count)
                            {
                                entry.IconGrayHash = tokens[index + 1];
                                index += 2;
                                continue;
                            }

                            index++;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.ApiName))
                        {
                            entries.Add(entry);
                        }
                    }

                    if (entries.Count > 0)
                    {
                        return entries;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE SCHEMA ERROR: path={schemaPath} msg={ex.Message}");
                }
            }

            return null;
        }

        private Dictionary<int, long> TryGetSteamAppCacheUnlockTimes(int appId)
        {
            var result = new Dictionary<int, long>();
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var userStatsPath in GetSteamAppCacheUserStatsFilePaths(appId))
            {
                try
                {
                    if (!File.Exists(userStatsPath))
                    {
                        continue;
                    }

                    var bytes = File.ReadAllBytes(userStatsPath);
                    var marker = System.Text.Encoding.ASCII.GetBytes("AchievementTimes");

                    for (var position = 0; position <= bytes.Length - marker.Length; position++)
                    {
                        if (!ByteSequenceEquals(bytes, position, marker))
                        {
                            continue;
                        }

                        var cursor = position + marker.Length;
                        while (cursor < bytes.Length && bytes[cursor] != 0)
                        {
                            cursor++;
                        }

                        if (cursor < bytes.Length)
                        {
                            cursor++;
                        }

                        while (cursor < bytes.Length)
                        {
                            while (cursor < bytes.Length && (bytes[cursor] < (byte)'0' || bytes[cursor] > (byte)'9'))
                            {
                                cursor++;
                            }

                            if (cursor >= bytes.Length)
                            {
                                break;
                            }

                            var start = cursor;
                            while (cursor < bytes.Length && bytes[cursor] >= (byte)'0' && bytes[cursor] <= (byte)'9')
                            {
                                cursor++;
                            }

                            if (cursor >= bytes.Length || bytes[cursor] != 0)
                            {
                                continue;
                            }

                            var key = System.Text.Encoding.ASCII.GetString(bytes, start, cursor - start);
                            cursor++;

                            if (cursor + 4 > bytes.Length)
                            {
                                break;
                            }

                            var timestamp = BitConverter.ToUInt32(bytes, cursor);
                            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var entryIndex) &&
                                timestamp >= 946684800 &&
                                timestamp <= nowUnix + 86400)
                            {
                                result[entryIndex] = timestamp;
                                cursor += 4;
                                continue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE USERSTATS ERROR: path={userStatsPath} msg={ex.Message}");
                }
            }

            return result;
        }

        private static List<string> ExtractNullDelimitedTokens(byte[] bytes)
        {
            var tokens = new List<string>();
            if (bytes == null || bytes.Length == 0)
            {
                return tokens;
            }

            var buffer = new List<byte>();
            for (var i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] == 0)
                {
                    AddSanitizedToken(tokens, buffer);
                    buffer.Clear();
                    continue;
                }

                buffer.Add(bytes[i]);
            }

            AddSanitizedToken(tokens, buffer);
            return tokens;
        }

        private static void AddSanitizedToken(List<string> tokens, List<byte> buffer)
        {
            if (tokens == null || buffer == null || buffer.Count == 0)
            {
                return;
            }

            var raw = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            var sanitized = new string(raw.Where(character => !char.IsControl(character)).ToArray()).Trim();
            if (!string.IsNullOrWhiteSpace(sanitized))
            {
                tokens.Add(sanitized);
            }
        }

        private static string ParseLocalizedTokenValue(IReadOnlyList<string> tokens, ref int index)
        {
            string fallback = null;
            index++;

            if (index < tokens.Count && string.Equals(tokens[index], "name", StringComparison.OrdinalIgnoreCase))
            {
                index++;
            }

            while (index < tokens.Count)
            {
                if (string.Equals(tokens[index], "token", StringComparison.OrdinalIgnoreCase))
                {
                    index += Math.Min(2, tokens.Count - index);
                    continue;
                }

                if (IsSchemaFieldToken(tokens[index]) ||
                    (int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var nextIndex) &&
                     index + 1 < tokens.Count &&
                     string.Equals(tokens[index + 1], "name", StringComparison.OrdinalIgnoreCase)))
                {
                    break;
                }

                var language = tokens[index];
                if (index + 1 >= tokens.Count)
                {
                    index++;
                    continue;
                }

                var value = tokens[index + 1];
                if (string.Equals(language, "english", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
                {
                    index += 2;
                    return value;
                }

                if (string.IsNullOrWhiteSpace(fallback) && !string.IsNullOrWhiteSpace(value))
                {
                    fallback = value;
                }

                index += 2;
            }

            return fallback;
        }

        private static bool IsSchemaFieldToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            switch (token)
            {
                case "display":
                case "desc":
                case "hidden":
                case "icon":
                case "icon_gray":
                case "progress":
                case "value":
                case "type":
                case "min_val":
                case "max_val":
                case "operation":
                case "operand1":
                case "bits":
                case "stats":
                    return true;
                default:
                    return false;
            }
        }

        private static bool ByteSequenceEquals(byte[] source, int offset, byte[] sequence)
        {
            if (source == null || sequence == null || offset < 0 || offset + sequence.Length > source.Length)
            {
                return false;
            }

            for (var i = 0; i < sequence.Length; i++)
            {
                if (source[offset + i] != sequence[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static string BuildSteamAchievementIconUrl(int appId, string iconHash)
        {
            if (appId <= 0 || string.IsNullOrWhiteSpace(iconHash))
            {
                return null;
            }

            return $"https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/{appId}/{iconHash}";
        }

        private IEnumerable<string> GetSteamAppCacheSchemaFilePaths(int appId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileName = $"UserGameStatsSchema_{appId}.bin";

            foreach (var statsRoot in GetSteamAppCacheStatsRoots())
            {
                var path = Path.Combine(statsRoot, fileName);
                if (File.Exists(path))
                {
                    candidates.Add(path);
                }
            }

            return candidates;
        }

        private IEnumerable<string> GetSteamAppCacheUserStatsFilePaths(int appId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffix = "_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin";
            var preferredAccountIds = GetPreferredSteamAccountIds();

            foreach (var statsRoot in GetSteamAppCacheStatsRoots())
            {
                if (!Directory.Exists(statsRoot))
                {
                    continue;
                }

                try
                {
                    foreach (var accountId in preferredAccountIds)
                    {
                        var exactPath = Path.Combine(
                            statsRoot,
                            $"UserGameStats_{accountId}_{appId.ToString(CultureInfo.InvariantCulture)}.bin");

                        if (File.Exists(exactPath))
                        {
                            candidates.Add(exactPath);
                        }
                    }

                    foreach (var path in Directory.EnumerateFiles(statsRoot, "UserGameStats_*" + suffix, SearchOption.TopDirectoryOnly))
                    {
                        candidates.Add(path);
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE USERSTATS SEARCH ERROR: root={statsRoot} msg={ex.Message}");
                }
            }

            return candidates;
        }

        private IEnumerable<string> GetPreferredSteamAccountIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            var configuredSteamUserId = steamSettings?.SteamUserId?.Trim();
            foreach (var candidate in ExpandSteamAccountIdCandidates(configuredSteamUserId))
            {
                ids.Add(candidate);
            }

            foreach (var userdataRoot in GetSteamUserdataRoots())
            {
                if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
                {
                    continue;
                }

                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                    {
                        var name = Path.GetFileName(userDir)?.Trim();
                        if (!string.IsNullOrWhiteSpace(name) && Regex.IsMatch(name, @"^\d+$"))
                        {
                            ids.Add(name);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM ACCOUNT ID SEARCH ERROR: root={userdataRoot} msg={ex.Message}");
                }
            }

            return ids;
        }

        private static IEnumerable<string> ExpandSteamAccountIdCandidates(string steamUserId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(steamUserId))
            {
                return candidates;
            }

            var trimmed = steamUserId.Trim();
            if (Regex.IsMatch(trimmed, @"^\d+$"))
            {
                candidates.Add(trimmed);

                if (ulong.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var steamIdValue) &&
                    steamIdValue >= 76561197960265728UL)
                {
                    var accountId = steamIdValue - 76561197960265728UL;
                    if (accountId <= uint.MaxValue)
                    {
                        candidates.Add(accountId.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }

            return candidates;
        }

        private IEnumerable<string> GetSteamAppCacheStatsRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var installRoot in GetSteamInstallRoots())
            {
                var statsRoot = Path.Combine(installRoot, "appcache", "stats");
                if (Directory.Exists(statsRoot))
                {
                    roots.Add(statsRoot);
                }
            }

            return roots;
        }

        internal bool TryResolveAchievementsJsonPath(Game game, out string jsonPath, out int appId, out bool isOverridden)
        {
            jsonPath = null;
            appId = 0;
            isOverridden = false;

            if (!TryResolveAppId(game, out appId, out isOverridden))
            {
                return false;
            }

            if (!TryResolveLocalFolder(game, appId.ToString(CultureInfo.InvariantCulture), out var localFolderPath, out _, out _, out _) || string.IsNullOrWhiteSpace(localFolderPath))
            {
                return false;
            }

            jsonPath = Path.Combine(localFolderPath, "achievements.json");
            return true;
        }

        internal bool TryGetResolvedFolderInfo(Game game, out string selectedFolderPath, out IReadOnlyList<string> candidateFolders, out bool isOverridden, out bool isAmbiguous)
        {
            selectedFolderPath = null;
            candidateFolders = Array.Empty<string>();
            isOverridden = false;
            isAmbiguous = false;

            if (!TryResolveAppId(game, out var appId, out _))
            {
                return false;
            }

            return TryResolveLocalFolder(game, appId.ToString(CultureInfo.InvariantCulture), out selectedFolderPath, out candidateFolders, out isOverridden, out isAmbiguous);
        }

        public async Task<ExpectedAchievementsDownloadResult> DownloadExpectedAchievementsFileAsync(Game game, CancellationToken token)
        {
            if (game == null)
            {
                return new ExpectedAchievementsDownloadResult
                {
                    Success = false,
                    Message = ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_InvalidGame")
                };
            }

            if (!TryResolveAppId(game, out var appId, out var isOverridden))
            {
                return new ExpectedAchievementsDownloadResult
                {
                    Success = false,
                    Message = ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_NoAppId")
                };
            }

            if (!TryResolveAchievementsJsonPath(game, out var jsonPath, out _, out _))
            {
                return new ExpectedAchievementsDownloadResult
                {
                    Success = false,
                    AppId = appId,
                    UsedOverride = isOverridden,
                    Message = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_NoFolder"),
                        appId)
                };
            }

            var schema = await TryGetSteamSchemaAsync(appId).ConfigureAwait(false);
            if (schema?.Achievements == null || schema.Achievements.Count == 0)
            {
                return new ExpectedAchievementsDownloadResult
                {
                    Success = false,
                    FilePath = jsonPath,
                    AppId = appId,
                    UsedOverride = isOverridden,
                    Message = string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_NoSchema"),
                        appId)
                };
            }

            var payload = new SortedDictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in schema.Achievements.Where(a => !string.IsNullOrWhiteSpace(a?.Name)))
            {
                token.ThrowIfCancellationRequested();

                double? globalPercent = null;
                if (schema.GlobalPercentages?.TryGetValue(achievement.Name, out var resolvedGlobalPercent) == true)
                {
                    globalPercent = resolvedGlobalPercent;
                }

                payload[achievement.Name] = new LocalEntry
                {
                    earned = false,
                    earned_time = 0,
                    displayName = achievement.DisplayName ?? achievement.Name,
                    description = achievement.Description ?? string.Empty,
                    icon = achievement.Icon ?? string.Empty,
                    iconGray = achievement.IconGray ?? string.Empty,
                    hidden = achievement.Hidden == 1,
                    percent = NormalizePercent(globalPercent)
                };
            }

            Directory.CreateDirectory(Path.GetDirectoryName(jsonPath));
            var serialized = JsonConvert.SerializeObject(payload, Formatting.Indented);
            await Task.Run(() => File.WriteAllText(jsonPath, serialized), token).ConfigureAwait(false);

            return new ExpectedAchievementsDownloadResult
            {
                Success = true,
                FilePath = jsonPath,
                AppId = appId,
                UsedOverride = isOverridden,
                Message = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Success"),
                    game.Name,
                    jsonPath)
            };
        }

        private static AchievementDetail CreateAchievementDetail(
            string apiName,
            LocalEntry entry,
            SchemaAchievement schemaAch,
            SchemaAndPercentages steamSchema)
        {
            var displayName = !string.IsNullOrWhiteSpace(entry.displayName)
                ? entry.displayName
                : schemaAch?.DisplayName ?? apiName;

            var description = !string.IsNullOrWhiteSpace(entry.description)
                ? entry.description
                : schemaAch?.Description ?? "Local achievement from Local";

            var unlockedIcon = !string.IsNullOrWhiteSpace(entry.icon)
                ? entry.icon
                : schemaAch?.Icon ?? "Resources/UnlockedAchIcon.png";

            var lockedIcon = !string.IsNullOrWhiteSpace(entry.iconGray)
                ? entry.iconGray
                : schemaAch?.IconGray ?? "Resources/HiddenAchIcon.png";

            var detail = new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = displayName,
                Description = description,
                UnlockedIconPath = unlockedIcon,
                LockedIconPath = lockedIcon,
                Unlocked = entry.earned,
                Hidden = entry.hidden || (schemaAch?.Hidden == 1),
                UnlockTimeUtc = entry.earned && entry.earned_time > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(entry.earned_time).UtcDateTime
                    : (DateTime?)null
            };

            double? globalPercent = entry.percent;
            if (!globalPercent.HasValue)
            {
                if (schemaAch?.GlobalPercent.HasValue == true)
                {
                    globalPercent = schemaAch.GlobalPercent.Value;
                }
                else if (steamSchema?.GlobalPercentages?.TryGetValue(apiName, out var resolvedPercent) == true)
                {
                    globalPercent = resolvedPercent;
                }
            }

            if (globalPercent.HasValue)
            {
                detail.GlobalPercentUnlocked = NormalizePercent(globalPercent.Value);
                if (detail.GlobalPercentUnlocked.HasValue)
                {
                    detail.Rarity = PercentRarityHelper.GetRarityTier(detail.GlobalPercentUnlocked.Value);
                }
            }

            return detail;
        }

        private async Task<Dictionary<string, LocalEntry>> LoadLocalEntriesAsync(string jsonPath, string iniPath)
        {
            var merged = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var json = await Task.Run(() => File.ReadAllText(jsonPath)).ConfigureAwait(false);
                var jsonEntries = JsonConvert.DeserializeObject<Dictionary<string, LocalEntry>>(json);
                if (jsonEntries != null)
                {
                    foreach (var entry in jsonEntries.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
                    {
                        merged[entry.Key] = entry.Value;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(iniPath) && File.Exists(iniPath))
            {
                var ini = await Task.Run(() => File.ReadAllLines(iniPath)).ConfigureAwait(false);
                var iniEntries = ParseIniEntries(ini);
                foreach (var entry in iniEntries)
                {
                    if (merged.TryGetValue(entry.Key, out var existing))
                    {
                        existing.earned = entry.Value.earned;
                        if (entry.Value.earned_time > 0)
                        {
                            existing.earned_time = entry.Value.earned_time;
                        }

                        if (entry.Value.hidden)
                        {
                            existing.hidden = true;
                        }

                        if (entry.Value.percent.HasValue)
                        {
                            existing.percent = entry.Value.percent;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.Value.displayName))
                        {
                            existing.displayName = entry.Value.displayName;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.Value.description))
                        {
                            existing.description = entry.Value.description;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.Value.icon))
                        {
                            existing.icon = entry.Value.icon;
                        }

                        if (!string.IsNullOrWhiteSpace(entry.Value.iconGray))
                        {
                            existing.iconGray = entry.Value.iconGray;
                        }

                        merged[entry.Key] = existing;
                    }
                    else
                    {
                        merged[entry.Key] = entry.Value;
                    }
                }
            }

            return merged;
        }

        private static Dictionary<string, LocalEntry> ParseIniEntries(IEnumerable<string> lines)
        {
            var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            var currentSection = string.Empty;

            foreach (var rawLine in lines ?? Array.Empty<string>())
            {
                var line = (rawLine ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) && line.Length > 2)
                {
                    currentSection = line.Substring(1, line.Length - 2).Trim();
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                ApplyIniEntry(entries, currentSection, key, value);
            }

            return entries;
        }

        private static void ApplyIniEntry(Dictionary<string, LocalEntry> entries, string section, string key, string value)
        {
            if (TryExtractAchievementField(key, out var achievementName, out var fieldName))
            {
                ApplyIniField(entries, achievementName, fieldName, value);
                return;
            }

            if (!IsGenericIniSection(section) && TryParseKnownIniField(key, out fieldName))
            {
                ApplyIniField(entries, section, fieldName, value);
                return;
            }

            if (TryParseUnlockedValue(value, out var unlocked))
            {
                ApplyIniField(entries, key, "earned", unlocked ? "1" : "0");
                return;
            }

            if (TryParseUnlockTimestamp(value, out var timestamp))
            {
                ApplyIniField(entries, key, "earned_time", timestamp.ToString(CultureInfo.InvariantCulture));
            }
        }

        private static void ApplyIniField(Dictionary<string, LocalEntry> entries, string achievementName, string fieldName, string value)
        {
            if (string.IsNullOrWhiteSpace(achievementName) || string.IsNullOrWhiteSpace(fieldName))
            {
                return;
            }

            entries.TryGetValue(achievementName, out var entry);

            switch (fieldName)
            {
                case "earned":
                case "unlocked":
                case "achieved":
                    if (TryParseUnlockedValue(value, out var unlocked))
                    {
                        entry.earned = unlocked;
                    }
                    break;

                case "earned_time":
                case "unlocktime":
                case "timestamp":
                case "time":
                    if (TryParseUnlockTimestamp(value, out var timestamp))
                    {
                        entry.earned_time = timestamp;
                        if (timestamp > 0)
                        {
                            entry.earned = true;
                        }
                    }
                    break;

                case "displayname":
                    entry.displayName = value;
                    break;

                case "description":
                    entry.description = value;
                    break;

                case "icon":
                    entry.icon = value;
                    break;

                case "icongray":
                case "lockedicon":
                    entry.iconGray = value;
                    break;

                case "hidden":
                    if (TryParseUnlockedValue(value, out var hidden))
                    {
                        entry.hidden = hidden;
                    }
                    break;

                case "percent":
                    if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedPercent))
                    {
                        entry.percent = parsedPercent;
                    }
                    break;

                default:
                    return;
            }

            entries[achievementName] = entry;
        }

        private static bool TryExtractAchievementField(string key, out string achievementName, out string fieldName)
        {
            achievementName = null;
            fieldName = null;

            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            foreach (var separator in new[] { '.', ':' })
            {
                var index = key.LastIndexOf(separator);
                if (index > 0 && index < key.Length - 1)
                {
                    var candidateAchievementName = key.Substring(0, index).Trim();
                    var candidateFieldName = key.Substring(index + 1).Trim();
                    if (TryParseKnownIniField(candidateFieldName, out fieldName))
                    {
                        achievementName = candidateAchievementName;
                        return true;
                    }
                }
            }

            foreach (var suffix in KnownIniFieldSuffixes)
            {
                if (key.EndsWith(suffix.Key, StringComparison.OrdinalIgnoreCase) && key.Length > suffix.Key.Length)
                {
                    achievementName = key.Substring(0, key.Length - suffix.Key.Length).TrimEnd('_', '-', '.');
                    fieldName = suffix.Value;
                    return !string.IsNullOrWhiteSpace(achievementName);
                }
            }

            return false;
        }

        private static bool TryParseKnownIniField(string key, out string fieldName)
        {
            fieldName = null;
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return KnownIniFields.TryGetValue(key.Trim(), out fieldName);
        }

        private static bool IsGenericIniSection(string section)
        {
            if (string.IsNullOrWhiteSpace(section))
            {
                return true;
            }

            return GenericIniSections.Contains(section.Trim());
        }

        private static bool TryParseUnlockedValue(string value, out bool unlocked)
        {
            unlocked = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            switch (value.Trim().Trim('"').ToLowerInvariant())
            {
                case "1":
                case "true":
                case "yes":
                case "y":
                case "on":
                case "unlocked":
                case "earned":
                    unlocked = true;
                    return true;

                case "0":
                case "false":
                case "no":
                case "n":
                case "off":
                case "locked":
                    unlocked = false;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryParseUnlockTimestamp(string value, out long timestamp)
        {
            timestamp = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim().Trim('"');
            if (long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixTimestamp))
            {
                timestamp = unixTimestamp;
                return true;
            }

            if (DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedDate))
            {
                timestamp = parsedDate.ToUnixTimeSeconds();
                return true;
            }

            return false;
        }

        private bool TryFindAchievementFiles(string appId, out string jsonPath, out string iniPath)
        {
            jsonPath = null;
            iniPath = null;

            TryFindAchievementFile(appId, "achievements.json", out jsonPath);
            TryFindAchievementFile(appId, "achievements.ini", out iniPath);
            return !string.IsNullOrWhiteSpace(jsonPath) || !string.IsNullOrWhiteSpace(iniPath);
        }

        private bool TryFindAchievementFile(string appId, string fileName, out string filePath)
        {
            filePath = null;
            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                try
                {
                    var candidate = root;
                    if (candidate.EndsWith(fileName, StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(candidate))
                        {
                            filePath = candidate;
                            return true;
                        }

                        continue;
                    }

                    candidate = Path.Combine(candidate, appId, fileName);
                    if (File.Exists(candidate))
                    {
                        filePath = candidate;
                        return true;
                    }

                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (var matchDir in Directory.EnumerateDirectories(root, appId, SearchOption.AllDirectories))
                    {
                        candidate = Path.Combine(matchDir, fileName);
                        if (File.Exists(candidate))
                        {
                            filePath = candidate;
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"SEARCH ERROR: root={root} msg={ex.Message}");
                }
            }

            return false;
        }

        private bool TryResolveLocalFolder(
            Game game,
            string appId,
            out string folderPath,
            out IReadOnlyList<string> candidateFolders,
            out bool isOverridden,
            out bool isAmbiguous)
        {
            folderPath = null;
            candidateFolders = Array.Empty<string>();
            isOverridden = false;
            isAmbiguous = false;

            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            var candidates = FindLocalFolders(appId);
            candidateFolders = candidates;

            if (game != null && TryGetFolderOverride(game.Id, out var overriddenFolderPath))
            {
                if (Directory.Exists(overriddenFolderPath))
                {
                    folderPath = overriddenFolderPath;
                    isOverridden = true;
                    return true;
                }

                _logger?.Warn($"Local folder override for '{game.Name}' no longer exists: {overriddenFolderPath}");
            }

            if (candidates.Count == 0)
            {
                return false;
            }

            if (candidates.Count == 1)
            {
                folderPath = candidates[0];
                return true;
            }

            isAmbiguous = true;
            folderPath = ChooseBestLocalFolderCandidate(candidates);
            NotifyAmbiguousFolderSelection(game, appId, candidates, folderPath);
            return !string.IsNullOrWhiteSpace(folderPath);
        }

        private List<string> FindLocalFolders(string appId)
        {
            var folders = new List<string>();
            if (string.IsNullOrWhiteSpace(appId))
            {
                return folders;
            }

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(root))
                    {
                        var candidate = Path.Combine(root, appId);
                        if (Directory.Exists(candidate))
                        {
                            folders.Add(candidate);
                        }

                        if (string.Equals(Path.GetFileName(root), appId, StringComparison.OrdinalIgnoreCase))
                        {
                            folders.Add(root);
                        }

                        foreach (var matchDir in Directory.EnumerateDirectories(root, appId, SearchOption.AllDirectories))
                        {
                            folders.Add(matchDir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"SEARCH ERROR: root={root} msg={ex.Message}");
                }
            }

            return folders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string ChooseBestLocalFolderCandidate(IEnumerable<string> candidates)
        {
            return candidates?
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => new
                {
                    Path = path,
                    Score = GetLocalFolderCandidateScore(path),
                    LastWrite = GetLatestAchievementFileWriteTime(path)
                })
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.LastWrite)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path)
                .FirstOrDefault();
        }

        private static int GetLocalFolderCandidateScore(string folderPath)
        {
            var score = 0;
            if (File.Exists(Path.Combine(folderPath, "achievements.ini")))
            {
                score += 2;
            }

            if (File.Exists(Path.Combine(folderPath, "achievements.json")))
            {
                score += 1;
            }

            return score;
        }

        private static DateTime GetLatestAchievementFileWriteTime(string folderPath)
        {
            var latest = DateTime.MinValue;
            foreach (var fileName in new[] { "achievements.ini", "achievements.json" })
            {
                var filePath = Path.Combine(folderPath, fileName);
                if (File.Exists(filePath))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(filePath);
                    if (lastWrite > latest)
                    {
                        latest = lastWrite;
                    }
                }
            }

            return latest;
        }

        private void NotifyAmbiguousFolderSelection(Game game, string appId, IReadOnlyList<string> candidates, string selectedFolderPath)
        {
            if (game == null || game.Id == Guid.Empty || candidates == null || candidates.Count <= 1)
            {
                return;
            }

            lock (ReportedAmbiguousFolderGames)
            {
                if (!ReportedAmbiguousFolderGames.Add(game.Id))
                {
                    return;
                }
            }

            try
            {
                var message = string.Format(
                    ResourceProvider.GetString("LOCPlayAch_LocalFolder_AmbiguousNotification"),
                    game.Name,
                    appId,
                    selectedFolderPath,
                    candidates.Count);

                _api?.Notifications?.Add(new NotificationMessage(
                    $"PlayAch-LocalFolderAmbiguous-{game.Id}",
                    $"{ResourceProvider.GetString("LOCPlayAch_Title_PluginName")}\n{message}",
                    NotificationType.Info));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to show Local folder ambiguity notification for '{game.Name}'.");
            }
        }

        private bool TryFindLocalFolder(string appId, out string folderPath)
        {
            folderPath = FindLocalFolders(appId).FirstOrDefault();
            return !string.IsNullOrWhiteSpace(folderPath);
        }

        private SteamLocalProgressSummary TryGetSteamLocalProgressSummary(int appId)
        {
            if (appId <= 0)
            {
                return null;
            }

            foreach (var progressFilePath in GetSteamAchievementProgressFilePaths())
            {
                try
                {
                    if (!File.Exists(progressFilePath))
                    {
                        continue;
                    }

                    var json = File.ReadAllText(progressFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var root = JObject.Parse(json);
                    var mapCache = root["mapCache"] as JArray;
                    if (mapCache == null)
                    {
                        continue;
                    }

                    for (var i = 0; i < mapCache.Count; i++)
                    {
                        if (!(mapCache[i] is JArray entry) || entry.Count < 2)
                        {
                            continue;
                        }

                        var entryAppId = entry[0]?.Value<int?>();
                        if (!entryAppId.HasValue || entryAppId.Value != appId)
                        {
                            continue;
                        }

                        var payload = entry[1]?.ToObject<SteamLocalProgressPayload>();
                        if (payload == null)
                        {
                            continue;
                        }

                        var totalCount = Math.Max(0, payload.total);
                        if (totalCount <= 0)
                        {
                            return null;
                        }

                        var unlockedCount = Math.Max(0, Math.Min(payload.unlocked, totalCount));
                        return new SteamLocalProgressSummary
                        {
                            AppId = appId,
                            TotalCount = totalCount,
                            UnlockedCount = unlockedCount,
                            SourcePath = progressFilePath
                        };
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM LOCAL PROGRESS ERROR: path={progressFilePath} msg={ex.Message}");
                }
            }

            return null;
        }

        private Dictionary<string, LocalEntry> TryGetSteamLibraryCacheEntries(int appId)
        {
            if (appId <= 0)
            {
                return null;
            }

            foreach (var cacheFilePath in GetSteamLibraryCacheFilePaths(appId))
            {
                try
                {
                    if (!File.Exists(cacheFilePath))
                    {
                        continue;
                    }

                    var json = File.ReadAllText(cacheFilePath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var sections = JArray.Parse(json);
                    var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

                    for (var i = 0; i < sections.Count; i++)
                    {
                        if (!(sections[i] is JArray section) || section.Count < 2)
                        {
                            continue;
                        }

                        var sectionName = section[0]?.Value<string>();
                        if (!string.Equals(sectionName, "achievements", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var payload = section[1]?["data"] as JObject;
                        if (payload == null)
                        {
                            continue;
                        }

                        AddSteamLibraryCacheSectionEntries(entries, payload["vecHighlight"] as JArray);
                        AddSteamLibraryCacheSectionEntries(entries, payload["vecUnachieved"] as JArray);
                        AddSteamLibraryCacheSectionEntries(entries, payload["vecAchievedHidden"] as JArray);
                    }

                    if (entries.Count > 0)
                    {
                        return entries;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM LIBRARYCACHE ERROR: path={cacheFilePath} msg={ex.Message}");
                }
            }

            return null;
        }

        private static void AddSteamLibraryCacheSectionEntries(Dictionary<string, LocalEntry> entries, JArray sectionEntries)
        {
            if (entries == null || sectionEntries == null)
            {
                return;
            }

            for (var i = 0; i < sectionEntries.Count; i++)
            {
                if (!(sectionEntries[i] is JObject entry))
                {
                    continue;
                }

                var id = entry["strID"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var unlocked = entry["bAchieved"]?.Value<bool?>() ?? false;
                var unlockTime = entry["rtUnlocked"]?.Value<long?>() ?? 0;
                if (!unlocked && unlockTime > 0)
                {
                    unlocked = true;
                }

                entries[id] = new LocalEntry
                {
                    earned = unlocked,
                    earned_time = unlockTime,
                    displayName = entry["strName"]?.Value<string>(),
                    description = entry["strDescription"]?.Value<string>(),
                    icon = entry["strImage"]?.Value<string>(),
                    hidden = entry["bHidden"]?.Value<bool?>() ?? false,
                    percent = NormalizePercent(entry["flAchieved"]?.Value<double?>())
                };
            }
        }

        private IEnumerable<string> GetSteamLibraryCacheFilePaths(int appId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileName = appId.ToString(CultureInfo.InvariantCulture) + ".json";

            foreach (var userdataRoot in GetSteamUserdataRoots())
            {
                if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
                {
                    continue;
                }

                var directCachePath = Path.Combine(userdataRoot, "config", "librarycache", fileName);
                if (File.Exists(directCachePath))
                {
                    candidates.Add(directCachePath);
                }

                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                    {
                        var cachePath = Path.Combine(userDir, "config", "librarycache", fileName);
                        if (File.Exists(cachePath))
                        {
                            candidates.Add(cachePath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM LIBRARYCACHE SEARCH ERROR: root={userdataRoot} msg={ex.Message}");
                }
            }

            return candidates;
        }

        private IEnumerable<string> GetSteamAchievementProgressFilePaths()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var userdataRoot in GetSteamUserdataRoots())
            {
                if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
                {
                    continue;
                }

                var directConfigPath = Path.Combine(userdataRoot, "config", "librarycache", "achievement_progress.json");
                if (File.Exists(directConfigPath))
                {
                    candidates.Add(directConfigPath);
                }

                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                    {
                        var progressPath = Path.Combine(userDir, "config", "librarycache", "achievement_progress.json");
                        if (File.Exists(progressPath))
                        {
                            candidates.Add(progressPath);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM USERDATA SEARCH ERROR: root={userdataRoot} msg={ex.Message}");
                }
            }

            return candidates;
        }

        private IEnumerable<string> GetSteamUserdataRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var configuredUserdataPath = GetConfiguredSteamUserdataPath();
            if (!string.IsNullOrWhiteSpace(configuredUserdataPath))
            {
                roots.Add(configuredUserdataPath);
            }

            foreach (var candidate in GetSteamInstallCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    continue;
                }

                var configLibraryCachePath = Path.Combine(expanded, "config", "librarycache");
                if (Directory.Exists(configLibraryCachePath))
                {
                    roots.Add(expanded);
                    continue;
                }

                if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(expanded);
                    continue;
                }

                var userdataPath = Path.Combine(expanded, "userdata");
                if (Directory.Exists(userdataPath))
                {
                    roots.Add(userdataPath);
                }
            }

            return roots;
        }

        private IEnumerable<string> GetSteamInstallRoots()
        {
            var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var configuredPath = GetConfiguredSteamBasePath();
            if (!string.IsNullOrWhiteSpace(configuredPath) && Directory.Exists(configuredPath))
            {
                roots.Add(configuredPath);
            }

            foreach (var candidate in GetSteamInstallCandidates())
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
                if (string.IsNullOrWhiteSpace(expanded))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(expanded, "appcache", "stats")) || Directory.Exists(Path.Combine(expanded, "userdata")))
                {
                    roots.Add(expanded);
                }
            }

            return roots;
        }

        private string GetConfiguredSteamBasePath()
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            var configuredPath = settings?.SteamUserdataPath?.Trim();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return null;
            }

            if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(expanded);
                return parent?.FullName;
            }

            return expanded;
        }

        private string GetConfiguredSteamUserdataPath()
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            var configuredPath = settings?.SteamUserdataPath?.Trim();
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return null;
            }

            var expanded = Environment.ExpandEnvironmentVariables(configuredPath);
            if (string.IsNullOrWhiteSpace(expanded))
            {
                return null;
            }

            if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase) && Directory.Exists(expanded))
            {
                return expanded;
            }

            var userdataPath = Path.Combine(expanded, "userdata");
            return Directory.Exists(userdataPath)
                ? userdataPath
                : expanded;
        }

        private IEnumerable<string> GetSteamInstallCandidates()
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Environment.GetEnvironmentVariable("SteamPath"),
                @"%ProgramFiles(x86)%\Steam",
                @"%ProgramFiles%\Steam"
            };

            foreach (var drive in Environment.GetLogicalDrives())
            {
                candidates.Add(Path.Combine(drive, "Program Files (x86)", "Steam"));
                candidates.Add(Path.Combine(drive, "Program Files", "Steam"));
                candidates.Add(Path.Combine(drive, "Programs", "Steam"));
                candidates.Add(Path.Combine(drive, "Steam"));
            }

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                candidates.Add(root);
            }

            return candidates;
        }

        private async Task<SchemaAndPercentages> TryGetSteamSchemaAsync(int appId)
        {
            if (_steamSchemaCache.TryGetValue(appId, out var cached))
            {
                return cached;
            }

            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            var apiKey = steamSettings?.SteamApiKey?.Trim();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _steamSchemaCache[appId] = null;
                return null;
            }

            try
            {
                using var httpClient = new HttpClient();
                var apiClient = new SteamApiClient(httpClient, _logger);
                var language = string.IsNullOrWhiteSpace(_pluginSettings?.Persisted?.GlobalLanguage)
                    ? "english"
                    : _pluginSettings.Persisted.GlobalLanguage.Trim();

                var schema = await apiClient.GetSchemaForGameDetailedAsync(apiKey, appId, language, CancellationToken.None).ConfigureAwait(false);
                _steamSchemaCache[appId] = schema;
                return schema;
            }
            catch (Exception ex)
            {
                Log($"STEAM SCHEMA ERROR: appId={appId} msg={ex.Message}");
                _steamSchemaCache[appId] = null;
                return null;
            }
        }

        private static double? NormalizePercent(double? rawPercent)
        {
            if (!rawPercent.HasValue || double.IsNaN(rawPercent.Value) || double.IsInfinity(rawPercent.Value))
            {
                return null;
            }

            return Math.Max(0d, Math.Min(100d, rawPercent.Value));
        }

        private IEnumerable<string> GetLocalRootPaths()
        {
            var roots = new List<string>();
            var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var publicFolder = Environment.GetEnvironmentVariable("PUBLIC") ?? string.Empty;

            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\Goldberg SteamEmu Saves"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\GSE Saves"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\EMPRESS"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\Steam\CODEX"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\SmartSteamEmu"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%APPDATA%\CreamAPI"));

            if (!string.IsNullOrWhiteSpace(publicFolder))
            {
                roots.Add(Path.Combine(publicFolder, "Documents", "OnlineFix"));
                roots.Add(Path.Combine(publicFolder, "Documents", "Steam", "RUNE"));
                roots.Add(Path.Combine(publicFolder, "Documents", "Steam", "CODEX"));
                roots.Add(Path.Combine(publicFolder, "EMPRESS"));
            }

            if (!string.IsNullOrWhiteSpace(documents))
            {
                roots.Add(Path.Combine(documents, "SkidRow"));
            }

            if (!string.IsNullOrWhiteSpace(commonAppData))
            {
                roots.Add(Path.Combine(commonAppData, "Steam"));
            }

            roots.Add(Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Steam"));
            roots.Add(Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Steam"));

            var steamPath = Environment.GetEnvironmentVariable("SteamPath");
            if (!string.IsNullOrWhiteSpace(steamPath))
            {
                roots.Add(steamPath);
                roots.Add(Path.Combine(steamPath, "userdata"));
            }

            roots.Add(Path.Combine(localAppData, "SKIDROW"));

            if (_pluginSettings?.Persisted?.ExtraLocalPaths != null)
            {
                var extra = _pluginSettings.Persisted.ExtraLocalPaths
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var raw in extra)
                {
                    var trimmed = raw.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                    {
                        continue;
                    }

                    var expanded = Environment.ExpandEnvironmentVariables(trimmed);
                    roots.Add(expanded);
                }
            }

            return roots.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        // REQUIRED: Returning a real settings object makes the platform appear in the UI Filters
        public IProviderSettings GetSettings()
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();

            if (string.IsNullOrWhiteSpace(settings.ExtraLocalPaths)
                && !string.IsNullOrWhiteSpace(_pluginSettings?.Persisted?.ExtraLocalPaths))
            {
                settings.ExtraLocalPaths = _pluginSettings.Persisted.ExtraLocalPaths;
            }

            settings.SteamUserdataPath ??= string.Empty;
            settings.SteamAppIdOverrides ??= new Dictionary<Guid, int>();
            settings.LocalFolderOverrides ??= new Dictionary<Guid, string>();

            return settings;
        }

        public void ApplySettings(IProviderSettings settings)
        {
            if (settings is LocalSettings localSettings)
            {
                if (_pluginSettings?.Persisted != null)
                {
                    _pluginSettings.Persisted.ExtraLocalPaths = localSettings.ExtraLocalPaths ?? string.Empty;
                }
            }
        }

        public ProviderSettingsViewBase CreateSettingsView() => new LocalSettingsView(_api);

        private static readonly Dictionary<string, string> KnownIniFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["earned"] = "earned",
            ["unlocked"] = "unlocked",
            ["achieved"] = "achieved",
            ["unlocktime"] = "unlocktime",
            ["unlock_time"] = "unlocktime",
            ["earned_time"] = "earned_time",
            ["timestamp"] = "timestamp",
            ["time"] = "time",
            ["displayname"] = "displayname",
            ["description"] = "description",
            ["icon"] = "icon",
            ["icongray"] = "icongray",
            ["icon_gray"] = "icongray",
            ["lockedicon"] = "lockedicon",
            ["hidden"] = "hidden",
            ["percent"] = "percent"
        };

        private static readonly Dictionary<string, string> KnownIniFieldSuffixes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".earned"] = "earned",
            [".unlocked"] = "unlocked",
            [".achieved"] = "achieved",
            [".unlocktime"] = "unlocktime",
            [".unlock_time"] = "unlocktime",
            [".earned_time"] = "earned_time",
            [".timestamp"] = "timestamp",
            [".time"] = "time",
            [".displayname"] = "displayname",
            [".description"] = "description",
            [".icon"] = "icon",
            [".icongray"] = "icongray",
            [".hidden"] = "hidden",
            [".percent"] = "percent",
            ["_earned"] = "earned",
            ["_unlocked"] = "unlocked",
            ["_achieved"] = "achieved",
            ["_unlocktime"] = "unlocktime",
            ["_unlock_time"] = "unlocktime",
            ["_earned_time"] = "earned_time",
            ["_timestamp"] = "timestamp",
            ["_time"] = "time",
            ["_hidden"] = "hidden",
            ["_percent"] = "percent"
        };

        private static readonly HashSet<string> GenericIniSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            string.Empty,
            "default",
            "general",
            "steam",
            "achievements",
            "steamachievements",
            "stats",
            "steamstats",
            "steamuserstats"
        };

        private struct LocalEntry
        {
            public bool earned { get; set; }
            public long earned_time { get; set; }
            public string displayName { get; set; }
            public string description { get; set; }
            public string icon { get; set; }
            public string iconGray { get; set; }
            public bool hidden { get; set; }
            public double? percent { get; set; }
        }

        private sealed class SteamLocalProgressSummary
        {
            public int AppId { get; set; }
            public int TotalCount { get; set; }
            public int UnlockedCount { get; set; }
            public string SourcePath { get; set; }
        }

        private sealed class SteamLocalProgressPayload
        {
            public int appid { get; set; }
            public int unlocked { get; set; }
            public int total { get; set; }
        }

        private sealed class SteamAppCacheSchemaEntry
        {
            public int Index { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public bool Hidden { get; set; }
            public string IconHash { get; set; }
            public string IconGrayHash { get; set; }
        }
    }
}