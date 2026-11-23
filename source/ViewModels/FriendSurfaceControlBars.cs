using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The friend-list filter state a friends surface exposes to its friend summaries control bar.
    /// </summary>
    internal interface IFriendSummariesFilterSource : INotifyPropertyChanged
    {
        string FriendSearchText { get; set; }

        bool FavoritesOnly { get; set; }

        void ClearFriendSearch();
    }

    /// <summary>
    /// Control-bar pieces shared by the Friends Overview and View Friends' Achievements surfaces.
    /// </summary>
    internal static class FriendSurfaceControlBars
    {
        /// <summary>
        /// Builds the friend summaries control bar: friend search and the favorites toggle, then
        /// <paramref name="platformFilter"/> when the surface offers one.
        /// </summary>
        internal static GridControlBarViewModel CreateFriendSummariesControlBar(
            IFriendSummariesFilterSource source,
            GridControlBarItem platformFilter = null)
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    source,
                    nameof(IFriendSummariesFilterSource.FriendSearchText),
                    () => source.FriendSearchText,
                    value => source.FriendSearchText = value,
                    GridControlBarText.Get("LOCPlayAch_FriendsOverview_SearchFriends", "Search Friends"),
                    source.ClearFriendSearch)
                {
                    Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_FriendsOverview_SearchFriends"
                }
            };
            controlBar.Items.Add(new GridToggleFilter(
                source,
                nameof(IFriendSummariesFilterSource.FavoritesOnly),
                ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Favorites"),
                () => source.FavoritesOnly,
                value => source.FavoritesOnly = value,
                GridToggleFilterIcon.Favorite)
            {
                Id = PlayniteAchievements.Models.ShowcaseControlBarItems.Favorites,
                LabelKey = "LOCPlayAch_RefreshModeShort_Favorites",
                ClearedValue = false
            });
            if (platformFilter != null)
            {
                controlBar.Items.Add(platformFilter);
            }

            return controlBar;
        }

        /// <summary>
        /// The Compare dropdown candidates: every friend other than <paramref name="selectedFriend"/>,
        /// favorites first, then those for which <paramref name="hasPairData"/> holds, and
        /// otherwise in the order given. With no friend selected the rows belong to every friend,
        /// so none is left out.
        /// </summary>
        internal static IReadOnlyList<FriendSummaryItem> GetCompareFriendOptions(
            IEnumerable<FriendSummaryItem> friends,
            FriendSummaryItem selectedFriend,
            Func<FriendSummaryItem, bool> hasPairData)
        {
            // OrderBy is stable, so each group keeps the order the friends arrived in.
            return friends
                .Where(friend => friend != null &&
                    (selectedFriend == null ||
                     !FriendOverviewProjection.IsSameFriend(friend, selectedFriend)))
                .OrderBy(friend => friend.IsFavorite ? 0 : 1)
                .ThenBy(friend => hasPairData(friend) ? 0 : 1)
                .ToList();
        }
    }
}
