using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Settings;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services
{
    internal sealed class ResolvedGameCustomData
    {
        public static ResolvedGameCustomData Empty { get; } = new ResolvedGameCustomData();

        public bool ExcludedFromRefreshes { get; set; }

        public bool ExcludedFromSummaries { get; set; }

        public bool UseSeparateLockedIcons { get; set; }

        public string ManualCapstoneApiName { get; set; }

        public List<string> AchievementOrder { get; set; } = new List<string>();

        public Dictionary<string, string> AchievementCategoryOverrides { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AchievementCategoryTypeOverrides { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ResolvedSidebarGameCustomData
    {
        public bool ExcludedFromSummaries { get; set; }

        public bool UseSeparateLockedIcons { get; set; }
    }

    internal static class GameCustomDataLookup
    {
        public static ResolvedSidebarGameCustomData ResolveSidebarGameCustomData(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolved = ResolveGameCustomData(gameId, fallbackSettings, store);
            return new ResolvedSidebarGameCustomData
            {
                ExcludedFromSummaries = resolved.ExcludedFromSummaries,
                UseSeparateLockedIcons = resolved.UseSeparateLockedIcons
            };
        }

        public static ResolvedGameCustomData ResolveGameCustomData(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty)
            {
                return new ResolvedGameCustomData
                {
                    UseSeparateLockedIcons = fallbackSettings?.UseSeparateLockedIconsWhenAvailable == true
                };
            }

            var hasCustomData = TryLoad(gameId, out var customData, store);
            var resolved = new ResolvedGameCustomData
            {
                ExcludedFromRefreshes = hasCustomData
                    ? customData?.ExcludedFromRefreshes == true
                    : fallbackSettings?.ExcludedGameIds?.Contains(gameId) == true,
                ExcludedFromSummaries = hasCustomData
                    ? customData?.ExcludedFromSummaries == true
                    : fallbackSettings?.ExcludedFromSummariesGameIds?.Contains(gameId) == true,
                UseSeparateLockedIcons = fallbackSettings?.UseSeparateLockedIconsWhenAvailable == true ||
                    (hasCustomData
                        ? customData?.UseSeparateLockedIconsOverride == true
                        : fallbackSettings?.SeparateLockedIconEnabledGameIds?.Contains(gameId) == true),
                ManualCapstoneApiName = hasCustomData
                    ? customData?.ManualCapstoneApiName
                                        : fallbackSettings?.ManualCapstones != null &&
                                            fallbackSettings.ManualCapstones.TryGetValue(gameId, out var capstone)
                                                ? NormalizeValue(capstone)
                                                : null,
                AchievementOrder = hasCustomData
                    ? customData?.AchievementOrder ?? new List<string>()
                                        : fallbackSettings?.AchievementOrderOverrides != null &&
                                            fallbackSettings.AchievementOrderOverrides.TryGetValue(gameId, out var configuredOrder)
                                                ? AchievementOrderHelper.NormalizeApiNames(configuredOrder)
                                                : new List<string>(),
                AchievementCategoryOverrides = hasCustomData
                    ? customData?.AchievementCategoryOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                        : fallbackSettings?.AchievementCategoryOverrides != null &&
                                            fallbackSettings.AchievementCategoryOverrides.TryGetValue(gameId, out var configuredCategoryOverrides)
                                                ? CloneStringMap(configuredCategoryOverrides)
                                                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                AchievementCategoryTypeOverrides = hasCustomData
                    ? customData?.AchievementCategoryTypeOverrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                                        : fallbackSettings?.AchievementCategoryTypeOverrides != null &&
                                            fallbackSettings.AchievementCategoryTypeOverrides.TryGetValue(gameId, out var configuredCategoryTypeOverrides)
                                                ? CloneStringMap(configuredCategoryTypeOverrides)
                                                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            return resolved;
        }

        public static bool IsExcludedFromRefreshes(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).ExcludedFromRefreshes;
        }

        public static bool IsExcludedFromSummaries(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).ExcludedFromSummaries;
        }

        public static HashSet<Guid> GetExcludedRefreshGameIds(
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolvedStore = ResolveStore(store);
            if (resolvedStore == null)
            {
                return fallbackSettings?.ExcludedGameIds != null
                    ? new HashSet<Guid>(fallbackSettings.ExcludedGameIds)
                    : new HashSet<Guid>();
            }

            return resolvedStore.GetExcludedRefreshGameIds(fallbackSettings?.ExcludedGameIds);
        }

        public static HashSet<Guid> GetExcludedSummaryGameIds(
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolvedStore = ResolveStore(store);
            if (resolvedStore == null)
            {
                return fallbackSettings?.ExcludedFromSummariesGameIds != null
                    ? new HashSet<Guid>(fallbackSettings.ExcludedFromSummariesGameIds)
                    : new HashSet<Guid>();
            }

            return resolvedStore.GetExcludedSummaryGameIds(fallbackSettings?.ExcludedFromSummariesGameIds);
        }

        public static bool ShouldUseSeparateLockedIcons(
            Guid? gameId,
            PersistedSettings settings,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId ?? Guid.Empty, settings, store).UseSeparateLockedIcons;
        }

        public static string GetManualCapstone(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).ManualCapstoneApiName;
        }

        public static List<string> GetAchievementOrder(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementOrder;
        }

        public static Dictionary<string, string> GetAchievementCategoryOverrides(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementCategoryOverrides;
        }

        public static Dictionary<string, string> GetAchievementCategoryTypeOverrides(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementCategoryTypeOverrides;
        }

        public static Dictionary<string, string> GetAchievementUnlockedIconOverrides(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty || !TryLoad(gameId, out var customData, store))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return CloneStringMap(customData?.AchievementUnlockedIconOverrides);
        }

        public static Dictionary<string, string> GetAchievementLockedIconOverrides(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty || !TryLoad(gameId, out var customData, store))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            return CloneStringMap(customData?.AchievementLockedIconOverrides);
        }

        public static bool TryGetManualLink(
            Guid gameId,
            out ManualAchievementLink link,
            GameCustomDataStore store = null,
            ManualSettings fallbackSettings = null)
        {
            link = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store) &&
                customData?.ManualLink != null)
            {
                link = customData.ManualLink.Clone();
                return true;
            }

            return fallbackSettings?.AchievementLinks != null &&
                   fallbackSettings.AchievementLinks.TryGetValue(gameId, out link) &&
                   link != null;
        }

        public static bool TryGetRetroAchievementsGameIdOverride(
            Guid gameId,
            out int gameIdOverride,
            GameCustomDataStore store = null,
            RetroAchievementsSettings fallbackSettings = null)
        {
            gameIdOverride = 0;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store) &&
                customData?.RetroAchievementsGameIdOverride.HasValue == true)
            {
                gameIdOverride = customData.RetroAchievementsGameIdOverride.Value;
                return gameIdOverride > 0;
            }

            return fallbackSettings?.RaGameIdOverrides != null &&
                   fallbackSettings.RaGameIdOverrides.TryGetValue(gameId, out gameIdOverride);
        }

        public static bool IsExophaseIncluded(
            Guid gameId,
            ExophaseSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store))
            {
                return customData?.ForceUseExophase == true;
            }

            return fallbackSettings?.IncludedGames?.Contains(gameId) == true;
        }

        public static bool TryGetExophaseSlugOverride(
            Guid gameId,
            out string slugOverride,
            ExophaseSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            slugOverride = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store))
            {
                slugOverride = NormalizeValue(customData?.ExophaseSlugOverride);
                return !string.IsNullOrWhiteSpace(slugOverride);
            }

            if (fallbackSettings?.SlugOverrides != null &&
                fallbackSettings.SlugOverrides.TryGetValue(gameId, out slugOverride))
            {
                slugOverride = NormalizeValue(slugOverride);
                return !string.IsNullOrWhiteSpace(slugOverride);
            }

            slugOverride = null;
            return false;
        }

        private static bool TryLoad(Guid gameId, out GameCustomDataFile customData, GameCustomDataStore store = null)
        {
            customData = null;
            var resolvedStore = ResolveStore(store);
            return resolvedStore != null && resolvedStore.TryLoad(gameId, out customData);
        }

        private static GameCustomDataStore ResolveStore(GameCustomDataStore store)
        {
            return store ?? PlayniteAchievementsPlugin.Instance?.GameCustomDataStore;
        }

        private static Dictionary<string, string> CloneStringMap(IReadOnlyDictionary<string, string> source)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = NormalizeValue(pair.Key);
                var value = NormalizeValue(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                map[key] = value;
            }

            return map;
        }

        private static string NormalizeValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

    }
}
