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
        public int SchemaVersion { get; set; } = 1;

        public Guid PlayniteGameId { get; set; }

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
    }
}
