using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;

namespace PlayniteAchievements.Providers.Local
{
    public class LocalSavesProvider : IDataProvider
    {
        private static readonly HashSet<Guid> ReportedAmbiguousFolderGames = new HashSet<Guid>();
        private static readonly Regex GenericAchievementNamePattern = new Regex(
            @"^(ach(ieve(ment)?)?|stat|unlock|trophy|badge)[_\-\s]?\d+$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SteamHuntersPublicSteamIdRegex = new Regex(
            @"""privacyState"":(?<privacy>\d+).*?""steamId"":""(?<id>\d{17})""",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
        private static readonly string[] InstallSchemaRelativePaths =
        {
            @"steam_settings\achievements.json",
            @"steam_settings\settings\achievements.json",
            @"steam_settings\stats\achievements.json",
            @"achievement\achievements.json",
            @"achievements.json"
        };

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
        private readonly Dictionary<int, string> _steamSchemaSourceCache = new Dictionary<int, string>();
        private readonly object _discoveryCacheLock = new object();
        private readonly Dictionary<string, IReadOnlyList<string>> _localFolderCandidatesCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, IReadOnlyList<string>> _steamAppCacheSchemaFilePathsCache = new Dictionary<int, IReadOnlyList<string>>();
        private readonly Dictionary<int, IReadOnlyList<string>> _steamAppCacheUserStatsFilePathsCache = new Dictionary<int, IReadOnlyList<string>>();
        private readonly Dictionary<int, IReadOnlyList<string>> _steamLibraryCacheFilePathsCache = new Dictionary<int, IReadOnlyList<string>>();
        private IReadOnlyList<string> _steamAchievementProgressFilePathsCache;
        private IReadOnlyList<string> _steamUserdataRootsCache;
        private IReadOnlyList<string> _steamInstallRootsCache;
        private HashSet<int> _steamLocalProgressAppIdsCache;

        private string ResolvedProviderIconKey
        {
            get
            {
                var customIconPath = ProviderRegistry.Settings<LocalSettings>()?.CustomProviderIconPath;
                return !string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath)
                    ? customIconPath
                    : "ProviderIconLocal";
            }
        }

        public string ProviderKey => "Local";
        public string ProviderName => "Local"; 
        public string ProviderIconKey => ResolvedProviderIconKey;
        public string ProviderColorHex => "#FF8A00";

        public bool IsAuthenticated => true;
        public ISessionManager AuthSession => null;

        private void Log(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
            {
                return;
            }

            _logger?.Info($"[Local] {msg}");
        }

        public LocalSavesProvider(IPlayniteAPI playniteApi, ILogger logger, PlayniteAchievementsSettings settings)
        {
            _api = playniteApi;
            _logger = logger;
            _pluginSettings = settings; // Store the full settings object
            Log("=== Provider Starting V9 (Discovery Mode) ===");
        }

        public bool IsCapable(Game game)
        {
            if (!TryResolveAppId(game, out var appId, out _ ) || appId <= 0)
            {
                return false;
            }

            var appIdText = appId.ToString(CultureInfo.InvariantCulture);
            if (TryResolveLocalFolder(game, appIdText, out _, out _, out _, out _))
            {
                return true;
            }

            if (GetSteamAppCacheSchemaFilePaths(appId).Any())
            {
                return true;
            }

            if (GetSteamAppCacheUserStatsFilePaths(appId).Any())
            {
                return true;
            }

            if (GetSteamLibraryCacheFilePaths(appId).Any())
            {
                return true;
            }

            if (HasSteamAchievementProgressForApp(appId))
            {
                return true;
            }

            return SupportsSchemaOnlyManualFallback(game);
        }

        public async Task<GameAchievementData> GetAchievementsAsync(Game game, RefreshRequest request)
        {
            var appId = GetAppId(game, out var isAppIdOverridden);
            if (string.IsNullOrEmpty(appId)) return null;

            var hasResolvedLocalFolder = TryResolveLocalFolder(game, appId, out var localFolderPath, out _, out _, out _);

            string jsonPath = null;
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                jsonPath = ResolveAchievementFilePath(localFolderPath, "achievements.json");
            }

            string iniPath = null;
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                iniPath = ResolveAchievementFilePath(localFolderPath, "achievements.ini");
            }

            var hasAchievementsFile = !string.IsNullOrWhiteSpace(jsonPath) || !string.IsNullOrWhiteSpace(iniPath);

            SchemaAndPercentages steamSchema = null;
            string steamSchemaSource = null;
            var apiNameMap = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            var schemaLookupByText = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            var schemaLookupByTitle = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            int appIdInt = 0;
            if (int.TryParse(appId, out appIdInt))
            {
                steamSchema = await TryGetSteamSchemaAsync(appIdInt).ConfigureAwait(false);
                steamSchemaSource = _steamSchemaSourceCache.TryGetValue(appIdInt, out var resolvedSource)
                    ? resolvedSource
                    : null;
                if (steamSchema?.Achievements != null)
                {
                    apiNameMap = steamSchema.Achievements
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                        .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
                    schemaLookupByText = BuildSchemaLookupByText(steamSchema.Achievements);
                    schemaLookupByTitle = BuildSchemaLookupByTitle(steamSchema.Achievements);
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

            var hasSchemaAchievements = steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0;

            if (!hasResolvedLocalFolder &&
                !hasSchemaAchievements &&
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
                        if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                        {
                            raw = RemapGenericAchievementEntries(raw, steamSchema.Achievements);
                        }

                        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var shouldExpandSchemaFirst = ShouldExpandSchemaAchievementsForLocalEntries(steamSchemaSource);
                        var correlatedCount = 0;

                        if (shouldExpandSchemaFirst && steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
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

                        var remaining = raw.Where(kv => !added.Contains(kv.Key));
                        if (shouldExpandSchemaFirst && steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                        {
                            remaining = remaining.Where(kv => !IsGenericAchievementId(kv.Key));
                        }

                        foreach (var kv in remaining)
                        {
                            var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                            if (schemaAch != null)
                            {
                                correlatedCount++;
                            }

                            data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        }

                        if (!shouldExpandSchemaFirst && steamSchema?.Achievements?.Count > 0)
                        {
                            Log($"SCHEMA CORRELATION: appId={appIdInt} source={steamSchemaSource ?? "unknown"} localEntries={raw.Count} matched={correlatedCount}");
                            if (correlatedCount == 0)
                            {
                                Log($"SCHEMA CORRELATION LIMITATION: appId={appIdInt} source={steamSchemaSource ?? "unknown"} localEntries={raw.Count} reason=title-only-source-no-match");
                            }
                        }

                        PreserveCachedLocalMetadata(data);
                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from local save data. schemaSource={steamSchemaSource ?? "none"}");
                        return data;
                    }
                }

                if (steamAppCacheEntries != null && steamAppCacheEntries.Count > 0)
                {
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamAppCacheEntries)
                    {
                        var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        added.Add(kv.Key);
                    }

                    PreserveCachedLocalMetadata(data);
                    Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from Steam appcache stats.");
                    return data;
                }

                if (steamLibraryCacheEntries != null && steamLibraryCacheEntries.Count > 0)
                {
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamLibraryCacheEntries)
                    {
                        var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema));
                        added.Add(kv.Key);
                    }

                    PreserveCachedLocalMetadata(data);
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
                            UnlockedIconPath = schemaAch.Icon ?? AchievementIconResolver.GetDefaultUnlockedIcon(),
                            LockedIconPath = schemaAch.IconGray ?? AchievementIconResolver.GetDefaultIcon(),
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

                    PreserveCachedLocalMetadata(data);
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

        private bool SupportsSchemaOnlyManualFallback(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (TryGetAppIdOverride(game.Id, out _) || TryGetFolderOverride(game.Id, out _))
            {
                return true;
            }

            if (_pluginSettings?.Persisted?.PreferredProviderOverrides != null &&
                _pluginSettings.Persisted.PreferredProviderOverrides.TryGetValue(game.Id, out var preferredProvider) &&
                string.Equals(preferredProvider?.Trim(), ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (game.PluginId != Guid.Empty)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(game.GameId) && Regex.IsMatch(game.GameId, @"^\d+$"))
            {
                return true;
            }

            if (game.Links != null && game.Links.Any(link => Regex.IsMatch(link?.Url ?? string.Empty, @"/app/(\d+)")))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(game.Notes) &&
                Regex.IsMatch(game.Notes, @"SteamID[:\s]+(\d+)", RegexOptions.IgnoreCase))
            {
                return true;
            }

            return false;
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

        private string GetSelectedSteamAppCacheUserId()
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            return (settings?.SteamAppCacheUserId ?? string.Empty).Trim();
        }

        private bool IsSteamAppCacheDisabled()
        {
            return string.Equals(GetSelectedSteamAppCacheUserId(), LocalSettings.SteamAppCacheUserNone, StringComparison.OrdinalIgnoreCase);
        }

        private IEnumerable<string> GetSteamAppCacheSchemaFilePaths(int appId)
        {
            lock (_discoveryCacheLock)
            {
                if (_steamAppCacheSchemaFilePathsCache.TryGetValue(appId, out var cachedPaths))
                {
                    return cachedPaths;
                }
            }

            if (IsSteamAppCacheDisabled())
            {
                var empty = Array.Empty<string>();
                lock (_discoveryCacheLock)
                {
                    _steamAppCacheSchemaFilePathsCache[appId] = empty;
                }

                return empty;
            }

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

            var resolvedCandidates = candidates.ToList();
            lock (_discoveryCacheLock)
            {
                _steamAppCacheSchemaFilePathsCache[appId] = resolvedCandidates;
            }

            return resolvedCandidates;
        }

        private IEnumerable<string> GetSteamAppCacheUserStatsFilePaths(int appId)
        {
            lock (_discoveryCacheLock)
            {
                if (_steamAppCacheUserStatsFilePathsCache.TryGetValue(appId, out var cachedPaths))
                {
                    return cachedPaths;
                }
            }

            if (IsSteamAppCacheDisabled())
            {
                var empty = Array.Empty<string>();
                lock (_discoveryCacheLock)
                {
                    _steamAppCacheUserStatsFilePathsCache[appId] = empty;
                }

                return empty;
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffix = "_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin";
            var preferredAccountIds = GetPreferredSteamAccountIds().ToList();
            var hasPreferredAccountIds = preferredAccountIds.Count > 0;

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

                    if (!hasPreferredAccountIds)
                    {
                        foreach (var path in Directory.EnumerateFiles(statsRoot, "UserGameStats_*" + suffix, SearchOption.TopDirectoryOnly))
                        {
                            candidates.Add(path);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE USERSTATS SEARCH ERROR: root={statsRoot} msg={ex.Message}");
                }
            }

            var resolvedCandidates = candidates.ToList();
            lock (_discoveryCacheLock)
            {
                _steamAppCacheUserStatsFilePathsCache[appId] = resolvedCandidates;
            }

            return resolvedCandidates;
        }

        private IEnumerable<string> GetPreferredSteamAccountIds()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var selectedSteamAppCacheUserId = GetSelectedSteamAppCacheUserId();
            if (string.Equals(selectedSteamAppCacheUserId, LocalSettings.SteamAppCacheUserNone, StringComparison.OrdinalIgnoreCase))
            {
                return ids;
            }

            if (!string.IsNullOrWhiteSpace(selectedSteamAppCacheUserId))
            {
                foreach (var candidate in ExpandSteamAccountIdCandidates(selectedSteamAppCacheUserId))
                {
                    ids.Add(candidate);
                }

                return ids;
            }

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
            var localDisplayName = entry.displayName?.Trim();
            var schemaDisplayName = schemaAch?.DisplayName?.Trim();
            var displayName = !string.IsNullOrWhiteSpace(schemaDisplayName) && IsLowQualityDisplayName(localDisplayName, apiName)
                ? schemaDisplayName
                : (!string.IsNullOrWhiteSpace(localDisplayName)
                    ? localDisplayName
                    : schemaDisplayName ?? apiName);

            var localDescription = entry.description?.Trim();
            var schemaDescription = schemaAch?.Description?.Trim();
            var description = !string.IsNullOrWhiteSpace(schemaDescription) && IsLowQualityDescription(localDescription)
                ? schemaDescription
                : (!string.IsNullOrWhiteSpace(localDescription)
                    ? localDescription
                    : schemaDescription ?? "Local achievement from Local");

            var unlockedIcon = !string.IsNullOrWhiteSpace(schemaAch?.Icon) && IsLowQualityIconPath(entry.icon, isLockedIcon: false)
                ? schemaAch.Icon
                : (!string.IsNullOrWhiteSpace(entry.icon)
                    ? entry.icon
                    : schemaAch?.Icon ?? AchievementIconResolver.GetDefaultUnlockedIcon());

            var lockedIcon = !string.IsNullOrWhiteSpace(schemaAch?.IconGray) && IsLowQualityIconPath(entry.iconGray, isLockedIcon: true)
                ? schemaAch.IconGray
                : (!string.IsNullOrWhiteSpace(entry.iconGray)
                    ? entry.iconGray
                    : schemaAch?.IconGray ?? AchievementIconResolver.GetDefaultIcon());

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

        private static SchemaAchievement ResolveSchemaAchievement(
            string key,
            LocalEntry entry,
            IReadOnlyDictionary<string, SchemaAchievement> apiNameMap,
            IReadOnlyDictionary<string, SchemaAchievement> schemaLookupByText,
            IReadOnlyDictionary<string, SchemaAchievement> schemaLookupByTitle)
        {
            if (!string.IsNullOrWhiteSpace(key) && apiNameMap != null && apiNameMap.TryGetValue(key, out var byApiName))
            {
                return byApiName;
            }

            var textLookupKey = BuildAchievementLookupKey(entry.displayName, entry.description);
            if (!string.IsNullOrWhiteSpace(textLookupKey) &&
                schemaLookupByText != null &&
                schemaLookupByText.TryGetValue(textLookupKey, out var byText))
            {
                return byText;
            }

            var titleLookupKey = BuildAchievementTitleLookupKey(entry.displayName);
            if (!string.IsNullOrWhiteSpace(titleLookupKey) &&
                schemaLookupByTitle != null &&
                schemaLookupByTitle.TryGetValue(titleLookupKey, out var byTitle))
            {
                return byTitle;
            }

            return null;
        }

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByText(IReadOnlyList<SchemaAchievement> achievements)
        {
            var result = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return result;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var lookupKey = BuildAchievementLookupKey(achievement.DisplayName, achievement.Description);
                if (!string.IsNullOrWhiteSpace(lookupKey) && !result.ContainsKey(lookupKey))
                {
                    result[lookupKey] = achievement;
                }
            }

            return result;
        }

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByTitle(IReadOnlyList<SchemaAchievement> achievements)
        {
            var titleGroups = new Dictionary<string, List<SchemaAchievement>>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                var lookupKey = BuildAchievementTitleLookupKey(achievement.DisplayName);
                if (string.IsNullOrWhiteSpace(lookupKey))
                {
                    continue;
                }

                if (!titleGroups.TryGetValue(lookupKey, out var bucket))
                {
                    bucket = new List<SchemaAchievement>();
                    titleGroups[lookupKey] = bucket;
                }

                bucket.Add(achievement);
            }

            return titleGroups
                .Where(group => group.Value.Count == 1)
                .ToDictionary(group => group.Key, group => group.Value[0], StringComparer.OrdinalIgnoreCase);
        }

        private static bool ShouldExpandSchemaAchievementsForLocalEntries(string schemaSource)
        {
            return !string.Equals(schemaSource, "steam-community", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(schemaSource, "completionist", StringComparison.OrdinalIgnoreCase);
        }

        private static void MergeSchemaMetadata(IReadOnlyList<SchemaAchievement> targetAchievements, IReadOnlyList<SchemaAchievement> sourceAchievements)
        {
            if (targetAchievements == null || sourceAchievements == null || targetAchievements.Count == 0 || sourceAchievements.Count == 0)
            {
                return;
            }

            var sourceByApiName = sourceAchievements
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.Name))
                .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var sourceByText = BuildSchemaLookupByText(sourceAchievements);
            var sourceByTitle = BuildSchemaLookupByTitle(sourceAchievements);

            foreach (var target in targetAchievements)
            {
                if (target == null)
                {
                    continue;
                }

                SchemaAchievement source = null;
                if (!string.IsNullOrWhiteSpace(target.Name))
                {
                    sourceByApiName.TryGetValue(target.Name, out source);
                }

                if (source == null)
                {
                    var lookupKey = BuildAchievementLookupKey(target.DisplayName, target.Description);
                    if (!string.IsNullOrWhiteSpace(lookupKey))
                    {
                        sourceByText.TryGetValue(lookupKey, out source);
                    }
                }

                if (source == null)
                {
                    var titleLookupKey = BuildAchievementTitleLookupKey(target.DisplayName);
                    if (!string.IsNullOrWhiteSpace(titleLookupKey))
                    {
                        sourceByTitle.TryGetValue(titleLookupKey, out source);
                    }
                }

                if (source == null)
                {
                    continue;
                }

                if (IsLowQualityDisplayName(target.DisplayName, target.Name) && !IsLowQualityDisplayName(source.DisplayName, source.Name))
                {
                    target.DisplayName = source.DisplayName;
                }

                if (IsLowQualityDescription(target.Description) && !IsLowQualityDescription(source.Description))
                {
                    target.Description = source.Description;
                }

                if (IsLowQualityIconPath(target.Icon, isLockedIcon: false) && !IsLowQualityIconPath(source.Icon, isLockedIcon: false))
                {
                    target.Icon = source.Icon;
                }

                if (IsLowQualityIconPath(target.IconGray, isLockedIcon: true) && !IsLowQualityIconPath(source.IconGray, isLockedIcon: true))
                {
                    target.IconGray = source.IconGray;
                }

                if (target.Hidden != 1 && source.Hidden == 1)
                {
                    target.Hidden = 1;
                }
            }
        }

        private void PreserveCachedLocalMetadata(GameAchievementData data)
        {
            if (data?.PlayniteGameId == null || data.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            var previousLocalData = TryLoadCachedLocalGameData(data.PlayniteGameId.Value);
            if (previousLocalData == null)
            {
                previousLocalData = TryLoadCachedProviderGameData(data.PlayniteGameId.Value, "Steam");
            }
            if (previousLocalData == null && data.AppId > 0)
            {
                previousLocalData = TryLoadCachedProviderGameDataByAppId(data.AppId, ProviderKey);
            }
            if (previousLocalData == null && data.AppId > 0)
            {
                previousLocalData = TryLoadCachedProviderGameDataByAppId(data.AppId, "Steam");
            }

            var previousAchievements = previousLocalData?.Achievements;
            if (previousAchievements == null || previousAchievements.Count == 0)
            {
                return;
            }

            var previousByApiName = previousAchievements
                .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.ApiName))
                .ToDictionary(achievement => achievement.ApiName, achievement => achievement, StringComparer.OrdinalIgnoreCase);

            foreach (var achievement in data.Achievements)
            {
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                if (!previousByApiName.TryGetValue(achievement.ApiName, out var previousAchievement) || previousAchievement == null)
                {
                    continue;
                }

                if (ShouldPreserveDisplayName(achievement, previousAchievement))
                {
                    achievement.DisplayName = previousAchievement.DisplayName;
                }

                if (ShouldPreserveDescription(achievement, previousAchievement))
                {
                    achievement.Description = previousAchievement.Description;
                }

                if (ShouldPreserveUnlockedIcon(achievement, previousAchievement))
                {
                    achievement.UnlockedIconPath = previousAchievement.UnlockedIconPath;
                }

                if (ShouldPreserveLockedIcon(achievement, previousAchievement))
                {
                    achievement.LockedIconPath = previousAchievement.LockedIconPath;
                }
            }
        }

        private GameAchievementData TryLoadCachedLocalGameData(Guid playniteGameId)
        {
            return TryLoadCachedProviderGameData(playniteGameId, ProviderKey);
        }

        private GameAchievementData TryLoadCachedProviderGameData(Guid playniteGameId, string providerKey)
        {
            try
            {
                var cacheManager = PlayniteAchievementsPlugin.Instance?.RefreshRuntime?.Cache as CacheManager;
                return cacheManager?.LoadGameData(playniteGameId.ToString(), providerKey);
            }
            catch (Exception ex)
            {
                Log($"LOCAL CACHE MERGE ERROR: gameId={playniteGameId} provider={providerKey} msg={ex.Message}");
                return null;
            }
        }

        private GameAchievementData TryLoadCachedProviderGameDataByAppId(int appId, string providerKey)
        {
            if (appId <= 0)
            {
                return null;
            }

            try
            {
                var cacheManager = PlayniteAchievementsPlugin.Instance?.RefreshRuntime?.Cache as CacheManager;
                return cacheManager?.LoadGameData($"app:{appId}", providerKey);
            }
            catch (Exception ex)
            {
                Log($"LOCAL CACHE APPID MERGE ERROR: appId={appId} provider={providerKey} msg={ex.Message}");
                return null;
            }
        }

        private static bool ShouldPreserveDisplayName(AchievementDetail incoming, AchievementDetail existing)
        {
            return IsLowQualityDisplayName(incoming) && !IsLowQualityDisplayName(existing);
        }

        private static bool ShouldPreserveDescription(AchievementDetail incoming, AchievementDetail existing)
        {
            return IsLowQualityDescription(incoming?.Description) && !IsLowQualityDescription(existing?.Description);
        }

        private static bool ShouldPreserveUnlockedIcon(AchievementDetail incoming, AchievementDetail existing)
        {
            return IsLowQualityIconPath(incoming?.UnlockedIconPath, isLockedIcon: false) &&
                   !IsLowQualityIconPath(existing?.UnlockedIconPath, isLockedIcon: false);
        }

        private static bool ShouldPreserveLockedIcon(AchievementDetail incoming, AchievementDetail existing)
        {
            return IsLowQualityIconPath(incoming?.LockedIconPath, isLockedIcon: true) &&
                   !IsLowQualityIconPath(existing?.LockedIconPath, isLockedIcon: true);
        }

        private static bool IsLowQualityDisplayName(AchievementDetail achievement)
        {
            return IsLowQualityDisplayName(achievement?.DisplayName, achievement?.ApiName);
        }

        private static bool IsLowQualityDisplayName(string displayName, string apiName)
        {
            displayName = displayName?.Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return true;
            }

            apiName = apiName?.Trim();
            if (!string.IsNullOrWhiteSpace(apiName) && string.Equals(displayName, apiName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return GenericAchievementNamePattern.IsMatch(displayName);
        }

        private static bool IsLowQualityDescription(string description)
        {
            description = description?.Trim();
            return string.IsNullOrWhiteSpace(description) ||
                   string.Equals(description, "Local achievement from Local", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, LocalEntry> RemapGenericAchievementEntries(
            Dictionary<string, LocalEntry> source,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            if (source == null || source.Count == 0 || schemaAchievements == null || schemaAchievements.Count == 0)
            {
                return source ?? new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var remapped = new Dictionary<string, LocalEntry>(source, StringComparer.OrdinalIgnoreCase);
            foreach (var kv in source)
            {
                if (!TryParseGenericAchievementIndex(kv.Key, out var oneBasedIndex))
                {
                    continue;
                }

                var zeroBasedIndex = oneBasedIndex - 1;
                if (zeroBasedIndex < 0 || zeroBasedIndex >= schemaAchievements.Count)
                {
                    continue;
                }

                var schemaName = schemaAchievements[zeroBasedIndex]?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(schemaName) || remapped.ContainsKey(schemaName))
                {
                    continue;
                }

                remapped[schemaName] = kv.Value;
            }

            return remapped;
        }

        private static bool IsGenericAchievementId(string value)
        {
            return TryParseGenericAchievementIndex(value, out _) || IsSyntheticGenericAchievementId(value);
        }

        private static bool IsSyntheticGenericAchievementId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return SyntheticGenericAchievementIds.Contains(value.Trim());
        }

        private static bool TryParseGenericAchievementIndex(string value, out int index)
        {
            index = 0;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var match = GenericNumberedAchievementPattern.Match(value.Trim());
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) && index > 0;
        }

        private static bool IsLowQualityIconPath(string iconPath, bool isLockedIcon)
        {
            iconPath = iconPath?.Trim();
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return true;
            }

            var legacyDefaultIcon = isLockedIcon
                ? "Resources/HiddenAchIcon.png"
                : "Resources/UnlockedAchIcon.png";
            var normalizedIconPath = AchievementIconResolver.NormalizeIconPath(iconPath);
            var defaultIcon = isLockedIcon
                ? AchievementIconResolver.GetDefaultIcon()
                : AchievementIconResolver.GetDefaultUnlockedIcon();

            return string.Equals(iconPath, legacyDefaultIcon, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedIconPath, defaultIcon, StringComparison.OrdinalIgnoreCase);
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

            var isGenericSection = IsGenericIniSection(section);

            if (!isGenericSection && TryParseKnownIniField(key, out fieldName))
            {
                ApplyIniField(entries, section, fieldName, value);
                return;
            }

            if (!isGenericSection || ShouldIgnoreGenericIniKey(key))
            {
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

        private static bool ShouldIgnoreGenericIniKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return true;
            }

            var normalized = key.Trim();
            if (normalized.All(char.IsDigit))
            {
                return true;
            }

            return IgnoredGenericIniKeys.Contains(normalized);
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

                    var appFolder = Path.Combine(candidate, appId);
                    candidate = ResolveAchievementFilePath(appFolder, fileName);
                    if (!string.IsNullOrWhiteSpace(candidate))
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
                        candidate = ResolveAchievementFilePath(matchDir, fileName);
                        if (!string.IsNullOrWhiteSpace(candidate))
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
            if (string.IsNullOrWhiteSpace(appId))
            {
                return new List<string>();
            }

            lock (_discoveryCacheLock)
            {
                if (_localFolderCandidatesCache.TryGetValue(appId, out var cachedFolders))
                {
                    return cachedFolders.ToList();
                }
            }

            var folders = new List<string>();

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

            var resolvedFolders = folders
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            lock (_discoveryCacheLock)
            {
                _localFolderCandidatesCache[appId] = resolvedFolders;
            }

            return resolvedFolders.ToList();
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
            if (!string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "achievements.ini")))
            {
                score += 2;
            }

            if (!string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "achievements.json")))
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
                var filePath = ResolveAchievementFilePath(folderPath, fileName);
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath))
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

        private static string ResolveAchievementFilePath(string folderPath, string fileName)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var directPath = Path.Combine(folderPath, fileName);
            if (File.Exists(directPath))
            {
                return directPath;
            }

            var statsPath = Path.Combine(folderPath, "Stats", fileName);
            if (File.Exists(statsPath))
            {
                return statsPath;
            }

            return null;
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
            lock (_discoveryCacheLock)
            {
                if (_steamLibraryCacheFilePathsCache.TryGetValue(appId, out var cachedPaths))
                {
                    return cachedPaths;
                }
            }

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

            var resolvedCandidates = candidates.ToList();
            lock (_discoveryCacheLock)
            {
                _steamLibraryCacheFilePathsCache[appId] = resolvedCandidates;
            }

            return resolvedCandidates;
        }

        private IEnumerable<string> GetSteamAchievementProgressFilePaths()
        {
            lock (_discoveryCacheLock)
            {
                if (_steamAchievementProgressFilePathsCache != null)
                {
                    return _steamAchievementProgressFilePathsCache;
                }
            }

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

            var resolvedCandidates = candidates.ToList();
            lock (_discoveryCacheLock)
            {
                _steamAchievementProgressFilePathsCache = resolvedCandidates;
            }

            return resolvedCandidates;
        }

        private bool HasSteamAchievementProgressForApp(int appId)
        {
            if (appId <= 0)
            {
                return false;
            }

            var cachedAppIds = _steamLocalProgressAppIdsCache;
            if (cachedAppIds == null)
            {
                cachedAppIds = LoadSteamAchievementProgressAppIds();
                _steamLocalProgressAppIdsCache = cachedAppIds;
            }

            return cachedAppIds.Contains(appId);
        }

        private HashSet<int> LoadSteamAchievementProgressAppIds()
        {
            var appIds = new HashSet<int>();

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
                        if (entryAppId.HasValue && entryAppId.Value > 0)
                        {
                            appIds.Add(entryAppId.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM LOCAL PROGRESS APPID CACHE ERROR: path={progressFilePath} msg={ex.Message}");
                }
            }

            return appIds;
        }

        private IEnumerable<string> GetSteamUserdataRoots()
        {
            lock (_discoveryCacheLock)
            {
                if (_steamUserdataRootsCache != null)
                {
                    return _steamUserdataRootsCache;
                }
            }

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

            var resolvedRoots = roots.ToList();
            lock (_discoveryCacheLock)
            {
                _steamUserdataRootsCache = resolvedRoots;
            }

            return resolvedRoots;
        }

        private IEnumerable<string> GetSteamInstallRoots()
        {
            lock (_discoveryCacheLock)
            {
                if (_steamInstallRootsCache != null)
                {
                    return _steamInstallRootsCache;
                }
            }

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

            var resolvedRoots = roots.ToList();
            lock (_discoveryCacheLock)
            {
                _steamInstallRootsCache = resolvedRoots;
            }

            return resolvedRoots;
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
            var schemaPreference = GetSteamSchemaPreference();
            if (_steamSchemaCache.TryGetValue(appId, out var cached) && cached != null)
            {
                var cachedSource = _steamSchemaSourceCache.TryGetValue(appId, out var source) ? source : null;
                if (IsSchemaSourceCompatibleWithPreference(cachedSource, schemaPreference))
                {
                    return cached;
                }

                _steamSchemaCache.Remove(appId);
                _steamSchemaSourceCache.Remove(appId);
            }

            var steamSettings = ProviderRegistry.Settings<SteamSettings>();
            var apiKey = steamSettings?.SteamApiKey?.Trim();
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                try
                {
                    using var httpClient = new HttpClient();
                    var apiClient = new SteamApiClient(httpClient, _logger);
                    var language = GetSteamLanguage();

                    var schema = await apiClient.GetSchemaForGameDetailedAsync(apiKey, appId, language, CancellationToken.None).ConfigureAwait(false);
                    if (schema?.Achievements?.Count > 0)
                    {
                        _steamSchemaCache[appId] = schema;
                        _steamSchemaSourceCache[appId] = "steam-api";
                        return schema;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM SCHEMA API ERROR: appId={appId} msg={ex.Message}");
                }
            }

            var appCacheSchema = TryGetSteamAppCacheSchemaAndPercentages(appId);
            if (appCacheSchema?.Achievements?.Count > 0)
            {
                _steamSchemaCache[appId] = appCacheSchema;
                _steamSchemaSourceCache[appId] = "steam-appcache";
                return appCacheSchema;
            }

            async Task<SchemaAndPercentages> TryPreferredAnonymousSchemaAsync()
            {
                switch (schemaPreference)
                {
                    case LocalSteamSchemaPreference.PreferSteamHunters:
                        return await TryGetSteamHuntersSchemaAsync(appId).ConfigureAwait(false);
                    case LocalSteamSchemaPreference.PreferSteam:
                        return await TryGetSteamHuntersSchemaAsync(appId).ConfigureAwait(false);
                    case LocalSteamSchemaPreference.PreferCompletionist:
                        using (var completionistClient = CreateAnonymousSteamHttpClient())
                        {
                            return await TryGetCompletionistSchemaAsync(completionistClient, appId).ConfigureAwait(false);
                        }
                    case LocalSteamSchemaPreference.PreferSteamCommunity:
                    default:
                        using (var communityClient = CreateAnonymousSteamHttpClient())
                        {
                            return await TryGetSteamCommunityStatsSchemaAsync(communityClient, appId).ConfigureAwait(false);
                        }
                }
            }

            var preferredAnonymousSchema = await TryPreferredAnonymousSchemaAsync().ConfigureAwait(false);
            if (preferredAnonymousSchema?.Achievements?.Count > 0)
            {
                _steamSchemaCache[appId] = preferredAnonymousSchema;
                _steamSchemaSourceCache[appId] = GetPreferredAnonymousSchemaSourceName(schemaPreference);
                Log($"SCHEMA SOURCE SELECTED: appId={appId} preference={schemaPreference} source={_steamSchemaSourceCache[appId]}");
                return preferredAnonymousSchema;
            }

            if (schemaPreference == LocalSteamSchemaPreference.PreferSteam ||
                schemaPreference == LocalSteamSchemaPreference.PreferSteamCommunity ||
                schemaPreference == LocalSteamSchemaPreference.PreferCompletionist)
            {
                var steamHuntersSchema = await TryGetSteamHuntersSchemaAsync(appId).ConfigureAwait(false);
                if (steamHuntersSchema?.Achievements?.Count > 0)
                {
                    _steamSchemaCache[appId] = steamHuntersSchema;
                    _steamSchemaSourceCache[appId] = "steamhunters";
                    Log($"SCHEMA SOURCE FALLBACK: appId={appId} preference={schemaPreference} source=steamhunters");
                    return steamHuntersSchema;
                }
            }

            if (schemaPreference == LocalSteamSchemaPreference.PreferSteamHunters ||
                schemaPreference == LocalSteamSchemaPreference.PreferCompletionist)
            {
                using var communityClient = CreateAnonymousSteamHttpClient();
                var steamCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(communityClient, appId).ConfigureAwait(false);
                if (steamCommunitySchema?.Achievements?.Count > 0)
                {
                    _steamSchemaCache[appId] = steamCommunitySchema;
                    _steamSchemaSourceCache[appId] = "steam-community";
                    Log($"SCHEMA SOURCE FALLBACK: appId={appId} preference={schemaPreference} source=steam-community");
                    return steamCommunitySchema;
                }
            }

            var installSchema = TryGetInstallSchema(appId);
            if (installSchema?.Achievements?.Count > 0)
            {
                _steamSchemaCache[appId] = installSchema;
                _steamSchemaSourceCache[appId] = "install-schema";
                return installSchema;
            }

            var publicSchema = await TryGetPublicSteamSchemaAsync(appId).ConfigureAwait(false);
            if (publicSchema?.Achievements?.Count > 0)
            {
                _steamSchemaCache[appId] = publicSchema;
                _steamSchemaSourceCache[appId] = "steam-public-profile";
                return publicSchema;
            }

            _steamSchemaCache.Remove(appId);
            _steamSchemaSourceCache.Remove(appId);
            return null;
        }

        private LocalSteamSchemaPreference GetSteamSchemaPreference()
        {
            var preference = ProviderRegistry.Settings<LocalSettings>()?.SteamSchemaPreference ?? LocalSteamSchemaPreference.PreferSteam;
            return preference == LocalSteamSchemaPreference.PreferSteamCommunity
                ? LocalSteamSchemaPreference.PreferSteamHunters
                : preference;
        }

        private static bool IsSchemaSourceCompatibleWithPreference(string source, LocalSteamSchemaPreference preference)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return false;
            }

            switch (preference)
            {
                case LocalSteamSchemaPreference.PreferSteamHunters:
                    return string.Equals(source, "steamhunters", StringComparison.OrdinalIgnoreCase);
                case LocalSteamSchemaPreference.PreferSteamCommunity:
                    return string.Equals(source, "steam-community", StringComparison.OrdinalIgnoreCase);
                case LocalSteamSchemaPreference.PreferCompletionist:
                    return string.Equals(source, "completionist", StringComparison.OrdinalIgnoreCase);
                case LocalSteamSchemaPreference.PreferSteam:
                    return !string.Equals(source, "completionist", StringComparison.OrdinalIgnoreCase) &&
                           !string.Equals(source, "steam-community", StringComparison.OrdinalIgnoreCase);
                default:
                    return !string.Equals(source, "completionist", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string GetPreferredAnonymousSchemaSourceName(LocalSteamSchemaPreference preference)
        {
            switch (preference)
            {
                case LocalSteamSchemaPreference.PreferSteamHunters:
                    return "steamhunters";
                case LocalSteamSchemaPreference.PreferSteam:
                    return "steamhunters";
                case LocalSteamSchemaPreference.PreferCompletionist:
                    return "completionist";
                case LocalSteamSchemaPreference.PreferSteamCommunity:
                default:
                    return "steam-community";
            }
        }

        private string GetSteamLanguage()
        {
            return string.IsNullOrWhiteSpace(_pluginSettings?.Persisted?.GlobalLanguage)
                ? "english"
                : _pluginSettings.Persisted.GlobalLanguage.Trim();
        }

        private async Task<SchemaAndPercentages> TryGetPublicSteamSchemaAsync(int appId)
        {
            try
            {
                using var httpClient = CreateAnonymousSteamHttpClient();
                var steamIds = await GetPublicSteamIdsFromSteamHuntersAsync(httpClient, appId).ConfigureAwait(false);
                if (steamIds.Count == 0)
                {
                    return null;
                }

                SchemaAndPercentages bestSchema = null;
                foreach (var steamId in steamIds.Take(8))
                {
                    var schema = await TryGetPublicSteamSchemaFromProfileAsync(httpClient, appId, steamId).ConfigureAwait(false);
                    if (schema?.Achievements?.Count > (bestSchema?.Achievements?.Count ?? 0))
                    {
                        bestSchema = schema;
                    }

                    if (bestSchema?.Achievements?.Count >= 25)
                    {
                        break;
                    }
                }

                if (bestSchema?.Achievements?.Count > 0)
                {
                    Log($"STEAM PUBLIC PROFILE SCHEMA: appId={appId} count={bestSchema.Achievements.Count}");
                }

                return bestSchema;
            }
            catch (Exception ex)
            {
                Log($"STEAM PUBLIC PROFILE SCHEMA ERROR: appId={appId} msg={ex.Message}");
                return null;
            }
        }

        private async Task<SchemaAndPercentages> TryGetSteamCommunityStatsSchemaAsync(HttpClient httpClient, int appId)
        {
            if (httpClient == null || appId <= 0)
            {
                return null;
            }

            try
            {
                var url = $"https://steamcommunity.com/stats/{appId}/achievements?l={GetSteamLanguage()}";
                var html = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    Log($"STEAM COMMUNITY SCHEMA: appId={appId} fetch=empty");
                    return null;
                }

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]") ??
                           doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveTxtHolder')]");
                if (rows == null || rows.Count == 0)
                {
                    Log($"STEAM COMMUNITY SCHEMA: appId={appId} rows=0");
                    return null;
                }

                var achievements = new List<SchemaAchievement>();
                var titleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in rows)
                {
                    var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        continue;
                    }

                    titleCounts[title] = titleCounts.TryGetValue(title, out var currentCount)
                        ? currentCount + 1
                        : 1;

                    var description = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? string.Empty).Trim();
                    var hidden = row.SelectSingleNode(".//div[contains(@class,'achieveHiddenBox')]") != null || string.IsNullOrWhiteSpace(description);
                    var iconUrl = row.SelectSingleNode(".//img")?.GetAttributeValue("src", string.Empty)?.Trim();

                    achievements.Add(new SchemaAchievement
                    {
                        Name = title,
                        DisplayName = title,
                        Description = description,
                        Icon = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                        IconGray = string.IsNullOrWhiteSpace(iconUrl) ? null : iconUrl,
                        Hidden = hidden ? 1 : 0
                    });
                }

                achievements = achievements
                    .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.DisplayName))
                    .Where(achievement => titleCounts.TryGetValue(achievement.DisplayName, out var count) && count == 1)
                    .ToList();

                if (achievements.Count == 0)
                {
                    return null;
                }

                Log($"STEAM COMMUNITY SCHEMA: appId={appId} rows={rows.Count} usable={achievements.Count} hidden={achievements.Count(achievement => achievement.Hidden == 1)}");
                return new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                };
            }
            catch (Exception ex)
            {
                Log($"STEAM COMMUNITY SCHEMA ERROR: appId={appId} msg={ex.Message}");
                return null;
            }
        }

        private async Task<SchemaAndPercentages> TryGetCompletionistSchemaAsync(HttpClient httpClient, int appId)
        {
            if (httpClient == null || appId <= 0)
            {
                return null;
            }

            try
            {
                var url = $"https://completionist.me/steam/app/{appId}/achievements";
                var html = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    return null;
                }

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var achievementRows = doc.DocumentNode.SelectNodes("//tr[td]");
                if (achievementRows == null || achievementRows.Count == 0)
                {
                    return null;
                }

                var achievements = new List<SchemaAchievement>();
                var percentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

                foreach (var row in achievementRows)
                {
                    var cells = row.SelectNodes("./td");
                    if (cells == null || cells.Count < 5)
                    {
                        continue;
                    }

                    var titleAndDescription = WebUtility.HtmlDecode(cells[1].InnerText ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(titleAndDescription))
                    {
                        continue;
                    }

                    var normalizedText = Regex.Replace(titleAndDescription, @"\s+", " ").Trim();
                    if (string.IsNullOrWhiteSpace(normalizedText) ||
                        normalizedText.StartsWith("Visibility", StringComparison.OrdinalIgnoreCase) ||
                        normalizedText.StartsWith("Global Unlock Percentage", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    SplitCompletionistTitleAndDescription(normalizedText, out var displayName, out var description);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var hiddenText = WebUtility.HtmlDecode(cells[2].InnerText ?? string.Empty).Trim();
                    var percentText = WebUtility.HtmlDecode(cells[3].InnerText ?? string.Empty).Trim();
                    var hidden = hiddenText.Contains("") || hiddenText.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0;

                    var iconUrl = row.SelectSingleNode(".//div[contains(@class,'image')]//img")?.GetAttributeValue("src", null)?.Trim();
                    if (string.IsNullOrWhiteSpace(iconUrl))
                    {
                        iconUrl = row.SelectSingleNode(".//img")?.GetAttributeValue("src", null)?.Trim();
                    }

                    var achievement = new SchemaAchievement
                    {
                        Name = displayName,
                        DisplayName = displayName,
                        Description = description ?? string.Empty,
                        Icon = iconUrl,
                        IconGray = iconUrl,
                        Hidden = hidden ? 1 : 0
                    };

                    if (TryParseCompletionistPercent(percentText, out var globalPercent))
                    {
                        achievement.GlobalPercent = globalPercent;
                        percentages[achievement.Name] = globalPercent;
                    }

                    achievements.Add(achievement);
                }

                achievements = achievements
                    .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.DisplayName))
                    .GroupBy(achievement => achievement.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (achievements.Count == 0)
                {
                    return null;
                }

                Log($"COMPLETIONIST SCHEMA: appId={appId} count={achievements.Count} hidden={achievements.Count(achievement => achievement.Hidden == 1)}");
                return new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = percentages
                };
            }
            catch (Exception ex)
            {
                Log($"COMPLETIONIST SCHEMA ERROR: appId={appId} msg={ex.Message}");
                return null;
            }
        }

        private static void SplitCompletionistTitleAndDescription(string text, out string displayName, out string description)
        {
            displayName = string.Empty;
            description = string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var trimmed = text.Trim();
            var sentenceBreakMatch = Regex.Match(trimmed, @"^(?<name>.+?[A-Za-z0-9!'""\):])\s+(?<desc>[A-Z0-9].+)$");
            if (sentenceBreakMatch.Success)
            {
                displayName = sentenceBreakMatch.Groups["name"].Value.Trim();
                description = sentenceBreakMatch.Groups["desc"].Value.Trim();
                return;
            }

            displayName = trimmed;
        }

        private static bool TryParseCompletionistPercent(string text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var match = Regex.Match(text, @"(?<value>\d+(?:\.\d+)?)%");
            if (!match.Success)
            {
                return false;
            }

            return double.TryParse(match.Groups["value"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private async Task<SchemaAndPercentages> TryGetSteamHuntersSchemaAsync(int appId)
        {
            try
            {
                using var httpClient = CreateAnonymousSteamHttpClient();
                var url = $"https://steamhunters.com/api/apps/{appId}/achievements";
                var payload = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(payload))
                {
                    return null;
                }

                var items = JArray.Parse(payload);
                if (items.Count == 0)
                {
                    return null;
                }

                var percentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                var achievements = items
                    .OfType<JObject>()
                    .Select(item => CreateSteamHuntersAchievement(appId, item, percentages))
                    .Where(achievement => achievement != null && !string.IsNullOrWhiteSpace(achievement.Name))
                    .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (achievements.Count == 0)
                {
                    return null;
                }

                var steamCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(httpClient, appId).ConfigureAwait(false);
                MergeSchemaMetadata(achievements, steamCommunitySchema?.Achievements);

                Log($"STEAMHUNTERS SCHEMA: appId={appId} count={achievements.Count} hidden={achievements.Count(achievement => achievement.Hidden == 1)}");
                return new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = percentages
                };
            }
            catch (Exception ex)
            {
                Log($"STEAMHUNTERS SCHEMA ERROR: appId={appId} msg={ex.Message}");
                return null;
            }
        }

        private async Task TryEnrichSteamHuntersIconsFromCommunityStatsAsync(HttpClient httpClient, int appId, List<SchemaAchievement> achievements)
        {
            if (httpClient == null || appId <= 0 || achievements == null || achievements.Count == 0)
            {
                return;
            }

            try
            {
                var url = $"https://steamcommunity.com/stats/{appId}/achievements?l={GetSteamLanguage()}";
                var html = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(html))
                {
                    return;
                }

                var iconMap = ParseSteamCommunityAchievementIconMap(html);
                if (iconMap.Count == 0)
                {
                    return;
                }

                foreach (var achievement in achievements)
                {
                    if (achievement == null)
                    {
                        continue;
                    }

                    var lookupKey = BuildAchievementLookupKey(achievement.DisplayName, achievement.Description);
                    if (!string.IsNullOrWhiteSpace(lookupKey) &&
                        iconMap.TryGetValue(lookupKey, out var exactIconUrl) &&
                        !string.IsNullOrWhiteSpace(exactIconUrl))
                    {
                        achievement.Icon = achievement.Icon ?? exactIconUrl;
                        achievement.IconGray = achievement.IconGray ?? exactIconUrl;
                        continue;
                    }

                    var titleOnlyLookupKey = BuildAchievementTitleLookupKey(achievement.DisplayName);
                    if (string.IsNullOrWhiteSpace(titleOnlyLookupKey) || !iconMap.TryGetValue(titleOnlyLookupKey, out var iconUrl) || string.IsNullOrWhiteSpace(iconUrl))
                    {
                        continue;
                    }

                    achievement.Icon = achievement.Icon ?? iconUrl;
                    achievement.IconGray = achievement.IconGray ?? iconUrl;
                }
            }
            catch (Exception ex)
            {
                Log($"STEAM COMMUNITY ICON ENRICH ERROR: appId={appId} msg={ex.Message}");
            }
        }

        private static Dictionary<string, string> ParseSteamCommunityAchievementIconMap(string html)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(html))
            {
                return result;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var rows = doc.DocumentNode.SelectNodes("//div[contains(@class,'achieveRow')]");
            if (rows == null || rows.Count == 0)
            {
                return result;
            }

            foreach (var row in rows)
            {
                var title = WebUtility.HtmlDecode(row.SelectSingleNode(".//h3")?.InnerText ?? string.Empty).Trim();
                var description = WebUtility.HtmlDecode(row.SelectSingleNode(".//h5")?.InnerText ?? string.Empty).Trim();
                var iconUrl = row.SelectSingleNode(".//div[contains(@class,'achieveImgHolder')]//img")?.GetAttributeValue("src", string.Empty)?.Trim();
                if (string.IsNullOrWhiteSpace(iconUrl))
                {
                    continue;
                }

                var key = BuildAchievementLookupKey(title, description);
                if (!string.IsNullOrWhiteSpace(key) && !result.ContainsKey(key))
                {
                    result[key] = iconUrl;
                }

                var titleOnlyKey = BuildAchievementTitleLookupKey(title);
                if (!string.IsNullOrWhiteSpace(titleOnlyKey))
                {
                    if (result.TryGetValue(titleOnlyKey, out var existingTitleIconUrl))
                    {
                        if (!string.Equals(existingTitleIconUrl, iconUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            result[titleOnlyKey] = string.Empty;
                        }
                    }
                    else
                    {
                        result[titleOnlyKey] = iconUrl;
                    }
                }
            }

            return result;
        }

        private static string BuildAchievementLookupKey(string displayName, string description)
        {
            var normalizedName = NormalizeAchievementLookupPart(displayName);
            if (string.IsNullOrWhiteSpace(normalizedName))
            {
                return null;
            }

            return normalizedName + "|" + NormalizeAchievementLookupPart(description);
        }

        private static string BuildAchievementTitleLookupKey(string displayName)
        {
            var normalizedName = NormalizeAchievementLookupPart(displayName);
            return string.IsNullOrWhiteSpace(normalizedName)
                ? null
                : "title:" + normalizedName;
        }

        private static string NormalizeAchievementLookupPart(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = WebUtility.HtmlDecode(value).Trim().ToLowerInvariant();
            value = Regex.Replace(value, @"\s+", " ");
            return value;
        }

        private static string ExtractSteamHuntersModelJson(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return null;
            }

            var varIndex = html.IndexOf("var sh", StringComparison.OrdinalIgnoreCase);
            if (varIndex < 0)
            {
                return null;
            }

            var modelIndex = html.IndexOf("model:", varIndex, StringComparison.OrdinalIgnoreCase);
            if (modelIndex < 0)
            {
                return null;
            }

            var jsonStart = html.IndexOf('{', modelIndex);
            if (jsonStart < 0)
            {
                return null;
            }

            return ExtractBalancedJsonObject(html, jsonStart);
        }

        private static string ExtractBalancedJsonObject(string text, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(text) || startIndex < 0 || startIndex >= text.Length || text[startIndex] != '{')
            {
                return null;
            }

            var depth = 0;
            var inString = false;
            var isEscaped = false;

            for (var index = startIndex; index < text.Length; index++)
            {
                var current = text[index];

                if (inString)
                {
                    if (isEscaped)
                    {
                        isEscaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        isEscaped = true;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                    }

                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                    continue;
                }

                if (current != '}')
                {
                    continue;
                }

                depth--;
                if (depth == 0)
                {
                    return text.Substring(startIndex, index - startIndex + 1);
                }
            }

            return null;
        }

        private SchemaAchievement CreateSteamHuntersAchievement(int appId, JObject item, Dictionary<string, double> percentages)
        {
            var apiName = item?["apiName"]?.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            var steamPercentage = item["steamPercentage"]?.Value<double?>() ?? item["estimatedSteamPercentage"]?.Value<double?>();
            if (steamPercentage.HasValue && !double.IsNaN(steamPercentage.Value) && !double.IsInfinity(steamPercentage.Value))
            {
                percentages[apiName] = steamPercentage.Value;
            }

            return new SchemaAchievement
            {
                Name = apiName,
                DisplayName = WebUtility.HtmlDecode(item["name"]?.Value<string>()?.Trim() ?? apiName),
                Description = WebUtility.HtmlDecode(item["description"]?.Value<string>()?.Trim() ?? string.Empty),
                Icon = ResolveSteamHuntersIcon(appId, item["icon"]?.Value<string>()),
                IconGray = ResolveSteamHuntersIcon(appId, item["iconGray"]?.Value<string>()),
                Hidden = item["hidden"]?.Value<bool?>() == true ? 1 : 0,
                GlobalPercent = steamPercentage
            };
        }

        private string ResolveSteamHuntersIcon(int appId, string iconHashOrUrl)
        {
            iconHashOrUrl = iconHashOrUrl?.Trim();
            if (string.IsNullOrWhiteSpace(iconHashOrUrl))
            {
                return null;
            }

            if (Uri.TryCreate(iconHashOrUrl, UriKind.Absolute, out var iconUri))
            {
                return iconUri.ToString();
            }

            return BuildSteamAchievementIconUrl(appId, iconHashOrUrl);
        }

        private HttpClient CreateAnonymousSteamHttpClient()
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(20)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/137.0.0.0 Safari/537.36");
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en-US"));
            client.DefaultRequestHeaders.AcceptLanguage.Add(new StringWithQualityHeaderValue("en", 0.9));
            return client;
        }

        private async Task<List<string>> GetPublicSteamIdsFromSteamHuntersAsync(HttpClient httpClient, int appId)
        {
            var url = $"https://steamhunters.com/apps/{appId}/users?sort=completionstate";
            var html = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(html))
            {
                return new List<string>();
            }

            var steamIds = new List<string>();
            foreach (Match match in SteamHuntersPublicSteamIdRegex.Matches(html))
            {
                if (!match.Success)
                {
                    continue;
                }

                if (!string.Equals(match.Groups["privacy"].Value, "0", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var steamId = match.Groups["id"].Value?.Trim();
                if (string.IsNullOrWhiteSpace(steamId) || steamIds.Contains(steamId, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                steamIds.Add(steamId);
            }

            return steamIds;
        }

        private async Task<SchemaAndPercentages> TryGetPublicSteamSchemaFromProfileAsync(HttpClient httpClient, int appId, string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return null;
            }

            try
            {
                var language = Uri.EscapeDataString(GetSteamLanguage());
                var url = $"https://steamcommunity.com/profiles/{steamId}/stats/{appId}/?xml=1&l={language}";
                var xml = await httpClient.GetStringAsync(url).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(xml) || xml.IndexOf("<playerstats", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return null;
                }

                var document = XDocument.Parse(xml);
                var achievements = document.Descendants("achievement")
                    .Select(node => new SchemaAchievement
                    {
                        Name = node.Element("apiname")?.Value?.Trim(),
                        DisplayName = node.Element("name")?.Value?.Trim(),
                        Description = node.Element("description")?.Value?.Trim(),
                        Icon = node.Element("iconOpen")?.Value?.Trim(),
                        IconGray = node.Element("iconClosed")?.Value?.Trim(),
                        Hidden = 0
                    })
                    .Where(achievement => !string.IsNullOrWhiteSpace(achievement.Name))
                    .GroupBy(achievement => achievement.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();

                if (achievements.Count == 0)
                {
                    return null;
                }

                return new SchemaAndPercentages
                {
                    Achievements = achievements,
                    GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                };
            }
            catch (Exception ex)
            {
                Log($"STEAM PUBLIC PROFILE XML ERROR: appId={appId} steamId={steamId} msg={ex.Message}");
                return null;
            }
        }

        private SchemaAndPercentages TryGetSteamAppCacheSchemaAndPercentages(int appId)
        {
            var entries = TryGetSteamAppCacheSchema(appId);
            if (entries == null || entries.Count == 0)
            {
                return null;
            }

            return new SchemaAndPercentages
            {
                Achievements = entries
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.ApiName))
                    .Select(entry => new SchemaAchievement
                    {
                        Name = entry.ApiName,
                        DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.ApiName : entry.DisplayName,
                        Description = entry.Description ?? string.Empty,
                        Hidden = entry.Hidden ? 1 : 0,
                        Icon = BuildSteamAchievementIconUrl(appId, entry.IconHash),
                        IconGray = BuildSteamAchievementIconUrl(appId, entry.IconGrayHash)
                    })
                    .ToList(),
                GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private SchemaAndPercentages TryGetInstallSchema(int appId)
        {
            foreach (var schemaPath in GetInstallSchemaFilePaths(appId))
            {
                try
                {
                    if (!File.Exists(schemaPath))
                    {
                        continue;
                    }

                    var json = File.ReadAllText(schemaPath);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    var schema = ParseInstallSchemaJson(appId, schemaPath, json);
                    if (schema?.Achievements?.Count > 0)
                    {
                        Log($"LOCAL INSTALL SCHEMA: appId={appId} path={schemaPath} count={schema.Achievements.Count}");
                        return schema;
                    }
                }
                catch (Exception ex)
                {
                    Log($"LOCAL INSTALL SCHEMA ERROR: appId={appId} path={schemaPath} msg={ex.Message}");
                }
            }

            return null;
        }

        private IEnumerable<string> GetInstallSchemaFilePaths(int appId)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                try
                {
                    foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                    {
                        if (!directory.EndsWith(appId.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        foreach (var relativePath in InstallSchemaRelativePaths)
                        {
                            var candidate = Path.Combine(directory, relativePath);
                            if (File.Exists(candidate))
                            {
                                candidates.Add(candidate);
                            }
                        }
                    }
                }
                catch
                {
                }
            }

            var game = TryGetPlayniteGameForAppId(appId);
            var installDirectory = PathExpansion.ExpandGamePath(_api, game, game?.InstallDirectory);
            if (!string.IsNullOrWhiteSpace(installDirectory) && Directory.Exists(installDirectory))
            {
                foreach (var relativePath in InstallSchemaRelativePaths)
                {
                    var candidate = Path.Combine(installDirectory, relativePath);
                    if (File.Exists(candidate))
                    {
                        candidates.Add(candidate);
                    }
                }

                try
                {
                    foreach (var nestedPath in Directory.EnumerateFiles(installDirectory, "achievements.json", SearchOption.AllDirectories))
                    {
                        if (nestedPath.IndexOf("steam_settings", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            candidates.Add(nestedPath);
                        }
                    }
                }
                catch
                {
                }
            }

            return candidates;
        }

        private Game TryGetPlayniteGameForAppId(int appId)
        {
            try
            {
                return _api?.Database?.Games?.FirstOrDefault(game =>
                    TryResolveAppId(game, out var resolvedAppId, out _) && resolvedAppId == appId);
            }
            catch
            {
                return null;
            }
        }

        private SchemaAndPercentages ParseInstallSchemaJson(int appId, string schemaPath, string json)
        {
            var token = JToken.Parse(json);
            var achievements = new List<SchemaAchievement>();

            if (token is JObject keyedObject)
            {
                foreach (var property in keyedObject.Properties())
                {
                    var achievement = ParseInstallSchemaEntry(appId, schemaPath, property.Name, property.Value);
                    if (achievement != null)
                    {
                        achievements.Add(achievement);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array.OfType<JObject>())
                {
                    var apiName = item["name"]?.Value<string>() ??
                                  item["id"]?.Value<string>() ??
                                  item["apiName"]?.Value<string>() ??
                                  item["internal_name"]?.Value<string>();
                    var achievement = ParseInstallSchemaEntry(appId, schemaPath, apiName, item);
                    if (achievement != null)
                    {
                        achievements.Add(achievement);
                    }
                }
            }

            if (achievements.Count == 0)
            {
                return null;
            }

            if (!LooksLikeSchemaPayload(achievements))
            {
                Log($"LOCAL INSTALL SCHEMA SKIPPED: appId={appId} path={schemaPath} reason=progress-only-payload");
                return null;
            }

            return new SchemaAndPercentages
            {
                Achievements = achievements,
                GlobalPercentages = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            };
        }

        private static bool LooksLikeSchemaPayload(IReadOnlyCollection<SchemaAchievement> achievements)
        {
            if (achievements == null || achievements.Count == 0)
            {
                return false;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.Name))
                {
                    continue;
                }

                var hasMeaningfulDisplayName = !IsLowQualityDisplayName(achievement.DisplayName, achievement.Name);
                var hasMeaningfulDescription = !IsLowQualityDescription(achievement.Description);
                var hasMeaningfulIcon = !IsLowQualityIconPath(achievement.Icon, isLockedIcon: false) ||
                                        !IsLowQualityIconPath(achievement.IconGray, isLockedIcon: true);

                if (hasMeaningfulDisplayName || hasMeaningfulDescription || hasMeaningfulIcon)
                {
                    return true;
                }
            }

            return false;
        }

        private SchemaAchievement ParseInstallSchemaEntry(int appId, string schemaPath, string apiName, JToken value)
        {
            apiName = apiName?.Trim();
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return null;
            }

            var entryObject = value as JObject;
            var displayName = GetLocalizedSchemaText(entryObject, "displayName", "name", "title", "localized_name") ?? apiName;
            var description = GetLocalizedSchemaText(entryObject, "description", "desc", "localized_desc") ?? string.Empty;
            var hidden = ReadBooleanLikeValue(entryObject?["hidden"]);

            var iconValue = entryObject?["icon"]?.Value<string>() ?? entryObject?["iconUnlocked"]?.Value<string>();
            var grayIconValue = entryObject?["icon_gray"]?.Value<string>() ?? entryObject?["icongray"]?.Value<string>() ?? entryObject?["iconGray"]?.Value<string>() ?? entryObject?["iconLocked"]?.Value<string>();

            return new SchemaAchievement
            {
                Name = apiName,
                DisplayName = displayName,
                Description = description,
                Hidden = hidden ? 1 : 0,
                Icon = ResolveSchemaIconPath(appId, schemaPath, iconValue),
                IconGray = ResolveSchemaIconPath(appId, schemaPath, grayIconValue)
            };
        }

        private string GetLocalizedSchemaText(JObject entryObject, params string[] candidateNames)
        {
            if (entryObject == null || candidateNames == null)
            {
                return null;
            }

            foreach (var name in candidateNames)
            {
                if (string.IsNullOrWhiteSpace(name) || !entryObject.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var token))
                {
                    continue;
                }

                var resolved = ResolveLocalizedToken(token);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }

            return null;
        }

        private string ResolveLocalizedToken(JToken token)
        {
            switch (token)
            {
                case null:
                    return null;
                case JValue value:
                    return value.Value<string>()?.Trim();
                case JObject obj:
                {
                    var language = GetSteamLanguage();
                    foreach (var candidate in EnumerateLanguageCandidates(language))
                    {
                        if (obj.TryGetValue(candidate, StringComparison.OrdinalIgnoreCase, out var localizedToken))
                        {
                            var resolved = ResolveLocalizedToken(localizedToken);
                            if (!string.IsNullOrWhiteSpace(resolved))
                            {
                                return resolved;
                            }
                        }
                    }

                    var firstValue = obj.Properties().Select(property => ResolveLocalizedToken(property.Value)).FirstOrDefault(valueText => !string.IsNullOrWhiteSpace(valueText));
                    return firstValue;
                }
                default:
                    return token.ToString().Trim();
            }
        }

        private IEnumerable<string> EnumerateLanguageCandidates(string language)
        {
            language = string.IsNullOrWhiteSpace(language) ? "english" : language.Trim();
            yield return language;

            if (language.Contains('-'))
            {
                yield return language.Split('-')[0];
            }

            if (!string.Equals(language, "english", StringComparison.OrdinalIgnoreCase))
            {
                yield return "english";
            }
        }

        private static bool ReadBooleanLikeValue(JToken token)
        {
            switch (token?.Type)
            {
                case JTokenType.Boolean:
                    return token.Value<bool>();
                case JTokenType.Integer:
                    return token.Value<int>() != 0;
                case JTokenType.String:
                    var value = token.Value<string>()?.Trim();
                    return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
                default:
                    return false;
            }
        }

        private string ResolveSchemaIconPath(int appId, string schemaPath, string iconValue)
        {
            iconValue = iconValue?.Trim();
            if (string.IsNullOrWhiteSpace(iconValue))
            {
                return null;
            }

            if (Uri.TryCreate(iconValue, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri.ToString();
            }

            if (Regex.IsMatch(iconValue, "^[0-9a-f]{8,64}$", RegexOptions.IgnoreCase))
            {
                return BuildSteamAchievementIconUrl(appId, iconValue);
            }

            var schemaDirectory = Path.GetDirectoryName(schemaPath);
            if (!string.IsNullOrWhiteSpace(schemaDirectory))
            {
                var normalized = iconValue.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var relativePath = normalized.TrimStart(Path.DirectorySeparatorChar);
                var combined = Path.GetFullPath(Path.Combine(schemaDirectory, relativePath));
                if (File.Exists(combined))
                {
                    return combined;
                }

                var parentDirectory = Directory.GetParent(schemaDirectory)?.FullName;
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    combined = Path.GetFullPath(Path.Combine(parentDirectory, relativePath));
                    if (File.Exists(combined))
                    {
                        return combined;
                    }
                }
            }

            return iconValue;
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

            foreach (var extraPath in LocalSettings.SplitExtraLocalPaths(_pluginSettings?.Persisted?.ExtraLocalPaths))
            {
                var expanded = Environment.ExpandEnvironmentVariables(extraPath);
                roots.Add(expanded);
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
            if (settings.SteamSchemaPreference == LocalSteamSchemaPreference.PreferSteamCommunity)
            {
                settings.SteamSchemaPreference = LocalSteamSchemaPreference.PreferSteamHunters;
            }

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

        public ProviderSettingsViewBase CreateSettingsView() => new LocalSettingsView(_api, _pluginSettings, _logger);

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

        private static readonly Regex GenericNumberedAchievementPattern = new Regex(
            @"^achievement_(\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly HashSet<string> SyntheticGenericAchievementIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "achievement_all",
            "all_achievements",
            "achievement_complete",
            "achievements_complete"
        };

        private static readonly HashSet<string> IgnoredGenericIniKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "count",
            "total",
            "curprogress",
            "currentprogress",
            "maxprogress",
            "progress",
            "statvalue",
            "value"
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