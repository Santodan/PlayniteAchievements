using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal interface IHoyoverseDefinitionClient
    {
        Task<IReadOnlyList<AchievementDetail>> GetDefinitionsAsync(
            HoyoverseGameKind kind,
            string globalLanguage,
            CancellationToken cancel);
    }

    internal sealed class HoyoverseDefinitionClient : IHoyoverseDefinitionClient, IDisposable
    {
        private static readonly TimeSpan DefinitionTtl = TimeSpan.FromHours(24);
        private static readonly Regex IndexAssetPathRegex = new Regex(@"/assets/index-[^""']+\.js", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HtmlTagRegex = new Regex(@"<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex MarkupTokenRegex = new Regex(@"\{[A-Z]+#([^}]*)\}", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRunRegex = new Regex(@"\s+", RegexOptions.Compiled);
        private static readonly Regex ExponentNumberRegex = new Regex(@"\b(\d+)e(\d+)\b", RegexOptions.Compiled);
        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private readonly ILogger _logger;
        private readonly string _cacheRoot;

        private sealed class HsrSeriesInfo
        {
            public string Title { get; set; }

            public string IconPath { get; set; }
        }

        public HoyoverseDefinitionClient(HttpClient httpClient, ILogger logger, string pluginUserDataPath)
        {
            _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _ownsHttpClient = httpClient == null;
            _logger = logger;
            _cacheRoot = Path.Combine(pluginUserDataPath ?? string.Empty, "hoyoverse", "definitions");
        }

        public async Task<IReadOnlyList<AchievementDetail>> GetDefinitionsAsync(
            HoyoverseGameKind kind,
            string globalLanguage,
            CancellationToken cancel)
        {
            try
            {
                switch (kind)
                {
                    case HoyoverseGameKind.GenshinImpact:
                        return await GetGenshinDefinitionsAsync(globalLanguage, cancel).ConfigureAwait(false);
                    case HoyoverseGameKind.HonkaiStarRail:
                        return await GetHonkaiStarRailDefinitionsAsync(globalLanguage, cancel).ConfigureAwait(false);
                    case HoyoverseGameKind.ZenlessZoneZero:
                        return await GetZenlessZoneZeroDefinitionsAsync(globalLanguage, cancel).ConfigureAwait(false);
                    default:
                        return Array.Empty<AchievementDetail>();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[HoYoverse] Failed to load achievement definitions for {kind}.");
                return Array.Empty<AchievementDetail>();
            }
        }

        private async Task<IReadOnlyList<AchievementDetail>> GetGenshinDefinitionsAsync(
            string globalLanguage,
            CancellationToken cancel)
        {
            var language = MapGlobalLanguageToPaimonLocale(globalLanguage);
            var url = $"https://raw.githubusercontent.com/MadeBaruna/paimon-moe/main/src/data/achievement/{language}.json";
            var json = await GetStringWithCacheAsync($"genshin-{language}.json", url, cancel).ConfigureAwait(false);
            return ParseGenshinDefinitions(json);
        }

        private async Task<IReadOnlyList<AchievementDetail>> GetHonkaiStarRailDefinitionsAsync(
            string globalLanguage,
            CancellationToken cancel)
        {
            var textMapLanguage = MapGlobalLanguageToStarRailTextMap(globalLanguage);
            var textMapUrl = $"https://raw.githubusercontent.com/DimbreathBot/TurnBasedGameData/refs/heads/main/TextMap/TextMap{textMapLanguage}.json";
            var achievementUrl = "https://raw.githubusercontent.com/DimbreathBot/TurnBasedGameData/refs/heads/main/ExcelOutput/AchievementData.json";
            var seriesUrl = "https://raw.githubusercontent.com/DimbreathBot/TurnBasedGameData/refs/heads/main/ExcelOutput/AchievementSeries.json";

            var achievements = await GetStringWithCacheAsync("hsr-achievement-data.json", achievementUrl, cancel).ConfigureAwait(false);
            var series = await GetStringWithCacheAsync("hsr-achievement-series.json", seriesUrl, cancel).ConfigureAwait(false);
            var textMap = await GetStringWithCacheAsync($"hsr-textmap-{textMapLanguage}.json", textMapUrl, cancel).ConfigureAwait(false);

            string englishTextMap = null;
            if (!string.Equals(textMapLanguage, "EN", StringComparison.OrdinalIgnoreCase))
            {
                var englishUrl = "https://raw.githubusercontent.com/DimbreathBot/TurnBasedGameData/refs/heads/main/TextMap/TextMapEN.json";
                englishTextMap = await GetStringWithCacheAsync("hsr-textmap-EN.json", englishUrl, cancel).ConfigureAwait(false);
            }

            return ParseHonkaiStarRailDefinitions(achievements, series, textMap, englishTextMap);
        }

        private async Task<IReadOnlyList<AchievementDetail>> GetZenlessZoneZeroDefinitionsAsync(
            string globalLanguage,
            CancellationToken cancel)
        {
            var locale = MapGlobalLanguageToZzzLocale(globalLanguage);
            var html = await GetStringWithCacheAsync("zzz-index.html", "https://zzz.seelie.me/", cancel).ConfigureAwait(false);
            var indexAsset = IndexAssetPathRegex.Match(html ?? string.Empty).Value;
            if (string.IsNullOrWhiteSpace(indexAsset))
            {
                throw new InvalidDataException("Could not find zzz.seelie.me index asset.");
            }

            var indexUrl = "https://zzz.seelie.me" + indexAsset;
            var indexJs = await GetStringWithCacheAsync("zzz-index.js", indexUrl, cancel).ConfigureAwait(false);
            var localeAsset = FindZzzAchievementAsset(indexJs, locale) ?? FindZzzAchievementAsset(indexJs, "en");
            if (string.IsNullOrWhiteSpace(localeAsset))
            {
                throw new InvalidDataException("Could not find zzz.seelie.me achievement locale asset.");
            }

            var url = "https://zzz.seelie.me" + localeAsset;
            var js = await GetStringWithCacheAsync($"zzz-achievements-{locale}.js", url, cancel).ConfigureAwait(false);
            return ParseZenlessZoneZeroDefinitions(js);
        }

        private async Task<string> GetStringWithCacheAsync(string cacheFileName, string url, CancellationToken cancel)
        {
            Directory.CreateDirectory(_cacheRoot);
            var cachePath = Path.Combine(_cacheRoot, SanitizeFileName(cacheFileName));

            if (File.Exists(cachePath))
            {
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath);
                if (age <= DefinitionTtl)
                {
                    return File.ReadAllText(cachePath);
                }
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", "PlayniteAchievements/HoYoverse");
                    using (var response = await _httpClient.SendAsync(request, cancel).ConfigureAwait(false))
                    {
                        response.EnsureSuccessStatusCode();
                        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        File.WriteAllText(cachePath, content);
                        return content;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (File.Exists(cachePath))
                {
                    _logger?.Warn(ex, $"[HoYoverse] Using stale definition cache for '{cacheFileName}'.");
                    return File.ReadAllText(cachePath);
                }

                throw;
            }
        }

        internal static List<AchievementDetail> ParseGenshinDefinitions(string json)
        {
            var root = ParseJsonToken(json);
            var achievements = new List<AchievementDetail>();
            if (root == null)
            {
                return achievements;
            }

            foreach (var category in EnumerateCategoryObjects(root))
            {
                var categoryName = ReadString(category.Object["name"]) ??
                                   ReadString(category.Object["title"]) ??
                                   category.Name;
                var token = category.Object["achievements"] ??
                            category.Object["items"] ??
                            category.Object["data"] ??
                            category.Object;

                foreach (var obj in FindAchievementObjects(token, "id", "name"))
                {
                    var id = ReadString(obj["id"]);
                    var name = ReadString(obj["name"]);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var points = ReadInt(obj["reward"]) ?? ReadInt(obj["points"]) ?? 5;
                    var iconPath = ReadString(obj["icon"]) ??
                                   ReadString(obj["iconPath"]) ??
                                   GetGenshinCategoryIconFileName(category.Name, category.Object);
                    achievements.Add(CreateDefinition(
                        id,
                        name,
                        ReadString(obj["desc"]) ?? ReadString(obj["description"]),
                        categoryName,
                        points,
                        ResolveHoyoverseIconPath(
                            HoyoverseGameKind.GenshinImpact,
                            iconPath)));
                }
            }

            return DeduplicateDefinitions(achievements);
        }

        internal static List<AchievementDetail> ParseHonkaiStarRailDefinitions(
            string achievementDataJson,
            string achievementSeriesJson,
            string textMapJson,
            string englishTextMapJson = null)
        {
            var achievementRoot = ParseJsonToken(achievementDataJson);
            var seriesRoot = ParseJsonToken(achievementSeriesJson);
            var textMap = JObject.Parse(string.IsNullOrWhiteSpace(textMapJson) ? "{}" : textMapJson);
            var englishTextMap = string.IsNullOrWhiteSpace(englishTextMapJson)
                ? textMap
                : JObject.Parse(englishTextMapJson);
            var seriesById = BuildHsrSeriesMap(seriesRoot, textMap, englishTextMap);
            var definitions = new List<AchievementDetail>();

            foreach (var obj in EnumerateObjects(achievementRoot))
            {
                var id = ReadString(obj["AchievementID"]) ?? ReadString(obj["ID"]) ?? ReadString(obj["id"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var titleHash = obj["AchievementTitle"]?["Hash"] ?? obj["Title"]?["Hash"] ?? obj["Title"];
                var descHash = obj["AchievementDesc"]?["Hash"] ?? obj["Desc"]?["Hash"] ?? obj["Desc"];
                var name = ResolveText(textMap, englishTextMap, titleHash);
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var description = ResolveText(textMap, englishTextMap, descHash);
                description = ApplyHsrParameters(description, obj["ParamList"]);
                var seriesId = ReadString(obj["SeriesID"]) ?? ReadString(obj["Series"]);
                seriesById.TryGetValue(seriesId ?? string.Empty, out var seriesInfo);

                var points = ReadHsrPoints(obj["Rarity"]);
                definitions.Add(CreateDefinition(
                    id,
                    CleanText(name),
                    CleanText(description),
                    seriesInfo?.Title,
                    points,
                    ResolveHoyoverseIconPath(
                        HoyoverseGameKind.HonkaiStarRail,
                        ReadString(obj["IconPath"]) ?? seriesInfo?.IconPath)));
            }

            return DeduplicateDefinitions(definitions);
        }

        internal static List<AchievementDetail> ParseZenlessZoneZeroDefinitions(string jsOrJson)
        {
            var normalized = NormalizeZzzPayload(jsOrJson);
            var root = ParseJsonToken(normalized);
            var definitions = new List<AchievementDetail>();
            if (root == null)
            {
                return definitions;
            }

            foreach (var category in EnumerateCategoryObjects(root))
            {
                var categoryName = ReadString(category.Object["n"]) ??
                                   ReadString(category.Object["name"]) ??
                                   category.Name;
                var achievementToken = category.Object["a"] ??
                                       category.Object["achievements"] ??
                                       category.Object["items"] ??
                                       category.Object;

                foreach (var obj in FindAchievementObjects(achievementToken, "id", "n", "name"))
                {
                    var id = ReadString(obj["id"]);
                    var name = ReadString(obj["n"]) ?? ReadString(obj["name"]);
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var points = ReadInt(obj["r"]) ?? ReadInt(obj["reward"]) ?? 5;
                    definitions.Add(CreateDefinition(
                        id,
                        CleanText(name),
                        CleanText(ReadString(obj["d"]) ?? ReadString(obj["desc"]) ?? ReadString(obj["description"])),
                        CleanText(categoryName),
                        points,
                        ResolveHoyoverseIconPath(
                            HoyoverseGameKind.ZenlessZoneZero,
                            ReadString(obj["icon"]) ?? ReadString(obj["i"]))));
                }
            }

            return DeduplicateDefinitions(definitions);
        }

        internal static string MapGlobalLanguageToPaimonLocale(string globalLanguage)
        {
            switch (NormalizeLanguage(globalLanguage))
            {
                case "chinese":
                case "schinese":
                case "simplified chinese":
                    return "chs";
                case "tchinese":
                case "traditional chinese":
                    return "cht";
                case "japanese":
                    return "ja";
                case "koreana":
                case "korean":
                    return "ko";
                case "french":
                    return "fr";
                case "german":
                    return "de";
                case "spanish":
                case "latam":
                    return "es";
                case "russian":
                    return "ru";
                case "thai":
                    return "th";
                case "vietnamese":
                    return "vi";
                case "indonesian":
                    return "id";
                case "portuguese":
                case "brazilian":
                    return "pt";
                default:
                    return "en";
            }
        }

        internal static string MapGlobalLanguageToStarRailTextMap(string globalLanguage)
        {
            switch (NormalizeLanguage(globalLanguage))
            {
                case "chinese":
                case "schinese":
                case "simplified chinese":
                    return "CHS";
                case "tchinese":
                case "traditional chinese":
                    return "CHT";
                case "japanese":
                    return "JP";
                case "koreana":
                case "korean":
                    return "KR";
                case "french":
                    return "FR";
                case "german":
                    return "DE";
                case "spanish":
                case "latam":
                    return "ES";
                case "russian":
                    return "RU";
                case "thai":
                    return "TH";
                case "vietnamese":
                    return "VI";
                case "indonesian":
                    return "ID";
                case "portuguese":
                case "brazilian":
                    return "PT";
                default:
                    return "EN";
            }
        }

        internal static string MapGlobalLanguageToZzzLocale(string globalLanguage)
        {
            switch (NormalizeLanguage(globalLanguage))
            {
                case "chinese":
                case "schinese":
                case "simplified chinese":
                    return "zh";
                case "tchinese":
                case "traditional chinese":
                case "tw":
                case "zh-tw":
                    return "tw";
                case "japanese":
                    return "ja";
                case "koreana":
                case "korean":
                    return "ko";
                case "french":
                    return "fr";
                case "german":
                    return "de";
                case "spanish":
                case "latam":
                    return "es";
                case "russian":
                    return "ru";
                case "thai":
                    return "th";
                case "vietnamese":
                    return "vi";
                case "indonesian":
                    return "id";
                case "portuguese":
                case "brazilian":
                    return "pt";
                default:
                    return "en";
            }
        }

        internal static string FindZzzAchievementAsset(string indexJs, string locale)
        {
            var escapedLocale = Regex.Escape(locale ?? "en");
            var patterns = new[]
            {
                $@"/assets/locale/achievements-{escapedLocale}-[^""'\)\s,]+\.js",
                $@"/locale/achievements-{escapedLocale}-[^""'\)\s,]+\.js",
                $@"\./locale/achievements-{escapedLocale}-[^""'\)\s,]+\.js",
                $@"(?<![A-Za-z0-9_./-])locale/achievements-{escapedLocale}-[^""'\)\s,]+\.js",
                $@"(?<![A-Za-z0-9_./-])assets/locale/achievements-{escapedLocale}-[^""'\)\s,]+\.js"
            };

            foreach (var pattern in patterns)
            {
                var match = Regex.Match(indexJs ?? string.Empty, pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    return NormalizeZzzAchievementAsset(match.Value);
                }
            }

            return null;
        }

        private static string NormalizeZzzAchievementAsset(string asset)
        {
            var value = (asset ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (value.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            if (value.StartsWith("./", StringComparison.Ordinal))
            {
                value = value.Substring(2);
            }

            if (value.StartsWith("/locale/", StringComparison.OrdinalIgnoreCase))
            {
                return "/assets" + value;
            }

            if (value.StartsWith("locale/", StringComparison.OrdinalIgnoreCase))
            {
                return "/assets/" + value;
            }

            if (value.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            {
                return "/" + value;
            }

            return value.StartsWith("/", StringComparison.Ordinal)
                ? value
                : "/" + value;
        }

        private static IEnumerable<(string Name, JObject Object)> EnumerateCategoryObjects(JToken root)
        {
            if (root is JObject rootObject)
            {
                foreach (var property in rootObject.Properties())
                {
                    if (property.Value is JObject categoryObject)
                    {
                        yield return (property.Name, categoryObject);
                    }
                    else if (property.Value is JArray categoryArray)
                    {
                        foreach (var child in categoryArray.OfType<JObject>())
                        {
                            yield return (property.Name, child);
                        }
                    }
                }
            }
            else if (root is JArray rootArray)
            {
                foreach (var child in rootArray.OfType<JObject>())
                {
                    yield return (ReadString(child["name"]) ?? ReadString(child["n"]) ?? string.Empty, child);
                }
            }
        }

        private static Dictionary<string, HsrSeriesInfo> BuildHsrSeriesMap(JToken seriesRoot, JObject textMap, JObject englishTextMap)
        {
            var map = new Dictionary<string, HsrSeriesInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in EnumerateObjects(seriesRoot))
            {
                var id = ReadString(obj["SeriesID"]) ?? ReadString(obj["ID"]) ?? ReadString(obj["id"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var titleHash = obj["SeriesTitle"]?["Hash"] ??
                                obj["AchievementSeriesTitle"]?["Hash"] ??
                                obj["Title"]?["Hash"] ??
                                obj["Title"];
                var title = ResolveText(textMap, englishTextMap, titleHash);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    map[id] = new HsrSeriesInfo
                    {
                        Title = CleanText(title),
                        IconPath = ReadString(obj["MainIconPath"]) ??
                                   ReadString(obj["IconPath"]) ??
                                   ReadString(obj["Icon"])
                    };
                }
            }

            return map;
        }

        private static string ResolveHoyoverseIconPath(HoyoverseGameKind kind, string sourcePath)
        {
            var source = (sourcePath ?? string.Empty).Trim();
            if (IsHttpPath(source) || File.Exists(source))
            {
                return source;
            }

            var fileName = Path.GetFileName(source);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = GetDefaultIconFileName(kind);
            }

            var folderName = GetIconFolderName(kind);
            if (string.IsNullOrWhiteSpace(folderName) || string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            var baseDirectory = Path.GetDirectoryName(typeof(HoyoverseDefinitionClient).Assembly.Location) ??
                                AppDomain.CurrentDomain.BaseDirectory ??
                                string.Empty;
            var candidate = Path.Combine(baseDirectory, "Resources", "Hoyoverse", folderName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var defaultFileName = GetDefaultIconFileName(kind);
            if (!string.IsNullOrWhiteSpace(defaultFileName) &&
                !string.Equals(fileName, defaultFileName, StringComparison.OrdinalIgnoreCase))
            {
                var defaultCandidate = Path.Combine(baseDirectory, "Resources", "Hoyoverse", folderName, defaultFileName);
                if (File.Exists(defaultCandidate))
                {
                    return defaultCandidate;
                }
            }

            var fallback = Path.Combine(baseDirectory, "Resources", "UnlockedAchIcon.png");
            return File.Exists(fallback) ? fallback : null;
        }

        private static string GetGenshinCategoryIconFileName(string categoryKey, JObject categoryObject)
        {
            var order = ReadInt(categoryObject?["order"]);
            if (order.HasValue && order.Value > 0)
            {
                return (order.Value - 1).ToString(CultureInfo.InvariantCulture) + ".png";
            }

            if (int.TryParse(categoryKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index) &&
                index >= 0)
            {
                return index.ToString(CultureInfo.InvariantCulture) + ".png";
            }

            return GetDefaultIconFileName(HoyoverseGameKind.GenshinImpact);
        }

        private static bool IsHttpPath(string value)
        {
            return !string.IsNullOrWhiteSpace(value) &&
                   (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }

        private static string GetIconFolderName(HoyoverseGameKind kind)
        {
            switch (kind)
            {
                case HoyoverseGameKind.GenshinImpact:
                    return "GenshinImpact";
                case HoyoverseGameKind.HonkaiStarRail:
                    return "HonkaiStarRail";
                case HoyoverseGameKind.ZenlessZoneZero:
                    return "ZenlessZoneZero";
                default:
                    return null;
            }
        }

        private static string GetDefaultIconFileName(HoyoverseGameKind kind)
        {
            switch (kind)
            {
                case HoyoverseGameKind.HonkaiStarRail:
                    return "CultivateAchievementIcon.png";
                case HoyoverseGameKind.GenshinImpact:
                case HoyoverseGameKind.ZenlessZoneZero:
                    return "ac.png";
                default:
                    return null;
            }
        }

        private static IEnumerable<JObject> FindAchievementObjects(JToken token, params string[] requiredKeys)
        {
            if (token == null)
            {
                yield break;
            }

            if (token is JObject obj)
            {
                if (requiredKeys.Any(key => obj[key] != null) &&
                    (obj["id"] != null || obj["AchievementID"] != null || obj["ID"] != null))
                {
                    yield return obj;
                }

                foreach (var child in obj.Properties())
                {
                    foreach (var match in FindAchievementObjects(child.Value, requiredKeys))
                    {
                        yield return match;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array)
                {
                    foreach (var match in FindAchievementObjects(child, requiredKeys))
                    {
                        yield return match;
                    }
                }
            }
        }

        private static IEnumerable<JObject> EnumerateObjects(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            if (token is JObject obj)
            {
                yield return obj;
                foreach (var property in obj.Properties())
                {
                    foreach (var child in EnumerateObjects(property.Value))
                    {
                        yield return child;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array)
                {
                    foreach (var childObject in EnumerateObjects(child))
                    {
                        yield return childObject;
                    }
                }
            }
        }

        private static AchievementDetail CreateDefinition(
            string id,
            string name,
            string description,
            string category,
            int points,
            string iconPath)
        {
            var cleanName = CleanText(name);
            return new AchievementDetail
            {
                ApiName = id?.Trim(),
                DisplayName = string.IsNullOrWhiteSpace(cleanName) ? id?.Trim() : cleanName,
                Description = CleanText(description),
                Category = CleanText(category),
                Points = points,
                Rarity = GetRarityFromReward(points),
                UnlockedIconPath = iconPath,
                LockedIconPath = iconPath,
                GlobalPercentUnlocked = null,
                Hidden = false,
                Unlocked = false,
                UnlockTimeUtc = null
            };
        }

        private static List<AchievementDetail> DeduplicateDefinitions(IEnumerable<AchievementDetail> source)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AchievementDetail>();
            foreach (var detail in source ?? Enumerable.Empty<AchievementDetail>())
            {
                if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName) || !seen.Add(detail.ApiName))
                {
                    continue;
                }

                result.Add(detail);
            }

            return result;
        }

        private static RarityTier GetRarityFromReward(int points)
        {
            if (points >= 20)
            {
                return RarityTier.Rare;
            }

            if (points >= 10)
            {
                return RarityTier.Uncommon;
            }

            return RarityTier.Common;
        }

        private static int ReadHsrPoints(JToken rarityToken)
        {
            var rarity = ReadString(rarityToken);
            if (string.IsNullOrWhiteSpace(rarity))
            {
                return 5;
            }

            switch (rarity.Trim().ToLowerInvariant())
            {
                case "high":
                case "rare":
                case "20":
                    return 20;
                case "mid":
                case "medium":
                case "10":
                    return 10;
                default:
                    return 5;
            }
        }

        private static string ResolveText(JObject textMap, JObject englishTextMap, JToken hashToken)
        {
            var hash = ReadString(hashToken);
            if (string.IsNullOrWhiteSpace(hash))
            {
                return null;
            }

            var value = ReadString(textMap?[hash]);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return ReadString(englishTextMap?[hash]);
        }

        private static string ApplyHsrParameters(string text, JToken paramList)
        {
            if (string.IsNullOrWhiteSpace(text) || !(paramList is JArray parameters) || parameters.Count == 0)
            {
                return text;
            }

            var result = text;
            for (var i = 0; i < parameters.Count; i++)
            {
                var value = ReadString(parameters[i]?["Value"]) ??
                            ReadString(parameters[i]?["value"]) ??
                            ReadString(parameters[i]);
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var placeholder = "#" + (i + 1);
                result = Regex.Replace(result, Regex.Escape(placeholder) + @"(?:\[[^\]]+\])?", value);
            }

            return result;
        }

        private static string CleanText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var text = value.Replace("\\n", " ").Replace("\n", " ").Replace("\r", " ");
            text = HtmlTagRegex.Replace(text, string.Empty);
            text = MarkupTokenRegex.Replace(text, "$1");
            text = WhitespaceRunRegex.Replace(text, " ");
            return text.Trim();
        }

        private static string NormalizeZzzPayload(string jsOrJson)
        {
            var text = (jsOrJson ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return "{}";
            }

            var objectStart = text.IndexOf('{');
            var exportStart = text.IndexOf(";export", StringComparison.Ordinal);
            if (objectStart > 0)
            {
                text = text.Substring(objectStart);
            }

            if (exportStart > objectStart && exportStart >= 0)
            {
                var length = exportStart - objectStart;
                if (length > 0 && length <= text.Length)
                {
                    text = text.Substring(0, length);
                }
            }

            text = NormalizeJsStringLiterals(text);
            text = QuoteUnquotedObjectKeys(text);
            text = ExponentNumberRegex.Replace(text, match =>
            {
                if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                {
                    return match.Value;
                }

                return ((long)number).ToString(CultureInfo.InvariantCulture);
            });

            return text;
        }

        private static string NormalizeJsStringLiterals(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var quote = text[i];
                if (quote != '"' && quote != '\'' && quote != '`')
                {
                    result.Append(quote);
                    continue;
                }

                var value = new StringBuilder();
                for (i++; i < text.Length; i++)
                {
                    var current = text[i];
                    if (current == '\\')
                    {
                        if (i + 1 >= text.Length)
                        {
                            value.Append(current);
                            continue;
                        }

                        var escaped = text[++i];
                        AppendJsEscapedCharacter(value, escaped, text, ref i);
                        continue;
                    }

                    if (current == quote)
                    {
                        break;
                    }

                    value.Append(current);
                }

                result.Append(JsonConvert.ToString(value.ToString()));
            }

            return result.ToString();
        }

        private static void AppendJsEscapedCharacter(StringBuilder value, char escaped, string text, ref int index)
        {
            switch (escaped)
            {
                case 'b':
                    value.Append('\b');
                    return;
                case 'f':
                    value.Append('\f');
                    return;
                case 'n':
                    value.Append('\n');
                    return;
                case 'r':
                    value.Append('\r');
                    return;
                case 't':
                    value.Append('\t');
                    return;
                case 'v':
                    value.Append('\v');
                    return;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    return;
                case '\n':
                    return;
                case 'x':
                    if (TryReadHexEscape(text, index + 1, 2, out var hexValue))
                    {
                        value.Append((char)hexValue);
                        index += 2;
                        return;
                    }

                    break;
                case 'u':
                    if (TryReadHexEscape(text, index + 1, 4, out var unicodeValue))
                    {
                        value.Append((char)unicodeValue);
                        index += 4;
                        return;
                    }

                    break;
            }

            value.Append(escaped);
        }

        private static bool TryReadHexEscape(string text, int start, int length, out int value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text) || start < 0 || start + length > text.Length)
            {
                return false;
            }

            return int.TryParse(
                text.Substring(start, length),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string QuoteUnquotedObjectKeys(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var result = new StringBuilder(text.Length);
            var i = 0;
            while (i < text.Length)
            {
                var current = text[i];
                if (current == '"')
                {
                    AppendJsonStringLiteral(text, result, ref i);
                    continue;
                }

                if (current == '{' || current == ',')
                {
                    result.Append(current);
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        result.Append(text[i]);
                        i++;
                    }

                    if (TryAppendQuotedObjectKey(text, result, ref i))
                    {
                        continue;
                    }

                    continue;
                }

                result.Append(current);
                i++;
            }

            return result.ToString();
        }

        private static void AppendJsonStringLiteral(string text, StringBuilder result, ref int index)
        {
            result.Append(text[index++]);
            while (index < text.Length)
            {
                var current = text[index++];
                result.Append(current);
                if (current == '\\' && index < text.Length)
                {
                    result.Append(text[index++]);
                    continue;
                }

                if (current == '"')
                {
                    return;
                }
            }
        }

        private static bool TryAppendQuotedObjectKey(string text, StringBuilder result, ref int index)
        {
            if (index >= text.Length)
            {
                return false;
            }

            var start = index;
            int end;
            if (IsIdentifierStart(text[index]))
            {
                end = index + 1;
                while (end < text.Length && IsIdentifierPart(text[end]))
                {
                    end++;
                }
            }
            else if (char.IsDigit(text[index]))
            {
                end = index + 1;
                while (end < text.Length && char.IsDigit(text[end]))
                {
                    end++;
                }
            }
            else
            {
                return false;
            }

            var colon = end;
            while (colon < text.Length && char.IsWhiteSpace(text[colon]))
            {
                colon++;
            }

            if (colon >= text.Length || text[colon] != ':')
            {
                return false;
            }

            result.Append('"');
            result.Append(text, start, end - start);
            result.Append('"');
            index = end;
            return true;
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || value == '$' || char.IsLetter(value);
        }

        private static bool IsIdentifierPart(char value)
        {
            return IsIdentifierStart(value) || char.IsDigit(value);
        }

        private static JToken ParseJsonToken(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JToken.Parse(json);
        }

        private static string ReadString(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return null;
            }

            if (token.Type == JTokenType.String)
            {
                return token.Value<string>();
            }

            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float || token.Type == JTokenType.Boolean)
            {
                return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
            }

            return token.ToString(Formatting.None);
        }

        private static int? ReadInt(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>();
            }

            if (int.TryParse(ReadString(token), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return null;
        }

        private static string NormalizeLanguage(string globalLanguage)
        {
            return (globalLanguage ?? string.Empty).Trim().ToLowerInvariant();
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (value ?? "cache").Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
            return new string(chars);
        }

        public void Dispose()
        {
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }
    }
}
