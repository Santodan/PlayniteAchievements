using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Services.Achievements;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class DefinitionCategoryBackfill
    {
        public long DefinitionId { get; set; }
        public string Category { get; set; }
        public string CategoryType { get; set; }
    }

    internal static class SqlNadoCacheBehavior
    {
        // Computes category backfills for a mapped friend proxy row: a fetched definition's
        // Category/CategoryType only fills existing rows still holding the Default placeholder
        // (or blank), matched by ApiName. Rows a user scan already categorized are never changed,
        // keeping the mapped current-user schema canonical.
        public static List<DefinitionCategoryBackfill> ComputeDefinitionCategoryBackfills(
            IReadOnlyList<(long Id, string ApiName, string Category, string CategoryType)> existingDefinitions,
            IEnumerable<AchievementDetail> achievements)
        {
            var result = new List<DefinitionCategoryBackfill>();
            if (existingDefinitions == null || existingDefinitions.Count == 0 || achievements == null)
            {
                return result;
            }

            var incomingByApiName = new Dictionary<string, AchievementDetail>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in achievements)
            {
                var incomingApiName = achievement?.ApiName?.Trim();
                if (!string.IsNullOrWhiteSpace(incomingApiName) && !incomingByApiName.ContainsKey(incomingApiName))
                {
                    incomingByApiName[incomingApiName] = achievement;
                }
            }

            foreach (var existing in existingDefinitions)
            {
                var apiName = existing.ApiName?.Trim();
                if (string.IsNullOrWhiteSpace(apiName) ||
                    existing.Id <= 0 ||
                    !incomingByApiName.TryGetValue(apiName, out var incoming))
                {
                    continue;
                }

                var incomingCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(incoming.Category);
                var incomingCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(incoming.CategoryType);
                var fillCategory = !IsDefaultCategoryValue(incomingCategory) && IsDefaultCategoryValue(existing.Category);
                var fillCategoryType = !IsDefaultCategoryValue(incomingCategoryType) && IsDefaultCategoryValue(existing.CategoryType);
                if (!fillCategory && !fillCategoryType)
                {
                    continue;
                }

                result.Add(new DefinitionCategoryBackfill
                {
                    DefinitionId = existing.Id,
                    Category = fillCategory ? incomingCategory : existing.Category,
                    CategoryType = fillCategoryType ? incomingCategoryType : existing.CategoryType
                });
            }

            return result;
        }

        // Blank and the "Default" placeholder both mean "no category assigned".
        public static bool IsDefaultCategoryValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return normalized.Length == 0 ||
                   string.Equals(
                       normalized,
                       AchievementCategoryTypeHelper.DefaultCategoryLabel,
                       StringComparison.OrdinalIgnoreCase);
        }

        // Builds fallback achievement definitions from a friend unlock scrape, used only when a game has
        // no cached definitions. Requires a stable ApiName plus a display name; rows a provider could only
        // key as unlock-status (no ApiName/name) are skipped and duplicate ApiNames are collapsed, so this
        // seeds nothing for providers that do not carry definition-quality data in their scrape.
        // Unlock rows sourced from the Exophase earned-awards JSON endpoint carry only stable keys
        // ("exophase:{id}" plus the platform-native ProviderNativeKey) and no display text; they can
        // match definitions solely by key.
        public static bool RowsRequireStableKeyedDefinitions(IReadOnlyList<FriendAchievementRow> rows)
        {
            return rows != null &&
                   rows.Count > 0 &&
                   rows.All(row => row?.ApiName?.StartsWith("exophase:", StringComparison.OrdinalIgnoreCase) == true &&
                                   string.IsNullOrWhiteSpace(row.DisplayName));
        }

        // Legacy display-derived Exophase keys ("exophase_...") mean the game's definitions are
        // still awaiting the stable-id migration; stable-keyed unlock rows cannot match them and
        // must defer. Definitions keyed by another provider's scheme (Steam apinames, PSN trophy
        // keys) are NOT migration-pending — those saves proceed and match via the native-key bridge.
        public static bool HasLegacyExophaseKeyedDefinitions(IEnumerable<string> definitionApiNames)
        {
            return definitionApiNames != null &&
                   definitionApiNames.Any(apiName =>
                       apiName?.StartsWith("exophase_", StringComparison.OrdinalIgnoreCase) == true);
        }

        // Native-key bridge: an aggregator unlock row carries the platform's native achievement key
        // (Exophase /earned canonical_id). Definitions written by the platform's own provider may use
        // that key verbatim (Steam: ApiName == apiname).
        public static bool MatchesNativeKey(string definitionApiName, string nativeKey)
        {
            var definition = definitionApiName?.Trim();
            var native = nativeKey?.Trim();
            return !string.IsNullOrEmpty(definition) &&
                   !string.IsNullOrEmpty(native) &&
                   string.Equals(definition, native, StringComparison.OrdinalIgnoreCase);
        }

        // Group-qualified native-key form: PSN definitions are keyed "{group}:{trophyId}" (group
        // "default" when blank) while Exophase's canonical_id for PSN is the bare trophy id, so the
        // bridge matches on the "{id}" segment. "exophase:{id}" is the aggregator's own scheme, never
        // a group-qualified native key, and is excluded. Adjust here if live PSN canonical_id data
        // shows a different shape.
        public static bool MatchesGroupQualifiedNativeKey(string definitionApiName, string nativeKey)
        {
            var definition = definitionApiName?.Trim();
            var native = nativeKey?.Trim();
            if (string.IsNullOrEmpty(definition) || string.IsNullOrEmpty(native))
            {
                return false;
            }

            var separatorIndex = definition.LastIndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= definition.Length - 1)
            {
                return false;
            }

            var group = definition.Substring(0, separatorIndex);
            if (string.Equals(group, "exophase", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var idSegment = definition.Substring(separatorIndex + 1);
            return string.Equals(idSegment, native, StringComparison.OrdinalIgnoreCase);
        }

        public static List<AchievementDetail> BuildDefinitionsFromFriendRows(IEnumerable<FriendAchievementRow> rows)
        {
            var result = new List<AchievementDetail>();
            if (rows == null)
            {
                return result;
            }

            var seenApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ApiName) || string.IsNullOrWhiteSpace(row.DisplayName))
                {
                    continue;
                }

                var apiName = row.ApiName.Trim();
                if (!seenApiNames.Add(apiName))
                {
                    continue;
                }

                result.Add(new AchievementDetail
                {
                    ApiName = apiName,
                    DisplayName = row.DisplayName,
                    Description = row.Description,
                    UnlockedIconPath = FirstNonBlank(row.UnlockedIconUrl, row.IconUrl),
                    LockedIconPath = FirstNonBlank(row.LockedIconUrl, row.IconUrl, row.UnlockedIconUrl),
                    Points = row.Points,
                    ScaledPoints = row.ScaledPoints,
                    Category = row.Category,
                    CategoryType = row.CategoryType,
                    TrophyType = row.TrophyType,
                    Hidden = row.Hidden,
                    IsCapstone = row.IsCapstone ||
                        string.Equals(row.TrophyType?.Trim(), "platinum", StringComparison.OrdinalIgnoreCase),
                    GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                    Rarity = row.Rarity ?? RarityTier.Common,
                    ProgressDenom = row.ProgressDenom
                });
            }

            return result;
        }

        private static string FirstNonBlank(params string[] values)
        {
            return values?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        }

        public static List<long> ComputeStaleDefinitionIds(
            IDictionary<string, long> existingDefinitionIdsByApiName,
            IEnumerable<string> incomingApiNames)
        {
            var staleIds = new List<long>();
            if (existingDefinitionIdsByApiName == null || existingDefinitionIdsByApiName.Count == 0)
            {
                return staleIds;
            }

            var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (incomingApiNames != null)
            {
                foreach (var apiName in incomingApiNames)
                {
                    var normalized = apiName?.Trim();
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        desired.Add(normalized);
                    }
                }
            }

            foreach (var pair in existingDefinitionIdsByApiName)
            {
                var apiName = pair.Key?.Trim();
                if (string.IsNullOrWhiteSpace(apiName) || desired.Contains(apiName))
                {
                    continue;
                }

                if (pair.Value > 0)
                {
                    staleIds.Add(pair.Value);
                }
            }

            return staleIds;
        }

        public static bool ShouldMarkLegacyImportDone(
            int parseFailedCount,
            int dbWriteFailedCount,
            int remainingFileCount)
        {
            return parseFailedCount <= 0 &&
                   dbWriteFailedCount <= 0 &&
                   remainingFileCount <= 0;
        }

        public static bool ShouldFallbackToProviderGameIdLookup(
            string providerKey,
            string playniteGameId,
            long? providerGameId)
        {
            if (!providerGameId.HasValue || providerGameId.Value <= 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(playniteGameId))
            {
                return false;
            }

            return true;
        }

        public static bool IsRetroAchievementsProvider(string providerKey)
        {
            return !string.IsNullOrWhiteSpace(providerKey) &&
                   string.Equals(providerKey.Trim(), "RetroAchievements", StringComparison.OrdinalIgnoreCase);
        }

        public static bool CanReclaimExophaseProxy(string incomingProviderKey)
        {
            var incomingProvider = NormalizeProviderKey(incomingProviderKey);

            if (string.IsNullOrWhiteSpace(incomingProvider))
            {
                return false;
            }

            return !string.Equals(incomingProvider, "Exophase", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(incomingProvider, "Manual", StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(incomingProvider, "Unmapped", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ComputeIsCompleted(
            bool providerIsCompleted,
            int unlockedCount,
            int totalCount,
            bool markerUnlocked)
        {
            if (providerIsCompleted || markerUnlocked)
            {
                return true;
            }

            return totalCount > 0 && unlockedCount >= totalCount;
        }

        private static string NormalizeProviderKey(string providerKey)
        {
            return string.IsNullOrWhiteSpace(providerKey)
                ? null
                : providerKey.Trim();
        }
    }
}
