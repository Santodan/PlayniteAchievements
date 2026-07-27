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
        public int SchemaVersion { get; set; } = 5;

        public Guid PlayniteGameId { get; set; }

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

        public GameCustomDataPortableFile Clone()
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
                AchievementCategoryImageOverrides = GameCustomDataFile.CloneCategoryImageOverrideMap(AchievementCategoryImageOverrides),
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
    }
}
