using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.Settings
{
    public partial class PersistedSettings
    {
        public HashSet<Guid> DisabledRealtimeNotificationGameIds { get; set; } = new HashSet<Guid>();

        public Dictionary<Guid, string> PreferredProviderOverrides { get; set; } = new Dictionary<Guid, string>();

        public string ExtraLocalPaths { get; set; } = string.Empty;

        public string ExcludedLocalPaths { get; set; } = string.Empty;

        public int LastAllGamesCollectorScore { get; set; }

        public int LastAllGamesCollectorLevel { get; set; }

        public double LastAllGamesCollectorLevelProgress { get; set; }

        public string LastAllGamesCollectorRank { get; set; } = "Bronze1";

        public int LastAllGamesPrestigeScore { get; set; }

        public int LastAllGamesPrestigeLevel { get; set; }

        public double LastAllGamesPrestigeLevelProgress { get; set; }

        public string LastAllGamesPrestigeRank { get; set; } = "Bronze1";

        public CompactListSortMode DefaultAchievementSortMode { get; set; } = CompactListSortMode.None;

        public bool DefaultAchievementSortDescending { get; set; } = true;

        public string CustomSortPath { get; set; }

        public bool CustomSortDescending { get; set; } = true;

        public string GamesOverviewCustomSortPath { get; set; }

        public bool GamesOverviewCustomSortDescending { get; set; } = true;

        public string GamesOverviewCustomSecondarySorts { get; set; } = string.Empty;

        public bool GamesOverviewCustomSortUsesSourceOrder { get; set; }

        public string RecentAchievementsCustomSortPath { get; set; }

        public bool RecentAchievementsCustomSortDescending { get; set; } = true;

        public string RecentAchievementsCustomSecondarySorts { get; set; } = string.Empty;

        public bool RecentAchievementsCustomSortUsesSourceOrder { get; set; }

        public string SidebarAllAchievementsCustomSortPath { get; set; }

        public bool SidebarAllAchievementsCustomSortDescending { get; set; } = true;

        public string SidebarAllAchievementsCustomSecondarySorts { get; set; } = string.Empty;

        public bool SidebarAllAchievementsCustomSortUsesSourceOrder { get; set; }

        public string SidebarSelectedGameCustomSortPath { get; set; }

        public bool SidebarSelectedGameCustomSortDescending { get; set; } = true;

        public string SidebarSelectedGameCustomSecondarySorts { get; set; } = string.Empty;

        public bool SidebarSelectedGameCustomSortUsesSourceOrder { get; set; }
        public string DefaultUnlockNotificationStyle { get; set; } = string.Empty;

        public Dictionary<string, string> ProviderUnlockNotificationStyles { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string LastUpstreamReleaseNotificationVersion { get; set; } = string.Empty;

        public string LastForkReleaseNotificationVersion { get; set; } = string.Empty;
        public bool EnableGridTextWrapping { get; set; }

        public bool EnableCompactGridMode { get; set; }

        public string SidebarDefaultRefreshModeKey { get; set; } = string.Empty;

        public string SidebarDefaultPlayStatusFilter { get; set; } = "played";

        public string OverviewProviderFilterKeys { get; set; } = string.Empty;

        public string OverviewCompletenessFilterKeys { get; set; } = "complete;inprogress";

        public string OverviewPlayStatusFilterKeys { get; set; } = string.Empty;
        public GamesOverviewSortMode GamesOverviewGridSortMode { get; set; } = GamesOverviewSortMode.RecentUnlock;

        public bool GamesOverviewGridSortDescending { get; set; } = true;

        public CompactListSortMode SidebarSelectedGameGridSortMode { get; set; } = CompactListSortMode.UnlockTime;

        public bool SidebarSelectedGameGridSortDescending { get; set; } = true;
    }
}
