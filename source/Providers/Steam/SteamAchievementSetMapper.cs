using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Steam.Models;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Maps Steam's achievement data keyed by internal key (Web API and the achievements page)
    /// onto api-name keyed unlock state, and Steam achievement sets onto categories.
    /// </summary>
    internal static class SteamAchievementSetMapper
    {
        internal sealed class UnlockMapping
        {
            public UserUnlockedAchievements Unlocks { get; set; }

            // Keys the player has data for that the schema does not define.
            public int UnknownKeyCount { get; set; }
        }

        /// <summary>
        /// Converts unlocks keyed by internal key to api-name keyed unlocks. A key resolves
        /// through <paramref name="schema"/> first and <paramref name="fallbackSchema"/> second
        /// (the page's own schema, whose rows carry both key and api name).
        /// </summary>
        public static UnlockMapping MapUnlocks(
            int appId,
            SchemaAndPercentages schema,
            SteamUserAchievements user,
            SchemaAndPercentages fallbackSchema = null)
        {
            var unlocks = new UserUnlockedAchievements
            {
                AppId = appId,
                LastUpdatedUtc = DateTime.UtcNow
            };
            var mapping = new UnlockMapping { Unlocks = unlocks };
            if (user?.ByInternalKey == null || user.ByInternalKey.Count == 0)
            {
                return mapping;
            }

            var byKey = IndexByInternalKey(schema);
            var fallbackByKey = IndexByInternalKey(fallbackSchema);

            foreach (var entry in user.ByInternalKey)
            {
                if (!byKey.TryGetValue(entry.Key, out var definition) &&
                    !fallbackByKey.TryGetValue(entry.Key, out definition))
                {
                    mapping.UnknownKeyCount++;
                    continue;
                }

                var apiName = definition.Name?.Trim();
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    mapping.UnknownKeyCount++;
                    continue;
                }

                var achievement = entry.Value;
                if (achievement.Unlocked)
                {
                    unlocks.UnlockedApiNames.Add(apiName);
                    var unlockTime = FromUnixSeconds(achievement.UnlockTime);
                    if (unlockTime.HasValue)
                    {
                        unlocks.UnlockTimesUtc[apiName] = unlockTime.Value;
                    }
                }

                // The page schema has no progress bounds, so look them up on either definition.
                var bounds = definition.MaxProgress.HasValue
                    ? definition
                    : (byKey.TryGetValue(entry.Key, out var primary) ? primary : definition);
                var progress = ResolveProgress(bounds, achievement);
                if (progress.HasValue)
                {
                    unlocks.ProgressNum[apiName] = progress.Value.Num;
                    unlocks.ProgressDenom[apiName] = progress.Value.Denom;
                }
            }

            return mapping;
        }

        /// <summary>
        /// Progress for achievements whose schema defines a range wider than 1, matching the rule
        /// the stats-page scraper applied to "n / m" progress text.
        /// </summary>
        internal static (int Num, int Denom)? ResolveProgress(SchemaAchievement definition, UserAchievement achievement)
        {
            if (definition?.MaxProgress == null || achievement == null)
            {
                return null;
            }

            var min = definition.MinProgress ?? 0;
            var max = definition.MaxProgress.Value;
            var range = max - min;
            if (range <= 1 || double.IsNaN(range) || double.IsInfinity(range))
            {
                return null;
            }

            double? current = achievement.ProgressInt.HasValue
                ? achievement.ProgressInt.Value
                : achievement.ProgressFloat;
            if (!current.HasValue)
            {
                if (!achievement.Unlocked)
                {
                    return null;
                }

                current = max;
            }

            var num = Math.Max(0, Math.Min(range, current.Value - min));
            return ((int)Math.Round(num), (int)Math.Round(range));
        }

        internal static DateTime? FromUnixSeconds(long? seconds)
        {
            if (!seconds.HasValue || seconds.Value <= 0)
            {
                return null;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds.Value).UtcDateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        /// <summary>
        /// Sets Category and CategoryKind from Steam achievement sets, and marks archived
        /// achievements Unobtainable. Returns the api names that belong to a named set (any set
        /// other than the unnamed base set), which other category sources leave alone when merging.
        /// Games without real sets keep a null Category, the provider convention for "no
        /// category".
        /// </summary>
        public static HashSet<string> ApplySets(
            IList<AchievementDetail> achievements,
            SchemaAndPercentages schema,
            string gameName)
        {
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null || achievements.Count == 0 || schema?.Achievements == null)
            {
                return named;
            }

            var definitions = schema.Achievements
                .Where(definition => !string.IsNullOrWhiteSpace(definition?.Name))
                .GroupBy(definition => definition.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var useSets = schema.HasAchievementSets;

            foreach (var achievement in achievements)
            {
                var apiName = achievement?.ApiName?.Trim();
                if (string.IsNullOrWhiteSpace(apiName) || !definitions.TryGetValue(apiName, out var definition))
                {
                    continue;
                }

                if (useSets)
                {
                    schema.Groups.TryGetValue(definition.GroupId, out var group);
                    achievement.Category = ResolveSetLabel(group, gameName);
                    achievement.CategoryKind = group?.DlcAppId > 0
                        ? SteamCategoryLabels.DlcKind
                        : SteamCategoryLabels.BaseKind;
                    if (IsNamedSet(group))
                    {
                        named.Add(apiName);
                    }
                }

                if (definition.Archived || (useSets && IsArchivedGroup(schema, definition.GroupId)))
                {
                    achievement.Tags = WithUnobtainable(achievement.Tags);
                }
            }

            return named;
        }

        /// <summary>
        /// Re-applies Unobtainable to archived achievements after another enricher reset their
        /// types.
        /// </summary>
        public static void ApplyArchived(IList<AchievementDetail> achievements, SchemaAndPercentages schema)
        {
            if (achievements == null || schema?.Achievements == null)
            {
                return;
            }

            var archived = new HashSet<string>(
                schema.Achievements
                    .Where(definition => !string.IsNullOrWhiteSpace(definition?.Name) &&
                                         (definition.Archived || IsArchivedGroup(schema, definition.GroupId)))
                    .Select(definition => definition.Name.Trim()),
                StringComparer.OrdinalIgnoreCase);
            if (archived.Count == 0)
            {
                return;
            }

            foreach (var achievement in achievements)
            {
                var apiName = achievement?.ApiName?.Trim();
                if (apiName != null && archived.Contains(apiName))
                {
                    achievement.Tags = WithUnobtainable(achievement.Tags);
                }
            }
        }

        /// <summary>
        /// One default art entry per set label: DLC sets use their DLC app's art, every other set
        /// the game's own, base first so a DLC label equal to the game name cannot take it.
        /// Same rule as the SteamHunters plan, so both sources key art files identically.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, int>> BuildCategoryImagePlan(
            SchemaAndPercentages schema,
            string gameName,
            int appId)
        {
            var plan = new List<KeyValuePair<string, int>>();
            if (schema?.HasAchievementSets != true || appId <= 0)
            {
                return plan;
            }

            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
            var baseLabel = string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();
            if (baseLabel != null && seen.Add(baseLabel))
            {
                plan.Add(new KeyValuePair<string, int>(
                    AchievementCategoryHelper.NormalizeCategoryOrDefault(baseLabel),
                    appId));
            }

            var usedGroupIds = new HashSet<int>(schema.Achievements
                .Where(definition => definition != null)
                .Select(definition => definition.GroupId));
            foreach (var group in schema.Groups.Values
                         .Where(group => usedGroupIds.Contains(group.GroupId))
                         .OrderBy(group => group.GroupId == 0 ? 0 : 1)
                         .ThenBy(group => group.Order)
                         .ThenBy(group => group.GroupId))
            {
                var label = ResolveSetLabel(group, gameName);
                if (string.IsNullOrWhiteSpace(label) || !seen.Add(label))
                {
                    continue;
                }

                plan.Add(new KeyValuePair<string, int>(
                    AchievementCategoryHelper.NormalizeCategoryOrDefault(label),
                    group.DlcAppId > 0 ? group.DlcAppId : appId));
            }

            return plan;
        }

        // The unnamed base set takes the game name, like SteamHunters' base category. DLC set
        // names that repeat the game name ("DREDGE - The Pale Reach") drop that prefix.
        internal static string ResolveSetLabel(SchemaAchievementGroup group, string gameName)
        {
            var name = group?.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim();
            }

            return SteamCategoryLabels.StripGameNamePrefix(name, gameName);
        }

        internal static bool IsNamedSet(SchemaAchievementGroup group)
        {
            return group != null && (group.GroupId != 0 || !string.IsNullOrWhiteSpace(group.Name));
        }

        private static bool IsArchivedGroup(SchemaAndPercentages schema, int groupId)
        {
            return schema?.Groups != null &&
                   schema.Groups.TryGetValue(groupId, out var group) &&
                   group.Archived;
        }

        private static string WithUnobtainable(string tags)
        {
            return AchievementTagHelper.Combine(new[]
            {
                tags,
                AchievementTagHelper.Unobtainable
            });
        }

        private static Dictionary<int, SchemaAchievement> IndexByInternalKey(SchemaAndPercentages schema)
        {
            var index = new Dictionary<int, SchemaAchievement>();
            foreach (var definition in schema?.Achievements ?? Enumerable.Empty<SchemaAchievement>())
            {
                if (definition?.InternalKey != null && !index.ContainsKey(definition.InternalKey.Value))
                {
                    index[definition.InternalKey.Value] = definition;
                }
            }

            return index;
        }
    }
}
