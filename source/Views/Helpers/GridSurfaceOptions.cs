using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Views.Settings.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// The grid options record a grid's Display Settings edit, found from the grid's column
    /// settings key the same way the Display Settings menu resolves it; null for a key no surface
    /// owns.
    /// </summary>
    internal static class GridSurfaceOptions
    {
        public static GridCommonOptions Resolve(GridOptionKind kind, string columnSettingsKey)
        {
            var catalog = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions;
            if (catalog == null)
            {
                return null;
            }

            switch (kind)
            {
                case GridOptionKind.Achievement:
                    return GridOptionsCatalog.TryResolveAchievementId(columnSettingsKey, out var achievementId)
                        ? catalog.GetAchievement(achievementId)
                        : null;
                case GridOptionKind.GameSummaries:
                    return GridOptionsCatalog.TryResolveGameSummariesId(columnSettingsKey, out var gameId)
                        ? catalog.GetGameSummaries(gameId)
                        : null;
                case GridOptionKind.FriendSummaries:
                    return GridOptionsCatalog.TryResolveFriendSummariesId(columnSettingsKey, out var friendId)
                        ? catalog.GetFriendSummaries(friendId)
                        : null;
                default:
                    return null;
            }
        }
    }
}