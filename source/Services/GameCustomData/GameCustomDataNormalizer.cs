using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayniteAchievements.Services.GameCustomData
{
    internal static class GameCustomDataNormalizer
    {
        internal const int CurrentSchemaVersion = 5;

        private sealed class LegacyFilterExtractionResult
        {
            public Dictionary<string, string> CategoryTypeOverrides { get; set; }

            public List<string> FilteredAchievementApiNames { get; set; }

            public List<string> SummaryFilteredAchievementApiNames { get; set; }
        }

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
            normalized.AchievementCategoryOrder = NormalizeCategoryOrder(normalized.AchievementCategoryOrder);
            normalized.AchievementCategoryImageOverrides = NormalizeCategoryImageOverrides(normalized.AchievementCategoryImageOverrides);
            normalized.GameSummaryCategory = NormalizeGameSummaryCategory(normalized.GameSummaryCategory);
            var extractedFilters = ExtractLegacyAchievementFilters(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementCategoryTypeOverrides = extractedFilters.CategoryTypeOverrides;
            normalized.FilteredAchievementApiNames = MergeApiNameLists(
                normalized.FilteredAchievementApiNames,
                extractedFilters.FilteredAchievementApiNames);
            normalized.SummaryFilteredAchievementApiNames = MergeApiNameLists(
                normalized.SummaryFilteredAchievementApiNames,
                extractedFilters.SummaryFilteredAchievementApiNames);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.AchievementNotes = AchievementNoteHelper.NormalizeNoteMap(normalized.AchievementNotes);
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
            normalized.AchievementCategoryOrder = NormalizeCategoryOrder(normalized.AchievementCategoryOrder);
            normalized.AchievementCategoryImageOverrides = NormalizeCategoryImageOverrides(normalized.AchievementCategoryImageOverrides);
            normalized.GameSummaryCategory = NormalizeGameSummaryCategory(normalized.GameSummaryCategory);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.FilteredAchievementApiNames = NormalizeAchievementApiNameList(normalized.FilteredAchievementApiNames);
            normalized.SummaryFilteredAchievementApiNames = NormalizeAchievementApiNameList(normalized.SummaryFilteredAchievementApiNames);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.AchievementNotes = AchievementNoteHelper.NormalizeNoteMap(normalized.AchievementNotes);
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
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
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
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
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
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   data.ProviderOverride != null ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
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
                AchievementCategoryOrder = existing.AchievementCategoryOrder != null && existing.AchievementCategoryOrder.Count > 0
                    ? new List<string>(existing.AchievementCategoryOrder)
                    : legacy.AchievementCategoryOrder != null && legacy.AchievementCategoryOrder.Count > 0
                        ? new List<string>(legacy.AchievementCategoryOrder)
                        : null,
                AchievementCategoryImageOverrides = existing.AchievementCategoryImageOverrides != null && existing.AchievementCategoryImageOverrides.Count > 0
                    ? GameCustomDataFile.CloneCategoryImageOverrideMap(existing.AchievementCategoryImageOverrides)
                    : legacy.AchievementCategoryImageOverrides != null && legacy.AchievementCategoryImageOverrides.Count > 0
                        ? GameCustomDataFile.CloneCategoryImageOverrideMap(legacy.AchievementCategoryImageOverrides)
                        : null,
                GameSummaryCategory = existing.GameSummaryCategory?.Clone() ?? legacy.GameSummaryCategory?.Clone(),
                FilteredAchievementApiNames = existing.FilteredAchievementApiNames != null && existing.FilteredAchievementApiNames.Count > 0
                    ? new List<string>(existing.FilteredAchievementApiNames)
                    : legacy.FilteredAchievementApiNames != null && legacy.FilteredAchievementApiNames.Count > 0
                        ? new List<string>(legacy.FilteredAchievementApiNames)
                        : null,
                SummaryFilteredAchievementApiNames = existing.SummaryFilteredAchievementApiNames != null && existing.SummaryFilteredAchievementApiNames.Count > 0
                    ? new List<string>(existing.SummaryFilteredAchievementApiNames)
                    : legacy.SummaryFilteredAchievementApiNames != null && legacy.SummaryFilteredAchievementApiNames.Count > 0
                        ? new List<string>(legacy.SummaryFilteredAchievementApiNames)
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
                AchievementNotes = existing.AchievementNotes != null && existing.AchievementNotes.Count > 0
                    ? new Dictionary<string, string>(existing.AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : legacy.AchievementNotes != null && legacy.AchievementNotes.Count > 0
                        ? new Dictionary<string, string>(legacy.AchievementNotes, StringComparer.OrdinalIgnoreCase)
                        : null,
                ProviderOverride = ResolveEffectiveProviderOverride(existing) ??
                    ResolveEffectiveProviderOverride(legacy),
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

                case "FFXIV":
                    return new ProviderOverrideData
                    {
                        ProviderKey = providerKey,
                        Value = null
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

            if (string.Equals(normalized, "FFXIV", StringComparison.OrdinalIgnoreCase))
            {
                return "FFXIV";
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

        private static List<string> NormalizeAchievementApiNameList(IEnumerable<string> apiNames)
        {
            var normalized = AchievementOrderHelper.NormalizeApiNames(apiNames);
            return normalized.Count > 0 ? normalized : null;
        }

        private static List<string> NormalizeCategoryOrder(IEnumerable<string> categoryLabels)
        {
            if (categoryLabels == null)
            {
                return null;
            }

            var normalized = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var categoryLabel in categoryLabels)
            {
                var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryLabel);
                if (string.IsNullOrWhiteSpace(label) || !seen.Add(label))
                {
                    continue;
                }

                normalized.Add(label);
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static List<string> MergeApiNameLists(IEnumerable<string> first, IEnumerable<string> second)
        {
            var merged = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddApiNames(first, merged, seen);
            AddApiNames(second, merged, seen);
            return merged.Count > 0 ? merged : null;
        }

        private static void AddApiNames(
            IEnumerable<string> apiNames,
            ICollection<string> target,
            ISet<string> seen)
        {
            if (target == null || seen == null)
            {
                return;
            }

            var normalized = AchievementOrderHelper.NormalizeApiNames(apiNames);
            foreach (var apiName in normalized)
            {
                if (seen.Add(apiName))
                {
                    target.Add(apiName);
                }
            }
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

        private static LegacyFilterExtractionResult ExtractLegacyAchievementFilters(Dictionary<string, string> values)
        {
            var result = new LegacyFilterExtractionResult
            {
                CategoryTypeOverrides = null,
                FilteredAchievementApiNames = null,
                SummaryFilteredAchievementApiNames = null
            };

            if (values == null)
            {
                return result;
            }

            var categoryTypeOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var filteredApiNames = new List<string>();
            var summaryFilteredApiNames = new List<string>();

            foreach (var pair in values)
            {
                var apiName = NormalizeString(pair.Key);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var categoryTypes = new List<string>();
                var isFiltered = false;
                var isSummaryFiltered = false;
                foreach (var token in SplitCategoryTypeTokens(pair.Value))
                {
                    if (IsLegacyFilteredCategoryType(token))
                    {
                        isFiltered = true;
                        continue;
                    }

                    if (IsLegacySummaryFilteredCategoryType(token))
                    {
                        isSummaryFiltered = true;
                        continue;
                    }

                    var normalizedToken = AchievementCategoryTypeHelper.Normalize(token);
                    foreach (var categoryType in AchievementCategoryTypeHelper.ParseValues(normalizedToken))
                    {
                        if (!categoryTypes.Contains(categoryType))
                        {
                            categoryTypes.Add(categoryType);
                        }
                    }
                }

                var normalizedCategoryTypes = AchievementCategoryTypeHelper.Combine(categoryTypes);
                if (!string.IsNullOrWhiteSpace(normalizedCategoryTypes))
                {
                    categoryTypeOverrides[apiName] = normalizedCategoryTypes;
                }

                if (isFiltered)
                {
                    filteredApiNames.Add(apiName);
                }
                else if (isSummaryFiltered)
                {
                    summaryFilteredApiNames.Add(apiName);
                }
            }

            result.CategoryTypeOverrides = categoryTypeOverrides.Count > 0 ? categoryTypeOverrides : null;
            result.FilteredAchievementApiNames = NormalizeAchievementApiNameList(filteredApiNames);
            result.SummaryFilteredAchievementApiNames = NormalizeAchievementApiNameList(summaryFilteredApiNames);
            return result;
        }

        private static IEnumerable<string> SplitCategoryTypeTokens(string value)
        {
            var normalized = NormalizeString(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            var separators = new[] { '|', ',', ';', '/' };
            foreach (var token in normalized.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = NormalizeString(token);
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    yield return trimmed;
                }
            }
        }

        private static bool IsLegacyFilteredCategoryType(string value)
        {
            var normalized = NormalizeLegacyCategoryTypeToken(value);
            return string.Equals(normalized, "ignored", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "ignore", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacySummaryFilteredCategoryType(string value)
        {
            var normalized = NormalizeLegacyCategoryTypeToken(value);
            return string.Equals(normalized, "summaryignored", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "summary_ignored", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "summary ignored", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "si", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLegacyCategoryTypeToken(string value)
        {
            return (value ?? string.Empty).Trim();
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

        private static Dictionary<string, CategoryImageOverrideData> NormalizeCategoryImageOverrides(
            Dictionary<string, CategoryImageOverrideData> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeString(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(category) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                normalized[category] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return normalized.Count > 0 ? normalized : null;
        }

        internal static GameSummaryCategoryData NormalizeGameSummaryCategory(GameSummaryCategoryData value)
        {
            var label = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(value?.Label);
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            var providerLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(value?.ProviderLabel);
            return new GameSummaryCategoryData
            {
                Label = label,
                ProviderLabel = string.IsNullOrWhiteSpace(providerLabel) ? label : providerLabel
            };
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
