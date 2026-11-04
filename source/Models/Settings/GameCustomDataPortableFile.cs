using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Portable import/export representation for per-game custom data.
    /// Internal exclusion flags are intentionally omitted.
    /// </summary>
    public sealed class GameCustomDataPortableFile
    {
        /// <summary>Discriminator shared with the other portable package manifests.</summary>
        public const string GameCustomDataKind = "PlayniteAchievements.GameCustomData";

        /// <summary>
        /// Written on export; files from before the field read as null and are still accepted.
        /// </summary>
        public string Kind { get; set; }

        public int SchemaVersion { get; set; } = 8;

        public Guid PlayniteGameId { get; set; }

        /// <summary>
        /// How another machine can recognize the game this file is for. Import ignores it (the
        /// caller picks the target game); the workshop uses it to match a shared file to a
        /// library game.
        /// </summary>
        public List<PortableGameKey> GameKeys { get; set; }

        public bool? UseSeparateLockedIconsOverride { get; set; }

        public string ManualCapstoneApiName { get; set; }

        /// <inheritdoc cref="GameCustomDataFile.CapstonesMaterialized"/>
        public bool CapstonesMaterialized { get; set; }

        /// <inheritdoc cref="GameCustomDataFile.Capstones"/>
        public List<CapstoneAssignment> Capstones { get; set; }

        /// <inheritdoc cref="GameCustomDataFile.AutoCapstoneGenerated"/>
        public bool AutoCapstoneGenerated { get; set; }

        public List<string> AchievementOrder { get; set; }

        public Dictionary<string, string> AchievementCategoryOverrides { get; set; }

        public Dictionary<string, string> AchievementCategoryTypeOverrides { get; set; }

        public List<string> AchievementCategoryOrder { get; set; }

        public Dictionary<string, CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; }

        /// <summary>
        /// Kind of each category, keyed by category path (see CategoryKindHelper). A category absent
        /// here takes its kind from the provider; CategoryKindHelper.None says it has none.
        /// </summary>
        public Dictionary<string, string> AchievementCategoryKinds { get; set; }

        /// <summary>Tags only this game offers, alongside the global list in settings.</summary>
        public List<PlayniteAchievements.Models.Achievements.AchievementTagDefinition> AchievementTags { get; set; }

        public GameSummaryCategoryData GameSummaryCategory { get; set; }

        public List<string> FilteredAchievementApiNames { get; set; }

        public List<string> SummaryFilteredAchievementApiNames { get; set; }

        public List<string> GoalAchievementApiNames { get; set; }

        public Dictionary<string, string> AchievementUnlockedIconOverrides { get; set; }

        public Dictionary<string, string> AchievementLockedIconOverrides { get; set; }

        public Dictionary<string, string> AchievementNotes { get; set; }

        /// <summary>
        /// Per-achievement user customization, keyed by ApiName. Schema 8 onward; the legacy
        /// scalar maps above carry schema-7 exports and are folded in on import.
        /// </summary>
        public Dictionary<string, AchievementOverride> AchievementOverrides { get; set; }

        public int? RetroAchievementsGameIdOverride { get; set; }

        public List<int> RetroAchievementsSelectedSubsetGameIds { get; set; }

        public string XeniaTitleIdOverride { get; set; }

        public string ShadPS4MatchIdOverride { get; set; }

        public bool? ForceUseExophase { get; set; }

        public string ExophaseSlugOverride { get; set; }

        public GameNotificationAppearanceOverride NotificationAppearanceOverride { get; set; }

        public ProviderOverrideData ProviderOverride { get; set; }

        public string ExophaseEnrichmentSlugOverride { get; set; }

        public Dictionary<string, MetadataLinkData> MetadataLinks { get; set; }

        public MetadataChoices MetadataChoices { get; set; }

        public ManualAchievementLink ManualLink { get; set; }

        public List<CustomAchievementDefinition> CustomAchievements { get; set; }

        public string CustomProviderId { get; set; }

        /// <summary>
        /// Snapshot of the assigned custom provider (name, color, icon path data) so a package
        /// imported on another machine can recreate it. A local definition with the same id wins
        /// on import.
        /// </summary>
        public CustomProviderDefinition CustomProvider { get; set; }

        public GameCustomDataPortableFile Clone()
        {
            return new GameCustomDataPortableFile
            {
                Kind = Kind,
                SchemaVersion = SchemaVersion,
                PlayniteGameId = PlayniteGameId,
                GameKeys = GameKeys != null
                    ? GameKeys.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
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
                AchievementCategoryImageOverrides = GameCustomDataFile.CloneCategoryImageOverrideMap(AchievementCategoryImageOverrides),
                AchievementCategoryKinds = GameCustomDataFile.CloneCategoryKindMap(AchievementCategoryKinds),
                AchievementTags = GameCustomDataFile.CloneTagList(AchievementTags),
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
                AchievementOverrides = GameCustomDataFile.CloneAchievementOverrideMap(AchievementOverrides),
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
                MetadataLinks = MetadataLinkData.CloneMap(MetadataLinks),
                MetadataChoices = MetadataChoices?.Clone(),
                ManualLink = ManualLink?.Clone(),
                CustomAchievements = CustomAchievements != null
                    ? CustomAchievements.ConvertAll(item => item?.Clone()).FindAll(item => item != null)
                    : null,
                CustomProviderId = CustomProviderId,
                CustomProvider = CustomProvider?.Clone()
            };
        }
    }
}
