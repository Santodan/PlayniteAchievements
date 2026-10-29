using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers.RPCS3;
using PlayniteAchievements.Services.CustomProviders;
using PlayniteAchievements.Providers.ShadPS4;
using PlayniteAchievements.Providers.Xenia;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    internal static class GameCustomDataNormalizer
    {
        // v7: notification badge images and header texts moved onto each surface style, and
        // portable files became zip-only under the bare .pa extension.
        // v8: the per-achievement parallel maps (category, category type, note, both icon
        // overrides) fold into one AchievementOverride record per ApiName, which also carries the
        // newly editable title, description, points and trophy type. The legacy maps are still
        // written as a mirror until every consumer reads the record.
        // v9: the category type splits into a kind stored per category (AchievementCategoryKinds)
        // and per-achievement tags. A pre-v9 type override is split once into the record's legacy
        // kind and its tags, and the category type map is no longer written.
        internal const int CurrentSchemaVersion = 9;

        private const int CategoryTypeSplitSchemaVersion = 9;

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
            var splitLegacyTypes = (data?.SchemaVersion ?? CurrentSchemaVersion) < CategoryTypeSplitSchemaVersion;
            var normalized = data?.Clone() ?? CreateDefault(playniteGameId);
            normalized.SchemaVersion = CurrentSchemaVersion;
            normalized.PlayniteGameId = playniteGameId;
            normalized.ExcludedFromRefreshes = normalized.ExcludedFromRefreshes == true ? true : (bool?)null;
            normalized.ExcludedFromSummaries = normalized.ExcludedFromSummaries == true ? true : (bool?)null;
            normalized.RuleClaims = NormalizeRuleClaims(normalized.RuleClaims);
            normalized.UserPinnedFields = NormalizeRuleFieldList(normalized.UserPinnedFields);
            normalized.UseSeparateLockedIconsOverride = normalized.UseSeparateLockedIconsOverride == true ? true : (bool?)null;
            normalized.ForceUseExophase = normalized.ForceUseExophase == true ? true : (bool?)null;
            NormalizeCapstones(normalized);
            normalized.ExophaseSlugOverride = NormalizeString(normalized.ExophaseSlugOverride);
            // The Exophase rarity page was once its own field; it is now the game's Exophase link.
            normalized.MetadataLinks = NormalizeMetadataLinks(MoveEnrichmentSlugToLink(normalized.MetadataLinks, NormalizeString(normalized.ExophaseEnrichmentSlugOverride)));
            normalized.ExophaseEnrichmentSlugOverride = null;
            normalized.MetadataChoices = normalized.MetadataChoices?.IsEmpty() == false ? normalized.MetadataChoices : null;
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
            normalized.AchievementCategoryKinds = NormalizeCategoryKinds(normalized.AchievementCategoryKinds);
            normalized.AchievementTags = NormalizeGameTags(normalized.AchievementTags);
            normalized.GameSummaryCategory = NormalizeGameSummaryCategory(normalized.GameSummaryCategory);
            var extractedFilters = ExtractLegacyAchievementFilters(normalized.AchievementCategoryTypeOverrides);
            normalized.AchievementCategoryTypeOverrides = extractedFilters.CategoryTypeOverrides;
            normalized.FilteredAchievementApiNames = MergeApiNameLists(
                normalized.FilteredAchievementApiNames,
                extractedFilters.FilteredAchievementApiNames);
            normalized.SummaryFilteredAchievementApiNames = MergeApiNameLists(
                normalized.SummaryFilteredAchievementApiNames,
                extractedFilters.SummaryFilteredAchievementApiNames);
            normalized.GoalAchievementApiNames = NormalizeAchievementOrder(normalized.GoalAchievementApiNames);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.AchievementNotes = AchievementNoteHelper.NormalizeNoteMap(normalized.AchievementNotes);
            // Schema 8 fold: the parallel maps above become one record per achievement. Both
            // shapes are then kept in sync, so a writer targeting either is still correct.
            normalized.AchievementOverrides = NormalizeAchievementOverrides(MergeLegacyAchievementMaps(
                NormalizeAchievementOverrides(splitLegacyTypes
                    ? SplitLegacyCategoryTypes(normalized.AchievementOverrides)
                    : normalized.AchievementOverrides),
                normalized.AchievementCategoryOverrides,
                // Always folded: no v9 build writes this map, and a value only fills an empty field.
                normalized.AchievementCategoryTypeOverrides,
                normalized.AchievementNotes,
                normalized.AchievementUnlockedIconOverrides,
                normalized.AchievementLockedIconOverrides));
            normalized.AchievementCategoryOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.Category);
            normalized.AchievementCategoryTypeOverrides = null;
            normalized.AchievementNotes = ProjectOverrideField(normalized.AchievementOverrides, o => o.Note);
            normalized.AchievementUnlockedIconOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.UnlockedIconPath);
            normalized.AchievementLockedIconOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.LockedIconPath);
            normalized.NotificationAppearanceOverride =
                NormalizeNotificationAppearanceOverride(normalized.NotificationAppearanceOverride);
            normalized.ManualLink = NormalizeManualLink(normalized.ManualLink);
            normalized.CustomAchievements = NormalizeCustomAchievements(normalized.CustomAchievements, splitLegacyTypes);
            normalized.CustomProviderId = NormalizeCustomProviderId(normalized.CustomProviderId, normalized.CustomAchievements);
            PruneOrphanedCustomAchievementReferences(normalized);
            return normalized;
        }

        public static GameCustomDataPortableFile NormalizePortable(GameCustomDataPortableFile data, Guid playniteGameId)
        {
            var splitLegacyTypes = (data?.SchemaVersion ?? CurrentSchemaVersion) < CategoryTypeSplitSchemaVersion;
            var normalized = data?.Clone() ?? new GameCustomDataPortableFile();
            normalized.SchemaVersion = CurrentSchemaVersion;
            normalized.PlayniteGameId = playniteGameId;
            normalized.UseSeparateLockedIconsOverride = normalized.UseSeparateLockedIconsOverride == true ? true : (bool?)null;
            normalized.ForceUseExophase = normalized.ForceUseExophase == true ? true : (bool?)null;
            NormalizeCapstones(normalized);
            normalized.ExophaseSlugOverride = NormalizeString(normalized.ExophaseSlugOverride);
            // The Exophase rarity page was once its own field; it is now the game's Exophase link.
            normalized.MetadataLinks = NormalizeMetadataLinks(MoveEnrichmentSlugToLink(normalized.MetadataLinks, NormalizeString(normalized.ExophaseEnrichmentSlugOverride)));
            normalized.ExophaseEnrichmentSlugOverride = null;
            normalized.MetadataChoices = normalized.MetadataChoices?.IsEmpty() == false ? normalized.MetadataChoices : null;
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
            normalized.AchievementCategoryKinds = NormalizeCategoryKinds(normalized.AchievementCategoryKinds);
            normalized.AchievementTags = NormalizeGameTags(normalized.AchievementTags);
            normalized.GameSummaryCategory = NormalizeGameSummaryCategory(normalized.GameSummaryCategory);
            normalized.AchievementCategoryTypeOverrides = NormalizeCategoryTypeOverrides(normalized.AchievementCategoryTypeOverrides);
            normalized.FilteredAchievementApiNames = NormalizeAchievementApiNameList(normalized.FilteredAchievementApiNames);
            normalized.SummaryFilteredAchievementApiNames = NormalizeAchievementApiNameList(normalized.SummaryFilteredAchievementApiNames);
            normalized.GoalAchievementApiNames = NormalizeAchievementOrder(normalized.GoalAchievementApiNames);
            normalized.AchievementUnlockedIconOverrides = NormalizeIconOverrides(normalized.AchievementUnlockedIconOverrides);
            normalized.AchievementLockedIconOverrides = NormalizeIconOverrides(normalized.AchievementLockedIconOverrides);
            normalized.AchievementNotes = AchievementNoteHelper.NormalizeNoteMap(normalized.AchievementNotes);
            // Schema 8 fold, matching NormalizeInternal, so an imported schema-7 package lands on
            // the record and an exported package carries both shapes.
            normalized.AchievementOverrides = NormalizeAchievementOverrides(MergeLegacyAchievementMaps(
                NormalizeAchievementOverrides(splitLegacyTypes
                    ? SplitLegacyCategoryTypes(normalized.AchievementOverrides)
                    : normalized.AchievementOverrides),
                normalized.AchievementCategoryOverrides,
                // Always folded: no v9 build writes this map, and a value only fills an empty field.
                normalized.AchievementCategoryTypeOverrides,
                normalized.AchievementNotes,
                normalized.AchievementUnlockedIconOverrides,
                normalized.AchievementLockedIconOverrides));
            normalized.AchievementCategoryOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.Category);
            normalized.AchievementCategoryTypeOverrides = null;
            normalized.AchievementNotes = ProjectOverrideField(normalized.AchievementOverrides, o => o.Note);
            normalized.AchievementUnlockedIconOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.UnlockedIconPath);
            normalized.AchievementLockedIconOverrides = ProjectOverrideField(normalized.AchievementOverrides, o => o.LockedIconPath);
            normalized.NotificationAppearanceOverride =
                NormalizeNotificationAppearanceOverride(normalized.NotificationAppearanceOverride);
            normalized.ManualLink = NormalizeManualLink(normalized.ManualLink);
            normalized.CustomAchievements = NormalizeCustomAchievements(normalized.CustomAchievements, splitLegacyTypes);
            normalized.CustomProviderId = NormalizeCustomProviderId(normalized.CustomProviderId, normalized.CustomAchievements);
            normalized.CustomProvider = NormalizeCustomProviderSnapshot(normalized.CustomProvider, normalized.CustomProviderId);
            PruneOrphanedCustomAchievementReferences(normalized);
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
                   // A claim alone must survive: it holds the status to restore when the rule lets go.
                   (data.RuleClaims != null && data.RuleClaims.Count > 0) ||
                   (data.UserPinnedFields != null && data.UserPinnedFields.Count > 0) ||
                   data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   data.CapstonesMaterialized ||
                   // Kept alone as well: dropping a record that carries only this would let
                   // generation author the capstone the user removed all over again.
                   data.AutoCapstoneGenerated ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   (data.AchievementCategoryKinds != null && data.AchievementCategoryKinds.Count > 0) ||
                   (data.AchievementTags != null && data.AchievementTags.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.GoalAchievementApiNames != null && data.GoalAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   (data.AchievementOverrides != null && data.AchievementOverrides.Count > 0) ||
                   data.ProviderOverride != null ||
                   !string.IsNullOrWhiteSpace(data.ExophaseEnrichmentSlugOverride) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.NotificationAppearanceOverride != null ||
                   data.UnlockSounds != null ||
                   data.NotificationBehaviorOverride != null ||
                   data.CaptureOverride != null ||
                   HasAnyMetadataLink(data.MetadataLinks) ||
                   data.MetadataChoices?.IsEmpty() == false ||
                   data.ManualLink != null ||
                   (data.CustomAchievements != null && data.CustomAchievements.Count > 0) ||
                   !string.IsNullOrWhiteSpace(data.CustomProviderId);
        }

        public static bool HasPortableData(GameCustomDataPortableFile data)
        {
            if (data == null)
            {
                return false;
            }

            return HasAnyMetadataLink(data.MetadataLinks) ||
                   data.MetadataChoices?.IsEmpty() == false ||
                   data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   data.CapstonesMaterialized ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   (data.AchievementCategoryKinds != null && data.AchievementCategoryKinds.Count > 0) ||
                   (data.AchievementTags != null && data.AchievementTags.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.GoalAchievementApiNames != null && data.GoalAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   (data.AchievementOverrides != null && data.AchievementOverrides.Count > 0) ||
                   data.ProviderOverride != null ||
                   !string.IsNullOrWhiteSpace(data.ExophaseEnrichmentSlugOverride) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.NotificationAppearanceOverride != null ||
                   data.ManualLink != null ||
                   (data.CustomAchievements != null && data.CustomAchievements.Count > 0) ||
                   !string.IsNullOrWhiteSpace(data.CustomProviderId);
        }

        /// <summary>
        /// Whether a game counts as customized for the Customized tag: anything an export would
        /// carry, plus the per-game unlock sounds, notification behavior and capture overrides.
        /// </summary>
        public static bool HasVisibleCustomization(GameCustomDataFile data)
        {
            return HasPortableData(data) ||
                   (data != null &&
                    (data.UnlockSounds != null ||
                     data.NotificationBehaviorOverride != null ||
                     data.CaptureOverride != null));
        }

        public static bool HasPortableData(GameCustomDataFile data)
        {
            if (data == null)
            {
                return false;
            }

            // Only links the user set: an automatic match is not a customization.
            return HasUserMetadataLink(data.MetadataLinks) ||
                   data.MetadataChoices?.IsEmpty() == false ||
                   data.UseSeparateLockedIconsOverride == true ||
                   !string.IsNullOrWhiteSpace(data.ManualCapstoneApiName) ||
                   data.CapstonesMaterialized ||
                   (data.AchievementOrder != null && data.AchievementOrder.Count > 0) ||
                   (data.AchievementCategoryOverrides != null && data.AchievementCategoryOverrides.Count > 0) ||
                   (data.AchievementCategoryTypeOverrides != null && data.AchievementCategoryTypeOverrides.Count > 0) ||
                   (data.AchievementCategoryOrder != null && data.AchievementCategoryOrder.Count > 0) ||
                   (data.AchievementCategoryImageOverrides != null && data.AchievementCategoryImageOverrides.Count > 0) ||
                   (data.AchievementCategoryKinds != null && data.AchievementCategoryKinds.Count > 0) ||
                   (data.AchievementTags != null && data.AchievementTags.Count > 0) ||
                   data.GameSummaryCategory != null ||
                   (data.FilteredAchievementApiNames != null && data.FilteredAchievementApiNames.Count > 0) ||
                   (data.SummaryFilteredAchievementApiNames != null && data.SummaryFilteredAchievementApiNames.Count > 0) ||
                   (data.GoalAchievementApiNames != null && data.GoalAchievementApiNames.Count > 0) ||
                   (data.AchievementUnlockedIconOverrides != null && data.AchievementUnlockedIconOverrides.Count > 0) ||
                   (data.AchievementLockedIconOverrides != null && data.AchievementLockedIconOverrides.Count > 0) ||
                   (data.AchievementNotes != null && data.AchievementNotes.Count > 0) ||
                   (data.AchievementOverrides != null && data.AchievementOverrides.Count > 0) ||
                   data.ProviderOverride != null ||
                   !string.IsNullOrWhiteSpace(data.ExophaseEnrichmentSlugOverride) ||
                   (data.RetroAchievementsGameIdOverride.HasValue && data.RetroAchievementsGameIdOverride.Value > 0) ||
                   !string.IsNullOrWhiteSpace(data.XeniaTitleIdOverride) ||
                   !string.IsNullOrWhiteSpace(data.ShadPS4MatchIdOverride) ||
                   data.ForceUseExophase == true ||
                   !string.IsNullOrWhiteSpace(data.ExophaseSlugOverride) ||
                   data.NotificationAppearanceOverride != null ||
                   data.ManualLink != null ||
                   (data.CustomAchievements != null && data.CustomAchievements.Count > 0) ||
                   !string.IsNullOrWhiteSpace(data.CustomProviderId);
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
                RuleClaims = GameCustomDataFile.CloneRuleClaims(existing.RuleClaims ?? legacy.RuleClaims),
                UserPinnedFields = existing.UserPinnedFields != null
                    ? new List<string>(existing.UserPinnedFields)
                    : legacy.UserPinnedFields != null
                        ? new List<string>(legacy.UserPinnedFields)
                        : null,
                UseSeparateLockedIconsOverride = existing.UseSeparateLockedIconsOverride ?? legacy.UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = !string.IsNullOrWhiteSpace(existing.ManualCapstoneApiName)
                    ? existing.ManualCapstoneApiName
                    : legacy.ManualCapstoneApiName,
                CapstonesMaterialized = existing.CapstonesMaterialized || legacy.CapstonesMaterialized,
                AutoCapstoneGenerated = existing.AutoCapstoneGenerated || legacy.AutoCapstoneGenerated,
                Capstones = NormalizeCapstoneList(existing.CapstonesMaterialized ? existing.Capstones : legacy.Capstones),
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
                AchievementCategoryKinds = GameCustomDataFile.CloneCategoryKindMap(
                    existing.AchievementCategoryKinds != null && existing.AchievementCategoryKinds.Count > 0
                        ? existing.AchievementCategoryKinds
                        : legacy.AchievementCategoryKinds),
                AchievementTags = GameCustomDataFile.CloneTagList(existing.AchievementTags ?? legacy.AchievementTags),
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
                GoalAchievementApiNames = existing.GoalAchievementApiNames != null && existing.GoalAchievementApiNames.Count > 0
                    ? new List<string>(existing.GoalAchievementApiNames)
                    : legacy.GoalAchievementApiNames != null && legacy.GoalAchievementApiNames.Count > 0
                        ? new List<string>(legacy.GoalAchievementApiNames)
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
                AchievementOverrides = existing.AchievementOverrides != null && existing.AchievementOverrides.Count > 0
                    ? GameCustomDataFile.CloneAchievementOverrideMap(existing.AchievementOverrides)
                    : legacy.AchievementOverrides != null && legacy.AchievementOverrides.Count > 0
                        ? GameCustomDataFile.CloneAchievementOverrideMap(legacy.AchievementOverrides)
                        : null,
                NotificationAppearanceOverride =
                    NormalizeNotificationAppearanceOverride(existing.NotificationAppearanceOverride) ??
                    NormalizeNotificationAppearanceOverride(legacy.NotificationAppearanceOverride),
                UnlockSounds = existing.UnlockSounds?.Clone() ?? legacy.UnlockSounds?.Clone(),
                NotificationBehaviorOverride = existing.NotificationBehaviorOverride?.Clone() ??
                    legacy.NotificationBehaviorOverride?.Clone(),
                CaptureOverride = existing.CaptureOverride?.Clone() ?? legacy.CaptureOverride?.Clone(),
                ProviderOverride = ResolveEffectiveProviderOverride(existing) ??
                    ResolveEffectiveProviderOverride(legacy),
                ExophaseEnrichmentSlugOverride = !string.IsNullOrWhiteSpace(existing.ExophaseEnrichmentSlugOverride)
                    ? existing.ExophaseEnrichmentSlugOverride
                    : legacy.ExophaseEnrichmentSlugOverride,
                MetadataLinks = MetadataLinkData.CloneMap(
                    HasAnyMetadataLink(existing.MetadataLinks) ? existing.MetadataLinks : legacy.MetadataLinks),
                MetadataChoices = (existing.MetadataChoices ?? legacy.MetadataChoices)?.Clone(),
                ManualLink = existing.ManualLink?.Clone() ?? legacy.ManualLink?.Clone(),
                CustomAchievements = existing.CustomAchievements != null && existing.CustomAchievements.Count > 0
                    ? existing.CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : legacy.CustomAchievements != null && legacy.CustomAchievements.Count > 0
                        ? legacy.CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                        : null,
                // Follows whichever side supplied the custom achievements it belongs to.
                CustomProviderId = existing.CustomAchievements != null && existing.CustomAchievements.Count > 0
                    ? existing.CustomProviderId
                    : legacy.CustomProviderId
            };
        }

        /// <summary>
        /// Keeps one claim per field, last one winning, and drops claims with no field.
        /// </summary>
        private static List<RuleClaim> NormalizeRuleClaims(IEnumerable<RuleClaim> claims)
        {
            if (claims == null)
            {
                return null;
            }

            var byField = new Dictionary<string, RuleClaim>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (var claim in claims)
            {
                var field = NormalizeString(claim?.Field);
                if (field == null)
                {
                    continue;
                }

                if (!byField.ContainsKey(field))
                {
                    order.Add(field);
                }

                byField[field] = new RuleClaim
                {
                    Field = field,
                    Written = NormalizeString(claim.Written),
                    Prior = NormalizeString(claim.Prior)
                };
            }

            return order.Count > 0 ? order.ConvertAll(field => byField[field]) : null;
        }

        private static List<string> NormalizeRuleFieldList(IEnumerable<string> fields)
        {
            if (fields == null)
            {
                return null;
            }

            var normalized = new List<string>();
            foreach (var field in fields)
            {
                var value = NormalizeString(field);
                if (value != null && !normalized.Contains(value))
                {
                    normalized.Add(value);
                }
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static GameNotificationAppearanceOverride NormalizeNotificationAppearanceOverride(
            GameNotificationAppearanceOverride value)
        {
            if (value?.Style == null)
            {
                return null;
            }

            return value.Clone();
        }

        /// <summary>
        /// Canonicalizes the providers whose override values have a stored shape to enforce, and
        /// keeps every other provider's override as entered: its value was validated by the
        /// provider's override descriptor when it was set, and the provider parses it again when it
        /// refreshes. This runs on every read too, before providers exist, so it cannot consult them.
        /// </summary>
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
                case "GameJolt":
                    return TryNormalizePositiveInteger(value, out var id)
                        ? new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = id.ToString(CultureInfo.InvariantCulture)
                        }
                        : null;

                case "Meta":
                    // Meta app ids are 15-16 digits, past the int range of the case above.
                    return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var metaAppId) &&
                           metaAppId > 0
                        ? new ProviderOverrideData
                        {
                            ProviderKey = providerKey,
                            Value = metaAppId.ToString(CultureInfo.InvariantCulture)
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
                case "Riot":
                case "GW2":
                case "Hypixel":
                    return new ProviderOverrideData
                    {
                        ProviderKey = providerKey,
                        Value = null
                    };

                default:
                    return new ProviderOverrideData
                    {
                        ProviderKey = providerKey,
                        Value = value
                    };
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

            if (string.Equals(normalized, "GameJolt", StringComparison.OrdinalIgnoreCase))
            {
                return "GameJolt";
            }

            if (string.Equals(normalized, "Riot", StringComparison.OrdinalIgnoreCase))
            {
                return "Riot";
            }

            if (string.Equals(normalized, "GW2", StringComparison.OrdinalIgnoreCase))
            {
                return "GW2";
            }

            if (string.Equals(normalized, "Hypixel", StringComparison.OrdinalIgnoreCase))
            {
                return "Hypixel";
            }

            if (string.Equals(normalized, "Meta", StringComparison.OrdinalIgnoreCase))
            {
                return "Meta";
            }

            // Any other provider's key is kept as written; lookups compare keys case-insensitively.
            return normalized;
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
            var seen = new HashSet<string>(CategoryPathHelper.LabelComparer);
            foreach (var categoryLabel in categoryLabels)
            {
                var label = CategoryPathHelper.NormalizePath(categoryLabel);
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
                var category = AchievementCategoryHelper.NormalizeCategory(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(category))
                {
                    continue;
                }

                // Blank stays dropped rather than becoming an explicit Default assignment; a real
                // value is canonicalized so nothing downstream has to re-normalize the path.
                normalized[apiName] = CategoryPathHelper.NormalizePath(category);
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

                    if (!categoryTypes.Contains(token, StringComparer.OrdinalIgnoreCase))
                    {
                        categoryTypes.Add(token);
                    }
                }

                // Kept raw: LegacyCategoryTypes splits the value into kind and tags.
                if (categoryTypes.Count > 0)
                {
                    categoryTypeOverrides[apiName] = string.Join("|", categoryTypes);
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
                // Kept raw: LegacyCategoryTypes splits the value into kind and tags.
                var categoryType = NormalizeString(pair.Value);
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

        /// <summary>
        /// Canonicalizes the per-achievement override map the same way the legacy parallel maps
        /// are canonicalized, and drops rows that carry nothing so the "is this game customized"
        /// predicates stay accurate.
        /// </summary>
        private static Dictionary<string, AchievementOverride> NormalizeAchievementOverrides(
            Dictionary<string, AchievementOverride> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                var apiName = NormalizeString(pair.Key);
                if (string.IsNullOrWhiteSpace(apiName) || pair.Value == null)
                {
                    continue;
                }

                var category = AchievementCategoryHelper.NormalizeCategory(pair.Value.Category);
                var entry = new AchievementOverride
                {
                    DisplayName = NormalizeString(pair.Value.DisplayName),
                    Description = NormalizeString(pair.Value.Description),
                    // A stored value and a clear flag are mutually exclusive; the value wins.
                    ClearDescription = pair.Value.ClearDescription && NormalizeString(pair.Value.Description) == null,
                    // A negative override is meaningless for a score total; drop rather than store.
                    Points = pair.Value.Points.HasValue && pair.Value.Points.Value >= 0
                        ? pair.Value.Points
                        : null,
                    TrophyType = NormalizeTrophyType(pair.Value.TrophyType),
                    ClearTrophyType = pair.Value.ClearTrophyType && NormalizeTrophyType(pair.Value.TrophyType) == null,
                    UnlockTimeUtc = NormalizeUtc(pair.Value.UnlockTimeUtc),
                    // A stored timestamp and a clear flag are mutually exclusive; the timestamp wins.
                    ClearUnlockTime = pair.Value.ClearUnlockTime && !pair.Value.UnlockTimeUtc.HasValue,
                    Category = !string.IsNullOrWhiteSpace(category)
                        ? CategoryPathHelper.NormalizePath(category)
                        : null,
                    // Softcore/Hardcore come only from RetroAchievements refreshes, never an override.
                    Tags = AchievementTagHelper.StripDerived(pair.Value.Tags),
                    ClearTags = pair.Value.ClearTags && AchievementTagHelper.StripDerived(pair.Value.Tags) == null,
                    Note = AchievementNoteHelper.NormalizeNote(pair.Value.Note),
                    UnlockedIconPath = NormalizeString(pair.Value.UnlockedIconPath),
                    LockedIconPath = NormalizeString(pair.Value.LockedIconPath),
                    // Either value is a customization, so this is carried as stored: null means the
                    // provider still decides. Omitting it here dropped the override on every save,
                    // and a hidden-only record then read as empty and was discarded outright.
                    Hidden = pair.Value.Hidden
                };

                if (!entry.IsEmpty)
                {
                    normalized[apiName] = entry;
                }
            }

            return normalized.Count > 0 ? normalized : null;
        }

        /// <summary>
        /// Folds the schema-7 parallel maps into the per-achievement record. A legacy value fills
        /// a field only where the record has nothing, so a record written directly is never
        /// clobbered by a stale mirror.
        /// </summary>
        private static Dictionary<string, AchievementOverride> MergeLegacyAchievementMaps(
            Dictionary<string, AchievementOverride> existing,
            Dictionary<string, string> categories,
            Dictionary<string, string> categoryTypes,
            Dictionary<string, string> notes,
            Dictionary<string, string> unlockedIcons,
            Dictionary<string, string> lockedIcons)
        {
            var merged = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            if (existing != null)
            {
                foreach (var pair in existing)
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value != null)
                    {
                        merged[pair.Key] = pair.Value.Clone();
                    }
                }
            }

            ApplyLegacyAchievementField(merged, categories, (entry, value) =>
            {
                if (string.IsNullOrWhiteSpace(entry.Category))
                {
                    entry.Category = value;
                }
            });
            ApplyLegacyAchievementField(merged, categoryTypes, LegacyCategoryTypes.ApplyTo);
            ApplyLegacyAchievementField(merged, notes, (entry, value) =>
            {
                if (string.IsNullOrWhiteSpace(entry.Note))
                {
                    entry.Note = value;
                }
            });
            ApplyLegacyAchievementField(merged, unlockedIcons, (entry, value) =>
            {
                if (string.IsNullOrWhiteSpace(entry.UnlockedIconPath))
                {
                    entry.UnlockedIconPath = value;
                }
            });
            ApplyLegacyAchievementField(merged, lockedIcons, (entry, value) =>
            {
                if (string.IsNullOrWhiteSpace(entry.LockedIconPath))
                {
                    entry.LockedIconPath = value;
                }
            });

            return merged.Count > 0 ? merged : null;
        }

        private static void ApplyLegacyAchievementField(
            Dictionary<string, AchievementOverride> target,
            Dictionary<string, string> source,
            Action<AchievementOverride, string> apply)
        {
            if (source == null)
            {
                return;
            }

            foreach (var pair in source)
            {
                var apiName = NormalizeString(pair.Key);
                var value = NormalizeString(pair.Value);
                if (string.IsNullOrWhiteSpace(apiName) || string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                if (!target.TryGetValue(apiName, out var entry) || entry == null)
                {
                    entry = new AchievementOverride();
                    target[apiName] = entry;
                }

                apply(entry, value);
            }
        }

        /// <summary>
        /// Projects one field of the per-achievement record back into its schema-7 map shape, so
        /// consumers that have not been repointed to the record keep seeing current values.
        /// </summary>
        private static Dictionary<string, string> ProjectOverrideField(
            Dictionary<string, AchievementOverride> overrides,
            Func<AchievementOverride, string> selector)
        {
            if (overrides == null)
            {
                return null;
            }

            var projected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in overrides)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                var value = NormalizeString(selector(pair.Value));
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                projected[pair.Key] = value;
            }

            return projected.Count > 0 ? projected : null;
        }

        private static Dictionary<string, CategoryImageOverrideData> NormalizeCategoryImageOverrides(
            Dictionary<string, CategoryImageOverrideData> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, CategoryImageOverrideData>(CategoryPathHelper.LabelComparer);
            foreach (var pair in values)
            {
                var category = CategoryPathHelper.NormalizePath(pair.Key);
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
            var label = AchievementCategoryHelper.NormalizeCategory(value?.Label);
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            var normalizedLabel = CategoryPathHelper.NormalizePath(label);
            var providerLabel = AchievementCategoryHelper.NormalizeCategory(value?.ProviderLabel);
            return new GameSummaryCategoryData
            {
                Label = normalizedLabel,
                ProviderLabel = string.IsNullOrWhiteSpace(providerLabel)
                    ? normalizedLabel
                    : CategoryPathHelper.NormalizePath(providerLabel)
            };
        }

        /// <summary>
        /// <paramref name="links"/> with <paramref name="slug"/>, a slug the user set for the Exophase rarity page
        /// before that page was a metadata link, as the game's Exophase link, unless it already has one.
        /// </summary>
        private static Dictionary<string, MetadataLinkData> MoveEnrichmentSlugToLink(Dictionary<string, MetadataLinkData> links, string slug)
        {
            if (string.IsNullOrWhiteSpace(slug))
            {
                return links;
            }

            var moved = links == null
                ? new Dictionary<string, MetadataLinkData>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, MetadataLinkData>(links, StringComparer.OrdinalIgnoreCase);
            if (!moved.ContainsKey(MetadataChoices.Exophase))
            {
                moved[MetadataChoices.Exophase] = new MetadataLinkData { Id = slug.Trim(), IsUserSet = true };
            }

            return moved;
        }

        /// <summary>
        /// Trimmed keys and values; an automatic link without an id carries nothing and is dropped,
        /// while a user's empty link is kept as their "no match".
        /// </summary>
        private static Dictionary<string, MetadataLinkData> NormalizeMetadataLinks(Dictionary<string, MetadataLinkData> links)
        {
            if (links == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, MetadataLinkData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in links)
            {
                var key = NormalizeString(pair.Key);
                var id = NormalizeString(pair.Value?.Id);
                // A blank id is a stored "no match": the user's, or the source's own matching that found nothing.
                if (key == null || pair.Value == null)
                {
                    continue;
                }

                normalized[key] = new MetadataLinkData
                {
                    Id = id,
                    Label = NormalizeString(pair.Value.Label),
                    IsUserSet = pair.Value.IsUserSet,
                    Pairings = id == null ? null : NormalizeMetadataPairings(pair.Value.Pairings)
                };
            }

            return normalized.Count > 0 ? normalized : null;
        }

        /// <summary>Pairings keyed by a non-blank ApiName; an empty value ("none") is kept.</summary>
        private static Dictionary<string, string> NormalizeMetadataPairings(Dictionary<string, string> pairings)
        {
            if (pairings == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in pairings)
            {
                var target = NormalizeString(pair.Key);
                if (target != null)
                {
                    normalized[target] = NormalizeString(pair.Value) ?? string.Empty;
                }
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static bool HasAnyMetadataLink(Dictionary<string, MetadataLinkData> links)
        {
            return links != null && links.Values.Any(link => link != null);
        }

        internal static bool HasUserMetadataLink(Dictionary<string, MetadataLinkData> links)
        {
            return links != null && links.Values.Any(link => link?.IsUserSet == true || link?.Pairings?.Count > 0);
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
                DisplayPlatformKeyOverride = NormalizeString(link.DisplayPlatformKeyOverride),
                CreatedUtc = createdUtc,
                LastModifiedUtc = lastModifiedUtc
            };
        }

        /// <summary>
        /// A custom provider assignment only has meaning while the game has custom achievements, so
        /// the id is dropped otherwise and never keeps an empty row alive.
        /// </summary>
        private static string NormalizeCustomProviderId(
            string customProviderId,
            IReadOnlyCollection<CustomAchievementDefinition> customAchievements)
        {
            var normalized = CustomProviderKeys.NormalizeId(customProviderId);
            return normalized != null && customAchievements != null && customAchievements.Count > 0
                ? normalized
                : null;
        }

        private static CustomProviderDefinition NormalizeCustomProviderSnapshot(
            CustomProviderDefinition snapshot,
            string customProviderId)
        {
            var name = NormalizeString(snapshot?.Name);
            if (snapshot == null || customProviderId == null || name == null)
            {
                return null;
            }

            return new CustomProviderDefinition
            {
                Id = customProviderId,
                Name = name,
                ColorHex = NormalizeString(snapshot.ColorHex),
                IconPathData = NormalizeString(snapshot.IconPathData),
                IconSource = NormalizeString(snapshot.IconSource)
            };
        }

        /// <summary>
        /// Folds the legacy single capstone into the set and rebuilds the set itself.
        /// </summary>
        /// <remarks>
        /// The fold is behaviour-preserving: a stored single capstone already suppressed every
        /// provider capstone, which is exactly what a materialized set does.
        ///
        /// An empty set is stored as a null list rather than an empty one, so
        /// <see cref="GameCustomDataFile.CapstonesMaterialized"/> is the only thing separating
        /// "this game has no capstones" from "this game has never been touched".
        /// </remarks>
        private static void NormalizeCapstones(GameCustomDataFile data)
        {
            var legacy = NormalizeString(data.ManualCapstoneApiName);
            if (!data.CapstonesMaterialized && !string.IsNullOrWhiteSpace(legacy))
            {
                data.CapstonesMaterialized = true;
                data.Capstones = new List<CapstoneAssignment>
                {
                    new CapstoneAssignment { ApiName = legacy }
                };
            }

            data.ManualCapstoneApiName = null;
            data.Capstones = NormalizeCapstoneList(data.Capstones);
        }

        private static void NormalizeCapstones(GameCustomDataPortableFile data)
        {
            var legacy = NormalizeString(data.ManualCapstoneApiName);
            if (!data.CapstonesMaterialized && !string.IsNullOrWhiteSpace(legacy))
            {
                data.CapstonesMaterialized = true;
                data.Capstones = new List<CapstoneAssignment>
                {
                    new CapstoneAssignment { ApiName = legacy }
                };
            }

            data.ManualCapstoneApiName = null;
            data.Capstones = NormalizeCapstoneList(data.Capstones);
        }

        /// <summary>
        /// Drops blank entries and keeps one assignment per achievement, the last written winning
        /// so that re-nominating an achievement moves it rather than duplicating it.
        /// </summary>
        private static List<CapstoneAssignment> NormalizeCapstoneList(IEnumerable<CapstoneAssignment> assignments)
        {
            if (assignments == null)
            {
                return null;
            }

            var normalized = new List<CapstoneAssignment>();
            var indexByApiName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var assignment in assignments)
            {
                var apiName = NormalizeString(assignment?.ApiName);
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var entry = new CapstoneAssignment { ApiName = apiName };
                if (indexByApiName.TryGetValue(apiName, out var existingIndex))
                {
                    normalized[existingIndex] = entry;
                    continue;
                }

                indexByApiName[apiName] = normalized.Count;
                normalized.Add(entry);
            }

            return normalized.Count > 0 ? normalized : null;
        }

        /// <summary>
        /// Drops every ApiName-keyed reference to an authored achievement the file no longer
        /// defines: its capstone entry, order slot, goal, filters, and per-achievement overrides.
        /// </summary>
        /// <remarks>
        /// An authored achievement exists only through its definition, so a reference to a
        /// <c>custom:</c> ApiName without one can never resolve. Deleting an achievement rewrote the
        /// definition list and left the references behind, where the resolvers hid them until a
        /// new achievement was generated with the same ID and inherited them, capstone included.
        /// Provider ApiNames are left alone: a provider achievement can be absent from a refresh
        /// and come back.
        /// </remarks>
        private static void PruneOrphanedCustomAchievementReferences(GameCustomDataFile data)
        {
            var live = CollectCustomApiNames(data.CustomAchievements);
            data.Capstones = PruneCapstones(data.Capstones, live);
            data.AchievementOrder = PruneApiNameList(data.AchievementOrder, live);
            data.FilteredAchievementApiNames = PruneApiNameList(data.FilteredAchievementApiNames, live);
            data.SummaryFilteredAchievementApiNames = PruneApiNameList(data.SummaryFilteredAchievementApiNames, live);
            data.GoalAchievementApiNames = PruneApiNameList(data.GoalAchievementApiNames, live);
            data.AchievementOverrides = PruneApiNameMap(data.AchievementOverrides, live);
            data.AchievementCategoryOverrides = PruneApiNameMap(data.AchievementCategoryOverrides, live);
            data.AchievementCategoryTypeOverrides = PruneApiNameMap(data.AchievementCategoryTypeOverrides, live);
            data.AchievementNotes = PruneApiNameMap(data.AchievementNotes, live);
            data.AchievementUnlockedIconOverrides = PruneApiNameMap(data.AchievementUnlockedIconOverrides, live);
            data.AchievementLockedIconOverrides = PruneApiNameMap(data.AchievementLockedIconOverrides, live);
        }

        private static void PruneOrphanedCustomAchievementReferences(GameCustomDataPortableFile data)
        {
            var live = CollectCustomApiNames(data.CustomAchievements);
            data.Capstones = PruneCapstones(data.Capstones, live);
            data.AchievementOrder = PruneApiNameList(data.AchievementOrder, live);
            data.FilteredAchievementApiNames = PruneApiNameList(data.FilteredAchievementApiNames, live);
            data.SummaryFilteredAchievementApiNames = PruneApiNameList(data.SummaryFilteredAchievementApiNames, live);
            data.GoalAchievementApiNames = PruneApiNameList(data.GoalAchievementApiNames, live);
            data.AchievementOverrides = PruneApiNameMap(data.AchievementOverrides, live);
            data.AchievementCategoryOverrides = PruneApiNameMap(data.AchievementCategoryOverrides, live);
            data.AchievementCategoryTypeOverrides = PruneApiNameMap(data.AchievementCategoryTypeOverrides, live);
            data.AchievementNotes = PruneApiNameMap(data.AchievementNotes, live);
            data.AchievementUnlockedIconOverrides = PruneApiNameMap(data.AchievementUnlockedIconOverrides, live);
            data.AchievementLockedIconOverrides = PruneApiNameMap(data.AchievementLockedIconOverrides, live);
        }

        private static HashSet<string> CollectCustomApiNames(IEnumerable<CustomAchievementDefinition> definitions)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions ?? Enumerable.Empty<CustomAchievementDefinition>())
            {
                var apiName = CustomAchievementProjectionService.BuildApiName(definition?.Id);
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    live.Add(apiName);
                }
            }

            return live;
        }

        private static bool IsOrphanedCustomApiName(string apiName, HashSet<string> live)
        {
            return CustomAchievementProjectionService.IsCustomApiName(apiName) &&
                   !live.Contains(apiName.Trim());
        }

        private static List<CapstoneAssignment> PruneCapstones(List<CapstoneAssignment> assignments, HashSet<string> live)
        {
            if (assignments == null)
            {
                return null;
            }

            assignments.RemoveAll(assignment => IsOrphanedCustomApiName(assignment?.ApiName, live));
            return assignments.Count > 0 ? assignments : null;
        }

        private static List<string> PruneApiNameList(List<string> apiNames, HashSet<string> live)
        {
            if (apiNames == null)
            {
                return null;
            }

            apiNames.RemoveAll(apiName => IsOrphanedCustomApiName(apiName, live));
            return apiNames.Count > 0 ? apiNames : null;
        }

        private static Dictionary<string, TValue> PruneApiNameMap<TValue>(
            Dictionary<string, TValue> map,
            HashSet<string> live)
        {
            if (map == null)
            {
                return null;
            }

            foreach (var key in map.Keys.Where(key => IsOrphanedCustomApiName(key, live)).ToList())
            {
                map.Remove(key);
            }

            return map.Count > 0 ? map : null;
        }

        /// <summary>
        /// Copies of <paramref name="values"/> with each pre-v9 type override's tags moved to
        /// <see cref="AchievementOverride.Tags"/> (see <see cref="LegacyCategoryTypes.ApplyTo"/>).
        /// </summary>
        private static Dictionary<string, AchievementOverride> SplitLegacyCategoryTypes(
            Dictionary<string, AchievementOverride> values)
        {
            if (values == null)
            {
                return null;
            }

            var split = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in values)
            {
                if (pair.Value == null)
                {
                    continue;
                }

                var entry = pair.Value.Clone();
                var legacyValue = entry.LegacyCategoryType;
                entry.LegacyCategoryType = null;
                LegacyCategoryTypes.ApplyTo(entry, legacyValue);
                split[pair.Key] = entry;
            }

            return split;
        }

        private static Dictionary<string, string> NormalizeCategoryKinds(Dictionary<string, string> values)
        {
            if (values == null)
            {
                return null;
            }

            var normalized = new Dictionary<string, string>(CategoryPathHelper.LabelComparer);
            foreach (var pair in values)
            {
                var category = AchievementCategoryHelper.NormalizeCategory(pair.Key);
                var kind = CategoryKindHelper.NormalizeStored(pair.Value);
                if (category == null || kind == null)
                {
                    continue;
                }

                normalized[CategoryPathHelper.NormalizePath(category)] = kind;
            }

            return normalized.Count > 0 ? normalized : null;
        }

        /// <summary>
        /// A game's own tags. Any key may be one, built-in keys included: a tag deleted from the
        /// global list lives on as a game's own, and the game's definition is the one shown there.
        /// </summary>
        private static List<AchievementTagDefinition> NormalizeGameTags(IEnumerable<AchievementTagDefinition> values)
        {
            var normalized = AchievementTagHelper.NormalizeDefinitions(values, ensureSystemTags: false);
            return normalized != null && normalized.Count > 0 ? normalized : null;
        }

        private static List<CustomAchievementDefinition> NormalizeCustomAchievements(
            IEnumerable<CustomAchievementDefinition> definitions,
            bool splitLegacyTypes)
        {
            if (definitions == null)
            {
                return null;
            }

            var normalized = new List<CustomAchievementDefinition>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                var displayName = NormalizeString(definition?.DisplayName);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var id = CustomAchievementProjectionService.NormalizeId(definition.Id);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = CustomAchievementProjectionService.GenerateId(displayName, usedIds);
                }

                if (!usedIds.Add(id))
                {
                    continue;
                }

                var unlocked = definition.Unlocked;
                normalized.Add(new CustomAchievementDefinition
                {
                    Id = id,
                    DisplayName = displayName,
                    Description = NormalizeString(definition.Description),
                    Unlocked = unlocked,
                    UnlockTimeUtc = unlocked ? NormalizeUtc(definition.UnlockTimeUtc) : null,
                    UnlockedIconPath = NormalizeString(definition.UnlockedIconPath),
                    LockedIconPath = NormalizeString(definition.LockedIconPath),
                    Points = NormalizeNonNegativeInt(definition.Points),
                    ScaledPoints = NormalizeNonNegativeInt(definition.ScaledPoints),
                    Category = AchievementCategoryHelper.NormalizeCategoryOrDefault(definition.Category),
                    // Before v9 the type field held kinds and tags together.
                    Tags = splitLegacyTypes
                        ? LegacyCategoryTypes.SplitTags(definition.CategoryKind)
                        : AchievementTagHelper.StripDerived(definition.Tags),
                    CategoryKind = splitLegacyTypes
                        ? LegacyCategoryTypes.SplitKind(definition.CategoryKind)
                        : CategoryKindHelper.Normalize(definition.CategoryKind),
                    TrophyType = NormalizeTrophyType(definition.TrophyType),
                    Hidden = definition.Hidden,
                    IsAutoCapstone = definition.IsAutoCapstone,
                    IsWholeGameAutoCapstone = definition.IsAutoCapstone && definition.IsWholeGameAutoCapstone,
                    IsCapstone = definition.IsCapstone,
                    Rarity = NormalizeRarity(definition.Rarity),
                    GlobalPercentUnlocked = NormalizePercent(definition.GlobalPercentUnlocked),
                    ProgressNum = NormalizeProgressNum(definition.ProgressNum, definition.ProgressDenom),
                    ProgressDenom = NormalizeProgressDenom(definition.ProgressNum, definition.ProgressDenom)
                });
            }

            return normalized.Count > 0 ? normalized : null;
        }

        private static DateTime? NormalizeUtc(DateTime? value)
        {
            if (!value.HasValue || value.Value == DateTime.MinValue)
            {
                return null;
            }

            var date = value.Value;
            if (date.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }

            return date.ToUniversalTime();
        }

        private static int? NormalizeNonNegativeInt(int? value)
        {
            return value.HasValue && value.Value >= 0 ? value : null;
        }

        private static double? NormalizePercent(double? value)
        {
            return value.HasValue && value.Value >= 0 && value.Value <= 100 ? value : null;
        }

        private static int? NormalizeProgressDenom(int? num, int? denom)
        {
            return denom.HasValue &&
                   denom.Value > 0 &&
                   (!num.HasValue || (num.Value >= 0 && num.Value <= denom.Value))
                ? denom
                : null;
        }

        private static int? NormalizeProgressNum(int? num, int? denom)
        {
            return denom.HasValue &&
                   denom.Value > 0 &&
                   num.HasValue &&
                   num.Value >= 0 &&
                   num.Value <= denom.Value
                ? num
                : null;
        }

        private static string NormalizeRarity(string value)
        {
            var normalized = NormalizeString(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            return RarityTierExtensions.TryParse(normalized, out var tier)
                ? tier.ToString()
                : null;
        }

        private static string NormalizeTrophyType(string value)
        {
            var normalized = NormalizeString(value);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return null;
            }

            switch (normalized.ToLowerInvariant())
            {
                case "bronze":
                case "silver":
                case "gold":
                case "platinum":
                    return normalized.ToLowerInvariant();
                default:
                    return null;
            }
        }
    }
}
