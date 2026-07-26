using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
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
        public int SchemaVersion { get; set; } = 5;

        public Guid PlayniteGameId { get; set; }

        public bool? ExcludedFromRefreshes { get; set; }

        public bool? ExcludedFromSummaries { get; set; }

        public bool? UseSeparateLockedIconsOverride { get; set; }

        public string ManualCapstoneApiName { get; set; }

        public List<string> AchievementOrder { get; set; }

        public Dictionary<string, string> AchievementCategoryOverrides { get; set; }

        public Dictionary<string, string> AchievementCategoryTypeOverrides { get; set; }

        public List<string> AchievementCategoryOrder { get; set; }

        public Dictionary<string, CategoryImageOverrideData> AchievementCategoryImageOverrides { get; set; }

        public GameSummaryCategoryData GameSummaryCategory { get; set; }

        public List<string> FilteredAchievementApiNames { get; set; }

        public List<string> SummaryFilteredAchievementApiNames { get; set; }

        public Dictionary<string, string> AchievementUnlockedIconOverrides { get; set; }

        public Dictionary<string, string> AchievementLockedIconOverrides { get; set; }

        public Dictionary<string, string> AchievementNotes { get; set; }

        public int? RetroAchievementsGameIdOverride { get; set; }

        public string XeniaTitleIdOverride { get; set; }

        public string ShadPS4MatchIdOverride { get; set; }

        public bool? ForceUseExophase { get; set; }

        public string ExophaseSlugOverride { get; set; }

        public ProviderOverrideData ProviderOverride { get; set; }

        public ManualAchievementLink ManualLink { get; set; }

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
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementNotes = AchievementNotes != null
                    ? new Dictionary<string, string>(AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                XeniaTitleIdOverride = XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = ShadPS4MatchIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
                ProviderOverride = ProviderOverride?.Clone(),
                ManualLink = ManualLink?.Clone()
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
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementNotes = AchievementNotes != null
                    ? new Dictionary<string, string>(AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                XeniaTitleIdOverride = XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = ShadPS4MatchIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
                ProviderOverride = ProviderOverride?.Clone(),
                ManualLink = ManualLink?.Clone()
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
                SchemaVersion = portable?.SchemaVersion > 0 ? portable.SchemaVersion : 5,
                PlayniteGameId = playniteGameId,
                ExcludedFromRefreshes = excludedFromRefreshes,
                ExcludedFromSummaries = excludedFromSummaries,
                UseSeparateLockedIconsOverride = portable?.UseSeparateLockedIconsOverride,
                ManualCapstoneApiName = portable?.ManualCapstoneApiName,
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
                AchievementUnlockedIconOverrides = portable?.AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = portable?.AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementNotes = portable?.AchievementNotes != null
                    ? new Dictionary<string, string>(portable.AchievementNotes, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = portable?.RetroAchievementsGameIdOverride,
                XeniaTitleIdOverride = portable?.XeniaTitleIdOverride,
                ShadPS4MatchIdOverride = portable?.ShadPS4MatchIdOverride,
                ForceUseExophase = portable?.ForceUseExophase,
                ExophaseSlugOverride = portable?.ExophaseSlugOverride,
                ProviderOverride = portable?.ProviderOverride?.Clone(),
                ManualLink = portable?.ManualLink?.Clone()
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
    }
}
