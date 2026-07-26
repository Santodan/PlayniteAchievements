using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class BattleNetWowCatalogCache
    {
        private const int SchemaVersion = 3;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

        private readonly string _cacheRoot;
        private readonly ILogger _logger;

        public BattleNetWowCatalogCache(string pluginUserDataPath, ILogger logger)
        {
            _logger = logger;
            _cacheRoot = string.IsNullOrWhiteSpace(pluginUserDataPath)
                ? null
                : Path.Combine(pluginUserDataPath, "battlenet", "wow_catalog");
        }

        public bool IsEnabled => !string.IsNullOrWhiteSpace(_cacheRoot);

        public bool TryLoad(
            string region,
            string apiLocale,
            string wowLocale,
            string officialIndexSignature,
            int officialIndexCount,
            out List<AchievementDetail> achievements,
            out string reason)
        {
            achievements = null;
            reason = null;

            if (!IsEnabled)
            {
                reason = "disabled";
                return false;
            }

            var path = GetCachePath(region, apiLocale, wowLocale);
            if (!File.Exists(path))
            {
                reason = "missing";
                return false;
            }

            WowCatalogCacheFile file;
            try
            {
                file = JsonConvert.DeserializeObject<WowCatalogCacheFile>(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[BattleNet/WoW] Failed to read WoW catalog cache at {path}.");
                reason = "unreadable";
                return false;
            }

            if (file == null)
            {
                reason = "empty";
                return false;
            }

            if (file.SchemaVersion != SchemaVersion)
            {
                reason = "schema";
                return false;
            }

            if (!EqualsNormalized(file.Region, region) ||
                !EqualsNormalized(file.ApiLocale, apiLocale) ||
                !EqualsNormalized(file.WowLocale, wowLocale))
            {
                reason = "scope";
                return false;
            }

            if (file.UpdatedUtc == default(DateTime) ||
                DateTime.UtcNow - ToUtc(file.UpdatedUtc) > MaxAge)
            {
                reason = "expired";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(officialIndexSignature) &&
                !string.Equals(file.OfficialIndexSignature, officialIndexSignature, StringComparison.Ordinal))
            {
                reason = "official-index-changed";
                return false;
            }

            if (officialIndexCount > 0 &&
                file.OfficialIndexCount > 0 &&
                file.OfficialIndexCount != officialIndexCount)
            {
                reason = "official-index-count-changed";
                return false;
            }

            if (file.Achievements == null || file.Achievements.Count == 0)
            {
                reason = "no-achievements";
                return false;
            }

            achievements = file.Achievements
                .Select(ToAchievementDetail)
                .Where(item => item != null)
                .ToList();
            reason = "hit";
            return achievements.Count > 0;
        }

        public void Save(
            string region,
            string apiLocale,
            string wowLocale,
            string officialIndexSignature,
            int officialIndexCount,
            IEnumerable<AchievementDetail> achievements)
        {
            if (!IsEnabled)
            {
                return;
            }

            var normalizedAchievements = (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ApiName))
                .Select(ToCacheAchievement)
                .Where(item => item != null)
                .ToList();
            if (normalizedAchievements.Count == 0)
            {
                return;
            }

            var file = new WowCatalogCacheFile
            {
                SchemaVersion = SchemaVersion,
                Region = NormalizeScope(region),
                ApiLocale = NormalizeScope(apiLocale),
                WowLocale = NormalizeScope(wowLocale),
                UpdatedUtc = DateTime.UtcNow,
                OfficialIndexSignature = officialIndexSignature,
                OfficialIndexCount = Math.Max(officialIndexCount, 0),
                Achievements = normalizedAchievements
            };

            var path = GetCachePath(region, apiLocale, wowLocale);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(file, Formatting.None));
                _logger?.Info($"[BattleNet/WoW] Saved WoW catalog cache. achievements={normalizedAchievements.Count}, scope={file.Region}/{file.ApiLocale}/{file.WowLocale}");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[BattleNet/WoW] Failed to write WoW catalog cache at {path}.");
            }
        }

        public static string BuildOfficialIndexSignature(IEnumerable<WowOfficialAchievementDefinition> catalog)
        {
            var builder = new StringBuilder();
            foreach (var item in (catalog ?? Enumerable.Empty<WowOfficialAchievementDefinition>())
                .Where(item => item != null && item.Id > 0)
                .OrderBy(item => item.Id))
            {
                builder
                    .Append(item.Id.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.Name ?? string.Empty).Append('|')
                    .Append(item.Description ?? string.Empty).Append('|')
                    .Append(item.Points.ToString(CultureInfo.InvariantCulture)).Append('|')
                    .Append(item.IsHidden ? "1" : "0").Append('|')
                    .Append(item.IsObtainable.HasValue ? (item.IsObtainable.Value ? "1" : "0") : string.Empty).Append('|')
                    .Append(item.IsObtainableInGame.HasValue ? (item.IsObtainableInGame.Value ? "1" : "0") : string.Empty).Append('|')
                    .Append(item.Category?.Id.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|')
                    .Append(item.Category?.Name ?? string.Empty)
                    .AppendLine();
            }

            if (builder.Length == 0)
            {
                return null;
            }

            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        private string GetCachePath(string region, string apiLocale, string wowLocale)
        {
            return Path.Combine(
                _cacheRoot,
                $"{SanitizeFileName(region)}_{SanitizeFileName(apiLocale)}_{SanitizeFileName(wowLocale)}.json");
        }

        private static WowCatalogCacheAchievement ToCacheAchievement(AchievementDetail detail)
        {
            if (detail == null || string.IsNullOrWhiteSpace(detail.ApiName))
            {
                return null;
            }

            return new WowCatalogCacheAchievement
            {
                ApiName = detail.ApiName,
                DisplayName = detail.DisplayName,
                Description = detail.Description,
                UnlockedIconPath = detail.UnlockedIconPath,
                LockedIconPath = detail.LockedIconPath,
                Points = detail.Points,
                ScaledPoints = detail.ScaledPoints,
                CategoryType = detail.CategoryType,
                Category = detail.Category,
                TrophyType = detail.TrophyType,
                IsCapstone = detail.IsCapstone,
                Hidden = detail.Hidden
            };
        }

        private static AchievementDetail ToAchievementDetail(WowCatalogCacheAchievement item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ApiName))
            {
                return null;
            }

            return new AchievementDetail
            {
                ApiName = item.ApiName,
                DisplayName = item.DisplayName,
                Description = item.Description,
                UnlockedIconPath = item.UnlockedIconPath,
                LockedIconPath = item.LockedIconPath,
                Points = item.Points,
                ScaledPoints = item.ScaledPoints,
                CategoryType = item.CategoryType,
                Category = item.Category,
                TrophyType = item.TrophyType,
                IsCapstone = item.IsCapstone,
                Hidden = item.Hidden,
                ProviderKey = "BattleNet",
                Unlocked = false
            };
        }

        private static bool EqualsNormalized(string left, string right)
        {
            return string.Equals(NormalizeScope(left), NormalizeScope(right), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeScope(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static DateTime ToUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Utc)
            {
                return value;
            }

            if (value.Kind == DateTimeKind.Local)
            {
                return value.ToUniversalTime();
            }

            return DateTime.SpecifyKind(value, DateTimeKind.Utc);
        }

        private static string SanitizeFileName(string value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "default" : value.Trim().ToLowerInvariant();
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                builder.Append(invalid.Contains(ch) ? '_' : ch);
            }

            return builder.ToString();
        }

        private sealed class WowCatalogCacheFile
        {
            public int SchemaVersion { get; set; }
            public string Region { get; set; }
            public string ApiLocale { get; set; }
            public string WowLocale { get; set; }
            public DateTime UpdatedUtc { get; set; }
            public string OfficialIndexSignature { get; set; }
            public int OfficialIndexCount { get; set; }
            public List<WowCatalogCacheAchievement> Achievements { get; set; }
        }

        private sealed class WowCatalogCacheAchievement
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public int? ScaledPoints { get; set; }
            public string CategoryType { get; set; }
            public string Category { get; set; }
            public string TrophyType { get; set; }
            public bool IsCapstone { get; set; }
            public bool Hidden { get; set; }
        }
    }
}
