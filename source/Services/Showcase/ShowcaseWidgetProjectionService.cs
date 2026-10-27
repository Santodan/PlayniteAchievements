using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Showcase
{
    public sealed class ShowcaseStatistic
    {
        public string Key { get; set; }

        public string LabelKey { get; set; }

        public double Value { get; set; }

        public bool HasValue { get; set; } = true;
    }

    public sealed class ShowcaseChartEntry
    {
        public string Key { get; set; }

        public string LabelKey { get; set; }

        public string Label { get; set; }

        public double Value { get; set; }

        public string SecondaryText { get; set; }
    }

    public sealed class ShowcaseActivityDay
    {
        public DateTime Date { get; set; }

        public int Count { get; set; }

        /// <summary>0 = no unlocks; 1..4 = percentile bucket among the window's active days,
        /// with the busiest day always 4.</summary>
        public int Intensity { get; set; }

        /// <summary>The achievements unlocked on this local day in unlock order, or null when
        /// none; the rows are the snapshot's own instances, shared across windows.</summary>
        public IReadOnlyList<AchievementDisplayItem> Unlocks { get; set; }
    }

    public sealed class ShowcaseActivityCalendar
    {
        /// <summary>Contiguous ascending days; the first entry falls on <see cref="FirstDayOfWeek"/> and the last is the end date.</summary>
        public IReadOnlyList<ShowcaseActivityDay> Days { get; set; } =
            Array.Empty<ShowcaseActivityDay>();

        /// <summary>The weekday each week column starts on.</summary>
        public DayOfWeek FirstDayOfWeek { get; set; } = DayOfWeek.Sunday;

        public DateTime StartDate { get; set; }

        public DateTime EndDate { get; set; }

        public int MaxCount { get; set; }

        public int TotalCount { get; set; }

        public int ActiveDayCount { get; set; }
    }

    public sealed class ShowcaseScorePoint
    {
        public DateTime Date { get; set; }

        /// <summary>Cumulative collection score as of the end of <see cref="Date"/>.</summary>
        public int CollectionScore { get; set; }

        /// <summary>Cumulative prestige score as of the end of <see cref="Date"/>.</summary>
        public int PrestigeScore { get; set; }

        /// <summary>Cumulative points per points family as of the end of <see cref="Date"/>.</summary>
        public IReadOnlyDictionary<string, int> PointsByFamily { get; set; } =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public int Gamerscore => GetPoints(PointsFamilies.Xbox);

        public int EpicXp => GetPoints(PointsFamilies.Epic);

        public int RetroPoints => GetPoints(PointsFamilies.RetroAchievements);

        public int GetPoints(string family) =>
            family != null && PointsByFamily != null && PointsByFamily.TryGetValue(family, out var points)
                ? points
                : 0;

        public int GetScore(ScoreCardType type)
        {
            if (type.IsPrestige)
            {
                return PrestigeScore;
            }

            return type.IsPoints ? GetPoints(type.PointsFamily) : CollectionScore;
        }
    }

    public sealed class ShowcaseAchievementItem
    {
        public PinnedAchievementReference Pin { get; set; }

        public AchievementDisplayItem Achievement { get; set; }

        public bool IsMissing => Achievement == null;

        public string Name => Achievement?.DisplayName ??
            Pin?.LastKnownAchievementName ??
            Pin?.ApiName ??
            string.Empty;

        public string GameName => Achievement?.GameName ??
            Pin?.LastKnownGameName ??
            string.Empty;

        public string IconPath => Achievement?.DisplayIcon;
    }

    /// <summary>
    /// The pin collection a widget is limited to, resolved against the showcase it projects from.
    /// A game collection keeps its games (and their achievements); an achievement collection
    /// keeps its pins, whose games are what the widget's Game filter lists.
    /// </summary>
    public sealed class ShowcaseCollectionScope
    {
        private readonly HashSet<Guid> _games;
        private readonly HashSet<(Guid, string)> _pins;

        private ShowcaseCollectionScope(
            ShowcaseCollectionType type,
            string collectionId,
            IReadOnlyList<Guid> gameIds,
            IReadOnlyList<PinnedAchievementReference> pins)
        {
            Type = type;
            CollectionId = collectionId;
            Pins = pins ?? Array.Empty<PinnedAchievementReference>();
            GameIds = gameIds;
            _games = new HashSet<Guid>(gameIds);
            _pins = new HashSet<(Guid, string)>(Pins.Select(pin => (pin.GameId, pin.ApiName?.ToUpperInvariant())));
        }

        public ShowcaseCollectionType Type { get; }

        public string CollectionId { get; }

        /// <summary>The collection's games, in pin order for a game collection.</summary>
        public IReadOnlyList<Guid> GameIds { get; }

        /// <summary>An achievement collection's pins in pin order; empty for a game collection.</summary>
        public IReadOnlyList<PinnedAchievementReference> Pins { get; }

        /// <summary>The instance's collection, or null when it has none or the collection is gone.</summary>
        public static ShowcaseCollectionScope Resolve(
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance)
        {
            var collectionId = ShowcaseWidgetOptions.GetPinCollectionId(instance);
            switch (ShowcaseWidgetOptions.GetCollectionType(instance))
            {
                case ShowcaseCollectionType.Achievements:
                    var achievements = ShowcasePinService.ResolveAchievementCollection(settings, collectionId);
                    if (achievements == null)
                    {
                        return null;
                    }

                    var pins = (achievements.Pins ?? new List<PinnedAchievementReference>())
                        .Where(pin => pin != null)
                        .ToList();
                    return new ShowcaseCollectionScope(
                        ShowcaseCollectionType.Achievements,
                        achievements.CollectionId,
                        pins.Select(pin => pin.GameId).Distinct().ToList(),
                        pins);
                case ShowcaseCollectionType.Games:
                    var games = ShowcasePinService.ResolveGameCollection(settings, collectionId);
                    return games == null
                        ? null
                        : new ShowcaseCollectionScope(
                            ShowcaseCollectionType.Games,
                            games.CollectionId,
                            (games.GameIds ?? new List<Guid>()).Distinct().ToList(),
                            null);
                default:
                    return null;
            }
        }

        public bool ContainsGame(Guid? gameId) => gameId.HasValue && _games.Contains(gameId.Value);

        /// <summary>A pinned achievement for an achievement collection; any achievement of a listed game otherwise.</summary>
        public bool ContainsAchievement(AchievementDisplayItem item)
        {
            if (item?.PlayniteGameId == null)
            {
                return false;
            }

            return Type == ShowcaseCollectionType.Achievements
                ? _pins.Contains((item.PlayniteGameId.Value, item.ApiName?.ToUpperInvariant()))
                : _games.Contains(item.PlayniteGameId.Value);
        }
    }

    public sealed class ShowcaseWidgetProjection
    {
        public ShowcaseWidgetInstanceSettings Instance { get; set; }

        public OverviewDataSnapshot Snapshot { get; set; }

        public IReadOnlyList<ShowcaseStatistic> Statistics { get; set; } =
            Array.Empty<ShowcaseStatistic>();

        public IReadOnlyList<ShowcaseChartEntry> ChartEntries { get; set; } =
            Array.Empty<ShowcaseChartEntry>();

        public IReadOnlyList<ShowcaseAchievementItem> Achievements { get; set; } =
            Array.Empty<ShowcaseAchievementItem>();

        public IReadOnlyList<AchievementDisplayItem> MosaicAchievements { get; set; } =
            Array.Empty<AchievementDisplayItem>();

        public IReadOnlyList<GameSummaryItem> Games { get; set; } =
            Array.Empty<GameSummaryItem>();

        public IReadOnlyDictionary<DateTime, int> Timeline { get; set; } =
            new Dictionary<DateTime, int>();

        /// <summary>Per-platform segments of <see cref="Timeline"/>; null unless the widget splits by platform.</summary>
        public IReadOnlyList<TimelineSeriesCounts> TimelineByPlatform { get; set; }

        public ShowcaseProfileSettings Profile { get; set; }

        /// <summary>Grid-ready achievement rows (pinned or recent, per widget kind).</summary>
        public IReadOnlyList<AchievementDisplayItem> AchievementRows { get; set; } =
            Array.Empty<AchievementDisplayItem>();

        public ShowcaseActivityCalendar ActivityCalendar { get; set; } =
            new ShowcaseActivityCalendar();

        public IReadOnlyList<ShowcaseScorePoint> ScoreHistory { get; set; } =
            Array.Empty<ShowcaseScorePoint>();

        public ShowcaseProfileProjection ResolvedProfile { get; set; }

        /// <summary>
        /// Live grid display options record for grid widget kinds
        /// (<see cref="AchievementGridOptions"/> or <see cref="GameSummaryGridOptions"/>,
        /// resolved from the widget's surface key); null for non-grid kinds or when no
        /// catalog was supplied.
        /// </summary>
        public object GridWidgetOptions { get; set; }

        /// <summary>
        /// The collection a widget showing a collection in pin order uses, after falling back to
        /// the protected Default collection. Reorder and unpin commands use this stable id.
        /// </summary>
        public string ResolvedPinCollectionId { get; set; }

        /// <summary>The pin collection the widget is limited to; null when it has none.</summary>
        public ShowcaseCollectionScope Collection { get; set; }

        /// <summary>
        /// Set by the overview's mini-showcase: the snapshot already follows the overview's
        /// filters, and the widget's clicks set those filters.
        /// </summary>
        public bool IsLinked { get; set; }

        /// <summary>
        /// The overview's whole library, for a linked widget's own control bar to offer its
        /// choices from, so the overview's filters (set by other widgets' clicks too) never
        /// shrink that bar or drop its selections. Null for an unlinked widget.
        /// </summary>
        public IReadOnlyList<GameSummaryItem> LinkedOptionGames { get; set; }

        /// <summary>
        /// A linked pie's selected slices as <see cref="OverviewLinkedSliceKeys"/> values or
        /// provider keys, never display labels.
        /// </summary>
        public IReadOnlyCollection<string> LinkedSliceKeys { get; set; } = Array.Empty<string>();

        /// <summary>The overview's unlock-day filter, for a linked timeline or calendar to mark.</summary>
        public UnlockDaySpan? HighlightedSpan { get; set; }

        /// <summary>The game a linked widget narrowed to, shown beside its title.</summary>
        public string ContextLabel { get; set; }

        /// <summary>
        /// The linked state this projection was decorated with (selected slices, the marked
        /// days, the narrowed game), in one comparable string. That state can change while the
        /// snapshot does not, and only a widget whose own stamp moved needs reprojecting.
        /// </summary>
        public string LinkedStamp { get; set; }
    }

    public static class ShowcaseWidgetProjectionService
    {
        // Derived series keyed by the snapshot that produced them. Rebuilding the dashboard for an
        // unrelated change (a pin toggle, a widget option, a resize) re-enters Build for every
        // widget; without this the score history and activity calendar would rescan the whole
        // library each time and hand the charts a new collection, forcing a re-plot. Entries die
        // with their snapshot, so a real data refresh recomputes exactly once.
        private static readonly ConditionalWeakTable<OverviewDataSnapshot, DerivedSeriesCache> DerivedCache =
            new ConditionalWeakTable<OverviewDataSnapshot, DerivedSeriesCache>();

        private sealed class DerivedSeriesCache
        {
            public readonly Dictionary<string, IReadOnlyList<ShowcaseScorePoint>> ScoreHistory =
                new Dictionary<string, IReadOnlyList<ShowcaseScorePoint>>(StringComparer.Ordinal);

            public readonly Dictionary<string, ShowcaseActivityCalendar> ActivityCalendars =
                new Dictionary<string, ShowcaseActivityCalendar>(StringComparer.Ordinal);

            public IReadOnlyList<ShowcaseStatistic> Statistics;

            public DateTime StatisticsDay;

            public DailyScoreDeltas ScoreDeltas;

            /// <summary>Unlocked rows grouped by local unlock day, each day in unlock order.</summary>
            public Dictionary<DateTime, IReadOnlyList<AchievementDisplayItem>> UnlocksByDay;

            public IReadOnlyList<AchievementDisplayItem> AllAchievementRows;

            public readonly Dictionary<string, IReadOnlyList<AchievementDisplayItem>> PinRows =
                new Dictionary<string, IReadOnlyList<AchievementDisplayItem>>(StringComparer.Ordinal);

            /// <summary>Unlock Next candidates still locked, with their game summary attached.</summary>
            public IReadOnlyList<UnlockNextCandidate> UnlockNextPool;
        }

        /// <summary>
        /// A locked candidate paired with the summary of the game it belongs to, so the window
        /// filter and the completion ranking do not re-scan the library per achievement.
        /// </summary>
        private sealed class UnlockNextCandidate
        {
            public AchievementDisplayItem Achievement;

            public GameSummaryItem Game;

            public double CompletionFraction;
        }

        private static string WindowKey(TimeWindow window, DateTime today) =>
            (window ?? ShowcaseTimelineOptions.DefaultWindow).ToKey() + "@" + today.Date.Ticks.ToString(CultureInfo.InvariantCulture);

        public static ShowcaseWidgetProjection Build(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance,
            DateTime? now = null,
            GridOptionsCatalog gridOptions = null,
            bool trimCalendarToUnlocks = false)
        {
            snapshot = snapshot ?? new OverviewDataSnapshot();
            settings = settings ?? new ShowcaseSettings();
            instance = instance ?? new ShowcaseWidgetInstanceSettings();

            var result = new ShowcaseWidgetProjection
            {
                Instance = instance,
                Snapshot = snapshot,
                Profile = instance.Profile ?? new ShowcaseProfileSettings()
            };

            AchievementGridOptions achievementOptions = null;
            GameSummaryGridOptions gameOptions = null;
            var surfaceKey = ShowcaseGridSurfaces.ResolveWidgetSurface(instance.Kind, instance.InstanceId);
            if (surfaceKey != null && gridOptions != null)
            {
                if (ShowcaseGridSurfaces.IsAchievementSurface(surfaceKey))
                {
                    achievementOptions = gridOptions.GetAchievement(surfaceKey);
                    result.GridWidgetOptions = achievementOptions;
                }
                else
                {
                    gameOptions = gridOptions.GetGameSummaries(surfaceKey);
                    result.GridWidgetOptions = gameOptions;
                }
            }

            switch (instance.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    // The library's statistics; the view model narrows them to its collection and
                    // control bar.
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    result.Statistics = GetStatistics(snapshot, now ?? DateTime.Now);
                    result.ResolvedProfile = ShowcaseProfileResolver.Resolve(
                        instance.Profile,
                        snapshot.CurrentUserIdentities);
                    break;
                case ShowcaseWidgetKind.Statistics:
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    result.Statistics = GetStatistics(snapshot, now ?? DateTime.Now);
                    break;
                case ShowcaseWidgetKind.NativePoints:
                    result.ChartEntries = BuildNativePoints(snapshot, instance);
                    break;
                case ShowcaseWidgetKind.IconMosaic:
                    // The collapsed Mosaic: achievement icons or game covers per the Content
                    // option; each content mode keeps its own source semantics.
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    if (ShowcaseWidgetOptions.GetMosaicContent(instance) == ShowcaseMosaicContent.Games)
                    {
                        if (ShowsGameCollectionInPinOrder(instance, result.Collection))
                        {
                            result.ResolvedPinCollectionId = result.Collection.CollectionId;
                        }

                        result.Games = ResolveGameMosaic(snapshot, settings, instance, int.MaxValue);
                        break;
                    }

                    if (ShowsPinsInPinOrder(instance, result.Collection))
                    {
                        result.ResolvedPinCollectionId = result.Collection.CollectionId;
                    }

                    result.MosaicAchievements = ResolveMosaic(snapshot, settings, instance, int.MaxValue);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    // The collapsed Achievements Grid: every unlocked achievement by default, an
                    // achievement collection's pins with reorder support, or the Unlock Next
                    // candidates; a collection limits the other sources to its rows.
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    if (ShowsPinsInPinOrder(instance, result.Collection))
                    {
                        result.ResolvedPinCollectionId = result.Collection.CollectionId;
                        result.Achievements = ResolvePinnedAchievements(snapshot, result.Collection.Pins);
                        result.AchievementRows = ResolvePinRowsCached(snapshot, result.Achievements);
                        break;
                    }

                    result.AchievementRows = LimitToCollection(
                        ShowcaseWidgetOptions.GetAchievementGridSource(instance) == ShowcaseAchievementGridSource.UnlockNext
                            // Uncapped here: the grid's MaxRows applies after its search filter.
                            ? ResolveUnlockNext(snapshot, instance, int.MaxValue, now)
                            : ResolveAllAchievements(snapshot),
                        result.Collection);
                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    // The collapsed Game Summaries Grid: the library by default, or the Playnite
                    // favorites or Finish Next subsets; a game collection limits them to its games,
                    // the library in pin order.
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    IReadOnlyList<GameSummaryItem> gridGames;
                    switch (ShowcaseWidgetOptions.GetGameGridSource(instance))
                    {
                        case ShowcaseGameGridSource.PlayniteFavorites:
                            gridGames = ResolvePlayniteFavorites(snapshot?.GameSummaries);
                            break;
                        case ShowcaseGameGridSource.FinishNext:
                            gridGames = ResolveFinishNextGames(snapshot?.GameSummaries, instance, now).ToList();
                            break;
                        default:
                            gridGames = ResolveGameSummaries(snapshot, instance);
                            if (result.Collection != null)
                            {
                                result.ResolvedPinCollectionId = result.Collection.CollectionId;
                                gridGames = ResolvePinnedGameSummaries(gridGames, result.Collection.GameIds);
                            }

                            break;
                    }

                    result.Games = LimitToCollection(gridGames, result.Collection);
                    break;
                case ShowcaseWidgetKind.Pie:
                    result.Collection = ShowcaseCollectionScope.Resolve(settings, instance);
                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    result.ActivityCalendar = GetActivityCalendar(
                        snapshot,
                        instance,
                        (now ?? DateTime.Now).Date,
                        trimCalendarToUnlocks);
                    break;
                case ShowcaseWidgetKind.Scores:
                    result.ScoreHistory = GetScoreHistory(snapshot, instance, (now ?? DateTime.Now).Date);
                    break;
                case ShowcaseWidgetKind.Timeline:
                    // The chart view model resolves the instance's window itself (the same
                    // TimeWindow the options hold), so the projection hands over the per-day
                    // counts unwindowed rather than clipping them a second time here.
                    result.Timeline = NormalizeDailyCounts(snapshot);
                    // Built per call rather than cached on the snapshot, so a platform recolor
                    // reaches the segments on the next rebuild.
                    result.TimelineByPlatform = ShowcaseTimelineOptions.GetSplit(instance) == ShowcaseTimelineSplit.Platform
                        ? TimelinePlatformSeries.FromSnapshot(snapshot)
                        : null;
                    break;
            }

            return result;
        }

        // The cached accessors below hand back the same instance for the same snapshot and window,
        // so repeated dashboard rebuilds neither rescan the library nor hand the charts a new
        // collection to re-plot.
        private static IReadOnlyList<ShowcaseStatistic> GetStatistics(
            OverviewDataSnapshot snapshot,
            DateTime now)
        {
            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (cache.Statistics == null || cache.StatisticsDay != now.Date)
            {
                cache.Statistics = BuildStatistics(snapshot, now);
                cache.StatisticsDay = now.Date;
            }

            return cache.Statistics;
        }

        private static IReadOnlyList<ShowcaseScorePoint> GetScoreHistory(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate)
        {
            var cache = DerivedCache.GetOrCreateValue(snapshot);
            var key = WindowKey(ShowcaseTimelineOptions.GetWindow(instance), endDate);
            if (!cache.ScoreHistory.TryGetValue(key, out var history))
            {
                history = BuildScoreHistory(snapshot, instance, endDate);
                cache.ScoreHistory[key] = history;
            }

            return history;
        }

        private static ShowcaseActivityCalendar GetActivityCalendar(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate,
            bool trimToUnlocks)
        {
            var cache = DerivedCache.GetOrCreateValue(snapshot);
            var firstDayOfWeek = FormattingCulture.Current.DateTimeFormat.FirstDayOfWeek;
            var key = WindowKey(ShowcaseTimelineOptions.GetWindow(instance), endDate)
                + (trimToUnlocks ? "|trim" : string.Empty)
                + "|" + (int)firstDayOfWeek;
            if (!cache.ActivityCalendars.TryGetValue(key, out var calendar))
            {
                calendar = BuildActivityCalendar(snapshot, instance, endDate, trimToUnlocks, firstDayOfWeek);
                cache.ActivityCalendars[key] = calendar;
            }

            return calendar;
        }

        /// <summary>Per-day unlock counts collapsed onto date keys, with negatives clamped away.</summary>
        private static Dictionary<DateTime, int> NormalizeDailyCounts(OverviewDataSnapshot snapshot)
        {
            return (snapshot?.GlobalUnlockCountsByDate ?? new Dictionary<DateTime, int>())
                .GroupBy(pair => pair.Key.Date)
                .ToDictionary(group => group.Key, group => group.Sum(pair => Math.Max(0, pair.Value)));
        }

        /// <summary>
        /// The instance's window resolved against <paramref name="today"/>: presets roll back from
        /// today, a custom range keeps its own bounds, and an open start falls back to the earliest
        /// counted day. This is the one resolver shared with the chart view models.
        /// </summary>
        private static DayRange ResolveDayRange(
            ShowcaseWidgetInstanceSettings instance,
            DateTime today,
            IReadOnlyDictionary<DateTime, int> counts)
        {
            return ShowcaseTimelineOptions.GetWindow(instance).Resolve(today, UnlockDayCounts.Earliest(counts));
        }

        /// <summary>Every day from start to end inclusive, with missing days reported as zero.</summary>
        private static IEnumerable<(DateTime Date, int Count)> EnumerateDailyWindow(
            DateTime start,
            DateTime end,
            IReadOnlyDictionary<DateTime, int> counts)
        {
            var days = Math.Max(1, (end - start).Days + 1);
            for (var offset = 0; offset < days; offset++)
            {
                var date = start.AddDays(offset);
                counts.TryGetValue(date, out var count);
                yield return (date, count);
            }
        }

        public static IReadOnlyList<ShowcaseStatistic> BuildStatistics(
            OverviewDataSnapshot snapshot,
            DateTime now)
        {
            snapshot = snapshot ?? new OverviewDataSnapshot();
            var summaries = snapshot.GameSummaries ?? new List<GameSummaryItem>();
            var achievements = snapshot.Achievements ?? new List<AchievementDisplayItem>();
            var counts = snapshot.GlobalUnlockCountsByDate ?? new Dictionary<DateTime, int>();

            // One pass over the day counts: the library can hold years of them and this runs for
            // every Profile and Statistics widget on the page.
            var thirtyDayStart = now.Date.AddDays(-29);
            var activeDays = 0;
            var datedUnlocks = 0;
            var lastThirtyDays = 0;
            foreach (var pair in counts)
            {
                var value = Math.Max(0, pair.Value);
                if (value <= 0)
                {
                    continue;
                }

                activeDays++;
                datedUnlocks += value;
                if (pair.Key.Date >= thirtyDayStart && pair.Key.Date <= now.Date)
                {
                    lastThirtyDays += value;
                }
            }

            // One pass over the achievements, accumulating instead of materializing the
            // ~tens of thousands of rarity percentages just to average them.
            var rarityCount = 0;
            var raritySum = 0d;
            foreach (var item in achievements)
            {
                if (item?.Unlocked == true && item.GlobalPercentUnlocked.HasValue)
                {
                    rarityCount++;
                    raritySum += item.GlobalPercentUnlocked.Value;
                }
            }

            CalculateStreaks(counts, now.Date, out var currentStreak, out var longestStreak);

            var playedGames = 0;
            var totalPlaytime = 0d;
            foreach (var game in summaries)
            {
                if (game == null)
                {
                    continue;
                }

                if (game.PlaytimeSeconds > 0 || game.LastPlayed.HasValue)
                {
                    playedGames++;
                }

                totalPlaytime += game.PlaytimeSeconds;
            }

            return new List<ShowcaseStatistic>
            {
                Stat("unlocked", "LOCPlayAch_Common_Unlocked", snapshot.TotalUnlocked),
                Stat("locked", "LOCPlayAch_Common_Locked", snapshot.TotalLocked),
                Stat("completion", "LOCPlayAch_ManageAchievements_Overview_Completion", snapshot.GlobalProgressionPercent),
                Stat("trackedGames", "LOCPlayAch_Showcase_Stat_TrackedGames", snapshot.TotalGames),
                Stat("playedGames", "LOCPlayAch_Showcase_Stat_PlayedGames", playedGames),
                Stat("completedGames", "LOCPlayAch_Showcase_Stat_CompletedGames", snapshot.Completions),
                Stat("playtime", "LOCPlayAch_Common_Label_Playtime", totalPlaytime),
                Stat(
                    "activeDayRate",
                    "LOCPlayAch_Showcase_Stat_ActiveDayRate",
                    activeDays > 0 ? (double)datedUnlocks / activeDays : 0),
                Stat("thirtyDayRate", "LOCPlayAch_Showcase_Stat_ThirtyDayRate", lastThirtyDays / 30d),
                Stat(
                    "averageGlobalUnlock",
                    "LOCPlayAch_Showcase_Stat_AverageGlobalUnlock",
                    rarityCount > 0 ? raritySum / rarityCount : 0,
                    hasValue: rarityCount > 0),
                Stat("currentStreak", "LOCPlayAch_Showcase_Stat_CurrentStreak", currentStreak),
                Stat("longestStreak", "LOCPlayAch_Showcase_Stat_LongestStreak", longestStreak)
            };
        }

        public static IReadOnlyList<ShowcaseChartEntry> BuildNativePoints(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance)
        {
            var summaries = snapshot?.GameSummaries ?? new List<GameSummaryItem>();
            var grouping = ShowcaseWidgetOptions.GetPointsGrouping(instance);
            var topN = ShowcaseWidgetOptions.GetTopN(instance);

            IEnumerable<ShowcaseChartEntry> entries;
            if (grouping == ShowcasePointsGrouping.Game)
            {
                entries = summaries
                    .Where(game => game != null && game.Points > 0)
                    .Select(game => new ShowcaseChartEntry
                    {
                        Key = game.PlayniteGameId?.ToString("D") ??
                            $"{game.ProviderKey}:{game.ProviderGameKey}",
                        Label = game.GameName,
                        Value = game.Points,
                        SecondaryText = game.Provider
                    });
            }
            else
            {
                entries = summaries
                    .Where(game => game != null && game.Points > 0)
                    .GroupBy(
                        game => string.IsNullOrWhiteSpace(game.ProviderKey)
                            ? "Unknown"
                            : game.ProviderKey,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(group => new ShowcaseChartEntry
                    {
                        Key = group.Key,
                        LabelKey = string.Equals(
                            group.Key,
                            "Unknown",
                            StringComparison.OrdinalIgnoreCase)
                            ? "LOCPlayAch_Showcase_UnknownProvider"
                            : null,
                        Label = group.Select(game => game.Provider)
                            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label)) ?? group.Key,
                        Value = group.Sum(game => (double)game.Points)
                    });
            }

            return ApplyTopN(entries, topN);
        }

        public static IReadOnlyList<ShowcaseAchievementItem> ResolvePinnedAchievements(
            OverviewDataSnapshot snapshot,
            IEnumerable<PinnedAchievementReference> pins)
        {
            var achievements = snapshot?.Achievements ?? new List<AchievementDisplayItem>();

            // Index by (game id, case-insensitive api name) so each pin resolves in O(1)
            // instead of scanning every achievement. The first entry wins on a key collision,
            // mirroring the prior FirstOrDefault; achievements without a Playnite game id cannot
            // match a pinned reference and are skipped.
            var lookup = new Dictionary<(Guid, string), AchievementDisplayItem>();
            foreach (var item in achievements)
            {
                if (item?.PlayniteGameId == null)
                {
                    continue;
                }

                var key = (item.PlayniteGameId.Value, item.ApiName?.ToUpperInvariant());
                if (!lookup.ContainsKey(key))
                {
                    lookup[key] = item;
                }
            }

            return (pins ?? Array.Empty<PinnedAchievementReference>())
                .Where(pin => pin != null)
                .Select(pin =>
                {
                    lookup.TryGetValue((pin.GameId, pin.ApiName?.ToUpperInvariant()), out var match);
                    return new ShowcaseAchievementItem { Pin = pin, Achievement = match };
                })
                .ToList();
        }

        // An achievement collection on the source that lists everything (the grid's All, the
        // mosaic's Recent) shows the collection itself: its pins in pin order, reorderable.
        private static bool ShowsPinsInPinOrder(
            ShowcaseWidgetInstanceSettings instance,
            ShowcaseCollectionScope collection)
        {
            if (collection?.Type != ShowcaseCollectionType.Achievements)
            {
                return false;
            }

            return instance.Kind == ShowcaseWidgetKind.IconMosaic
                ? ShowcaseWidgetOptions.GetMosaicSource(instance) == ShowcaseMosaicSource.All
                : ShowcaseWidgetOptions.GetAchievementGridSource(instance) == ShowcaseAchievementGridSource.All;
        }

        // The game mosaic's All source with a game collection shows its games in pin order.
        private static bool ShowsGameCollectionInPinOrder(
            ShowcaseWidgetInstanceSettings instance,
            ShowcaseCollectionScope collection) =>
            collection?.Type == ShowcaseCollectionType.Games &&
            ShowcaseWidgetOptions.GetGameMosaicSource(instance) == ShowcaseGameMosaicSource.All;

        private static IReadOnlyList<AchievementDisplayItem> LimitToCollection(
            IReadOnlyList<AchievementDisplayItem> rows,
            ShowcaseCollectionScope collection) =>
            collection == null
                ? rows
                : (rows ?? Array.Empty<AchievementDisplayItem>()).Where(collection.ContainsAchievement).ToList();

        private static IReadOnlyList<GameSummaryItem> LimitToCollection(
            IReadOnlyList<GameSummaryItem> games,
            ShowcaseCollectionScope collection) =>
            collection == null
                ? games
                : (games ?? Array.Empty<GameSummaryItem>())
                    .Where(game => collection.ContainsGame(game?.PlayniteGameId))
                    .ToList();

        /// <summary>Pinned games in pin order; unknown ids are skipped.</summary>
        private static IReadOnlyList<GameSummaryItem> ResolvePinnedGameSummaries(
            IReadOnlyList<GameSummaryItem> summaries,
            IEnumerable<Guid> pinnedGameIds)
        {
            var byId = (summaries ?? Array.Empty<GameSummaryItem>())
                .Where(game => game?.PlayniteGameId.HasValue == true)
                .GroupBy(game => game.PlayniteGameId.Value)
                .ToDictionary(group => group.Key, group => group.First());
            return (pinnedGameIds ?? Array.Empty<Guid>())
                .Where(byId.ContainsKey)
                .Select(id => byId[id])
                .ToList();
        }

        /// <summary>
        /// <see cref="MaterializePinRows"/> memoized per snapshot and pin-list identity, so
        /// re-projections of the same snapshot hand the grids reference-equal rows (missing
        /// pins otherwise allocate fresh placeholder rows each time, defeating the widgets'
        /// SameRows short-circuit and forcing a full grid rebuild). A pin edit changes the
        /// key, so its rebuild still happens.
        /// </summary>
        private static IReadOnlyList<AchievementDisplayItem> ResolvePinRowsCached(
            OverviewDataSnapshot snapshot,
            IReadOnlyList<ShowcaseAchievementItem> items)
        {
            if (snapshot == null)
            {
                return MaterializePinRows(items);
            }

            var key = string.Join(
                ";",
                (items ?? (IReadOnlyList<ShowcaseAchievementItem>)Array.Empty<ShowcaseAchievementItem>())
                    .Where(item => item != null)
                    .Select(item =>
                        (item.Pin?.GameId.ToString() ?? string.Empty) + ":" +
                        (item.Pin?.ApiName ?? string.Empty) + ":" +
                        (item.IsMissing ? "1" : "0")));

            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (!cache.PinRows.TryGetValue(key, out var rows))
            {
                rows = MaterializePinRows(items);
                cache.PinRows[key] = rows;
            }

            return rows;
        }

        /// <summary>
        /// Grid-ready rows for the pinned achievements widget. Missing pins become
        /// placeholder rows that keep the pin identity so the row menu's unpin and
        /// reorder actions still resolve them.
        /// </summary>
        public static IReadOnlyList<AchievementDisplayItem> MaterializePinRows(
            IEnumerable<ShowcaseAchievementItem> items)
        {
            return (items ?? Array.Empty<ShowcaseAchievementItem>())
                .Where(item => item != null)
                .Select(item => item.IsMissing ? CreatePlaceholderRow(item.Pin) : item.Achievement)
                .ToList();
        }

        /// <summary>
        /// A pin with no matching row (its achievement was renamed, filtered, or its game is gone).
        /// It is not a locked achievement, only an unresolved one, so it shows its last-known name
        /// unmasked: with the default appearance its blank detail read as locked, and the spoiler
        /// settings covered its icon, name and description as if it were a hidden achievement.
        /// </summary>
        private static AchievementDisplayItem CreatePlaceholderRow(PinnedAchievementReference pin)
        {
            var row = new AchievementDisplayItem
            {
                PlayniteGameId = pin?.GameId,
                ApiName = pin?.ApiName,
                DisplayName = pin?.LastKnownAchievementName,
                GameName = pin?.LastKnownGameName
            };
            row.ApplyAppearanceSettings(new AchievementDisplayItem.AppearanceSettingsSnapshot
            {
                ShowHiddenIcon = true,
                ShowHiddenTitle = true,
                ShowHiddenDescription = true,
                ShowLockedIcon = true
            });
            return row;
        }

        /// <summary>
        /// Every unlocked achievement in the snapshot, newest unlock first, so the grid's Default
        /// sort reads as newest unlocks. The snapshot also carries locked pinned goals (hydrated for
        /// the pinned widgets); they stay out of this source, where they read as spoiler-covered
        /// rows among the unlocks. Cached per snapshot because the full library is sorted once, not
        /// per dashboard rebuild; the MaxRows cap applies in the widget view model after its search
        /// filter.
        /// </summary>
        public static IReadOnlyList<AchievementDisplayItem> ResolveAllAchievements(
            OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new List<AchievementDisplayItem>();
            }

            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (cache.AllAchievementRows == null)
            {
                cache.AllAchievementRows = (snapshot.Achievements ?? new List<AchievementDisplayItem>())
                    .Where(item => item != null && item.Unlocked)
                    .OrderByDescending(item => item.UnlockTimeUtc ?? DateTime.MinValue)
                    .ToList();
            }

            return cache.AllAchievementRows;
        }

        /// <summary>
        /// The unfinished games closest to done within the instance's last-played window, shared
        /// by the Finish Next mosaic and grid sources. Unfinished by achievement count rather than
        /// IsCompleted, which is capstone based: a game can hold its capstone and still have
        /// achievements left.
        /// </summary>
        public static IEnumerable<GameSummaryItem> ResolveFinishNextGames(
            IEnumerable<GameSummaryItem> summaries,
            ShowcaseWidgetInstanceSettings instance,
            DateTime? now = null)
        {
            var cutoff = ResolveLastPlayedBounds(instance, now);
            var includeUnplayed = ShowcaseWidgetOptions.GetFinishNextIncludeUnplayed(instance);
            var minimumFraction = ShowcaseWidgetOptions.GetFinishNextMinimumProgress(instance) / 100d;
            var maxRemaining = ShowcaseWidgetOptions.GetFinishNextMaxRemaining(instance);
            var eligible = (summaries ?? Enumerable.Empty<GameSummaryItem>())
                .Where(game => game != null &&
                    game.TotalAchievements > 0 &&
                    game.UnlockedAchievements < game.TotalAchievements &&
                    (IsWithinWindow(game, cutoff) || (includeUnplayed && game.LastPlayed == null)) &&
                    CompletionFraction(game) >= minimumFraction &&
                    (maxRemaining == 0 || Remaining(game) <= maxRemaining));

            IOrderedEnumerable<GameSummaryItem> ranked;
            switch (ShowcaseWidgetOptions.GetFinishNextCriterion(instance))
            {
                case FinishNextCriterion.FewestRemaining:
                    ranked = eligible
                        .OrderBy(Remaining)
                        .ThenByDescending(CompletionFraction);
                    break;
                case FinishNextCriterion.EasiestRemaining:
                    ranked = eligible
                        .OrderByDescending(RemainingEase)
                        .ThenBy(Remaining);
                    break;
                default:
                    ranked = eligible
                        .OrderByDescending(CompletionFraction)
                        .ThenBy(Remaining);
                    break;
            }

            return ranked.ThenByDescending(game => game.LastPlayed ?? DateTime.MinValue);
        }

        private static int Remaining(GameSummaryItem game) =>
            Math.Max(0, game.TotalAchievements - game.UnlockedAchievements);

        /// <summary>
        /// How commonly earned a game's remaining achievements are, 0 (all ultra rare) to 1 (all
        /// common), from the locked count in each rarity tier. A game with no tier data sits in the
        /// middle rather than first or last.
        /// </summary>
        private static double RemainingEase(GameSummaryItem game)
        {
            var common = Math.Max(0, game.TotalCommonPossible - game.CommonCount);
            var uncommon = Math.Max(0, game.TotalUncommonPossible - game.UncommonCount);
            var rare = Math.Max(0, game.TotalRarePossible - game.RareCount);
            var ultraRare = Math.Max(0, game.TotalUltraRarePossible - game.UltraRareCount);
            var total = common + uncommon + rare + ultraRare;
            if (total == 0)
            {
                return 0.5;
            }

            return (common + (uncommon * 2d / 3d) + (rare / 3d)) / total;
        }

        /// <summary>Playnite-favorite games, alphabetical.</summary>
        private static IReadOnlyList<GameSummaryItem> ResolvePlayniteFavorites(
            IReadOnlyList<GameSummaryItem> summaries)
        {
            return (summaries ?? new List<GameSummaryItem>())
                .Where(game => game?.IsFavorite == true)
                .OrderBy(game => game.GameName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<GameSummaryItem> ResolveGameSummaries(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance)
        {
            var games = (snapshot?.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null);

            // Progress and activity are the control bar's filters. Sorting and the MaxRows cap apply in the widget view model (alongside its
            // control-bar filters), so editing those options re-orders one widget's rows
            // instead of re-projecting the whole dashboard.
            return games.ToList();
        }

        public static IReadOnlyList<GameSummaryItem> ResolveGameMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance) =>
            ResolveGameMosaic(snapshot, settings, instance, ShowcaseWidgetOptions.GetGameMosaicCount(instance));

        /// <summary>
        /// The game mosaic's source rows in source order, capped at <paramref name="count"/>. The
        /// widget projection passes no cap so the control-bar filter reaches the whole source,
        /// and the widget applies the Count option after filtering.
        /// </summary>
        public static IReadOnlyList<GameSummaryItem> ResolveGameMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance,
            int count)
        {
            var summaries = snapshot?.GameSummaries ?? new List<GameSummaryItem>();
            var collection = ShowcaseCollectionScope.Resolve(settings, instance);
            IEnumerable<GameSummaryItem> games;
            switch (ShowcaseWidgetOptions.GetGameMosaicSource(instance))
            {
                case ShowcaseGameMosaicSource.PlayniteFavorites:
                    games = summaries
                        .Where(game => game?.IsFavorite == true)
                        .OrderBy(game => game.GameName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                case ShowcaseGameMosaicSource.FinishNext:
                    games = ResolveFinishNextGames(summaries, instance);
                    break;
                default:
                    games = ShowsGameCollectionInPinOrder(instance, collection)
                        ? ResolvePinnedGameSummaries(summaries, collection.GameIds)
                        : summaries
                            .Where(game => game != null)
                            .OrderByDescending(game => game.LastUnlockUtc ?? DateTime.MinValue);
                    break;
            }

            if (collection != null)
            {
                games = games.Where(game => collection.ContainsGame(game?.PlayniteGameId));
            }

            return games.Take(count).ToList();
        }

        /// <param name="trimToUnlocks">
        /// Narrows the window to the days from the first unlock in it to the last, for a calendar
        /// following a selection (the overview's mini-showcase) whose span is what matters, not
        /// the run up to today.
        /// </param>
        /// <param name="firstDayOfWeek">
        /// The weekday week columns start on; null follows the formatting culture, so a locale
        /// whose weeks start on Monday gets Monday-first columns.
        /// </param>
        public static ShowcaseActivityCalendar BuildActivityCalendar(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate,
            bool trimToUnlocks = false,
            DayOfWeek? firstDayOfWeek = null)
        {
            var weekStart = firstDayOfWeek ?? FormattingCulture.Current.DateTimeFormat.FirstDayOfWeek;
            var counts = NormalizeDailyCounts(snapshot);
            var range = ResolveDayRange(instance, endDate.Date, counts);
            var start = range.Start;
            // A custom window with a fixed end stops the calendar there rather than at today.
            endDate = range.End;
            if (trimToUnlocks)
            {
                var active = counts
                    .Where(pair => pair.Value > 0 && pair.Key >= start && pair.Key <= endDate)
                    .Select(pair => pair.Key)
                    .ToList();
                if (active.Count > 0)
                {
                    start = active.Min();
                    endDate = active.Max();
                }
            }
            // Weeks render as columns starting on weekStart, so the window starts on that weekday.
            while (start.DayOfWeek != weekStart)
            {
                start = start.AddDays(-1);
            }

            var days = new List<ShowcaseActivityDay>();
            var max = 0;
            var total = 0;
            var activeDays = 0;
            var unlocksByDay = GetUnlocksByDay(snapshot);
            foreach (var day in EnumerateDailyWindow(start, endDate, counts))
            {
                max = Math.Max(max, day.Count);
                total += day.Count;
                IReadOnlyList<AchievementDisplayItem> unlocks = null;
                if (day.Count > 0)
                {
                    activeDays++;
                    unlocksByDay.TryGetValue(day.Date, out unlocks);
                }

                days.Add(new ShowcaseActivityDay { Date = day.Date, Count = day.Count, Unlocks = unlocks });
            }

            var activeCounts = days
                .Where(day => day.Count > 0)
                .Select(day => day.Count)
                .OrderBy(count => count)
                .ToList();
            var lowerQuartile = Percentile(activeCounts, 0.25);
            var median = Percentile(activeCounts, 0.5);
            var upperQuartile = Percentile(activeCounts, 0.75);
            foreach (var day in days)
            {
                day.Intensity = ComputeActivityIntensity(
                    day.Count,
                    max,
                    lowerQuartile,
                    median,
                    upperQuartile);
            }

            return new ShowcaseActivityCalendar
            {
                Days = days,
                FirstDayOfWeek = weekStart,
                StartDate = start,
                EndDate = endDate,
                MaxCount = max,
                TotalCount = total,
                ActiveDayCount = activeDays
            };
        }

        /// <summary>
        /// The snapshot's unlocked rows grouped by local unlock day, folded once per snapshot the
        /// way the score deltas are. A calendar cell hands its day's list to the day popup, so the
        /// lists hold the snapshot's own row instances and die with the snapshot; undated unlocks
        /// have no day and are left out, matching the day counts.
        /// </summary>
        private static Dictionary<DateTime, IReadOnlyList<AchievementDisplayItem>> GetUnlocksByDay(
            OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new Dictionary<DateTime, IReadOnlyList<AchievementDisplayItem>>();
            }

            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (cache.UnlocksByDay != null)
            {
                return cache.UnlocksByDay;
            }

            var grouped = new Dictionary<DateTime, List<AchievementDisplayItem>>();
            foreach (var item in snapshot.Achievements ?? new List<AchievementDisplayItem>())
            {
                if (item?.Unlocked != true || !item.UnlockTimeUtc.HasValue)
                {
                    continue;
                }

                var day = UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value);
                if (!grouped.TryGetValue(day, out var rows))
                {
                    rows = new List<AchievementDisplayItem>();
                    grouped[day] = rows;
                }

                rows.Add(item);
            }

            var index = new Dictionary<DateTime, IReadOnlyList<AchievementDisplayItem>>(grouped.Count);
            foreach (var pair in grouped)
            {
                // Chronological within the day, so the list reads as the day's activity.
                pair.Value.Sort((left, right) => left.UnlockTimeUtc.Value.CompareTo(right.UnlockTimeUtc.Value));
                index[pair.Key] = pair.Value;
            }

            cache.UnlocksByDay = index;
            return index;
        }

        /// <summary>
        /// Percentile bucket among the window's active days; 0 only for zero-count days. Rank-based
        /// thresholds keep one outlier day from washing every typical day into the lightest tier
        /// the way max-relative quartiles did; the busiest day always renders at full intensity,
        /// which also keeps uniform-activity and single-active-day windows dark rather than faint.
        /// </summary>
        private static int ComputeActivityIntensity(
            int count,
            int max,
            int lowerQuartile,
            int median,
            int upperQuartile)
        {
            if (count <= 0 || max <= 0)
            {
                return 0;
            }

            if (count >= max)
            {
                return 4;
            }

            return count <= lowerQuartile ? 1 : count <= median ? 2 : count <= upperQuartile ? 3 : 4;
        }

        /// <summary>Nearest-rank percentile of an ascending-sorted list; 0 when the list is empty.</summary>
        private static int Percentile(IReadOnlyList<int> sorted, double quantile)
        {
            if (sorted == null || sorted.Count == 0)
            {
                return 0;
            }

            var rank = (int)Math.Ceiling(quantile * sorted.Count);
            return sorted[Math.Max(1, Math.Min(sorted.Count, rank)) - 1];
        }

        private sealed class DailyScoreDeltas
        {
            public readonly Dictionary<DateTime, int> Collection = new Dictionary<DateTime, int>();
            public readonly Dictionary<DateTime, int> Prestige = new Dictionary<DateTime, int>();
            public readonly Dictionary<string, Dictionary<DateTime, int>> Points =
                new Dictionary<string, Dictionary<DateTime, int>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, int> PointsBaseline =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            public int BaselineCollection;
            public int BaselinePrestige;
            public bool SawUnlocked;

            public void AddPoints(string family, DateTime day, int points)
            {
                if (!Points.TryGetValue(family, out var daily))
                {
                    daily = new Dictionary<DateTime, int>();
                    Points[family] = daily;
                }

                daily.TryGetValue(day, out var earned);
                daily[day] = earned + points;
            }

            public void AddPointsBaseline(string family, int points)
            {
                PointsBaseline.TryGetValue(family, out var baseline);
                PointsBaseline[family] = baseline + points;
            }
        }

        /// <summary>
        /// The points family per game, from the game summary, which already folds in the game's
        /// override and its effective platform key. Achievement rows carry the raw provider key,
        /// so an Exophase Xbox unlock is resolved through its game here. A game whose points count
        /// toward no card maps to null.
        /// </summary>
        private static Dictionary<Guid, string> BuildPointsFamiliesByGame(OverviewDataSnapshot snapshot)
        {
            var families = new Dictionary<Guid, string>();
            foreach (var game in snapshot?.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game?.PlayniteGameId.HasValue == true)
                {
                    families[game.PlayniteGameId.Value] = game.PointsFamily;
                }
            }

            return families;
        }

        /// <summary>
        /// Per-day score earned, folded out of the achievement list once per snapshot. This is the
        /// only step that scales with the library, so changing a chart's range re-windows these
        /// deltas instead of rescanning every achievement.
        /// </summary>
        private static DailyScoreDeltas GetDailyScoreDeltas(OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new DailyScoreDeltas();
            }

            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (cache.ScoreDeltas != null)
            {
                return cache.ScoreDeltas;
            }

            var deltas = new DailyScoreDeltas();
            var familiesByGame = BuildPointsFamiliesByGame(snapshot);
            // Single pass over the full achievement list - materializing the unlocked subset
            // would allocate a list the size of the user's whole unlock history.
            foreach (var item in snapshot.Achievements ?? new List<AchievementDisplayItem>())
            {
                if (item?.Unlocked != true)
                {
                    continue;
                }

                deltas.SawUnlocked = true;
                var family = item.PlayniteGameId.HasValue &&
                    familiesByGame.TryGetValue(item.PlayniteGameId.Value, out var gameFamily)
                        ? gameFamily
                        : PointsFamilies.FromProviderKey(item.ProviderKey);
                var platformPoints = item.Points;
                // Softcore unlocks are left out, as in the points totals.
                var hasPlatformScore = platformPoints > 0 &&
                    family != null &&
                    !AchievementTagHelper.IsSoftcore(item.Tags);

                if (item.UnlockTimeUtc.HasValue)
                {
                    var day = UnlockDayCounts.DayOf(item.UnlockTimeUtc.Value);
                    deltas.Collection.TryGetValue(day, out var collection);
                    deltas.Collection[day] = collection + item.CollectionScore;
                    deltas.Prestige.TryGetValue(day, out var prestige);
                    deltas.Prestige[day] = prestige + item.PrestigeScore;
                    if (hasPlatformScore)
                    {
                        deltas.AddPoints(family, day, platformPoints);
                    }
                }
                else
                {
                    deltas.BaselineCollection += item.CollectionScore;
                    deltas.BaselinePrestige += item.PrestigeScore;
                    if (hasPlatformScore)
                    {
                        deltas.AddPointsBaseline(family, platformPoints);
                    }
                }
            }

            cache.ScoreDeltas = deltas;
            return deltas;
        }

        /// <summary>
        /// Cumulative score per day, reconstructed retroactively from unlock timestamps
        /// with today's per-achievement score values. Unlocks without a timestamp form a
        /// constant baseline so the curve ends at the true earned total.
        /// </summary>
        public static IReadOnlyList<ShowcaseScorePoint> BuildScoreHistory(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate)
        {
            endDate = endDate.Date;
            var deltas = GetDailyScoreDeltas(snapshot);
            if (!deltas.SawUnlocked)
            {
                return Array.Empty<ShowcaseScorePoint>();
            }

            var dailyCollection = deltas.Collection;
            var dailyPrestige = deltas.Prestige;
            var earliestDay = dailyCollection.Count > 0 ? dailyCollection.Keys.Min() : (DateTime?)null;
            var range = ShowcaseTimelineOptions.GetWindow(instance).Resolve(endDate, earliestDay);
            var start = range.Start;
            endDate = range.End;

            // Unlocks before the window still count toward the running totals, so the first
            // point starts from the score already earned rather than from zero.
            var cumulativeCollection = deltas.BaselineCollection +
                dailyCollection.Where(pair => pair.Key < start).Sum(pair => pair.Value);
            var cumulativePrestige = deltas.BaselinePrestige +
                dailyPrestige.Where(pair => pair.Key < start).Sum(pair => pair.Value);
            var cumulativePoints = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var family in deltas.Points.Keys
                .Concat(deltas.PointsBaseline.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                deltas.PointsBaseline.TryGetValue(family, out var baseline);
                cumulativePoints[family] = baseline + (deltas.Points.TryGetValue(family, out var daily)
                    ? daily.Where(pair => pair.Key < start).Sum(pair => pair.Value)
                    : 0);
            }

            var rangeDays = Math.Max(1, (endDate - start).Days + 1);
            // The chart is a couple of hundred pixels wide at most, and a hoverable LiveCharts
            // series carries a hit-testable shape per point, so emitting a point per day would
            // cost hundreds of invisible shapes. This is still finer than one point per pixel.
            var step = Math.Max(1, (int)Math.Ceiling(rangeDays / 120.0));
            var points = new List<ShowcaseScorePoint>();
            var offset = 0;
            foreach (var day in EnumerateDailyWindow(start, endDate, dailyCollection))
            {
                cumulativeCollection += day.Count;
                if (dailyPrestige.TryGetValue(day.Date, out var prestige))
                {
                    cumulativePrestige += prestige;
                }

                foreach (var family in deltas.Points)
                {
                    if (family.Value.TryGetValue(day.Date, out var earned))
                    {
                        cumulativePoints[family.Key] += earned;
                    }
                }

                if (offset % step == 0 || offset == rangeDays - 1)
                {
                    points.Add(new ShowcaseScorePoint
                    {
                        Date = day.Date,
                        CollectionScore = cumulativeCollection,
                        PrestigeScore = cumulativePrestige,
                        PointsByFamily = new Dictionary<string, int>(cumulativePoints, StringComparer.OrdinalIgnoreCase)
                    });
                }

                offset++;
            }

            return points;
        }

        public static IReadOnlyList<AchievementDisplayItem> ResolveMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance) =>
            ResolveMosaic(snapshot, settings, instance, ShowcaseWidgetOptions.GetMosaicCount(instance));

        /// <summary>
        /// The achievement mosaic's source rows in source order, capped at <paramref name="count"/>.
        /// The widget projection passes no cap so the control-bar filter reaches the whole source,
        /// and the widget applies the Count option after filtering. Unlock Next walks its ranking
        /// greedily, so a later cap keeps the same rows a capped call returns.
        /// </summary>
        public static IReadOnlyList<AchievementDisplayItem> ResolveMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance,
            int count)
        {
            var source = ShowcaseWidgetOptions.GetMosaicSource(instance);
            var collection = ShowcaseCollectionScope.Resolve(settings, instance);
            IEnumerable<AchievementDisplayItem> achievements;
            switch (source)
            {
                case ShowcaseMosaicSource.UnlockNext:
                    // Selection, not re-arrangement: the criterion decides which locked
                    // achievements make the cut, so it runs here and the widget's generic sort
                    // is hidden for this source. A collection drops the rest of the ranking, so
                    // the cap applies after it.
                    if (collection == null)
                    {
                        return ResolveUnlockNext(snapshot, instance, count);
                    }

                    achievements = ResolveUnlockNext(snapshot, instance, int.MaxValue);
                    break;
                default:
                    achievements = ShowsPinsInPinOrder(instance, collection)
                        ? ResolvePinnedAchievements(snapshot, collection.Pins)
                            .Where(item => !item.IsMissing)
                            .Select(item => item.Achievement)
                        : (snapshot?.RecentAchievements ?? new List<AchievementDisplayItem>())
                            .Where(item => item != null)
                            .OrderByDescending(item => item.UnlockTimeUtc);
                    break;
            }

            if (collection != null)
            {
                achievements = achievements.Where(collection.ContainsAchievement);
            }

            return achievements.Take(count).ToList();
        }

        /// <summary>
        /// Locked achievements to hunt next, narrowed out of the snapshot's bounded candidate pool
        /// by the widget's window and hidden-achievement choice, ranked by its criterion, then
        /// capped per game. The pool itself is config-independent, so every option edit is answered here
        /// without rebuilding the snapshot.
        /// </summary>
        public static IReadOnlyList<AchievementDisplayItem> ResolveUnlockNext(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            int count,
            DateTime? now = null)
        {
            var pool = ResolveUnlockNextPool(snapshot);
            if (pool.Count == 0)
            {
                return new List<AchievementDisplayItem>();
            }

            var criterion = ShowcaseWidgetOptions.GetUnlockNextCriterion(instance);
            var includeHidden = ShowcaseWidgetOptions.GetIncludeHiddenAchievements(instance);
            var cutoff = ResolveLastPlayedBounds(instance, now);

            var eligible = pool.Where(candidate =>
                (includeHidden || !candidate.Achievement.Hidden) &&
                IsWithinWindow(candidate.Game, cutoff));

            // The per-game cap is a ceiling, not a quota: walking one ranked list means a game only
            // appears when its achievements earn a slot, and no game is handed a hard achievement
            // just so every game gets a tile.
            var perGame = ShowcaseWidgetOptions.GetMaxPerGame(instance);
            var takenPerGame = new Dictionary<Guid, int>();
            var result = new List<AchievementDisplayItem>();
            foreach (var candidate in Rank(eligible, criterion))
            {
                if (result.Count >= count)
                {
                    break;
                }

                var gameId = candidate.Achievement.PlayniteGameId ?? Guid.Empty;
                takenPerGame.TryGetValue(gameId, out var taken);
                if (taken >= perGame)
                {
                    continue;
                }

                takenPerGame[gameId] = taken + 1;
                result.Add(candidate.Achievement);
            }

            return result;
        }

        /// <summary>
        /// The snapshot's candidate pool, dropping anything unlocked since it was built (a delta
        /// tick carries the pool forward rather than rehydrating it) and pairing each row with its
        /// game summary. Cached per snapshot because it scans the library's unlocked rows once.
        /// </summary>
        private static IReadOnlyList<UnlockNextCandidate> ResolveUnlockNextPool(
            OverviewDataSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return new List<UnlockNextCandidate>();
            }

            var cache = DerivedCache.GetOrCreateValue(snapshot);
            if (cache.UnlockNextPool != null)
            {
                return cache.UnlockNextPool;
            }

            var candidates = snapshot.UnlockNextCandidates ?? new List<AchievementDisplayItem>();
            if (candidates.Count == 0)
            {
                cache.UnlockNextPool = new List<UnlockNextCandidate>();
                return cache.UnlockNextPool;
            }

            var summariesById = new Dictionary<Guid, GameSummaryItem>();
            foreach (var summary in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (summary?.PlayniteGameId != null)
                {
                    summariesById[summary.PlayniteGameId.Value] = summary;
                }
            }

            var unlockedSince = BuildUnlockedKeySet(snapshot, candidates);
            var pool = new List<UnlockNextCandidate>(candidates.Count);
            foreach (var achievement in candidates)
            {
                if (achievement == null ||
                    achievement.Unlocked ||
                    achievement.PlayniteGameId == null)
                {
                    continue;
                }

                if (unlockedSince.Contains(UnlockNextKey(
                        achievement.PlayniteGameId.Value,
                        achievement.ApiName)))
                {
                    continue;
                }

                summariesById.TryGetValue(achievement.PlayniteGameId.Value, out var summary);
                pool.Add(new UnlockNextCandidate
                {
                    Achievement = achievement,
                    Game = summary,
                    CompletionFraction = CompletionFraction(summary)
                });
            }

            cache.UnlockNextPool = pool;
            return pool;
        }

        /// <summary>
        /// Keys of pool achievements that the snapshot now reports as unlocked. Built from the
        /// pool's own keys so the scan over the library's unlocked rows allocates nothing per row.
        /// </summary>
        private static HashSet<string> BuildUnlockedKeySet(
            OverviewDataSnapshot snapshot,
            IReadOnlyList<AchievementDisplayItem> candidates)
        {
            var poolKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (candidate?.PlayniteGameId != null)
                {
                    poolKeys.Add(UnlockNextKey(candidate.PlayniteGameId.Value, candidate.ApiName));
                }
            }

            var unlocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in snapshot.Achievements ?? new List<AchievementDisplayItem>())
            {
                if (item?.Unlocked != true || item.PlayniteGameId == null)
                {
                    continue;
                }

                var key = UnlockNextKey(item.PlayniteGameId.Value, item.ApiName);
                if (poolKeys.Contains(key))
                {
                    unlocked.Add(key);
                }
            }

            return unlocked;
        }

        private static string UnlockNextKey(Guid gameId, string apiName) =>
            gameId.ToString("N") + "|" + (apiName ?? string.Empty);

        /// <summary>
        /// One ranked list across every game. Easiest leads with the most commonly earned;
        /// Closest to completion leads with the games nearest done and, within a game, with its
        /// most commonly earned leftovers rather than its hardest.
        /// </summary>
        private static IEnumerable<UnlockNextCandidate> Rank(
            IEnumerable<UnlockNextCandidate> candidates,
            UnlockNextCriterion criterion)
        {
            var ranked = criterion == UnlockNextCriterion.Easiest
                ? candidates.OrderBy(candidate => 0)
                : candidates.OrderByDescending(candidate => candidate.CompletionFraction);

            // Achievements with no global percentage are not known to be easy, so they sort
            // behind every achievement that has one rather than winning the slot.
            return ranked
                .ThenByDescending(candidate => candidate.Achievement.GlobalPercentUnlocked.HasValue)
                .ThenByDescending(candidate => candidate.Achievement.GlobalPercentUnlocked ?? 0)
                .ThenBy(candidate => candidate.Achievement.DefaultOrderIndex)
                .ThenBy(candidate => candidate.Achievement.ApiName, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The local-day bounds a game's last-played day must fall inside to contribute; unbounded
        /// for All. A custom window bounds both ends.
        /// </summary>
        private static DayBounds ResolveLastPlayedBounds(ShowcaseWidgetInstanceSettings instance, DateTime? now)
        {
            return ShowcaseWidgetOptions.GetLastPlayedTimeWindow(instance).ResolveBounds((now ?? DateTime.Now).Date);
        }

        private static bool IsWithinWindow(GameSummaryItem game, DayBounds bounds)
        {
            if (bounds.IsUnbounded)
            {
                return true;
            }

            // A game that was never played has no date to compare, so a window excludes it.
            return game?.LastPlayed != null &&
                bounds.Contains(Common.DateTimeUtilities.ToLocalDay(game.LastPlayed.Value));
        }

        private static double CompletionFraction(GameSummaryItem summary)
        {
            return summary == null || summary.TotalAchievements <= 0
                ? 0
                : (double)summary.UnlockedAchievements / summary.TotalAchievements;
        }

        private static IReadOnlyList<ShowcaseChartEntry> ApplyTopN(
            IEnumerable<ShowcaseChartEntry> source,
            int topN)
        {
            var ordered = (source ?? Array.Empty<ShowcaseChartEntry>())
                .Where(entry => entry != null && entry.Value > 0)
                .OrderByDescending(entry => entry.Value)
                .ThenBy(entry => entry.Label, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (ordered.Count <= topN)
            {
                return ordered;
            }

            var result = ordered.Take(topN).ToList();
            result.Add(new ShowcaseChartEntry
            {
                Key = "Other",
                LabelKey = "LOCOther",
                Value = ordered.Skip(topN).Sum(entry => entry.Value)
            });
            return result;
        }

        private static void CalculateStreaks(
            IDictionary<DateTime, int> counts,
            DateTime today,
            out int current,
            out int longest)
        {
            var dates = new HashSet<DateTime>(
                (counts ?? new Dictionary<DateTime, int>())
                    .Where(pair => pair.Value > 0)
                    .Select(pair => pair.Key.Date));
            longest = 0;
            var running = 0;
            DateTime? previous = null;
            foreach (var date in dates.OrderBy(date => date))
            {
                running = previous.HasValue && date == previous.Value.AddDays(1)
                    ? running + 1
                    : 1;
                longest = Math.Max(longest, running);
                previous = date;
            }

            current = 0;
            var cursor = dates.Contains(today) ? today : today.AddDays(-1);
            while (dates.Contains(cursor))
            {
                current++;
                cursor = cursor.AddDays(-1);
            }
        }

        private static ShowcaseStatistic Stat(
            string key,
            string labelKey,
            double value,
            bool hasValue = true)
        {
            return new ShowcaseStatistic
            {
                Key = key,
                LabelKey = labelKey,
                Value = value,
                HasValue = hasValue
            };
        }
    }
}
