using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Services
{
    internal sealed class LegacyManualImportResult
    {
        public int Scanned { get; internal set; }
        public int Imported { get; internal set; }
        public int ParseFailures { get; internal set; }
        public int SkippedNotManual { get; internal set; }
        public int SkippedIgnored { get; internal set; }
        public int SkippedInvalidFileName { get; internal set; }
        public int SkippedGameMissing { get; internal set; }
        public int SkippedManualLinkExists { get; internal set; }
        public int SkippedCachedProviderData { get; internal set; }
        public int SkippedUnsupportedSource { get; internal set; }
        public int SkippedUnresolvedSourceGameId { get; internal set; }
        public bool ManualProviderAutoEnabled { get; internal set; }
        public List<Guid> ImportedGameIds { get; } = new List<Guid>();
        public Dictionary<string, int> UnsupportedSources { get; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class LegacyManualLinkImporter
    {
        private static readonly DateTime MinimumLegacyUnlockUtc =
            new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private static readonly DateTime LegacyUnknownUnlockSentinelUtc =
            new DateTimeOffset(1982, 12, 15, 0, 0, 0, TimeSpan.FromHours(-5)).UtcDateTime;

        private static readonly Regex SteamStatsAppIdRegex = new Regex(
            @"/stats/(?<id>\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex SteamAppsAppIdRegex = new Regex(
            @"/apps/(?<id>\d+)(?:/|$)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly Func<PersistedSettings> _getPersistedSettings;
        private readonly Func<Guid, bool> _gameExists;
        private readonly Func<Guid, bool> _hasCachedProviderData;
        private readonly ILogger _logger;
        private readonly IReadOnlyDictionary<string, string> _sourceResolver;

        public LegacyManualLinkImporter(
            PersistedSettings persistedSettings,
            Func<Guid, bool> gameExists,
            Func<Guid, bool> hasCachedProviderData,
            ILogger logger = null,
            IReadOnlyDictionary<string, string> sourceResolver = null)
            : this(
                  () => persistedSettings,
                  gameExists,
                  hasCachedProviderData,
                  logger,
                  sourceResolver)
        {
        }

        public LegacyManualLinkImporter(
            Func<PersistedSettings> getPersistedSettings,
            Func<Guid, bool> gameExists,
            Func<Guid, bool> hasCachedProviderData,
            ILogger logger = null,
            IReadOnlyDictionary<string, string> sourceResolver = null)
        {
            _getPersistedSettings = getPersistedSettings ?? throw new ArgumentNullException(nameof(getPersistedSettings));
            _gameExists = gameExists ?? throw new ArgumentNullException(nameof(gameExists));
            _hasCachedProviderData = hasCachedProviderData ?? throw new ArgumentNullException(nameof(hasCachedProviderData));
            _logger = logger;
            _sourceResolver = sourceResolver ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Steam"] = "Steam",
                ["Exophase"] = "Exophase"
            };
        }

        public LegacyManualImportResult Import(string folderPath)
        {
            var result = new LegacyManualImportResult();

            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return result;
            }

            var persistedSettings = _getPersistedSettings();
            if (persistedSettings == null)
            {
                return result;
            }

            var manualLinks = persistedSettings.ManualAchievementLinks ?? new Dictionary<Guid, ManualAchievementLink>();
            persistedSettings.ManualAchievementLinks = manualLinks;

            var jsonFiles = Directory
                .EnumerateFiles(folderPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var jsonFile in jsonFiles)
            {
                result.Scanned++;

                try
                {
                    if (!Guid.TryParse(Path.GetFileNameWithoutExtension(jsonFile), out var gameId))
                    {
                        result.SkippedInvalidFileName++;
                        continue;
                    }

                    var payload = ParsePayload(jsonFile);
                    if (payload == null)
                    {
                        result.ParseFailures++;
                        continue;
                    }

                    if (!payload.IsManual)
                    {
                        result.SkippedNotManual++;
                        continue;
                    }

                    if (payload.IsIgnored)
                    {
                        result.SkippedIgnored++;
                        continue;
                    }

                    if (!_gameExists(gameId))
                    {
                        result.SkippedGameMissing++;
                        continue;
                    }

                    if (manualLinks.TryGetValue(gameId, out var existingLink))
                    {
                        if (IsValidManualLink(existingLink))
                        {
                            result.SkippedManualLinkExists++;
                            continue;
                        }

                        // Clean stale/invalid entries so import can recreate a usable link.
                        manualLinks.Remove(gameId);
                    }

                    if (_hasCachedProviderData(gameId))
                    {
                        result.SkippedCachedProviderData++;
                        continue;
                    }

                    if (!TryResolveSourceKey(payload.SourceName, out var sourceKey))
                    {
                        result.SkippedUnsupportedSource++;
                        IncrementUnsupportedSourceCount(result, payload.SourceName);
                        continue;
                    }

                    if (!TryResolveSourceGameId(payload.SourceUrl, payload.Items, out var sourceGameId))
                    {
                        result.SkippedUnresolvedSourceGameId++;
                        continue;
                    }

                    var nowUtc = DateTime.UtcNow;
                    BuildUnlockData(payload.Items, out var unlockTimes, out var unlockStates);
                    manualLinks[gameId] = new ManualAchievementLink
                    {
                        SourceKey = sourceKey,
                        SourceGameId = sourceGameId,
                        UnlockTimes = unlockTimes,
                        UnlockStates = unlockStates,
                        CreatedUtc = nowUtc,
                        LastModifiedUtc = nowUtc
                    };

                    result.Imported++;
                    result.ImportedGameIds.Add(gameId);
                }
                catch (Exception ex)
                {
                    result.ParseFailures++;
                    _logger?.Warn(ex, $"Legacy manual import failed for '{jsonFile}'.");
                }
            }

            return result;
        }

        private bool TryResolveSourceKey(string sourceName, out string sourceKey)
        {
            sourceKey = null;
            if (string.IsNullOrWhiteSpace(sourceName))
            {
                return false;
            }

            return _sourceResolver.TryGetValue(sourceName.Trim(), out sourceKey) &&
                   !string.IsNullOrWhiteSpace(sourceKey);
        }

        private static bool IsValidManualLink(ManualAchievementLink link)
        {
            return link != null &&
                   !string.IsNullOrWhiteSpace(link.SourceKey) &&
                   !string.IsNullOrWhiteSpace(link.SourceGameId);
        }

        private static void IncrementUnsupportedSourceCount(
            LegacyManualImportResult result,
            string sourceName)
        {
            var key = string.IsNullOrWhiteSpace(sourceName) ? "<empty>" : sourceName.Trim();
            if (!result.UnsupportedSources.TryGetValue(key, out var count))
            {
                count = 0;
            }

            result.UnsupportedSources[key] = count + 1;
        }

        private static void BuildUnlockData(
            IReadOnlyList<LegacyAchievementItem> items,
            out Dictionary<string, DateTime?> unlockTimes,
            out Dictionary<string, bool> unlockStates)
        {
            unlockTimes = new Dictionary<string, DateTime?>();
            unlockStates = new Dictionary<string, bool>();
            if (items == null)
            {
                return;
            }

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.ApiName))
                {
                    continue;
                }

                if (TryParseLegacyUnlock(item.DateUnlocked, out var unlockUtc))
                {
                    var apiName = item.ApiName.Trim();
                    unlockStates[apiName] = true;
                    if (unlockUtc.HasValue)
                    {
                        unlockTimes[apiName] = unlockUtc;
                    }
                }
            }
        }

        private static bool TryResolveSourceGameId(
            string sourceUrl,
            IReadOnlyList<LegacyAchievementItem> items,
            out string sourceGameId)
        {
            sourceGameId = null;

            // Try Steam stats URL pattern first
            if (TryExtractAppIdFromStatsUrl(sourceUrl, out sourceGameId))
            {
                return true;
            }

            // Try Exophase achievement page URL
            if (TryExtractExophaseUrl(sourceUrl, out sourceGameId))
            {
                return true;
            }

            if (items == null)
            {
                return false;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                if (TryExtractAppIdFromIconUrl(item.UrlUnlocked, out sourceGameId))
                {
                    return true;
                }

                if (TryExtractAppIdFromIconUrl(item.UrlLocked, out sourceGameId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryExtractAppIdFromStatsUrl(string url, out string appId)
        {
            appId = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var match = SteamStatsAppIdRegex.Match(url);
            if (!match.Success)
            {
                return false;
            }

            appId = match.Groups["id"]?.Value;
            return !string.IsNullOrWhiteSpace(appId);
        }

        private static bool TryExtractAppIdFromIconUrl(string url, out string appId)
        {
            appId = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var match = SteamAppsAppIdRegex.Match(url);
            if (!match.Success)
            {
                return false;
            }

            appId = match.Groups["id"]?.Value;
            return !string.IsNullOrWhiteSpace(appId);
        }

        private static bool TryExtractExophaseUrl(string url, out string exophaseId)
        {
            exophaseId = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            // Exophase achievement URLs follow patterns like:
            // https://www.exophase.com/game/<game-slug>/achievements
            // Extract just the slug for storage (more stable than full URL)
            if (url.IndexOf("exophase.com/game/", StringComparison.OrdinalIgnoreCase) >= 0 &&
                url.IndexOf("/achievements", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Extract the slug from the URL
                var match = Regex.Match(url, @"/game/([^/]+)/achievements", RegexOptions.IgnoreCase);
                if (match.Success && match.Groups.Count > 1)
                {
                    exophaseId = match.Groups[1].Value;
                    return true;
                }

                // Fallback: store full URL if we can't extract slug (for edge cases)
                exophaseId = url.Trim();
                return true;
            }

            return false;
        }

        private static bool TryParseLegacyUnlock(string value, out DateTime? utc)
        {
            utc = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var dto))
            {
                var parsedUtc = dto.UtcDateTime;
                if (parsedUtc == LegacyUnknownUnlockSentinelUtc)
                {
                    return true;
                }

                if (parsedUtc < MinimumLegacyUnlockUtc)
                {
                    return false;
                }

                utc = parsedUtc;
                return true;
            }

            return false;
        }

        private static LegacyPayload ParsePayload(string filePath)
        {
            var json = File.ReadAllText(filePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            JObject root;
            using (var stringReader = new StringReader(json))
            using (var jsonReader = new JsonTextReader(stringReader) { DateParseHandling = DateParseHandling.None })
            {
                root = JObject.Load(jsonReader);
            }
            var sourcesLink = root["SourcesLink"] as JObject;
            var itemArray = root["Items"] as JArray;

            var items = new List<LegacyAchievementItem>();
            if (itemArray != null)
            {
                foreach (var token in itemArray)
                {
                    if (!(token is JObject itemObject))
                    {
                        continue;
                    }

                    items.Add(new LegacyAchievementItem
                    {
                        ApiName = itemObject.Value<string>("ApiName"),
                        DateUnlocked = itemObject.Value<string>("DateUnlocked"),
                        UrlUnlocked = itemObject.Value<string>("UrlUnlocked"),
                        UrlLocked = itemObject.Value<string>("UrlLocked")
                    });
                }
            }

            return new LegacyPayload
            {
                IsManual = root.Value<bool?>("IsManual") == true,
                IsIgnored = root.Value<bool?>("IsIgnored") == true,
                SourceName = sourcesLink?.Value<string>("Name"),
                SourceUrl = sourcesLink?.Value<string>("Url"),
                Items = items
            };
        }

        private sealed class LegacyPayload
        {
            public bool IsManual { get; set; }
            public bool IsIgnored { get; set; }
            public string SourceName { get; set; }
            public string SourceUrl { get; set; }
            public List<LegacyAchievementItem> Items { get; set; }
        }

        private sealed class LegacyAchievementItem
        {
            public string ApiName { get; set; }
            public string DateUnlocked { get; set; }
            public string UrlUnlocked { get; set; }
            public string UrlLocked { get; set; }
        }
    }
}
