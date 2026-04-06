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

            if (!TryResolveLocalFolder(game, appId, out var localFolderPath, out _, out _, out _)) return null;

            var jsonPath = Path.Combine(localFolderPath, "achievements.json");
            if (!File.Exists(jsonPath))
            {
                jsonPath = null;
            }

            var iniPath = Path.Combine(localFolderPath, "achievements.ini");
            if (!File.Exists(iniPath))
            {
                iniPath = null;
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

        public ProviderSettingsViewBase CreateSettingsView() => new LocalSettingsView();

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
    }
}