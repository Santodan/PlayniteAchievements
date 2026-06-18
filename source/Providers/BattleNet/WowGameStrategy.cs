using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.BattleNet.Models;

namespace PlayniteAchievements.Providers.BattleNet
{
    internal sealed class WowGameStrategy
    {
        internal const string EnglishMetadataLocale = "en-US";

        private readonly BattleNetApiClient _client;
        private readonly ILogger _logger;

        public WowGameStrategy(BattleNetApiClient client, ILogger logger)
        {
            _client = client;
            _logger = logger;
        }

        public bool MatchesGame(Game game)
        {
            return BattleNetGameSupport.IsWowGame(game);
        }

        public async Task<GameAchievementData> FetchAchievementsAsync(Game game, string locale, CancellationToken ct)
        {
            var settings = ProviderRegistry.Settings<BattleNetSettings>();
            var region = settings.WowRegion;
            var realmSlug = settings.WowRealmSlug;
            var character = settings.WowCharacter;
            var effectiveLocale = string.IsNullOrWhiteSpace(locale) ? "en-US" : locale;

            if (string.IsNullOrEmpty(region) || string.IsNullOrEmpty(realmSlug) || string.IsNullOrEmpty(character))
            {
                _logger?.Warn($"[BattleNet/WoW] Region, realm, or character not configured. region={Presence(region)}, realmSlug={Presence(realmSlug)}, character={Presence(character)}");
                return null;
            }

            var wowLocale = BattleNetLocaleMapper.ToWowWebLocale(effectiveLocale);
            var allData = await _client.GetWowAllAchievementsAsync(region, realmSlug, character, wowLocale, ct);

            if (allData == null || allData.Count == 0)
            {
                _logger?.Warn($"[BattleNet/WoW] No public achievement category payloads returned. game={GameLabel(game)}");
                return null;
            }

            var achievements = ParseAchievements(allData);
            if (achievements.Count == 0)
            {
                _logger?.Warn($"[BattleNet/WoW] Public achievement payloads contained no parsed achievements. game={GameLabel(game)}");
                return null;
            }

            var data = new GameAchievementData
            {
                ProviderKey = "BattleNet",
                GameName = game.Name,
                PlayniteGameId = game.Id,
                AppId = StableAppId("WoW"),
                Achievements = achievements,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements.Count > 0
            };
            return data;
        }

        internal static List<AchievementDetail> ParseAchievements(IEnumerable<WowAchievementsData> allData)
        {
            var achievements = new List<AchievementDetail>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var categoryData in allData ?? Enumerable.Empty<WowAchievementsData>())
            {
                if (categoryData == null)
                {
                    continue;
                }

                var category = categoryData.Name ?? categoryData.Category;
                var subcategories = ReadSubcategories(categoryData.Subcategories);
                if (subcategories.Count > 0)
                {
                    foreach (var subcategory in subcategories)
                    {
                        AddAchievements(achievements, seen, subcategory?.Achievements, category);
                    }
                }
                else
                {
                    AddAchievements(achievements, seen, categoryData.AchievementsList, category);
                }
            }

            return achievements;
        }

        internal static bool RequiresEnglishMetadataProjection(string locale)
        {
            var wowLocale = BattleNetLocaleMapper.ToWowWebLocale(
                string.IsNullOrWhiteSpace(locale) ? EnglishMetadataLocale : locale);
            return !string.Equals(wowLocale, "en-us", StringComparison.OrdinalIgnoreCase);
        }

        internal static List<AchievementDetail> CreateEnglishMetadataProjection(
            IList<AchievementDetail> nativeAchievements,
            IList<AchievementDetail> englishAchievements)
        {
            var englishByApiName = BuildAchievementLookup(englishAchievements);
            var projection = new List<AchievementDetail>();

            foreach (var nativeAchievement in nativeAchievements ?? Enumerable.Empty<AchievementDetail>())
            {
                if (nativeAchievement == null)
                {
                    projection.Add(null);
                    continue;
                }

                var projected = CloneForMetadataProjection(nativeAchievement);
                var apiName = NormalizeApiName(nativeAchievement.ApiName);
                if (!string.IsNullOrWhiteSpace(apiName) &&
                    englishByApiName.TryGetValue(apiName, out var englishAchievement))
                {
                    if (!string.IsNullOrWhiteSpace(englishAchievement.DisplayName))
                    {
                        projected.DisplayName = englishAchievement.DisplayName;
                    }

                    if (!string.IsNullOrWhiteSpace(englishAchievement.Description))
                    {
                        projected.Description = englishAchievement.Description;
                    }

                    if (!string.IsNullOrWhiteSpace(englishAchievement.Category))
                    {
                        projected.Category = englishAchievement.Category;
                    }

                    if (englishAchievement.Points.HasValue)
                    {
                        projected.Points = englishAchievement.Points;
                    }
                }

                projection.Add(projected);
            }

            return projection;
        }

        internal static int ApplyProjectedRarity(
            IList<AchievementDetail> targetAchievements,
            IList<AchievementDetail> projectedAchievements)
        {
            var projectedByApiName = BuildAchievementLookup(projectedAchievements);
            var updated = 0;

            foreach (var targetAchievement in targetAchievements ?? Enumerable.Empty<AchievementDetail>())
            {
                var apiName = NormalizeApiName(targetAchievement?.ApiName);
                if (string.IsNullOrWhiteSpace(apiName) ||
                    !projectedByApiName.TryGetValue(apiName, out var projectedAchievement) ||
                    projectedAchievement?.GlobalPercentUnlocked == null)
                {
                    continue;
                }

                if (targetAchievement.GlobalPercentUnlocked == projectedAchievement.GlobalPercentUnlocked &&
                    targetAchievement.Rarity == projectedAchievement.Rarity)
                {
                    continue;
                }

                targetAchievement.GlobalPercentUnlocked = projectedAchievement.GlobalPercentUnlocked;
                targetAchievement.Rarity = projectedAchievement.Rarity;
                updated++;
            }

            return updated;
        }

        private static void AddAchievements(
            List<AchievementDetail> target,
            HashSet<string> seen,
            IEnumerable<WowAchievement> source,
            string category)
        {
            foreach (var achievement in source ?? Enumerable.Empty<WowAchievement>())
            {
                if (achievement == null || achievement.Id == 0 || !seen.Add(achievement.Id.ToString()))
                {
                    continue;
                }

                var detail = new AchievementDetail
                {
                    ApiName = achievement.Id.ToString(),
                    DisplayName = achievement.Name,
                    Description = achievement.Description,
                    UnlockedIconPath = achievement.Icon?.Url,
                    Points = achievement.Point > 0 ? achievement.Point : (int?)null,
                    Category = category,
                    ProviderKey = "BattleNet",
                    Unlocked = achievement.Time.HasValue && achievement.Time.Value != default
                };

                if (detail.Unlocked)
                {
                    var time = achievement.Time.Value;
                    detail.UnlockTimeUtc = time.Kind == DateTimeKind.Utc
                        ? time
                        : time.Kind == DateTimeKind.Local
                            ? time.ToUniversalTime()
                            : DateTime.SpecifyKind(time, DateTimeKind.Utc);
                }

                target.Add(detail);
            }
        }

        private static AchievementDetail CloneForMetadataProjection(AchievementDetail source)
        {
            return new AchievementDetail
            {
                ApiName = source.ApiName,
                DisplayName = source.DisplayName,
                Description = source.Description,
                UnlockedIconPath = source.UnlockedIconPath,
                LockedIconPath = source.LockedIconPath,
                Points = source.Points,
                ScaledPoints = source.ScaledPoints,
                CategoryType = source.CategoryType,
                Category = source.Category,
                TrophyType = source.TrophyType,
                IsCapstone = source.IsCapstone,
                Hidden = source.Hidden,
                UnlockTimeUtc = source.UnlockTimeUtc,
                GlobalPercentUnlocked = source.GlobalPercentUnlocked,
                Rarity = source.Rarity,
                ProgressNum = source.ProgressNum,
                ProgressDenom = source.ProgressDenom,
                ProviderKey = source.ProviderKey,
                Unlocked = source.Unlocked
            };
        }

        private static Dictionary<string, AchievementDetail> BuildAchievementLookup(
            IEnumerable<AchievementDetail> achievements)
        {
            return (achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName))
                .GroupBy(achievement => NormalizeApiName(achievement.ApiName), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }

        private static string NormalizeApiName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static List<WowSubcategory> ReadSubcategories(object value)
        {
            try
            {
                if (value is JObject keyed)
                {
                    return keyed.Properties()
                        .Select(property => property.Value?.ToObject<WowSubcategory>())
                        .Where(item => item != null)
                        .ToList();
                }

                if (value is JArray array)
                {
                    return array
                        .Select(token => token?.ToObject<WowSubcategory>())
                        .Where(item => item != null)
                        .ToList();
                }

                return (value as IEnumerable<WowSubcategory>)?
                    .Where(item => item != null)
                    .ToList() ?? new List<WowSubcategory>();
            }
            catch
            {
                return new List<WowSubcategory>();
            }
        }

        private static int StableAppId(string id)
        {
            int hash = 0;
            foreach (char c in id)
            {
                hash = (hash << 5) - hash + c;
            }
            return Math.Abs(hash);
        }

        private static string Presence(string value) => string.IsNullOrWhiteSpace(value) ? "missing" : "set";

        private static string GameLabel(Game game)
        {
            if (game == null)
            {
                return "<null>";
            }

            return $"{game.Name ?? "<unnamed>"} ({game.Id})";
        }
    }
}
