using System;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// The friends widgets' control bar: a search box and the Favorites toggle, the same items
    /// the Friends Overview's friend list offers. Each widget decides what the search matches.
    /// </summary>
    public abstract class FriendWidgetControlBarAdapter : SharedControlBarAdapter
    {
        private string _searchText = string.Empty;
        private bool _favoritesOnly;

        protected FriendWidgetControlBarAdapter()
        {
            ControlBar = CreateControlBar();
        }

        public override GridControlBarViewModel ControlBar { get; }

        /// <summary>The search box's placeholder and its row label in the widget settings.</summary>
        protected abstract string SearchLabelKey { get; }

        public string SearchText
        {
            get => _searchText;
            set
            {
                var normalized = value ?? string.Empty;
                if (string.Equals(_searchText, normalized, StringComparison.Ordinal))
                {
                    return;
                }

                _searchText = normalized;
                OnPropertyChanged(nameof(SearchText));
                RaiseFilterChanged();
            }
        }

        /// <summary>True to show only favorite friends' rows.</summary>
        public bool FavoritesOnly
        {
            get => _favoritesOnly;
            set
            {
                if (_favoritesOnly == value)
                {
                    return;
                }

                _favoritesOnly = value;
                OnPropertyChanged(nameof(FavoritesOnly));
                RaiseFilterChanged();
            }
        }

        public override ControlBarFilterState CaptureState() => new ControlBarFilterState
        {
            SearchText = SearchText,
            FavoritesOnly = FavoritesOnly ? true : (bool?)null
        };

        public override void RestoreState(ControlBarFilterState state)
        {
            if (state == null)
            {
                return;
            }

            _searchText = state.SearchText ?? string.Empty;
            _favoritesOnly = state.FavoritesOnly == true;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(FavoritesOnly));
        }

        private GridControlBarViewModel CreateControlBar()
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = new GridSearchControl(
                    this,
                    nameof(SearchText),
                    () => SearchText,
                    value => SearchText = value,
                    ResourceProvider.GetString(SearchLabelKey),
                    () => SearchText = string.Empty)
                {
                    Id = ShowcaseControlBarItems.Search,
                    LabelKey = SearchLabelKey
                }
            };
            controlBar.Items.Add(new GridToggleFilter(
                this,
                nameof(FavoritesOnly),
                ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Favorites"),
                () => FavoritesOnly,
                value => FavoritesOnly = value,
                GridToggleFilterIcon.Favorite)
            {
                Id = ShowcaseControlBarItems.Favorites,
                LabelKey = "LOCPlayAch_RefreshModeShort_Favorites",
                ClearedValue = false
            });
            return controlBar;
        }
    }

    /// <summary>The friend unlocks widget's bar; the search matches the friend, achievement and game.</summary>
    public sealed class FriendUnlocksControlBarAdapter : FriendWidgetControlBarAdapter
    {
        protected override string SearchLabelKey => "LOCPlayAch_Filter_Achievements";

        public override string StateKey => "ControlBar.FriendUnlocks";
    }

    /// <summary>The friends leaderboard's bar; the search matches the friend.</summary>
    public sealed class FriendLeaderboardControlBarAdapter : FriendWidgetControlBarAdapter
    {
        protected override string SearchLabelKey => "LOCPlayAch_FriendsOverview_SearchFriends";

        public override string StateKey => "ControlBar.FriendLeaderboard";
    }

    /// <summary>The friends' games widget's bar; the search matches the game.</summary>
    public sealed class FriendGamesControlBarAdapter : FriendWidgetControlBarAdapter
    {
        protected override string SearchLabelKey => "LOCPlayAch_Filter_Games";

        public override string StateKey => "ControlBar.FriendGames";
    }
}
