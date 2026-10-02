using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// A game's complete notification appearance snapshot. A null instance on
    /// <see cref="GameCustomDataFile"/> means the game continues to follow its provider/global
    /// appearance live.
    /// </summary>
    public sealed class GameNotificationAppearanceOverride
    {
        public NotificationStyleSettings Style { get; set; }

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool ToastUseThemeStyling { get; set; } = true;

        [JsonProperty(DefaultValueHandling = DefaultValueHandling.Include)]
        public bool FrameUseThemeStyling { get; set; } = true;

        public GameNotificationAppearanceOverride Clone()
        {
            return new GameNotificationAppearanceOverride
            {
                Style = Style?.Clone(),
                ToastUseThemeStyling = ToastUseThemeStyling,
                FrameUseThemeStyling = FrameUseThemeStyling
            };
        }
    }

    public sealed class ProviderOverrideData
    {
        public string ProviderKey { get; set; }

        public string Value { get; set; }

        public ProviderOverrideData Clone()
        {
            return new ProviderOverrideData
            {
                ProviderKey = ProviderKey,
                Value = Value
            };
        }
    }

    // Single category art override. Pre-refactor files stored separate Icon and Cover values;
    // those members are intentionally not migrated and are skipped on deserialization.
    public sealed class CategoryImageOverrideData
    {
        public string Art { get; set; }

        public CategoryImageOverrideData Clone()
        {
            return new CategoryImageOverrideData
            {
                Art = Art
            };
        }
    }

    // Category whose art is used as the game's image in game summary grids.
    // Label matches the image-override key space (effective/display label);
    // ProviderLabel keys provider-default art and survives renames.
    public sealed class GameSummaryCategoryData
    {
        public string Label { get; set; }

        public string ProviderLabel { get; set; }

        public GameSummaryCategoryData Clone()
        {
            return new GameSummaryCategoryData
            {
                Label = Label,
                ProviderLabel = ProviderLabel
            };
        }
    }

    /// <summary>
    /// Internal storage representation for per-game custom data.
    /// </summary>
    public sealed class GameCustomDataFile
    {
        public int SchemaVersion { get; set; } = 8;

        public Guid PlayniteGameId { get; set; }

        public bool? ExcludedFromRefreshes { get; set; }

        public bool? ExcludedFromSummaries { get; set; }

        public bool? UseSeparateLockedIconsOverride { get; set; }

        public string ManualCapstoneApiName { get; set; }

        /// <summary>
        /// True once the user has edited this game's capstones. From that moment the
        /// <see cref="Capstones"/> list is the whole truth for the game and provider capstone
        /// flags no longer apply to it, so an empty list means "this game has no capstones"
        /// rather than "fall back to the provider".
        /// </summary>
        /// <remarks>
        /// Stored rather than derived from the list being non-null because the normalizer nulls
        /// empty collections, which would otherwise collapse a deliberately emptied set back into
        /// an untouched one.
        /// </remarks>
        public bool CapstonesMaterialized { get; set; }

        /// <summary>
        /// The game's capstones once <see cref="CapstonesMaterialized"/> is set, seeded from the
        /// provider's own capstones at the moment of the first edit.
        /// </summary>
        public List<CapstoneAssignment> Capstones { get; set; }

        /// <summary>
        /// True once automatic capstone generation has handled this game, whether it authored an
        /// auto capstone, nominated an existing platinum, or found one already there. It is never
        /// handled again, so a capstone the user deleted or reset stays gone; filtering is how a
        /// generated capstone is set aside.
        /// </summary>
        public bool AutoCapstoneGenerated { get; set; }

        public List<string> AchievementOrder { get; set; }

        /// <inheritdoc cref="AchievementUnlockedIconOverrides"/>
        public Dictionary<string, string> AchievementCategoryOverrides { get; set; }

        /// <inheritdoc cref="AchievementUnlockedIconOverrides"/>
        public Dictionary<string, string> AchievementCategoryTypeOverrides { get; set; }

        public List<string> AchievementCategoryOrder { get; set; }

        public Dictionary<string, CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; }

        public GameSummaryCategoryData GameSummaryCategory { get; set; }

        public List<string> FilteredAchievementApiNames { get; set; }

        public List<string> SummaryFilteredAchievementApiNames { get; set; }

        /// <summary>
        /// Achievements the user is working toward, most-wanted first. Membership is the goal
        /// flag and list position is the goal order, matching <see cref="AchievementOrder"/>.
        /// </summary>
        public List<string> GoalAchievementApiNames { get; set; }

        /// <summary>
        /// Per-achievement user customization, keyed by ApiName. Schema 8 onward; the legacy
        /// scalar maps below are folded into this on migration and then cleared.
        /// </summary>
        public Dictionary<string, AchievementOverride> AchievementOverrides { get; set; }

        /// <summary>
        /// Legacy (schema 7 and earlier). Migrated into <see cref="AchievementOverrides"/>.
        /// Retained so existing records still deserialize; do not read these outside migration.
        /// </summary>
        public Dictionary<string, string> AchievementUnlockedIconOverrides { get; set; }

        /// <inheritdoc cref="AchievementUnlockedIconOverrides"/>
        public Dictionary<string, string> AchievementLockedIconOverrides { get; set; }

        public bool? ViewAchievementsIconFetchEnabled { get; set; }

        /// <inheritdoc cref="AchievementUnlockedIconOverrides"/>
        public Dictionary<string, string> AchievementNotes { get; set; }

        public int? RetroAchievementsGameIdOverride { get; set; }

        public List<int> RetroAchievementsSelectedSubsetGameIds { get; set; }

        public string XeniaTitleIdOverride { get; set; }

        public string ShadPS4MatchIdOverride { get; set; }

        public bool? ForceUseExophase { get; set; }

        public string ExophaseSlugOverride { get; set; }

        public GameNotificationAppearanceOverride NotificationAppearanceOverride { get; set; }
        public string SteamAccountIdOverride { get; set; }

        public ProviderOverrideData ProviderOverride { get; set; }

        /// <summary>
        /// Exophase slug used only for rarity/metadata enrichment when another provider services
        /// the game. Distinct from the legacy <see cref="ExophaseSlugOverride"/>, which selects
        /// the servicing provider and is migrated into <see cref="ProviderOverride"/>.
        /// </summary>
        public string ExophaseEnrichmentSlugOverride { get; set; }

        public ManualAchievementLink ManualLink { get; set; }

        public List<CustomAchievementDefinition> CustomAchievements { get; set; }

        /// <summary>
        /// Id of the user-defined custom provider a custom-only game displays as. Only meaningful
        /// while the game has custom achievements and no cached provider data.
        /// </summary>
        public string CustomProviderId { get; set; }

        public GameCustomDataFile Clone()
        {
            return new GameCustomDataFile
            {
                SchemaVersion = SchemaVersion,
                PlayniteGameId = PlayniteGameId,
                ExcludedFromRefreshes = ExcludedFromRefreshes,
                ExcludedFromSummaries = ExcludedFromSummaries,
                UseSeparateLockedIconsOverride = UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = ManualCapstoneApiName,
                CapstonesMaterialized = CapstonesMaterialized,
                AutoCapstoneGenerated = AutoCapstoneGenerated,
                Capstones = Capstones != null
                    ? Capstones.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                AchievementOrder = AchievementOrder != null
                    ? new List<string>(AchievementOrder)
                    : null,
                AchievementCategoryOverrides = AchievementCategoryOverrides != null
                    ? new Dictionary<string, string>(AchievementCategoryOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryTypeOverrides = AchievementCategoryTypeOverrides != null
                    ? new Dictionary<string, string>(AchievementCategoryTypeOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryOrder = AchievementCategoryOrder != null
                    ? new List<string>(AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(AchievementCategoryImageOverrides),
                GameSummaryCategory = GameSummaryCategory?.Clone(),
                FilteredAchievementApiNames = FilteredAchievementApiNames != null
                    ? new List<string>(FilteredAchievementApiNames)
                    : null,
                SummaryFilteredAchievementApiNames = SummaryFilteredAchievementApiNames != null
                    ? new List<string>(SummaryFilteredAchievementApiNames)
                    : null,
                GoalAchievementApiNames = GoalAchievementApiNames != null
                    ? new List<string>(GoalAchievementApiNames)
                    : null,
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                ViewAchievementsIconFetchEnabled = ViewAchievementsIconFetchEnabled,
                AchievementNotes = AchievementNotes != null
                    ? new Dictionary<string, string>(AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementOverrides = CloneAchievementOverrideMap(AchievementOverrides),
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                RetroAchievementsSelectedSubsetGameIds = RetroAchievementsSelectedSubsetGameIds != null
                    ? new List<int>(RetroAchievementsSelectedSubsetGameIds)
                    : null,
                XeniaTitleIdOverride = XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = ShadPS4MatchIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
                NotificationAppearanceOverride = NotificationAppearanceOverride?.Clone(),
                SteamAccountIdOverride = SteamAccountIdOverride,
                ProviderOverride = ProviderOverride?.Clone(),
                ExophaseEnrichmentSlugOverride = ExophaseEnrichmentSlugOverride,
                ManualLink = ManualLink?.Clone(),
                CustomAchievements = CustomAchievements != null
                    ? CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                CustomProviderId = CustomProviderId
            };
        }

        public GameCustomDataPortableFile ToPortable()
        {
            return new GameCustomDataPortableFile
            {
                SchemaVersion = SchemaVersion,
                PlayniteGameId = PlayniteGameId,
                UseSeparateLockedIconsOverride = UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = ManualCapstoneApiName,
                CapstonesMaterialized = CapstonesMaterialized,
                AutoCapstoneGenerated = AutoCapstoneGenerated,
                Capstones = Capstones != null
                    ? Capstones.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                AchievementOrder = AchievementOrder != null
                    ? new List<string>(AchievementOrder)
                    : null,
                AchievementCategoryOverrides = AchievementCategoryOverrides != null
                    ? new Dictionary<string, string>(AchievementCategoryOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryTypeOverrides = AchievementCategoryTypeOverrides != null
                    ? new Dictionary<string, string>(AchievementCategoryTypeOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryOrder = AchievementCategoryOrder != null
                    ? new List<string>(AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(AchievementCategoryImageOverrides),
                GameSummaryCategory = GameSummaryCategory?.Clone(),
                FilteredAchievementApiNames = FilteredAchievementApiNames != null
                    ? new List<string>(FilteredAchievementApiNames)
                    : null,
                SummaryFilteredAchievementApiNames = SummaryFilteredAchievementApiNames != null
                    ? new List<string>(SummaryFilteredAchievementApiNames)
                    : null,
                GoalAchievementApiNames = GoalAchievementApiNames != null
                    ? new List<string>(GoalAchievementApiNames)
                    : null,
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementNotes = AchievementNotes != null
                    ? new Dictionary<string, string>(AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementOverrides = CloneAchievementOverrideMap(AchievementOverrides),
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                RetroAchievementsSelectedSubsetGameIds = RetroAchievementsSelectedSubsetGameIds != null
                    ? new List<int>(RetroAchievementsSelectedSubsetGameIds)
                    : null,
                XeniaTitleIdOverride = XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = ShadPS4MatchIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
                NotificationAppearanceOverride = NotificationAppearanceOverride?.Clone(),
                ProviderOverride = ProviderOverride?.Clone(),
                ExophaseEnrichmentSlugOverride = ExophaseEnrichmentSlugOverride,
                ManualLink = ManualLink?.Clone(),
                CustomAchievements = CustomAchievements != null
                    ? CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                // The provider snapshot is filled by the store at export time; the model has no
                // access to the custom provider catalog.
                CustomProviderId = CustomProviderId
            };
        }

        public static GameCustomDataFile FromPortable(
            GameCustomDataPortableFile portable,
            Guid playniteGameId,
            bool? excludedFromRefreshes,
            bool? excludedFromSummaries)
        {
            return new GameCustomDataFile
            {
                SchemaVersion = portable?.SchemaVersion > 0 ? portable.SchemaVersion : 8,
                PlayniteGameId = playniteGameId,
                ExcludedFromRefreshes = excludedFromRefreshes,
                ExcludedFromSummaries = excludedFromSummaries,
                UseSeparateLockedIconsOverride = portable?.UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = portable?.ManualCapstoneApiName,
                CapstonesMaterialized = portable?.CapstonesMaterialized ?? false,
                AutoCapstoneGenerated = portable?.AutoCapstoneGenerated ?? false,
                Capstones = portable?.Capstones != null
                    ? portable.Capstones.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                AchievementOrder = portable?.AchievementOrder != null
                    ? new List<string>(portable.AchievementOrder)
                    : null,
                AchievementCategoryOverrides = portable?.AchievementCategoryOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementCategoryOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryTypeOverrides = portable?.AchievementCategoryTypeOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementCategoryTypeOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementCategoryOrder = portable?.AchievementCategoryOrder != null
                    ? new List<string>(portable.AchievementCategoryOrder)
                    : null,
                AchievementCategoryImageOverrides = CloneCategoryImageOverrideMap(portable?.AchievementCategoryImageOverrides),
                GameSummaryCategory = portable?.GameSummaryCategory?.Clone(),
                FilteredAchievementApiNames = portable?.FilteredAchievementApiNames != null
                    ? new List<string>(portable.FilteredAchievementApiNames)
                    : null,
                SummaryFilteredAchievementApiNames = portable?.SummaryFilteredAchievementApiNames != null
                    ? new List<string>(portable.SummaryFilteredAchievementApiNames)
                    : null,
                GoalAchievementApiNames = portable?.GoalAchievementApiNames != null
                    ? new List<string>(portable.GoalAchievementApiNames)
                    : null,
                AchievementUnlockedIconOverrides = portable?.AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = portable?.AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementNotes = portable?.AchievementNotes != null
                    ? new Dictionary<string, string>(portable.AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementOverrides = CloneAchievementOverrideMap(portable?.AchievementOverrides),
                RetroAchievementsGameIdOverride = portable?.RetroAchievementsGameIdOverride,
                RetroAchievementsSelectedSubsetGameIds = portable?.RetroAchievementsSelectedSubsetGameIds != null
                    ? new List<int>(portable.RetroAchievementsSelectedSubsetGameIds)
                    : null,
                XeniaTitleIdOverride = portable?.XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = portable?.ShadPS4MatchIdOverride,
                ForceUseExophase = portable?.ForceUseExophase,
                ExophaseSlugOverride = portable?.ExophaseSlugOverride,
                NotificationAppearanceOverride = portable?.NotificationAppearanceOverride?.Clone(),
                ProviderOverride = portable?.ProviderOverride?.Clone(),
                ExophaseEnrichmentSlugOverride = portable?.ExophaseEnrichmentSlugOverride,
                ManualLink = portable?.ManualLink?.Clone(),
                CustomAchievements = portable?.CustomAchievements != null
                    ? portable.CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                CustomProviderId = portable?.CustomProviderId
            };
        }

        internal static Dictionary<string, CategoryImageOverrideData> CloneCategoryImageOverrideMap(
            IReadOnlyDictionary<string, CategoryImageOverrideData> source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new Dictionary<string, CategoryImageOverrideData>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null)
                {
                    continue;
                }

                clone[pair.Key] = pair.Value.Clone();
            }

            return clone.Count > 0 ? clone : null;
        }

        internal static Dictionary<string, AchievementOverride> CloneAchievementOverrideMap(
            IReadOnlyDictionary<string, AchievementOverride> source)
        {
            if (source == null)
            {
                return null;
            }

            var clone = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.IsEmpty)
                {
                    continue;
                }

                clone[pair.Key] = pair.Value.Clone();
            }

            return clone.Count > 0 ? clone : null;
        }
    }
}
