using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// One score card plus its cumulative score-over-time series for the mini line chart
    /// rendered under the card.
    /// </summary>
    public sealed class ScoreCardWithHistoryViewModel : ObservableObject
    {
        private static readonly Func<double, string> AxisLabelFormatter =
            value => value.ToString("N0", FormattingCulture.Current);

        private ScoreCardBadgePosition _badgePosition;

        public ScoreCardWithHistoryViewModel(
            ScoreCardViewModel card,
            int currentScore,
            ChartValues<int> historyValues,
            IList<string> historyLabels,
            bool showChart,
            string historyCaption,
            string historyStartText,
            string historyEndText)
        {
            Card = card;
            HistoryValues = historyValues;
            HistoryLabels = historyLabels;
            ShowChart = showChart;
            HistoryCaption = historyCaption;
            HistoryStartText = historyStartText;
            HistoryEndText = historyEndText;
            var hasHistory = historyValues != null && historyValues.Count > 0;
            var windowMin = hasHistory ? historyValues.Min() : currentScore;
            var windowMax = hasHistory ? historyValues.Max() : currentScore;
            // Points cards have no tiers, so their chart has no tier lines.
            var frame = card?.ScoreType.IsPoints == true
                ? ScoreHistoryAxis.FrameWithoutTiers(currentScore, windowMin, windowMax)
                : ScoreHistoryAxis.Frame(currentScore, windowMin, windowMax);
            HistoryMinValue = frame.Min;
            HistoryAxisMax = frame.Max;
            TierSections = BuildSections(frame, card?.AccentBrush, card?.NextTierAccentBrush);
        }

        public ScoreCardViewModel Card { get; }

        /// <summary>
        /// Set in place by the widget, so flipping a side re-binds the card instead of
        /// rebuilding it and re-plotting its chart.
        /// </summary>
        public ScoreCardBadgePosition BadgePosition
        {
            get => _badgePosition;
            set => SetValue(ref _badgePosition, value);
        }

        public ChartValues<int> HistoryValues { get; }

        /// <summary>
        /// Bottom of the mini chart's Y axis: the window's first value, so the chart height is
        /// spent on score gained inside the window rather than on the already-earned total. When
        /// nothing was earned it drops to the current tier's start instead.
        /// </summary>
        public double HistoryMinValue { get; }

        /// <summary>
        /// Top of the mini chart's Y axis: the window's last value plus headroom, so the line
        /// always spans the chart however wide the current tier is. It snaps down to the next
        /// tier's start when that lies within the headroom, and frames the whole tier when
        /// nothing was earned. NaN (auto) only when no next tier exists.
        /// </summary>
        public double HistoryAxisMax { get; }

        /// <summary>
        /// At most two horizontal reference lines: a solid hairline in the card's accent where the
        /// current tier began, and a dashed line in the next tier's accent at the next tier when the
        /// ceiling snapped to it. See <see cref="ScoreHistoryAxis"/>.
        /// </summary>
        public SectionsCollection TierSections { get; }

        public Func<double, string> YLabelFormatter => AxisLabelFormatter;

        private static SectionsCollection BuildSections(
            ScoreHistoryAxisFrame frame,
            Brush accent,
            Brush nextTierAccent)
        {
            var sections = new SectionsCollection();
            if (frame.CurrentTierLine.HasValue)
            {
                sections.Add(CreateSection(frame.CurrentTierLine.Value, accent, dashed: false));
            }

            if (frame.NextTierLine.HasValue)
            {
                sections.Add(CreateSection(frame.NextTierLine.Value, nextTierAccent ?? accent, dashed: true));
            }

            return sections;
        }

        private static AxisSection CreateSection(double value, Brush stroke, bool dashed)
        {
            return new AxisSection
            {
                Value = value,
                SectionWidth = 0,
                Stroke = stroke ?? Brushes.Gray,
                StrokeThickness = 1,
                StrokeDashArray = dashed ? new DoubleCollection { 4, 4 } : null,
                DisableAnimations = true
            };
        }

        /// <summary>Per-point date labels; hidden on the axis, surfaced by the hover tooltip.</summary>
        public IList<string> HistoryLabels { get; }

        public bool ShowChart { get; }

        public string HistoryCaption { get; }

        public string HistoryStartText { get; }

        public string HistoryEndText { get; }
    }

    /// <summary>
    /// Backs the Scores widget by reusing the existing <see cref="ScoreCardViewModel"/> /
    /// ScoreCardControl. Shows the one score card the widget is set to (the UniformGrid host keeps
    /// a single cell). The card carries a cumulative score
    /// history line derived from the projection that fills whatever height the cell leaves under
    /// the card; density only scales the card width.
    /// </summary>
    public sealed class ScoresWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        // The widget instance's shared adapter, so the filters survive view model swaps. Only a
        // points card uses it: one provider's points narrowed to games.
        private readonly ShowcaseControlBarSlot<ChartControlBarAdapter> _controlBarSlot;
        private GridControlBarViewModel _controlBar;

        // The last narrowed view, reused while its source and games hold, so a refresh that keeps
        // them neither rebuilds the card nor recounts its history. The source is held weakly, as
        // the built snapshot below is.
        private WeakReference<OverviewDataSnapshot> _narrowedFrom;
        private HashSet<GameSummaryItem> _narrowedGames;
        private OverviewDataSnapshot _narrowed;
        private string _narrowedHistoryKey;
        private IReadOnlyList<ShowcaseScorePoint> _narrowedHistory;

        public ScoresWidgetViewModel()
        {
            _controlBarSlot = new ShowcaseControlBarSlot<ChartControlBarAdapter>(Refresh);
        }

        /// <summary>A points card's platform, game and progress filters its settings show; null otherwise.</summary>
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

        private int _rows = 1;
        private int _columns = 1;
        private bool _isFeatured = true;
        private double _maxCardWidth = 360;

        // What the cards were last built from. Rebuilding the collection makes LiveCharts throw
        // away and re-plot every series, so an unrelated refresh (a pin toggle, another widget's
        // option, a resize that keeps the same density) must not touch it. The snapshot is held
        // weakly: it is change-detection state only, and a strong field here would pin a whole
        // full-library snapshot generation per Scores widget.
        private IReadOnlyList<ShowcaseScorePoint> _builtHistory;
        private WeakReference<OverviewDataSnapshot> _builtSnapshot;
        private ScoreCardType _builtType;
        private bool _builtShowChart;

        public BulkObservableCollection<ScoreCardWithHistoryViewModel> Cards { get; } =
            new BulkObservableCollection<ScoreCardWithHistoryViewModel>();

        public int Rows { get => _rows; private set => SetValue(ref _rows, value); }

        public int Columns { get => _columns; private set => SetValue(ref _columns, value); }

        public bool IsFeatured { get => _isFeatured; private set => SetValue(ref _isFeatured, value); }

        public double MaxCardWidth { get => _maxCardWidth; private set => SetValue(ref _maxCardWidth, value); }

        /// <summary>
        /// A points card's snapshot narrowed to the games its control bar keeps; the snapshot
        /// itself for the other cards, or while the bar keeps every game.
        /// </summary>
        /// <param name="keptGames">The platform's games the bar keeps; empty for the other cards.</param>
        private OverviewDataSnapshot ApplyControlBarFilter(
            OverviewDataSnapshot source,
            ScoreCardType type,
            out IReadOnlyList<GameSummaryItem> keptGames)
        {
            _controlBarSlot.Bind(Projection?.Instance?.InstanceId);
            keptGames = Array.Empty<GameSummaryItem>();
            if (!type.IsPoints)
            {
                ControlBar = null;
                return source;
            }

            _controlBarSlot.UpdateShownBar(Projection?.Instance);
            ControlBar = _controlBarSlot.ShownBar;

            // Only the card's platform earns its points, so the bar lists and filters that
            // platform's games alone.
            var adapter = _controlBarSlot.Adapter;
            var family = PointsFamilies.Normalize(type.PointsFamily);
            var games = (source.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null &&
                               string.Equals(PointsFamilies.Normalize(game.PointsFamily), family, StringComparison.OrdinalIgnoreCase))
                .ToList();
            adapter.UpdateOptions(games);
            var kept = adapter.Apply(games);
            keptGames = kept;
            if (kept.Count == games.Count)
            {
                return source;
            }

            OverviewDataSnapshot narrowedFrom = null;
            _narrowedFrom?.TryGetTarget(out narrowedFrom);
            if (!ReferenceEquals(narrowedFrom, source) || _narrowedGames == null || !_narrowedGames.SetEquals(kept))
            {
                _narrowedFrom = new WeakReference<OverviewDataSnapshot>(source);
                _narrowedGames = new HashSet<GameSummaryItem>(kept);
                _narrowed = OverviewLinkedSnapshots.NarrowToGames(source, kept);
                _narrowedHistory = null;
            }

            return _narrowed;
        }

        // The kept games' unlocks per rarity, summed: only the card's platform, as its points are.
        private static IReadOnlyList<BadgeCountItem> BuildRarityBadges(IReadOnlyList<GameSummaryItem> games) =>
            BadgeCountItem.BuildRarity(
                games.Sum(game => game.UltraRareCount),
                games.Sum(game => game.RareCount),
                games.Sum(game => game.UncommonCount),
                games.Sum(game => game.CommonCount));

        // A card narrowed to one game shows that game's art when the option asks for it; with
        // several games, or art the game lacks, the platform icon stays.
        private static string ResolveGameArt(IReadOnlyList<GameSummaryItem> games, ShowcaseScoreCardArt art)
        {
            if (games.Count != 1)
            {
                return null;
            }

            switch (art)
            {
                case ShowcaseScoreCardArt.GameCover:
                    return games[0].GameCoverPath;
                case ShowcaseScoreCardArt.GameIcon:
                    return games[0].GameLogo;
                default:
                    return null;
            }
        }

        // The narrowed view's history over the card's window, counted once per view and day.
        private IReadOnlyList<ShowcaseScorePoint> NarrowedHistory(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance)
        {
            var today = DateTime.Now.Date;
            var key = ShowcaseTimelineOptions.GetWindow(instance).ToKey() + "@" + today.Ticks;
            if (_narrowedHistory == null || !string.Equals(_narrowedHistoryKey, key, StringComparison.Ordinal))
            {
                _narrowedHistory = ShowcaseWidgetProjectionService.BuildScoreHistory(snapshot, instance, today);
                _narrowedHistoryKey = key;
            }

            return _narrowedHistory;
        }

        protected override void Refresh()
        {
            var source = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var instance = Projection?.Instance;
            var type = ShowcaseWidgetOptions.GetScoreCardType(instance);
            var snapshot = ApplyControlBarFilter(source, type, out var keptGames);
            var rarityBadges = ShowcaseWidgetOptions.GetScoreCardShowRarityBadges(instance)
                ? BuildRarityBadges(keptGames)
                : null;
            var gameArt = ResolveGameArt(keptGames, ShowcaseWidgetOptions.GetScoreCardArt(instance));

            Rows = 1;
            Columns = 1;
            IsFeatured = true;
            MaxCardWidth = Density == WidgetViewportDensity.Expanded ? 440 : 360;
            var badgePosition = ShowcaseWidgetOptions.GetScoreCardBadgePosition(instance);
            var useCustomColors = ShowcaseWidgetOptions.GetScoreCardUseCustomColors(instance);
            var showTotal = ShowcaseWidgetOptions.GetScoreCardShowTotal(instance);
            foreach (var existing in Cards)
            {
                existing.BadgePosition = badgePosition;
                existing.Card.SetUseCustomColors(useCustomColors);
                existing.Card.SetShowTotal(showTotal);
                existing.Card.SetRarityBadges(rarityBadges);
                existing.Card.SetGameArt(gameArt);
            }

            var history = ReferenceEquals(snapshot, source)
                ? Projection?.ScoreHistory ?? new List<ShowcaseScorePoint>()
                : NarrowedHistory(snapshot, instance);
            // Two points is the least that draws a line at all; the option then decides whether
            // the card spends its space on one.
            var showChart = history.Count >= 2 && ShowcaseWidgetOptions.GetScoreHistoryShown(instance);
            OverviewDataSnapshot builtSnapshot = null;
            _builtSnapshot?.TryGetTarget(out builtSnapshot);
            if (Cards.Count == 1 &&
                ReferenceEquals(_builtHistory, history) &&
                ReferenceEquals(builtSnapshot, snapshot) &&
                _builtType == type &&
                _builtShowChart == showChart)
            {
                return;
            }

            _builtHistory = history;
            _builtSnapshot = new WeakReference<OverviewDataSnapshot>(snapshot);
            _builtType = type;
            _builtShowChart = showChart;

            var rangeCaption = TimeWindowText.Describe(
                ShowcaseTimelineOptions.GetWindow(instance),
                history.Count > 0 ? history[0].Date : (DateTime?)null);
            var culture = FormattingCulture.Current;
            var historyLabels = history
                .Select(point => point.Date.ToString("d", culture))
                .ToList();
            var historyStart = history.Count > 0 ? historyLabels[0] : string.Empty;
            var historyEnd = history.Count > 0 ? historyLabels[historyLabels.Count - 1] : string.Empty;

            var uniformBadges = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?
                .UseUniformRarityBadges ?? false;
            var card = new ScoreCardViewModel(type);
            card.SetUseCustomColors(useCustomColors);
            card.SetShowTotal(showTotal);
            card.ApplyFor(type, snapshot, uniformBadges);
            card.SetRarityBadges(rarityBadges);
            card.SetGameArt(gameArt);
            Cards.ReplaceAll(new[]
            {
                new ScoreCardWithHistoryViewModel(
                    card,
                    snapshot.GetScore(type),
                    new ChartValues<int>(history.Select(point => point.GetScore(type))),
                    historyLabels,
                    showChart,
                    rangeCaption,
                    historyStart,
                    historyEnd)
                {
                    BadgePosition = badgePosition
                }
            });
        }    }
}
