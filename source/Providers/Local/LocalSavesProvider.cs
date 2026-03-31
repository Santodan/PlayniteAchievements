using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
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
            var appId = GetAppId(game);
            if (string.IsNullOrEmpty(appId)) return null;

            if (!TryFindLocalFolder(appId, out var localFolderPath)) return null;

            var hasAchievementsFile = TryFindAchievementsFile(appId, out var jsonPath);

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
            }

            try
            {
                if (hasAchievementsFile)
                {
                    var json = await Task.Run(() => File.ReadAllText(jsonPath));
                    var raw = JsonConvert.DeserializeObject<Dictionary<string, LocalEntry>>(json);
                    if (raw != null && raw.Count > 0)
                    {
                        foreach (var kv in raw)
                        {
                            apiNameMap.TryGetValue(kv.Key, out var schemaAch);
                            var entry = kv.Value;
                            var displayName = !string.IsNullOrWhiteSpace(entry.displayName)
                                ? entry.displayName
                                : schemaAch?.DisplayName ?? kv.Key;

                            var description = !string.IsNullOrWhiteSpace(entry.description)
                                ? entry.description
                                : schemaAch?.Description ?? "Local achievement from " + ProviderName;

                            var unlockedIcon = !string.IsNullOrWhiteSpace(entry.icon)
                                ? entry.icon
                                : schemaAch?.Icon ?? "Resources/UnlockedAchIcon.png";

                            var lockedIcon = !string.IsNullOrWhiteSpace(entry.iconGray)
                                ? entry.iconGray
                                : schemaAch?.IconGray ?? "Resources/HiddenAchIcon.png";

                            var detail = new AchievementDetail
                            {
                                ApiName = kv.Key,
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

                            double? globalPercent = null;
                            if (entry.percent.HasValue)
                            {
                                globalPercent = entry.percent.Value;
                            }
                            else if (steamSchema?.GlobalPercentages?.TryGetValue(kv.Key, out var percent) == true)
                            {
                                globalPercent = percent;
                            }

                            if (globalPercent.HasValue)
                            {
                                detail.GlobalPercentUnlocked = NormalizePercent(globalPercent.Value);
                                if (detail.GlobalPercentUnlocked.HasValue)
                                {
                                    detail.Rarity = PercentRarityHelper.GetRarityTier(detail.GlobalPercentUnlocked.Value);
                                }
                            }

                            data.Achievements.Add(detail);
                        }

                        Log($"SUCCESS: {game.Name} - Found {data.Achievements.Count} achievement definitions from local save file.");
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

        private string GetAppId(Game game)
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

        private bool TryFindAchievementsFile(string appId, out string jsonPath)
        {
            jsonPath = null;
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
                    if (candidate.EndsWith("achievements.json", StringComparison.OrdinalIgnoreCase))
                    {
                        if (File.Exists(candidate))
                        {
                            jsonPath = candidate;
                            return true;
                        }

                        continue;
                    }

                    candidate = Path.Combine(candidate, appId, "achievements.json");
                    if (File.Exists(candidate))
                    {
                        jsonPath = candidate;
                        return true;
                    }

                    if (!Directory.Exists(root))
                    {
                        continue;
                    }

                    foreach (var matchDir in Directory.EnumerateDirectories(root, appId, SearchOption.AllDirectories))
                    {
                        candidate = Path.Combine(matchDir, "achievements.json");
                        if (File.Exists(candidate))
                        {
                            jsonPath = candidate;
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

        private bool TryFindLocalFolder(string appId, out string folderPath)
        {
            folderPath = null;
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
                    if (Directory.Exists(root))
                    {
                        var candidate = Path.Combine(root, appId);
                        if (Directory.Exists(candidate))
                        {
                            folderPath = candidate;
                            return true;
                        }

                        if (string.Equals(Path.GetFileName(root), appId, StringComparison.OrdinalIgnoreCase))
                        {
                            folderPath = root;
                            return true;
                        }

                        foreach (var matchDir in Directory.EnumerateDirectories(root, appId, SearchOption.AllDirectories))
                        {
                            folderPath = matchDir;
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