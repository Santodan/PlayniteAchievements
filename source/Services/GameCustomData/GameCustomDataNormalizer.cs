using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Xenia;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayniteAchievements.Services
{
    internal static class GameCustomDataNormalizer
    {
        internal const int CurrentSchemaVersion = 2;

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
            normalized.SteamAccountIdOverride = NormalizeString(normalized.SteamAccountIdOverride);
            normalized.XeniaTitleIdOverride = XeniaTitleIdHelper.Normalize(normalized.XeniaTitleIdOverride);
            normalized.ShadPS4MatchIdOverride = ShadPS4MatchIdHelper.Normalize(normalized.ShadPS4MatchIdOverride);
            normalized.RetroAchievementsGameIdOverride =
                normalized.RetroAchievementsGameIdOverride.HasValue && normalized.RetroAchievementsGameIdOverride.Value > 0
                    ? normalized.RetroAchievementsGameIdOverride
                    : null;
            normalized.ProviderOverride =
                NormalizeProviderOverride(normalized.ProviderOverride) ??
                ResolveLegacyProviderOverride(normalized);
            ClearLegacyProviderOverrideFields(normalized);
            normalized.AchievementOrder = NormalizeAchievementOrder(normalized.AchievementOrder);
            normalized.AchievementCategoryOverrides = NormalizeCategoryOverrides(normalized.AchievementCategoryOverrides);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.ViewAchievementsIconFetchEnabled = normalized.ViewAchievementsIconFetchEnabled ?? true;
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
            normalized.SteamAccountIdOverride = NormalizeString(normalized.SteamAccountIdOverride);
            normalized.XeniaTitleIdOverride = XeniaTitleIdHelper.Normalize(normalized.XeniaTitleIdOverride);
            normalized.ShadPS4MatchIdOverride = ShadPS4MatchIdHelper.Normalize(normalized.ShadPS4MatchIdOverride);
            normalized.RetroAchievementsGameIdOverride =
                normalized.RetroAchievementsGameIdOverride.HasValue && normalized.RetroAchievementsGameIdOverride.Value > 0
                    ? normalized.RetroAchievementsGameIdOverride
                    : null;
            normalized.ProviderOverride =
                NormalizeProviderOverride(normalized.ProviderOverride) ??
                ResolveLegacyProviderOverride(normalized);
            ClearLegacyProviderOverrideFields(normalized);
            normalized.AchievementOrder = NormalizeAchievementOrder(normalized.AchievementOrder);
            normalized.AchievementCategoryOverrides = NormalizeCategoryOverrides(normalized.AchievementCategoryOverrides);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.ViewAchievementsIconFetchEnabled = normalized.ViewAchievementsIconFetchEnabled ?? true;
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
                   data.ViewAchievementsIconFetchEnabled.HasValue ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.SteamAccountIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                    data.ManualLink != null;
        }

        public static bool HasPortableData(GameCustomDataFile data)
        {
            return HasVisibleCustomization(data);
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
                   data.ViewAchievementsIconFetchEnabled.HasValue ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.SteamAccountIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                    data.ManualLink != null;
        }

        public static bool HasVisibleCustomization(GameCustomDataFile data)
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
                   data.ViewAchievementsIconFetchEnabled.HasValue ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.SteamAccountIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
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
                ProviderOverride = ResolveEffectiveProviderOverride(existing) ??
                    ResolveEffectiveProviderOverride(legacy),
                ViewAchievementsIconFetchEnabled = existing.ViewAchievementsIconFetchEnabled ?? legacy.ViewAchievementsIconFetchEnabled,
                RetroAchievementsGameIdOverride = existing.RetroAchievementsGameIdOverride ?? legacy.RetroAchievementsGameIdOverride,
                SteamAccountIdOverride = !string.IsNullOrWhiteSpace(existing.SteamAccountIdOverride)
                    ? existing.SteamAccountIdOverride
                    : legacy.SteamAccountIdOverride,
                XeniaTitleIdOverride = !string.IsNullOrWhiteSpace(existing.XeniaTitleIdOverride)
                    ? existing.XeniaTitleIdOverride
                    : legacy.XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = !string.IsNullOrWhiteSpace(existing.ShadPS4MatchIdOverride)
                    ? existing.ShadPS4MatchIdOverride
                    : legacy.ShadPS4MatchIdOverride,
                ForceUseExophase = existing.ForceUseExophase ?? legacy.ForceUseExophase,
                ExophaseSlugOverride = !string.IsNullOrWhiteSpace(existing.ExophaseSlugOverride)
                    ? existing.ExophaseSlugOverride
                    : legacy.ExophaseSlugOverride,
                ManualLink = existing.ManualLink?.Clone() ?? legacy.ManualLink?.Clone()
            };
        }

        internal static ProviderOverrideData NormalizeProviderOverride(ProviderOverrideData providerOverride)
        {
            var providerKey = NormalizeProviderKey(providerOverride?.ProviderKey);
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return null;
            }

            var value = NormalizeString(providerOverride?.Value);
            switch (providerKey)
            {
                case "Steam":
                case "RetroAchievements":
                    return TryNormalizePositiveInteger(value, out var id)
                        ? new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = id.ToString(CultureInfo.InvariantCulture)
                        }
                        : null;

                case "Xenia":
                    var xeniaTitleId = XeniaTitleIdHelper.Normalize(value);
                    return string.IsNullOrWhiteSpace(xeniaTitleId)
                        ? null
                        : new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = xeniaTitleId
                        };

                case "ShadPS4":
                    var shadMatchId = ShadPS4MatchIdHelper.Normalize(value);
                    return string.IsNullOrWhiteSpace(shadMatchId)
                        ? null
                        : new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = shadMatchId
                        };

                case "RPCS3":
                    var rpcs3MatchId = Rpcs3MatchIdHelper.Normalize(value);
                    return string.IsNullOrWhiteSpace(rpcs3MatchId)
                        ? null
                        : new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = rpcs3MatchId
                        };

                case "Exophase":
                    return new ProviderOverrideData
                    {
                        ProviderKey = providerKey,
                        Value = value
                    };

                default:
                    return null;
            }
        }

        private static ProviderOverrideData ResolveEffectiveProviderOverride(GameCustomDataFile data)
        {
            return NormalizeProviderOverride(data?.ProviderOverride) ??
                   ResolveLegacyProviderOverride(data);
        }

        private static ProviderOverrideData ResolveEffectiveProviderOverride(GameCustomDataPortableFile data)
        {
            return NormalizeProviderOverride(data?.ProviderOverride) ??
                   ResolveLegacyProviderOverride(data);
        }

        private static ProviderOverrideData ResolveLegacyProviderOverride(GameCustomDataFile data)
        {
            if (data == null)
            {
                return null;
            }

            if (data.RetroAchievementsGameIdOverride.HasValue &&
                data.RetroAchievementsGameIdOverride.Value > 0)
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "RetroAchievements",
                    Value = data.RetroAchievementsGameIdOverride.Value.ToString(CultureInfo.InvariantCulture)
                };
            }

            if (!string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "Xenia",
                    Value = data.XeniaTitleIdOverride
                };
            }

            if (!string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "ShadPS4",
                    Value = data.ShadPS4MatchIdOverride
                };
            }

            if (data.ForceUseExophase == true ||
                !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "Exophase",
                    Value = NormalizeString(data.ExophaseSlugOverride)
                };
            }

            return null;
        }

        private static ProviderOverrideData ResolveLegacyProviderOverride(GameCustomDataPortableFile data)
        {
            if (data == null)
            {
                return null;
            }

            if (data.RetroAchievementsGameIdOverride.HasValue &&
                data.RetroAchievementsGameIdOverride.Value > 0)
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "RetroAchievements",
                    Value = data.RetroAchievementsGameIdOverride.Value.ToString(CultureInfo.InvariantCulture)
                };
            }

            if (!string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "Xenia",
                    Value = data.XeniaTitleIdOverride
                };
            }

            if (!string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "ShadPS4",
                    Value = data.ShadPS4MatchIdOverride
                };
            }

            if (data.ForceUseExophase == true ||
                !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride))
            {
                return new ProviderOverrideData
                {
                    ProviderKey = "Exophase",
                    Value = NormalizeString(data.ExophaseSlugOverride)
                };
            }

            return null;
        }

        private static void ClearLegacyProviderOverrideFields(GameCustomDataFile data)
        {
            if (data == null)
            {
                return;
            }

            data.RetroAchievementsGameIdOverride = null;
            data.XeniaTitleIdOverride = null;
            data.ShadPS4MatchIdOverride = null;
            data.ForceUseExophase = null;
            data.ExophaseSlugOverride = null;
        }

        private static void ClearLegacyProviderOverrideFields(GameCustomDataPortableFile data)
        {
            if (data == null)
            {
                return;
            }

            data.RetroAchievementsGameIdOverride = null;
            data.XeniaTitleIdOverride = null;
            data.ShadPS4MatchIdOverride = null;
            data.ForceUseExophase = null;
            data.ExophaseSlugOverride = null;
        }

        private static string NormalizeProviderKey(string providerKey)
        {
            var normalized = NormalizeString(providerKey);
            if (string.IsNullOrWhiteSpace(normalized) ||
                string.Equals(normalized, "None", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(normalized, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                return "Steam";
            }

            if (string.Equals(normalized, "RetroAchievements", StringComparison.OrdinalIgnoreCase))
            {
                return "RetroAchievements";
            }

            if (string.Equals(normalized, "Xenia", StringComparison.OrdinalIgnoreCase))
            {
                return "Xenia";
            }

            if (string.Equals(normalized, "ShadPS4", StringComparison.OrdinalIgnoreCase))
            {
                return "ShadPS4";
            }

            if (string.Equals(normalized, "RPCS3", StringComparison.OrdinalIgnoreCase))
            {
                return "RPCS3";
            }

            if (string.Equals(normalized, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                return "Exophase";
            }

            return null;
        }

        private static bool TryNormalizePositiveInteger(string value, out int id)
        {
            return int.TryParse(
                       NormalizeString(value),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out id) &&
                   id > 0;
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
