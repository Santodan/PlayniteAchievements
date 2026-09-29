using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using LiveCharts;
using LiveCharts.Wpf;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// One score card plus its cumulative score-over-time series for the mini line chart
    /// rendered under the card.
    /// </summary>
    public sealed class ScoreCardWithHistoryViewModel
    {
        private static readonly Func<double, string> AxisLabelFormatter =
            value => value.ToString("N0", FormattingCulture.Current);

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
            var frame = ScoreHistoryAxis.Frame(
                currentScore,
                hasHistory ? historyValues.Min() : currentScore,
                hasHistory ? historyValues.Max() : currentScore);
            HistoryMinValue = frame.Min;
            HistoryAxisMax = frame.Max;
            TierSections = BuildSections(frame, card?.AccentBrush, card?.NextTierAccentBrush);
        }

        public ScoreCardViewModel Card { get; }

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
    /// ScoreCardControl. Shows the collection and/or prestige card per the score mode, laid out in
    /// a UniformGrid whose orientation follows the viewport. Each card carries a cumulative score
    /// history line derived from the projection that fills whatever height the cell leaves under
    /// the card; density only scales the card width.
    /// </summary>
    public sealed class ScoresWidgetViewModel : ShowcaseWidgetViewModelBase
    {
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
        private ShowcaseScoreMode _builtMode;
        private ShowcaseScoreHistoryMode _builtHistoryMode;
        private bool _builtShowChart;

        public BulkObservableCollection<ScoreCardWithHistoryViewModel> Cards { get; } =
            new BulkObservableCollection<ScoreCardWithHistoryViewModel>();

        public int Rows { get => _rows; private set => SetValue(ref _rows, value); }

        public int Columns { get => _columns; private set => SetValue(ref _columns, value); }

        public bool IsFeatured { get => _isFeatured; private set => SetValue(ref _isFeatured, value); }

        public double MaxCardWidth { get => _maxCardWidth; private set => SetValue(ref _maxCardWidth, value); }

        protected override void Refresh()
        {
            var snapshot = Projection?.Snapshot ?? new OverviewDataSnapshot();
            var mode = ShowcaseWidgetOptions.GetScoreMode(Projection?.Instance);
            var includeCollection = mode != ShowcaseScoreMode.Prestige;
            var includePrestige = mode != ShowcaseScoreMode.Collection;
            var count = (includeCollection ? 1 : 0) + (includePrestige ? 1 : 0);
            var tall = Orientation == WidgetViewportOrientation.Tall;

            Rows = count > 1 && tall ? 2 : 1;
            Columns = count > 1 && !tall ? 2 : 1;
            IsFeatured = true;
            MaxCardWidth = Density == WidgetViewportDensity.Expanded ? 440 : 360;

            var history = Projection?.ScoreHistory ?? new List<ShowcaseScorePoint>();
            // Two points is the least that draws a line at all; the option then decides which
            // cards spend their space on one.
            var showChart = history.Count >= 2;
            var historyMode = ShowcaseWidgetOptions.GetScoreHistoryMode(Projection?.Instance);
            var showCollectionChart = showChart &&
                (historyMode == ShowcaseScoreHistoryMode.Dual ||
                    historyMode == ShowcaseScoreHistoryMode.Collection);
            var showPrestigeChart = showChart &&
                (historyMode == ShowcaseScoreHistoryMode.Dual ||
                    historyMode == ShowcaseScoreHistoryMode.Prestige);
            OverviewDataSnapshot builtSnapshot = null;
            _builtSnapshot?.TryGetTarget(out builtSnapshot);
            if (Cards.Count > 0 &&
                ReferenceEquals(_builtHistory, history) &&
                ReferenceEquals(builtSnapshot, Projection?.Snapshot) &&
                _builtMode == mode &&
                _builtHistoryMode == historyMode &&
                _builtShowChart == showChart)
            {
                return;
            }

            _builtHistory = history;
            _builtSnapshot = Projection?.Snapshot == null
                ? null
                : new WeakReference<OverviewDataSnapshot>(Projection.Snapshot);
            _builtMode = mode;
            _builtHistoryMode = historyMode;
            _builtShowChart = showChart;

            var rangeCaption = TimeWindowText.Describe(
                ShowcaseTimelineOptions.GetWindow(Projection?.Instance),
                history.Count > 0 ? history[0].Date : (DateTime?)null);
            var culture = FormattingCulture.Current;
            var historyLabels = history
                .Select(point => point.Date.ToString("d", culture))
                .ToList();
            var historyStart = history.Count > 0 ? historyLabels[0] : string.Empty;
            var historyEnd = history.Count > 0 ? historyLabels[historyLabels.Count - 1] : string.Empty;

            var uniformBadges = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?
                .UseUniformRarityBadges ?? false;
            var cards = new List<ScoreCardWithHistoryViewModel>();
            if (includeCollection)
            {
                var card = new ScoreCardViewModel(ScoreCardType.Collection);
                card.Apply(
                    snapshot.CollectorScore,
                    snapshot.CollectorLevel,
                    snapshot.CollectorLevelProgress,
                    snapshot.CollectorRank,
                    uniformBadges);
                cards.Add(new ScoreCardWithHistoryViewModel(
                    card,
                    snapshot.CollectorScore,
                    new ChartValues<int>(history.Select(point => point.CollectionScore)),
                    historyLabels,
                    showCollectionChart,
                    rangeCaption,
                    historyStart,
                    historyEnd));
            }

            if (includePrestige)
            {
                var card = new ScoreCardViewModel(ScoreCardType.Prestige);
                card.Apply(
                    snapshot.PrestigeScore,
                    snapshot.PrestigeLevel,
                    snapshot.PrestigeLevelProgress,
                    snapshot.PrestigeRank,
                    uniformBadges);
                cards.Add(new ScoreCardWithHistoryViewModel(
                    card,
                    snapshot.PrestigeScore,
                    new ChartValues<int>(history.Select(point => point.PrestigeScore)),
                    historyLabels,
                    showPrestigeChart,
                    rangeCaption,
                    historyStart,
                    historyEnd));
            }

            Cards.ReplaceAll(cards);
        }

    }
}
