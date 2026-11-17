using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;
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
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    internal sealed class ResolvedGameCustomData
    {
        public static ResolvedGameCustomData Empty { get; } = new ResolvedGameCustomData();

        public bool ExcludedFromRefreshes { get; set; }

        public bool ExcludedFromSummaries { get; set; }

        public bool UseSeparateLockedIcons { get; set; }

        /// <inheritdoc cref="Models.Settings.GameCustomDataFile.CapstonesMaterialized"/>
        public bool CapstonesMaterialized { get; set; }

        /// <inheritdoc cref="Models.Settings.GameCustomDataFile.Capstones"/>
        public List<CapstoneAssignment> Capstones { get; set; } = new List<CapstoneAssignment>();

        /// <summary>
        /// True when the game's achievements come from a manual link. Such a game records its own
        /// unlock state, so the per-achievement unlock-time override does not apply to it.
        /// </summary>
        public bool HasManualLink { get; set; }

        public List<string> AchievementOrder { get; set; } = new List<string>();

        public List<string> AchievementCategoryOrder { get; set; } = new List<string>();

        public Dictionary<string, CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; } =
            new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);

        /// <inheritdoc cref="Models.Settings.GameCustomDataFile.AchievementCategoryKinds"/>
        public Dictionary<string, string> AchievementCategoryKinds { get; set; } =
            new Dictionary<string, string>(CategoryPathHelper.LabelComparer);

        /// <inheritdoc cref="Models.Settings.GameCustomDataFile.AchievementTags"/>
        public List<AchievementTagDefinition> AchievementTags { get; set; } = new List<AchievementTagDefinition>();

        /// <summary>
        /// The kind stored for <paramref name="categoryPath"/>: a kind, <see cref="CategoryKindHelper.None"/>
        /// for a category set to have none, or null when the provider decides.
        /// </summary>
        public string GetStoredCategoryKind(string categoryPath)
        {
            var key = CategoryPathHelper.NormalizePath(categoryPath);
            return key != null && AchievementCategoryKinds != null &&
                   AchievementCategoryKinds.TryGetValue(key, out var kind) &&
                   !string.IsNullOrWhiteSpace(kind)
                ? kind
                : null;
        }

        public GameSummaryCategoryData GameSummaryCategory { get; set; }

        public HashSet<string> FilteredAchievementApiNames { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> SummaryFilteredAchievementApiNames { get; set; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Goal achievements in user-defined order. A list rather than a set because position
        /// carries the goal order.
        /// </summary>
        public List<string> GoalAchievementApiNames { get; set; } = new List<string>();

        /// <summary>Per-achievement customization, keyed by ApiName.</summary>
        public Dictionary<string, AchievementOverride> AchievementOverrides { get; set; } =
            new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);

        /// <summary>One override field of each customized achievement, keyed by ApiName.</summary>
        public Dictionary<string, string> Project(Func<AchievementOverride, string> field) =>
            AchievementOverride.Project(AchievementOverrides, field);

        public List<CustomAchievementDefinition> CustomAchievements { get; set; } =
            new List<CustomAchievementDefinition>();
    }

    internal static class GameCustomDataLookup
    {
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

            // Read off the cached record instead of a clone of it. This runs once per game
            // across the whole library during hydration, and it already rebuilds every
            // collection it hands out -- so going through TryLoad meant a full deep copy of the
            // record (including a per-entry copy of every override) followed by a second copy
            // of most of it. On a library where every game is customized that doubling is the
            // dominant cost of a hydration pass.
            //
            // Everything the builder returns is freshly allocated, so nothing escapes into the
            // cache; the four collections that used to ride out as references on the discarded
            // clone are copied explicitly there.
            var resolvedStore = ResolveStore(store);
            var resolved = resolvedStore?.QueryGame(
                gameId,
                customData => customData == null ? null : BuildResolvedFromRecord(customData, fallbackSettings));

            return resolved ?? new ResolvedGameCustomData
            {
                UseSeparateLockedIcons = fallbackSettings?.UseSeparateLockedIconsWhenAvailable == true
            };
        }

        /// <summary>
        /// Resolves one custom data record against the global settings fallbacks, without reading
        /// the store. Callers that hold a record not stored for any game (a package preview) use it
        /// directly.
        /// </summary>
        internal static ResolvedGameCustomData BuildResolvedFromRecord(
            GameCustomDataFile customData,
            PersistedSettings fallbackSettings)
        {
            var resolved = new ResolvedGameCustomData
            {
                ExcludedFromRefreshes = customData.ExcludedFromRefreshes == true,
                ExcludedFromSummaries = customData.ExcludedFromSummaries == true,
                UseSeparateLockedIcons = fallbackSettings?.UseSeparateLockedIconsWhenAvailable == true ||
                    customData.UseSeparateLockedIconsOverride == true,
                HasManualLink = customData.ManualLink != null,
                CapstonesMaterialized = customData.CapstonesMaterialized,

                // Copied rather than referenced: the record here is the cached instance, not a
                // clone of it, so handing out its own collections would let a caller mutate the
                // cache.
                Capstones = customData.Capstones != null
                    ? customData.Capstones.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : new List<CapstoneAssignment>(),
                AchievementOrder = customData.AchievementOrder != null
                    ? new List<string>(customData.AchievementOrder)
                    : new List<string>(),
                AchievementCategoryOrder = CloneCategoryOrder(customData.AchievementCategoryOrder),
                AchievementCategoryImageOverrides =
                    CloneCategoryImageOverrideMap(customData.AchievementCategoryImageOverrides),
                AchievementCategoryKinds = Models.Settings.GameCustomDataFile.CloneCategoryKindMap(customData.AchievementCategoryKinds) ??
                    new Dictionary<string, string>(CategoryPathHelper.LabelComparer),
                AchievementTags = Models.Settings.GameCustomDataFile.CloneTagList(customData.AchievementTags) ??
                    new List<AchievementTagDefinition>(),
                GameSummaryCategory =
                    GameCustomDataNormalizer.NormalizeGameSummaryCategory(customData.GameSummaryCategory),
                FilteredAchievementApiNames = CloneApiNameSet(customData.FilteredAchievementApiNames),
                SummaryFilteredAchievementApiNames = CloneApiNameSet(customData.SummaryFilteredAchievementApiNames),
                GoalAchievementApiNames = AchievementOrderHelper.NormalizeApiNames(customData.GoalAchievementApiNames),
                CustomAchievements = CloneCustomAchievements(customData.CustomAchievements)
            };

            resolved.AchievementOverrides = customData.AchievementOverrides != null
                ? CloneOverrideMap(customData.AchievementOverrides)
                : new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);

            return resolved;
        }

        // Both read one flag, so neither builds the resolved projection: that allocates and
        // copies every collection the record holds, which is a great deal of work to answer a
        // bool on a heavily customized game.
        public static bool IsExcludedFromRefreshes(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ReadGameFlag(
                gameId,
                store,
                customData => customData.ExcludedFromRefreshes == true);
        }

        public static bool IsExcludedFromSummaries(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ReadGameFlag(
                gameId,
                store,
                customData => customData.ExcludedFromSummaries == true);
        }

        /// <summary>Whether the game reads its unlocks from a linked file.</summary>
        public static bool HasLinkedFile(Guid gameId, GameCustomDataStore store = null)
        {
            return ReadGameFlag(gameId, store, customData => customData.LinkedFile != null);
        }

        /// <summary>
        /// Reads one flag off a game's stored record; false when the game has none.
        /// </summary>
        private static bool ReadGameFlag(
            Guid gameId,
            GameCustomDataStore store,
            Func<GameCustomDataFile, bool> fromRecord)
        {
            if (gameId != Guid.Empty)
            {
                var resolvedStore = ResolveStore(store);
                var stored = resolvedStore?.QueryGame(
                    gameId,
                    customData => customData == null ? (bool?)null : fromRecord(customData));
                if (stored.HasValue)
                {
                    return stored.Value;
                }
            }

            return false;
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

            // Read, not copied: this answers a bool, and tag sync asks it once per game across
            // the library, so going through TryLoad deep-cloned every customized game's record
            // - overrides, notes and all - to look at a handful of fields.
            var resolvedStore = ResolveStore(store);
            if (resolvedStore != null)
            {
                var stored = resolvedStore.QueryGame(
                    gameId,
                    customData => customData == null
                        ? (bool?)null
                        : GameCustomDataNormalizer.HasVisibleCustomization(customData));
                if (stored.HasValue)
                {
                    return stored.Value;
                }
            }

            return GameCustomDataNormalizer.HasVisibleCustomization(
                BuildLegacyRecord(gameId, fallbackSettings, store));
        }

        /// <summary>
        /// The <see cref="CustomizationArea"/>s a game's stored customization falls into, read
        /// the same way as <see cref="HasVisibleCustomization"/>: from the store without copying
        /// the record, else from the legacy settings.
        /// </summary>
        public static CustomizationArea GetCustomizationAreas(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty)
            {
                return CustomizationArea.None;
            }

            var resolvedStore = ResolveStore(store);
            if (resolvedStore != null)
            {
                var stored = resolvedStore.QueryGame(
                    gameId,
                    customData => customData == null
                        ? (CustomizationArea?)null
                        : CustomizationAreaClassifier.Classify(customData));
                if (stored.HasValue)
                {
                    return stored.Value;
                }
            }

            return CustomizationAreaClassifier.Classify(BuildLegacyRecord(gameId, fallbackSettings, store));
        }

        /// <summary>
        /// A record assembled from the provider settings that still hold per-game values (a
        /// transient manual link, provider id overrides), for games the store has no record for.
        /// </summary>
        private static GameCustomDataFile BuildLegacyRecord(
            Guid gameId,
            PersistedSettings fallbackSettings,
            GameCustomDataStore store)
        {
            var legacyData = new GameCustomDataFile
            {
                PlayniteGameId = gameId
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

            return GameCustomDataNormalizer.NormalizeInternal(legacyData, gameId);
        }

        public static HashSet<Guid> GetExcludedRefreshGameIds(
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolvedStore = ResolveStore(store);
            return resolvedStore?.GetExcludedRefreshGameIds() ?? new HashSet<Guid>();
        }

        public static HashSet<Guid> GetExcludedSummaryGameIds(
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            var resolvedStore = ResolveStore(store);
            return resolvedStore?.GetExcludedSummaryGameIds() ?? new HashSet<Guid>();
        }

        /// <remarks>
        /// Reads the one flag rather than building the resolved projection, which allocates and
        /// copies every collection the record holds. The appearance snapshot falls back to this
        /// per achievement row whenever the caller has no resolved value in hand, so a full
        /// projection build here was paid per row.
        /// </remarks>
        public static bool ShouldUseSeparateLockedIcons(
            Guid? gameId,
            PersistedSettings settings,
            GameCustomDataStore store = null)
        {
            // The global setting wins on its own, exactly as the resolved projection has it.
            if (settings?.UseSeparateLockedIconsWhenAvailable == true)
            {
                return true;
            }

            var resolvedGameId = gameId ?? Guid.Empty;
            return ReadGameFlag(
                resolvedGameId,
                store,
                customData => customData.UseSeparateLockedIconsOverride == true);
        }

        /// <summary>
        /// A game's stored capstones. An untouched game returns an unmaterialized set, which tells
        /// the resolver to seed from the provider instead.
        /// </summary>
        /// <remarks>
        /// Reads the two fields it needs rather than building the resolved projection, which
        /// allocates and copies every collection the record holds. Tag sync asks this once per
        /// game across the library.
        /// </remarks>
        public static CapstoneSet GetCapstoneSet(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            if (gameId != Guid.Empty)
            {
                var resolvedStore = ResolveStore(store);
                var stored = resolvedStore?.QueryGame<CapstoneSet?>(
                    gameId,
                    customData => customData == null
                        ? (CapstoneSet?)null
                        : new CapstoneSet(
                            customData.CapstonesMaterialized,
                            customData.Capstones != null
                                ? customData.Capstones.ConvertAll(item => item?.Clone())
                                    .FindAll(item => item != null)
                                : new List<CapstoneAssignment>()));
                if (stored.HasValue)
                {
                    return stored.Value;
                }
            }

            return new CapstoneSet(false, new List<CapstoneAssignment>());
        }

        public static Dictionary<string, string> GetAchievementCategoryOverrides(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).Project(o => o.Category);
        }

        /// <summary>
        /// The tag override of each customized achievement, keyed by ApiName: its tags, or "" when
        /// the user removed every tag.
        /// </summary>
        public static Dictionary<string, string> GetAchievementTagOverrides(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return BuildTagOverrideMap(ResolveGameCustomData(gameId, fallbackSettings, store));
        }

        /// <inheritdoc cref="GetAchievementTagOverrides"/>
        public static Dictionary<string, string> BuildTagOverrideMap(ResolvedGameCustomData resolved)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var overrides = resolved?.AchievementOverrides;
            if (overrides == null)
            {
                return map;
            }

            foreach (var pair in overrides)
            {
                var apiName = (pair.Key ?? string.Empty).Trim();
                if (apiName.Length == 0 || pair.Value == null)
                {
                    continue;
                }

                if (pair.Value.ClearTags)
                {
                    map[apiName] = string.Empty;
                }
                else if (pair.Value.Tags != null)
                {
                    map[apiName] = pair.Value.Tags;
                }
            }

            return map;
        }

        public static Dictionary<string, string> GetAchievementCategoryKinds(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).AchievementCategoryKinds;
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
            var resolvedStore = ResolveStore(store);
            if (gameId == Guid.Empty || resolvedStore == null)
            {
                return false;
            }

            // Read off the cached record rather than a deep clone of it. The whole-library
            // overview build resolves summary art once per game, and going through TryLoad
            // deep-copied every customized game's overrides, notes and icon maps to reach two
            // fields. Both values handed back are freshly built, so nothing escapes into the
            // cache: the normalizer returns a new instance, and the image map is copied.
            var result = resolvedStore.QueryGame(
                gameId,
                customData =>
                {
                    var normalized = GameCustomDataNormalizer.NormalizeGameSummaryCategory(
                        customData?.GameSummaryCategory);
                    return normalized == null
                        ? null
                        : Tuple.Create(
                            normalized,
                            GameCustomDataFile.CloneCategoryImageOverrideMap(
                                customData?.AchievementCategoryImageOverrides));
                });

            if (result == null)
            {
                return false;
            }

            selection = result.Item1;
            imageOverrides = result.Item2;
            return true;
        }

        /// <summary>
        /// A game's category ordering and category art overrides, read without resolving the
        /// whole customization record. Both come back as fresh copies; either may be null when
        /// the game stores none.
        /// </summary>
        /// <remarks>
        /// <see cref="ResolveGameCustomData"/> deep-clones the stored record and then rebuilds
        /// roughly ten collections off it, which is a great deal of copying when the caller
        /// wants two of them. The overview's achievement materialization does this once per
        /// distinct game, so on a library where every game is customized it was paying that
        /// per game. Neither field has a legacy-settings fallback -- a game with no stored
        /// record simply has neither -- so reading them directly is equivalent.
        /// </remarks>
        public static void GetCategoryMetadata(
            Guid gameId,
            out List<string> categoryOrder,
            out Dictionary<string, CategoryImageOverrideData> categoryImageOverrides,
            GameCustomDataStore store = null)
        {
            categoryOrder = null;
            categoryImageOverrides = null;

            var resolvedStore = ResolveStore(store);
            if (gameId == Guid.Empty || resolvedStore == null)
            {
                return;
            }

            // Empty comes back as null, matching what the caller derived from the resolved
            // record: an absent ordering and an absent art map are the same thing as empty ones.
            var result = resolvedStore.QueryGame(
                gameId,
                customData =>
                {
                    var order = CloneCategoryOrder(customData?.AchievementCategoryOrder);
                    var art = GameCustomDataFile.CloneCategoryImageOverrideMap(
                        customData?.AchievementCategoryImageOverrides);
                    return Tuple.Create(
                        order != null && order.Count > 0 ? order : null,
                        art != null && art.Count > 0 ? art : null);
                });

            if (result == null)
            {
                return;
            }

            categoryOrder = result.Item1;
            categoryImageOverrides = result.Item2;
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

        public static List<CustomAchievementDefinition> GetCustomAchievements(
            Guid gameId,
            PersistedSettings fallbackSettings = null,
            GameCustomDataStore store = null)
        {
            return ResolveGameCustomData(gameId, fallbackSettings, store).CustomAchievements;
        }

        public static bool HasAnyCustomAchievements(GameCustomDataStore store = null)
        {
            var resolvedStore = ResolveStore(store);
            if (resolvedStore == null)
            {
                return false;
            }

            // Asked over the cached records rather than a cloned copy of them: this is a bool,
            // and LoadAll would deep-copy every customized game in the library to produce it.
            return resolvedStore.QueryAll(
                rows => rows.Any(CustomAchievementProjectionService.HasCustomAchievements));
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

            var notes = ResolveGameCustomData(gameId, fallbackSettings, store).Project(o => o.Note);
            return notes != null && notes.TryGetValue(normalizedApiName, out var note)
                ? note
                : null;
        }

        // Both read one map off the record rather than cloning the record to reach it. They are
        // called back to back once per game during hydration -- immediately after the same
        // record was already resolved -- so going through TryLoad meant two more deep copies of
        // every override, note and capstone the game holds, per game.
        public static Dictionary<string, string> GetAchievementUnlockedIconOverrides(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            return ReadIconOverrideMap(gameId, store, o => o.UnlockedIconPath);
        }

        public static Dictionary<string, string> GetAchievementLockedIconOverrides(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            return ReadIconOverrideMap(gameId, store, o => o.LockedIconPath);
        }

        private static Dictionary<string, string> ReadIconOverrideMap(
            Guid gameId,
            GameCustomDataStore store,
            Func<AchievementOverride, string> field)
        {
            if (gameId != Guid.Empty)
            {
                var resolvedStore = ResolveStore(store);
                var map = resolvedStore?.QueryGame(
                    gameId,
                    customData => customData == null ? null : AchievementOverride.Project(customData.AchievementOverrides, field));
                if (map != null)
                {
                    return map;
                }
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

        public static bool IsViewAchievementsIconFetchEnabled(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty ||
                !PlayniteAchievements.Providers.Local.LocalSavesProvider.TryGetCustomSchemaEnabledOverride(
                    gameId,
                    out var customSchemaEnabled) ||
                !customSchemaEnabled)
            {
                return false;
            }

            return !TryLoad(gameId, out var customData, store) ||
                   customData?.ViewAchievementsIconFetchEnabled != false;
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

        public static IReadOnlyList<int> GetRetroAchievementsSelectedSubsetGameIds(
            Guid gameId,
            GameCustomDataStore store = null)
        {
            if (gameId == Guid.Empty ||
                !TryLoad(gameId, out var customData, store) ||
                customData?.RetroAchievementsSelectedSubsetGameIds == null)
            {
                return Array.Empty<int>();
            }

            return customData.RetroAchievementsSelectedSubsetGameIds
                .Where(value => value > 0)
                .Distinct()
                .ToList();
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

        private static List<string> CloneCategoryOrder(IEnumerable<string> source)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
            if (source == null)
            {
                return result;
            }

            foreach (var value in source)
            {
                var normalized = AchievementCategoryHelper.NormalizeCategoryOrDefault(value);
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
            var map = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = AchievementCategoryHelper.NormalizeCategoryOrDefault(pair.Key);
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

        private static Dictionary<string, AchievementOverride> CloneOverrideMap(
            IReadOnlyDictionary<string, AchievementOverride> source)
        {
            var map = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            if (source == null)
            {
                return map;
            }

            foreach (var pair in source)
            {
                var key = NormalizeValue(pair.Key);
                if (string.IsNullOrWhiteSpace(key) || pair.Value == null || pair.Value.IsEmpty)
                {
                    continue;
                }

                map[key] = pair.Value.Clone();
            }

            return map;
        }

        private static List<CustomAchievementDefinition> CloneCustomAchievements(
            IEnumerable<CustomAchievementDefinition> source)
        {
            return source == null
                ? new List<CustomAchievementDefinition>()
                : source
                    .Select(definition => definition?.Clone())
                    .Where(definition => definition != null)
                    .ToList();
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
