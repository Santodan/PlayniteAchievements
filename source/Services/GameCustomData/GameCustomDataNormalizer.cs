using PlayniteAchievements.Models.Settings;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    internal static class GameCustomDataNormalizer
    {
        internal const int CurrentSchemaVersion = 1;

        public static GameCustomDataFile CreateDefault(Guid playniteGameId)
        {
            return new GameCustomDataFile
            {
                SchemaVersion = CurrentSchemaVersion,
                PlayniteGameId = playniteGameId
            };
        }

        public static GameCustomDataFile NormalizeInternal(GameCustomDataFile data, Guid playniteGameId)
        {
            var normalized = data?.Clone() ?? CreateDefault(playniteGameId);
            normalized.SchemaVersion = CurrentSchemaVersion;
            normalized.PlayniteGameId = playniteGameId;
            normalized.ExcludedFromRefreshes = normalized.ExcludedFromRefreshes == true ? true : (bool?)null;
            normalized.ExcludedFromSummaries = normalized.ExcludedFromSummaries == true ? true : (bool?)null;
            normalized.UseSeparateLockedIconsOverride = normalized.UseSeparateLockedIconsOverride == true ? true : (bool?)null;
            normalized.ForceUseExophase = normalized.ForceUseExophase == true ? true : (bool?)null;
            normalized.ManualCapstoneApiName = NormalizeString(normalized.ManualCapstoneApiName);
            normalized.ExophaseSlugOverride = NormalizeString(normalized.ExophaseSlugOverride);
            normalized.RetroAchievementsGameIdOverride =
                normalized.RetroAchievementsGameIdOverride.HasValue && normalized.RetroAchievementsGameIdOverride.Value > 0
                    ? normalized.RetroAchievementsGameIdOverride
                    : null;
            normalized.AchievementOrder = NormalizeAchievementOrder(normalized.AchievementOrder);
            normalized.AchievementCategoryOverrides = NormalizeCategoryOverrides(normalized.AchievementCategoryOverrides);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.ManualLink = NormalizeManualLink(normalized.ManualLink);
            return normalized;
        }

        public static GameCustomDataPortableFile NormalizePortable(GameCustomDataPortableFile data, Guid playniteGameId)
        {
            var normalized = data?.Clone() ?? new GameCustomDataPortableFile();
            normalized.SchemaVersion = CurrentSchemaVersion;
            normalized.PlayniteGameId = playniteGameId;
            normalized.UseSeparateLockedIconsOverride = normalized.UseSeparateLockedIconsOverride == true ? true : (bool?)null;
            normalized.ForceUseExophase = normalized.ForceUseExophase == true ? true : (bool?)null;
            normalized.ManualCapstoneApiName = NormalizeString(normalized.ManualCapstoneApiName);
            normalized.ExophaseSlugOverride = NormalizeString(normalized.ExophaseSlugOverride);
            normalized.RetroAchievementsGameIdOverride =
                normalized.RetroAchievementsGameIdOverride.HasValue && normalized.RetroAchievementsGameIdOverride.Value > 0
                    ? normalized.RetroAchievementsGameIdOverride
                    : null;
            normalized.AchievementOrder = NormalizeAchievementOrder(normalized.AchievementOrder);
            normalized.AchievementCategoryOverrides = NormalizeCategoryOverrides(normalized.AchievementCategoryOverrides);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.ManualLink = NormalizeManualLink(normalized.ManualLink);
            return normalized;
        }

        public static bool HasInternalData(GameCustomDataFile data)
        {
            if (data == null)
            {
                return false;
            }

            return data.ExcludedFromRefreshes == true ||
                   data.ExcludedFromSummaries == true ||
                   data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.ManualLink != null;
        }

        public static bool HasPortableData(GameCustomDataFile data)
        {
            if (data == null)
            {
                return false;
            }

            return data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.ManualLink != null;
        }

        public static bool HasPortableData(GameCustomDataPortableFile data)
        {
            if (data == null)
            {
                return false;
            }

            return data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.ManualLink != null;
        }

        public static GameCustomDataFile MergePreferExisting(GameCustomDataFile existing, GameCustomDataFile legacy)
        {
            if (existing == null)
            {
                return legacy?.Clone();
            }

            if (legacy == null)
            {
                return existing.Clone();
            }

            return new GameCustomDataFile
            {
                SchemaVersion = CurrentSchemaVersion,
                PlayniteGameId = existing.PlayniteGameId != Guid.Empty ? existing.PlayniteGameId : legacy.PlayniteGameId,
                ExcludedFromRefreshes = existing.ExcludedFromRefreshes ?? legacy.ExcludedFromRefreshes,
                ExcludedFromSummaries = existing.ExcludedFromSummaries ?? legacy.ExcludedFromSummaries,
                UseSeparateLockedIconsOverride = existing.UseSeparateLockedIconsOverride ?? legacy.UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = !string.IsNullOrWhiteSpace(existing.ManualCapstoneApiName)
                    ? existing.ManualCapstoneApiName
                    : legacy.ManualCapstoneApiName,
                AchievementOrder = existing.AchievementOrder != null && existing.AchievementOrder.Count > 0
                    ? new List<string>(existing.AchievementOrder)
                    : legacy.AchievementOrder != null && legacy.AchievementOrder.Count > 0
                        ? new List<string>(legacy.AchievementOrder)
                        : null,
                AchievementCategoryOverrides = existing.AchievementCategoryOverrides != null && existing.AchievementCategoryOverrides.Count > 0
                    ? new Dictionary<string, string>(existing.AchievementCategoryOverrides, StringComparer.OrdinalIgnoreCase)
                    : legacy.AchievementCategoryOverrides != null && legacy.AchievementCategoryOverrides.Count > 0
                        ? new Dictionary<string, string>(legacy.AchievementCategoryOverrides, StringComparer.OrdinalIgnoreCase)
                        : null,
                AchievementCategoryTypeOverrides = existing.AchievementCategoryTypeOverrides != null && existing.AchievementCategoryTypeOverrides.Count > 0
                    ? new Dictionary<string, string>(existing.AchievementCategoryTypeOverrides, StringComparer.OrdinalIgnoreCase)
                    : legacy.AchievementCategoryTypeOverrides != null && legacy.AchievementCategoryTypeOverrides.Count > 0
                        ? new Dictionary<string, string>(legacy.AchievementCategoryTypeOverrides, StringComparer.OrdinalIgnoreCase)
                        : null,
                AchievementUnlockedIconOverrides = existing.AchievementUnlockedIconOverrides != null && existing.AchievementUnlockedIconOverrides.Count > 0
                    ? new Dictionary<string, string>(existing.AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : legacy.AchievementUnlockedIconOverrides != null && legacy.AchievementUnlockedIconOverrides.Count > 0
                        ? new Dictionary<string, string>(legacy.AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                        : null,
                AchievementLockedIconOverrides = existing.AchievementLockedIconOverrides != null && existing.AchievementLockedIconOverrides.Count > 0
                    ? new Dictionary<string, string>(existing.AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : legacy.AchievementLockedIconOverrides != null && legacy.AchievementLockedIconOverrides.Count > 0
                        ? new Dictionary<string, string>(legacy.AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                        : null,
                RetroAchievementsGameIdOverride = existing.RetroAchievementsGameIdOverride ?? legacy.RetroAchievementsGameIdOverride,
                ForceUseExophase = existing.ForceUseExophase ?? legacy.ForceUseExophase,
                ExophaseSlugOverride = !string.IsNullOrWhiteSpace(existing.ExophaseSlugOverride)
                    ? existing.ExophaseSlugOverride
                    : legacy.ExophaseSlugOverride,
                ManualLink = existing.ManualLink?.Clone() ?? legacy.ManualLink?.Clone()
            };
        }

        private static string NormalizeString(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

        private static List<string> NormalizeAchievementOrder(IEnumerable<string> apiNames)
        {
            var normalized = AchievementOrderHelper.NormalizeApiNames(apiNames);
            return normalized.Count > 0 ? normalized : null;
        }

        private static Dictionary<string, string> NormalizeCategoryOverrides(Dictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var apiName = NormalizeString(pair.Key);
                var category = AchievementCategoryTypeHelper.NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                normalized[apiName] = category;
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static Dictionary<string, string> NormalizeCategoryTypeOverrides(Dictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var apiName = NormalizeString(pair.Key);
                var categoryType = AchievementCategoryTypeHelper.Normalize(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(categoryType))
                {
                    continue;
                }

                normalized[apiName] = categoryType;
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static Dictionary<string, string> NormalizeIconOverrides(Dictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var apiName = NormalizeString(pair.Key);
                var url = NormalizeString(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                normalized[apiName] = url;
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static ManualAchievementLink NormalizeManualLink(ManualAchievementLink link)
        {
            if (link == null)
            {
                return null;
            }

            var normalizedSourceKey = NormalizeString(link.SourceKey);
            var normalizedSourceGameId = NormalizeString(link.SourceGameId);
            if (string.IsNullOrWhiteSpace(normalizedSourceKey) || string.IsNullOrWhiteSpace(normalizedSourceGameId))
            {
                return null;
            }

            var compactStates = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (link.UnlockStates != null)
            {
                foreach (var pair in link.UnlockStates)
                {
                    var apiName = NormalizeString(pair.Key);
                    if (string.IsNullOrWhiteSpace(apiName) || !pair.Value)
                    {
                        continue;
                    }

                    compactStates[apiName] = true;
                }
            }

            var compactTimes = new Dictionary<string, DateTime?>(StringComparer.OrdinalIgnoreCase);
            if (link.UnlockTimes != null)
            {
                foreach (var pair in link.UnlockTimes)
                {
                    var apiName = NormalizeString(pair.Key);
                    if (string.IsNullOrWhiteSpace(apiName) || !pair.Value.HasValue)
                    {
                        continue;
                    }

                    compactTimes[apiName] = pair.Value.Value;
                    compactStates[apiName] = true;
                }
            }

            var createdUtc = link.CreatedUtc == default ? DateTime.UtcNow : link.CreatedUtc;
            var lastModifiedUtc = link.LastModifiedUtc == default ? createdUtc : link.LastModifiedUtc;
            return new ManualAchievementLink
            {
                SourceKey = normalizedSourceKey,
                SourceGameId = normalizedSourceGameId,
                UnlockStates = compactStates,
                UnlockTimes = compactTimes,
                AllowUnauthenticatedSchemaFetch = link.AllowUnauthenticatedSchemaFetch,
                CreatedUtc = createdUtc,
                LastModifiedUtc = lastModifiedUtc
            };
        }
    }
}
