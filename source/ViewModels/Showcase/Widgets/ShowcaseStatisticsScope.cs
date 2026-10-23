using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The games a Statistics or Profile widget counts: its collection first, then its control
    /// bar (which stays in effect while the bar is hidden). Hands back the projection's own
    /// snapshot and statistics while every game is kept, so the unfiltered widget keeps sharing
    /// the projection cache.
    /// </summary>
    public sealed class ShowcaseStatisticsScope
    {
        private readonly ShowcaseControlBarSlot<StatisticsControlBarAdapter> _controlBarSlot;

        // The last narrowed view, reused while its source and games hold. The source is held
        // weakly: it is change-detection state only.
        private WeakReference<OverviewDataSnapshot> _narrowedFrom;
        private HashSet<GameSummaryItem> _narrowedGames;
        private OverviewDataSnapshot _narrowed;
        private IReadOnlyList<ShowcaseStatistic> _narrowedStatistics;
        private DateTime _narrowedStatisticsDay;

        public ShowcaseStatisticsScope(Action onFilterChanged)
        {
            _controlBarSlot = new ShowcaseControlBarSlot<StatisticsControlBarAdapter>(onFilterChanged);
        }

        /// <summary>The bar items the widget's settings show; null when it shows none.</summary>
        public GridControlBarViewModel ShownBar => _controlBarSlot.ShownBar;

        /// <summary>The snapshot narrowed to the kept games, after <see cref="Apply"/>.</summary>
        public OverviewDataSnapshot Snapshot { get; private set; } = new OverviewDataSnapshot();

        /// <summary>The kept games' statistics, after <see cref="Apply"/>.</summary>
        public IReadOnlyList<ShowcaseStatistic> Statistics { get; private set; } = Array.Empty<ShowcaseStatistic>();

        public void Apply(ShowcaseWidgetProjection projection)
        {
            var source = projection?.Snapshot ?? new OverviewDataSnapshot();
            _controlBarSlot.Bind(projection?.Instance?.InstanceId);
            _controlBarSlot.UpdateShownBar(projection?.Instance);

            var adapter = _controlBarSlot.Adapter;
            var collection = projection?.Collection;
            var games = (source.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null)
                .ToList();
            // A linked widget's bar offers the whole library, not what the overview's filters left.
            adapter.UpdateOptions(projection?.IsLinked == true && projection.LinkedOptionGames != null
                ? projection.LinkedOptionGames.Where(game => game != null)
                : games);
            adapter.GameFilter.SetScope(collection?.GameIds);

            // The widget's collection comes before the bar, so its games are all the bar offers.
            var kept = adapter.Apply(collection == null
                ? games
                : games.Where(game => collection.ContainsGame(game.PlayniteGameId)));
            if (kept.Count == games.Count)
            {
                Snapshot = source;
                Statistics = projection?.Statistics ?? Array.Empty<ShowcaseStatistic>();
                return;
            }

            OverviewDataSnapshot narrowedFrom = null;
            _narrowedFrom?.TryGetTarget(out narrowedFrom);
            if (!ReferenceEquals(narrowedFrom, source) || _narrowedGames == null || !_narrowedGames.SetEquals(kept))
            {
                _narrowedFrom = new WeakReference<OverviewDataSnapshot>(source);
                _narrowedGames = new HashSet<GameSummaryItem>(kept);
                _narrowed = OverviewLinkedSnapshots.NarrowToGames(source, kept);
                _narrowedStatistics = null;
            }

            // Counted once per narrowed view and day, as the projection caches the library's.
            var now = DateTime.Now;
            if (_narrowedStatistics == null || _narrowedStatisticsDay != now.Date)
            {
                _narrowedStatistics = ShowcaseWidgetProjectionService.BuildStatistics(_narrowed, now);
                _narrowedStatisticsDay = now.Date;
            }

            Snapshot = _narrowed;
            Statistics = _narrowedStatistics;
        }
    }
}
