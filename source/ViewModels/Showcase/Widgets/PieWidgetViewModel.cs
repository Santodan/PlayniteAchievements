using System;
using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Pie widget by reusing PieChartWithRadialIcons/PieChartViewModel. Builds the chart
    /// for the configured distribution (completed games / provider / rarity / trophy); the legend
    /// and its side are per-widget settings that the chart control renders. A fresh chart is
    /// produced per refresh so the bound control always reflects the latest data. The widget's
    /// control bar filters the games the totals are computed from, so a pie can cover a single
    /// platform or progress bucket, and on a pie counting achievements its tag, rarity and trophy
    /// filters narrow which achievements count, locked ones included.
    /// </summary>
    public sealed class PieWidgetViewModel : ShowcaseWidgetViewModelBase, IDisposable
    {
        // The widget instance's shared adapter, so the filters survive view model swaps.
        private readonly ShowcaseControlBarSlot<PieControlBarAdapter> _controlBarSlot;
        private PieChartViewModel _chart = new PieChartViewModel();
        private GridControlBarViewModel _controlBar;

        private readonly AchievementDataService _dataService;

        // Bumped when tallies or tag definitions change, which a Tag pie cannot see in its
        // snapshot: totals are replaced on the rows in place, and names and colors live elsewhere.
        private int _tagVersion;

        public PieWidgetViewModel()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<PieControlBarAdapter>(Refresh);
            AchievementTagRegistry.Changed += OnTagDefinitionsChanged;
            _dataService = PlayniteAchievementsPlugin.Instance?.AchievementDataService;
            if (_dataService != null)
            {
                _dataService.TalliesChanged += OnTalliesChanged;
            }
        }

        public PieChartViewModel Chart { get => _chart; private set => SetValue(ref _chart, value); }

        /// <summary>The game filter items the widget's settings show; null when they show none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        public ShowcasePieMode Mode { get; private set; }

        // What the current chart was built from, so a highlight-only refresh can keep it.
        private OverviewDataSnapshot _chartSnapshot;
        private string _chartOptionsKey;

        private static string OptionsKey(ShowcaseWidgetInstanceSettings instance) =>
            string.Join(
                "\u001f",
                (instance?.Options ?? new Dictionary<string, string>())
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value));

        protected override void Refresh()
        {
            // A linked pie's snapshot already follows the overview's filters; its own control bar
            // narrows that further.
            var linked = Projection?.IsLinked == true;
            var mode = ShowcaseWidgetOptions.GetPieMode(Projection?.Instance);
            Mode = mode;
            var snapshot = ApplyControlBarFilter(Projection?.Snapshot ?? new OverviewDataSnapshot(), linked);
            var tagScope = mode == ShowcasePieMode.Tag ? ResolveTagScope(snapshot, linked) : null;

            // A linked pie is reprojected when the overview's selection moves even though its
            // data did not; then only the highlight changes, and rebuilding the chart would
            // redraw every slice for it. A Tag pie also redraws for what its snapshot does not
            // show: the tags picked, the games they resolve on, and a tag change.
            var optionsKey = OptionsKey(Projection?.Instance);
            if (tagScope != null)
            {
                optionsKey += "\u001ftags=" + string.Join(",", _controlBarSlot.Adapter.TagFilter.SelectedTags.OrderBy(key => key, StringComparer.OrdinalIgnoreCase)) +
                              "\u001fscope=" + string.Join(",", tagScope.OrderBy(id => id)) +
                              "\u001fv=" + _tagVersion;
            }
            if (linked &&
                Chart != null &&
                ReferenceEquals(snapshot, _chartSnapshot) &&
                string.Equals(optionsKey, _chartOptionsKey, StringComparison.Ordinal))
            {
                Chart.SetSelectedLabels((Projection.LinkedSliceKeys ?? Array.Empty<string>())
                    .Select(key => LabelForSliceKey(Chart, mode, key)));
                return;
            }

            // New data under the same options goes into the chart already shown: its Set*Data
            // calls sync the slices and legend in place, where a fresh chart view model hands
            // the control a new series collection that it redraws (and animates) from nothing.
            var reuse = Chart != null && string.Equals(optionsKey, _chartOptionsKey, StringComparison.Ordinal);
            _chartSnapshot = linked ? snapshot : null;
            _chartOptionsKey = optionsKey;
            var chart = reuse ? Chart : new PieChartViewModel
            {
                // Both are applied by each Set*Data call, so they must be assigned before
                // the data.
                SmallSliceMode = ShowcaseWidgetOptions.GetPieSmallSliceMode(Projection?.Instance),
                IncludeLocked = ShowcaseWidgetOptions.GetPieIncludeLocked(Projection?.Instance),
                CenterMode = ShowcaseWidgetOptions.GetPieCenterMode(Projection?.Instance),
                ShowIcons = ShowcaseWidgetOptions.GetPieShowIcons(Projection?.Instance),
                ShowLegend = ShowcaseWidgetOptions.GetShowLegend(Projection?.Instance),
                LegendPosition = ShowcaseWidgetOptions.GetPieLegendPosition(Projection?.Instance)
            };
            // Set on a reused chart too: the same options can show a cut view after a whole one.
            chart.CountsUnlocksOnly = snapshot.IsAchievementClip &&
                                      ShowcaseWidgetOptions.CountsAchievements(Projection?.Instance);
            switch (mode)
            {
                case ShowcasePieMode.Provider:
                    var games = snapshot.GameSummaries ?? new List<GameSummaryItem>();
                    var providerGroups = games
                        .Where(game => game != null && !string.IsNullOrWhiteSpace(game.ProviderKey))
                        .GroupBy(game => game.ProviderKey, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var metadata = providerGroups.ToDictionary(
                        group => group.Key,
                        group => (
                            group.Select(game => game.ProviderIconKey)
                                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty,
                            group.Select(game => game.ProviderColorHex)
                                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "#888888"),
                        StringComparer.OrdinalIgnoreCase);
                    var providerNames = providerGroups.ToDictionary(
                        group => group.Key,
                        group => group.Select(game => game.Provider)
                            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? group.Key,
                        StringComparer.OrdinalIgnoreCase);
                    if (ShowcaseWidgetOptions.GetPieCount(Projection?.Instance) == ShowcasePieCount.CompletedGames)
                    {
                        // Games rather than achievements: each platform's completed games, then
                        // every game not completed yet.
                        var completed = providerGroups.ToDictionary(
                            group => group.Key,
                            group => group.Count(game => game.IsCompleted),
                            StringComparer.OrdinalIgnoreCase);
                        var gameCounts = providerGroups.ToDictionary(
                            group => group.Key,
                            group => group.Count(),
                            StringComparer.OrdinalIgnoreCase);
                        chart.SetProviderData(
                            completed.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                            gameCounts,
                            gameCounts.Values.Sum() - completed.Values.Sum(),
                            Localize("LOCPlayAch_Overview_Incomplete"),
                            metadata,
                            providerNames);
                        break;
                    }

                    chart.SetProviderData(
                        snapshot.UnlockedByProvider,
                        snapshot.TotalByProvider,
                        snapshot.TotalLocked,
                        Localize("LOCPlayAch_Common_Locked"),
                        metadata,
                        providerNames);
                    break;
                case ShowcasePieMode.Rarity:
                    chart.SetRarityData(
                        snapshot.TotalCommon,
                        snapshot.TotalUncommon,
                        snapshot.TotalRare,
                        snapshot.TotalUltraRare,
                        snapshot.TotalLocked,
                        snapshot.TotalCommonPossible,
                        snapshot.TotalUncommonPossible,
                        snapshot.TotalRarePossible,
                        snapshot.TotalUltraRarePossible,
                        Localize("LOCPlayAch_Rarity_Common"),
                        Localize("LOCPlayAch_Rarity_Uncommon"),
                        Localize("LOCPlayAch_Rarity_Rare"),
                        Localize("LOCPlayAch_Rarity_UltraRare"),
                        Localize("LOCPlayAch_Common_Locked"));
                    break;
                case ShowcasePieMode.Tag:
                    SetTagData(chart, snapshot, tagScope);
                    break;
                case ShowcasePieMode.Trophy:
                    chart.SetTrophyData(
                        snapshot.TotalPlatinum,
                        snapshot.TotalGold,
                        snapshot.TotalSilver,
                        snapshot.TotalBronze,
                        snapshot.TotalPlatinumPossible,
                        snapshot.TotalGoldPossible,
                        snapshot.TotalSilverPossible,
                        snapshot.TotalBronzePossible,
                        Localize("LOCPlayAch_Trophy_Platinum"),
                        Localize("LOCPlayAch_Trophy_Gold"),
                        Localize("LOCPlayAch_Trophy_Silver"),
                        Localize("LOCPlayAch_Trophy_Bronze"),
                        Localize("LOCPlayAch_Common_Locked"));
                    break;
                default:
                    chart.SetGameData(
                        snapshot.PossibleCompletions,
                        snapshot.Completions,
                        Localize("LOCPlayAch_Completed"),
                        Localize("LOCPlayAch_Overview_Incomplete"));
                    break;
            }

            if (linked)
            {
                chart.SetSelectedLabels((Projection.LinkedSliceKeys ?? Array.Empty<string>())
                    .Select(key => LabelForSliceKey(chart, mode, key)));
            }

            // The replaced chart is subscribed to the process-lifetime appearance event, so it
            // must be released explicitly; otherwise every refresh strands one chart view model
            // and its whole series/slice/legend graph in memory for the rest of the session.
            var previous = Chart;
            Chart = chart;
            if (!ReferenceEquals(previous, chart))
            {
                previous?.Dispose();
            }
        }

        // The last narrowed linked view, reused while its source and games are unchanged so a
        // reprojection that only moves the selection still takes the highlight-only path.
        private OverviewDataSnapshot _narrowedFrom;
        private HashSet<GameSummaryItem> _narrowedGames;
        private OverviewDataSnapshot _narrowed;

        // The last view counted through the achievement filters, reused while its source and
        // picks are unchanged, for the same reason.
        private OverviewDataSnapshot _filteredFrom;
        private string _filteredKey;
        private OverviewDataSnapshot _filtered;

        /// <summary>
        /// Filters the snapshot through the widget instance's control bar (which stays in effect
        /// while the bar is hidden): its game filters narrow the games, then, on a pie counting
        /// achievements, its tag, rarity and trophy filters narrow their achievements, locked ones
        /// included. The original snapshot is returned when nothing is filtered. A linked view
        /// keeps what the overview's filters made of it, cut to matching unlocks where it was.
        /// </summary>
        private OverviewDataSnapshot ApplyControlBarFilter(OverviewDataSnapshot snapshot, bool linked)
        {
            var narrowed = ApplyGameFilter(snapshot, linked);
            var adapter = _controlBarSlot.Adapter;
            if (!adapter.HasAchievementSelection)
            {
                return narrowed;
            }

            var key = string.Join(",", adapter.TagFilter.SelectedTags.OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)) +
                      "|" + adapter.TierFilters.SelectedRarityText + "|" + adapter.TierFilters.SelectedTrophyText;
            if (!ReferenceEquals(_filteredFrom, narrowed) || !string.Equals(_filteredKey, key, StringComparison.Ordinal))
            {
                _filteredFrom = narrowed;
                _filteredKey = key;
                _filtered = OverviewLinkedSnapshots.FilterAchievements(
                    narrowed,
                    adapter.MatchesAchievement,
                    adapter.MatchesTally);
            }

            return _filtered;
        }

        // The snapshot narrowed to the games the bar's game filters keep.
        private OverviewDataSnapshot ApplyGameFilter(OverviewDataSnapshot snapshot, bool linked)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(Projection?.Instance);
            ControlBar = _controlBarSlot.ShownBar;

            var adapter = _controlBarSlot.Adapter;
            adapter.SetAchievementFiltersApply(ShowcaseWidgetOptions.CountsAchievements(Projection?.Instance));
            var collection = Projection?.Collection;
            var games = (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null)
                .ToList();
            // A linked pie's bar offers the whole library, not what the overview's filters left.
            adapter.UpdateOptions(linked && Projection?.LinkedOptionGames != null
                ? Projection.LinkedOptionGames.Where(game => game != null)
                : games);
            adapter.GameFilter.SetScope(collection?.GameIds);

            // The widget's collection comes before the bar, so its games are all the bar offers.
            var filtered = adapter.Apply(collection == null
                ? games
                : games.Where(game => collection.ContainsGame(game.PlayniteGameId)));
            if (filtered.Count == games.Count)
            {
                return snapshot;
            }

            if (!linked)
            {
                return OverviewDataSnapshot.FromGameSummaries(filtered);
            }

            var kept = new HashSet<GameSummaryItem>(filtered);
            if (!ReferenceEquals(_narrowedFrom, snapshot) || _narrowedGames == null || !_narrowedGames.SetEquals(kept))
            {
                _narrowedFrom = snapshot;
                _narrowedGames = kept;
                _narrowed = OverviewLinkedSnapshots.NarrowToGames(snapshot, filtered);
            }

            return _narrowed;
        }

        /// <summary>
        /// The slice identity behind a clicked label: a provider key for the platform pie, or an
        /// <see cref="OverviewLinkedSliceKeys"/> value. Null for a slice the overview cannot filter by.
        /// </summary>
        public string SliceKeyForLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return null;
            }

            switch (Mode)
            {
                case ShowcasePieMode.Provider:
                    return string.Equals(label, Localize("LOCPlayAch_Common_Locked"), StringComparison.OrdinalIgnoreCase)
                        ? OverviewLinkedSliceKeys.Locked
                        : Chart?.GetProviderKeyFromLabel(label);
                case ShowcasePieMode.CompletedGames:
                    if (string.Equals(label, Localize("LOCPlayAch_Completed"), StringComparison.OrdinalIgnoreCase))
                    {
                        return OverviewLinkedSliceKeys.Complete;
                    }

                    return string.Equals(label, Localize("LOCPlayAch_Overview_Incomplete"), StringComparison.OrdinalIgnoreCase)
                        ? OverviewLinkedSliceKeys.Incomplete
                        : null;
                case ShowcasePieMode.Rarity:
                case ShowcasePieMode.Trophy:
                    // The overview's rarity and trophy filters list these same labels.
                    return string.Equals(label, Localize("LOCPlayAch_Common_Locked"), StringComparison.OrdinalIgnoreCase)
                        ? OverviewLinkedSliceKeys.Locked
                        : label;
                case ShowcasePieMode.Tag:
                    // Tag names are the user's, so a slice maps back through its tag key.
                    return string.Equals(label, Localize("LOCPlayAch_Common_Locked"), StringComparison.OrdinalIgnoreCase)
                        ? OverviewLinkedSliceKeys.Locked
                        : Chart?.GetTagKeyFromLabel(label);
                default:
                    return null;
            }
        }

        private static string LabelForSliceKey(PieChartViewModel chart, ShowcasePieMode mode, string key)
        {
            switch (mode)
            {
                case ShowcasePieMode.Provider:
                    return chart.GetLabelForProviderKey(key);
                case ShowcasePieMode.CompletedGames:
                    return key == OverviewLinkedSliceKeys.Complete
                        ? Localize("LOCPlayAch_Completed")
                        : key == OverviewLinkedSliceKeys.Incomplete
                            ? Localize("LOCPlayAch_Overview_Incomplete")
                            : null;
                case ShowcasePieMode.Rarity:
                case ShowcasePieMode.Trophy:
                    return key;
                case ShowcasePieMode.Tag:
                    return chart.GetLabelForTagKey(key);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Releases the current chart. Refresh only disposes the chart it replaces, so without
        /// this the last one survives whenever the widget view model itself is discarded (a kind
        /// swap, a deleted widget, dashboard teardown) and stays rooted by the process-lifetime
        /// appearance event for the rest of the session.
        /// </summary>
        public void Dispose()
        {
            _chart?.Dispose();
            AchievementTagRegistry.Changed -= OnTagDefinitionsChanged;
            if (_dataService != null)
            {
                _dataService.TalliesChanged -= OnTalliesChanged;
            }
        }

        // Distinct colors for tags the user left uncolored, by their place in the pie.
        private static readonly string[] TagPalette =
        {
            "#4E79A7", "#F28E2B", "#E15759", "#76B7B2", "#59A14F",
            "#EDC948", "#B07AA1", "#FF9DA7", "#9C755F", "#86BCB6"
        };

        /// <summary>
        /// The games a Tag pie's tags resolve on: the games its bar picked, else the game a linked
        /// pie's overview narrowed it to, else none (the global tags).
        /// </summary>
        private IReadOnlyCollection<Guid> ResolveTagScope(OverviewDataSnapshot snapshot, bool linked)
        {
            var picked = _controlBarSlot.Adapter.GameFilter.SelectedGameIds;
            if (picked.Count > 0)
            {
                return picked.ToList();
            }

            if (linked && !string.IsNullOrEmpty(Projection?.ContextLabel))
            {
                var narrowedTo = (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                    .Where(game => game?.PlayniteGameId != null)
                    .Select(game => game.PlayniteGameId.Value)
                    .Distinct()
                    .ToList();
                if (narrowedTo.Count == 1)
                {
                    return narrowedTo;
                }
            }

            return Array.Empty<Guid>();
        }

        private static IReadOnlyList<string> TagOptions(OverviewDataSnapshot snapshot, IReadOnlyCollection<Guid> scope) =>
            AchievementTagScope.OptionKeys(
                scope,
                gameId => (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                    .Where(game => game?.PlayniteGameId == gameId)
                    .SelectMany(game => game.Tallies ?? Array.Empty<AchievementTally>())
                    .SelectMany(tally => tally.Tags));

        /// <summary>
        /// One slice per picked tag (every offered tag when none are picked) of its unlocked
        /// achievements, then the locked ones among them, each counted once however many of the
        /// drawn tags it carries; the centre counts the same way. A view cut to matching unlocks
        /// has only unlocked rows to count, so there every count is of unlocks.
        /// </summary>
        private void SetTagData(PieChartViewModel chart, OverviewDataSnapshot snapshot, IReadOnlyCollection<Guid> scope)
        {
            var options = TagOptions(snapshot, scope);
            var picked = _controlBarSlot.Adapter.TagFilter.SelectedTags;
            var keys = options.Where(key => picked.Count == 0 || picked.Contains(key)).ToList();
            IEnumerable<AchievementTally> tallies;
            if (snapshot.IsAchievementClip)
            {
                var unlockedRows = new AchievementTallyBuilder();
                foreach (var row in snapshot.Achievements ?? new List<AchievementDisplayItem>())
                {
                    if (row?.Unlocked == true)
                    {
                        unlockedRows.Add(row.Tags, unlocked: true);
                    }
                }

                tallies = unlockedRows.Build() ?? Array.Empty<AchievementTally>();
            }
            else
            {
                // The bar's rarity and trophy filters count here as they count everywhere else;
                // its tags are the slices themselves.
                var keep = snapshot.TallyFilter;
                tallies = (snapshot.GameSummaries ?? new List<GameSummaryItem>())
                    .SelectMany(game => game?.Tallies ?? Array.Empty<AchievementTally>())
                    .Where(tally => keep == null || keep(tally));
            }

            var (counts, unlocked, total) = AchievementTallyBuilder.CountShown(tallies, keys);
            var namingGame = AchievementTagScope.NamingGame(scope);
            var slices = new List<(string Key, string Label, int Unlocked, int Total, string ColorHex, string IconKey)>();
            foreach (var key in keys)
            {
                var count = counts[key];
                if (count.Total <= 0)
                {
                    continue;
                }

                var definition = AchievementTagRegistry.Resolve(key, namingGame);
                slices.Add((
                    key,
                    AchievementTagRegistry.GetName(key, namingGame),
                    count.Unlocked,
                    count.Total,
                    AchievementTagHelper.NormalizeColor(definition?.Color) ?? TagPalette[slices.Count % TagPalette.Length],
                    Views.Converters.ProviderIconConverter.TagIconKey(definition?.Icon)));
            }

            chart.SetTagData(slices, unlocked, total, Localize("LOCPlayAch_Common_Locked"));
        }

        private void OnTagDefinitionsChanged(object sender, AchievementTagsChangedEventArgs e) => RefreshTagPie();

        private void OnTalliesChanged(object sender, TalliesChangedEventArgs e) => RefreshTagPie();

        private void RefreshTagPie()
        {
            System.Windows.Application.Current?.Dispatcher?.InvokeIfNeeded(() =>
            {
                if (Mode != ShowcasePieMode.Tag || Projection == null)
                {
                    return;
                }

                _tagVersion++;
                Refresh();
            });
        }

        private static string Localize(string key) => ResourceProvider.GetString(key);
    }
}
