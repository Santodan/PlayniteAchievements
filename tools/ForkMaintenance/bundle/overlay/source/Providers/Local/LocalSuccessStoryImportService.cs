using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.ImportedGameMetadata;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Local
{
    internal sealed class LocalSuccessStoryImportService
    {
        internal const string SourceCategoryPrefix = "source:";
        internal const string SourceCategoryMissing = "source:<missing>";
        internal const string FlagCategoryManual = "flag:manual";
        internal const string FlagCategoryEmulators = "flag:emulators";
        internal const string FlagCategoryEmulatorPath = "flag:emulator-path";

        private static readonly string[] GuidIdPropertyCandidates =
        {
            "PlayniteGameId",
            "GameId",
            "Id"
        };

        private static readonly string[] NumericIdPropertyCandidates =
        {
            "DatabaseId",
            "GameDatabaseId",
            "DbId",
            "GameDbId"
        };

        private static readonly string[] NamePropertyCandidates =
        {
            "GameName",
            "Name",
            "Title"
        };

        private readonly IPlayniteAPI _api;
        private readonly ICacheManager _cacheManager;
        private readonly AchievementDataService _achievementDataService;
        private readonly ILogger _logger;
        private readonly ImportedGameMetadataApplier _metadataApplier;
        private readonly string _metadataSourceId;
        private readonly MetadataPlugin _metadataPlugin;
        private readonly string _targetSourceName;

        private sealed class LegacyUnlockState
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public bool Hidden { get; set; }
            public double? Percent { get; set; }
            public int? Points { get; set; }
            public bool Unlocked { get; set; }
            public DateTime? UnlockTimeUtc { get; set; }
        }

        private sealed class GameResolutionIndex
        {
            public Dictionary<Guid, Game> ByGuid { get; } = new Dictionary<Guid, Game>();

            public Dictionary<string, Game> ByNameNormalized { get; } =
                new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, Game> ByDatabaseId { get; } =
                new Dictionary<string, Game>(StringComparer.OrdinalIgnoreCase);
        }

        internal sealed class SuccessStoryImportProgressInfo
        {
            public double Percent { get; set; }
            public string Message { get; set; }
            public string Detail { get; set; }
        }

        internal sealed class SuccessStoryImportResult
        {
            public int ScannedFileCount { get; set; }
            public int ParsedFileCount { get; set; }
            public int FailedFileCount { get; set; }
            public int SkippedNonLocalCount { get; set; }
            public int MatchedGameCount { get; set; }
            public int MissingGameCount { get; set; }
            public int CreatedGameCount { get; set; }
            public int MissingCacheCount { get; set; }
            public int CreatedFromSuccessStoryCount { get; set; }
            public int AddedAchievementRows { get; set; }
            public int UpdatedGameCount { get; set; }
            public int UnchangedGameCount { get; set; }
            public int ImportedUnlockRows { get; set; }
            public int MatchedAchievementRows { get; set; }
            public int UpdatedAchievementRows { get; set; }

            public string BuildSummary()
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "Scanned {0} file(s), parsed {1}, failed {2}, skipped non-local {3}. Matched {4} game(s), created missing games {5}, missing game matches {6}, missing cache data {7}, created from SuccessStory {8}. Updated {9} game(s), unchanged {10}. Imported unlock rows {11}, matched achievements {12}, updated achievements {13}, added achievements {14}.",
                    ScannedFileCount,
                    ParsedFileCount,
                    FailedFileCount,
                    SkippedNonLocalCount,
                    MatchedGameCount,
                    CreatedGameCount,
                    MissingGameCount,
                    MissingCacheCount,
                    CreatedFromSuccessStoryCount,
                    UpdatedGameCount,
                    UnchangedGameCount,
                    ImportedUnlockRows,
                    MatchedAchievementRows,
                    UpdatedAchievementRows,
                    AddedAchievementRows);
            }
        }

        internal sealed class SuccessStoryCategoryScanItem
        {
            public string Key { get; set; }
            public string DisplayName { get; set; }
            public int Count { get; set; }
        }

        internal sealed class SuccessStoryCategoryScanResult
        {
            public int ScannedFileCount { get; set; }
            public int ParsedFileCount { get; set; }
            public int FailedFileCount { get; set; }
            public List<SuccessStoryCategoryScanItem> Categories { get; set; } = new List<SuccessStoryCategoryScanItem>();
        }

        public LocalSuccessStoryImportService(
            IPlayniteAPI api,
            ICacheManager cacheManager,
            AchievementDataService achievementDataService,
            ILogger logger,
            string metadataSourceId = null,
            string targetSourceName = null)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _achievementDataService = achievementDataService ?? throw new ArgumentNullException(nameof(achievementDataService));
            _logger = logger;
            _metadataSourceId = metadataSourceId;
            _targetSourceName = targetSourceName?.Trim() ?? string.Empty;
            _metadataApplier = new ImportedGameMetadataApplier(api, logger, "SuccessStoryImport");
            _metadataPlugin = ImportedGameMetadataSourceCatalog.ResolveMetadataPlugin(api, logger, metadataSourceId);
        }

        public async Task<SuccessStoryImportResult> ImportFromFolderAsync(
            string folderPath,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string> includeCategoryKeys = null,
            IReadOnlyCollection<string> excludeCategoryKeys = null,
            bool overwriteExistingAchievements = false,
            IProgress<SuccessStoryImportProgressInfo> progress = null)
        {
            var result = new SuccessStoryImportResult();
            var normalizedPath = (folderPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
            {
                return result;
            }

            var jsonFiles = Directory.EnumerateFiles(normalizedPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            result.ScannedFileCount = jsonFiles.Count;

            if (jsonFiles.Count == 0)
            {
                return result;
            }

            var gameIndex = BuildGameResolutionIndex();
            var total = jsonFiles.Count;
            var selectedCategoryKeys = NormalizeCategorySelection(includeCategoryKeys);
            var excludedCategoryKeys = NormalizeCategorySelection(excludeCategoryKeys);

            for (var i = 0; i < jsonFiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filePath = jsonFiles[i];
                var displayFileName = Path.GetFileName(filePath);
                ReportProgress(
                    progress,
                    i * 100d / Math.Max(1, total),
                    $"Importing SuccessStory file {i + 1}/{total}...",
                    displayFileName);

                try
                {
                    var root = LoadJson(filePath);
                    if (root == null)
                    {
                        result.FailedFileCount++;
                        continue;
                    }

                    result.ParsedFileCount++;

                    if (!ShouldImportEntry(root, selectedCategoryKeys, excludedCategoryKeys))
                    {
                        result.SkippedNonLocalCount++;
                        continue;
                    }

                    if (!TryResolveGame(filePath, root, gameIndex, out var matchedGame))
                    {
                        matchedGame = CreateGameFromSuccessStoryRoot(root);
                        if (matchedGame == null)
                        {
                            result.MissingGameCount++;
                            continue;
                        }

                        result.CreatedGameCount++;
                        IndexGame(gameIndex, matchedGame);
                        ApplyMetadataToNewGame(matchedGame, root);
                    }

                    result.MatchedGameCount++;

                    var sourceAchievements = ExtractAchievementStates(root);
                    if (sourceAchievements.Count == 0)
                    {
                        result.UnchangedGameCount++;
                        continue;
                    }

                    var unlockStates = sourceAchievements.Where(a => a.Unlocked).ToList();
                    result.ImportedUnlockRows += unlockStates.Count;

                    var cachedData = _achievementDataService.GetRawGameAchievementData(matchedGame.Id);
                    var wasCacheMissing = cachedData?.Achievements == null || cachedData.Achievements.Count == 0;
                    var createdFromSuccessStory = false;
                    if (wasCacheMissing)
                    {
                        result.MissingCacheCount++;
                        cachedData = BuildGameDataFromSuccessStory(matchedGame, root, sourceAchievements);
                        result.CreatedFromSuccessStoryCount++;
                        createdFromSuccessStory = true;
                    }
                    else if (overwriteExistingAchievements)
                    {
                        ReplaceAchievementsFromSuccessStory(cachedData, sourceAchievements);
                    }

                    var addedRowsForGame = EnsureAchievementsFromSuccessStory(cachedData, sourceAchievements);
                    result.AddedAchievementRows += addedRowsForGame;

                    var matchedRowsForGame = 0;
                    var updatedRowsForGame = 0;
                    var changed = ApplyUnlockStatesToCachedData(cachedData, unlockStates, out matchedRowsForGame, out updatedRowsForGame);
                    result.MatchedAchievementRows += matchedRowsForGame;
                    result.UpdatedAchievementRows += updatedRowsForGame;

                    if (!changed && addedRowsForGame == 0 && !createdFromSuccessStory)
                    {
                        result.UnchangedGameCount++;
                        continue;
                    }

                    cachedData.LastUpdatedUtc = DateTime.UtcNow;
                    var writeResult = _cacheManager.SaveGameData(matchedGame.Id.ToString(), cachedData);
                    if (writeResult?.Success == true)
                    {
                        result.UpdatedGameCount++;
                    }
                    else
                    {
                        result.UnchangedGameCount++;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result.FailedFileCount++;
                    _logger?.Warn(ex, $"[LocalSuccessStoryImport] Failed processing '{filePath}'.");
                }

                await Task.Yield();
            }

            ReportProgress(progress, 100d, "SuccessStory import completed.", result.BuildSummary());
            return result;
        }

        public static SuccessStoryCategoryScanResult ScanCategoriesInFolder(string folderPath)
        {
            var result = new SuccessStoryCategoryScanResult();
            var normalizedPath = (folderPath ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalizedPath) || !Directory.Exists(normalizedPath))
            {
                return result;
            }

            var categoryCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var jsonFiles = Directory.EnumerateFiles(normalizedPath, "*.json", SearchOption.TopDirectoryOnly).ToList();
            result.ScannedFileCount = jsonFiles.Count;

            foreach (var filePath in jsonFiles)
            {
                try
                {
                    var root = LoadJson(filePath);
                    if (root == null)
                    {
                        result.FailedFileCount++;
                        continue;
                    }

                    result.ParsedFileCount++;
                    foreach (var key in GetCategoryKeys(root))
                    {
                        if (string.IsNullOrWhiteSpace(key))
                        {
                            continue;
                        }

                        categoryCounts[key] = categoryCounts.TryGetValue(key, out var count)
                            ? count + 1
                            : 1;
                    }
                }
                catch
                {
                    result.FailedFileCount++;
                }
            }

            result.Categories = categoryCounts
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new SuccessStoryCategoryScanItem
                {
                    Key = pair.Key,
                    DisplayName = BuildCategoryDisplayName(pair.Key),
                    Count = pair.Value
                })
                .ToList();

            return result;
        }

        private static HashSet<string> NormalizeCategorySelection(IReadOnlyCollection<string> includeCategoryKeys)
        {
            if (includeCategoryKeys == null || includeCategoryKeys.Count == 0)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return new HashSet<string>(
                includeCategoryKeys
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim()),
                StringComparer.OrdinalIgnoreCase);
        }

        private static bool ShouldImportEntry(
            JObject root,
            HashSet<string> selectedCategoryKeys,
            HashSet<string> excludedCategoryKeys)
        {
            var categoryKeys = GetCategoryKeys(root);

            if (excludedCategoryKeys != null
                && excludedCategoryKeys.Count > 0
                && categoryKeys.Any(key => excludedCategoryKeys.Contains(key)))
            {
                return false;
            }

            if (selectedCategoryKeys == null || selectedCategoryKeys.Count == 0)
            {
                return IsLocalSuccessStoryEntry(root);
            }

            return categoryKeys.Any(key => selectedCategoryKeys.Contains(key));
        }

        internal static IReadOnlyCollection<string> GetCategoryKeys(JObject root)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (root == null)
            {
                return keys.ToList();
            }

            var sourceName = root["SourcesLink"]?["Name"]?.Value<string>()?.Trim();
            var sourceUrl = root["SourcesLink"]?["Url"]?.Value<string>()?.Trim();
            var hasSourceLink = root["SourcesLink"] != null;
            var isManual = root.Value<bool?>("IsManual") == true;
            var isEmulators = root.Value<bool?>("IsEmulators") == true;

            keys.Add(BuildSourceCategoryKey(sourceName, sourceUrl));

            if (isManual)
            {
                keys.Add(FlagCategoryManual);
            }

            if (isEmulators)
            {
                keys.Add(FlagCategoryEmulators);
            }

            if (!hasSourceLink && LooksLikeEmulatorAchievementPayload(root))
            {
                keys.Add(FlagCategoryEmulatorPath);
            }

            return keys.ToList();
        }

        private static string BuildSourceCategoryKey(string sourceName, string sourceUrl = null)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return SourceCategoryMissing;
            }

            var key = SourceCategoryPrefix + sourceName.Trim();

            // Add sub-type for Steam to distinguish between profile-based and stats-only URLs
            if (string.Equals(sourceName, "Steam", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                var hasProfileUrl = sourceUrl.IndexOf("/profiles/", StringComparison.OrdinalIgnoreCase) >= 0;
                var hasStatsUrl = sourceUrl.IndexOf("steamcommunity.com/stats/", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hasStatsUrl && !hasProfileUrl)
                {
                    key += "@stats";
                }
                else if (hasProfileUrl)
                {
                    key += "@profile";
                }
            }

            return key;
        }

        internal static string BuildCategoryDisplayName(string categoryKey)
        {
            if (string.IsNullOrWhiteSpace(categoryKey))
            {
                return "Unknown";
            }

            if (categoryKey.StartsWith(SourceCategoryPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var sourceLabel = categoryKey.Substring(SourceCategoryPrefix.Length);
                if (string.IsNullOrWhiteSpace(sourceLabel) || string.Equals(sourceLabel, "<missing>", StringComparison.OrdinalIgnoreCase))
                {
                    return "SourceLink: Missing";
                }

                // Handle Steam source with profile/stats distinction
                if (sourceLabel.StartsWith("Steam@", StringComparison.OrdinalIgnoreCase))
                {
                    var subType = sourceLabel.Substring(6); // Extract "profile" or "stats"
                    return $"SourceLink: Steam from {subType}";
                }

                return $"SourceLink: {sourceLabel}";
            }

            if (string.Equals(categoryKey, FlagCategoryManual, StringComparison.OrdinalIgnoreCase))
            {
                return "Flag: Manual";
            }

            if (string.Equals(categoryKey, FlagCategoryEmulators, StringComparison.OrdinalIgnoreCase))
            {
                return "Flag: Emulators";
            }

            if (string.Equals(categoryKey, FlagCategoryEmulatorPath, StringComparison.OrdinalIgnoreCase))
            {
                return "Flag: Local emulator paths in achievements";
            }

            return categoryKey;
        }

        private static bool IsLocalSuccessStoryEntry(JObject root)
        {
            if (root == null)
            {
                return false;
            }

            var sourceName = root["SourcesLink"]?["Name"]?.Value<string>()?.Trim();
            var sourceUrl = root["SourcesLink"]?["Url"]?.Value<string>()?.Trim();
            var hasSourceLink = root["SourcesLink"] != null;
            var isManual = root.Value<bool?>("IsManual") == true;
            var isEmulatorEntry = root.Value<bool?>("IsEmulators") == true;

            if (isEmulatorEntry)
            {
                return true;
            }

            if (!hasSourceLink && LooksLikeEmulatorAchievementPayload(root))
            {
                return true;
            }

            if (string.Equals(sourceName, "Local", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var isSteamSource = string.Equals(sourceName, "Steam", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(sourceUrl)
                    && sourceUrl.IndexOf("steamcommunity.com/stats/", StringComparison.OrdinalIgnoreCase) >= 0);

            var isSteamStatsWithoutProfile =
                !string.IsNullOrWhiteSpace(sourceUrl)
                && sourceUrl.IndexOf("steamcommunity.com/stats/", StringComparison.OrdinalIgnoreCase) >= 0
                && sourceUrl.IndexOf("/profiles/", StringComparison.OrdinalIgnoreCase) < 0;

            if (isSteamStatsWithoutProfile)
            {
                return true;
            }

            // SuccessStory local/manual Steam imports are represented as manual Steam entries.
            return isManual && isSteamSource;
        }

        private static bool LooksLikeEmulatorAchievementPayload(JObject root)
        {
            var items = root?["Items"] as JArray;
            if (items == null || items.Count == 0)
            {
                return false;
            }

            foreach (var token in items)
            {
                var item = token as JObject;
                var unlockedPath = item?["UrlUnlocked"]?.Value<string>()?.Trim();
                var lockedPath = item?["UrlLocked"]?.Value<string>()?.Trim();

                if (LooksLikeLocalPath(unlockedPath) || LooksLikeLocalPath(lockedPath))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool LooksLikeLocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var trimmed = value.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return trimmed.IndexOf('\\') >= 0 || trimmed.IndexOf('/') >= 0;
        }

        private static void IndexGame(GameResolutionIndex index, Game game)
        {
            if (index == null || game == null || game.Id == Guid.Empty)
            {
                return;
            }

            index.ByGuid[game.Id] = game;

            var normalizedName = NormalizeKey(game.Name);
            if (!string.IsNullOrWhiteSpace(normalizedName) && !index.ByNameNormalized.ContainsKey(normalizedName))
            {
                index.ByNameNormalized[normalizedName] = game;
            }

            var databaseId = TryGetGameDatabaseId(game);
            if (!string.IsNullOrWhiteSpace(databaseId) && !index.ByDatabaseId.ContainsKey(databaseId))
            {
                index.ByDatabaseId[databaseId] = game;
            }
        }

        private GameResolutionIndex BuildGameResolutionIndex()
        {
            var index = new GameResolutionIndex();

            foreach (var game in _api.Database?.Games ?? Enumerable.Empty<Game>())
            {
                if (game == null || game.Id == Guid.Empty)
                {
                    continue;
                }

                index.ByGuid[game.Id] = game;

                var normalizedName = NormalizeKey(game.Name);
                if (!string.IsNullOrWhiteSpace(normalizedName) && !index.ByNameNormalized.ContainsKey(normalizedName))
                {
                    index.ByNameNormalized[normalizedName] = game;
                }

                var databaseId = TryGetGameDatabaseId(game);
                if (!string.IsNullOrWhiteSpace(databaseId) && !index.ByDatabaseId.ContainsKey(databaseId))
                {
                    index.ByDatabaseId[databaseId] = game;
                }
            }

            return index;
        }

        private static JObject LoadJson(string filePath)
        {
            var raw = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            using (var reader = new StringReader(raw))
            using (var jsonReader = new JsonTextReader(reader) { DateParseHandling = DateParseHandling.None })
            {
                return JObject.Load(jsonReader);
            }
        }

        private Game CreateGameFromSuccessStoryRoot(JObject root)
        {
            if (root == null)
            {
                return null;
            }

            var gameName = FirstNonEmptyRoot(root, "Name", "GameName", "Title")?.Trim();
            if (string.IsNullOrWhiteSpace(gameName))
            {
                gameName = "Imported from SuccessStory";
            }

            var metadata = new GameMetadata
            {
                Name = gameName,
                SortingName = gameName,
                IsInstalled = false,
                InstallDirectory = null
            };

            if (TryExtractAppIdFromSourceUrl(root, out var appId) && appId > 0)
            {
                metadata.GameId = appId.ToString(CultureInfo.InvariantCulture);
            }

            var resolvedSourceName = ResolveTargetSourceName(root);
            if (!string.IsNullOrWhiteSpace(resolvedSourceName))
            {
                metadata.Source = new MetadataNameProperty(resolvedSourceName);
            }

            try
            {
                return _api.Database.ImportGame(metadata);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[LocalSuccessStoryImport] Failed creating game '{gameName}' from SuccessStory root.");
                return null;
            }
        }

        private string ResolveTargetSourceName(JObject root)
        {
            if (!string.IsNullOrWhiteSpace(_targetSourceName))
            {
                return ResolveSourceNameFromDatabase(_targetSourceName);
            }

            var sourceName = root?["SourcesLink"]?["Name"]?.Value<string>()?.Trim();
            return ResolveSourceNameFromDatabase(sourceName);
        }

        private string ResolveSourceNameFromDatabase(string sourceName)
        {
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return null;
            }

            var targetName = sourceName.Trim();

            try
            {
                var availableNames = _api.Database?.Sources?
                    .Where(source => source != null && !string.IsNullOrWhiteSpace(source.Name))
                    .Select(source => source.Name.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList() ?? new List<string>();

                return availableNames.FirstOrDefault(name => string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[LocalSuccessStoryImport] Failed resolving source name '{targetName}'.");
                return null;
            }
        }

        private void ApplyMetadataToNewGame(Game game, JObject root)
        {
            if (game == null || root == null)
            {
                return;
            }

            if (!TryExtractAppIdFromSourceUrl(root, out var appId) || appId <= 0)
            {
                return;
            }

            try
            {
                _metadataApplier.ApplyToGame(game, appId, _metadataSourceId, _metadataPlugin, fastMetadataDownloader: null);
                _api.Database.Games.Update(game);
                _logger?.Debug($"[LocalSuccessStoryImport] Applied metadata for '{game.Name}' (appId={appId}).");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[LocalSuccessStoryImport] Failed applying metadata for '{game.Name}' (appId={appId}).");
            }
        }

        private static string FirstNonEmptyRoot(JObject root, params string[] propertyNames)
        {
            if (root == null || propertyNames == null)
            {
                return null;
            }

            foreach (var propertyName in propertyNames)
            {
                var value = root.Value<string>(propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private bool TryResolveGame(string filePath, JObject root, GameResolutionIndex index, out Game game)
        {
            game = null;

            var fileName = Path.GetFileNameWithoutExtension(filePath)?.Trim();
            if (TryResolveGameByToken(fileName, index, out game))
            {
                return true;
            }

            foreach (var property in GuidIdPropertyCandidates)
            {
                if (TryResolveGameByToken(root.Value<string>(property), index, out game))
                {
                    return true;
                }
            }

            foreach (var property in NumericIdPropertyCandidates)
            {
                if (TryResolveGameByToken(root.Value<string>(property), index, out game))
                {
                    return true;
                }
            }

            if (root["Game"] is JObject gameObject)
            {
                foreach (var property in GuidIdPropertyCandidates)
                {
                    if (TryResolveGameByToken(gameObject.Value<string>(property), index, out game))
                    {
                        return true;
                    }
                }

                foreach (var property in NumericIdPropertyCandidates)
                {
                    if (TryResolveGameByToken(gameObject.Value<string>(property), index, out game))
                    {
                        return true;
                    }
                }

                foreach (var property in NamePropertyCandidates)
                {
                    if (TryResolveGameByName(gameObject.Value<string>(property), index, out game))
                    {
                        return true;
                    }
                }
            }

            foreach (var property in NamePropertyCandidates)
            {
                if (TryResolveGameByName(root.Value<string>(property), index, out game))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryResolveGameByToken(string rawToken, GameResolutionIndex index, out Game game)
        {
            game = null;
            var token = (rawToken ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (Guid.TryParse(token, out var gameId) && index.ByGuid.TryGetValue(gameId, out game))
            {
                return true;
            }

            if (index.ByDatabaseId.TryGetValue(token, out game))
            {
                return true;
            }

            return false;
        }

        private bool TryResolveGameByName(string rawName, GameResolutionIndex index, out Game game)
        {
            game = null;
            var normalized = NormalizeKey(rawName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            return index.ByNameNormalized.TryGetValue(normalized, out game);
        }

        private List<LegacyUnlockState> ExtractUnlockStates(JObject root)
        {
            return ExtractAchievementStates(root)
                .Where(state => state?.Unlocked == true)
                .ToList();
        }

        private List<LegacyUnlockState> ExtractAchievementStates(JObject root)
        {
            var result = new List<LegacyUnlockState>();

            var itemArray = root["Items"] as JArray ?? root["Achievements"] as JArray;
            if (itemArray == null)
            {
                return result;
            }

            foreach (var token in itemArray)
            {
                if (!(token is JObject item))
                {
                    continue;
                }

                var apiName = FirstNonEmpty(item, "ApiName", "apiName", "Id", "Key", "Name");
                var displayName = FirstNonEmpty(item, "DisplayName", "Name", "Title");
                var unlockTimeUtc = ParseUnlockTimeUtc(item);
                var unlocked = ParseUnlocked(item, unlockTimeUtc);

                if (string.IsNullOrWhiteSpace(apiName) && string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                result.Add(new LegacyUnlockState
                {
                    ApiName = apiName?.Trim(),
                    DisplayName = displayName?.Trim(),
                    Description = FirstNonEmpty(item, "Description", "description"),
                    UnlockedIconPath = FirstNonEmpty(item, "UrlUnlocked", "UnlockedIconPath", "ImageUnlocked"),
                    LockedIconPath = FirstNonEmpty(item, "UrlLocked", "LockedIconPath", "ImageLocked"),
                    Hidden = item.Value<bool?>("IsHidden") == true,
                    Percent = item.Value<double?>("Percent"),
                    Points = ConvertPoints(item["GamerScore"]),
                    Unlocked = true,
                    UnlockTimeUtc = unlockTimeUtc
                });
                result[result.Count - 1].Unlocked = unlocked;
            }

            return result;
        }

        private static string FirstNonEmpty(JObject item, params string[] propertyNames)
        {
            if (item == null || propertyNames == null)
            {
                return null;
            }

            foreach (var propertyName in propertyNames)
            {
                var value = item.Value<string>(propertyName);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return null;
        }

        private static bool ParseUnlocked(JObject item, DateTime? parsedUnlockTimeUtc)
        {
            foreach (var key in new[] { "IsUnlock", "IsUnlocked", "Unlocked", "Unlock", "State" })
            {
                var token = item[key];
                if (token == null)
                {
                    continue;
                }

                switch (token.Type)
                {
                    case JTokenType.Boolean:
                        return token.Value<bool>();
                    case JTokenType.Integer:
                    case JTokenType.Float:
                        return token.Value<double>() > 0;
                    case JTokenType.String:
                        var text = token.Value<string>()?.Trim();
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            continue;
                        }

                        if (bool.TryParse(text, out var parsedBool))
                        {
                            return parsedBool;
                        }

                        if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNumber))
                        {
                            return parsedNumber > 0;
                        }

                        return string.Equals(text, "unlocked", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (parsedUnlockTimeUtc.HasValue)
            {
                return true;
            }

            var dateToken = FirstNonEmpty(item, "DateUnlocked", "UnlockTimeUtc", "UnlockDate", "Date");
            return !string.IsNullOrWhiteSpace(dateToken);
        }

        private static DateTime? ParseUnlockTimeUtc(JObject item)
        {
            var value = FirstNonEmpty(item, "DateUnlocked", "UnlockTimeUtc", "UnlockDate", "Date");
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                var utc = dto.UtcDateTime;
                if (utc.Year < 1990)
                {
                    return null;
                }

                return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }

            return null;
        }

        private static int? ConvertPoints(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (token.Type == JTokenType.Float)
            {
                return (int)Math.Round(token.Value<double>(), MidpointRounding.AwayFromZero);
            }

            if (token.Type == JTokenType.String)
            {
                var text = token.Value<string>();
                if (double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
                }
            }

            return null;
        }

        private static int EnsureAchievementsFromSuccessStory(
            GameAchievementData cachedData,
            IReadOnlyCollection<LegacyUnlockState> sourceAchievements)
        {
            if (cachedData == null || sourceAchievements == null || sourceAchievements.Count == 0)
            {
                return 0;
            }

            cachedData.Achievements ??= new List<AchievementDetail>();
            var byApiName = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToDictionary(a => a.ApiName.Trim(), a => a, StringComparer.OrdinalIgnoreCase);
            var byApiNameNormalized = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .GroupBy(a => NormalizeKey(a.ApiName))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var byDisplayNameNormalized = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.DisplayName))
                .GroupBy(a => NormalizeKey(a.DisplayName))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var source in sourceAchievements)
            {
                if (source == null || (string.IsNullOrWhiteSpace(source.ApiName) && string.IsNullOrWhiteSpace(source.DisplayName)))
                {
                    continue;
                }

                var existing = ResolveAchievement(source, byApiName, byApiNameNormalized, byDisplayNameNormalized);
                if (existing != null)
                {
                    continue;
                }

                var apiName = string.IsNullOrWhiteSpace(source.ApiName)
                    ? source.DisplayName?.Trim()
                    : source.ApiName.Trim();

                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var detail = new AchievementDetail
                {
                    ApiName = apiName,
                    DisplayName = string.IsNullOrWhiteSpace(source.DisplayName) ? apiName : source.DisplayName,
                    Description = source.Description,
                    UnlockedIconPath = source.UnlockedIconPath,
                    LockedIconPath = source.LockedIconPath,
                    Hidden = source.Hidden,
                    Points = source.Points,
                    GlobalPercentUnlocked = source.Percent,
                    Unlocked = source.Unlocked,
                    UnlockTimeUtc = source.UnlockTimeUtc,
                    DisplayOrder = cachedData.Achievements.Count
                };

                cachedData.Achievements.Add(detail);
                byApiName[apiName] = detail;
                var normalizedApi = NormalizeKey(apiName);
                if (!string.IsNullOrWhiteSpace(normalizedApi) && !byApiNameNormalized.ContainsKey(normalizedApi))
                {
                    byApiNameNormalized[normalizedApi] = detail;
                }

                var normalizedDisplay = NormalizeKey(detail.DisplayName);
                if (!string.IsNullOrWhiteSpace(normalizedDisplay) && !byDisplayNameNormalized.ContainsKey(normalizedDisplay))
                {
                    byDisplayNameNormalized[normalizedDisplay] = detail;
                }

                added++;
            }

            return added;
        }

        private static void ReplaceAchievementsFromSuccessStory(
            GameAchievementData cachedData,
            IReadOnlyCollection<LegacyUnlockState> sourceAchievements)
        {
            if (cachedData == null)
            {
                return;
            }

            var replacement = new GameAchievementData
            {
                Achievements = new List<AchievementDetail>()
            };

            EnsureAchievementsFromSuccessStory(replacement, sourceAchievements);
            cachedData.Achievements = replacement.Achievements ?? new List<AchievementDetail>();
            cachedData.HasAchievements = cachedData.Achievements.Count > 0;
        }

        private static GameAchievementData BuildGameDataFromSuccessStory(
            Game matchedGame,
            JObject root,
            IReadOnlyCollection<LegacyUnlockState> sourceAchievements)
        {
            var data = new GameAchievementData
            {
                PlayniteGameId = matchedGame?.Id,
                GameName = matchedGame?.Name,
                LibrarySourceName = matchedGame?.Source?.Name,
                ProviderKey = ResolveProviderKey(root, matchedGame),
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = sourceAchievements != null && sourceAchievements.Count > 0,
                Achievements = new List<AchievementDetail>()
            };

            if (TryExtractAppIdFromSourceUrl(root, out var appId) && appId > 0)
            {
                data.AppId = appId;
            }

            EnsureAchievementsFromSuccessStory(data, sourceAchievements);
            return data;
        }

        private static string ResolveProviderKey(JObject root, Game matchedGame)
        {
            var sourceName = root?["SourcesLink"]?["Name"]?.Value<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                if (sourceName.Equals("Steam", StringComparison.OrdinalIgnoreCase)) return "Steam";
                if (sourceName.Equals("GOG", StringComparison.OrdinalIgnoreCase)) return "GOG";
                if (sourceName.Equals("Epic", StringComparison.OrdinalIgnoreCase)) return "Epic";
                if (sourceName.Equals("Local", StringComparison.OrdinalIgnoreCase)) return "Local";
                return sourceName;
            }

            return matchedGame?.PluginId == Guid.Empty ? "Manual" : "Manual";
        }

        private static bool TryExtractAppIdFromSourceUrl(JObject root, out int appId)
        {
            appId = 0;
            var sourceUrl = root?["SourcesLink"]?["Url"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                return false;
            }

            var match = Regex.Match(sourceUrl, @"/stats/(?<id>\d+)", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            return int.TryParse(match.Groups["id"]?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out appId) && appId > 0;
        }

        private static bool ApplyUnlockStatesToCachedData(
            GameAchievementData cachedData,
            IReadOnlyCollection<LegacyUnlockState> unlockStates,
            out int matchedRows,
            out int updatedRows)
        {
            matchedRows = 0;
            updatedRows = 0;
            if (cachedData?.Achievements == null || cachedData.Achievements.Count == 0 || unlockStates == null || unlockStates.Count == 0)
            {
                return false;
            }

            var byApiName = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToDictionary(a => a.ApiName.Trim(), a => a, StringComparer.OrdinalIgnoreCase);

            var byApiNameNormalized = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .GroupBy(a => NormalizeKey(a.ApiName))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var byDisplayNameNormalized = cachedData.Achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.DisplayName))
                .GroupBy(a => NormalizeKey(a.DisplayName))
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var changed = false;

            foreach (var state in unlockStates)
            {
                if (state == null || !state.Unlocked)
                {
                    continue;
                }

                var target = ResolveAchievement(state, byApiName, byApiNameNormalized, byDisplayNameNormalized);
                if (target == null)
                {
                    continue;
                }

                matchedRows++;

                var rowChanged = false;
                if (!target.Unlocked)
                {
                    target.Unlocked = true;
                    rowChanged = true;
                }

                if (state.UnlockTimeUtc.HasValue && !target.UnlockTimeUtc.HasValue)
                {
                    target.UnlockTimeUtc = state.UnlockTimeUtc;
                    rowChanged = true;
                }

                if (rowChanged)
                {
                    updatedRows++;
                    changed = true;
                }
            }

            return changed;
        }

        private static AchievementDetail ResolveAchievement(
            LegacyUnlockState state,
            IReadOnlyDictionary<string, AchievementDetail> byApiName,
            IReadOnlyDictionary<string, AchievementDetail> byApiNameNormalized,
            IReadOnlyDictionary<string, AchievementDetail> byDisplayNameNormalized)
        {
            if (!string.IsNullOrWhiteSpace(state.ApiName))
            {
                var apiName = state.ApiName.Trim();
                if (byApiName.TryGetValue(apiName, out var byExactApi))
                {
                    return byExactApi;
                }

                var normalizedApi = NormalizeKey(apiName);
                if (!string.IsNullOrWhiteSpace(normalizedApi) && byApiNameNormalized.TryGetValue(normalizedApi, out var byNormalizedApi))
                {
                    return byNormalizedApi;
                }
            }

            var normalizedDisplay = NormalizeKey(state.DisplayName);
            if (!string.IsNullOrWhiteSpace(normalizedDisplay) && byDisplayNameNormalized.TryGetValue(normalizedDisplay, out var byDisplay))
            {
                return byDisplay;
            }

            return null;
        }

        private static string NormalizeKey(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return Regex.Replace(value.Trim(), "[^a-zA-Z0-9]", string.Empty).ToLowerInvariant();
        }

        private static string TryGetGameDatabaseId(Game game)
        {
            if (game == null)
            {
                return null;
            }

            try
            {
                var property = game.GetType().GetProperty("DatabaseId");
                var value = property?.GetValue(game);
                return value?.ToString()?.Trim();
            }
            catch
            {
                return null;
            }
        }

        private static void ReportProgress(
            IProgress<SuccessStoryImportProgressInfo> progress,
            double percent,
            string message,
            string detail)
        {
            progress?.Report(new SuccessStoryImportProgressInfo
            {
                Percent = percent,
                Message = message ?? string.Empty,
                Detail = detail ?? string.Empty
            });
        }
    }
}
