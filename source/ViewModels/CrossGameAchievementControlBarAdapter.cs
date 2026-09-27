using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Control bar for achievement rows drawn from many games (the showcase achievement grids and
    /// the achievement mosaic): a search box plus the provider/platform dropdown. An achievement
    /// row carries no platform of its own, so the dropdown and its filter go through the game
    /// summary each row belongs to.
    /// </summary>
    public sealed class CrossGameAchievementControlBarAdapter : SharedControlBarAdapter
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
        private readonly Dictionary<Guid, GameSummaryItem> _gamesById = new Dictionary<Guid, GameSummaryItem>();
        private List<GameSummaryItem> _libraryGames;
        private string _searchText = string.Empty;
        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();

        public CrossGameAchievementControlBarAdapter()
        {
            ControlBar = CreateControlBar();
        }

        public override GridControlBarViewModel ControlBar { get; }

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

        public ObservableCollection<ProviderFilterGroup> ProviderFilterGroups
        {
            get => _providerFilterGroups;
            private set => SetValue(ref _providerFilterGroups, value ?? new ObservableCollection<ProviderFilterGroup>());
        }

        public string SelectedProviderFilterText =>
            OverviewGameSummaryFilters.BuildProviderFilterText(
                ProviderFilterGroups,
                ResourceProvider.GetString("LOCPlayAch_Common_Label_Platform"));

        /// <summary>
        /// Sets the library's game summaries: rows resolve their provider and platforms from them,
        /// and the dropdown lists every provider and platform among them, not only those in the
        /// current rows, so a platform stays selectable before any of its achievements appear.
        /// Nothing is rebuilt while the same games arrive again, which keeps the groups an open
        /// dropdown is showing.
        /// </summary>
        public void UpdateGames(IReadOnlyList<GameSummaryItem> games)
        {
            games = games ?? Array.Empty<GameSummaryItem>();
            if (ProviderFilterGroupBuilder.HasSameGames(_libraryGames, games))
            {
                return;
            }

            _libraryGames = games.ToList();
            _gamesById.Clear();
            foreach (var game in _libraryGames)
            {
                if (game?.PlayniteGameId is Guid id && !_gamesById.ContainsKey(id))
                {
                    _gamesById[id] = game;
                }
            }

            ProviderFilterGroups = ProviderFilterGroupBuilder.Rebuild(
                _libraryGames,
                ProviderFilterGroups,
                OnProviderFilterSelectionChanged,
                PendingPlatformSelections);
            PendingPlatformSelections = null;
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            ControlBar.Refresh();
        }

        public override string StateKey => "ControlBar.Achievements";

        public override ControlBarFilterState CaptureState()
        {
            return new ControlBarFilterState
            {
                SearchText = SearchText,
                Platforms = CapturePlatformSelections(ProviderFilterGroups)
            };
        }

        public override void RestoreState(ControlBarFilterState state)
        {
            if (state == null)
            {
                return;
            }

            _searchText = state.SearchText ?? string.Empty;
            OnPropertyChanged(nameof(SearchText));
            PendingPlatformSelections = state.Platforms;
        }

        /// <summary>
        /// Applies the search, then the provider/platform selection. While a platform is selected,
        /// a row passes only when its game passes, so rows with no known game drop out.
        /// </summary>
        public IReadOnlyList<AchievementDisplayItem> Apply(IEnumerable<AchievementDisplayItem> source)
        {
            var items = (source ?? Enumerable.Empty<AchievementDisplayItem>())
                .Where(item => item != null)
                .ToList();

            IEnumerable<AchievementDisplayItem> filtered = items;
            var searchQuery = SearchQuery.From(SearchText);
            if (searchQuery.HasValue)
            {
                _searchIndex.Rebuild(items);
                filtered = filtered.Where(item => _searchIndex.Matches(item, searchQuery));
            }

            if ((ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>()).Any(group => group?.HasAnySelected == true))
            {
                var passingGameIds = new HashSet<Guid>(
                    OverviewGameSummaryFilters.ApplyProviderPlatformFilter(ResolveGames(items), ProviderFilterGroups)
                        .Select(game => game.PlayniteGameId.Value));
                filtered = filtered.Where(item =>
                    item.PlayniteGameId is Guid id && passingGameIds.Contains(id));
            }

            return filtered.ToList();
        }

        public void CollapseUnselectedProviderFilters()
        {
            foreach (var group in ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
            {
                if (!group.HasAnySelected)
                {
                    group.IsExpanded = false;
                }
            }
        }

        private List<GameSummaryItem> ResolveGames(IEnumerable<AchievementDisplayItem> rows)
        {
            var seen = new HashSet<Guid>();
            var games = new List<GameSummaryItem>();
            foreach (var row in rows ?? Enumerable.Empty<AchievementDisplayItem>())
            {
                if (row?.PlayniteGameId is Guid id &&
                    seen.Add(id) &&
                    _gamesById.TryGetValue(id, out var game))
                {
                    games.Add(game);
                }
            }

            return games;
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
                    ResourceProvider.GetString("LOCPlayAch_Filter_Achievements"),
                    () => SearchText = string.Empty)
            };
            controlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedProviderFilterText),
                () => SelectedProviderFilterText,
                () => ProviderFilterGroups,
                CollapseUnselectedProviderFilters)
            {
                Width = 170
            });
            return controlBar;
        }

        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            RaiseFilterChanged();
        }
    }
}
