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
    public sealed class CrossGameAchievementControlBarAdapter : PlayniteAchievements.Common.ObservableObject
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
        private readonly Dictionary<Guid, GameSummaryItem> _gamesById = new Dictionary<Guid, GameSummaryItem>();
        private IReadOnlyList<GameSummaryItem> _gamesSource;
        private string _searchText = string.Empty;
        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();

        public CrossGameAchievementControlBarAdapter()
        {
            ControlBar = CreateControlBar();
        }

        public event EventHandler FilterChanged;

        public GridControlBarViewModel ControlBar { get; }

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
        /// Sets the game summaries rows resolve their provider and platforms from. The lookup is
        /// rebuilt only when a different list arrives.
        /// </summary>
        public void UpdateGames(IReadOnlyList<GameSummaryItem> games)
        {
            if (ReferenceEquals(_gamesSource, games))
            {
                return;
            }

            _gamesSource = games;
            _gamesById.Clear();
            foreach (var game in games ?? Array.Empty<GameSummaryItem>())
            {
                if (game?.PlayniteGameId is Guid id && !_gamesById.ContainsKey(id))
                {
                    _gamesById[id] = game;
                }
            }
        }

        /// <summary>Rebuilds the dropdown from the games behind <paramref name="rows"/>, keeping selections.</summary>
        public void UpdateOptions(IEnumerable<AchievementDisplayItem> rows)
        {
            ProviderFilterGroups = ProviderFilterGroupBuilder.Rebuild(
                ResolveGames(rows),
                ProviderFilterGroups,
                OnProviderFilterSelectionChanged);
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            ControlBar.Refresh();
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

        private void RaiseFilterChanged()
        {
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
