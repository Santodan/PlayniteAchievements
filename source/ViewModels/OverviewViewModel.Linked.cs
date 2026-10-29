using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels
{
    // The overview's side of its mini-showcase: the snapshots its linked widgets project from,
    // the filters their clicks set, and the Achievements grid's own filters (an unlock date range,
    // rarity and trophy type), which a timeline column, calendar day or pie slice sets as well.
    // Those filters narrow the Achievements grid to the achievements they keep, the Games grid to
    // the games those belong to, and the linked widgets to both.
    public partial class OverviewViewModel
    {
        private DateTime? _unlockRangeFrom;
        private DateTime? _unlockRangeTo;
        private readonly HashSet<string> _selectedRarityFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _selectedTrophyFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Achievement tag keys; a tag pie slice in the mini-showcase toggles them too.
        private readonly HashSet<string> _selectedTagFilters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The global tags: the Achievements grid lists every game's unlocks.</summary>
        public IReadOnlyList<string> TagFilterOptions => AchievementTagRegistry.GetShownKeys(null);

        public string SelectedTagFilterText => _selectedTagFilters.Count == 0
            ? L("LOCPlayAch_Common_Tags")
            : string.Join(", ", _selectedTagFilters
                .Select(key => AchievementTagRegistry.GetName(key))
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase));

        public bool IsTagFilterSelected(string key) => key != null && _selectedTagFilters.Contains(key);

        public void SetTagFilterSelected(string key, bool isSelected)
        {
            if (SetFilterSelection(_selectedTagFilters, key, isSelected))
            {
                OnPropertyChanged(nameof(SelectedTagFilterText));
                ScheduleAchievementFilterApply();
            }
        }

        private void ClearTagFilters()
        {
            foreach (var key in _selectedTagFilters.ToList())
            {
                SetTagFilterSelected(key, false);
            }
        }

        private readonly Dictionary<(OverviewLinkedFilter Exclude, Guid? Game, bool CountsGames), LinkedSnapshotEntry> _linkedSnapshots =
            new Dictionary<(OverviewLinkedFilter Exclude, Guid? Game, bool CountsGames), LinkedSnapshotEntry>();

        /// <summary>
        /// Raised when what <see cref="GetLinkedSnapshot"/> returns may have changed: the
        /// snapshot, a filter, or the selected game.
        /// </summary>
        public event EventHandler LinkedDataChanged;

        /// <summary>The Achievements grid's range start day, or null for an open start.</summary>
        public DateTime? UnlockRangeFrom => _unlockRangeFrom;

        /// <summary>The Achievements grid's range end day, or null for an open end.</summary>
        public DateTime? UnlockRangeTo => _unlockRangeTo;

        /// <summary>The unlock date range as one span, open ends at the calendar's limits; null when unset.</summary>
        public UnlockDaySpan? UnlockSpanFilter =>
            _unlockRangeFrom.HasValue || _unlockRangeTo.HasValue
                ? new UnlockDaySpan(_unlockRangeFrom ?? DateTime.MinValue, _unlockRangeTo ?? DateTime.MaxValue.Date)
                : (UnlockDaySpan?)null;

        /// <summary>Sets the range to <paramref name="span"/>, or clears it when it is already that span.</summary>
        public void ToggleUnlockSpanFilter(UnlockDaySpan span)
        {
            if (Nullable.Equals(UnlockSpanFilter, span))
            {
                SetUnlockRange(null, null);
            }
            else
            {
                SetUnlockRange(span.Start, span.End);
            }
        }

        /// <summary>Sets the range from the Achievements grid's pickers; either end may be open.</summary>
        public void SetUnlockRange(DateTime? from, DateTime? to)
        {
            from = from?.Date;
            to = to?.Date;
            if (Nullable.Equals(_unlockRangeFrom, from) && Nullable.Equals(_unlockRangeTo, to))
            {
                return;
            }

            _unlockRangeFrom = from;
            _unlockRangeTo = to;
            OnPropertyChanged(nameof(UnlockSpanFilter));
            ScheduleAchievementFilterApply();
        }

        // Rarity and trophy filters: their option labels are the slice labels the rarity and
        // trophy pies draw, so a slice click selects the option of the same name.
        public ObservableCollection<string> RarityFilterOptions { get; } = new ObservableCollection<string>();

        public ObservableCollection<string> TrophyFilterOptions { get; } = new ObservableCollection<string>();

        public string SelectedRarityFilterText =>
            GetSelectedFilterText(_selectedRarityFilters, RarityFilterOptions, L("LOCPlayAch_Column_Rarity"));

        public string SelectedTrophyFilterText =>
            GetSelectedFilterText(_selectedTrophyFilters, TrophyFilterOptions, L("LOCPlayAch_Column_Trophy"));

        public bool IsRarityFilterSelected(string value) => IsFilterSelected(_selectedRarityFilters, value);

        public bool IsTrophyFilterSelected(string value) => IsFilterSelected(_selectedTrophyFilters, value);

        public void SetRarityFilterSelected(string value, bool isSelected)
        {
            if (SetFilterSelection(_selectedRarityFilters, value, isSelected))
            {
                OnPropertyChanged(nameof(SelectedRarityFilterText));
                ScheduleAchievementFilterApply();
            }
        }

        public void SetTrophyFilterSelected(string value, bool isSelected)
        {
            if (SetFilterSelection(_selectedTrophyFilters, value, isSelected))
            {
                OnPropertyChanged(nameof(SelectedTrophyFilterText));
                ScheduleAchievementFilterApply();
            }
        }

        private void ClearRarityFilters()
        {
            foreach (var value in _selectedRarityFilters.ToList())
            {
                SetRarityFilterSelected(value, false);
            }
        }

        private void ClearTrophyFilters()
        {
            foreach (var value in _selectedTrophyFilters.ToList())
            {
                SetTrophyFilterSelected(value, false);
            }
        }

        // The badges the Rarity and Trophy filters show beside their labels and in the bar.
        private string RarityFilterIconKey(string label) =>
            RarityByLabel().TryGetValue(label ?? string.Empty, out var tier) ? tier.ToDynamicIconKey() : null;

        private string TrophyFilterIconKey(string label) =>
            TrophyTypeByLabel().TryGetValue(label ?? string.Empty, out var type)
                ? RarityTrophyControlBarFilters.TrophyChoices
                    .FirstOrDefault(choice => string.Equals(choice.Type, type, StringComparison.OrdinalIgnoreCase)).IconKey
                : null;

        private Dictionary<string, RarityTier> RarityByLabel() =>
            new Dictionary<string, RarityTier>(StringComparer.OrdinalIgnoreCase)
            {
                [L("LOCPlayAch_Rarity_Common")] = RarityTier.Common,
                [L("LOCPlayAch_Rarity_Uncommon")] = RarityTier.Uncommon,
                [L("LOCPlayAch_Rarity_Rare")] = RarityTier.Rare,
                [L("LOCPlayAch_Rarity_UltraRare")] = RarityTier.UltraRare
            };

        private Dictionary<string, string> TrophyTypeByLabel() =>
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [L("LOCPlayAch_Trophy_Platinum")] = "platinum",
                [L("LOCPlayAch_Trophy_Gold")] = "gold",
                [L("LOCPlayAch_Trophy_Silver")] = "silver",
                [L("LOCPlayAch_Trophy_Bronze")] = "bronze"
            };

        /// <summary>
        /// Fills the rarity options, and the trophy options only when some unlocked achievement
        /// has a trophy type; an empty list hides the trophy filter.
        /// </summary>
        private void UpdateAchievementFilterOptions()
        {
            CollectionHelper.SynchronizeCollection(RarityFilterOptions, RarityByLabel().Keys.ToList());
            var hasTrophies = (_allRecentAchievements ?? new List<AchievementDisplayItem>())
                .Any(item => !string.IsNullOrWhiteSpace(item?.TrophyType));
            CollectionHelper.SynchronizeCollection(
                TrophyFilterOptions,
                hasTrophies ? TrophyTypeByLabel().Keys.ToList() : new List<string>());
            OnPropertyChanged(nameof(SelectedRarityFilterText));
            OnPropertyChanged(nameof(SelectedTrophyFilterText));
        }

        private void ScheduleAchievementFilterApply() => ScheduleFilterApply();

        /// <summary>
        /// Applies a filter change in two steps. The linked widgets refresh at once, since they
        /// read the filter state rather than the grids, and draw in the next Background pass.
        /// The grids refilter after that, deferred to ContextIdle so the click or pick that set
        /// the filter finishes first. A click on a chart so shows on the charts without waiting
        /// for the grids to rebuild their rows; the grids' own pass then finds the widgets'
        /// views unchanged and leaves them alone.
        /// </summary>
        private void ScheduleFilterApply()
        {
            InvalidateLinkedSnapshots();
            System.Windows.Application.Current?.Dispatcher?.BeginInvoke(
                new Action(ApplyLeftFilters),
                System.Windows.Threading.DispatcherPriority.ContextIdle);
        }

        /// <summary>
        /// Rows whose game the Games grid lists, so its filters (platform, progress, activity,
        /// search) combine with the Achievements grid's own; every row while the Games grid is
        /// unfiltered.
        /// </summary>
        private IEnumerable<AchievementDisplayItem> KeepGamesGridGames(IEnumerable<AchievementDisplayItem> items)
        {
            var listed = _filteredGameSummaries;
            if (listed == null || listed.Count == (_allGameSummaries?.Count(game => game != null) ?? 0))
            {
                return items;
            }

            var ids = new HashSet<Guid>(listed
                .Where(game => game?.PlayniteGameId.HasValue == true)
                .Select(game => game.PlayniteGameId.Value));
            return items.Where(item => item?.PlayniteGameId.HasValue == true && ids.Contains(item.PlayniteGameId.Value));
        }

        private bool HasAchievementFilters(OverviewLinkedFilter exclude) =>
            ((exclude & OverviewLinkedFilter.UnlockSpan) == 0 && UnlockSpanFilter.HasValue) ||
            ((exclude & OverviewLinkedFilter.Rarity) == 0 && _selectedRarityFilters.Count > 0) ||
            ((exclude & OverviewLinkedFilter.Trophy) == 0 && _selectedTrophyFilters.Count > 0) ||
            ((exclude & OverviewLinkedFilter.Tag) == 0 && _selectedTagFilters.Count > 0);

        /// <summary>
        /// The Achievements grid's filters, less those in <paramref name="exclude"/>, as one test
        /// on an achievement row. Only unlocked rows with a time pass: the filters describe unlocks.
        /// </summary>
        private Func<AchievementDisplayItem, bool> AchievementFilter(OverviewLinkedFilter exclude)
        {
            var span = (exclude & OverviewLinkedFilter.UnlockSpan) == 0 ? UnlockSpanFilter : null;
            var rarityByLabel = RarityByLabel();
            var rarities = (exclude & OverviewLinkedFilter.Rarity) == 0 && _selectedRarityFilters.Count > 0
                ? new HashSet<RarityTier>(_selectedRarityFilters
                    .Where(rarityByLabel.ContainsKey)
                    .Select(label => rarityByLabel[label]))
                : null;
            var trophyByLabel = TrophyTypeByLabel();
            var trophies = (exclude & OverviewLinkedFilter.Trophy) == 0 && _selectedTrophyFilters.Count > 0
                ? new HashSet<string>(
                    _selectedTrophyFilters.Where(trophyByLabel.ContainsKey).Select(label => trophyByLabel[label]),
                    StringComparer.OrdinalIgnoreCase)
                : null;
            var tags = (exclude & OverviewLinkedFilter.Tag) == 0 && _selectedTagFilters.Count > 0
                ? _selectedTagFilters.ToList()
                : null;

            return item =>
                item?.Unlocked == true &&
                item.UnlockTimeUtc.HasValue &&
                (!span.HasValue || span.Value.Contains(UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value))) &&
                (rarities == null || rarities.Contains(item.Rarity)) &&
                (trophies == null || trophies.Contains((item.TrophyType ?? string.Empty).Trim())) &&
                (tags == null || tags.Any(tag => AchievementTagHelper.Contains(item.Tags, tag)));
        }

        // A key for the filters a view applies, so a cached view knows when they moved.
        private string AchievementFilterKey(OverviewLinkedFilter exclude)
        {
            if (!HasAchievementFilters(exclude))
            {
                return string.Empty;
            }

            var span = (exclude & OverviewLinkedFilter.UnlockSpan) == 0 ? UnlockSpanFilter : null;
            var rarity = (exclude & OverviewLinkedFilter.Rarity) == 0
                ? string.Join(",", _selectedRarityFilters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                : string.Empty;
            var trophy = (exclude & OverviewLinkedFilter.Trophy) == 0
                ? string.Join(",", _selectedTrophyFilters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                : string.Empty;
            var tag = (exclude & OverviewLinkedFilter.Tag) == 0
                ? string.Join(",", _selectedTagFilters.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
                : string.Empty;
            return (span.HasValue ? span.Value.Start.Ticks + "-" + span.Value.End.Ticks : string.Empty) +
                   "|" + rarity + "|" + trophy + "|" + tag;
        }

        /// <summary>The Achievements grid's rows its filters keep; every row while none is set.</summary>
        private IEnumerable<AchievementDisplayItem> ApplyAchievementFilters(IEnumerable<AchievementDisplayItem> items)
        {
            return HasAchievementFilters(OverviewLinkedFilter.None)
                ? items.Where(AchievementFilter(OverviewLinkedFilter.None))
                : items;
        }

        /// <summary>
        /// Games with at least one achievement the Achievements grid's filters keep (less those in
        /// <paramref name="exclude"/>); every game while none is set.
        /// </summary>
        private IEnumerable<GameSummaryItem> ApplyAchievementFiltersToGames(
            IEnumerable<GameSummaryItem> games,
            OverviewLinkedFilter exclude = OverviewLinkedFilter.None)
        {
            if (!HasAchievementFilters(exclude))
            {
                return games;
            }

            var filter = AchievementFilter(exclude);
            var kept = new HashSet<Guid>((_allRecentAchievements ?? new List<AchievementDisplayItem>())
                .Where(filter)
                .Where(item => item.PlayniteGameId.HasValue)
                .Select(item => item.PlayniteGameId.Value));
            return games.Where(game => game?.PlayniteGameId.HasValue == true && kept.Contains(game.PlayniteGameId.Value));
        }

        /// <summary>
        /// The snapshot a linked widget projects from: the overview's snapshot narrowed by every
        /// filter except those in <paramref name="exclude"/>, or by the selected game when
        /// <paramref name="selection"/> says so, with its achievements cut to the ones the
        /// Achievements grid's filters keep. A view that <paramref name="countsGames"/> keeps
        /// whole games instead: the achievement filters pick which games, and each keeps all its
        /// achievements, unlocked and locked. Null until the overview has a snapshot. A filter
        /// change that leaves a view as it was returns the same instance, so the widgets on that
        /// view see no new data and are left alone.
        /// </summary>
        public OverviewDataSnapshot GetLinkedSnapshot(
            OverviewLinkedFilter exclude,
            OverviewLinkedSelection selection,
            bool countsGames = false)
        {
            var source = _latestSnapshot;
            if (source == null)
            {
                return null;
            }

            var narrowTo = GetLinkedNarrowedGame(selection)?.PlayniteGameId;
            // Narrowed to one game the games filters no longer apply, but the achievement ones do.
            const OverviewLinkedFilter achievementFilters =
                OverviewLinkedFilter.UnlockSpan | OverviewLinkedFilter.Rarity | OverviewLinkedFilter.Trophy |
                OverviewLinkedFilter.Tag;
            var key = (narrowTo.HasValue ? exclude & achievementFilters : exclude, narrowTo, countsGames);
            // Whole games are not cut, so for them the filters matter only through the games kept.
            var filterKey = countsGames ? string.Empty : AchievementFilterKey(exclude);
            _linkedSnapshots.TryGetValue(key, out var entry);
            if (entry != null && entry.IsCurrent)
            {
                return entry.Snapshot;
            }

            List<GameSummaryItem> kept;
            bool keptIsAll;
            if (narrowTo.HasValue)
            {
                kept = (_allGameSummaries ?? new List<GameSummaryItem>())
                    .Where(candidate => candidate?.PlayniteGameId == narrowTo)
                    .Take(1)
                    .ToList();
                keptIsAll = false;
            }
            else
            {
                var all = (_allGameSummaries ?? new List<GameSummaryItem>()).Where(game => game != null).ToList();
                kept = FilterLinkedGames(all, exclude);
                keptIsAll = kept.Count == all.Count;
            }

            var ids = new HashSet<Guid>(kept
                .Where(game => game.PlayniteGameId.HasValue)
                .Select(game => game.PlayniteGameId.Value));
            if (entry != null &&
                ReferenceEquals(entry.Source, source) &&
                string.Equals(entry.FilterKey, filterKey, StringComparison.Ordinal) &&
                entry.KeptCount == kept.Count &&
                entry.KeptIds.SetEquals(ids))
            {
                entry.IsCurrent = true;
                return entry.Snapshot;
            }

            OverviewDataSnapshot snapshot;
            using (PerfScope.Start(_logger, "Overview.LinkedSnapshot", thresholdMs: 5,
                context: exclude + (countsGames ? " games" : string.Empty)))
            {
                snapshot = OverviewLinkedSnapshots.Build(source, kept, keptIsAll);
                if (filterKey.Length > 0)
                {
                    snapshot = OverviewLinkedSnapshots.ClipToAchievements(snapshot, AchievementFilter(exclude));
                }
            }

            _linkedSnapshots[key] = new LinkedSnapshotEntry
            {
                Source = source,
                FilterKey = filterKey,
                KeptCount = kept.Count,
                KeptIds = ids,
                Snapshot = snapshot,
                IsCurrent = true
            };
            return snapshot;
        }

        /// <summary>One linked view's snapshot and the games and filters it was built from.</summary>
        private sealed class LinkedSnapshotEntry
        {
            public OverviewDataSnapshot Source;
            public string FilterKey;
            public int KeptCount;
            public HashSet<Guid> KeptIds;
            public OverviewDataSnapshot Snapshot;

            // Cleared by InvalidateLinkedSnapshots: the next request re-checks the games.
            public bool IsCurrent;
        }

        /// <summary>
        /// A linked widget's title, by priority: the selected game while the widget is narrowed to
        /// it, otherwise the filtered platforms when the widget follows the platform filter, and
        /// otherwise nothing.
        /// </summary>
        public string GetLinkedContextLabel(OverviewLinkedFilter exclude, OverviewLinkedSelection selection)
        {
            var game = GetLinkedNarrowedGame(selection);
            if (game != null)
            {
                return game.GameName;
            }

            if ((exclude & OverviewLinkedFilter.Provider) != 0)
            {
                return string.Empty;
            }

            return string.Join(", ", (ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
                .Where(group => group.HasAnySelected && !string.IsNullOrWhiteSpace(group.DisplayName))
                .Select(group => group.DisplayName));
        }

        /// <summary>The selected game a linked widget with this rule narrows to, or null.</summary>
        public GameSummaryItem GetLinkedNarrowedGame(OverviewLinkedSelection selection)
        {
            var selected = ResolveSelectedGameForChartContext(_latestSnapshot);
            return OverviewLinkedSnapshots.NarrowsTo(selected, selection) ? selected : null;
        }

        // The Games grid's filters (ApplyLeftFilters), less the ones the widget sets itself. A
        // completions pie that leaves the progress filter out still keeps it to the games that
        // could finish, as the overview's completions pie did.
        private List<GameSummaryItem> FilterLinkedGames(List<GameSummaryItem> all, OverviewLinkedFilter exclude)
        {
            IEnumerable<GameSummaryItem> games = all;
            var searchQuery = Services.Search.SearchQuery.From(LeftSearchText);
            if (searchQuery.HasValue)
            {
                games = games.Where(game => _gameSummarySearchIndex.Matches(game, searchQuery));
            }

            if ((exclude & OverviewLinkedFilter.Provider) == 0)
            {
                games = OverviewGameSummaryFilters.ApplyProviderPlatformFilter(games, ProviderFilterGroups);
            }

            games = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                games,
                _selectedPlayStatusFilters,
                (exclude & OverviewLinkedFilter.Completeness) == 0
                    ? _selectedCompletenessFilters
                    : GetCompletedGamesPieProgressFilters(),
                L("LOCPlayAch_Filter_Played"),
                L("LOCPlayAch_Filter_Unplayed"),
                L("LOCPlayAch_Filter_Complete"),
                L("LOCPlayAch_Filter_InProgress"),
                L("LOCPlayAch_Filter_NoProgress"));

            return ApplyAchievementFiltersToGames(games, exclude).ToList();
        }

        /// <summary>
        /// The slices a linked pie shows as selected: the filtered platforms, the side of the
        /// progress filter (if exactly one), or the selected rarities and trophy types.
        /// </summary>
        public IReadOnlyCollection<string> GetLinkedSliceKeys(ShowcasePieMode mode)
        {
            switch (mode)
            {
                case ShowcasePieMode.Provider:
                    return (ProviderFilterGroups ?? Enumerable.Empty<ProviderFilterGroup>())
                        .Where(group => group.HasAnySelected && !string.IsNullOrWhiteSpace(group.ProviderKey))
                        .Select(group => group.ProviderKey)
                        .ToList();
                case ShowcasePieMode.CompletedGames:
                    var complete = _selectedCompletenessFilters.Contains(L("LOCPlayAch_Filter_Complete"));
                    var incomplete = _selectedCompletenessFilters.Contains(L("LOCPlayAch_Filter_InProgress")) ||
                                     _selectedCompletenessFilters.Contains(L("LOCPlayAch_Filter_NoProgress"));
                    if (complete == incomplete)
                    {
                        return Array.Empty<string>();
                    }

                    return new[] { complete ? OverviewLinkedSliceKeys.Complete : OverviewLinkedSliceKeys.Incomplete };
                case ShowcasePieMode.Rarity:
                    return _selectedRarityFilters.ToList();
                case ShowcasePieMode.Trophy:
                    return _selectedTrophyFilters.ToList();
                case ShowcasePieMode.Tag:
                    return _selectedTagFilters.ToList();
                default:
                    return Array.Empty<string>();
            }
        }

        /// <summary>
        /// Applies a linked pie slice click: toggles that platform, that side of the progress
        /// filter, or that rarity or trophy type. A Locked slice clears the pie's filter.
        /// </summary>
        public void ApplyLinkedSliceClick(ShowcasePieMode mode, string sliceKey)
        {
            if (string.IsNullOrWhiteSpace(sliceKey))
            {
                return;
            }

            switch (mode)
            {
                case ShowcasePieMode.Provider:
                    if (sliceKey == OverviewLinkedSliceKeys.Locked)
                    {
                        ClearProviderFilters();
                        return;
                    }

                    ProviderFilterGroups?
                        .FirstOrDefault(group => string.Equals(group.ProviderKey, sliceKey, StringComparison.OrdinalIgnoreCase))?
                        .ToggleAll();
                    return;
                case ShowcasePieMode.CompletedGames:
                    if (sliceKey == OverviewLinkedSliceKeys.Complete)
                    {
                        ToggleCompletenessFilterFromPieChart(L("LOCPlayAch_Filter_Complete"));
                    }
                    else if (sliceKey == OverviewLinkedSliceKeys.Incomplete)
                    {
                        ToggleCompletenessFilterFromPieChart(L("LOCPlayAch_Overview_Incomplete"));
                    }

                    return;
                case ShowcasePieMode.Rarity:
                    if (sliceKey == OverviewLinkedSliceKeys.Locked)
                    {
                        ClearRarityFilters();
                    }
                    else if (RarityByLabel().ContainsKey(sliceKey))
                    {
                        SetRarityFilterSelected(sliceKey, !IsRarityFilterSelected(sliceKey));
                    }

                    return;
                case ShowcasePieMode.Trophy:
                    if (sliceKey == OverviewLinkedSliceKeys.Locked)
                    {
                        ClearTrophyFilters();
                    }
                    else if (TrophyTypeByLabel().ContainsKey(sliceKey))
                    {
                        SetTrophyFilterSelected(sliceKey, !IsTrophyFilterSelected(sliceKey));
                    }

                    return;
                case ShowcasePieMode.Tag:
                    if (sliceKey == OverviewLinkedSliceKeys.Locked)
                    {
                        ClearTagFilters();
                        return;
                    }

                    var selected = !IsTagFilterSelected(sliceKey);
                    SetTagFilterSelected(sliceKey, selected);
                    // The selected game's grid has its own Tags filter, which takes the tag too,
                    // so the grid beside the pie shows the achievements the slice stands for.
                    if (SelectedGame != null)
                    {
                        _selectedGameControlBar?.SetCategoryTypeFilterSelected(sliceKey, selected);
                    }

                    return;
            }
        }

        /// <param name="rebuild">
        /// True when what the widgets draw changed with no change to their games (platform or
        /// rarity colors), so every view gets a fresh snapshot instead of keeping its own.
        /// </param>
        private void InvalidateLinkedSnapshots(bool rebuild = false)
        {
            if (rebuild)
            {
                _linkedSnapshots.Clear();
            }

            foreach (var entry in _linkedSnapshots.Values)
            {
                entry.IsCurrent = false;
            }

            LinkedDataChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
