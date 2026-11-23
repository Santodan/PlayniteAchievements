using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>The theme's friend lists.</summary>
    internal sealed class FriendRuntimeState
    {
        public static FriendRuntimeState Empty { get; } = new FriendRuntimeState(null);

        public FriendRuntimeState(FriendsOverviewLists lists)
        {
            Lists = lists ?? FriendsOverviewLists.Empty;
        }

        public FriendsOverviewLists Lists { get; }

        public bool HasData => Lists.HasData;

        public IReadOnlyList<FriendSummaryItem> Friends => Lists.Friends;

        public IReadOnlyList<FriendGameSummaryItem> AggregateGames => Lists.Games;
    }
}
