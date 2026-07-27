using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    internal sealed class FriendRuntimeState
    {
        public static FriendRuntimeState Empty { get; } = new FriendRuntimeState(null);

        public FriendRuntimeState(FriendsOverviewData data, PersistedSettings settings = null)
        {
            Projection = new FriendOverviewProjection(data, settings);
        }

        public FriendRuntimeState(FriendOverviewProjection projection)
        {
            Projection = projection ?? new FriendOverviewProjection(null);
        }

        public FriendOverviewProjection Projection { get; }

        public bool HasData => Projection?.HasData == true;

        public IReadOnlyList<FriendSummaryItem> Friends =>
            Projection?.Friends ?? new List<FriendSummaryItem>();

        public IReadOnlyList<FriendGameSummaryItem> AggregateGames =>
            Projection?.AggregateGames ?? new List<FriendGameSummaryItem>();

        public IReadOnlyList<FriendAchievementDisplayItem> RecentUnlocks =>
            Projection?.RecentUnlocks ?? new List<FriendAchievementDisplayItem>();

        public IReadOnlyList<FriendAchievementDisplayItem> AllAchievements =>
            Projection?.AllAchievements ?? new List<FriendAchievementDisplayItem>();

        public IReadOnlyList<FriendAchievementDisplayItem> AllUnlockedAchievements =>
            Projection?.AllUnlockedAchievements ?? new List<FriendAchievementDisplayItem>();
    }
}
