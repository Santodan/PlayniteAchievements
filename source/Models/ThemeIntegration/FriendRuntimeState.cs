using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Models.ThemeIntegration
{
    /// <summary>The theme's friend lists and the achievement rows of its friend scope.</summary>
    internal sealed class FriendRuntimeState
    {
        public static FriendRuntimeState Empty { get; } = new FriendRuntimeState(null, null);

        public FriendRuntimeState(FriendsOverviewLists lists, IReadOnlyList<FriendAchievementDisplayItem> rows)
        {
            Lists = lists ?? FriendsOverviewLists.Empty;
            Rows = rows ?? Array.Empty<FriendAchievementDisplayItem>();
        }

        public FriendsOverviewLists Lists { get; }

        public IReadOnlyList<FriendAchievementDisplayItem> Rows { get; }

        public bool HasData => Lists.HasData;

        public IReadOnlyList<FriendSummaryItem> Friends => Lists.Friends;

        public IReadOnlyList<FriendGameSummaryItem> AggregateGames => Lists.Games;
    }
}
