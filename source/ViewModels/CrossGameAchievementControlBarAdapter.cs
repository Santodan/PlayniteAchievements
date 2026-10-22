using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
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

        // Rarity and Trophy, which the Timeline's bar shares.
        private readonly RarityTrophyControlBarFilters _tierFilters;

        private readonly HashSet<string> _tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The rows of the last filter pass, which the tag options read the picked games' own tags from.
        private IReadOnlyList<AchievementDisplayItem> _tagOptionRows = Array.Empty<AchievementDisplayItem>();

        /// <summary>
        /// The tags offered: the global tags with no game picked, a picked game's tags (global, its
        /// own, and the implicit ones its rows carry), or the tags every picked game has.
        /// </summary>
        public IReadOnlyList<string> TagOptions =>
            AchievementTagScope.OptionKeys(
                GameFilter.SelectedGameIds,
                gameId => _tagOptionRows
                    .Where(row => row?.PlayniteGameId == gameId)
                    .SelectMany(row => AchievementTagHelper.GetComponents(row.Tags)));

        public string SelectedTagText
        {
            get
            {
                var names = _tags.Select(GetTagName).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToList();
                return names.Count == 0 ? ResourceProvider.GetString("LOCTagsLabel") : string.Join(", ", names);
            }
        }

        // A per-game tag resolves its name only on its game, so a picked game names them.
        private string GetTagName(string key) =>
            AchievementTagRegistry.GetName(key, AchievementTagScope.NamingGame(GameFilter.SelectedGameIds));

        private bool IsTagSelected(string key) => key != null && _tags.Contains(key);

        private void SetTagSelected(string key, bool selected)
        {
            if (string.IsNullOrWhiteSpace(key) || !(selected ? _tags.Add(key.Trim()) : _tags.Remove(key.Trim())))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedTagText));
            RaiseFilterChanged();
        }

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
            PendingPlatformSelections = null;
            OnPropertyChanged(nameof(SelectedProviderFilterText));
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
                Tags = _tags.Count > 0 ? _tags.ToList() : null,
                UnlockedFrom = _unlockedFrom,
                UnlockedTo = _unlockedTo
            };
            _tierFilters.Capture(state);
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
            _tags.Clear();
            foreach (var tag in state.Tags ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    _tags.Add(tag.Trim());
                }
            }

            OnPropertyChanged(nameof(SelectedTagText));
            _unlockedFrom = state.UnlockedFrom?.Date;
            _unlockedTo = state.UnlockedTo?.Date;
            OnPropertyChanged(nameof(UnlockedRangeKey));
            _tierFilters.Restore(state);
        }

        /// <summary>True while any filter would drop a row.</summary>
        public bool HasActiveFilters =>
            SearchQuery.From(SearchText).HasValue ||
            (ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>()).Any(group => group?.HasAnySelected == true) ||
            GameFilter.HasSelection ||
            _tags.Count > 0 ||
            _unlockedFrom.HasValue ||
            _unlockedTo.HasValue ||
            _tierFilters.HasSelection;

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
            if (_unlockedFrom.HasValue || _unlockedTo.HasValue)
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

            _tagOptionRows = items;
            if (_tags.Count > 0)
            {
                filtered = filtered.Where(item => _tags.Any(tag => AchievementTagHelper.Contains(item.Tags, tag)));
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
                nameof(SelectedTagText),
                () => SelectedTagText,
                () => TagOptions,
                IsTagSelected,
                SetTagSelected,
                GetTagName)
            {
                Id = ShowcaseControlBarItems.Tag,
                LabelKey = "LOCTagsLabel",
                // The options follow the picked games, so the item stays shown while they change.
                AutoHideWhenUnavailable = false,
                Width = 150
            });
            controlBar.Items.Add(new GridDateRangeFilter(
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
            });
            controlBar.Items.Add(_tierFilters.RarityItem);
            controlBar.Items.Add(_tierFilters.TrophyItem);
            return controlBar;
        }

        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            RaiseFilterChanged();
        }
    }
}
