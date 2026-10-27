using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Achievements;
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
    public class CrossGameAchievementControlBarAdapter : SharedControlBarAdapter
    {
        private readonly SearchTextIndex<AchievementDisplayItem> _searchIndex =
            new SearchTextIndex<AchievementDisplayItem>(item =>
                SearchTextBuilder.ForRecentAchievement(item?.GameName, item?.DisplayName));
        private readonly Dictionary<Guid, GameSummaryItem> _gamesById = new Dictionary<Guid, GameSummaryItem>();
        private List<GameSummaryItem> _libraryGames;
        private string _searchText = string.Empty;
        private DateTime? _unlockedFrom;
        private DateTime? _unlockedTo;
        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();

        /// <summary>The unlock date range's start day, or null for an open start.</summary>
        public DateTime? UnlockedFrom => _unlockedFrom;

        /// <summary>The unlock date range's end day, or null for an open end.</summary>
        public DateTime? UnlockedTo => _unlockedTo;

        private GridDateRangeFilter _unlockDateItem;
        private bool _locksRows;

        /// <summary>
        /// True while the rows are locked achievements (Unlock Next): the unlock date range would
        /// drop every one, so it does not apply and its item hides. The range itself is kept.
        /// </summary>
        public bool LocksRows
        {
            get => _locksRows;
            set
            {
                if (_locksRows == value)
                {
                    return;
                }

                _locksRows = value;
                if (_unlockDateItem != null)
                {
                    _unlockDateItem.IsVisible = !value;
                }
            }
        }

        // The Category dropdown: the one game's categories, offered while the rows (within the
        // game filter) come from a single game, and empty, so hidden, while they span several.
        private readonly HashSet<string> _selectedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ObservableCollection<string> _categoryOptions = new ObservableCollection<string>();
        private IReadOnlyList<AchievementDisplayItem> _categoryOptionRows;

        public string SelectedCategoryText
        {
            get
            {
                var selected = _categoryOptions.Where(_selectedCategories.Contains).ToList();
                return selected.Count == 0
                    ? ResourceProvider.GetString("LOCCategoryLabel")
                    : string.Join(", ", selected.Select(AchievementCategoryHelper.ToCategoryLabelDisplayText));
            }
        }

        /// <summary>
        /// The whole achievement list of the game the rows show, so the Category dropdown also
        /// offers categories none of the shown rows fall in. Ignored while it is another game's.
        /// </summary>
        public void SetCategoryOptionRows(IReadOnlyList<AchievementDisplayItem> rows)
        {
            _categoryOptionRows = rows;
            RaiseFilterChanged();
        }

        // Rarity and Trophy, which the Timeline's bar shares.
        private readonly RarityTrophyControlBarFilters _tierFilters;

        // Tags, which the game, chart and pie bars share. Its options read the picked games' own
        // tags from the rows of the last filter pass.
        private readonly TagControlBarFilter _tagFilter;
        private IReadOnlyList<AchievementDisplayItem> _tagOptionRows = Array.Empty<AchievementDisplayItem>();

        public IReadOnlyList<string> TagOptions => _tagFilter.TagOptions;

        /// <summary>Raised for the range item; its from and to change together.</summary>
        public string UnlockedRangeKey => (_unlockedFrom?.Ticks ?? 0) + "-" + (_unlockedTo?.Ticks ?? 0);

        public void SetUnlockedRange(DateTime? from, DateTime? to)
        {
            from = from?.Date;
            to = to?.Date;
            if (Nullable.Equals(_unlockedFrom, from) && Nullable.Equals(_unlockedTo, to))
            {
                return;
            }

            _unlockedFrom = from;
            _unlockedTo = to;
            OnPropertyChanged(nameof(UnlockedRangeKey));
            RaiseFilterChanged();
        }

        public CrossGameAchievementControlBarAdapter()
        {
            _tierFilters = new RarityTrophyControlBarFilters(RaiseFilterChanged);
            _tagFilter = new TagControlBarFilter(
                RaiseFilterChanged,
                () => GameFilter.SelectedGameIds,
                gameId => _tagOptionRows
                    .Where(row => row?.PlayniteGameId == gameId)
                    .SelectMany(row => AchievementTagHelper.GetComponents(row.Tags)));
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
                ResourceProvider.GetString("LOCPlatformTitle"));

        /// <summary>
        /// Sets the library's game summaries: rows resolve their provider and platforms from them,
        /// and the dropdown lists every provider and platform among them, not only those in the
        /// current rows, so a platform stays selectable before any of its achievements appear.
        /// Nothing is rebuilt while the same games arrive again, which keeps the groups an open
        /// dropdown is showing.
        /// </summary>
        public void UpdateGames(IReadOnlyList<GameSummaryItem> games)
        {
            GameFilter.UpdateGames(games);
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
            SettlePendingPlatformSelections(ProviderFilterGroups);
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            GameFilter.SetPlatforms(ProviderFilterGroups);
            _tierFilters.UpdateGames(_libraryGames);

            ControlBar.Refresh();
        }

        public override string StateKey => "ControlBar.Achievements";

        public override ControlBarFilterState CaptureState()
        {
            var state = new ControlBarFilterState
            {
                SearchText = SearchText,
                Platforms = CapturePlatformSelections(ProviderFilterGroups),
                Games = GameFilter.Capture(),
                UnlockedFrom = _unlockedFrom,
                UnlockedTo = _unlockedTo,
                Categories = _selectedCategories.Count > 0 ? _selectedCategories.ToList() : null
            };
            _tierFilters.Capture(state);
            _tagFilter.Capture(state);
            return state;
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
            GameFilter.Restore(state.Games);
            _tagFilter.Restore(state);
            _unlockedFrom = state.UnlockedFrom?.Date;
            _unlockedTo = state.UnlockedTo?.Date;
            OnPropertyChanged(nameof(UnlockedRangeKey));
            _tierFilters.Restore(state);
            _selectedCategories.Clear();
            foreach (var category in state.Categories ?? Enumerable.Empty<string>())
            {
                _selectedCategories.Add(AchievementCategoryHelper.NormalizeCategoryOrDefault(category));
            }

            OnPropertyChanged(nameof(SelectedCategoryText));
        }

        /// <summary>True while any filter would drop a row.</summary>
        public bool HasActiveFilters =>
            SearchQuery.From(SearchText).HasValue ||
            (ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>()).Any(group => group?.HasAnySelected == true) ||
            GameFilter.HasSelection ||
            _tagFilter.HasSelection ||
            _unlockedFrom.HasValue ||
            _unlockedTo.HasValue ||
            _tierFilters.HasSelection ||
            _selectedCategories.Count > 0;

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

            // The unlock range keeps unlocked rows whose local unlock day falls in it.
            if (!_locksRows && (_unlockedFrom.HasValue || _unlockedTo.HasValue))
            {
                var span = new UnlockDaySpan(_unlockedFrom ?? DateTime.MinValue, _unlockedTo ?? DateTime.MaxValue.Date);
                filtered = filtered.Where(item =>
                    item.Unlocked &&
                    item.UnlockTimeUtc.HasValue &&
                    span.Contains(UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value)));
            }

            if (GameFilter.HasSelection)
            {
                filtered = filtered.Where(item => GameFilter.Matches(item.PlayniteGameId));
            }

            var gameRows = GameFilter.HasSelection
                ? items.Where(item => GameFilter.Matches(item.PlayniteGameId)).ToList()
                : items;
            if (UpdateCategoryOptions(gameRows) && _selectedCategories.Count > 0)
            {
                filtered = filtered.Where(item => _selectedCategories.Contains(
                    AchievementCategoryHelper.NormalizeCategoryOrDefault(item.CategoryLabel)));
            }

            _tagOptionRows = items;
            if (_tagFilter.HasSelection)
            {
                filtered = filtered.Where(_tagFilter.MatchesRow);
            }

            if (_tierFilters.HasSelection)
            {
                filtered = filtered.Where(_tierFilters.Matches);
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

        /// <summary>
        /// Lists the categories of the one game <paramref name="gameRows"/> come from, or none
        /// while they span several games. Returns whether the rows are one game's.
        /// </summary>
        private bool UpdateCategoryOptions(IReadOnlyList<AchievementDisplayItem> gameRows)
        {
            var gameIds = gameRows.Select(item => item.PlayniteGameId).Distinct().ToList();
            var gameId = gameIds.Count == 1 ? gameIds[0] : null;
            List<string> options;
            if (!gameId.HasValue)
            {
                options = new List<string>();
            }
            else
            {
                var source = _categoryOptionRows != null &&
                             _categoryOptionRows.Count > 0 &&
                             _categoryOptionRows[0]?.PlayniteGameId == gameId
                    ? _categoryOptionRows
                    : gameRows;
                options = AchievementCategoryFilterOrderHelper.BuildOrderedCategoryLabels(
                    source,
                    item => item?.CategoryLabel,
                    source
                        .Where(item => item != null && item.CategoryOrderIndex < int.MaxValue)
                        .OrderBy(item => item.CategoryOrderIndex)
                        .Select(item => item.CategoryLabel));
            }

            var changed = !options.SequenceEqual(_categoryOptions, StringComparer.OrdinalIgnoreCase);
            if (changed)
            {
                CollectionHelper.SynchronizeCollection(_categoryOptions, options);
            }

            // A selection only means something within the game it was made for.
            var optionSet = new HashSet<string>(options, StringComparer.OrdinalIgnoreCase);
            changed |= _selectedCategories.RemoveWhere(category => !optionSet.Contains(category)) > 0;
            if (changed)
            {
                OnPropertyChanged(nameof(SelectedCategoryText));
                ControlBar?.Refresh();
            }

            return gameId.HasValue;
        }

        private void SetCategorySelected(string category, bool isSelected)
        {
            if (string.IsNullOrWhiteSpace(category))
            {
                return;
            }

            var normalized = AchievementCategoryHelper.NormalizeCategoryOrDefault(category);
            if (isSelected ? _selectedCategories.Add(normalized) : _selectedCategories.Remove(normalized))
            {
                OnPropertyChanged(nameof(SelectedCategoryText));
                RaiseFilterChanged();
            }
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
                {
                    Id = ShowcaseControlBarItems.Search,
                    LabelKey = "LOCPlayAch_Filter_Achievements"
                }
            };
            controlBar.Items.Add(new GridProviderPlatformFilter(
                this,
                nameof(SelectedProviderFilterText),
                () => SelectedProviderFilterText,
                () => ProviderFilterGroups,
                CollapseUnselectedProviderFilters)
            {
                Id = ShowcaseControlBarItems.Platform,
                LabelKey = "LOCPlatformTitle",
                Width = 170
            });
            controlBar.Items.Add(GameFilter.Item);
            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedCategoryText),
                () => SelectedCategoryText,
                () => _categoryOptions,
                category => _selectedCategories.Contains(AchievementCategoryHelper.NormalizeCategoryOrDefault(category)),
                SetCategorySelected,
                AchievementCategoryHelper.ToCategoryLabelDisplayText)
            {
                Id = ShowcaseControlBarItems.Category,
                LabelKey = "LOCCategoryLabel",
                Width = 140,
                IsCategoryFilter = true,
                RendersCategoryTree = true,
                ToolTip = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_Filter_Label")
            });
            controlBar.Items.Add(_tagFilter.Item);
            _unlockDateItem = new GridDateRangeFilter(
                this,
                nameof(UnlockedRangeKey),
                () => UnlockedFrom,
                () => UnlockedTo,
                SetUnlockedRange,
                ResourceProvider.GetString("LOCPlayAch_Filter_AllTime"))
            {
                Id = ShowcaseControlBarItems.UnlockDate,
                LabelKey = "LOCPlayAch_Common_Unlocked",
                AutoHideWhenUnavailable = false,
                Width = 190
            };
            controlBar.Items.Add(_unlockDateItem);
            controlBar.Items.Add(_tierFilters.RarityItem);
            controlBar.Items.Add(_tierFilters.TrophyItem);
            return controlBar;
        }

        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            GameFilter.SetPlatforms(ProviderFilterGroups);
            RaiseFilterChanged();
        }
    }
}
