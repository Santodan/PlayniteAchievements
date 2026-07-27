using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.Library
{
    internal sealed class LibraryProjectionSnapshot
    {
        public OverviewDataSnapshot OverviewSnapshot { get; set; }

        public LibraryRuntimeState LibraryState { get; set; }

        public bool UsedCachedSummary { get; set; }

        public int? HydratedGameCount { get; set; }
    }
}
