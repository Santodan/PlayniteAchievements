using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Internal storage representation for per-game custom data.
    /// </summary>
    public sealed class GameCustomDataFile
    {
        public int SchemaVersion { get; set; } = 1;

        public Guid PlayniteGameId { get; set; }

        public bool? ExcludedFromRefreshes { get; set; }

        public bool? ExcludedFromSummaries { get; set; }

        public bool? UseSeparateLockedIconsOverride { get; set; }

        public string ManualCapstoneApiName { get; set; }

        public List<string> AchievementOrder { get; set; }

        public Dictionary<string, string> AchievementCategoryOverrides { get; set; }

        public Dictionary<string, string> AchievementCategoryTypeOverrides { get; set; }

        public Dictionary<string, string> AchievementUnlockedIconOverrides { get; set; }

        public Dictionary<string, string> AchievementLockedIconOverrides { get; set; }

        public int? RetroAchievementsGameIdOverride { get; set; }

        public bool? ForceUseExophase { get; set; }

        public string ExophaseSlugOverride { get; set; }

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
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
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
                AchievementUnlockedIconOverrides = AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = RetroAchievementsGameIdOverride,
                ForceUseExophase = ForceUseExophase,
                ExophaseSlugOverride = ExophaseSlugOverride,
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
                SchemaVersion = portable?.SchemaVersion > 0 ? portable.SchemaVersion : 1,
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
                AchievementUnlockedIconOverrides = portable?.AchievementUnlockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementUnlockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                AchievementLockedIconOverrides = portable?.AchievementLockedIconOverrides != null
                    ? new Dictionary<string, string>(portable.AchievementLockedIconOverrides, StringComparer.OrdinalIgnoreCase)
                    : null,
                RetroAchievementsGameIdOverride = portable?.RetroAchievementsGameIdOverride,
                ForceUseExophase = portable?.ForceUseExophase,
                ExophaseSlugOverride = portable?.ExophaseSlugOverride,
                ManualLink = portable?.ManualLink?.Clone()
            };
        }
    }
}
