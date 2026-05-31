using Newtonsoft.Json.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.Hoyoverse
{
    internal static class HoyoverseExportParser
    {
        public static HashSet<string> ReadUnlockedIds(
            HoyoverseGameKind kind,
            string exportPath,
            IReadOnlyList<AchievementDetail> definitions,
            ILogger logger)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(exportPath))
            {
                return result;
            }

            if (!File.Exists(exportPath))
            {
                logger?.Warn($"[HoYoverse] Export file does not exist: {exportPath}");
                return result;
            }

            try
            {
                var text = File.ReadAllText(exportPath);
                if (IsStarRailStationExport(exportPath, text))
                {
                    return kind == HoyoverseGameKind.HonkaiStarRail
                        ? ParseStarRailStationDat(text, definitions)
                        : result;
                }

                var root = JToken.Parse(text);
                AddPaimonMoeUnlockedIds(kind, root, result);
                AddSeelieUnlockedIds(root, result);
                AddStarDbUnlockedIds(kind, root, result);
                AddStarDbExporterUnlockedIds(kind, root, result);
                AddRngMoeUnlockedIds(kind, root, result);
            }
            catch (Exception ex)
            {
                logger?.Warn(ex, $"[HoYoverse] Failed to parse export file '{exportPath}'. Achievements will remain locked.");
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            return result;
        }

        private static bool IsStarRailStationExport(string path, string text)
        {
            return string.Equals(Path.GetExtension(path), ".dat", StringComparison.OrdinalIgnoreCase) ||
                   (text ?? string.Empty).StartsWith("srs", StringComparison.Ordinal);
        }

        private static void AddPaimonMoeUnlockedIds(
            HoyoverseGameKind kind,
            JToken root,
            ISet<string> result)
        {
            if (kind != HoyoverseGameKind.GenshinImpact)
            {
                return;
            }

            var achievementRoot = root?["achievement"];
            if (achievementRoot == null)
            {
                return;
            }

            AddTruthyPropertyNames(achievementRoot, result);
        }

        private static void AddSeelieUnlockedIds(JToken root, ISet<string> result)
        {
            var achievements = root?["achievements"] as JObject;
            if (achievements == null)
            {
                return;
            }

            foreach (var property in achievements.Properties())
            {
                if (IsTruthy(property.Value) || IsTruthy(property.Value?["done"]))
                {
                    result.Add(property.Name);
                }
            }
        }

        private static void AddStarDbUnlockedIds(
            HoyoverseGameKind kind,
            JToken root,
            ISet<string> result)
        {
            var gameKey = GetStarDbGameKey(kind);
            if (string.IsNullOrWhiteSpace(gameKey))
            {
                return;
            }

            var achievements = root?["user"]?[gameKey]?["achievements"];
            if (achievements == null)
            {
                return;
            }

            if (achievements is JArray array)
            {
                foreach (var item in array)
                {
                    var id = ReadId(item);
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        result.Add(id);
                    }
                }
            }
            else if (achievements is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (IsTruthy(property.Value) || IsTruthy(property.Value?["done"]) || IsTruthy(property.Value?["unlocked"]))
                    {
                        result.Add(property.Name);
                    }
                }
            }
        }

        private static void AddStarDbExporterUnlockedIds(
            HoyoverseGameKind kind,
            JToken root,
            ISet<string> result)
        {
            var exporterKey = GetStarDbExporterKey(kind);
            if (string.IsNullOrWhiteSpace(exporterKey))
            {
                return;
            }

            var achievements = root?[exporterKey] as JArray;
            if (achievements == null)
            {
                return;
            }

            foreach (var item in achievements)
            {
                var id = ReadId(item);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(id);
                }
            }
        }

        private static void AddRngMoeUnlockedIds(
            HoyoverseGameKind kind,
            JToken root,
            ISet<string> result)
        {
            if (kind != HoyoverseGameKind.ZenlessZoneZero)
            {
                return;
            }

            var profiles = root?["data"]?["profiles"];
            if (profiles == null)
            {
                return;
            }

            foreach (var profile in EnumerateProfileObjects(profiles))
            {
                var enabled = profile?["stores"]?["2"]?["enabled"] as JObject;
                if (enabled == null)
                {
                    continue;
                }

                foreach (var property in enabled.Properties())
                {
                    if (IsTruthy(property.Value))
                    {
                        result.Add(property.Name);
                    }
                }
            }
        }

        private static HashSet<string> ParseStarRailStationDat(
            string content,
            IReadOnlyList<AchievementDetail> definitions)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var compressed = (content ?? string.Empty).StartsWith("srs", StringComparison.Ordinal)
                ? content.Substring(3)
                : content;
            var json = HoyoverseLzStringUtf16Codec.DecompressFromUtf16(compressed);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            var root = JToken.Parse(json);
            var completeState = root?["data"]?["stores"]?["1_achieve"]?["completeState"];
            if (completeState == null)
            {
                return result;
            }

            var officialIds = new HashSet<string>(
                (definitions ?? Array.Empty<AchievementDetail>())
                    .Where(detail => !string.IsNullOrWhiteSpace(detail?.ApiName))
                    .Select(detail => detail.ApiName),
                StringComparer.OrdinalIgnoreCase);
            var definitionsByTitle = (definitions ?? Array.Empty<AchievementDetail>())
                .Where(detail => !string.IsNullOrWhiteSpace(detail?.DisplayName) && !string.IsNullOrWhiteSpace(detail.ApiName))
                .GroupBy(detail => NormalizeTitle(detail.DisplayName), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().ApiName, StringComparer.OrdinalIgnoreCase);

            foreach (var stationId in EnumerateStarRailStationCompletedIds(completeState)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (officialIds.Contains(stationId))
                {
                    result.Add(stationId);
                    continue;
                }

                if (StarRailStationAchievementMap.TryGetOfficialTitle(stationId, out var englishTitle) &&
                    definitionsByTitle.TryGetValue(NormalizeTitle(englishTitle), out var officialId))
                {
                    result.Add(officialId);
                }
            }

            return result;
        }

        private static IEnumerable<string> EnumerateStarRailStationCompletedIds(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            if (token is JObject obj)
            {
                var objectId = ReadScalarId(obj["id"]) ??
                               ReadScalarId(obj["achievementId"]) ??
                               ReadScalarId(obj["achievement_id"]);
                if (!string.IsNullOrWhiteSpace(objectId))
                {
                    if (!HasCompletionMarker(obj) || HasTruthyCompletionMarker(obj))
                    {
                        yield return objectId;
                    }

                    yield break;
                }

                foreach (var property in obj.Properties())
                {
                    if (property.Value is JObject childObject)
                    {
                        if (HasTruthyCompletionMarker(childObject))
                        {
                            yield return property.Name;
                            continue;
                        }

                        foreach (var nested in EnumerateStarRailStationCompletedIds(childObject))
                        {
                            yield return nested;
                        }

                        continue;
                    }

                    if (property.Value is JArray childArray)
                    {
                        foreach (var nested in EnumerateStarRailStationCompletedIds(childArray))
                        {
                            yield return nested;
                        }

                        continue;
                    }

                    if (IsTruthy(property.Value))
                    {
                        if (!IsCompletionMarkerProperty(property.Name))
                        {
                            yield return property.Name;
                        }

                        continue;
                    }

                    var scalarId = ReadScalarId(property.Value);
                    if (!string.IsNullOrWhiteSpace(scalarId))
                    {
                        yield return scalarId;
                    }
                }

                yield break;
            }

            if (token is JArray array)
            {
                foreach (var child in array)
                {
                    if (child is JObject || child is JArray)
                    {
                        foreach (var nested in EnumerateStarRailStationCompletedIds(child))
                        {
                            yield return nested;
                        }

                        continue;
                    }

                    var scalarId = ReadScalarId(child);
                    if (!string.IsNullOrWhiteSpace(scalarId))
                    {
                        yield return scalarId;
                    }
                }

                yield break;
            }

            var id = ReadScalarId(token);
            if (!string.IsNullOrWhiteSpace(id))
            {
                yield return id;
            }
        }

        private static IEnumerable<JObject> EnumerateProfileObjects(JToken profiles)
        {
            if (profiles is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Value is JObject profile)
                    {
                        yield return profile;
                    }
                }
            }
            else if (profiles is JArray array)
            {
                foreach (var profile in array.OfType<JObject>())
                {
                    yield return profile;
                }
            }
        }

        private static void AddTruthyPropertyNames(JToken token, ISet<string> result)
        {
            foreach (var id in EnumerateTruthyPropertyNames(token))
            {
                result.Add(id);
            }
        }

        private static IEnumerable<string> EnumerateTruthyPropertyNames(JToken token)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (IsTruthy(property.Value))
                    {
                        yield return property.Name;
                    }

                    foreach (var nested in EnumerateTruthyPropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var child in array)
                {
                    foreach (var nested in EnumerateTruthyPropertyNames(child))
                    {
                        yield return nested;
                    }
                }
            }
        }

        private static bool IsTruthy(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
            {
                return false;
            }

            if (token.Type == JTokenType.Boolean)
            {
                return token.Value<bool>();
            }

            if (token.Type == JTokenType.Integer)
            {
                return token.Value<int>() != 0;
            }

            var value = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
            return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "done", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadId(JToken item)
        {
            if (item == null ||
                item.Type == JTokenType.Null ||
                item.Type == JTokenType.Undefined ||
                item.Type == JTokenType.Boolean)
            {
                return null;
            }

            if (item.Type == JTokenType.String || item.Type == JTokenType.Integer)
            {
                return Convert.ToString(((JValue)item).Value, CultureInfo.InvariantCulture);
            }

            if (!(item is JObject))
            {
                return null;
            }

            return item["id"]?.ToString() ??
                   item["achievementId"]?.ToString() ??
                   item["achievement_id"]?.ToString();
        }

        private static string ReadScalarId(JToken item)
        {
            var id = ReadId(item)?.Trim();
            if (string.IsNullOrWhiteSpace(id) ||
                id.Length < 4 ||
                string.Equals(id, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(id, "done", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return id;
        }

        private static bool HasCompletionMarker(JObject obj)
        {
            return obj != null &&
                obj.Properties().Any(property => IsCompletionMarkerProperty(property.Name));
        }

        private static bool HasTruthyCompletionMarker(JObject obj)
        {
            return obj != null &&
                obj.Properties().Any(property =>
                    IsCompletionMarkerProperty(property.Name) &&
                    IsTruthy(property.Value));
        }

        private static bool IsCompletionMarkerProperty(string name)
        {
            return string.Equals(name, "done", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "complete", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "unlocked", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "isUnlocked", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetStarDbGameKey(HoyoverseGameKind kind)
        {
            switch (kind)
            {
                case HoyoverseGameKind.GenshinImpact:
                    return "gi";
                case HoyoverseGameKind.HonkaiStarRail:
                    return "hsr";
                case HoyoverseGameKind.ZenlessZoneZero:
                    return "zzz";
                default:
                    return null;
            }
        }

        private static string GetStarDbExporterKey(HoyoverseGameKind kind)
        {
            switch (kind)
            {
                case HoyoverseGameKind.GenshinImpact:
                    return "gi_achievements";
                case HoyoverseGameKind.HonkaiStarRail:
                    return "hsr_achievements";
                default:
                    return null;
            }
        }

        private static string NormalizeTitle(string title)
        {
            return (title ?? string.Empty).Trim().ToUpperInvariant();
        }
    }

    internal static class StarRailStationAchievementMap
    {
        private const string ResourceName = "PlayniteAchievements.Providers.Hoyoverse.Data.starrailstation.json";

        private static readonly Lazy<Dictionary<string, string>> StationIdToEnglishTitle =
            new Lazy<Dictionary<string, string>>(LoadStationIdMap);

        public static bool TryGetOfficialTitle(string stationId, out string title)
        {
            return StationIdToEnglishTitle.Value.TryGetValue(stationId ?? string.Empty, out title);
        }

        private static Dictionary<string, string> LoadStationIdMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var assembly = typeof(StarRailStationAchievementMap).Assembly;
                using (var stream = assembly.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        return map;
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        var root = JObject.Parse(reader.ReadToEnd());
                        foreach (var achievement in root?["achievements"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
                        {
                            var id = ReadMapString(achievement["id"]);
                            var title = ReadMapString(achievement["title"]);
                            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(title))
                            {
                                map[id] = title;
                            }
                        }
                    }
                }
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return map;
        }

        private static string ReadMapString(JToken token)
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

            return token.ToString(Newtonsoft.Json.Formatting.None);
        }
    }
}
