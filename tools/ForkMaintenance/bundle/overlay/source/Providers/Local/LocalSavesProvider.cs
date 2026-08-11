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
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using HtmlAgilityPack;
using Microsoft.Win32;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Providers.Steam.Models;

using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;

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
        private static readonly Regex ManualAppIdInNotesRegex = new Regex(
            @"(?:steamid|appid|gameid|uplayid|ubisoftid|lumaplayid)\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SteamSchemaAppIdInNotesRegex = new Regex(
            @"(?:steamid|steamappid|steam[_\s]?app[_\s]?id)\s*[:=]\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] InstallSchemaRelativePaths =
        {
            @"steam_settings\achievements.json",
            @"steam_settings\settings\achievements.json",
            @"steam_settings\stats\achievements.json",
            @"tenoke.ini",
            @"achievement\achievements.json",
            @"achievements.json"
        };

        private static readonly string[] LocalAchievementIniFileNames =
        {
            "tenoke.ini",
            "achievements.ini",
            "user_stats.ini"
        };

        public sealed class ExpectedAchievementsDownloadResult
        {
            public bool Success { get; set; }
            public string FilePath { get; set; }
            public string Message { get; set; }
            public int AppId { get; set; }
            public bool UsedOverride { get; set; }
        }

        public enum LocalAchievementFileKind
        {
            Json,
            Ini
        }

        public sealed class LocalAchievementEditResult
        {
            public bool Success { get; set; }
            public string FilePath { get; set; }
            public LocalAchievementFileKind FileKind { get; set; }
            public int AppId { get; set; }
            public bool UsedOverride { get; set; }
            public int UpdatedCount { get; set; }
            public string Message { get; set; }
        }

        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly PlayniteAchievementsSettings _pluginSettings;
        // private readonly string debugPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Local_Debug.txt");
        private readonly Dictionary<int, SchemaAndPercentages> _steamSchemaCache = new Dictionary<int, SchemaAndPercentages>();
        private readonly Dictionary<int, string> _steamSchemaSourceCache = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _steamSchemaLanguageCache = new Dictionary<int, string>();
        // Maps Playnite game name to resolved Steam App ID for LumaPlay games (0 = not found / not applicable)
        private readonly Dictionary<string, int> _lumaPlaySteamAppIdCache = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan RealtimeRepeatedLogInterval = TimeSpan.FromMinutes(10);
        private readonly object _logThrottleLock = new object();
        private readonly Dictionary<string, DateTime> _lastRealtimeLogTimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly AsyncLocal<int> _realtimeLogScopeDepth = new AsyncLocal<int>();
        private readonly object _discoveryCacheLock = new object();
        private readonly Dictionary<string, IReadOnlyList<string>> _localFolderCandidatesCache = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, IReadOnlyList<string>> _steamAppCacheSchemaFilePathsCache = new Dictionary<int, IReadOnlyList<string>>();
        private IReadOnlyList<string> _steamUserdataRootsCache;
        private IReadOnlyList<string> _steamInstallRootsCache;
        private HashSet<int> _steamLocalProgressAppIdsCache;

        private string ResolvedProviderIconKey
        {
            get
            {
                var localSettings = ProviderRegistry.Settings<LocalSettings>();
                var customIconPath = localSettings?.CustomProviderIconPath;
                if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
                    return customIconPath;

                var borrowedKey = localSettings?.BorrowedProviderIconKey?.Trim();
                if (!string.IsNullOrWhiteSpace(borrowedKey))
                {
                    var borrowedProvider = ProviderRegistry.Instance?.GetProvider(borrowedKey);
                    if (borrowedProvider != null && !string.IsNullOrWhiteSpace(borrowedProvider.ProviderIconKey))
                        return borrowedProvider.ProviderIconKey;
                }

                return "ProviderIconLocal";
            }
        }

        public string ProviderKey => "Local";
        public string ProviderName => "Local"; 
        public string ProviderIconKey => ResolvedProviderIconKey;
        public string ProviderColorHex
        {
            get
            {
                var localSettings = ProviderRegistry.Settings<LocalSettings>();
                var customIconPath = localSettings?.CustomProviderIconPath;
                if (!string.IsNullOrWhiteSpace(customIconPath) && File.Exists(customIconPath))
                    return "#FF8A00";

                var borrowedKey = localSettings?.BorrowedProviderIconKey?.Trim();
                if (!string.IsNullOrWhiteSpace(borrowedKey))
                {
                    var borrowedProvider = ProviderRegistry.Instance?.GetProvider(borrowedKey);
                    if (borrowedProvider != null && !string.IsNullOrWhiteSpace(borrowedProvider.ProviderColorHex))
                        return borrowedProvider.ProviderColorHex;
                }

                return "#FF8A00";
            }
        }

        public bool IsAuthenticated => true;
        public ISessionManager AuthSession => null;
        public IFriendsProvider Friends => null;

        private void Log(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg))
            {
                return;
            }

            // Failures must always be recorded. Only real-time monitor refreshes throttle
            // repeated provider messages; every other Local feature keeps normal logging.
            if (_realtimeLogScopeDepth.Value > 0 &&
                msg.IndexOf("ERROR", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var now = DateTime.UtcNow;
                lock (_logThrottleLock)
                {
                    if (_lastRealtimeLogTimes.TryGetValue(msg, out var lastLogTime) &&
                        now - lastLogTime < RealtimeRepeatedLogInterval)
                    {
                        return;
                    }

                    _lastRealtimeLogTimes[msg] = now;
                }
            }

            _logger?.Info($"[Local] {msg}");
        }

        internal IDisposable BeginRealtimeLogThrottle()
        {
            _realtimeLogScopeDepth.Value++;
            return new RealtimeLogThrottleScope(this);
        }

        private sealed class RealtimeLogThrottleScope : IDisposable
        {
            private LocalSavesProvider _owner;

            public RealtimeLogThrottleScope(LocalSavesProvider owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null)
                {
                    owner._realtimeLogScopeDepth.Value = Math.Max(
                        0,
                        owner._realtimeLogScopeDepth.Value - 1);
                }
            }
        }

        private readonly SteamApiTokenService _steamApiTokenService;

        public LocalSavesProvider(IPlayniteAPI playniteApi, ILogger logger, PlayniteAchievementsSettings settings, SteamApiTokenService steamApiTokenService = null)
        {
            _api = playniteApi;
            _logger = logger;
            _pluginSettings = settings; // Store the full settings object
            _steamApiTokenService = steamApiTokenService;
            Log("=== Provider Starting V9 (Discovery Mode) ===");
        }

        public bool IsCapable(Game game)
        {
            if (!TryResolveAppId(game, out var appId, out _ ) || appId <= 0)
            {
                // Explicit Local configuration is sufficient capability and should not trigger
                // install-directory discovery while Custom Refresh is building its game list.
                if (SupportsSchemaOnlyManualFallback(game))
                {
                    return true;
                }

                // Games owned by another library plugin (Epic, GOG, etc.) commonly have
                // non-numeric IDs. They are not Local candidates unless explicitly configured.
                // Avoid recursively probing every such game's install directory on the UI path.
                if (game == null || game.PluginId != Guid.Empty)
                {
                    return false;
                }

                if (TryAutoDetectTenokeAppId(game, out var autoDetectedTenokeAppId, out var autoDetectedTenokeIniPath) && autoDetectedTenokeAppId > 0)
                {
                    appId = autoDetectedTenokeAppId;
                    Log($"TENOKE AUTO-DETECT: game='{game?.Name}' appId={appId} source={autoDetectedTenokeIniPath}");
                }
                else
                {
                    if (game != null &&
                        TryGetLumaPlayIniPathOverride(game.Id, out var lumaIniOverridePath))
                    {
                        if (File.Exists(lumaIniOverridePath))
                        {
                            return true;
                        }

                        Log($"LUMAPLAY CAPABILITY: game='{game?.Name}' iniOverrideMissing='{lumaIniOverridePath}'");
                    }

                    return false;
                }
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

            if (GetSteamAppCacheUserStatsFilePaths(appId, game).Any())
            {
                return true;
            }

            if (GetSteamLibraryCacheFilePaths(appId, game).Any())
            {
                return true;
            }

            if (TryGetSteamLocalProgressSummary(appId, game) != null)
            {
                return true;
            }

            if (IsWindowsPlatform() &&
                TryGetConfiguredLumaPlayAppId(game, out var configuredLumaAppId, out _) &&
                TryGetLumaPlayRegistryEntries(configuredLumaAppId.ToString(CultureInfo.InvariantCulture))?.Count > 0)
            {
                return true;
            }

            return SupportsSchemaOnlyManualFallback(game);
        }

        public async Task<GameAchievementData> GetAchievementsAsync(Game game, RefreshRequest request)
        {
            var appId = GetAppId(game, out var isAppIdOverridden);
            var hasCustomSchemaPathOverride = game != null && TryGetCustomSchemaPathOverride(game.Id, out _);
            var hasEnabledCustomSchemaOverride = game != null &&
                TryGetCustomSchemaEnabledOverride(game.Id, out var isCustomSchemaEnabled) &&
                isCustomSchemaEnabled;
            var shouldFetchIconsFromGame = hasEnabledCustomSchemaOverride && game != null &&
                GameCustomDataLookup.IsViewAchievementsIconFetchEnabled(game.Id);
            // Schema-defined icons from the active schema should always remain usable.
            // Fetch toggle controls whether game/steam icon metadata gets injected upstream.
            var includeSchemaIconFallback = true;
            string configuredFolderOverridePath = null;
            var hasFolderOverride = game != null && TryGetFolderOverride(game.Id, out configuredFolderOverridePath);
            var hasDirectAchievementFileOverride = hasFolderOverride && File.Exists(configuredFolderOverridePath);
            if (string.IsNullOrEmpty(appId))
            {
                // If no app id is available from metadata, try to resolve it from a configured
                // LumaPlay.ini override for this game. This enables manual games with empty GameId.
                if (game != null &&
                    TryGetLumaPlayIniPathOverride(game.Id, out var configuredLumaPlayIniPath) &&
                    TryExtractUplayGameIdFromLumaPlayIni(configuredLumaPlayIniPath, out var appIdFromLumaPlayIni))
                {
                    appId = appIdFromLumaPlayIni.ToString(CultureInfo.InvariantCulture);
                    isAppIdOverridden = true;
                    Log($"LUMAPLAY APPID INFERRED: game='{game?.Name}' appId={appId} source={configuredLumaPlayIniPath}");
                }

                if (string.IsNullOrEmpty(appId) &&
                    game != null &&
                    TryGetFolderOverride(game.Id, out var tenokeOverrideFolderPath) &&
                    TryFindTenokeIniPathFromFolder(tenokeOverrideFolderPath, out var discoveredTenokeIniPath) &&
                    TryExtractAppIdFromTenokeIni(discoveredTenokeIniPath, out var appIdFromTenokeIni))
                {
                    appId = appIdFromTenokeIni.ToString(CultureInfo.InvariantCulture);
                    isAppIdOverridden = true;
                    Log($"TENOKE APPID INFERRED: game='{game?.Name}' appId={appId} source={discoveredTenokeIniPath}");
                }

                if (string.IsNullOrEmpty(appId) &&
                    TryAutoDetectTenokeAppId(game, out var autoDetectedTenokeAppId, out var autoDetectedTenokeIniPath) &&
                    autoDetectedTenokeAppId > 0)
                {
                    appId = autoDetectedTenokeAppId.ToString(CultureInfo.InvariantCulture);
                    isAppIdOverridden = true;
                    Log($"TENOKE APPID AUTO-DETECTED: game='{game?.Name}' appId={appId} source={autoDetectedTenokeIniPath}");
                }

                if (string.IsNullOrEmpty(appId) &&
                    game != null &&
                    TryGetLumaPlayIniPathOverride(game.Id, out var configuredLumaIniPath) &&
                    !File.Exists(configuredLumaIniPath))
                {
                    Log($"LUMAPLAY APPID RESOLUTION: game='{game?.Name}' iniOverrideMissing='{configuredLumaIniPath}'");
                }
            }

            if (string.IsNullOrEmpty(appId))
            {
                if (!hasEnabledCustomSchemaOverride && !hasFolderOverride && !SupportsSchemaOnlyManualFallback(game))
                {
                    Log($"LOCAL APPID RESOLUTION: game='{game?.Name}' failed (no app id from metadata or configured overrides).");
                    return null;
                }

                Log($"LOCAL APPID RESOLUTION: game='{game?.Name}' failed, continuing with override-only/schema-only mode (customSchema={hasEnabledCustomSchemaOverride}, folderOverride={hasFolderOverride}, schemaFallback={SupportsSchemaOnlyManualFallback(game)}).");
            }

            string localFolderPath = null;
            var hasResolvedLocalFolder = TryResolveLocalFolder(game, appId, out localFolderPath, out _, out _, out _);

            string jsonPath = null;
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                jsonPath = ResolveAchievementFilePath(localFolderPath, "achievements.json");
            }

            var iniPaths = new List<string>();
            if (hasResolvedLocalFolder && !string.IsNullOrWhiteSpace(localFolderPath))
            {
                foreach (var localIniFileName in LocalAchievementIniFileNames)
                {
                    var resolvedIniPath = ResolveAchievementFilePath(localFolderPath, localIniFileName);
                    if (!string.IsNullOrWhiteSpace(resolvedIniPath) &&
                        !iniPaths.Contains(resolvedIniPath, StringComparer.OrdinalIgnoreCase))
                    {
                        iniPaths.Add(resolvedIniPath);
                    }
                }
            }

            Dictionary<string, LocalEntry> lumaRegistryEntries = null;
            var hasValidLumaPlayIniOverride = TryGetConfiguredLumaPlayAppId(game, out var configuredLumaPlayAppId, out _);
            if (IsWindowsPlatform() &&
                hasValidLumaPlayIniOverride &&
                string.IsNullOrWhiteSpace(jsonPath) &&
                iniPaths.Count == 0)
            {
                lumaRegistryEntries = TryGetLumaPlayRegistryEntries(
                    configuredLumaPlayAppId.ToString(CultureInfo.InvariantCulture));
            }

            var hasAchievementsFile = !string.IsNullOrWhiteSpace(jsonPath) || iniPaths.Count > 0;
            var hasLumaRegistryEntries = lumaRegistryEntries != null && lumaRegistryEntries.Count > 0;
            var isLumaPlayContext = hasValidLumaPlayIniOverride;
            var preferLocalizedSchemaText = ShouldPreferLocalizedSteamText();

            SchemaAndPercentages steamSchema = null;
            string steamSchemaSource = null;
            var apiNameMap = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            var schemaLookupByText = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            var schemaLookupByTitle = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            int appIdInt = 0;
            int steamSchemaAppIdInt = 0;
            if (!int.TryParse(appId, out appIdInt) && SupportsSchemaOnlyManualFallback(game) && !string.IsNullOrWhiteSpace(game?.Name))
            {
                var resolvedManualSchemaAppId = await TryResolveSteamAppIdByGameNameAsync(game.Name).ConfigureAwait(false);
                if (resolvedManualSchemaAppId > 0)
                {
                    appIdInt = resolvedManualSchemaAppId;
                    appId = resolvedManualSchemaAppId.ToString(CultureInfo.InvariantCulture);
                    Log($"MANUAL SCHEMA AUTO-RESOLVE: game='{game.Name}' steamAppId={resolvedManualSchemaAppId}");
                }
            }

            if (appIdInt > 0)
            {
                steamSchemaAppIdInt = appIdInt;
                if (TryResolveSteamSchemaAppId(game, appIdInt, out var resolvedSteamSchemaAppId) && resolvedSteamSchemaAppId > 0)
                {
                    steamSchemaAppIdInt = resolvedSteamSchemaAppId;
                }

                // For LumaPlay (Uplay/Ubisoft Connect) games: the registry key uses a Uplay game ID,
                // which differs from the Steam App ID. If no explicit schema override was set via Links
                // or Notes, auto-resolve the correct Steam App ID by searching Steam Store by game name.
                if (isLumaPlayContext && steamSchemaAppIdInt == appIdInt && !string.IsNullOrWhiteSpace(game?.Name))
                {
                    var autoSteamAppId = await TryResolveSteamAppIdByGameNameAsync(game.Name).ConfigureAwait(false);
                    if (autoSteamAppId > 0 && autoSteamAppId != appIdInt)
                    {
                        steamSchemaAppIdInt = autoSteamAppId;
                        Log($"LUMA AUTO-RESOLVE: game='{game.Name}' uplayAppId={appIdInt} steamAppId={autoSteamAppId}");
                    }
                }

                steamSchema = await TryGetSteamSchemaAsync(steamSchemaAppIdInt).ConfigureAwait(false);
                steamSchemaSource = _steamSchemaSourceCache.TryGetValue(steamSchemaAppIdInt, out var resolvedSource)
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

                if (steamSchemaAppIdInt != appIdInt)
                {
                    Log($"SCHEMA APPID OVERRIDE: game={game?.Name} localAppId={appIdInt} steamSchemaAppId={steamSchemaAppIdInt}");
                }
            }

            if (game != null &&
                hasEnabledCustomSchemaOverride &&
                TryGetCustomSchemaPathOverride(game.Id, out var customSchemaPath))
            {
                var customSchemaAchievements = TryLoadCustomSchemaAchievements(customSchemaPath, out var customSchemaError);
                if (customSchemaAchievements != null && customSchemaAchievements.Count > 0)
                {
                    if (shouldFetchIconsFromGame)
                    {
                        ApplyCustomSchemaDefaultIcons(customSchemaAchievements, ResolveGameImagePath(game));
                    }

                    if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                    {
                        MergeSchemaMetadata(
                            customSchemaAchievements,
                            steamSchema.Achievements,
                            preferSourceText: false,
                            includeIconMetadata: shouldFetchIconsFromGame);
                    }

                    steamSchema ??= new SchemaAndPercentages();
                    steamSchema.Achievements = customSchemaAchievements;
                    steamSchemaSource = string.IsNullOrWhiteSpace(steamSchemaSource)
                        ? "custom-schema"
                        : $"custom-schema+{steamSchemaSource}";

                    apiNameMap = steamSchema.Achievements
                        .Where(a => !string.IsNullOrWhiteSpace(a.Name))
                        .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
                    schemaLookupByText = BuildSchemaLookupByText(steamSchema.Achievements);
                    schemaLookupByTitle = BuildSchemaLookupByTitle(steamSchema.Achievements);

                    Log($"CUSTOM SCHEMA: game={game?.Name} loaded={customSchemaAchievements.Count} file={customSchemaPath}");
                }
                else
                {
                    Log($"CUSTOM SCHEMA: game={game?.Name} failed file={customSchemaPath} reason={customSchemaError}");
                }
            }

            SteamLocalProgressSummary steamLocalProgress = null;
            if (appIdInt > 0)
            {
                steamLocalProgress = TryGetSteamLocalProgressSummary(appIdInt, game);
            }

            Dictionary<string, LocalEntry> steamAppCacheEntries = null;
            if (appIdInt > 0)
            {
                steamAppCacheEntries = TryGetSteamAppCacheEntries(appIdInt, game);
            }

            Dictionary<string, LocalEntry> steamLibraryCacheEntries = null;
            if (appIdInt > 0)
            {
                steamLibraryCacheEntries = TryGetSteamLibraryCacheEntries(appIdInt, game);
            }

            var hasSchemaAchievements = steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0;

            if (!hasResolvedLocalFolder &&
                !hasLumaRegistryEntries &&
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
                Dictionary<string, LocalEntry> raw = null;
                var rawSource = "local save data";

                if (hasAchievementsFile)
                {
                    // Keep JSON metadata/icons when present, but let INI override progress fields.
                    // This preserves custom local icon paths (for example img/*.png) even when
                    // achievements.ini exists.
                    raw = await LoadLocalEntriesAsync(jsonPath, iniPaths).ConfigureAwait(false);
                }

                if ((raw == null || raw.Count == 0) && hasLumaRegistryEntries)
                {
                    raw = lumaRegistryEntries;
                    rawSource = "LumaPlay registry";
                }

                if (raw != null && raw.Count > 0)
                {
                    Log($"LOCAL SOURCE: game={game?.Name} folder={localFolderPath ?? "none"} json={jsonPath ?? "none"} ini={(iniPaths.Count > 0 ? string.Join("|", iniPaths) : "none")} rawEntries={raw.Count} schemaEntries={(steamSchema?.Achievements?.Count ?? 0)} source={rawSource}");

                    if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                    {
                        // For LumaPlay registry entries, normalize the key names to match the Steam
                        // schema names before correlation. LumaPlay may store names with or without
                        // an "ach_" prefix compared to how the same game's Steam schema names them.
                        if (rawSource == "LumaPlay registry")
                        {
                            raw = NormalizeLumaPlayNamesAgainstSchema(raw, steamSchema.Achievements);
                        }
                    }

                    if (steamSchema?.Achievements != null && steamSchema.Achievements.Count > 0)
                    {
                        var correlatedEntries = CorrelateLocalEntriesWithSchema(raw, steamSchema.Achievements);

                        foreach (var schemaAch in steamSchema.Achievements)
                        {
                            if (string.IsNullOrWhiteSpace(schemaAch?.Name))
                            {
                                continue;
                            }

                            correlatedEntries.TryGetValue(schemaAch.Name, out var entry);
                            data.Achievements.Add(CreateAchievementDetail(schemaAch.Name, entry, schemaAch, steamSchema, preferLocalizedSchemaText, includeSchemaIconFallback));
                        }

                        PreserveCachedLocalMetadata(data);
                        Log($"SCHEMA CORRELATION: appId={appIdInt} source={steamSchemaSource ?? "unknown"} localEntries={raw.Count} matched={correlatedEntries.Count}");
                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} schema achievements with local progress from {rawSource}. schemaSource={steamSchemaSource ?? "none"}");
                        return data;
                    }

                    foreach (var kv in raw)
                    {
                        var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema, preferLocalizedSchemaText, includeSchemaIconFallback));
                    }

                    FilterPlaceholderLocalAchievements(data);
                    if (data.Achievements.Count == 0)
                    {
                        Log($"INFO: {game.Name} - Ignored local achievements with placeholder descriptions.");
                    }
                    else
                    {
                        PreserveCachedLocalMetadata(data);
                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from {rawSource}. schemaSource={steamSchemaSource ?? "none"}");
                        return data;
                    }
                }

                if (hasDirectAchievementFileOverride && hasAchievementsFile)
                {
                    // A directly selected achievement file is authoritative. In particular, an
                    // intentionally empty file must not fall through to Steam appcache/library
                    // progress and resurrect achievements from a different local source.
                    if (steamSchema?.Achievements != null)
                    {
                        foreach (var schemaAch in steamSchema.Achievements)
                        {
                            if (!string.IsNullOrWhiteSpace(schemaAch?.Name))
                            {
                                data.Achievements.Add(CreateAchievementDetail(
                                    schemaAch.Name,
                                    new LocalEntry(),
                                    schemaAch,
                                    steamSchema,
                                    preferLocalizedSchemaText,
                                    includeSchemaIconFallback));
                            }
                        }
                    }

                    Log($"SUCCESS: {game.Name} - Direct Local achievement file override produced {data.Achievements.Count} locked achievements; Steam local cache fallbacks were skipped. file={configuredFolderOverridePath}");
                    return data;
                }

                if (steamAppCacheEntries != null && steamAppCacheEntries.Count > 0)
                {
                    // Display only achievements that are actually tracked in the Steam appcache.
                    // Use schema data for enrichment, but don't add achievements missing from the cache.
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamAppCacheEntries)
                    {
                        var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema, preferLocalizedSchemaText, includeSchemaIconFallback));
                        added.Add(kv.Key);
                    }

                    FilterPlaceholderLocalAchievements(data);
                    if (data.Achievements.Count == 0)
                    {
                        Log($"INFO: {game.Name} - Ignored Steam appcache achievements with placeholder descriptions.");
                    }
                    else
                    {
                        PreserveCachedLocalMetadata(data);
                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from Steam appcache stats.");
                        return data;
                    }
                }

                if (steamLibraryCacheEntries != null && steamLibraryCacheEntries.Count > 0)
                {
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kv in steamLibraryCacheEntries)
                    {
                        var schemaAch = ResolveSchemaAchievement(kv.Key, kv.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                        data.Achievements.Add(CreateAchievementDetail(kv.Key, kv.Value, schemaAch, steamSchema, preferLocalizedSchemaText, includeSchemaIconFallback));
                        added.Add(kv.Key);
                    }

                    FilterPlaceholderLocalAchievements(data);
                    if (data.Achievements.Count == 0)
                    {
                        Log($"INFO: {game.Name} - Ignored Steam library cache achievements with placeholder descriptions.");
                    }
                    else
                    {
                        PreserveCachedLocalMetadata(data);
                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievements from Steam local library cache.");
                        return data;
                    }
                }

                if (steamLocalProgress != null && steamLocalProgress.TotalCount > 0)
                {
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

                        data.Achievements.Add(CreateAchievementDetail(
                            schemaAch.Name,
                            new LocalEntry(),
                            schemaAch,
                            steamSchema,
                            preferLocalizedSchemaText,
                            includeSchemaIconFallback));
                    }

                    FilterPlaceholderLocalAchievements(data);
                    if (data.Achievements.Count == 0)
                    {
                        Log($"INFO: {game.Name} - Steam schema achievements were ignored due to placeholder descriptions.");
                    }
                    else
                    {
                        PreserveCachedLocalMetadata(data);
                        Log($"INFO: {game.Name} - Local folder found, loaded {data.Achievements.Count} achievement definitions from Steam schema.");
                        return data;
                    }
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
                    // Keep the provider marked enabled without replacing the rest of
                    // the Local provider settings payload.
                    var localSettings = ProviderRegistry.Settings<LocalSettings>();
                    localSettings.IsEnabled = true;
                    ProviderRegistry.Write(localSettings);
                    
                    await onAchievementsUpdated(game, data);
                    Log($"DATABASE: Submitted {game.Name} (Count: {data.Achievements.Count})");
                }
                
                if (games?.Count > 0) onGameProcessed(game);
            }
            return new RebuildPayload();
        }

        internal bool TryGetActiveMonitorSourceFingerprint(Game game, out string fingerprint)
        {
            fingerprint = null;
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            var paths = new List<string>();
            var appId = GetAppId(game, out _);
            int lumaAppId;

            if (string.IsNullOrWhiteSpace(appId))
            {
                if (TryGetLumaPlayIniPathOverride(game.Id, out var lumaOverridePath) &&
                    TryExtractUplayGameIdFromLumaPlayIni(lumaOverridePath, out lumaAppId))
                {
                    appId = lumaAppId.ToString(CultureInfo.InvariantCulture);
                    AddExistingPath(paths, lumaOverridePath);
                }
                else if (TryGetFolderOverride(game.Id, out var overriddenFolderPath))
                {
                    if (TryFindTenokeIniPathFromFolder(overriddenFolderPath, out var tenokePath) &&
                             TryExtractAppIdFromTenokeIni(tenokePath, out var tenokeAppId))
                    {
                        appId = tenokeAppId.ToString(CultureInfo.InvariantCulture);
                        AddExistingPath(paths, tenokePath);
                    }
                }
            }

            if (TryGetCustomSchemaEnabledOverride(game.Id, out var customSchemaEnabled) &&
                customSchemaEnabled &&
                TryGetCustomSchemaPathOverride(game.Id, out var customSchemaPath))
            {
                AddExistingPath(paths, customSchemaPath);
            }

            if (TryResolveLocalFolder(game, appId, out var localFolderPath, out _, out _, out _) &&
                !string.IsNullOrWhiteSpace(localFolderPath))
            {
                AddExistingPath(paths, ResolveAchievementFilePath(localFolderPath, "achievements.json"));
                foreach (var localIniFileName in LocalAchievementIniFileNames)
                {
                    AddExistingPath(paths, ResolveAchievementFilePath(localFolderPath, localIniFileName));
                }
            }

            if (int.TryParse(appId, out var appIdInt) && appIdInt > 0)
            {
                foreach (var path in GetSteamAppCacheSchemaFilePaths(appIdInt))
                {
                    AddExistingPath(paths, path);
                }

                foreach (var path in GetSteamAppCacheUserStatsFilePaths(appIdInt, game))
                {
                    AddExistingPath(paths, path);
                }

                foreach (var path in GetSteamLibraryCacheFilePaths(appIdInt, game))
                {
                    AddExistingPath(paths, path);
                }

                var steamLocalProgress = TryGetSteamLocalProgressSummary(appIdInt, game);
                AddExistingPath(paths, steamLocalProgress?.SourcePath);
            }

            if (paths.Count == 0)
            {
                return false;
            }

            fingerprint = string.Join("|", paths
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(BuildFileFingerprint));
            return !string.IsNullOrWhiteSpace(fingerprint);
        }

        private static void AddExistingPath(ICollection<string> paths, string path)
        {
            if (paths == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (File.Exists(path))
                {
                    paths.Add(Path.GetFullPath(path));
                }
            }
            catch
            {
            }
        }

        private static string BuildFileFingerprint(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return string.Concat(
                    info.FullName,
                    ":",
                    info.Length.ToString(CultureInfo.InvariantCulture),
                    ":",
                    info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                return path ?? string.Empty;
            }
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

        internal static bool TryGetCustomSchemaPathOverride(Guid gameId, out string schemaPath)
        {
            schemaPath = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.CustomSchemaPathOverrides == null ||
                !settings.CustomSchemaPathOverrides.TryGetValue(gameId, out var configuredPath))
            {
                return false;
            }

            schemaPath = configuredPath?.Trim();
            return !string.IsNullOrWhiteSpace(schemaPath);
        }

        internal static bool TryGetCustomSchemaEnabledOverride(Guid gameId, out bool isEnabled)
        {
            isEnabled = false;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            return settings?.CustomSchemaEnabledOverrides != null &&
                   settings.CustomSchemaEnabledOverrides.TryGetValue(gameId, out isEnabled);
        }

        internal static bool TrySetCustomSchemaEnabledOverride(Guid gameId, bool isEnabled, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.CustomSchemaEnabledOverrides ??= new Dictionary<Guid, bool>();
            settings.CustomSchemaEnabledOverrides[gameId] = isEnabled;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local custom schema enabled override for '{gameName}' to '{isEnabled}'");
            return true;
        }

        internal static bool TryClearCustomSchemaEnabledOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.CustomSchemaEnabledOverrides == null || !settings.CustomSchemaEnabledOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local custom schema enabled override for '{gameName}'");
            return true;
        }

        internal static bool TryGetLumaPlayAppIdOverride(Guid gameId, out int appId)
        {
            appId = 0;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            return settings?.LumaPlayAppIdOverrides != null &&
                   settings.LumaPlayAppIdOverrides.TryGetValue(gameId, out appId) &&
                   appId > 0;
        }

        internal static bool TryGetLumaPlayIniPathOverride(Guid gameId, out string iniPath)
        {
            iniPath = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.LumaPlayIniPathOverrides == null ||
                !settings.LumaPlayIniPathOverrides.TryGetValue(gameId, out var configuredPath))
            {
                return false;
            }

            iniPath = configuredPath?.Trim();
            return !string.IsNullOrWhiteSpace(iniPath);
        }

        internal static bool TryGetSteamAppCacheUserOverride(Guid gameId, out string userId)
        {
            userId = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.SteamAppCacheUserOverrides == null ||
                !settings.SteamAppCacheUserOverrides.TryGetValue(gameId, out var configuredUserId))
            {
                return false;
            }

            userId = configuredUserId?.Trim();
            return !string.IsNullOrWhiteSpace(userId);
        }

        internal static bool TryGetRefreshOnGameCloseOverride(Guid gameId, out bool shouldRefresh)
        {
            shouldRefresh = false;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            return settings?.RefreshOnGameCloseOverrides != null &&
                   settings.RefreshOnGameCloseOverrides.TryGetValue(gameId, out shouldRefresh);
        }

        internal static bool ShouldRefreshAchievementsOnGameClose(Guid gameId)
        {
            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings == null)
            {
                return false;
            }

            if (gameId != Guid.Empty &&
                settings.RefreshOnGameCloseOverrides != null &&
                settings.RefreshOnGameCloseOverrides.TryGetValue(gameId, out var overrideValue))
            {
                return overrideValue;
            }

            return settings.RefreshAchievementsOnGameClose;
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

        internal static bool TrySetCustomSchemaPathOverride(Guid gameId, string schemaPath, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(schemaPath))
            {
                return false;
            }

            var normalizedPath = schemaPath.Trim();
            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.CustomSchemaPathOverrides ??= new Dictionary<Guid, string>();
            settings.CustomSchemaPathOverrides[gameId] = normalizedPath;
            settings.CustomSchemaEnabledOverrides ??= new Dictionary<Guid, bool>();
            settings.CustomSchemaEnabledOverrides[gameId] = true;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local custom schema override for '{gameName}' to '{normalizedPath}'");
            return true;
        }

        internal static bool TrySetLumaPlayAppIdOverride(Guid gameId, int appId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || appId <= 0)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.LumaPlayAppIdOverrides[gameId] = appId;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local LumaPlay Uplay App ID override for '{gameName}' to {appId}");
            return true;
        }

        internal static bool TryClearLumaPlayAppIdOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.LumaPlayAppIdOverrides == null || !settings.LumaPlayAppIdOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local LumaPlay Uplay App ID override for '{gameName}'");
            return true;
        }

        internal static bool TrySetLumaPlayIniPathOverride(Guid gameId, string iniPath, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(iniPath))
            {
                return false;
            }

            var normalizedPath = iniPath.Trim();
            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.LumaPlayIniPathOverrides[gameId] = normalizedPath;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local LumaPlay.ini override for '{gameName}' to '{normalizedPath}'");
            return true;
        }

        internal static bool TryClearLumaPlayIniPathOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.LumaPlayIniPathOverrides == null || !settings.LumaPlayIniPathOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local LumaPlay.ini override for '{gameName}'");
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

        internal static bool TryClearCustomSchemaPathOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            var removedPath = settings?.CustomSchemaPathOverrides != null && settings.CustomSchemaPathOverrides.Remove(gameId);
            var removedEnabled = settings?.CustomSchemaEnabledOverrides != null && settings.CustomSchemaEnabledOverrides.Remove(gameId);
            if (!removedPath && !removedEnabled)
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local custom schema override for '{gameName}'");
            return true;
        }

        internal static bool TrySetSteamAppCacheUserOverride(Guid gameId, string userId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(userId))
            {
                return false;
            }

            var normalizedUserId = userId.Trim();
            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.SteamAppCacheUserOverrides ??= new Dictionary<Guid, string>();
            settings.SteamAppCacheUserOverrides[gameId] = normalizedUserId;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local Steam appcache user override for '{gameName}' to '{normalizedUserId}'");
            return true;
        }

        internal static bool TrySetRefreshOnGameCloseOverride(Guid gameId, bool shouldRefresh, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            settings.RefreshOnGameCloseOverrides ??= new Dictionary<Guid, bool>();
            settings.RefreshOnGameCloseOverrides[gameId] = shouldRefresh;
            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Set Local refresh-on-game-close override for '{gameName}' to '{shouldRefresh}'.");
            return true;
        }

        internal static bool TryClearSteamAppCacheUserOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.SteamAppCacheUserOverrides == null || !settings.SteamAppCacheUserOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local Steam appcache user override for '{gameName}'");
            return true;
        }

        internal static bool TryClearRefreshOnGameCloseOverride(Guid gameId, string gameName, Action persistSettingsForUi, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.RefreshOnGameCloseOverrides == null || !settings.RefreshOnGameCloseOverrides.Remove(gameId))
            {
                return false;
            }

            ProviderRegistry.Write(settings);
            persistSettingsForUi?.Invoke();
            logger?.Info($"Cleared Local refresh-on-game-close override for '{gameName}'.");
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

            if (TryGetLumaPlayIniPathOverride(game.Id, out var overriddenLumaPlayIniPath) &&
                TryExtractUplayGameIdFromLumaPlayIni(overriddenLumaPlayIniPath, out var lumaPlayIniAppId))
            {
                appId = lumaPlayIniAppId;
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
                if (TryExtractAppIdFromNotes(game.Notes, out var notesAppId))
                {
                    return notesAppId.ToString(CultureInfo.InvariantCulture);
                }
            }
            return null;
        }

        private static bool TryExtractUplayGameIdFromLumaPlayIni(string iniPath, out int appId)
        {
            appId = 0;
            if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            {
                return false;
            }

            try
            {
                var inAchievementsSection = false;
                foreach (var rawLine in File.ReadLines(iniPath))
                {
                    var line = (rawLine ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        var sectionName = line.Substring(1, line.Length - 2).Trim();
                        inAchievementsSection = string.Equals(sectionName, "Achievements", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inAchievementsSection)
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separatorIndex).Trim();
                    if (!string.Equals(key, "UplayGameID", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = line.Substring(separatorIndex + 1).Trim();
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) && appId > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryGetConfiguredLumaPlayAppId(Game game, out int appId, out string iniPath)
        {
            appId = 0;
            iniPath = null;
            return game != null &&
                TryGetLumaPlayIniPathOverride(game.Id, out iniPath) &&
                TryExtractUplayGameIdFromLumaPlayIni(iniPath, out appId);
        }

        private bool TryAutoDetectLumaPlayAppId(Game game, out int appId, out string detectedIniPath)
        {
            appId = 0;
            detectedIniPath = null;
            if (game == null)
            {
                return false;
            }

            var installDirectory = PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim();
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return false;
            }

            if (!TryFindLumaPlayIniPathFromFolder(installDirectory, out var iniPath, maxSearchDepth: 3))
            {
                return false;
            }

            if (!TryExtractUplayGameIdFromLumaPlayIni(iniPath, out appId) || appId <= 0)
            {
                return false;
            }

            detectedIniPath = iniPath;
            return true;
        }

        private bool TryAutoDetectTenokeAppId(Game game, out int appId, out string detectedIniPath)
        {
            appId = 0;
            detectedIniPath = null;
            if (game == null)
            {
                return false;
            }

            var installDirectory = PathExpansion.ExpandGamePath(_api, game, game.InstallDirectory)?.Trim();
            if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
            {
                return false;
            }

            if (!TryFindTenokeIniPathFromFolder(installDirectory, out var iniPath, maxSearchDepth: 3))
            {
                return false;
            }

            if (!TryExtractAppIdFromTenokeIni(iniPath, out appId) || appId <= 0)
            {
                return false;
            }

            detectedIniPath = iniPath;
            return true;
        }

        private static bool TryFindTenokeIniPathFromFolder(string folderPath, out string iniPath, int maxSearchDepth = 1)
        {
            iniPath = null;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            var directPath = Path.Combine(folderPath, "tenoke.ini");
            if (File.Exists(directPath))
            {
                iniPath = directPath;
                return true;
            }

            return TryFindTenokeIniPathRecursive(folderPath, 0, Math.Max(0, maxSearchDepth), out iniPath);
        }

        private static bool TryFindTenokeIniPathRecursive(string directoryPath, int currentDepth, int maxDepth, out string iniPath)
        {
            iniPath = null;
            if (currentDepth >= maxDepth)
            {
                return false;
            }

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directoryPath);
            }
            catch
            {
                return false;
            }

            foreach (var child in children)
            {
                var candidate = Path.Combine(child, "tenoke.ini");
                if (File.Exists(candidate))
                {
                    iniPath = candidate;
                    return true;
                }

                if (TryFindTenokeIniPathRecursive(child, currentDepth + 1, maxDepth, out iniPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractAppIdFromTenokeIni(string iniPath, out int appId)
        {
            appId = 0;
            if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
            {
                return false;
            }

            try
            {
                var inTenokeSection = false;
                foreach (var rawLine in File.ReadLines(iniPath))
                {
                    var line = (rawLine ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                    {
                        var sectionName = line.Substring(1, line.Length - 2).Trim();
                        inTenokeSection = string.Equals(sectionName, "TENOKE", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    if (!inTenokeSection)
                    {
                        continue;
                    }

                    var separatorIndex = line.IndexOf('=');
                    if (separatorIndex <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, separatorIndex).Trim();
                    if (!string.Equals(key, "id", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(key, "appid", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = StripIniInlineComment(line.Substring(separatorIndex + 1)).Trim().Trim('"');
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) && appId > 0;
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryFindLumaPlayIniPathFromFolder(string folderPath, out string iniPath, int maxSearchDepth = 1)
        {
            iniPath = null;
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            var directPath = Path.Combine(folderPath, "LumaPlay.ini");
            if (File.Exists(directPath))
            {
                iniPath = directPath;
                return true;
            }

            var lumaFilesPath = Path.Combine(folderPath, "LumaPlayFiles", "LumaPlay.ini");
            if (File.Exists(lumaFilesPath))
            {
                iniPath = lumaFilesPath;
                return true;
            }

            if (maxSearchDepth <= 0)
            {
                return false;
            }

            return TryFindLumaPlayIniPathRecursive(folderPath, 0, maxSearchDepth, out iniPath);
        }

        private static bool TryFindLumaPlayIniPathRecursive(string directoryPath, int currentDepth, int maxDepth, out string iniPath)
        {
            iniPath = null;
            if (currentDepth >= maxDepth)
            {
                return false;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directoryPath))
                {
                    var directPath = Path.Combine(child, "LumaPlay.ini");
                    if (File.Exists(directPath))
                    {
                        iniPath = directPath;
                        return true;
                    }

                    var lumaFilesPath = Path.Combine(child, "LumaPlayFiles", "LumaPlay.ini");
                    if (File.Exists(lumaFilesPath))
                    {
                        iniPath = lumaFilesPath;
                        return true;
                    }

                    if (TryFindLumaPlayIniPathRecursive(child, currentDepth + 1, maxDepth, out iniPath))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryExtractAppIdFromNotes(string notes, out int appId)
        {
            appId = 0;
            if (string.IsNullOrWhiteSpace(notes))
            {
                return false;
            }

            var match = ManualAppIdInNotesRegex.Match(notes);
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) && appId > 0;
        }

        private static bool TryResolveSteamSchemaAppId(Game game, int defaultAppId, out int schemaAppId)
        {
            schemaAppId = defaultAppId;
            if (game == null)
            {
                return false;
            }

            if (game.Links != null)
            {
                foreach (var link in game.Links)
                {
                    var match = Regex.Match(link?.Url ?? string.Empty, @"/app/(\d+)", RegexOptions.IgnoreCase);
                    if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appIdFromLink) && appIdFromLink > 0)
                    {
                        schemaAppId = appIdFromLink;
                        return true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(game.Notes))
            {
                var notesMatch = SteamSchemaAppIdInNotesRegex.Match(game.Notes);
                if (notesMatch.Success && int.TryParse(notesMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var appIdFromNotes) && appIdFromNotes > 0)
                {
                    schemaAppId = appIdFromNotes;
                    return true;
                }
            }

            return schemaAppId > 0;
        }

        /// <summary>
        /// Queries the Steam Store search API by game name to find the Steam App ID for a LumaPlay
        /// (Uplay/Ubisoft Connect) game. Results are cached in memory per game name.
        /// Returns 0 if no confident match is found or the request fails.
        /// </summary>
        private async Task<int> TryResolveSteamAppIdByGameNameAsync(string gameName)
        {
            if (string.IsNullOrWhiteSpace(gameName))
            {
                return 0;
            }

            if (_lumaPlaySteamAppIdCache.TryGetValue(gameName, out var cachedId))
            {
                return cachedId;
            }

            try
            {
                var normalizedQuery = NormalizeGameNameForSteamSearch(gameName);
                if (string.IsNullOrWhiteSpace(normalizedQuery))
                {
                    _lumaPlaySteamAppIdCache[gameName] = 0;
                    return 0;
                }

                var queryCandidates = BuildSteamSearchQueryCandidates(normalizedQuery)
                    .Take(5)
                    .ToList();

                using (var client = CreateAnonymousSteamHttpClient())
                {
                    int bestId = 0;
                    double bestScore = 0.0;
                    string bestQuery = normalizedQuery;

                    foreach (var query in queryCandidates)
                    {
                        var encoded = Uri.EscapeDataString(query);
                        var url = $"https://store.steampowered.com/api/storesearch/?term={encoded}&l=english&cc=US";
                        string json;
                        try
                        {
                            json = await client.GetStringAsync(url).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, $"[Local] Steam store search request failed for query '{query}'.");
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(json))
                        {
                            continue;
                        }

                        var root = JObject.Parse(json);
                        var items = root["items"] as JArray;
                        if (items == null || items.Count == 0)
                        {
                            continue;
                        }

                        for (int i = 0; i < items.Count; i++)
                        {
                            if (!(items[i] is JObject item))
                            {
                                continue;
                            }

                            var type = item["type"]?.Value<string>();
                            if (!string.Equals(type, "app", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var itemId = item["id"]?.Value<int>() ?? 0;
                            var itemName = item["name"]?.Value<string>();
                            if (itemId <= 0 || string.IsNullOrWhiteSpace(itemName))
                            {
                                continue;
                            }

                            var normalizedItemName = NormalizeGameNameForSteamSearch(itemName);
                            var score = Math.Max(
                                ComputeNameSimilarity(normalizedQuery, normalizedItemName),
                                ComputeNameSimilarity(query, normalizedItemName));
                            if (score > bestScore)
                            {
                                bestScore = score;
                                bestId = itemId;
                                bestQuery = query;
                            }

                            // First result with very high confidence, stop early
                            if (i == 0 && score >= 0.90)
                            {
                                break;
                            }
                        }

                        if (bestScore >= 0.90)
                        {
                            break;
                        }
                    }

                    // Only accept results above a confidence threshold
                    var resolvedId = bestScore >= 0.5 ? bestId : 0;
                    _lumaPlaySteamAppIdCache[gameName] = resolvedId;

                    if (resolvedId > 0)
                    {
                        Log($"STEAM SCHEMA SEARCH: game='{gameName}' normalized='{normalizedQuery}' query='{bestQuery}' steamAppId={resolvedId} score={bestScore:F2}");
                    }
                    else
                    {
                        Log($"STEAM SCHEMA SEARCH: game='{gameName}' no confident Steam match (bestScore={bestScore:F2})");
                    }

                    return resolvedId;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Local] Steam store search failed for game '{gameName}'.");
                _lumaPlaySteamAppIdCache[gameName] = 0;
                return 0;
            }
        }

        private static string NormalizeGameNameForSteamSearch(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return name ?? string.Empty;
            }

            // Strip trademark/copyright symbols
            name = Regex.Replace(name, @"[\u2122\u00ae\u00a9]", string.Empty);
            // Strip trailing parenthetical qualifiers: "(Steam Version)", "(RU)", etc.
            name = Regex.Replace(name, @"\s*\([^)]{1,30}\)\s*$", string.Empty);
            // Normalize apostrophes to plain quote for easier token matching
            name = name.Replace('\u2019', '\'');
            // Normalize whitespace
            name = Regex.Replace(name, @"\s+", " ").Trim();
            return name;
        }

        private static IEnumerable<string> BuildSteamSearchQueryCandidates(string normalizedName)
        {
            var candidates = new List<string>();
            void AddCandidate(string value)
            {
                value = NormalizeGameNameForSteamSearch(value);
                if (!string.IsNullOrWhiteSpace(value) && !candidates.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(value);
                }
            }

            AddCandidate(normalizedName);
            AddCandidate(RemoveDiacritics(normalizedName));

            // Handle possessive/title quirks like "Assassins's" vs "Assassin's".
            AddCandidate(Regex.Replace(normalizedName, @"\b([A-Za-z]{3,})'s\b", "$1", RegexOptions.IgnoreCase));
            AddCandidate(Regex.Replace(normalizedName, @"\b([A-Za-z]{3,})s's\b", "$1's", RegexOptions.IgnoreCase));

            // Handle common roman numeral vs arabic numeral title variants.
            AddCandidate(ConvertRomanNumeralsToDigits(normalizedName));
            AddCandidate(ConvertDigitsToRomanNumerals(normalizedName));

            return candidates;
        }

        private static string RemoveDiacritics(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value ?? string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(c);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string ConvertRomanNumeralsToDigits(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value ?? string.Empty;
            }

            return Regex.Replace(value, @"\bX\b|\bIX\b|\bVIII\b|\bVII\b|\bVI\b|\bV\b|\bIV\b|\bIII\b|\bII\b|\bI\b", match =>
            {
                return match.Value.ToUpperInvariant() switch
                {
                    "I" => "1",
                    "II" => "2",
                    "III" => "3",
                    "IV" => "4",
                    "V" => "5",
                    "VI" => "6",
                    "VII" => "7",
                    "VIII" => "8",
                    "IX" => "9",
                    "X" => "10",
                    _ => match.Value
                };
            }, RegexOptions.IgnoreCase);
        }

        private static string ConvertDigitsToRomanNumerals(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value ?? string.Empty;
            }

            return Regex.Replace(value, @"\b10\b|\b9\b|\b8\b|\b7\b|\b6\b|\b5\b|\b4\b|\b3\b|\b2\b|\b1\b", match =>
            {
                return match.Value switch
                {
                    "1" => "I",
                    "2" => "II",
                    "3" => "III",
                    "4" => "IV",
                    "5" => "V",
                    "6" => "VI",
                    "7" => "VII",
                    "8" => "VIII",
                    "9" => "IX",
                    "10" => "X",
                    _ => match.Value
                };
            });
        }

        private static double ComputeNameSimilarity(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            {
                return 0.0;
            }

            a = a.ToLowerInvariant();
            b = b.ToLowerInvariant();

            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                return 1.0;
            }

            var sep = new char[] { ' ', '-', '_', ':', '.', '\'', '"' };
            var tokensA = new HashSet<string>(
                a.Split(sep, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);
            var tokensB = new HashSet<string>(
                b.Split(sep, StringSplitOptions.RemoveEmptyEntries),
                StringComparer.Ordinal);

            if (tokensA.Count == 0 || tokensB.Count == 0)
            {
                return 0.0;
            }

            int matches = 0;
            foreach (var token in tokensA)
            {
                if (tokensB.Contains(token))
                {
                    matches++;
                }
            }

            return (double)matches / Math.Max(tokensA.Count, tokensB.Count);
        }

        private bool SupportsSchemaOnlyManualFallback(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            if (TryGetAppIdOverride(game.Id, out _) ||
                TryGetFolderOverride(game.Id, out _) ||
                (TryGetCustomSchemaPathOverride(game.Id, out _) && TryGetCustomSchemaEnabledOverride(game.Id, out var isCustomSchemaEnabledForFallback) && isCustomSchemaEnabledForFallback) ||
                TryGetConfiguredLumaPlayAppId(game, out _, out _))
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

            if (TryExtractAppIdFromNotes(game.Notes, out _))
            {
                return true;
            }

            // A name alone is not Local-provider configuration. In particular, Playnite creates
            // a temporary library-less "New Game" record when the manual-game editor opens.
            return false;
        }

        private Dictionary<string, LocalEntry> TryGetSteamAppCacheEntries(int appId, Game game = null)
        {
            if (appId <= 0)
            {
                return null;
            }

            var kvEntries = TryGetSteamAppCacheEntriesFromKvBinary(appId, game);
            if (kvEntries != null && kvEntries.Count > 0)
            {
                return kvEntries;
            }

            var schemaSections = TryGetSteamAppCacheSchemaSections(appId);
            if (schemaSections != null && schemaSections.Count > 0)
            {
                var unlockTimeSections = TryGetSteamAppCacheUnlockTimeSections(appId, game);
                var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                var hasUnlockSectionIds = unlockTimeSections.Any(section => section?.SectionId.HasValue == true);
                var unlockBySectionId = unlockTimeSections
                    .Where(section => section?.SectionId.HasValue == true)
                    .GroupBy(section => section.SectionId.Value)
                    .ToDictionary(group => group.Key, group => group.First(), EqualityComparer<int>.Default);

                for (var sectionIndex = 0; sectionIndex < schemaSections.Count; sectionIndex++)
                {
                    var schemaSection = schemaSections[sectionIndex];
                    Dictionary<int, long> unlockTimes = null;
                    uint? sectionDataValue = null;

                    if (schemaSection?.SectionId.HasValue == true &&
                        unlockBySectionId.TryGetValue(schemaSection.SectionId.Value, out var matchedSection) &&
                        matchedSection != null)
                    {
                        unlockTimes = matchedSection.UnlockTimes;
                        sectionDataValue = matchedSection.DataValue;
                    }
                    else if (!hasUnlockSectionIds && sectionIndex < unlockTimeSections.Count)
                    {
                        unlockTimes = unlockTimeSections[sectionIndex]?.UnlockTimes;
                        sectionDataValue = unlockTimeSections[sectionIndex]?.DataValue;
                    }

                    foreach (var schemaEntry in schemaSection.Entries)
                    {
                        var timestamp = 0L;
                        unlockTimes?.TryGetValue(schemaEntry.Index, out timestamp);
                        var unlocked = sectionDataValue.HasValue
                            ? ((sectionDataValue.Value >> schemaEntry.Index) & 1U) == 1U
                            : timestamp > 0;
                        MergeSteamAppCacheEntry(entries, schemaEntry, unlocked, timestamp, appId);
                    }
                }

                return entries.Count > 0 ? entries : null;
            }

            // No local schema .bin, try reading earned status directly from user stats .bin.
            // The UserGameStats_*.bin file stores achievement entries as:
            //   <apiName>\0<1-byte-type>\0<4-byte-int-value>  (type 0x01 = int32)
            // We scan for null-delimited tokens and look for pairs where the value byte is 1 (earned).
            var directEntries = TryGetSteamUserStatsDirectEntries(appId, game);
            return directEntries != null && directEntries.Count > 0 ? directEntries : null;
        }

        private Dictionary<string, LocalEntry> TryGetSteamAppCacheEntriesFromKvBinary(int appId, Game game = null)
        {
            foreach (var schemaPath in GetSteamAppCacheSchemaFilePaths(appId))
            {
                try
                {
                    if (!File.Exists(schemaPath))
                    {
                        continue;
                    }

                    if (!TryParseSteamKvBinary(File.ReadAllBytes(schemaPath), out var schemaRoot))
                    {
                        continue;
                    }

                    var schemaEntries = ExtractSteamAppCacheSchemaFromKv(schemaRoot);
                    if (schemaEntries.Count == 0)
                    {
                        continue;
                    }

                    var userStatsByStatId = new Dictionary<int, SteamKvUserStatData>();
                    foreach (var userStatsPath in GetSteamAppCacheUserStatsFilePaths(appId, game))
                    {
                        if (!File.Exists(userStatsPath))
                        {
                            continue;
                        }

                        if (!TryParseSteamKvBinary(File.ReadAllBytes(userStatsPath), out var userStatsRoot))
                        {
                            continue;
                        }

                        userStatsByStatId = ExtractSteamAppCacheUserStatsFromKv(userStatsRoot);
                        if (userStatsByStatId.Count > 0)
                        {
                            break;
                        }
                    }

                    var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var schemaEntry in schemaEntries)
                    {
                        var unlocked = false;
                        var timestamp = 0L;

                        if (userStatsByStatId.TryGetValue(schemaEntry.StatId, out var statData) && statData != null)
                        {
                            unlocked = ((statData.DataU32 >> schemaEntry.Bit) & 1U) == 1U;
                            if (unlocked)
                            {
                                statData.UnlockTimes.TryGetValue(schemaEntry.Bit, out timestamp);
                            }
                        }

                        MergeSteamAppCacheEntry(entries, new SteamAppCacheSchemaEntry
                        {
                            Index = schemaEntry.Bit,
                            ApiName = schemaEntry.ApiName,
                            DisplayName = schemaEntry.DisplayName,
                            Description = schemaEntry.Description,
                            Hidden = schemaEntry.Hidden,
                            IconHash = schemaEntry.IconHash,
                            IconGrayHash = schemaEntry.IconGrayHash
                        }, unlocked, timestamp, appId);
                    }

                    if (entries.Count > 0)
                    {
                        return entries;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE KV PARSE ERROR: path={schemaPath} msg={ex.Message}");
                }
            }

            return null;
        }

        private static bool TryParseSteamKvBinary(byte[] bytes, out Dictionary<string, object> root)
        {
            root = null;
            if (bytes == null || bytes.Length < 2)
            {
                return false;
            }

            var offset = 0;
            var firstType = bytes[offset++];
            if (firstType == 0x00)
            {
                if (!TryReadSteamKvCString(bytes, ref offset, out _))
                {
                    return false;
                }

                return TryParseSteamKvNodeChildren(bytes, ref offset, out root);
            }

            offset = 0;
            return TryParseSteamKvNodeChildren(bytes, ref offset, out root);
        }

        private static bool TryParseSteamKvNodeChildren(byte[] bytes, ref int offset, out Dictionary<string, object> node)
        {
            node = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            while (offset < bytes.Length)
            {
                var type = bytes[offset++];
                if (type == 0x08)
                {
                    return true;
                }

                if (!TryReadSteamKvCString(bytes, ref offset, out var key))
                {
                    return false;
                }

                object value;
                switch (type)
                {
                    case 0x00:
                        if (!TryParseSteamKvNodeChildren(bytes, ref offset, out var childNode))
                        {
                            return false;
                        }

                        value = childNode;
                        break;
                    case 0x01:
                        if (!TryReadSteamKvCString(bytes, ref offset, out var stringValue))
                        {
                            return false;
                        }

                        value = stringValue;
                        break;
                    case 0x02:
                        if (offset + 4 > bytes.Length)
                        {
                            return false;
                        }

                        value = BitConverter.ToInt32(bytes, offset);
                        offset += 4;
                        break;
                    case 0x03:
                        if (offset + 4 > bytes.Length)
                        {
                            return false;
                        }

                        value = BitConverter.ToSingle(bytes, offset);
                        offset += 4;
                        break;
                    case 0x04:
                        if (offset + 4 > bytes.Length)
                        {
                            return false;
                        }

                        value = BitConverter.ToInt32(bytes, offset);
                        offset += 4;
                        break;
                    case 0x05:
                        if (!TryReadSteamKvWideString(bytes, ref offset, out var wideStringValue))
                        {
                            return false;
                        }

                        value = wideStringValue;
                        break;
                    case 0x06:
                        if (offset + 4 > bytes.Length)
                        {
                            return false;
                        }

                        value = BitConverter.ToInt32(bytes, offset);
                        offset += 4;
                        break;
                    case 0x07:
                        if (offset + 8 > bytes.Length)
                        {
                            return false;
                        }

                        value = BitConverter.ToUInt64(bytes, offset);
                        offset += 8;
                        break;
                    default:
                        return false;
                }

                AddSteamKvValue(node, key, value);
            }

            return true;
        }

        private static bool TryReadSteamKvCString(byte[] bytes, ref int offset, out string value)
        {
            value = null;
            if (bytes == null || offset < 0 || offset >= bytes.Length)
            {
                return false;
            }

            var start = offset;
            while (offset < bytes.Length && bytes[offset] != 0x00)
            {
                offset++;
            }

            if (offset >= bytes.Length)
            {
                return false;
            }

            value = System.Text.Encoding.UTF8.GetString(bytes, start, offset - start);
            offset++;
            return true;
        }

        private static bool TryReadSteamKvWideString(byte[] bytes, ref int offset, out string value)
        {
            value = null;
            if (bytes == null || offset < 0 || offset >= bytes.Length)
            {
                return false;
            }

            var start = offset;
            while (offset + 1 < bytes.Length)
            {
                if (bytes[offset] == 0x00 && bytes[offset + 1] == 0x00)
                {
                    var length = offset - start;
                    value = length > 0
                        ? System.Text.Encoding.Unicode.GetString(bytes, start, length)
                        : string.Empty;
                    offset += 2;
                    return true;
                }

                offset += 2;
            }

            return false;
        }

        private static void AddSteamKvValue(IDictionary<string, object> node, string key, object value)
        {
            if (node == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (node.TryGetValue(key, out var existing))
            {
                if (existing is List<object> list)
                {
                    list.Add(value);
                }
                else
                {
                    node[key] = new List<object> { existing, value };
                }

                return;
            }

            node[key] = value;
        }

        private static List<SteamKvSchemaEntry> ExtractSteamAppCacheSchemaFromKv(Dictionary<string, object> root)
        {
            var results = new List<SteamKvSchemaEntry>();
            WalkSteamAppCacheSchemaNode(root, new List<string> { "root" }, results);

            var deduplicated = new List<SteamKvSchemaEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in results)
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.ApiName) || seen.Contains(entry.ApiName))
                {
                    continue;
                }

                seen.Add(entry.ApiName);
                deduplicated.Add(entry);
            }

            return deduplicated;
        }

        private static void WalkSteamAppCacheSchemaNode(object node, List<string> path, List<SteamKvSchemaEntry> results)
        {
            var dict = AsSteamKvDictionary(node);
            if (dict == null)
            {
                return;
            }

            var bitsNode = GetSteamKvValue(dict, "bits");
            var bitsDict = AsSteamKvDictionary(bitsNode);
            var statId = TryParseSteamKvInt(path.LastOrDefault());
            if (bitsDict != null && statId.HasValue)
            {
                foreach (var pair in bitsDict)
                {
                    foreach (var bitNode in ExpandSteamKvValues(pair.Value))
                    {
                        var bitDict = AsSteamKvDictionary(bitNode);
                        if (bitDict == null)
                        {
                            continue;
                        }

                        var bit = TryParseSteamKvInt(GetSteamKvValue(bitDict, "bit")) ?? TryParseSteamKvInt(pair.Key);
                        if (!bit.HasValue)
                        {
                            continue;
                        }

                        var apiName = FirstNonEmpty(
                            AsSteamKvString(GetSteamKvValue(bitDict, "name")),
                            AsSteamKvString(GetSteamKvValue(bitDict, "api")),
                            AsSteamKvString(GetSteamKvValue(bitDict, "statname")),
                            ResolveSteamKvLocalizedField(GetSteamKvValue(bitDict, "display"), "name"));

                        if (string.IsNullOrWhiteSpace(apiName))
                        {
                            continue;
                        }

                        results.Add(new SteamKvSchemaEntry
                        {
                            StatId = statId.Value,
                            Bit = bit.Value,
                            ApiName = apiName,
                            DisplayName = FirstNonEmpty(
                                ResolveSteamKvLocalizedField(GetSteamKvValue(bitDict, "display"), "name"),
                                AsSteamKvString(GetSteamKvValue(bitDict, "displayName")),
                                AsSteamKvString(GetSteamKvValue(bitDict, "name"))),
                            Description = FirstNonEmpty(
                                ResolveSteamKvLocalizedField(GetSteamKvValue(bitDict, "display"), "desc"),
                                AsSteamKvString(GetSteamKvValue(bitDict, "description")),
                                AsSteamKvString(GetSteamKvValue(bitDict, "desc"))),
                            IconHash = FirstNonEmpty(
                                ResolveSteamKvSimpleField(GetSteamKvValue(bitDict, "display"), "icon"),
                                AsSteamKvString(GetSteamKvValue(bitDict, "icon"))),
                            IconGrayHash = FirstNonEmpty(
                                ResolveSteamKvSimpleField(GetSteamKvValue(bitDict, "display"), "icon_gray"),
                                ResolveSteamKvSimpleField(GetSteamKvValue(bitDict, "display"), "icongray"),
                                AsSteamKvString(GetSteamKvValue(bitDict, "icon_gray"))),
                            Hidden = TryParseSteamKvBool(GetSteamKvValue(bitDict, "hidden")) ||
                                TryParseSteamKvBool(ResolveSteamKvRawField(GetSteamKvValue(bitDict, "display"), "hidden"))
                        });
                    }
                }
            }

            var legacyName = AsSteamKvString(GetSteamKvValue(dict, "name"));
            if (!string.IsNullOrWhiteSpace(legacyName) && TryInferSteamKvStatIdAndBit(path, out var legacyStatId, out var legacyBit))
            {
                results.Add(new SteamKvSchemaEntry
                {
                    StatId = legacyStatId,
                    Bit = legacyBit,
                    ApiName = legacyName,
                    DisplayName = FirstNonEmpty(
                        ResolveSteamKvLocalizedField(GetSteamKvValue(dict, "display"), "name"),
                        AsSteamKvString(GetSteamKvValue(dict, "display")),
                        legacyName),
                    Description = FirstNonEmpty(
                        ResolveSteamKvLocalizedField(GetSteamKvValue(dict, "display"), "desc"),
                        AsSteamKvString(GetSteamKvValue(dict, "description")),
                        AsSteamKvString(GetSteamKvValue(dict, "desc"))),
                    IconHash = FirstNonEmpty(
                        ResolveSteamKvSimpleField(GetSteamKvValue(dict, "display"), "icon"),
                        AsSteamKvString(GetSteamKvValue(dict, "icon"))),
                    IconGrayHash = FirstNonEmpty(
                        ResolveSteamKvSimpleField(GetSteamKvValue(dict, "display"), "icon_gray"),
                        ResolveSteamKvSimpleField(GetSteamKvValue(dict, "display"), "icongray"),
                        AsSteamKvString(GetSteamKvValue(dict, "icon_gray"))),
                    Hidden = TryParseSteamKvBool(GetSteamKvValue(dict, "hidden")) ||
                        TryParseSteamKvBool(ResolveSteamKvRawField(GetSteamKvValue(dict, "display"), "hidden"))
                });
            }

            foreach (var pair in dict)
            {
                foreach (var child in ExpandSteamKvValues(pair.Value))
                {
                    if (AsSteamKvDictionary(child) != null)
                    {
                        var childPath = new List<string>(path) { pair.Key };
                        WalkSteamAppCacheSchemaNode(child, childPath, results);
                    }
                }
            }
        }

        private static Dictionary<int, SteamKvUserStatData> ExtractSteamAppCacheUserStatsFromKv(Dictionary<string, object> root)
        {
            var stats = new Dictionary<int, SteamKvUserStatData>();
            WalkSteamAppCacheUserStatsNode(root, new List<string> { "root" }, stats);
            return stats;
        }

        private static void WalkSteamAppCacheUserStatsNode(object node, List<string> path, IDictionary<int, SteamKvUserStatData> stats)
        {
            var dict = AsSteamKvDictionary(node);
            if (dict == null)
            {
                return;
            }

            var dataNode = GetSteamKvValue(dict, "data");
            if (TryParseSteamKvUInt32(dataNode, out var dataU32))
            {
                var statId = TryParseSteamKvInt(path.LastOrDefault());
                if (statId.HasValue)
                {
                    var times = new Dictionary<int, long>();
                    var timesNode = FirstNonNull(
                        GetSteamKvValue(dict, "AchievementTimes"),
                        GetSteamKvValue(dict, "achievementTimes"),
                        GetSteamKvValue(dict, "AchievementsTimes"),
                        GetSteamKvValue(dict, "achievement_times"));

                    var timesDict = AsSteamKvDictionary(timesNode);
                    if (timesDict != null)
                    {
                        foreach (var pair in timesDict)
                        {
                            var bit = TryParseSteamKvInt(pair.Key);
                            var timestamp = TryParseSteamKvLong(pair.Value);
                            if (bit.HasValue && timestamp.HasValue)
                            {
                                times[bit.Value] = timestamp.Value;
                            }
                        }
                    }

                    stats[statId.Value] = new SteamKvUserStatData
                    {
                        DataU32 = dataU32,
                        UnlockTimes = times
                    };
                }
            }

            foreach (var pair in dict)
            {
                foreach (var child in ExpandSteamKvValues(pair.Value))
                {
                    if (AsSteamKvDictionary(child) != null)
                    {
                        var childPath = new List<string>(path) { pair.Key };
                        WalkSteamAppCacheUserStatsNode(child, childPath, stats);
                    }
                }
            }
        }

        private static Dictionary<string, object> AsSteamKvDictionary(object value)
        {
            if (value is Dictionary<string, object> dict)
            {
                return dict;
            }

            if (value is List<object> list)
            {
                return list.OfType<Dictionary<string, object>>().FirstOrDefault();
            }

            return null;
        }

        private static IEnumerable<object> ExpandSteamKvValues(object value)
        {
            if (value is List<object> list)
            {
                return list;
            }

            return value != null ? new[] { value } : Array.Empty<object>();
        }

        private static object GetSteamKvValue(IDictionary<string, object> dict, string key)
        {
            if (dict == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            if (dict.TryGetValue(key, out var value))
            {
                return value;
            }

            var pair = dict.FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(pair.Key) ? null : pair.Value;
        }

        private static string AsSteamKvString(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is string text)
            {
                return text;
            }

            if (value is Dictionary<string, object>)
            {
                return null;
            }

            if (value is List<object> list)
            {
                foreach (var item in list)
                {
                    var textValue = AsSteamKvString(item);
                    if (!string.IsNullOrWhiteSpace(textValue))
                    {
                        return textValue;
                    }
                }

                return null;
            }

            if (value is bool boolValue)
            {
                return boolValue ? "1" : "0";
            }

            if (value is int ||
                value is long ||
                value is uint ||
                value is ulong ||
                value is short ||
                value is ushort ||
                value is byte ||
                value is sbyte ||
                value is float ||
                value is double ||
                value is decimal)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            }

            return null;
        }

        private static string ResolveSteamKvLocalizedField(object containerNode, string fieldName)
        {
            var fieldNode = ResolveSteamKvRawField(containerNode, fieldName);
            var fieldText = AsSteamKvString(fieldNode);
            if (!string.IsNullOrWhiteSpace(fieldText))
            {
                return fieldText;
            }

            var dict = AsSteamKvDictionary(fieldNode);
            if (dict == null)
            {
                return null;
            }

            var english = AsSteamKvString(GetSteamKvValue(dict, "english"));
            if (!string.IsNullOrWhiteSpace(english))
            {
                return english;
            }

            foreach (var pair in dict)
            {
                if (string.Equals(pair.Key, "token", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = AsSteamKvString(pair.Value);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static string ResolveSteamKvSimpleField(object containerNode, string fieldName)
        {
            var fieldNode = ResolveSteamKvRawField(containerNode, fieldName);
            return AsSteamKvString(fieldNode);
        }

        private static object ResolveSteamKvRawField(object containerNode, string fieldName)
        {
            var containerDict = AsSteamKvDictionary(containerNode);
            return containerDict == null ? null : GetSteamKvValue(containerDict, fieldName);
        }

        private static int? TryParseSteamKvInt(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
            {
                return (int)longValue;
            }

            if (value is uint uintValue && uintValue <= int.MaxValue)
            {
                return (int)uintValue;
            }

            if (value is ulong ulongValue && ulongValue <= int.MaxValue)
            {
                return (int)ulongValue;
            }

            if (value is List<object> list)
            {
                foreach (var item in list)
                {
                    var parsed = TryParseSteamKvInt(item);
                    if (parsed.HasValue)
                    {
                        return parsed;
                    }
                }

                return null;
            }

            var text = AsSteamKvString(value);
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt)
                ? parsedInt
                : (int?)null;
        }

        private static long? TryParseSteamKvLong(object value)
        {
            if (value == null)
            {
                return null;
            }

            if (value is long longValue)
            {
                return longValue;
            }

            if (value is int intValue)
            {
                return intValue;
            }

            if (value is uint uintValue)
            {
                return uintValue;
            }

            if (value is ulong ulongValue && ulongValue <= long.MaxValue)
            {
                return (long)ulongValue;
            }

            if (value is List<object> list)
            {
                foreach (var item in list)
                {
                    var parsed = TryParseSteamKvLong(item);
                    if (parsed.HasValue)
                    {
                        return parsed;
                    }
                }

                return null;
            }

            var text = AsSteamKvString(value);
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong)
                ? parsedLong
                : (long?)null;
        }

        private static bool TryParseSteamKvUInt32(object value, out uint result)
        {
            result = 0;
            if (value == null)
            {
                return false;
            }

            if (value is uint uintValue)
            {
                result = uintValue;
                return true;
            }

            if (value is int intValue)
            {
                result = unchecked((uint)intValue);
                return true;
            }

            if (value is long longValue)
            {
                result = unchecked((uint)longValue);
                return true;
            }

            if (value is ulong ulongValue)
            {
                result = unchecked((uint)ulongValue);
                return true;
            }

            if (value is List<object> list)
            {
                foreach (var item in list)
                {
                    if (TryParseSteamKvUInt32(item, out result))
                    {
                        return true;
                    }
                }

                return false;
            }

            var text = AsSteamKvString(value);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLong))
            {
                result = unchecked((uint)parsedLong);
                return true;
            }

            return false;
        }

        private static bool TryParseSteamKvBool(object value)
        {
            if (value == null)
            {
                return false;
            }

            if (value is bool boolValue)
            {
                return boolValue;
            }

            if (value is int intValue)
            {
                return intValue != 0;
            }

            if (value is long longValue)
            {
                return longValue != 0;
            }

            if (value is List<object> list)
            {
                return list.Any(TryParseSteamKvBool);
            }

            var text = AsSteamKvString(value)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            return string.Equals(text, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryInferSteamKvStatIdAndBit(IReadOnlyList<string> path, out int statId, out int bit)
        {
            statId = 0;
            bit = 0;
            if (path == null || path.Count == 0)
            {
                return false;
            }

            var foundBit = false;
            for (var index = path.Count - 1; index >= 0; index--)
            {
                if (!int.TryParse(path[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
                {
                    continue;
                }

                if (!foundBit)
                {
                    bit = parsedValue;
                    foundBit = true;
                    continue;
                }

                statId = parsedValue;
                return true;
            }

            return false;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        private static object FirstNonNull(params object[] values)
        {
            return values?.FirstOrDefault(value => value != null);
        }

        /// <summary>
        /// Reads earned achievement status directly from UserGameStats_*.bin without needing the schema file.
        /// Uses the Steam binary VDF format where achievements are stored as nested objects:
        ///   \x00 "achievements" \x00
        ///     \x00 [API_NAME] \x00
        ///       \x02 "achieved" \x00 [int32_LE: 0=locked, 1=earned]
        ///       ...
        ///     \x08
        ///   \x08
        /// </summary>
        private Dictionary<string, LocalEntry> TryGetSteamUserStatsDirectEntries(int appId, Game game = null)
        {
            var result = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            var unlockTimes = TryGetSteamAppCacheUnlockTimes(appId, game);

            foreach (var userStatsPath in GetSteamAppCacheUserStatsFilePaths(appId, game))
            {
                try
                {
                    if (!File.Exists(userStatsPath))
                        continue;

                    var bytes = File.ReadAllBytes(userStatsPath);
                    ParseVdfAchievements(bytes, result, unlockTimes);

                    if (result.Count > 0)
                        break; // use first successful file
                }
                catch (Exception ex)
                {
                    Log($"STEAM USERSTATS DIRECT ERROR: path={userStatsPath} msg={ex.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Parses Steam binary VDF to extract achievement earned status.
        /// Scans for the "achievements" section and reads nested objects with "achieved" int32 fields.
        /// </summary>
        private static void ParseVdfAchievements(byte[] bytes, Dictionary<string, LocalEntry> result, Dictionary<int, long> unlockTimes)
        {
            if (bytes == null || bytes.Length == 0) return;

            // Find "achievements" key in the binary VDF (preceded by object-type 0x00)
            var achievementsKey = System.Text.Encoding.ASCII.GetBytes("achievements\x00");
            var sectionStart = -1;
            for (var i = 0; i <= bytes.Length - achievementsKey.Length; i++)
            {
                if (!ByteSequenceEquals(bytes, i, achievementsKey)) continue;
                // Preceded by 0x00 (object type byte) or at start of file
                if (i == 0 || bytes[i - 1] == 0x00 || bytes[i - 1] == 0x08)
                {
                    sectionStart = i + achievementsKey.Length; // position after "achievements\0"
                    break;
                }
            }

            if (sectionStart < 0) return;

            var pos = sectionStart;

            // Parse each achievement: \x00 [api_name] \x00 { fields } \x08
            while (pos < bytes.Length)
            {
                var typeByte = bytes[pos++];
                if (typeByte == 0x08) break; // end of achievements section
                if (typeByte != 0x00)
                {
                    // Non-object entry: skip key string then value to stay aligned
                    while (pos < bytes.Length && bytes[pos] != 0x00) pos++;
                    if (pos < bytes.Length) pos++; // skip null terminator
                    pos = SkipVdfValue(bytes, pos, typeByte);
                    continue;
                }

                // Read achievement API name
                var nameStart = pos;
                while (pos < bytes.Length && bytes[pos] != 0x00) pos++;
                if (pos >= bytes.Length) break;

                var nameLen = pos - nameStart;
                pos++; // skip null terminator

                if (nameLen < 2 || nameLen > 128) { SkipVdfObjectContents(bytes, ref pos); continue; }

                var apiName = System.Text.Encoding.ASCII.GetString(bytes, nameStart, nameLen);
                if (!System.Text.RegularExpressions.Regex.IsMatch(apiName, @"^[A-Za-z0-9_.\-]+$") ||
                    string.Equals(apiName, "achievements", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(apiName, "stats", StringComparison.OrdinalIgnoreCase))
                {
                    SkipVdfObjectContents(bytes, ref pos);
                    continue;
                }

                // Parse fields inside this achievement object to find "achieved"
                var earnedValue = ReadVdfAchievementEarned(bytes, ref pos);

                if (earnedValue < 0) continue; // couldn't find "achieved" field

                unlockTimes.TryGetValue(result.Count, out var timestamp);
                if (!result.ContainsKey(apiName))
                {
                    result[apiName] = new LocalEntry
                    {
                        earned = earnedValue == 1,
                        earned_time = earnedValue == 1 ? timestamp : 0,
                        displayName = apiName,
                        description = string.Empty
                    };
                }
            }
        }

        /// <summary>
        /// Reads fields inside an achievement VDF object, looking for the "achieved" int32 field.
        /// Advances pos past the end of the object (\x08). Returns the achieved value or -1 if not found.
        /// </summary>
        private static int ReadVdfAchievementEarned(byte[] bytes, ref int pos)
        {
            var achieved = -1;
            while (pos < bytes.Length)
            {
                var typeByte = bytes[pos++];
                if (typeByte == 0x08) break; // end of object

                // Read key
                var keyStart = pos;
                while (pos < bytes.Length && bytes[pos] != 0x00) pos++;
                if (pos >= bytes.Length) break;
                var keyLen = pos - keyStart;
                var isAchievedKey = keyLen == 8 && // "achieved" = 8 chars
                    bytes[keyStart + 0] == 'a' && bytes[keyStart + 1] == 'c' &&
                    bytes[keyStart + 2] == 'h' && bytes[keyStart + 3] == 'i' &&
                    bytes[keyStart + 4] == 'e' && bytes[keyStart + 5] == 'v' &&
                    bytes[keyStart + 6] == 'e' && bytes[keyStart + 7] == 'd';
                pos++; // skip null terminator

                pos = SkipVdfValue(bytes, pos, typeByte, isAchievedKey ? (int?)null : 0,
                    isAchievedKey ? (Action<int>)(v => { if (achieved < 0) achieved = v; }) : null);
            }
            return achieved;
        }

        /// <summary>
        /// Skips a VDF value based on its type byte. Returns the new position after the value.
        /// If typeByte is 0x02 (int32) and onInt is provided, calls onInt with the value before skipping.
        /// </summary>
        private static int SkipVdfValue(byte[] bytes, int pos, byte typeByte, int? unused = null, Action<int> onInt = null)
        {
            switch (typeByte)
            {
                case 0x01: // string: read until null
                    while (pos < bytes.Length && bytes[pos] != 0x00) pos++;
                    if (pos < bytes.Length) pos++; // skip null
                    break;
                case 0x02: // int32
                    if (pos + 4 <= bytes.Length)
                    {
                        onInt?.Invoke(BitConverter.ToInt32(bytes, pos));
                        pos += 4;
                    }
                    break;
                case 0x03: // float32
                    pos += 4;
                    break;
                case 0x04: // pointer (same as int32)
                    pos += 4;
                    break;
                case 0x05: // wstring: skip until double-null
                    while (pos + 1 < bytes.Length && !(bytes[pos] == 0x00 && bytes[pos + 1] == 0x00)) pos++;
                    if (pos + 1 < bytes.Length) pos += 2;
                    break;
                case 0x06: // color (4 bytes)
                    pos += 4;
                    break;
                case 0x07: // uint64 (8 bytes)
                    pos += 8;
                    break;
                case 0x00: // nested object
                    SkipVdfObjectContents(bytes, ref pos);
                    break;
            }
            return pos;
        }

        /// <summary>Skips all contents of a VDF object until \x08 end marker. Advances pos past \x08.</summary>
        private static void SkipVdfObjectContents(byte[] bytes, ref int pos)
        {
            while (pos < bytes.Length)
            {
                var typeByte = bytes[pos++];
                if (typeByte == 0x08) return;
                // Skip key
                while (pos < bytes.Length && bytes[pos] != 0x00) pos++;
                if (pos < bytes.Length) pos++;
                // Skip value
                pos = SkipVdfValue(bytes, pos, typeByte);
            }
        }

        private List<SteamAppCacheSchemaEntry> TryGetSteamAppCacheSchema(int appId)
        {
            return TryGetSteamAppCacheSchemaSections(appId)
                ?.SelectMany(section => section.Entries)
                .ToList();
        }

        private List<SteamAppCacheSchemaSection> TryGetSteamAppCacheSchemaSections(int appId)
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

                    var sections = new List<SteamAppCacheSchemaSection>();
                    var sectionIndex = 0;

                    while (sectionIndex + 1 < tokens.Count)
                    {
                        if (!int.TryParse(tokens[sectionIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sectionId) ||
                            !string.Equals(tokens[sectionIndex + 1], "bits", StringComparison.OrdinalIgnoreCase))
                        {
                            sectionIndex++;
                            continue;
                        }

                        var entries = new List<SteamAppCacheSchemaEntry>();
                        var index = sectionIndex + 2;

                        while (index < tokens.Count)
                        {
                            if (index + 1 < tokens.Count &&
                                int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
                                string.Equals(tokens[index + 1], "bits", StringComparison.OrdinalIgnoreCase))
                            {
                                break;
                            }

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
                                if (int.TryParse(tokens[index], NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
                                    index + 1 < tokens.Count &&
                                    (string.Equals(tokens[index + 1], "name", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(tokens[index + 1], "bits", StringComparison.OrdinalIgnoreCase)))
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

                            if (IsSteamAppCacheAchievementEntry(entry))
                            {
                                entries.Add(entry);
                            }
                        }

                        AddSteamAppCacheSchemaSection(sections, entries, sectionId);
                        sectionIndex = index;
                    }

                    if (sections.Count > 0)
                    {
                        return sections;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE SCHEMA ERROR: path={schemaPath} msg={ex.Message}");
                }
            }

            return null;
        }

        private static void AddSteamAppCacheSchemaSection(List<SteamAppCacheSchemaSection> sections, List<SteamAppCacheSchemaEntry> entries, int? sectionId = null)
        {
            if (sections == null || entries == null || entries.Count == 0)
            {
                return;
            }

            sections.Add(new SteamAppCacheSchemaSection
            {
                SectionId = sectionId,
                Entries = entries
            });
        }

        private static bool IsSteamAppCacheAchievementEntry(SteamAppCacheSchemaEntry entry)
        {
            return entry != null
                && !string.IsNullOrWhiteSpace(entry.ApiName)
                && (!string.IsNullOrWhiteSpace(entry.DisplayName)
                    || !string.IsNullOrWhiteSpace(entry.Description)
                    || !string.IsNullOrWhiteSpace(entry.IconHash)
                    || !string.IsNullOrWhiteSpace(entry.IconGrayHash));
        }

        private Dictionary<int, long> TryGetSteamAppCacheUnlockTimes(int appId, Game game = null)
        {
            var result = new Dictionary<int, long>();
            foreach (var section in TryGetSteamAppCacheUnlockTimeSections(appId, game))
            {
                if (section?.UnlockTimes == null)
                {
                    continue;
                }

                foreach (var pair in section.UnlockTimes)
                {
                    if (!result.ContainsKey(pair.Key))
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }

            return result;
        }

        private List<SteamAppCacheUnlockTimeSection> TryGetSteamAppCacheUnlockTimeSections(int appId, Game game = null)
        {
            var sections = new List<SteamAppCacheUnlockTimeSection>();
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            foreach (var userStatsPath in GetSteamAppCacheUserStatsFilePaths(appId, game))
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

                        var section = new Dictionary<int, long>();
                        TryFindSteamAppCacheSectionMetadata(bytes, position, out var sectionId, out var sectionDataValue);
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
                                section[entryIndex] = timestamp;
                                cursor += 4;
                                continue;
                            }

                            break;
                        }

                        if (section.Count > 0)
                        {
                            sections.Add(new SteamAppCacheUnlockTimeSection
                            {
                                SectionId = sectionId,
                                DataValue = sectionDataValue,
                                UnlockTimes = section
                            });
                        }
                    }

                    if (sections.Count > 0)
                    {
                        return sections;
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE USERSTATS ERROR: path={userStatsPath} msg={ex.Message}");
                }
            }

            return sections;
        }

        private static void TryFindSteamAppCacheSectionMetadata(
            byte[] bytes,
            int markerPosition,
            out int? sectionId,
            out uint? dataValue)
        {
            sectionId = null;
            dataValue = null;

            if (bytes == null || bytes.Length == 0 || markerPosition <= 0)
            {
                return;
            }

            for (var tokenEnd = markerPosition - 1; tokenEnd > 0; tokenEnd--)
            {
                if (bytes[tokenEnd] != 0)
                {
                    continue;
                }

                var tokenStart = tokenEnd - 1;
                while (tokenStart >= 0 && bytes[tokenStart] != 0)
                {
                    tokenStart--;
                }

                tokenStart++;
                var tokenLength = tokenEnd - tokenStart;
                if (tokenLength <= 0 || tokenLength > 10)
                {
                    continue;
                }

                var token = System.Text.Encoding.ASCII.GetString(bytes, tokenStart, tokenLength);
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSectionId))
                {
                    continue;
                }

                var cursor = tokenEnd + 1;
                if (cursor < bytes.Length && bytes[cursor] == 0x02)
                {
                    cursor++;
                }

                if (cursor + 4 < bytes.Length &&
                    bytes[cursor + 0] == (byte)'d' &&
                    bytes[cursor + 1] == (byte)'a' &&
                    bytes[cursor + 2] == (byte)'t' &&
                    bytes[cursor + 3] == (byte)'a' &&
                    bytes[cursor + 4] == 0x00)
                {
                    sectionId = parsedSectionId;
                    var valuePosition = cursor + 5;
                    if (valuePosition + 4 <= bytes.Length)
                    {
                        dataValue = BitConverter.ToUInt32(bytes, valuePosition);
                    }

                    return;
                }
            }
        }

        private static void MergeSteamAppCacheEntry(
            Dictionary<string, LocalEntry> entries,
            SteamAppCacheSchemaEntry schemaEntry,
            bool unlocked,
            long timestamp,
            int appId)
        {
            if (entries == null || schemaEntry == null || string.IsNullOrWhiteSpace(schemaEntry.ApiName))
            {
                return;
            }

            var merged = new LocalEntry
            {
                earned = unlocked,
                earned_time = unlocked ? timestamp : 0,
                displayName = string.IsNullOrWhiteSpace(schemaEntry.DisplayName) ? schemaEntry.ApiName : schemaEntry.DisplayName,
                description = schemaEntry.Description ?? string.Empty,
                icon = BuildSteamAchievementIconUrl(appId, schemaEntry.IconHash),
                iconGray = BuildSteamAchievementIconUrl(appId, schemaEntry.IconGrayHash),
                hidden = schemaEntry.Hidden
            };

            if (!entries.TryGetValue(schemaEntry.ApiName, out var existing))
            {
                entries[schemaEntry.ApiName] = merged;
                return;
            }

            existing.earned = existing.earned || merged.earned;
            existing.earned_time = Math.Max(existing.earned_time, merged.earned_time);
            if (string.IsNullOrWhiteSpace(existing.displayName))
            {
                existing.displayName = merged.displayName;
            }

            if (string.IsNullOrWhiteSpace(existing.description))
            {
                existing.description = merged.description;
            }

            if (string.IsNullOrWhiteSpace(existing.icon))
            {
                existing.icon = merged.icon;
            }

            if (string.IsNullOrWhiteSpace(existing.iconGray))
            {
                existing.iconGray = merged.iconGray;
            }

            existing.hidden = existing.hidden || merged.hidden;
            entries[schemaEntry.ApiName] = existing;
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

            return $"https://shared.fastly.steamstatic.com/community_assets/images/apps/{appId}/{iconHash}";
        }

        private string GetSelectedSteamAppCacheUserId(Game game = null)
        {
            if (game != null && TryGetSteamAppCacheUserOverride(game.Id, out var overriddenUserId))
            {
                return overriddenUserId;
            }

            var settings = ProviderRegistry.Settings<LocalSettings>();
            return (settings?.SteamAppCacheUserId ?? string.Empty).Trim();
        }

        private bool IsSteamAppCacheDisabled(Game game = null)
        {
            return string.Equals(GetSelectedSteamAppCacheUserId(game), LocalSettings.SteamAppCacheUserNone, StringComparison.OrdinalIgnoreCase);
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

        private IEnumerable<string> GetSteamAppCacheUserStatsFilePaths(int appId, Game game = null)
        {
            if (IsSteamAppCacheDisabled(game))
            {
                return Array.Empty<string>();
            }

            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var suffix = "_" + appId.ToString(CultureInfo.InvariantCulture) + ".bin";

            // When a specific user is explicitly configured use exact-match lookup only.
            // In auto mode (no explicit selection) always scan all UserGameStats_*_{appId}.bin
            // files so that any account ID present in the stats folder is found, regardless
            // of whether that account also has a userdata directory.
            var selectedUserId = GetSelectedSteamAppCacheUserId(game);
            var isExplicitUser = !string.IsNullOrWhiteSpace(selectedUserId);

            foreach (var statsRoot in GetSteamAppCacheStatsRoots())
            {
                if (!Directory.Exists(statsRoot))
                {
                    continue;
                }

                try
                {
                    if (isExplicitUser)
                    {
                        foreach (var accountId in ExpandSteamAccountIdCandidates(selectedUserId))
                        {
                            var exactPath = Path.Combine(
                                statsRoot,
                                $"UserGameStats_{accountId}_{appId.ToString(CultureInfo.InvariantCulture)}.bin");

                            if (File.Exists(exactPath))
                            {
                                candidates.Add(exactPath);
                            }
                        }
                    }
                    else
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

            return candidates
                .Select(path => new
                {
                    Path = path,
                    LastWriteUtc = TryGetFileLastWriteTimeUtc(path)
                })
                .OrderByDescending(item => item.LastWriteUtc)
                .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Path)
                .ToList();
        }

        private static DateTime TryGetFileLastWriteTimeUtc(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return DateTime.MinValue;
            }

            try
            {
                return File.GetLastWriteTimeUtc(path);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private IEnumerable<string> GetPreferredSteamAccountIds(Game game = null)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var selectedSteamAppCacheUserId = GetSelectedSteamAppCacheUserId(game);
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

        internal IReadOnlyList<LocalSteamAppCacheUserOption> GetAvailableSteamAppCacheUsers()
        {
            var personaNamesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var discoveredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var steamBasePath in GetSteamInstallRoots())
            {
                TryReadSteamLoginUsers(steamBasePath, personaNamesById);
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
                        var userId = Path.GetFileName(userDir)?.Trim();
                        if (!string.IsNullOrWhiteSpace(userId) && Regex.IsMatch(userId, @"^\d+$"))
                        {
                            discoveredIds.Add(userId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM USER DISCOVERY ERROR: root={userdataRoot} msg={ex.Message}");
                }
            }

            foreach (var statsRoot in GetSteamAppCacheStatsRoots())
            {
                if (string.IsNullOrWhiteSpace(statsRoot) || !Directory.Exists(statsRoot))
                {
                    continue;
                }

                try
                {
                    foreach (var statsPath in Directory.EnumerateFiles(statsRoot, "UserGameStats_*_*.bin", SearchOption.TopDirectoryOnly))
                    {
                        var parts = Path.GetFileNameWithoutExtension(statsPath)?.Split('_');
                        if (parts == null || parts.Length < 3)
                        {
                            continue;
                        }

                        var userId = parts[1]?.Trim();
                        if (!string.IsNullOrWhiteSpace(userId) && Regex.IsMatch(userId, @"^\d+$"))
                        {
                            discoveredIds.Add(userId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"STEAM APPCACHE USER DISCOVERY ERROR: root={statsRoot} msg={ex.Message}");
                }
            }

            return discoveredIds
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Select(id => new LocalSteamAppCacheUserOption(id, BuildSteamAppCacheUserDisplayName(id, personaNamesById)))
                .ToList();
        }

        private void TryReadSteamLoginUsers(string steamBasePath, IDictionary<string, string> personaNamesById)
        {
            if (string.IsNullOrWhiteSpace(steamBasePath) || personaNamesById == null)
            {
                return;
            }

            var loginUsersPath = Path.Combine(steamBasePath, "config", "loginusers.vdf");
            if (!File.Exists(loginUsersPath))
            {
                return;
            }

            try
            {
                var lines = File.ReadAllLines(loginUsersPath);
                string currentSteamId = null;

                foreach (var rawLine in lines)
                {
                    var line = rawLine?.Trim();
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    var steamIdMatch = Regex.Match(line, "^\"(?<id>\\d{5,})\"$");
                    if (steamIdMatch.Success)
                    {
                        currentSteamId = steamIdMatch.Groups["id"].Value;
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(currentSteamId))
                    {
                        continue;
                    }

                    var personaMatch = Regex.Match(line, @"^""PersonaName""\s+""(?<name>.*)""$");
                    if (!personaMatch.Success)
                    {
                        continue;
                    }

                    var accountId = TryConvertSteamId64ToAccountId(currentSteamId);
                    if (!string.IsNullOrWhiteSpace(accountId) && !personaNamesById.ContainsKey(accountId))
                    {
                        personaNamesById[accountId] = personaMatch.Groups["name"].Value.Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"STEAM LOGINUSERS READ ERROR: root={steamBasePath} msg={ex.Message}");
            }
        }

        private static string TryConvertSteamId64ToAccountId(string steamId)
        {
            if (string.IsNullOrWhiteSpace(steamId))
            {
                return null;
            }

            if (!ulong.TryParse(steamId.Trim(), out var steamIdValue) || steamIdValue < 76561197960265728UL)
            {
                return null;
            }

            var accountId = steamIdValue - 76561197960265728UL;
            return accountId <= uint.MaxValue
                ? accountId.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        private static string BuildSteamAppCacheUserDisplayName(string userId, IReadOnlyDictionary<string, string> personaNamesById)
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return string.Empty;
            }

            if (personaNamesById != null && personaNamesById.TryGetValue(userId, out var personaName) && !string.IsNullOrWhiteSpace(personaName))
            {
                return $"{personaName} ({userId})";
            }

            return userId;
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

            // Also accept a configured path that IS the stats directory directly
            // (e.g. user typed E:\Programs\Steam\appcache\stats)
            var settings = ProviderRegistry.Settings<LocalSettings>();
            var rawPath = settings?.SteamUserdataPath?.Trim();
            if (!string.IsNullOrWhiteSpace(rawPath))
            {
                var expanded = Environment.ExpandEnvironmentVariables(rawPath);
                if (!string.IsNullOrWhiteSpace(expanded) &&
                    string.Equals(Path.GetFileName(expanded), "stats", StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(expanded))
                {
                    roots.Add(expanded);
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

            jsonPath = ResolveAchievementFilePath(localFolderPath, "achievements.json");
            return !string.IsNullOrWhiteSpace(jsonPath);
        }

        internal bool TryResolveWritableAchievementFilePath(Game game, out string filePath, out LocalAchievementFileKind fileKind, out int appId, out bool isOverridden)
        {
            filePath = null;
            fileKind = LocalAchievementFileKind.Json;
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

            var jsonPath = ResolveAchievementFilePath(localFolderPath, "achievements.json");
            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                filePath = jsonPath;
                fileKind = LocalAchievementFileKind.Json;
                return true;
            }

            var iniPath = ResolveAchievementFilePath(localFolderPath, "achievements.ini");
            if (!string.IsNullOrWhiteSpace(iniPath) && File.Exists(iniPath))
            {
                filePath = iniPath;
                fileKind = LocalAchievementFileKind.Ini;
                return true;
            }

            return false;
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

            return TryResolveLocalFolder(
                game,
                appId.ToString(CultureInfo.InvariantCulture),
                out selectedFolderPath,
                out candidateFolders,
                out isOverridden,
                out isAmbiguous);
        }

        internal void ResetLocalFolderDiscovery(Game game)
        {
            if (game == null)
            {
                return;
            }

            if (TryResolveAppId(game, out var appId, out _))
            {
                lock (_discoveryCacheLock)
                {
                    _localFolderCandidatesCache.Remove(appId.ToString(CultureInfo.InvariantCulture));
                }
            }

            lock (ReportedAmbiguousFolderGames)
            {
                ReportedAmbiguousFolderGames.Remove(game.Id);
            }
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

        public async Task<LocalAchievementEditResult> SaveLocalAchievementsAsync(Game game, IReadOnlyCollection<AchievementDetail> achievements, CancellationToken token)
        {
            if (game == null)
            {
                return new LocalAchievementEditResult
                {
                    Success = false,
                    Message = ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame")
                };
            }

            if (achievements == null || achievements.Count == 0)
            {
                return new LocalAchievementEditResult
                {
                    Success = false,
                    Message = "No achievements were provided to save."
                };
            }

            if (!TryResolveWritableAchievementFilePath(game, out var filePath, out var fileKind, out var appId, out var isOverridden))
            {
                Log($"LOCAL EDIT SAVE: unresolved writable file for game='{game?.Name}' appId={appId} override={isOverridden}");
                return new LocalAchievementEditResult
                {
                    Success = false,
                    AppId = appId,
                    UsedOverride = isOverridden,
                    Message = "This Local provider setup does not use a writable achievements.json or achievements.ini file."
                };
            }

            Dictionary<string, LocalEntry> existingEntries;
            if (fileKind == LocalAchievementFileKind.Json)
            {
                existingEntries = await LoadLocalEntriesAsync(filePath, (string)null).ConfigureAwait(false);
            }
            else
            {
                existingEntries = await LoadLocalEntriesAsync(null, (string)filePath).ConfigureAwait(false);
            }

            existingEntries ??= new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            var updatedCount = 0;
            var matchedByApiNameCount = 0;
            var matchedByDisplayNameCount = 0;
            var createdEntryCount = 0;
            var iniDesiredStateByKey = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

            var existingDisplayNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var duplicateDisplayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in existingEntries)
            {
                var displayNameValue = pair.Value.displayName;
                var displayNameKey = string.IsNullOrWhiteSpace(displayNameValue)
                    ? string.Empty
                    : displayNameValue.Trim();
                if (string.IsNullOrWhiteSpace(displayNameKey))
                {
                    continue;
                }

                if (existingDisplayNameMap.ContainsKey(displayNameKey))
                {
                    duplicateDisplayNames.Add(displayNameKey);
                    continue;
                }

                existingDisplayNameMap[displayNameKey] = pair.Key;
            }

            foreach (var duplicateDisplayName in duplicateDisplayNames)
            {
                existingDisplayNameMap.Remove(duplicateDisplayName);
            }

            foreach (var achievement in achievements)
            {
                token.ThrowIfCancellationRequested();

                if (achievement == null || string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                var resolvedEntryKey = achievement.ApiName;
                var resolvedByDisplayName = false;

                if (existingEntries.ContainsKey(resolvedEntryKey))
                {
                    matchedByApiNameCount++;
                }
                else
                {
                    var displayNameKey = achievement.DisplayName?.Trim();
                    if (!string.IsNullOrWhiteSpace(displayNameKey) &&
                        existingDisplayNameMap.TryGetValue(displayNameKey, out var mappedKey) &&
                        existingEntries.ContainsKey(mappedKey))
                    {
                        resolvedEntryKey = mappedKey;
                        resolvedByDisplayName = true;
                        matchedByDisplayNameCount++;
                    }
                    else
                    {
                        createdEntryCount++;
                    }
                }

                existingEntries.TryGetValue(resolvedEntryKey, out var currentEntry);
                var hasUnlockTime = achievement.Unlocked && achievement.UnlockTimeUtc.HasValue;
                var resolvedUnlockTime = hasUnlockTime
                    ? DateTime.SpecifyKind(achievement.UnlockTimeUtc.Value, DateTimeKind.Utc)
                    : (DateTime?)null;

                if (achievement.Unlocked && !resolvedUnlockTime.HasValue && currentEntry.earned_time > 0)
                {
                    resolvedUnlockTime = DateTimeOffset.FromUnixTimeSeconds(currentEntry.earned_time).UtcDateTime;
                }

                if (achievement.Unlocked && !resolvedUnlockTime.HasValue)
                {
                    resolvedUnlockTime = DateTime.UtcNow;
                }

                currentEntry.earned = achievement.Unlocked;
                currentEntry.earned_time = achievement.Unlocked && resolvedUnlockTime.HasValue
                    ? new DateTimeOffset(resolvedUnlockTime.Value).ToUnixTimeSeconds()
                    : 0;

                if (string.IsNullOrWhiteSpace(currentEntry.displayName))
                {
                    currentEntry.displayName = achievement.DisplayName;
                }

                if (string.IsNullOrWhiteSpace(currentEntry.description))
                {
                    currentEntry.description = achievement.Description;
                }

                if (string.IsNullOrWhiteSpace(currentEntry.icon))
                {
                    currentEntry.icon = achievement.UnlockedIconPath;
                }

                if (string.IsNullOrWhiteSpace(currentEntry.iconGray))
                {
                    currentEntry.iconGray = achievement.LockedIconPath;
                }

                currentEntry.hidden = achievement.Hidden;
                currentEntry.percent = achievement.GlobalPercentUnlocked;

                // Persist under the original file key when matched by display name.
                existingEntries[resolvedEntryKey] = currentEntry;

                if (!resolvedByDisplayName &&
                    !string.Equals(resolvedEntryKey, achievement.ApiName, StringComparison.OrdinalIgnoreCase) &&
                    !existingEntries.ContainsKey(achievement.ApiName))
                {
                    existingEntries[achievement.ApiName] = currentEntry;
                }

                iniDesiredStateByKey[resolvedEntryKey] = currentEntry;

                updatedCount++;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(filePath));
            if (fileKind == LocalAchievementFileKind.Json)
            {
                var serialized = JsonConvert.SerializeObject(existingEntries, Formatting.Indented);
                await Task.Run(() => File.WriteAllText(filePath, serialized, Encoding.UTF8), token).ConfigureAwait(false);
            }
            else
            {
                await Task.Run(() => UpdateIniUnlockFieldsPreservingFormat(filePath, iniDesiredStateByKey), token).ConfigureAwait(false);
            }

            Log($"LOCAL EDIT SAVE: game='{game?.Name}' appId={appId} kind={fileKind} file={filePath} requested={achievements.Count} updated={updatedCount} matchedApi={matchedByApiNameCount} matchedDisplay={matchedByDisplayNameCount} created={createdEntryCount}");

            return new LocalAchievementEditResult
            {
                Success = true,
                FilePath = filePath,
                FileKind = fileKind,
                AppId = appId,
                UsedOverride = isOverridden,
                UpdatedCount = updatedCount,
                Message = $"Saved {updatedCount} achievement updates to {filePath}. (matched: {matchedByApiNameCount + matchedByDisplayNameCount}, created: {createdEntryCount})"
            };
        }

        private static void UpdateIniUnlockFieldsPreservingFormat(string iniPath, IReadOnlyDictionary<string, LocalEntry> desiredBySection)
        {
            if (string.IsNullOrWhiteSpace(iniPath))
            {
                return;
            }

            var lines = File.Exists(iniPath)
                ? File.ReadAllLines(iniPath, Encoding.UTF8).ToList()
                : new List<string>();

            var sections = ParseIniSectionInfos(lines);
            var sectionLookup = sections.ToDictionary(section => section.Name, section => section, StringComparer.OrdinalIgnoreCase);

            var unlockedKeyName = sections.Select(section => section.UnlockedKeyName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "achieved";
            var timeKeyName = sections.Select(section => section.TimeKeyName)
                .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "timestamp";

            var useTrueFalseBooleans = sections.Any(section =>
                !string.IsNullOrWhiteSpace(section.UnlockedValueRaw) &&
                (section.UnlockedValueRaw.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 section.UnlockedValueRaw.Equals("false", StringComparison.OrdinalIgnoreCase)));

            foreach (var desired in desiredBySection)
            {
                var sectionName = desired.Key?.Trim();
                if (string.IsNullOrWhiteSpace(sectionName))
                {
                    continue;
                }

                var unlockedValue = useTrueFalseBooleans
                    ? (desired.Value.earned ? "true" : "false")
                    : (desired.Value.earned ? "1" : "0");
                var timestampValue = (desired.Value.earned ? desired.Value.earned_time : 0).ToString(CultureInfo.InvariantCulture);

                if (sectionLookup.TryGetValue(sectionName, out var sectionInfo))
                {
                    if (sectionInfo.UnlockedLineIndex >= 0)
                    {
                        lines[sectionInfo.UnlockedLineIndex] = $"{sectionInfo.UnlockedKeyName}={unlockedValue}";
                    }

                    if (sectionInfo.TimeLineIndex >= 0)
                    {
                        lines[sectionInfo.TimeLineIndex] = $"{sectionInfo.TimeKeyName}={timestampValue}";
                    }

                    continue;
                }

                // Keep sparse OnlineFix style: only create new sections for unlocked achievements.
                if (!desired.Value.earned)
                {
                    continue;
                }

                if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines.Last()))
                {
                    lines.Add(string.Empty);
                }

                lines.Add($"[{sectionName}]");
                lines.Add($"{unlockedKeyName}={unlockedValue}");
                lines.Add($"{timeKeyName}={timestampValue}");
                lines.Add(string.Empty);
            }

            File.WriteAllLines(iniPath, lines, Encoding.UTF8);
        }

        private static List<IniSectionInfo> ParseIniSectionInfos(IReadOnlyList<string> lines)
        {
            var result = new List<IniSectionInfo>();
            if (lines == null || lines.Count == 0)
            {
                return result;
            }

            IniSectionInfo current = null;
            for (var index = 0; index < lines.Count; index++)
            {
                var rawLine = lines[index] ?? string.Empty;
                var line = rawLine.Trim();

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal) && line.Length > 2)
                {
                    if (current != null)
                    {
                        current.EndLineIndex = index - 1;
                        result.Add(current);
                    }

                    current = new IniSectionInfo
                    {
                        Name = line.Substring(1, line.Length - 2).Trim(),
                        StartLineIndex = index,
                        EndLineIndex = index
                    };

                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                current.EndLineIndex = index;

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, separatorIndex).Trim();
                var value = line.Substring(separatorIndex + 1).Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                var normalizedKey = key.ToLowerInvariant();
                if ((normalizedKey == "achieved" || normalizedKey == "earned" || normalizedKey == "unlocked") && current.UnlockedLineIndex < 0)
                {
                    current.UnlockedLineIndex = index;
                    current.UnlockedKeyName = key;
                    current.UnlockedValueRaw = value;
                }

                if ((normalizedKey == "timestamp" || normalizedKey == "earned_time" || normalizedKey == "unlocktime" || normalizedKey == "unlock_time" || normalizedKey == "time") && current.TimeLineIndex < 0)
                {
                    current.TimeLineIndex = index;
                    current.TimeKeyName = key;
                }
            }

            if (current != null)
            {
                result.Add(current);
            }

            return result;
        }

        private static AchievementDetail CreateAchievementDetail(
            string apiName,
            LocalEntry entry,
            SchemaAchievement schemaAch,
            SchemaAndPercentages steamSchema,
            bool preferLocalizedSchemaText,
            bool includeSchemaIconFallback)
        {
            var localDisplayName = entry.displayName?.Trim();
            var schemaDisplayName = schemaAch?.DisplayName?.Trim();
            var displayName = ShouldPreferSchemaDisplayName(localDisplayName, schemaDisplayName, apiName, preferLocalizedSchemaText)
                ? schemaDisplayName
                : (!string.IsNullOrWhiteSpace(localDisplayName)
                    ? localDisplayName
                    : schemaDisplayName ?? apiName);

            var localDescription = entry.description?.Trim();
            var schemaDescription = schemaAch?.Description?.Trim();
            var description = ShouldPreferSchemaDescription(localDescription, schemaDescription, preferLocalizedSchemaText)
                ? schemaDescription
                : (!string.IsNullOrWhiteSpace(localDescription)
                    ? localDescription
                    : schemaDescription ?? "Local achievement from Local");

            var unlockedIcon = includeSchemaIconFallback &&
                               !string.IsNullOrWhiteSpace(schemaAch?.Icon) &&
                               (IsLowQualityIconPath(entry.icon, isLockedIcon: false) || IsUnresolvedLocalIconPath(entry.icon))
                ? schemaAch.Icon
                : (!string.IsNullOrWhiteSpace(entry.icon)
                    ? entry.icon
                    : includeSchemaIconFallback
                        ? schemaAch?.Icon ?? AchievementIconResolver.GetDefaultUnlockedIcon()
                        : AchievementIconResolver.GetDefaultUnlockedIcon());

            var lockedIcon = includeSchemaIconFallback &&
                             !string.IsNullOrWhiteSpace(schemaAch?.IconGray) &&
                             (IsLowQualityIconPath(entry.iconGray, isLockedIcon: true) || IsUnresolvedLocalIconPath(entry.iconGray))
                ? schemaAch.IconGray
                : (!string.IsNullOrWhiteSpace(entry.iconGray)
                    ? entry.iconGray
                    : includeSchemaIconFallback
                        ? schemaAch?.IconGray ?? AchievementIconResolver.GetDefaultIcon()
                        : AchievementIconResolver.GetDefaultIcon());

            // Mark as having a user-defined local source icon when the resolved icon is a local
            // file path (not a CDN URL and not a pack:// placeholder). This prevents the Default
            // icon override from replacing icons defined in local JSON/schema files.
            var hasSourceUnlockedIcon = IsLocalSourceIconPath(unlockedIcon);
            var hasSourceLockedIcon = IsLocalSourceIconPath(lockedIcon);

            var detail = new AchievementDetail
            {
                ApiName = apiName,
                DisplayName = displayName,
                Description = description,
                UnlockedIconPath = unlockedIcon,
                LockedIconPath = lockedIcon,
                HasSourceUnlockedIcon = hasSourceUnlockedIcon,
                HasSourceLockedIcon = hasSourceLockedIcon,
                Unlocked = entry.earned,
                Hidden = entry.hidden || (schemaAch?.Hidden == 1),
                Points = schemaAch?.Points,
                ProgressNum = NormalizeAchievementProgress(entry.progress, entry.max_progress),
                ProgressDenom = NormalizeAchievementProgressMaximum(entry.max_progress),
                UnlockTimeUtc = entry.earned && entry.earned_time > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(entry.earned_time).UtcDateTime
                    : (DateTime?)null
            };

            // Online schema percentages are the authoritative global Steam rarity.
            // Local formats frequently persist zero, stale, or player-specific values,
            // which previously prevented anonymous SteamHunters rarity from applying.
            double? globalPercent = schemaAch?.GlobalPercent;
            if (!globalPercent.HasValue)
            {
                if (steamSchema?.GlobalPercentages?.TryGetValue(apiName, out var resolvedPercent) == true)
                {
                    globalPercent = resolvedPercent;
                }
                else
                {
                    globalPercent = entry.percent;
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

        private static bool ShouldPreferSchemaDisplayName(string existingDisplayName, string incomingDisplayName, string apiName, bool preferLocalizedSchemaText)
        {
            incomingDisplayName = incomingDisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(incomingDisplayName) || IsLowQualityDisplayName(incomingDisplayName, apiName))
            {
                return false;
            }

            if (preferLocalizedSchemaText)
            {
                return !string.Equals(existingDisplayName?.Trim(), incomingDisplayName, StringComparison.OrdinalIgnoreCase);
            }

            return IsLowQualityDisplayName(existingDisplayName, apiName);
        }

        private static bool ShouldPreferSchemaDescription(string existingDescription, string incomingDescription, bool preferLocalizedSchemaText)
        {
            incomingDescription = incomingDescription?.Trim();
            if (string.IsNullOrWhiteSpace(incomingDescription) || IsLowQualityDescription(incomingDescription))
            {
                return false;
            }

            if (preferLocalizedSchemaText)
            {
                return !string.Equals(existingDescription?.Trim(), incomingDescription, StringComparison.OrdinalIgnoreCase);
            }

            return IsLowQualityDescription(existingDescription);
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

        private static Dictionary<string, SchemaAchievement> BuildSchemaLookupByIcon(IReadOnlyList<SchemaAchievement> achievements, bool useLockedIcon)
        {
            var lookup = new Dictionary<string, SchemaAchievement>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0)
            {
                return lookup;
            }

            foreach (var achievement in achievements)
            {
                var iconValue = useLockedIcon ? achievement?.IconGray : achievement?.Icon;
                var iconHash = ExtractAchievementIconHash(iconValue);
                if (string.IsNullOrWhiteSpace(iconHash))
                {
                    continue;
                }

                if (!lookup.TryGetValue(iconHash, out var existing))
                {
                    lookup[iconHash] = achievement;
                    continue;
                }

                if (existing != null && !ReferenceEquals(existing, achievement))
                {
                    // Colliding icon hash cannot be used as a unique join key.
                    lookup[iconHash] = null;
                }
            }

            return lookup;
        }

        private static string ExtractAchievementIconHash(string iconValue)
        {
            iconValue = iconValue?.Trim();
            if (string.IsNullOrWhiteSpace(iconValue))
            {
                return null;
            }

            if (Uri.TryCreate(iconValue, UriKind.Absolute, out var absoluteUri))
            {
                iconValue = absoluteUri.AbsolutePath;
            }

            var querySeparator = iconValue.IndexOf('?');
            if (querySeparator >= 0)
            {
                iconValue = iconValue.Substring(0, querySeparator);
            }

            var fragmentSeparator = iconValue.IndexOf('#');
            if (fragmentSeparator >= 0)
            {
                iconValue = iconValue.Substring(0, fragmentSeparator);
            }

            var lastSlash = iconValue.LastIndexOf('/');
            if (lastSlash >= 0 && lastSlash < iconValue.Length - 1)
            {
                iconValue = iconValue.Substring(lastSlash + 1);
            }

            iconValue = iconValue.Trim();
            return string.IsNullOrWhiteSpace(iconValue)
                ? null
                : iconValue.ToLowerInvariant();
        }

        private static List<SchemaAchievement> TryLoadCustomSchemaAchievements(string schemaPath, out string error)
        {
            error = null;
            var normalizedPath = schemaPath?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                error = "empty path";
                return null;
            }

            if (!File.Exists(normalizedPath))
            {
                error = "file not found";
                return null;
            }

            try
            {
                var json = File.ReadAllText(normalizedPath, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json))
                {
                    error = "empty file";
                    return null;
                }

                var root = JToken.Parse(json);
                var schemaDirectory = Path.GetDirectoryName(normalizedPath);
                var parsed = ExtractCustomSchemaAchievements(root, schemaDirectory);
                if (parsed.Count == 0)
                {
                    error = "no schema rows";
                    return null;
                }

                return parsed;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private string ResolveGameImagePath(Game game)
        {
            if (game == null)
            {
                return null;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(game.CoverImage))
                {
                    var coverPath = _api?.Database?.GetFullFilePath(game.CoverImage);
                    if (!string.IsNullOrWhiteSpace(coverPath))
                    {
                        return coverPath;
                    }
                }

                if (!string.IsNullOrWhiteSpace(game.Icon))
                {
                    var iconPath = _api?.Database?.GetFullFilePath(game.Icon);
                    if (!string.IsNullOrWhiteSpace(iconPath))
                    {
                        return iconPath;
                    }
                }
            }
            catch
            {
                // Ignore and fall back to provider defaults.
            }

            return null;
        }

        private static void ApplyCustomSchemaDefaultIcons(IReadOnlyList<SchemaAchievement> achievements, string defaultIconPath)
        {
            if (achievements == null || achievements.Count == 0 || string.IsNullOrWhiteSpace(defaultIconPath))
            {
                return;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(achievement.Icon))
                {
                    achievement.Icon = defaultIconPath;
                }

                if (string.IsNullOrWhiteSpace(achievement.IconGray))
                {
                    achievement.IconGray = defaultIconPath;
                }
            }
        }

        private static SchemaAchievement CloneSchemaAchievementWithoutIcons(SchemaAchievement source)
        {
            if (source == null)
            {
                return null;
            }

            return new SchemaAchievement
            {
                Name = source.Name,
                DisplayName = source.DisplayName,
                Description = source.Description,
                Hidden = source.Hidden,
                Icon = null,
                IconGray = null,
                GlobalPercent = source.GlobalPercent
            };
        }

        private static List<SchemaAchievement> ExtractCustomSchemaAchievements(JToken root, string schemaDirectory)
        {
            var result = new List<SchemaAchievement>();
            var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                return result;
            }

            void AddIfUnique(SchemaAchievement achievement)
            {
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.Name))
                {
                    return;
                }

                if (seenNames.Add(achievement.Name))
                {
                    result.Add(achievement);
                }
            }

            if (root is JArray array)
            {
                foreach (var token in array.OfType<JObject>())
                {
                    AddIfUnique(TryParseCustomSchemaAchievement(token, null, schemaDirectory));
                }

                return result;
            }

            if (!(root is JObject obj))
            {
                return result;
            }

            if (obj["achievements"] is JArray wrappedArray)
            {
                foreach (var token in wrappedArray.OfType<JObject>())
                {
                    AddIfUnique(TryParseCustomSchemaAchievement(token, null, schemaDirectory));
                }
            }

            if (TryParseCustomSchemaAchievement(obj, null, schemaDirectory) is SchemaAchievement directAchievement)
            {
                AddIfUnique(directAchievement);
            }

            foreach (var property in obj.Properties())
            {
                if (!(property.Value is JObject propertyObject))
                {
                    continue;
                }

                AddIfUnique(TryParseCustomSchemaAchievement(propertyObject, property.Name, schemaDirectory));
            }

            return result;
        }

        private static SchemaAchievement TryParseCustomSchemaAchievement(JObject source, string fallbackName, string schemaDirectory)
        {
            if (source == null)
            {
                return null;
            }

            var name = source["name"]?.Value<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = source["apiName"]?.Value<string>()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = source["api_name"]?.Value<string>()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = source["id"]?.Value<string>()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = source["key"]?.Value<string>()?.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = fallbackName?.Trim();
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var displayName = ResolveCustomSchemaText(source["displayName"] ?? source["display_name"] ?? source["title"]);
            var description = ResolveCustomSchemaText(source["description"] ?? source["desc"]);
            var icon = ResolveCustomSchemaText(source["icon"] ?? source["unlockedIcon"] ?? source["image"]);
            var iconGray = ResolveCustomSchemaText(source["icon_gray"] ?? source["icongray"] ?? source["lockedIcon"]);

            icon = NormalizeCustomSchemaIconPath(icon, schemaDirectory);
            iconGray = NormalizeCustomSchemaIconPath(iconGray, schemaDirectory);

            return new SchemaAchievement
            {
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName,
                Description = description ?? string.Empty,
                Icon = icon ?? string.Empty,
                IconGray = iconGray ?? string.Empty,
                Hidden = TryParseCustomSchemaHidden(source["hidden"]),
                GlobalPercent = TryParseCustomSchemaPercent(source["globalPercent"] ?? source["percent"])
            };
        }

        private static string ResolveCustomSchemaText(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                var value = token.Value<string>()?.Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }

            if (!(token is JObject localizedObject))
            {
                return token.ToString().Trim();
            }

            var preferredKeys = new[]
            {
                "english",
                "en-US",
                "en_US",
                "en",
                "default",
                "token"
            };

            foreach (var key in preferredKeys)
            {
                var value = localizedObject[key]?.Value<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            foreach (var property in localizedObject.Properties())
            {
                var value = property.Value?.Value<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static int TryParseCustomSchemaHidden(JToken token)
        {
            if (token == null)
            {
                return 0;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>() ? 1 : 0;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() > 0 ? 1 : 0;
            }

            if (token.Type == JTokenType.String && bool.TryParse(token.Value<string>(), out var boolValue))
            {
                return boolValue ? 1 : 0;
            }

            return 0;
        }

        private static double? TryParseCustomSchemaPercent(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Float || token.Type == JTokenType.Integer)
            {
                return token.Value<double>();
            }

            if (token.Type == JTokenType.String &&
                double.TryParse(token.Value<string>(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string NormalizeCustomSchemaIconPath(string rawPath, string schemaDirectory)
        {
            var iconPath = rawPath?.Trim();
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return null;
            }

            if (Uri.TryCreate(iconPath, UriKind.Absolute, out var absoluteUri) &&
                (absoluteUri.Scheme == Uri.UriSchemeHttp || absoluteUri.Scheme == Uri.UriSchemeHttps || absoluteUri.Scheme == Uri.UriSchemeFile))
            {
                return iconPath;
            }

            if (Path.IsPathRooted(iconPath))
            {
                return iconPath;
            }

            if (string.IsNullOrWhiteSpace(schemaDirectory))
            {
                return iconPath;
            }

            try
            {
                var normalizedRelative = iconPath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(Path.Combine(schemaDirectory, normalizedRelative));
                if (File.Exists(resolved))
                {
                    return resolved;
                }

                // Some local achievement JSON files live in nested folders (for example "stats")
                // while icon folders are relative to a parent folder. Probe ancestors before giving up.
                var currentDirectory = new DirectoryInfo(schemaDirectory);
                for (var i = 0; i < 12 && currentDirectory?.Parent != null; i++)
                {
                    currentDirectory = currentDirectory.Parent;
                    var ancestorCandidate = Path.GetFullPath(Path.Combine(currentDirectory.FullName, normalizedRelative));
                    if (File.Exists(ancestorCandidate))
                    {
                        return ancestorCandidate;
                    }
                }

                return resolved;
            }
            catch
            {
                return iconPath;
            }
        }

        private static bool ShouldExpandSchemaAchievementsForLocalEntries(string schemaSource)
        {
            return !string.Equals(schemaSource, "steam-community", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(schemaSource, "completionist", StringComparison.OrdinalIgnoreCase);
        }

        private static void MergeSchemaMetadata(
            IReadOnlyList<SchemaAchievement> targetAchievements,
            IReadOnlyList<SchemaAchievement> sourceAchievements,
            bool preferSourceText = false,
            bool includeIconMetadata = true)
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
            var sourceByIcon = BuildSchemaLookupByIcon(sourceAchievements, useLockedIcon: false);
            var sourceByLockedIcon = BuildSchemaLookupByIcon(sourceAchievements, useLockedIcon: true);

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
                    var iconHash = ExtractAchievementIconHash(target.Icon);
                    if (!string.IsNullOrWhiteSpace(iconHash) &&
                        sourceByIcon.TryGetValue(iconHash, out var byIcon) &&
                        byIcon != null)
                    {
                        source = byIcon;
                    }
                }

                if (source == null)
                {
                    var iconGrayHash = ExtractAchievementIconHash(target.IconGray);
                    if (!string.IsNullOrWhiteSpace(iconGrayHash) &&
                        sourceByLockedIcon.TryGetValue(iconGrayHash, out var byLockedIcon) &&
                        byLockedIcon != null)
                    {
                        source = byLockedIcon;
                    }
                }

                if (source == null)
                {
                    continue;
                }

                if (ShouldPreferSchemaDisplayName(target.DisplayName, source.DisplayName, target.Name, preferSourceText))
                {
                    target.DisplayName = source.DisplayName;
                }

                if (ShouldPreferSchemaDescription(target.Description, source.Description, preferSourceText))
                {
                    target.Description = source.Description;
                }

                if (includeIconMetadata &&
                    IsLowQualityIconPath(target.Icon, isLockedIcon: false) &&
                    !IsLowQualityIconPath(source.Icon, isLockedIcon: false))
                {
                    target.Icon = source.Icon;
                }

                if (includeIconMetadata &&
                    IsLowQualityIconPath(target.IconGray, isLockedIcon: true) &&
                    !IsLowQualityIconPath(source.IconGray, isLockedIcon: true))
                {
                    target.IconGray = source.IconGray;
                }

                if (target.Hidden != 1 && source.Hidden == 1)
                {
                    target.Hidden = 1;
                }

                if (!target.GlobalPercent.HasValue && source.GlobalPercent.HasValue)
                {
                    target.GlobalPercent = source.GlobalPercent;
                }

                if (!target.Points.HasValue && source.Points.HasValue)
                {
                    target.Points = source.Points;
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
            bool hadPreviousCache = previousAchievements != null && previousAchievements.Count > 0;
            if (hadPreviousCache)
            {
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

                    // LumaPlay registry values do not expose per-achievement unlock timestamps.
                    // Preserve known timestamps from prior cache, and stamp first-seen time only
                    // when we detect a locked -> unlocked transition with no incoming timestamp.
                    if (achievement.Unlocked)
                    {
                        if (!achievement.UnlockTimeUtc.HasValue)
                        {
                            if (previousAchievement.Unlocked && previousAchievement.UnlockTimeUtc.HasValue)
                            {
                                achievement.UnlockTimeUtc = previousAchievement.UnlockTimeUtc;
                            }
                            else if (!previousAchievement.Unlocked)
                            {
                                achievement.UnlockTimeUtc = DateTime.UtcNow;
                            }
                            else if (previousAchievement.Unlocked)
                            {
                                achievement.UnlockTimeUtc = DateTime.UtcNow;
                            }
                        }
                    }
                    else
                    {
                        achievement.UnlockTimeUtc = null;
                    }
                }
            }
            else
            {
                // No previous cache: set unlock time for all unlocked achievements with no timestamp
                foreach (var achievement in data.Achievements)
                {
                    if (achievement != null && achievement.Unlocked && !achievement.UnlockTimeUtc.HasValue)
                    {
                        achievement.UnlockTimeUtc = DateTime.UtcNow;
                    }
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
                   string.Equals(description, "Local achievement from Local", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(description, "LumaPlay registry achievement", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPlaceholderLocalDescription(string description)
        {
            return string.Equals(description?.Trim(), "Local achievement from Local", StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, LocalEntry> CorrelateLocalEntriesWithSchema(
            IReadOnlyDictionary<string, LocalEntry> localEntries,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            var correlated = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            if (localEntries == null || localEntries.Count == 0 || schemaAchievements == null || schemaAchievements.Count == 0)
            {
                return correlated;
            }

            var workingEntries = RemapGenericAchievementEntries(
                localEntries.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                schemaAchievements);

            var schemaNameByKey = BuildSchemaNameByCorrelationKey(schemaAchievements);
            var apiNameMap = schemaAchievements
                .Where(a => !string.IsNullOrWhiteSpace(a?.Name))
                .ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
            var schemaLookupByText = BuildSchemaLookupByText(schemaAchievements);
            var schemaLookupByTitle = BuildSchemaLookupByTitle(schemaAchievements);

            foreach (var pair in workingEntries)
            {
                string correlatedSchemaName = null;

                foreach (var key in EnumerateCorrelationKeys(pair.Key))
                {
                    if (!schemaNameByKey.TryGetValue(key, out var schemaName))
                    {
                        continue;
                    }

                    correlatedSchemaName = schemaName;
                    break;
                }

                if (string.IsNullOrWhiteSpace(correlatedSchemaName))
                {
                    if (IsGenericAchievementId(pair.Key))
                    {
                        continue;
                    }

                    var resolved = ResolveSchemaAchievement(pair.Key, pair.Value, apiNameMap, schemaLookupByText, schemaLookupByTitle);
                    correlatedSchemaName = resolved?.Name;
                }

                if (string.IsNullOrWhiteSpace(correlatedSchemaName))
                {
                    continue;
                }

                if (correlated.TryGetValue(correlatedSchemaName, out var existing))
                {
                    correlated[correlatedSchemaName] = MergeLocalEntries(existing, pair.Value);
                }
                else
                {
                    correlated[correlatedSchemaName] = pair.Value;
                }
            }

            return correlated;
        }

        private static Dictionary<string, string> BuildSchemaNameByCorrelationKey(IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (schemaAchievements == null)
            {
                return map;
            }

            foreach (var schemaAchievement in schemaAchievements)
            {
                var schemaName = schemaAchievement?.Name?.Trim();
                if (string.IsNullOrWhiteSpace(schemaName))
                {
                    continue;
                }

                foreach (var key in EnumerateCorrelationKeys(schemaName))
                {
                    if (!map.ContainsKey(key))
                    {
                        map[key] = schemaName;
                    }
                }
            }

            return map;
        }

        private static IEnumerable<string> EnumerateCorrelationKeys(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                yield break;
            }

            var trimmed = value.Trim();
            var normalized = NormalizeCorrelationKey(trimmed);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                yield return normalized;
            }

            var withoutAchPrefix = RemoveAchPrefix(normalized);
            if (!string.IsNullOrWhiteSpace(withoutAchPrefix) && !string.Equals(withoutAchPrefix, normalized, StringComparison.OrdinalIgnoreCase))
            {
                yield return withoutAchPrefix;
            }

            if (!string.IsNullOrWhiteSpace(withoutAchPrefix) && int.TryParse(withoutAchPrefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numberValue))
            {
                yield return numberValue.ToString(CultureInfo.InvariantCulture);
            }
        }

        private static string NormalizeCorrelationKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray();
            return new string(chars);
        }

        private static string RemoveAchPrefix(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return string.Empty;
            }

            if (normalized.StartsWith("ach", StringComparison.OrdinalIgnoreCase))
            {
                return normalized.Substring(3);
            }

            return normalized;
        }

        private static void FilterPlaceholderLocalAchievements(GameAchievementData data)
        {
            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            data.Achievements = data.Achievements
                .Where(achievement => achievement != null && !IsPlaceholderLocalDescription(achievement.Description))
                .ToList();
        }

        private static Dictionary<string, LocalEntry> RemapGenericAchievementEntries(
            Dictionary<string, LocalEntry> source,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            if (source == null || source.Count == 0 || schemaAchievements == null || schemaAchievements.Count == 0)
            {
                return source ?? new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            }

            var schemaNames = new HashSet<string>(
                schemaAchievements
                    .Select(achievement => achievement?.Name?.Trim())
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            var remapped = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in source)
            {
                var targetKey = kv.Key;
                if (TryParseGenericAchievementIndex(kv.Key, out var oneBasedIndex) && !schemaNames.Contains(kv.Key))
                {
                    var zeroBasedIndex = oneBasedIndex - 1;
                    if (zeroBasedIndex >= 0 && zeroBasedIndex < schemaAchievements.Count)
                    {
                        var schemaName = schemaAchievements[zeroBasedIndex]?.Name?.Trim();
                        if (!string.IsNullOrWhiteSpace(schemaName))
                        {
                            targetKey = schemaName;
                        }
                    }
                }

                if (remapped.TryGetValue(targetKey, out var existing))
                {
                    remapped[targetKey] = MergeLocalEntries(existing, kv.Value);
                }
                else
                {
                    remapped[targetKey] = kv.Value;
                }
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

        private static bool IsUnresolvedLocalIconPath(string iconPath)
        {
            var value = iconPath?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                {
                    return false;
                }

                if (uri.Scheme == Uri.UriSchemeFile)
                {
                    return !File.Exists(uri.LocalPath);
                }

                return true;
            }

            if (Path.IsPathRooted(value))
            {
                return !File.Exists(value);
            }

            return true;
        }

        /// <summary>
        /// Returns true when <paramref name="iconPath"/> is a local file path that represents a
        /// user-defined icon (e.g. from a local JSON or custom schema file).  CDN HTTP(S) URLs
        /// and pack:// placeholder URIs return false.
        /// </summary>
        private static bool IsLocalSourceIconPath(string iconPath)
        {
            if (string.IsNullOrWhiteSpace(iconPath))
            {
                return false;
            }

            if (iconPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                iconPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var normalized = AchievementIconResolver.NormalizeIconPath(iconPath);
            return !string.Equals(normalized, AchievementIconResolver.GetDefaultUnlockedIcon(), StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(normalized, AchievementIconResolver.GetDefaultIcon(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsWindowsPlatform()
        {
            return Environment.OSVersion.Platform == PlatformID.Win32NT;
        }

#pragma warning disable CA1416
        private Dictionary<string, LocalEntry> TryGetLumaPlayRegistryEntries(string appId)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return null;
            }

            foreach (var candidateAppId in BuildLumaPlayAppIdCandidates(appId))
            {
                if (!TryResolveLumaPlayAchievementsRegistryPath(candidateAppId, out var registryPath, out var userSegment))
                {
                    continue;
                }

                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(registryPath, false))
                    {
                        if (key == null)
                        {
                            continue;
                        }

                        var entries = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                        foreach (var valueName in key.GetValueNames())
                        {
                            var normalizedName = valueName?.Trim();
                            if (string.IsNullOrWhiteSpace(normalizedName))
                            {
                                continue;
                            }

                            var rawValue = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                            RegistryValueKind valueKind;
                            try
                            {
                                valueKind = key.GetValueKind(valueName);
                            }
                            catch
                            {
                                valueKind = RegistryValueKind.Unknown;
                            }

                            var unlocked = false;
                            if (!TryReadLumaPlayRegistryUnlockedValue(valueKind, rawValue, out unlocked))
                            {
                                unlocked = false;
                            }

                            entries[normalizedName] = new LocalEntry
                            {
                                earned = unlocked,
                                earned_time = 0,
                                displayName = normalizedName,
                                description = "LumaPlay registry achievement"
                            };
                        }

                        if (entries.Count > 0)
                        {
                            Log($"LUMAPLAY REGISTRY: appId={candidateAppId} user={userSegment} values={entries.Count} path=HKCU\\{registryPath}");
                            // Apply canonical name resolution using the local Ubisoft Connect
                            // game config (achievements.json). This is the same operation the
                            // reference JS performs via getNameIndexFromConfigPath + resolveCanonicalName.
                            var ubisoftNameIndex = TryBuildUbisoftConnectNameIndex(candidateAppId);
                            var ubisoftMetadataByCanonical = TryBuildUbisoftConnectMetadataByCanonical(candidateAppId);
                            if ((ubisoftNameIndex != null && ubisoftNameIndex.Count > 0) ||
                                (ubisoftMetadataByCanonical != null && ubisoftMetadataByCanonical.Count > 0))
                            {
                                var resolved = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);
                                foreach (var kv in entries)
                                {
                                    var canonical = ResolveUbisoftCanonicalName(kv.Key, ubisoftNameIndex);
                                    var enriched = ApplyUbisoftMetadata(kv.Value, kv.Key, canonical, ubisoftMetadataByCanonical);

                                    if (resolved.TryGetValue(canonical, out var existing))
                                    {
                                        resolved[canonical] = MergeLocalEntries(existing, enriched);
                                    }
                                    else
                                    {
                                        resolved[canonical] = enriched;
                                    }
                                }

                                entries = resolved;
                                Log($"LUMAPLAY UBISOFT CONFIG: appId={candidateAppId} resolved={entries.Count} names+metadata from local Ubisoft config");
                            }
                            return entries;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"LUMAPLAY REGISTRY ERROR: appId={candidateAppId} path=HKCU\\{registryPath} msg={ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Remaps LumaPlay registry entry names to match Steam schema names when there is an
        /// "ach_" prefix mismatch. LumaPlay may store achievements as "ACH_FOO" while the Steam
        /// schema (and Ubisoft's own config) uses "FOO" as the canonical name, or vice versa.
        /// Any entry whose raw name is not found in the schema is left unchanged.
        /// </summary>
        private static Dictionary<string, LocalEntry> NormalizeLumaPlayNamesAgainstSchema(
            Dictionary<string, LocalEntry> entries,
            IReadOnlyList<SchemaAchievement> schemaAchievements)
        {
            if (entries == null || entries.Count == 0 || schemaAchievements == null || schemaAchievements.Count == 0)
            {
                return entries;
            }

            var schemaNames = new HashSet<string>(
                schemaAchievements
                    .Where(a => !string.IsNullOrWhiteSpace(a?.Name))
                    .Select(a => a.Name),
                StringComparer.OrdinalIgnoreCase);

            var remapped = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in entries)
            {
                var rawName = kv.Key;

                // 1. Already matches a schema name, keep as-is
                if (schemaNames.Contains(rawName))
                {
                    remapped[rawName] = kv.Value;
                    continue;
                }

                string resolvedName = null;

                // 2. Strip "ach_" prefix and try again (LumaPlay stores ACH_FOO, schema has FOO)
                if (rawName.StartsWith("ach_", StringComparison.OrdinalIgnoreCase) && rawName.Length > 4)
                {
                    var stripped = rawName.Substring(4);
                    if (schemaNames.Contains(stripped))
                    {
                        resolvedName = stripped;
                    }
                }

                // 3. Add "ACH_" prefix and try (LumaPlay stores FOO, schema has ACH_FOO)
                if (resolvedName == null && !rawName.StartsWith("ach_", StringComparison.OrdinalIgnoreCase))
                {
                    var prefixed = "ACH_" + rawName;
                    if (schemaNames.Contains(prefixed))
                    {
                        resolvedName = prefixed;
                    }
                }

                remapped[resolvedName ?? rawName] = kv.Value;
            }

            return remapped;
        }

        /// <summary>
        /// Tries to load the Ubisoft Connect / Uplay local game config (achievements.json) for the
        /// given Uplay app ID. The config lives in the Ubisoft Game Launcher data directory and maps
        /// internal achievement names (canonical) to their display data.
        /// Returns a case-insensitive dictionary mapping every known name variant to the canonical name.
        /// </summary>
        private static Dictionary<string, string> TryBuildUbisoftConnectNameIndex(string uplayAppId)
        {
            var achievementsArray = TryLoadUbisoftConnectAchievementArray(uplayAppId);
            if (achievementsArray == null || achievementsArray.Count == 0)
            {
                return null;
            }

            return BuildUbisoftAchievementNameIndex(achievementsArray);
        }

        private static Dictionary<string, UbisoftAchievementMetadata> TryBuildUbisoftConnectMetadataByCanonical(string uplayAppId)
        {
            var achievementsArray = TryLoadUbisoftConnectAchievementArray(uplayAppId);
            if (achievementsArray == null || achievementsArray.Count == 0)
            {
                return null;
            }

            return BuildUbisoftAchievementMetadataByCanonical(achievementsArray);
        }

        private static JArray TryLoadUbisoftConnectAchievementArray(string uplayAppId)
        {
            if (string.IsNullOrWhiteSpace(uplayAppId))
            {
                return null;
            }

            // Typical Ubisoft Connect / Uplay installation locations
            var baseCandidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Game Launcher"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Ubisoft", "Ubisoft Connect"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ubisoft", "Ubisoft Game Launcher"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ubisoft", "Ubisoft Connect"),
            };

            foreach (var base_ in baseCandidates)
            {
                if (!Directory.Exists(base_))
                {
                    continue;
                }

                var configPaths = new[]
                {
                    Path.Combine(base_, "data", uplayAppId, "achievements.json"),
                    Path.Combine(base_, "data", uplayAppId, "GameConfig.json"),
                };

                foreach (var configPath in configPaths)
                {
                    if (!File.Exists(configPath))
                    {
                        continue;
                    }

                    try
                    {
                        var json = File.ReadAllText(configPath, System.Text.Encoding.UTF8);
                        var token = JToken.Parse(json);
                        var extracted = ExtractUbisoftAchievementArray(token);
                        if (extracted != null && extracted.Count > 0)
                        {
                            return extracted;
                        }
                    }
                    catch
                    {
                        // Config file format unexpected or unreadable, skip
                    }
                }
            }

            return null;
        }

        private static JArray ExtractUbisoftAchievementArray(JToken root)
        {
            if (root == null)
            {
                return null;
            }

            if (root is JArray directArray && LooksLikeUbisoftAchievementArray(directArray))
            {
                return directArray;
            }

            if (!(root is JContainer container))
            {
                return null;
            }

            foreach (var child in container.Children())
            {
                var array = ExtractUbisoftAchievementArray(child);
                if (array != null)
                {
                    return array;
                }
            }

            return null;
        }

        private static bool LooksLikeUbisoftAchievementArray(JArray array)
        {
            if (array == null || array.Count == 0)
            {
                return false;
            }

            return array
                .OfType<JObject>()
                .Any(entry => !string.IsNullOrWhiteSpace(entry["name"]?.Value<string>()));
        }

        /// <summary>
        /// Builds a name lookup dictionary from a parsed Ubisoft achievements.json array.
        /// Maps each name variant (with/without "ach_" prefix, normalized) to the canonical name.
        /// Mirrors what the reference JS does in buildNameOnlyIndex + buildDisplayToNameIndex.
        /// </summary>
        private static Dictionary<string, string> BuildUbisoftAchievementNameIndex(JArray configArray)
        {
            var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in configArray)
            {
                if (!(item is JObject entry))
                {
                    continue;
                }

                var canonical = entry["name"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    continue;
                }

                // Map the canonical name itself
                index[canonical] = canonical;

                // If canonical does NOT start with "ach_", also register the "ach_"-prefixed variant
                if (!canonical.StartsWith("ach_", StringComparison.OrdinalIgnoreCase))
                {
                    var alias = "ACH_" + canonical;
                    if (!index.ContainsKey(alias))
                    {
                        index[alias] = canonical;
                    }
                }
                else
                {
                    // Canonical starts with "ach_", also register the stripped variant
                    var stripped = canonical.Substring(4);
                    if (!string.IsNullOrWhiteSpace(stripped) && !index.ContainsKey(stripped))
                    {
                        index[stripped] = canonical;
                    }
                }

                // Also map display names (localized) so achievement.ini section title lookups work
                var displayName = entry["displayName"];
                if (displayName is JObject localizedMap)
                {
                    foreach (var prop in localizedMap.Properties())
                    {
                        var dn = prop.Value?.Value<string>()?.Trim();
                        if (!string.IsNullOrWhiteSpace(dn) && !index.ContainsKey(dn))
                        {
                            index[dn] = canonical;
                        }
                    }
                }
                else if (displayName != null)
                {
                    var dn = displayName.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(dn) && !index.ContainsKey(dn))
                    {
                        index[dn] = canonical;
                    }
                }
            }

            return index;
        }

        private static Dictionary<string, UbisoftAchievementMetadata> BuildUbisoftAchievementMetadataByCanonical(JArray configArray)
        {
            var result = new Dictionary<string, UbisoftAchievementMetadata>(StringComparer.OrdinalIgnoreCase);
            if (configArray == null || configArray.Count == 0)
            {
                return result;
            }

            foreach (var item in configArray)
            {
                if (!(item is JObject entry))
                {
                    continue;
                }

                var canonical = entry["name"]?.Value<string>()?.Trim();
                if (string.IsNullOrWhiteSpace(canonical))
                {
                    continue;
                }

                var localizedDisplayName = ResolvePreferredUbisoftText(entry["displayName"]);
                var localizedDescription = ResolvePreferredUbisoftText(entry["description"]) ??
                                           ResolvePreferredUbisoftText(entry["strDescription"]);

                var metadata = new UbisoftAchievementMetadata
                {
                    CanonicalName = canonical,
                    DisplayName = localizedDisplayName,
                    Description = localizedDescription
                };

                if (!result.ContainsKey(canonical))
                {
                    result[canonical] = metadata;
                }
                else
                {
                    var current = result[canonical];
                    if (ShouldPreferSchemaDisplayName(current.DisplayName, metadata.DisplayName, canonical, preferLocalizedSchemaText: false))
                    {
                        current.DisplayName = metadata.DisplayName;
                    }

                    if (ShouldPreferSchemaDescription(current.Description, metadata.Description, preferLocalizedSchemaText: false))
                    {
                        current.Description = metadata.Description;
                    }

                    result[canonical] = current;
                }
            }

            return result;
        }

        private static string ResolvePreferredUbisoftText(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                var raw = token.Value<string>()?.Trim();
                return string.IsNullOrWhiteSpace(raw) ? null : raw;
            }

            if (token is JObject localizedObject)
            {
                var cultureName = CultureInfo.CurrentUICulture?.Name;
                var cultureTwoLetter = CultureInfo.CurrentUICulture?.TwoLetterISOLanguageName;
                var preferredKeys = new[]
                {
                    cultureName,
                    cultureName?.Replace('-', '_'),
                    cultureTwoLetter,
                    "en-US",
                    "en_US",
                    "en",
                    "default"
                };

                foreach (var key in preferredKeys.Where(k => !string.IsNullOrWhiteSpace(k)))
                {
                    var matchingProperty = localizedObject.Properties()
                        .FirstOrDefault(prop => string.Equals(prop.Name, key, StringComparison.OrdinalIgnoreCase));
                    var value = matchingProperty?.Value?.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                foreach (var property in localizedObject.Properties())
                {
                    var value = property.Value?.Value<string>()?.Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }

                return null;
            }

            if (token is JArray valuesArray)
            {
                foreach (var valueToken in valuesArray)
                {
                    var value = ResolvePreferredUbisoftText(valueToken);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        return value;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Resolves a raw registry value name to its canonical form using the Ubisoft name index.
        /// Falls back to the raw name if no match is found (same as JS resolveCanonicalName fallback).
        /// </summary>
        private static string ResolveUbisoftCanonicalName(string rawName, Dictionary<string, string> nameIndex)
        {
            if (string.IsNullOrWhiteSpace(rawName) || nameIndex == null)
            {
                return rawName ?? string.Empty;
            }

            if (nameIndex.TryGetValue(rawName, out var canonical))
            {
                return canonical;
            }

            // Try stripped "ach_" prefix
            if (rawName.StartsWith("ach_", StringComparison.OrdinalIgnoreCase) && rawName.Length > 4)
            {
                var stripped = rawName.Substring(4);
                if (nameIndex.TryGetValue(stripped, out canonical))
                {
                    return canonical;
                }
            }

            // Fall back to raw name (same as JS "return candidates[candidates.length - 1] || \"\"")
            return rawName;
        }

        private static LocalEntry ApplyUbisoftMetadata(
            LocalEntry entry,
            string rawName,
            string canonicalName,
            IReadOnlyDictionary<string, UbisoftAchievementMetadata> metadataByCanonical)
        {
            if (metadataByCanonical == null || metadataByCanonical.Count == 0 || string.IsNullOrWhiteSpace(canonicalName))
            {
                return entry;
            }

            if (!metadataByCanonical.TryGetValue(canonicalName, out var metadata) || metadata == null)
            {
                return entry;
            }

            if (ShouldPreferSchemaDisplayName(entry.displayName, metadata.DisplayName, rawName, preferLocalizedSchemaText: false))
            {
                entry.displayName = metadata.DisplayName;
            }

            if (ShouldPreferSchemaDescription(entry.description, metadata.Description, preferLocalizedSchemaText: false))
            {
                entry.description = metadata.Description;
            }

            return entry;
        }

        private static LocalEntry MergeLocalEntries(LocalEntry existing, LocalEntry incoming)
        {
            existing.earned = existing.earned || incoming.earned;
            existing.earned_time = Math.Max(existing.earned_time, incoming.earned_time);

            if (string.IsNullOrWhiteSpace(existing.displayName) ||
                ShouldPreferSchemaDisplayName(existing.displayName, incoming.displayName, existing.displayName, preferLocalizedSchemaText: false))
            {
                existing.displayName = incoming.displayName;
            }

            if (ShouldPreferSchemaDescription(existing.description, incoming.description, preferLocalizedSchemaText: false))
            {
                existing.description = incoming.description;
            }

            if (string.IsNullOrWhiteSpace(existing.icon))
            {
                existing.icon = incoming.icon;
            }

            if (string.IsNullOrWhiteSpace(existing.iconGray))
            {
                existing.iconGray = incoming.iconGray;
            }

            existing.hidden = existing.hidden || incoming.hidden;

            if (!existing.percent.HasValue && incoming.percent.HasValue)
            {
                existing.percent = incoming.percent;
            }

            if (!existing.progress.HasValue && incoming.progress.HasValue)
            {
                existing.progress = incoming.progress;
            }

            if (!existing.max_progress.HasValue && incoming.max_progress.HasValue)
            {
                existing.max_progress = incoming.max_progress;
            }

            return existing;
        }

        private static int? NormalizeAchievementProgressMaximum(int? maximum)
        {
            return maximum.HasValue && maximum.Value > 0
                ? maximum
                : null;
        }

        private static int? NormalizeAchievementProgress(int? progress, int? maximum)
        {
            var normalizedMaximum = NormalizeAchievementProgressMaximum(maximum);
            if (!progress.HasValue || !normalizedMaximum.HasValue)
            {
                return null;
            }

            return Math.Min(Math.Max(progress.Value, 0), normalizedMaximum.Value);
        }

        private static IEnumerable<string> BuildLumaPlayAppIdCandidates(string appId)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalized = appId?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
            {
                yield return normalized;
            }

            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericAppId) && numericAppId > 0)
            {
                var hexLower = numericAppId.ToString("x", CultureInfo.InvariantCulture);
                if (seen.Add(hexLower))
                {
                    yield return hexLower;
                }

                var hexUpper = numericAppId.ToString("X", CultureInfo.InvariantCulture);
                if (seen.Add(hexUpper))
                {
                    yield return hexUpper;
                }
            }
        }

        private static bool TryResolveLumaPlayAchievementsRegistryPath(string appId, out string registryPath, out string userSegment)
        {
            registryPath = null;
            userSegment = null;

            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            using (var root = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\LumaPlay", false))
            {
                if (root == null)
                {
                    return false;
                }

                foreach (var userName in root.GetSubKeyNames().Where(name => !string.IsNullOrWhiteSpace(name)).OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    var candidatePath = $@"SOFTWARE\LumaPlay\{userName}\{appId}\Achievements";
                    using (var achievementKey = Registry.CurrentUser.OpenSubKey(candidatePath, false))
                    {
                        if (achievementKey == null)
                        {
                            continue;
                        }

                        registryPath = candidatePath;
                        userSegment = userName;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryReadLumaPlayRegistryUnlockedValue(RegistryValueKind valueKind, object value, out bool unlocked)
        {
            unlocked = false;
            if (value == null)
            {
                return false;
            }

            switch (value)
            {
                case bool boolValue:
                    unlocked = boolValue;
                    return true;
                case byte byteValue:
                    unlocked = byteValue > 0;
                    return true;
                case short shortValue:
                    unlocked = shortValue > 0;
                    return true;
                case int intValue:
                    unlocked = intValue > 0;
                    return true;
                case long longValue:
                    unlocked = longValue > 0;
                    return true;
                case ushort ushortValue:
                    unlocked = ushortValue > 0;
                    return true;
                case uint uintValue:
                    unlocked = uintValue > 0;
                    return true;
                case ulong ulongValue:
                    unlocked = ulongValue > 0;
                    return true;
                case byte[] bytes:
                    unlocked = bytes.Any(b => b != 0);
                    return true;
            }

            var text = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            if (TryParseUnlockedValue(text, out unlocked))
            {
                return true;
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(text.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hexNumber))
            {
                unlocked = hexNumber > 0;
                return true;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimalNumber))
            {
                unlocked = decimalNumber > 0;
                return true;
            }

            if (valueKind == RegistryValueKind.MultiString && value is string[] values)
            {
                unlocked = values.Any(part => TryParseUnlockedValue(part, out var parsed) && parsed);
                return true;
            }

            return false;
        }
#pragma warning restore CA1416

        private async Task<Dictionary<string, LocalEntry>> LoadLocalEntriesAsync(string jsonPath, string iniPath)
        {
            return await LoadLocalEntriesAsync(
                jsonPath,
                string.IsNullOrWhiteSpace(iniPath) ? Enumerable.Empty<string>() : new[] { iniPath }).ConfigureAwait(false);
        }

        private async Task<Dictionary<string, LocalEntry>> LoadLocalEntriesAsync(string jsonPath, IEnumerable<string> iniPaths)
        {
            var merged = new Dictionary<string, LocalEntry>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(jsonPath) && File.Exists(jsonPath))
            {
                var json = await Task.Run(() => File.ReadAllText(jsonPath)).ConfigureAwait(false);
                var jsonEntries = JsonConvert.DeserializeObject<Dictionary<string, LocalEntry>>(json);
                if (jsonEntries != null)
                {
                    var jsonDirectory = Path.GetDirectoryName(jsonPath);
                    foreach (var entry in jsonEntries.Where(e => !string.IsNullOrWhiteSpace(e.Key)))
                    {
                        merged[entry.Key] = NormalizeLocalEntryIconPaths(entry.Value, jsonDirectory);
                    }
                }
            }

            foreach (var iniPath in (iniPaths ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(iniPath))
                {
                    continue;
                }

                var ini = await Task.Run(() => File.ReadAllLines(iniPath)).ConfigureAwait(false);
                var iniEntries = ParseIniEntries(ini);
                var iniDirectory = Path.GetDirectoryName(iniPath);
                foreach (var entry in iniEntries)
                {
                    var incoming = NormalizeLocalEntryIconPaths(entry.Value, iniDirectory);
                    if (merged.TryGetValue(entry.Key, out var existing))
                    {
                        existing.earned = incoming.earned;
                        if (incoming.earned_time > 0)
                        {
                            existing.earned_time = incoming.earned_time;
                        }

                        if (incoming.hidden)
                        {
                            existing.hidden = true;
                        }

                        if (incoming.percent.HasValue)
                        {
                            existing.percent = incoming.percent;
                        }

                        if (!string.IsNullOrWhiteSpace(incoming.displayName))
                        {
                            existing.displayName = incoming.displayName;
                        }

                        if (!string.IsNullOrWhiteSpace(incoming.description))
                        {
                            existing.description = incoming.description;
                        }

                        if (!string.IsNullOrWhiteSpace(incoming.icon))
                        {
                            existing.icon = incoming.icon;
                        }

                        if (!string.IsNullOrWhiteSpace(incoming.iconGray))
                        {
                            existing.iconGray = incoming.iconGray;
                        }

                        merged[entry.Key] = existing;
                    }
                    else
                    {
                        merged[entry.Key] = incoming;
                    }
                }
            }

            return merged;
        }

        private static LocalEntry NormalizeLocalEntryIconPaths(LocalEntry entry, string baseDirectory)
        {
            entry.icon = NormalizeCustomSchemaIconPath(entry.icon, baseDirectory);
            entry.iconGray = NormalizeCustomSchemaIconPath(entry.iconGray, baseDirectory);
            return entry;
        }

        private static IEnumerable<string> SerializeLocalEntriesToIni(IReadOnlyDictionary<string, LocalEntry> entries)
        {
            var lines = new List<string>();
            foreach (var pair in entries.OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var key = pair.Key?.Trim();
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                lines.Add($"[{key}]");
                lines.Add($"earned={(pair.Value.earned ? "1" : "0")}");
                lines.Add($"earned_time={pair.Value.earned_time}");

                if (!string.IsNullOrWhiteSpace(pair.Value.displayName))
                {
                    lines.Add($"displayName={pair.Value.displayName}");
                }

                if (!string.IsNullOrWhiteSpace(pair.Value.description))
                {
                    lines.Add($"description={pair.Value.description}");
                }

                if (!string.IsNullOrWhiteSpace(pair.Value.icon))
                {
                    lines.Add($"icon={pair.Value.icon}");
                }

                if (!string.IsNullOrWhiteSpace(pair.Value.iconGray))
                {
                    lines.Add($"iconGray={pair.Value.iconGray}");
                }

                lines.Add($"hidden={(pair.Value.hidden ? "1" : "0")}");
                if (pair.Value.percent.HasValue)
                {
                    lines.Add($"percent={pair.Value.percent.Value.ToString(CultureInfo.InvariantCulture)}");
                }

                lines.Add(string.Empty);
            }

            return lines;
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

                var key = TrimIniToken(line.Substring(0, separatorIndex));
                var value = StripIniInlineComment(line.Substring(separatorIndex + 1)).Trim().Trim('"');
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
            var achievementSection = NormalizeAchievementSectionName(section);

            if (isGenericSection && TryParseInlineIniObject(value, out var inlineFields))
            {
                foreach (var inlineField in inlineFields)
                {
                    ApplyIniField(entries, key, inlineField.Key, inlineField.Value);
                }

                return;
            }

            if (!isGenericSection && TryParseKnownIniField(key, out fieldName))
            {
                ApplyIniField(entries, achievementSection, fieldName, value);
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

        private static string NormalizeAchievementSectionName(string section)
        {
            var normalized = TrimIniToken(section);
            const string tenokeAchievementPrefix = "ACHIEVEMENTS.";
            if (normalized.StartsWith(tenokeAchievementPrefix, StringComparison.OrdinalIgnoreCase) &&
                normalized.Length > tenokeAchievementPrefix.Length)
            {
                return normalized.Substring(tenokeAchievementPrefix.Length).Trim();
            }

            return normalized;
        }

        private static string TrimIniToken(string value)
        {
            return (value ?? string.Empty).Trim().Trim('"');
        }

        private static string StripIniInlineComment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var inQuotes = false;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (!inQuotes && (c == '#' || c == ';'))
                {
                    return value.Substring(0, i);
                }
            }

            return value;
        }

        private static bool TryParseInlineIniObject(string value, out Dictionary<string, string> fields)
        {
            fields = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            if (!normalized.StartsWith("{", StringComparison.Ordinal) ||
                !normalized.EndsWith("}", StringComparison.Ordinal))
            {
                return false;
            }

            normalized = normalized.Substring(1, normalized.Length - 2);
            var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in normalized.Split(','))
            {
                var separatorIndex = part.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = TrimIniToken(part.Substring(0, separatorIndex));
                var fieldValue = TrimIniToken(part.Substring(separatorIndex + 1));
                if (TryParseKnownIniField(key, out var fieldName))
                {
                    parsed[fieldName] = fieldValue;
                }
            }

            fields = parsed;
            return parsed.Count > 0;
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
            if (string.IsNullOrWhiteSpace(iniPath))
            {
                TryFindAchievementFile(appId, "user_stats.ini", out iniPath);
            }

            if (string.IsNullOrWhiteSpace(iniPath))
            {
                TryFindAchievementFile(appId, "tenoke.ini", out iniPath);
            }

            return !string.IsNullOrWhiteSpace(jsonPath) || !string.IsNullOrWhiteSpace(iniPath);
        }

        private bool TryFindAchievementFile(string appId, string fileName, out string filePath)
        {
            filePath = null;
            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            var excludedLocalPaths = GetExcludedLocalPathEntries().ToList();

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (IsPathExcludedFromLocalScan(root, excludedLocalPaths))
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
                    if (!IsPathExcludedFromLocalScan(appFolder, excludedLocalPaths))
                    {
                        candidate = ResolveAchievementFilePath(appFolder, fileName);
                        if (!string.IsNullOrWhiteSpace(candidate))
                        {
                            filePath = candidate;
                            return true;
                        }
                    }

                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (var matchDir in EnumerateDirectoriesByNameExcludingPaths(root, appId, excludedLocalPaths))
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

            if (game != null && TryGetFolderOverride(game.Id, out var overriddenFolderPath))
            {
                if (Directory.Exists(overriddenFolderPath) || File.Exists(overriddenFolderPath))
                {
                    var overrideCandidates = string.IsNullOrWhiteSpace(appId)
                        ? new List<string>()
                        : FindLocalFolders(appId);

                    folderPath = overriddenFolderPath;
                    candidateFolders = overrideCandidates;
                    isOverridden = true;
                    isAmbiguous = overrideCandidates.Count > 1;
                    return true;
                }

                _logger?.Warn($"Local save folder or file override for '{game.Name}' no longer exists: {overriddenFolderPath}");
            }

            if (string.IsNullOrWhiteSpace(appId))
            {
                return false;
            }

            var candidates = FindLocalFolders(appId);
            candidateFolders = candidates;

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
            var excludedLocalPaths = GetExcludedLocalPathEntries().ToList();

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (IsPathExcludedFromLocalScan(root, excludedLocalPaths))
                {
                    continue;
                }

                try
                {
                    if (Directory.Exists(root))
                    {
                        var candidate = Path.Combine(root, appId);
                        if (!IsPathExcludedFromLocalScan(candidate, excludedLocalPaths))
                        {
                            TryAddLocalFolderCandidate(folders, candidate);
                        }

                        if (string.Equals(Path.GetFileName(root), appId, StringComparison.OrdinalIgnoreCase))
                        {
                            TryAddLocalFolderCandidate(folders, root);
                        }

                        foreach (var matchDir in EnumerateDirectoriesByNameExcludingPaths(root, appId, excludedLocalPaths))
                        {
                            TryAddLocalFolderCandidate(folders, matchDir);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log($"SEARCH ERROR: root={root} msg={ex.Message}");
                }
            }

            foreach (var statsRoot in GetSteamAppCacheStatsRoots())
            {
                if (string.IsNullOrWhiteSpace(statsRoot))
                {
                    continue;
                }

                if (IsPathExcludedFromLocalScan(statsRoot, excludedLocalPaths))
                {
                    continue;
                }

                if (HasSteamAppCacheFilesForAppId(statsRoot, appId))
                {
                    TryAddLocalFolderCandidate(folders, statsRoot, requireLocalAchievementFiles: false);
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

        private static void TryAddLocalFolderCandidate(ICollection<string> folders, string folderPath, bool requireLocalAchievementFiles = true)
        {
            if (folders == null || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return;
            }

            if (!requireLocalAchievementFiles || HasLocalAchievementFiles(folderPath))
            {
                folders.Add(folderPath);
            }
        }

        private static bool HasSteamAppCacheFilesForAppId(string statsRoot, string appId)
        {
            if (string.IsNullOrWhiteSpace(statsRoot) || string.IsNullOrWhiteSpace(appId) || !Directory.Exists(statsRoot))
            {
                return false;
            }

            var schemaPath = Path.Combine(statsRoot, $"UserGameStatsSchema_{appId}.bin");
            if (File.Exists(schemaPath))
            {
                return true;
            }

            return Directory.EnumerateFiles(statsRoot, $"UserGameStats_*_{appId}.bin", SearchOption.TopDirectoryOnly).Any();
        }

        private static bool HasLocalAchievementFiles(string folderPath)
        {
            return !string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "achievements.ini")) ||
                !string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "user_stats.ini")) ||
                !string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "tenoke.ini")) ||
                !string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "achievements.json"));
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

            if (!string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "user_stats.ini")))
            {
                score += 2;
            }

            if (!string.IsNullOrWhiteSpace(ResolveAchievementFilePath(folderPath, "tenoke.ini")))
            {
                score += 1;
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
            foreach (var fileName in new[] { "achievements.ini", "user_stats.ini", "tenoke.ini", "achievements.json" })
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

            if (File.Exists(folderPath))
            {
                return string.Equals(
                    Path.GetFileName(folderPath),
                    fileName,
                    StringComparison.OrdinalIgnoreCase)
                    ? folderPath
                    : null;
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

            var settings = ProviderRegistry.Settings<LocalSettings>();
            if (settings?.WarnOnAmbiguousLocalFolder == false)
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
                    NotificationType.Info,
                    () => PlayniteAchievementsPlugin.Instance?.OpenManageAchievementsLocalFolderOverrideView(game.Id)));
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

        private SteamLocalProgressSummary TryGetSteamLocalProgressSummary(int appId, Game game = null)
        {
            if (appId <= 0)
            {
                return null;
            }

            foreach (var progressFilePath in GetSteamAchievementProgressFilePaths(game))
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

        private Dictionary<string, LocalEntry> TryGetSteamLibraryCacheEntries(int appId, Game game = null)
        {
            if (appId <= 0)
            {
                return null;
            }

            foreach (var cacheFilePath in GetSteamLibraryCacheFilePaths(appId, game))
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

        private IEnumerable<string> GetSteamLibraryCacheFilePaths(int appId, Game game = null)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileName = appId.ToString(CultureInfo.InvariantCulture) + ".json";
            var preferredAccountIds = GetPreferredSteamAccountIds(game).ToList();
            var hasPreferredAccountIds = preferredAccountIds.Count > 0;

            foreach (var userdataRoot in GetSteamUserdataRoots())
            {
                if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
                {
                    continue;
                }

                var directCachePath = Path.Combine(userdataRoot, "config", "librarycache", fileName);
                if (!hasPreferredAccountIds && File.Exists(directCachePath))
                {
                    candidates.Add(directCachePath);
                }

                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                    {
                        if (hasPreferredAccountIds)
                        {
                            var userDirName = Path.GetFileName(userDir)?.Trim();
                            if (string.IsNullOrWhiteSpace(userDirName) || !preferredAccountIds.Contains(userDirName))
                            {
                                continue;
                            }
                        }

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

            return candidates.ToList();
        }

        private IEnumerable<string> GetSteamAchievementProgressFilePaths(Game game = null)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var preferredAccountIds = GetPreferredSteamAccountIds(game).ToList();
            var hasPreferredAccountIds = preferredAccountIds.Count > 0;

            foreach (var userdataRoot in GetSteamUserdataRoots())
            {
                if (string.IsNullOrWhiteSpace(userdataRoot) || !Directory.Exists(userdataRoot))
                {
                    continue;
                }

                var directConfigPath = Path.Combine(userdataRoot, "config", "librarycache", "achievement_progress.json");
                if (!hasPreferredAccountIds && File.Exists(directConfigPath))
                {
                    candidates.Add(directConfigPath);
                }

                try
                {
                    foreach (var userDir in Directory.EnumerateDirectories(userdataRoot))
                    {
                        if (hasPreferredAccountIds)
                        {
                            var userDirName = Path.GetFileName(userDir)?.Trim();
                            if (string.IsNullOrWhiteSpace(userDirName) || !preferredAccountIds.Contains(userDirName))
                            {
                                continue;
                            }
                        }

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

            return candidates.ToList();
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

                var configuredRoots = roots.ToList();
                lock (_discoveryCacheLock)
                {
                    _steamUserdataRootsCache = configuredRoots;
                }

                return configuredRoots;
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
            if (!string.IsNullOrWhiteSpace(configuredPath) &&
                Directory.Exists(Path.Combine(configuredPath, "appcache", "stats")))
            {
                roots.Add(configuredPath);

                var configuredRoots = roots.ToList();
                lock (_discoveryCacheLock)
                {
                    _steamInstallRootsCache = configuredRoots;
                }

                return configuredRoots;
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

                if (Directory.Exists(Path.Combine(expanded, "appcache", "stats")))
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

            // "...\userdata" goes up one level to Steam root
            if (string.Equals(Path.GetFileName(expanded), "userdata", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(expanded);
                return parent?.FullName;
            }

            // "...\appcache\stats" goes up two levels to Steam root
            if (string.Equals(Path.GetFileName(expanded), "stats", StringComparison.OrdinalIgnoreCase))
            {
                var appcachePath = Directory.GetParent(expanded);
                if (string.Equals(appcachePath?.Name, "appcache", StringComparison.OrdinalIgnoreCase))
                {
                    return appcachePath?.Parent?.FullName;
                }
            }

            // "...\appcache" goes up one level to Steam root
            if (string.Equals(Path.GetFileName(expanded), "appcache", StringComparison.OrdinalIgnoreCase))
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
            var schemaLanguage = GetSteamLanguage();

            void CacheSchema(SchemaAndPercentages schema, string source)
            {
                _steamSchemaCache[appId] = schema;
                _steamSchemaSourceCache[appId] = source;
                _steamSchemaLanguageCache[appId] = schemaLanguage;
            }

            void RemoveCachedSchema()
            {
                _steamSchemaCache.Remove(appId);
                _steamSchemaSourceCache.Remove(appId);
                _steamSchemaLanguageCache.Remove(appId);
            }

            SchemaAndPercentages cachedFallback = null;
            string cachedFallbackSource = null;
            if (_steamSchemaCache.TryGetValue(appId, out var cached) && cached != null)
            {
                var cachedSource = _steamSchemaSourceCache.TryGetValue(appId, out var source) ? source : null;
                var cachedLanguage = _steamSchemaLanguageCache.TryGetValue(appId, out var language) ? language : "english";
                if (IsSchemaSourceCompatibleWithPreference(cachedSource, schemaPreference) &&
                    string.Equals(cachedLanguage, schemaLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(cachedSource, "steam-web-api", StringComparison.OrdinalIgnoreCase))
                    {
                        return cached;
                    }

                    // Keep an anonymous/local result available, but first give an authenticated
                    // Steam session a chance to replace it with the authoritative Web API schema.
                    cachedFallback = cached;
                    cachedFallbackSource = cachedSource;
                }
                else
                {
                    RemoveCachedSchema();
                }
            }

            async Task<SchemaAndPercentages> TryAuthenticatedSteamSchemaAsync()
            {
                if (_steamApiTokenService != null)
                {
                    try
                    {
                        var token = await _steamApiTokenService.ResolveAsync(CancellationToken.None).ConfigureAwait(false);
                        if (!string.IsNullOrWhiteSpace(token))
                        {
                            using (var apiHttp = new HttpClient())
                            {
                                var apiClient = new SteamApiClient(apiHttp, _logger);
                                var apiSchema = await apiClient.GetSchemaForGameDetailedAsync(token, appId, schemaLanguage, CancellationToken.None).ConfigureAwait(false);
                                if (apiSchema?.Achievements?.Count > 0)
                                {
                                    Log($"SCHEMA SOURCE SELECTED: appId={appId} source=steam-web-api language={schemaLanguage}");
                                    return apiSchema;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug(ex, $"[Local] Steam Web API schema fetch failed for appId={appId}; falling back to anonymous sources.");
                    }
                }

                return null;
            }

            var authenticatedSteamSchema = await TryAuthenticatedSteamSchemaAsync().ConfigureAwait(false);
            if (authenticatedSteamSchema?.Achievements?.Count > 0)
            {
                CacheSchema(authenticatedSteamSchema, "steam-web-api");
                return authenticatedSteamSchema;
            }

            if (cachedFallback != null)
            {
                CacheSchema(cachedFallback, cachedFallbackSource);
                return cachedFallback;
            }

            async Task<SchemaAndPercentages> TryPreferredAnonymousSchemaAsync()
            {
                // Get base schema for correlation with local entries
                SchemaAndPercentages baseSchema = null;
                switch (schemaPreference)
                {
                    case LocalSteamSchemaPreference.PreferSteamHunters:
                        baseSchema = await TryGetSteamHuntersSchemaAsync(appId).ConfigureAwait(false);
                        break;
                    case LocalSteamSchemaPreference.PreferSteam:
                        baseSchema = await TryGetSteamHuntersSchemaAsync(appId).ConfigureAwait(false);
                        break;
                    case LocalSteamSchemaPreference.PreferCompletionist:
                        using (var completionistClient = CreateAnonymousSteamHttpClient())
                        {
                            baseSchema = await TryGetCompletionistSchemaAsync(completionistClient, appId).ConfigureAwait(false);
                        }
                        break;
                    case LocalSteamSchemaPreference.PreferSteamCommunity:
                    default:
                        using (var communityClient = CreateAnonymousSteamHttpClient())
                        {
                            baseSchema = await TryGetSteamCommunityStatsSchemaAsync(communityClient, appId).ConfigureAwait(false);
                        }
                        break;
                }

                // If non-English and PreferSteam, merge Community translations into SteamHunters for localized descriptions
                if (baseSchema?.Achievements?.Count > 0 &&
                    schemaPreference == LocalSteamSchemaPreference.PreferSteam &&
                    !IsEnglishSteamLanguage(schemaLanguage))
                {
                    using (var communityClient = CreateAnonymousSteamHttpClient())
                    {
                        var localizedCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(communityClient, appId).ConfigureAwait(false);
                        if (localizedCommunitySchema?.Achievements?.Count > 0)
                        {
                            // Merge Community translations into base schema for localized text
                            MergeSchemaMetadata(baseSchema.Achievements, localizedCommunitySchema.Achievements, preferSourceText: true);
                            Log($"SCHEMA SOURCE MERGED: appId={appId} language={schemaLanguage} base=steamhunters merged=steam-community (localized descriptions)");
                        }
                    }
                }

                return baseSchema;
            }

            var appCacheSchema = TryGetSteamAppCacheSchemaAndPercentages(appId);
            if (appCacheSchema?.Achievements?.Count > 0)
            {
                var mergedAnonymousSchema = await TryPreferredAnonymousSchemaAsync().ConfigureAwait(false);
                if (mergedAnonymousSchema?.Achievements?.Count > 0)
                {
                    MergeSchemaMetadata(appCacheSchema.Achievements, mergedAnonymousSchema.Achievements, ShouldPreferLocalizedSteamText());

                    // The local binary can be stale, add achievements present in the online schema
                    // but missing from the binary so the final schema count matches Steam.
                    var existingNames = new HashSet<string>(
                        appCacheSchema.Achievements
                            .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Name))
                            .Select(a => a.Name),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (var onlineAch in mergedAnonymousSchema.Achievements)
                    {
                        if (onlineAch == null || string.IsNullOrWhiteSpace(onlineAch.Name))
                            continue;
                        if (!existingNames.Contains(onlineAch.Name))
                            appCacheSchema.Achievements.Add(onlineAch);
                    }

                    if (mergedAnonymousSchema.GlobalPercentages?.Count > 0)
                    {
                        appCacheSchema.GlobalPercentages = new Dictionary<string, double>(mergedAnonymousSchema.GlobalPercentages, StringComparer.OrdinalIgnoreCase);
                    }

                    CacheSchema(appCacheSchema, $"steam-appcache+{GetPreferredAnonymousSchemaSourceName(schemaPreference)}");
                }
                else
                {
                    CacheSchema(appCacheSchema, "steam-appcache");
                }

                return appCacheSchema;
            }

            var preferredAnonymousSchema = await TryPreferredAnonymousSchemaAsync().ConfigureAwait(false);
            if (preferredAnonymousSchema?.Achievements?.Count > 0)
            {
                CacheSchema(preferredAnonymousSchema, GetPreferredAnonymousSchemaSourceName(schemaPreference));
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
                    CacheSchema(steamHuntersSchema, "steamhunters");
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
                    CacheSchema(steamCommunitySchema, "steam-community");
                    Log($"SCHEMA SOURCE FALLBACK: appId={appId} preference={schemaPreference} source=steam-community");
                    return steamCommunitySchema;
                }
            }

            var installSchema = TryGetInstallSchema(appId);
            if (installSchema?.Achievements?.Count > 0)
            {
                CacheSchema(installSchema, "install-schema");
                return installSchema;
            }

            var publicSchema = await TryGetPublicSteamSchemaAsync(appId).ConfigureAwait(false);
            if (publicSchema?.Achievements?.Count > 0)
            {
                CacheSchema(publicSchema, "steam-public-profile");
                return publicSchema;
            }

            RemoveCachedSchema();
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

            // An authenticated Steam Web API schema is authoritative regardless of which
            // anonymous fallback the user selected.
            if (string.Equals(source, "steam-web-api", StringComparison.OrdinalIgnoreCase))
            {
                return true;
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
            return NormalizeSteamLanguage(_pluginSettings?.Persisted?.GlobalLanguage);
        }

        private static string NormalizeSteamLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return "english";
            }

            var normalized = language.Trim().ToLowerInvariant();
            return normalized switch
            {
                "english" or "en" or "en-us" or "en-gb" => "english",
                "german" or "deutsch" or "de" => "german",
                "french" or "francais" or "fran\u00e7ais" or "fr" => "french",
                "spanish" or "espanol" or "espa\u00f1ol" or "es" => "spanish",
                "italian" or "italiano" or "it" => "italian",
                "portuguese" or "pt" => "portuguese",
                "brazilian" or "pt-br" or "brazilian portuguese" => "brazilian",
                "russian" or "ru" => "russian",
                "polish" or "pl" => "polish",
                "dutch" or "nl" => "dutch",
                "swedish" or "sv" => "swedish",
                "finnish" or "fi" => "finnish",
                "danish" or "da" => "danish",
                "norwegian" or "no" => "norwegian",
                "hungarian" or "hu" => "hungarian",
                "czech" or "cs" => "czech",
                "romanian" or "ro" => "romanian",
                "turkish" or "tr" => "turkish",
                "greek" or "el" => "greek",
                "bulgarian" or "bg" => "bulgarian",
                "ukrainian" or "uk" => "ukrainian",
                "thai" or "th" => "thai",
                "vietnamese" or "vi" => "vietnamese",
                "japanese" or "ja" => "japanese",
                "koreana" or "korean" or "ko" => "koreana",
                "schinese" or "simplified chinese" or "zh-cn" => "schinese",
                "tchinese" or "traditional chinese" or "zh-tw" => "tchinese",
                "arabic" or "ar" => "arabic",
                _ => "english"
            };
        }

        private bool ShouldPreferLocalizedSteamText()
        {
            return !IsEnglishSteamLanguage(GetSteamLanguage());
        }

        private static bool IsEnglishSteamLanguage(string language)
        {
            language = language?.Trim();
            if (string.IsNullOrWhiteSpace(language))
            {
                return true;
            }

            return string.Equals(language, "english", StringComparison.OrdinalIgnoreCase)
                || string.Equals(language, "en", StringComparison.OrdinalIgnoreCase)
                || string.Equals(language, "en-us", StringComparison.OrdinalIgnoreCase)
                || string.Equals(language, "en-gb", StringComparison.OrdinalIgnoreCase);
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

        private async Task<SchemaAndPercentages> TryGetSteamCommunityStatsSchemaAsync(HttpClient httpClient, int appId, string languageOverride = null)
        {
            if (httpClient == null || appId <= 0)
            {
                return null;
            }

            try
            {
                var language = NormalizeSteamLanguage(languageOverride ?? GetSteamLanguage());
                var url = $"https://steamcommunity.com/stats/{appId}/achievements?l={language}";
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
                    var hidden = hiddenText.Contains("\uf070") || hiddenText.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0;

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

                SchemaAndPercentages localizedPublicSchema = null;
                if (ShouldPreferLocalizedSteamText())
                {
                    var publicApiClient = new SteamApiClient(httpClient, _logger);
                    localizedPublicSchema = await publicApiClient
                        .GetPublicSchemaForGameDetailedAsync(appId, GetSteamLanguage(), CancellationToken.None)
                        .ConfigureAwait(false);
                    if (localizedPublicSchema?.Achievements?.Count > 0)
                    {
                        MergeSchemaMetadata(
                            achievements,
                            localizedPublicSchema.Achievements,
                            preferSourceText: true);
                        Log(
                            $"STEAMHUNTERS SCHEMA LOCALIZED: appId={appId} " +
                            $"language={GetSteamLanguage()} source=steam-public-api " +
                            $"count={localizedPublicSchema.Achievements.Count}");
                    }
                }

                var steamCommunitySchema = await TryGetSteamCommunityStatsSchemaAsync(httpClient, appId).ConfigureAwait(false);
                if (ShouldPreferLocalizedSteamText() &&
                    (localizedPublicSchema?.Achievements == null || localizedPublicSchema.Achievements.Count == 0))
                {
                    var steamCommunityEnglishSchema = await TryGetSteamCommunityStatsSchemaAsync(httpClient, appId, "english").ConfigureAwait(false);
                    var localizedBridge = BuildCommunityLocalizedBridge(
                        steamCommunityEnglishSchema?.Achievements,
                        steamCommunitySchema?.Achievements);
                    ApplyLocalizedCommunityTextToSteamHuntersAchievements(achievements, localizedBridge);
                }

                MergeSchemaMetadata(achievements, steamCommunitySchema?.Achievements, ShouldPreferLocalizedSteamText());

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

        private static IReadOnlyList<SchemaAchievement> BuildCommunityLocalizedBridge(
            IReadOnlyList<SchemaAchievement> englishAchievements,
            IReadOnlyList<SchemaAchievement> localizedAchievements)
        {
            if (englishAchievements == null || localizedAchievements == null ||
                englishAchievements.Count == 0 || localizedAchievements.Count == 0)
            {
                return Array.Empty<SchemaAchievement>();
            }

            var localizedByIcon = BuildSchemaLookupByIcon(localizedAchievements, useLockedIcon: false);
            var localizedByLockedIcon = BuildSchemaLookupByIcon(localizedAchievements, useLockedIcon: true);
            var bridge = new List<SchemaAchievement>();

            foreach (var englishAchievement in englishAchievements)
            {
                if (englishAchievement == null || string.IsNullOrWhiteSpace(englishAchievement.DisplayName))
                {
                    continue;
                }

                SchemaAchievement localizedAchievement = null;

                var iconHash = ExtractAchievementIconHash(englishAchievement.Icon);
                if (!string.IsNullOrWhiteSpace(iconHash) &&
                    localizedByIcon.TryGetValue(iconHash, out var byIcon) &&
                    byIcon != null)
                {
                    localizedAchievement = byIcon;
                }

                if (localizedAchievement == null)
                {
                    var lockedIconHash = ExtractAchievementIconHash(englishAchievement.IconGray);
                    if (!string.IsNullOrWhiteSpace(lockedIconHash) &&
                        localizedByLockedIcon.TryGetValue(lockedIconHash, out var byLockedIcon) &&
                        byLockedIcon != null)
                    {
                        localizedAchievement = byLockedIcon;
                    }
                }

                if (localizedAchievement == null)
                {
                    continue;
                }

                bridge.Add(new SchemaAchievement
                {
                    // Preserve English title/description as match keys and carry localized text separately.
                    Name = englishAchievement.Name,
                    DisplayName = englishAchievement.DisplayName,
                    Description = englishAchievement.Description,
                    LocalizedDisplayName = localizedAchievement.DisplayName,
                    LocalizedDescription = localizedAchievement.Description,
                    Icon = localizedAchievement.Icon,
                    IconGray = localizedAchievement.IconGray,
                    Hidden = localizedAchievement.Hidden,
                    GlobalPercent = localizedAchievement.GlobalPercent
                });
            }

            return bridge;
        }

        private static void ApplyLocalizedCommunityTextToSteamHuntersAchievements(
            IReadOnlyList<SchemaAchievement> targetAchievements,
            IReadOnlyList<SchemaAchievement> localizedBridge)
        {
            if (targetAchievements == null || localizedBridge == null ||
                targetAchievements.Count == 0 || localizedBridge.Count == 0)
            {
                return;
            }

            var bridgeByText = BuildSchemaLookupByText(localizedBridge);
            var bridgeByTitle = BuildSchemaLookupByTitle(localizedBridge);

            foreach (var target in targetAchievements)
            {
                if (target == null)
                {
                    continue;
                }

                SchemaAchievement bridgeMatch = null;
                var textLookupKey = BuildAchievementLookupKey(target.DisplayName, target.Description);
                if (!string.IsNullOrWhiteSpace(textLookupKey) &&
                    bridgeByText.TryGetValue(textLookupKey, out var byText))
                {
                    bridgeMatch = byText;
                }

                if (bridgeMatch == null)
                {
                    var titleLookupKey = BuildAchievementTitleLookupKey(target.DisplayName);
                    if (!string.IsNullOrWhiteSpace(titleLookupKey) &&
                        bridgeByTitle.TryGetValue(titleLookupKey, out var byTitle))
                    {
                        bridgeMatch = byTitle;
                    }
                }

                if (bridgeMatch == null)
                {
                    continue;
                }

                if (ShouldPreferSchemaDisplayName(target.DisplayName, bridgeMatch.LocalizedDisplayName, target.Name, preferLocalizedSchemaText: true))
                {
                    target.DisplayName = bridgeMatch.LocalizedDisplayName;
                }

                if (ShouldPreferSchemaDescription(target.Description, bridgeMatch.LocalizedDescription, preferLocalizedSchemaText: true))
                {
                    target.Description = bridgeMatch.LocalizedDescription;
                }
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
            var points = item["points"]?.Value<int?>();
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
                GlobalPercent = steamPercentage,
                Points = points
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
            var excludedLocalPaths = GetExcludedLocalPathEntries().ToList();

            foreach (var root in GetLocalRootPaths())
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                {
                    continue;
                }

                if (IsPathExcludedFromLocalScan(root, excludedLocalPaths))
                {
                    continue;
                }

                try
                {
                    foreach (var directory in EnumerateDirectoriesByNameExcludingPaths(root, appId.ToString(CultureInfo.InvariantCulture), excludedLocalPaths))
                    {
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
                roots.Add(Path.Combine(documents, "OnlineFix"));
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

        private IEnumerable<string> GetExcludedLocalPathEntries()
        {
            foreach (var excludedPath in LocalSettings.SplitExtraLocalPaths(_pluginSettings?.Persisted?.ExcludedLocalPaths))
            {
                var expanded = Environment.ExpandEnvironmentVariables(excludedPath);
                var normalized = NormalizeDirectoryPath(expanded);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    yield return normalized;
                }
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPathExcludedFromLocalScan(string path, IReadOnlyList<string> excludedRoots)
        {
            if (string.IsNullOrWhiteSpace(path) || excludedRoots == null || excludedRoots.Count == 0)
            {
                return false;
            }

            var normalizedPath = NormalizeDirectoryPath(path);
            if (string.IsNullOrWhiteSpace(normalizedPath))
            {
                return false;
            }

            foreach (var excludedRoot in excludedRoots)
            {
                if (string.IsNullOrWhiteSpace(excludedRoot))
                {
                    continue;
                }

                if (string.Equals(normalizedPath, excludedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (normalizedPath.StartsWith(excludedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(excludedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> EnumerateDirectoriesByNameExcludingPaths(string root, string directoryName, IReadOnlyList<string> excludedRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(directoryName) || !Directory.Exists(root))
            {
                yield break;
            }

            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                IEnumerable<string> childDirectories;

                try
                {
                    childDirectories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (var childDirectory in childDirectories)
                {
                    if (IsPathExcludedFromLocalScan(childDirectory, excludedRoots))
                    {
                        continue;
                    }

                    if (string.Equals(Path.GetFileName(childDirectory), directoryName, StringComparison.OrdinalIgnoreCase))
                    {
                        yield return childDirectory;
                    }

                    stack.Push(childDirectory);
                }
            }
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

            if (string.IsNullOrWhiteSpace(settings.ExcludedLocalPaths)
                && !string.IsNullOrWhiteSpace(_pluginSettings?.Persisted?.ExcludedLocalPaths))
            {
                settings.ExcludedLocalPaths = _pluginSettings.Persisted.ExcludedLocalPaths;
            }

            settings.SteamUserdataPath ??= string.Empty;
            settings.SteamAppIdOverrides ??= new Dictionary<Guid, int>();
            settings.LocalFolderOverrides ??= new Dictionary<Guid, string>();
            settings.SteamAppCacheUserOverrides ??= new Dictionary<Guid, string>();
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
                    _pluginSettings.Persisted.ExcludedLocalPaths = localSettings.ExcludedLocalPaths ?? string.Empty;
                }

                // Clear path-discovery caches so updated Steam paths take effect immediately.
                lock (_discoveryCacheLock)
                {
                    _steamInstallRootsCache = null;
                    _steamUserdataRootsCache = null;
                    _steamLocalProgressAppIdsCache = null;
                    _steamAppCacheSchemaFilePathsCache.Clear();
                    _localFolderCandidatesCache.Clear();
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
            @"^(?:ach(?:ieve(?:ment)?)?|stat|unlock|trophy|badge)[_\-\s]?(\d+)$",
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

            [JsonProperty("iconGray")]
            public string iconGray { get; set; }

            [JsonProperty("icon_gray")]
            private string iconGrayAlias
            {
                get => iconGray;
                set => iconGray = value;
            }

            public bool hidden { get; set; }
            public double? percent { get; set; }

            public int? progress { get; set; }

            [JsonProperty("max_progress")]
            public int? max_progress { get; set; }
        }

        private sealed class IniSectionInfo
        {
            public string Name { get; set; }
            public int StartLineIndex { get; set; }
            public int EndLineIndex { get; set; }
            public int UnlockedLineIndex { get; set; } = -1;
            public string UnlockedKeyName { get; set; }
            public string UnlockedValueRaw { get; set; }
            public int TimeLineIndex { get; set; } = -1;
            public string TimeKeyName { get; set; }
        }

        private sealed class UbisoftAchievementMetadata
        {
            public string CanonicalName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
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

        private sealed class SteamKvSchemaEntry
        {
            public int StatId { get; set; }
            public int Bit { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public bool Hidden { get; set; }
            public string IconHash { get; set; }
            public string IconGrayHash { get; set; }
        }

        private sealed class SteamKvUserStatData
        {
            public uint DataU32 { get; set; }
            public Dictionary<int, long> UnlockTimes { get; set; } = new Dictionary<int, long>();
        }

        private sealed class SteamAppCacheSchemaSection
        {
            public int? SectionId { get; set; }
            public List<SteamAppCacheSchemaEntry> Entries { get; set; } = new List<SteamAppCacheSchemaEntry>();
        }

        private sealed class SteamAppCacheUnlockTimeSection
        {
            public int? SectionId { get; set; }
            public uint? DataValue { get; set; }
            public Dictionary<int, long> UnlockTimes { get; set; } = new Dictionary<int, long>();
        }
    }
}
