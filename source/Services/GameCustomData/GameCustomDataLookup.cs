using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.Manual;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace PlayniteAchievements.Services.GameCustomData
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

        public List<string> AchievementCategoryOrder { get; set; } = new List<string>();

        public Dictionary<string, CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; } =
            new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);

        public GameSummaryCategoryData GameSummaryCategory { get; set; }

        public HashSet<string> FilteredAchievementApiNames { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SummaryFilteredAchievementApiNames { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> AchievementNotes { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class ResolvedOverviewGameCustomData
    {
        public bool ExcludedFromSummaries { get; set; }

        public bool UseSeparateLockedIcons { get; set; }
    }

    internal static class GameCustomDataLookup
    {
        public static ResolvedOverviewGameCustomData ResolveOverviewGameCustomData(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolved = ResolveGameCustomData(gameId, fallbackSettings, store);
            return new ResolvedOverviewGameCustomData
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
                                                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                AchievementCategoryOrder = hasCustomData
                    ? CloneCategoryOrder(customData?.AchievementCategoryOrder)
                    : new List<string>(),
                AchievementCategoryImageOverrides = hasCustomData
                    ? CloneCategoryImageOverrideMap(customData?.AchievementCategoryImageOverrides)
                    : new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase),
                GameSummaryCategory = hasCustomData
                    ? GameCustomDataNormalizer.NormalizeGameSummaryCategory(customData?.GameSummaryCategory)
                    : null,
                FilteredAchievementApiNames = hasCustomData
                    ? CloneApiNameSet(customData?.FilteredAchievementApiNames)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                SummaryFilteredAchievementApiNames = hasCustomData
                    ? CloneApiNameSet(customData?.SummaryFilteredAchievementApiNames)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                AchievementNotes = hasCustomData
                    ? CloneNoteMap(customData?.AchievementNotes)
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

        public static bool HasVisibleCustomization(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store))
            {
                return GameCustomDataNormalizer.HasVisibleCustomization(customData);
            }

            var legacyData = new GameCustomDataFile
            {
                PlayniteGameId = gameId,
                UseSeparateLockedIconsOverride = fallbackSettings?.SeparateLockedIconEnabledGameIds?.Contains(gameId) == true
                    ? true
                    : (bool?)null,
                ManualCapstoneApiName = fallbackSettings?.ManualCapstones != null &&
                                        fallbackSettings.ManualCapstones.TryGetValue(gameId, out var manualCapstone)
                    ? NormalizeValue(manualCapstone)
                    : null,
                AchievementOrder = fallbackSettings?.AchievementOrderOverrides != null &&
                                   fallbackSettings.AchievementOrderOverrides.TryGetValue(gameId, out var configuredOrder)
                    ? AchievementOrderHelper.NormalizeApiNames(configuredOrder)
                    : null,
                AchievementCategoryOverrides = fallbackSettings?.AchievementCategoryOverrides != null &&
                                               fallbackSettings.AchievementCategoryOverrides.TryGetValue(gameId, out var categoryOverrides)
                    ? CloneStringMap(categoryOverrides)
                    : null,
                AchievementCategoryTypeOverrides = fallbackSettings?.AchievementCategoryTypeOverrides != null &&
                                                   fallbackSettings.AchievementCategoryTypeOverrides.TryGetValue(gameId, out var categoryTypeOverrides)
                    ? CloneStringMap(categoryTypeOverrides)
                    : null
            };

            if (TryGetManualLink(
                gameId,
                out var manualLink,
                store,
                fallbackSettings: ProviderRegistry.Settings<ManualSettings>()))
            {
                legacyData.ManualLink = manualLink;
            }

            if (TryGetRetroAchievementsGameIdOverride(
                gameId,
                out var retroAchievementsGameId,
                store,
                fallbackSettings: ProviderRegistry.Settings<RetroAchievementsSettings>()))
            {
                legacyData.RetroAchievementsGameIdOverride = retroAchievementsGameId;
            }

            if (TryGetXeniaTitleIdOverride(gameId, out var xeniaTitleIdOverride, store))
            {
                legacyData.XeniaTitleIdOverride = xeniaTitleIdOverride;
            }

            if (TryGetShadPS4MatchIdOverride(gameId, out var shadPS4MatchIdOverride, store))
            {
                legacyData.ShadPS4MatchIdOverride = shadPS4MatchIdOverride;
            }

            var exophaseSettings = ProviderRegistry.Settings<ExophaseSettings>();
            if (IsExophaseIncluded(gameId, exophaseSettings, store))
            {
                legacyData.ForceUseExophase = true;
            }

            if (TryGetExophaseSlugOverride(gameId, out var exophaseSlugOverride, exophaseSettings, store))
            {
                legacyData.ExophaseSlugOverride = exophaseSlugOverride;
            }

            return GameCustomDataNormalizer.HasVisibleCustomization(
                GameCustomDataNormalizer.NormalizeInternal(legacyData, gameId));
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

        public static List<string> GetAchievementCategoryOrder(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementCategoryOrder;
        }

        public static Dictionary<string, CategoryImageOverrideData> GetAchievementCategoryImageOverrides(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementCategoryImageOverrides;
        }

        public static GameSummaryCategoryData GetGameSummaryCategory(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).GameSummaryCategory;
        }

        /// <summary>
        /// Lean read for per-game projection loops: one cached store load, no full
        /// resolved-object construction. The store clones the file on load, so the
        /// returned override map is safe to hand out.
        /// </summary>
        public static bool TryGetGameSummaryCategory(
            Guid gameId,
            out GameSummaryCategoryData selection,
            out IReadOnlyDictionary<string, CategoryImageOverrideData> imageOverrides,
            GameCustomDataStore store = null)
        {
            selection = null;
            imageOverrides = null;
            if (gameId == Guid.Empty || !TryLoad(gameId, out var customData, store))
            {
                return false;
            }

            selection = GameCustomDataNormalizer.NormalizeGameSummaryCategory(customData?.GameSummaryCategory);
            if (selection == null)
            {
                return false;
            }

            imageOverrides = customData?.AchievementCategoryImageOverrides;
            return true;
        }

        public static HashSet<string> GetFilteredAchievementApiNames(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).FilteredAchievementApiNames;
        }

        public static HashSet<string> GetSummaryFilteredAchievementApiNames(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).SummaryFilteredAchievementApiNames;
        }

        public static Dictionary<string, string> GetAchievementNotes(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementNotes;
        }

        public static string GetAchievementNote(
            Guid gameId,
            string apiName,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var normalizedApiName = NormalizeValue(apiName);
            if (string.IsNullOrWhiteSpace(normalizedApiName))
            {
                return null;
            }

            var notes = GetAchievementNotes(gameId, fallbackSettings, store);
            return notes != null && notes.TryGetValue(normalizedApiName, out var note)
                ? note
                : null;
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

        public static bool TryGetProviderOverride(
            Guid gameId,
            out ProviderOverrideData providerOverride,
            GameCustomDataStore store = null)
        {
            providerOverride = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryLoad(gameId, out var customData, store) &&
                customData?.ProviderOverride != null)
            {
                providerOverride = customData.ProviderOverride.Clone();
                return !string.IsNullOrWhiteSpace(providerOverride.ProviderKey);
            }

            return false;
        }

        /// <summary>
        /// Returns true when a provider override targets <paramref name="providerKey"/>, yielding its
        /// normalized value (which may be null for presence-only overrides). Used by providers that
        /// store their override value as-is and validate at the UI layer (no legacy fallback fields).
        /// </summary>
        public static bool TryGetProviderOverrideValue(
            Guid gameId,
            string providerKey,
            out string value,
            GameCustomDataStore store = null)
        {
            value = null;
            if (gameId == Guid.Empty || string.IsNullOrWhiteSpace(providerKey))
            {
                return false;
            }

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
            {
                value = NormalizeValue(providerOverride.Value);
                return true;
            }

            return false;
        }

        public static bool TryGetSteamAppIdOverride(
            Guid gameId,
            out int appIdOverride,
            GameCustomDataStore store = null)
        {
            appIdOverride = 0;
            return TryGetProviderOverride(gameId, out var providerOverride, store) &&
                   string.Equals(providerOverride.ProviderKey, "Steam", StringComparison.OrdinalIgnoreCase) &&
                   TryGetPositiveId(providerOverride.Value, out appIdOverride);
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

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase))
            {
                return TryGetPositiveId(providerOverride.Value, out gameIdOverride);
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

            if (TryGetProviderOverride(gameId, out var providerOverride, store))
            {
                return string.Equals(providerOverride.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase);
            }

            if (TryLoad(gameId, out var customData, store))
            {
                return customData?.ForceUseExophase == true;
            }

            return fallbackSettings?.IncludedGames?.Contains(gameId) == true;
        }

        public static bool TryGetXeniaTitleIdOverride(
            Guid gameId,
            out string titleIdOverride,
            GameCustomDataStore store = null)
        {
            titleIdOverride = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, "Xenia", StringComparison.OrdinalIgnoreCase))
            {
                titleIdOverride = XeniaTitleIdHelper.Normalize(providerOverride.Value);
                return !string.IsNullOrWhiteSpace(titleIdOverride);
            }

            if (TryLoad(gameId, out var customData, store))
            {
                titleIdOverride = XeniaTitleIdHelper.Normalize(customData?.XeniaTitleIdOverride);
                return !string.IsNullOrWhiteSpace(titleIdOverride);
            }

            return false;
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

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase))
            {
                slugOverride = NormalizeValue(providerOverride.Value);
                return !string.IsNullOrWhiteSpace(slugOverride);
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

        public static bool TryGetShadPS4MatchIdOverride(
            Guid gameId,
            out string matchIdOverride,
            GameCustomDataStore store = null)
        {
            matchIdOverride = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, "ShadPS4", StringComparison.OrdinalIgnoreCase))
            {
                matchIdOverride = ShadPS4MatchIdHelper.Normalize(providerOverride.Value);
                return !string.IsNullOrWhiteSpace(matchIdOverride);
            }

            if (TryLoad(gameId, out var customData, store))
            {
                matchIdOverride = ShadPS4MatchIdHelper.Normalize(customData?.ShadPS4MatchIdOverride);
                return !string.IsNullOrWhiteSpace(matchIdOverride);
            }

            return false;
        }

        public static bool TryGetRpcs3MatchIdOverride(
            Guid gameId,
            out string matchIdOverride,
            GameCustomDataStore store = null)
        {
            matchIdOverride = null;
            if (gameId == Guid.Empty)
            {
                return false;
            }

            if (TryGetProviderOverride(gameId, out var providerOverride, store) &&
                string.Equals(providerOverride.ProviderKey, "RPCS3", StringComparison.OrdinalIgnoreCase))
            {
                matchIdOverride = Rpcs3MatchIdHelper.Normalize(providerOverride.Value);
                return !string.IsNullOrWhiteSpace(matchIdOverride);
            }

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

        private static List<string> CloneCategoryOrder(IEnumerable<string> source)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return result;
            }

            foreach (var value in source)
            {
                var normalized = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(value);
                if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            return result;
        }

        private static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            var map = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(pair.Key);
                var art = NormalizeValue(pair.Value?.Art);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(art))
                {
                    continue;
                }

                map[key] = new CategoryImageOverrideData
                {
                    Art = art
                };
            }

            return map;
        }

        private static HashSet<string> CloneApiNameSet(IEnumerable<string> source)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return set;
            }

            foreach (var value in source)
            {
                var normalized = NormalizeValue(value);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    set.Add(normalized);
                }
            }

            return set;
        }

        private static Dictionary<string, string> CloneNoteMap(IReadOnlyDictionary<string, string> source)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = NormalizeValue(pair.Key);
                var value = AchievementNoteHelper.NormalizeNote(pair.Value);
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                map[key] = value;
            }

            return map;
        }

        private static bool TryGetPositiveId(string value, out int id)
        {
            return int.TryParse(
                       (value ?? string.Empty).Trim(),
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out id) &&
                   id > 0;
        }

        private static string NormalizeValue(string value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
        }

    }
}
