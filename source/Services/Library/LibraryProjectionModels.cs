using PlayniteAchievements.Models.ThemeIntegration;

namespace PlayniteAchievements.Services.Library
{
    internal sealed class LibraryProjectionSnapshot
    {
        public LibraryRuntimeState LibraryState { get; set; }

        public bool UsedCachedSummary { get; set; }

        public int? HydratedGameCount { get; set; }
    }
}
