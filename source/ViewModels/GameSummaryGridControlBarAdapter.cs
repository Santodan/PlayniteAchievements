using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Search;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    public class GameSummaryGridControlBarAdapter : SharedControlBarAdapter
    {
        private readonly bool _chartFilters;
        private readonly SearchTextIndex<GameSummaryItem> _searchIndex =
            new SearchTextIndex<GameSummaryItem>(item =>
                SearchTextBuilder.ForGameSummary(item?.GameName));
        private readonly HashSet<string> _selectedProgressFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedActivityFilters =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly bool _includeProgress;
        private readonly bool _includeNoProgress;

        // Held so the weak subscription lives as long as this bar.
        private readonly Action _onChoicesChanged;
        private string _searchText = string.Empty;
        private List<GameSummaryItem> _optionGames;
        private ObservableCollection<ProviderFilterGroup> _providerFilterGroups =
            new ObservableCollection<ProviderFilterGroup>();

        public GameSummaryGridControlBarAdapter()
            : this(chartFilters: false)
        {
        }

        /// <param name="chartFilters">
        /// The reduced bar for distribution charts: platform, plus progress limited to Complete
        /// and In Progress. Search, No Progress and activity are left out, and their saved state
        /// is not restored.
        /// </param>
        /// <param name="includeNoProgress">True to offer No Progress in a chart bar's progress filter too.</param>
        /// <param name="includeProgress">False to leave the progress filter out as well.</param>
        /// <param name="achievementFilters">
        /// True to add the Tags filter after Game and the Rarity and Trophy filters last. They
        /// narrow which achievements count, never which games: <see cref="Apply"/> ignores them,
        /// and the widget counts the kept games' achievements through
        /// <see cref="MatchesAchievement"/> and <see cref="MatchesTally"/>.
        /// </param>
        protected GameSummaryGridControlBarAdapter(
            bool chartFilters,
            bool includeProgress = true,
            bool achievementFilters = false,
            bool includeNoProgress = false)
        {
            _chartFilters = chartFilters;
            _includeProgress = includeProgress;
            if (achievementFilters)
            {
                TagFilter = new TagControlBarFilter(
                    RaiseFilterChanged,
                    () => GameFilter.SelectedGameIds,
                    GameTagKeys);
                TierFilters = new RarityTrophyControlBarFilters(RaiseFilterChanged);
            }

            _includeNoProgress = !chartFilters || includeNoProgress;
            UpdateProgressOptions();
            ActivityFilterOptions = new ObservableCollection<string>
            {
                PlayedLabel,
                UnplayedLabel
            };
            ControlBar = CreateControlBar();
            _onChoicesChanged = OnProgressChoicesChanged;
            ProgressFilterChoices.Subscribe(_onChoicesChanged);
        }

        public override GridControlBarViewModel ControlBar { get; }

        // New Achievement styles change the choices offered and which games a pick keeps.
        private void OnProgressChoicesChanged()
        {
            UpdateProgressOptions();
            ControlBar.Refresh();
            if (_selectedProgressFilters.Count > 0)
            {
                RaiseFilterChanged();
            }
        }

        /// <summary>The bar's Tags filter; null for a bar without achievement filters.</summary>
        public TagControlBarFilter TagFilter { get; }

        /// <summary>The bar's Rarity and Trophy filters; null for a bar without achievement filters.</summary>
        public RarityTrophyControlBarFilters TierFilters { get; }

        /// <summary>
        /// False while the widget counts games, not achievements: the achievement filters then
        /// hide and do not apply, while their picks are kept for when it counts achievements again.
        /// </summary>
        public bool AchievementFiltersApply { get; private set; } = true;

        /// <summary>True while an achievement filter applies and would drop an achievement.</summary>
        public bool HasAchievementSelection =>
            AchievementFiltersApply &&
            (TagFilter?.HasSelection == true || TierFilters?.HasSelection == true);

        /// <summary>Shows and applies the achievement filters, or hides and sets them aside.</summary>
        public void SetAchievementFiltersApply(bool apply)
        {
            AchievementFiltersApply = apply;
            if (TagFilter != null)
            {
                TagFilter.Item.IsVisible = apply;
            }

            if (TierFilters != null)
            {
                TierFilters.RarityItem.IsVisible = apply;
                TierFilters.TrophyItem.IsVisible = apply;
            }
        }

        /// <summary>True for an achievement row the applying achievement filters keep.</summary>
        public bool MatchesAchievement(AchievementDisplayItem row) =>
            !AchievementFiltersApply ||
            ((TagFilter?.MatchesRow(row) ?? true) && (TierFilters?.Matches(row) ?? true));

        /// <summary>True for a tally whose achievements the applying achievement filters keep.</summary>
        public bool MatchesTally(PlayniteAchievements.Models.Achievements.AchievementTally tally) =>
            !AchievementFiltersApply ||
            ((TagFilter?.MatchesTally(tally) ?? true) && (TierFilters?.Matches(tally) ?? true));

        /// <summary>True while the applying achievement filters keep the completions count.</summary>
        public bool KeepsCompletions =>
            !AchievementFiltersApply || (TierFilters?.KeepsCompletions ?? true);

        // The tag keys a game's achievements carry, locked or not, from the games the options list.
        private IEnumerable<string> GameTagKeys(Guid gameId) =>
            (_optionGames ?? new List<GameSummaryItem>())
                .Where(game => game?.PlayniteGameId == gameId)
                .SelectMany(game => game.Tallies ?? Array.Empty<PlayniteAchievements.Models.Achievements.AchievementTally>())
                .SelectMany(tally => tally.Tags);

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
                L("LOCPlayAch_Common_Platform"));

        /// <summary>
        /// The Progress choices as keys: the finishes the bar's games show, in Achievement styles
        /// order, then In Progress and (unless a chart bar leaves it out) No Progress.
        /// </summary>
        public IReadOnlyList<string> ProgressFilterOptions => _progressOptions;

        private IReadOnlyList<string> _progressOptions = Array.Empty<string>();
        private List<GameSummaryItem> _progressGames;

        // Rebuilds the choices; true when they changed.
        private bool UpdateProgressOptions()
        {
            var options = ProgressFilterChoices.Keys(
                ProgressFilterChoices.CurrentOrder(),
                _includeNoProgress,
                _progressGames);
            if (options.SequenceEqual(_progressOptions, StringComparer.Ordinal))
            {
                return false;
            }

            _progressOptions = options;
            OnPropertyChanged(nameof(SelectedProgressFilterText));
            return true;
        }

        public string SelectedProgressFilterText
        {
            get
            {
                var picked = ProgressFilterOptions
                    .Where(_selectedProgressFilters.Contains)
                    .Select(ProgressFilterChoices.Label)
                    .ToList();
                return picked.Count == 0 ? L("LOCPlayAch_Progress") : string.Join(", ", picked);
            }
        }

        public ObservableCollection<string> ActivityFilterOptions { get; }

        public string SelectedActivityFilterText => GetSelectedFilterText(
            _selectedActivityFilters,
            ActivityFilterOptions,
            L("LOCPlayAch_Filter_ActivitySelectorPlaceholder"));

        public IReadOnlyList<GameSummaryItem> Apply(IEnumerable<GameSummaryItem> source)
        {
            var items = (source ?? Enumerable.Empty<GameSummaryItem>())
                .Where(item => item != null)
                .ToList();

            IEnumerable<GameSummaryItem> filtered = items;
            var searchQuery = SearchQuery.From(SearchText);
            if (searchQuery.HasValue)
            {
                _searchIndex.Rebuild(items);
                filtered = filtered.Where(item => _searchIndex.Matches(item, searchQuery));
            }

            filtered = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(filtered, ProviderFilterGroups);
            if (GameFilter.HasSelection)
            {
                filtered = filtered.Where(item => GameFilter.Matches(item.PlayniteGameId));
            }

            filtered = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                filtered,
                _selectedActivityFilters,
                null,
                PlayedLabel,
                UnplayedLabel);
            var order = ProgressFilterChoices.CurrentOrder();
            var matchesProgress = ProgressFilterChoices.Matcher(
                _selectedProgressFilters,
                ProgressFilterChoices.Keys(order, _includeNoProgress),
                order);
            if (matchesProgress != null)
            {
                filtered = filtered.Where(matchesProgress);
            }

            return filtered.ToList();
        }

        public void UpdateOptions(IEnumerable<GameSummaryItem> source)
        {
            // A filter pass re-feeds the same games; rebuilding then would swap the groups out
            // from under an open dropdown, so a toggle there would land on a discarded group.
            GameFilter.UpdateGames(source);
            var games = (source ?? Enumerable.Empty<GameSummaryItem>()).ToList();
            _progressGames = source == null ? null : games;
            var progressChanged = UpdateProgressOptions();
            if (TierFilters?.UpdateGames(games) == true || progressChanged)
            {
                ControlBar.Refresh();
            }

            if (ProviderFilterGroupBuilder.HasSameGames(_optionGames, games))
            {
                return;
            }

            // A new snapshot re-feeds new instances of the same games on every edit. When they
            // would build the same groups, keep the groups and only track the new list. Restored
            // selections still pending name providers the last groups lacked, which the same
            // options lack too, so they have nothing to seed.
            if (ProviderFilterGroupBuilder.HasSameFilterOptions(_optionGames, games))
            {
                _optionGames = games;
                return;
            }

            _optionGames = games;
            ProviderFilterGroups = ProviderFilterGroupBuilder.Rebuild(
                games,
                ProviderFilterGroups,
                OnProviderFilterSelectionChanged,
                PendingPlatformSelections);
            SettlePendingPlatformSelections(ProviderFilterGroups);
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            GameFilter.SetPlatforms(ProviderFilterGroups);
            ControlBar.Refresh();
        }

        public override string StateKey => "ControlBar.Games";

        public override ControlBarFilterState CaptureState()
        {
            var state = new ControlBarFilterState
            {
                SearchText = SearchText,
                Platforms = CapturePlatformSelections(ProviderFilterGroups),
                Games = GameFilter.Capture(),
                ProgressKeys = _selectedProgressFilters.ToList(),
                Activity = SelectedPositions(ActivityFilterOptions, _selectedActivityFilters)
            };
            TagFilter?.Capture(state);
            TierFilters?.Capture(state);
            return state;
        }

        public override void RestoreState(ControlBarFilterState state)
        {
            if (state == null)
            {
                return;
            }

            _searchText = _chartFilters ? string.Empty : state.SearchText ?? string.Empty;
            PendingPlatformSelections = state.Platforms;
            GameFilter.Restore(state.Games);
            TagFilter?.Restore(state);
            TierFilters?.Restore(state);
            _selectedProgressFilters.Clear();
            if (_includeProgress)
            {
                foreach (var key in state.ProgressKeys ?? ProgressFilterChoices.FromLegacyPositions(state.Progress))
                {
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        _selectedProgressFilters.Add(key);
                    }
                }
            }
            RestorePositions(
                ActivityFilterOptions,
                _selectedActivityFilters,
                _chartFilters ? null : state.Activity);
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(SelectedProgressFilterText));
            OnPropertyChanged(nameof(SelectedActivityFilterText));
        }

        private static List<int> SelectedPositions(IList<string> options, HashSet<string> selected)
        {
            var positions = new List<int>();
            for (var i = 0; i < options.Count; i++)
            {
                if (selected.Contains(options[i]))
                {
                    positions.Add(i);
                }
            }

            return positions;
        }

        private static void RestorePositions(IList<string> options, HashSet<string> selected, IEnumerable<int> positions)
        {
            selected.Clear();
            foreach (var position in positions ?? Enumerable.Empty<int>())
            {
                if (position >= 0 && position < options.Count && !string.IsNullOrWhiteSpace(options[position]))
                {
                    selected.Add(options[position].Trim());
                }
            }
        }

        public void Clear()
        {
            _searchIndex.Clear();
            _optionGames = null;
            UpdateOptions(null);
        }

        public bool IsProgressFilterSelected(string value)
        {
            return IsFilterSelected(_selectedProgressFilters, value);
        }

        public void SetProgressFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedProgressFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedProgressFilterText));
            RaiseFilterChanged();
        }

        public bool IsActivityFilterSelected(string value)
        {
            return IsFilterSelected(_selectedActivityFilters, value);
        }

        public void SetActivityFilterSelected(string value, bool isSelected)
        {
            if (!SetFilterSelection(_selectedActivityFilters, value, isSelected))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedActivityFilterText));
            RaiseFilterChanged();
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

        private GridControlBarViewModel CreateControlBar()
        {
            var controlBar = new GridControlBarViewModel
            {
                Search = _chartFilters
                    ? null
                    : new GridSearchControl(
                        this,
                        nameof(SearchText),
                        () => SearchText,
                        value => SearchText = value,
                        L("LOCPlayAch_Filter_Games"),
                        () => SearchText = string.Empty)
                    {
                        Id = ShowcaseControlBarItems.Search,
                        LabelKey = "LOCPlayAch_Filter_Games"
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
                LabelKey = "LOCPlayAch_Common_Platform",
                Width = 170
            });
            controlBar.Items.Add(GameFilter.Item);
            if (TagFilter != null)
            {
                controlBar.Items.Add(TagFilter.Item);
            }

            if (_includeProgress)
            {
                controlBar.Items.Add(new GridMultiSelectFilter(
                    this,
                    nameof(SelectedProgressFilterText),
                    () => SelectedProgressFilterText,
                    () => ProgressFilterOptions,
                    IsProgressFilterSelected,
                    SetProgressFilterSelected,
                    ProgressFilterChoices.Label)
                {
                    Id = ShowcaseControlBarItems.Progress,
                    LabelKey = "LOCPlayAch_Progress",
                    Width = 170
                });
            }

            if (TierFilters != null)
            {
                controlBar.Items.Add(TierFilters.RarityItem);
                controlBar.Items.Add(TierFilters.TrophyItem);
            }

            if (_chartFilters)
            {
                return controlBar;
            }

            controlBar.Items.Add(new GridMultiSelectFilter(
                this,
                nameof(SelectedActivityFilterText),
                () => SelectedActivityFilterText,
                () => ActivityFilterOptions,
                IsActivityFilterSelected,
                SetActivityFilterSelected)
            {
                Id = ShowcaseControlBarItems.Activity,
                LabelKey = "LOCPlayAch_Filter_ActivitySelectorPlaceholder",
                Width = 170
            });
            return controlBar;
        }

        private void OnProviderFilterSelectionChanged()
        {
            OnPropertyChanged(nameof(SelectedProviderFilterText));
            GameFilter.SetPlatforms(ProviderFilterGroups);
            RaiseFilterChanged();
        }

        private static bool IsFilterSelected(HashSet<string> selectedValues, string value)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return selectedValues.Contains(value.Trim());
        }

        private static bool SetFilterSelection(HashSet<string> selectedValues, string value, bool isSelected)
        {
            if (selectedValues == null || string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var normalized = value.Trim();
            return isSelected
                ? selectedValues.Add(normalized)
                : selectedValues.Remove(normalized);
        }

        private static string GetSelectedFilterText(
            HashSet<string> selectedValues,
            IEnumerable<string> options,
            string placeholder)
        {
            if (selectedValues == null || selectedValues.Count == 0)
            {
                return placeholder;
            }

            var ordered = new List<string>();
            foreach (var option in options ?? Enumerable.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(option) && selectedValues.Contains(option))
                {
                    ordered.Add(option);
                }
            }

            if (ordered.Count == 0)
            {
                ordered.AddRange(selectedValues.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
            }

            return string.Join(", ", ordered);
        }

        private static string PlayedLabel => L("LOCPlayAch_Filter_Played");

        private static string UnplayedLabel => L("LOCPlayAch_Filter_Unplayed");

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
