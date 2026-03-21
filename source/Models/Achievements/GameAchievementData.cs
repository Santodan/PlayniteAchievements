using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Playnite.SDK.Models;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Models.Achievements
{
    /// <summary>
    /// User achievement data for a single game, combining schema metadata with user progress.
    /// </summary>
    public sealed class GameAchievementData
    {
        public DateTime LastUpdatedUtc { get; set; }

        /// <summary>
        /// Stable identifier for the provider (e.g., "PSN", "Steam").
        /// Used for database storage and internal lookups.
        /// </summary>
        public string ProviderKey { get; set; }

        /// <summary>
        /// Localized display name resolved from the active display provider key.
        /// Not persisted - computed at runtime for UI display.
        /// </summary>
        [IgnoreDataMember]
        public string ProviderDisplayName => ProviderRegistry.GetLocalizedName(EffectiveProviderKey);

        /// <summary>
        /// If this data is being fetched by a proxy provider (e.g. Exophase), this holds 
        /// the key of the original platform provider being overridden (e.g. "Xbox").
        /// Null if not applicable.
        /// </summary>
        public string ProviderPlatformKey { get; set; }

        /// <summary>
        /// The provider key to use for display purposes. Falls back to ProviderKey 
        /// if no proxy is currently active.
        /// </summary>
        [IgnoreDataMember]
        public string EffectiveProviderKey => !string.IsNullOrEmpty(ProviderPlatformKey) 
            ? ProviderPlatformKey 
            : ProviderKey;

        /// <summary>
        /// Playnite library source name for the game at refresh time (e.g. Steam, GOG).
        /// Best-effort metadata; may be empty for some entries.
        /// </summary>
        public string LibrarySourceName { get; set; }

        /// <summary>
        /// True if the game has achievements, False if scan found none.
        /// Updated by refresh operations based on scan results.
        /// Default is true so new stubs are not skipped during bulk scans.
        /// </summary>
        public bool HasAchievements { get; set; } = true;

        /// <summary>
        /// True if the user manually excluded this game from tracking.
        /// Set via context menu to exclude/include games.
        /// </summary>
        public bool ExcludedByUser { get; set; }

        /// <summary>
        /// True if AppId was set via manual user override rather than automatic detection.
        /// Used to indicate which games have manual RA ID overrides.
        /// </summary>
        public bool IsAppIdOverridden { get; set; }

        /// <summary>
        /// Computed completion status based on all achievements unlocked or capstone.
        /// </summary>
        public bool IsCompleted =>
            (Achievements?.Count > 0 && Achievements.All(a => a?.Unlocked == true)) ||
            IsCapstoneUnlocked();

        private bool IsCapstoneUnlocked()
        {
            if (Achievements == null || Achievements.Count == 0)
                return false;
            return Achievements.Any(a => a?.IsCapstone == true && a.Unlocked);
        }

        public string GameName { get; set; }

        public int AppId { get; set; }

        public Guid? PlayniteGameId { get; set; }

        /// <summary>
        /// Playnite Game reference, hydrated at load time from Playnite database.
        /// Not persisted to cache.
        /// </summary>
        [IgnoreDataMember]
        public Game Game { get; set; }

        /// <summary>
        /// Sorting name from Playnite's game database, derived from Game reference.
        /// Falls back to GameName if Game or SortingName is not available.
        /// </summary>
        [IgnoreDataMember]
        public string SortingName => Game?.SortingName ?? GameName;

        /// <summary>
        /// Runtime-only custom achievement order hydrated from plugin settings.
        /// Not persisted in cache/database.
        /// </summary>
        [IgnoreDataMember]
        public List<string> AchievementOrder { get; set; }

        public List<AchievementDetail> Achievements { get; set; } = new List<AchievementDetail>();

        /// <summary>
        /// Total count of achievements. Computed property for performance.
        /// </summary>
        [IgnoreDataMember]
        public int AchievementCount => Achievements?.Count ?? 0;

        /// <summary>
        /// Count of unlocked achievements. Computed property for performance.
        /// </summary>
        [IgnoreDataMember]
        public int UnlockedCount => Achievements?.Count(a => a.Unlocked) ?? 0;
    }
}
