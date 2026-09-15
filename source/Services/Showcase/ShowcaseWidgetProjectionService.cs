using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
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
    }

    public sealed class ShowcaseActivityCalendar
    {
        /// <summary>Contiguous ascending days; the first entry is a Sunday and the last is the end date.</summary>
        public IReadOnlyList<ShowcaseActivityDay> Days { get; set; } =
            Array.Empty<ShowcaseActivityDay>();

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
        /// Collection actually used by a pinned-source widget after falling back to the
        /// protected Default collection. Reorder and unpin commands use this stable id.
        /// </summary>
        public string ResolvedPinCollectionId { get; set; }
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

            public IReadOnlyList<AchievementDisplayItem> AllAchievementRows;

            public readonly Dictionary<string, IReadOnlyList<AchievementDisplayItem>> PinRows =
                new Dictionary<string, IReadOnlyList<AchievementDisplayItem>>(StringComparer.Ordinal);
        }

        private static string WindowKey(TimelineRange range, DateTime endDate) =>
            ((int)range).ToString(CultureInfo.InvariantCulture) + "@" + endDate.Ticks.ToString(CultureInfo.InvariantCulture);

        public static ShowcaseWidgetProjection Build(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance,
            DateTime? now = null,
            GridOptionsCatalog gridOptions = null)
        {
            snapshot = snapshot ?? new OverviewDataSnapshot();
            settings = settings ?? new ShowcaseSettings();
            instance = instance ?? new ShowcaseWidgetInstanceSettings();

            var result = new ShowcaseWidgetProjection
            {
                Instance = instance,
                Snapshot = snapshot,
                Profile = settings.Profile ?? new ShowcaseProfileSettings()
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
                    result.Statistics = GetStatistics(snapshot, now ?? DateTime.Now);
                    result.ResolvedProfile = ShowcaseProfileResolver.Resolve(
                        settings.Profile,
                        snapshot.CurrentUserIdentities);
                    break;
                case ShowcaseWidgetKind.Statistics:
                    result.Statistics = GetStatistics(snapshot, now ?? DateTime.Now);
                    break;
                case ShowcaseWidgetKind.NativePoints:
                    result.ChartEntries = BuildNativePoints(snapshot, instance);
                    break;
                case ShowcaseWidgetKind.IconMosaic:
                    // The collapsed Mosaic: achievement icons or game covers per the Content
                    // option; each content mode keeps its own source semantics.
                    if (ShowcaseWidgetOptions.GetMosaicContent(instance) == ShowcaseMosaicContent.Games)
                    {
                        if (ShowcaseWidgetOptions.GetGameMosaicSource(instance) == ShowcaseGameMosaicSource.Pinned)
                        {
                            result.ResolvedPinCollectionId = ShowcasePinService.ResolveGameCollection(
                                settings,
                                ShowcaseWidgetOptions.GetPinCollectionId(instance))?.CollectionId;
                        }
                        result.Games = ResolveGameMosaic(snapshot, settings, instance);
                        break;
                    }

                    if (ShowcaseWidgetOptions.GetMosaicSource(instance) == ShowcaseMosaicSource.Pinned)
                    {
                        result.ResolvedPinCollectionId = ShowcasePinService.ResolveAchievementCollection(
                            settings,
                            ShowcaseWidgetOptions.GetPinCollectionId(instance))?.CollectionId;
                    }
                    result.MosaicAchievements = ResolveMosaic(snapshot, settings, instance);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    // The collapsed Achievements Grid: every achievement by default, or a pin
                    // collection's rows with reorder support.
                    if (ShowcaseWidgetOptions.GetAchievementGridSource(instance) ==
                        ShowcaseAchievementGridSource.Pinned)
                    {
                        var gridPins = ShowcasePinService.ResolveAchievementCollection(
                            settings,
                            ShowcaseWidgetOptions.GetPinCollectionId(instance));
                        result.ResolvedPinCollectionId = gridPins?.CollectionId;
                        result.Achievements = ResolvePinnedAchievements(snapshot, gridPins?.Pins);
                        result.AchievementRows = ResolvePinRowsCached(snapshot, result.Achievements);
                    }
                    else
                    {
                        result.AchievementRows = ResolveAllAchievements(snapshot);
                    }

                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    // The collapsed Game Summaries Grid: the library by default, or the pinned
                    // collection / Playnite favorites subsets.
                    switch (ShowcaseWidgetOptions.GetGameGridSource(instance))
                    {
                        case ShowcaseGameGridSource.Pinned:
                            var gridGames = ShowcasePinService.ResolveGameCollection(
                                settings,
                                ShowcaseWidgetOptions.GetPinCollectionId(instance));
                            result.ResolvedPinCollectionId = gridGames?.CollectionId;
                            result.Games = ResolvePinnedGameSummaries(
                                snapshot?.GameSummaries ?? new List<GameSummaryItem>(),
                                gridGames?.GameIds);
                            break;
                        case ShowcaseGameGridSource.PlayniteFavorites:
                            result.Games = ResolvePlayniteFavorites(snapshot?.GameSummaries);
                            break;
                        default:
                            result.Games = ResolveGameSummaries(snapshot, instance);
                            break;
                    }

                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    result.ActivityCalendar = GetActivityCalendar(snapshot, instance, (now ?? DateTime.Now).Date);
                    break;
                case ShowcaseWidgetKind.Scores:
                    result.ScoreHistory = GetScoreHistory(snapshot, instance, (now ?? DateTime.Now).Date);
                    break;
                case ShowcaseWidgetKind.Timeline:
                    var endDate = (now ?? DateTime.Now).Date;
                    var sourceCounts = NormalizeDailyCounts(snapshot);
                    var startDate = ResolveWindowStart(instance, endDate, sourceCounts);
                    result.Timeline = EnumerateDailyWindow(startDate, endDate, sourceCounts)
                        .ToDictionary(day => day.Date, day => day.Count);
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
            var key = WindowKey(ShowcaseTimelineOptions.GetRange(instance), endDate);
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
            DateTime endDate)
        {
            var cache = DerivedCache.GetOrCreateValue(snapshot);
            var key = WindowKey(ShowcaseTimelineOptions.GetRange(instance), endDate);
            if (!cache.ActivityCalendars.TryGetValue(key, out var calendar))
            {
                calendar = BuildActivityCalendar(snapshot, instance, endDate);
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

        /// <summary>Start of the instance's configured range, never past the end date.</summary>
        private static DateTime ResolveWindowStart(
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate,
            IReadOnlyDictionary<DateTime, int> counts)
        {
            var start = GetTimelineStartDate(ShowcaseTimelineOptions.GetRange(instance), endDate, counts);
            return start > endDate ? endDate : start;
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

        private static DateTime GetTimelineStartDate(
            TimelineRange range,
            DateTime endDate,
            IReadOnlyDictionary<DateTime, int> counts)
        {
            switch (range)
            {
                case TimelineRange.SevenDays:
                    return endDate.AddDays(-6);
                case TimelineRange.FourteenDays:
                    return endDate.AddDays(-13);
                case TimelineRange.OneMonth:
                    return endDate.AddMonths(-1).AddDays(1);
                case TimelineRange.ThreeMonths:
                    return endDate.AddMonths(-3).AddDays(1);
                case TimelineRange.OneYear:
                    return endDate.AddYears(-1).AddDays(1);
                case TimelineRange.All:
                    return counts != null && counts.Count > 0
                        ? counts.Keys.Min().Date
                        : endDate;
                default:
                    return endDate.AddMonths(-3).AddDays(1);
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
                Stat("completion", "LOCPlayAch_Showcase_Stat_Completion", snapshot.GlobalProgressionPercent),
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
                .Select(item => item.IsMissing
                    ? new AchievementDisplayItem
                    {
                        PlayniteGameId = item.Pin?.GameId,
                        ApiName = item.Pin?.ApiName,
                        DisplayName = item.Pin?.LastKnownAchievementName,
                        GameName = item.Pin?.LastKnownGameName
                    }
                    : item.Achievement)
                .ToList();
        }

        /// <summary>
        /// Every achievement in the snapshot, unlocked-recent-first with the locked tail last,
        /// so the grid's Default sort reads as newest unlocks. Cached per snapshot because the
        /// full library is sorted once, not per dashboard rebuild; the MaxRows cap applies in
        /// the widget view model after its search filter.
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
                    .Where(item => item != null)
                    .OrderByDescending(item => item.Unlocked)
                    .ThenByDescending(item => item.UnlockTimeUtc ?? DateTime.MinValue)
                    .ToList();
            }

            return cache.AllAchievementRows;
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

            // Per-widget scope: which games qualify by played/unplayed activity.
            var activityScope = ShowcaseWidgetOptions.GetGameActivityScope(instance);
            if (activityScope != GameActivityScope.All && activityScope != GameActivityScope.None)
            {
                games = OverviewGameSummaryFilters.ApplyActivityAndProgressFilters(
                    games,
                    activityScope,
                    GameProgressScope.None);
            }

            if (ShowcaseWidgetOptions.GetHideCompleted(instance))
            {
                games = games.Where(game => !game.IsCompleted);
            }

            // Sorting and the MaxRows cap apply in the widget view model (alongside its
            // control-bar filters), so editing those options re-orders one widget's rows
            // instead of re-projecting the whole dashboard.
            return games.ToList();
        }

        public static IReadOnlyList<GameSummaryItem> ResolveGameMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance)
        {
            var summaries = snapshot?.GameSummaries ?? new List<GameSummaryItem>();
            var count = ShowcaseWidgetOptions.GetGameMosaicCount(instance);
            IEnumerable<GameSummaryItem> games;
            switch (ShowcaseWidgetOptions.GetGameMosaicSource(instance))
            {
                case ShowcaseGameMosaicSource.All:
                    games = summaries
                        .Where(game => game != null)
                        .OrderByDescending(game => game.LastUnlockUtc ?? DateTime.MinValue);
                    break;
                case ShowcaseGameMosaicSource.Pinned:
                    games = ResolvePinnedGameSummaries(
                        summaries,
                        ShowcasePinService.ResolveGameCollection(
                            settings,
                            ShowcaseWidgetOptions.GetPinCollectionId(instance))?.GameIds);
                    break;
                case ShowcaseGameMosaicSource.PlayniteFavorites:
                    games = summaries
                        .Where(game => game?.IsFavorite == true)
                        .OrderBy(game => game.GameName, StringComparer.CurrentCultureIgnoreCase);
                    break;
                default:
                    games = summaries
                        .Where(game => game?.IsCompleted == true)
                        .OrderByDescending(game => game.LastUnlockUtc ?? DateTime.MinValue);
                    break;
            }

            return games.Take(count).ToList();
        }

        public static ShowcaseActivityCalendar BuildActivityCalendar(
            OverviewDataSnapshot snapshot,
            ShowcaseWidgetInstanceSettings instance,
            DateTime endDate)
        {
            endDate = endDate.Date;
            var counts = NormalizeDailyCounts(snapshot);
            var start = ResolveWindowStart(instance, endDate, counts);
            // Weeks render as Sunday-first columns, so the window starts on a Sunday.
            while (start.DayOfWeek != DayOfWeek.Sunday)
            {
                start = start.AddDays(-1);
            }

            var days = new List<ShowcaseActivityDay>();
            var max = 0;
            var total = 0;
            var activeDays = 0;
            foreach (var day in EnumerateDailyWindow(start, endDate, counts))
            {
                max = Math.Max(max, day.Count);
                total += day.Count;
                if (day.Count > 0)
                {
                    activeDays++;
                }

                days.Add(new ShowcaseActivityDay { Date = day.Date, Count = day.Count });
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
                StartDate = start,
                EndDate = endDate,
                MaxCount = max,
                TotalCount = total,
                ActiveDayCount = activeDays
            };
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
            public int BaselineCollection;
            public int BaselinePrestige;
            public bool SawUnlocked;
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
            // Single pass over the full achievement list - materializing the unlocked subset
            // would allocate a list the size of the user's whole unlock history.
            foreach (var item in snapshot.Achievements ?? new List<AchievementDisplayItem>())
            {
                if (item?.Unlocked != true)
                {
                    continue;
                }

                deltas.SawUnlocked = true;
                if (item.UnlockTimeUtc.HasValue)
                {
                    var day = item.UnlockTimeUtc.Value.Date;
                    deltas.Collection.TryGetValue(day, out var collection);
                    deltas.Collection[day] = collection + item.CollectionScore;
                    deltas.Prestige.TryGetValue(day, out var prestige);
                    deltas.Prestige[day] = prestige + item.PrestigeScore;
                }
                else
                {
                    deltas.BaselineCollection += item.CollectionScore;
                    deltas.BaselinePrestige += item.PrestigeScore;
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
            var start = ResolveWindowStart(instance, endDate, dailyCollection);

            // Unlocks before the window still count toward the running totals, so the first
            // point starts from the score already earned rather than from zero.
            var cumulativeCollection = deltas.BaselineCollection +
                dailyCollection.Where(pair => pair.Key < start).Sum(pair => pair.Value);
            var cumulativePrestige = deltas.BaselinePrestige +
                dailyPrestige.Where(pair => pair.Key < start).Sum(pair => pair.Value);

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

                if (offset % step == 0 || offset == rangeDays - 1)
                {
                    points.Add(new ShowcaseScorePoint
                    {
                        Date = day.Date,
                        CollectionScore = cumulativeCollection,
                        PrestigeScore = cumulativePrestige
                    });
                }

                offset++;
            }

            return points;
        }

        public static IReadOnlyList<AchievementDisplayItem> ResolveMosaic(
            OverviewDataSnapshot snapshot,
            ShowcaseSettings settings,
            ShowcaseWidgetInstanceSettings instance)
        {
            var source = ShowcaseWidgetOptions.GetMosaicSource(instance);
            var count = ShowcaseWidgetOptions.GetMosaicCount(instance);
            IEnumerable<AchievementDisplayItem> achievements;
            switch (source)
            {
                case ShowcaseMosaicSource.Rarest:
                    achievements = (snapshot?.Achievements ?? new List<AchievementDisplayItem>())
                        .Where(item => item?.Unlocked == true)
                        // Match every established rarity-sorted surface. The resolver keeps
                        // tier-only achievements in their correct band instead of pushing them
                        // behind all percentage-backed achievements.
                        .OrderBy(item => item.RaritySortValue)
                        .ThenByDescending(item => item.UnlockTimeUtc);
                    break;
                case ShowcaseMosaicSource.Capstones:
                    // Hydration folds a game's manual capstone override into IsCapstone, so the
                    // flag on the display item is already the effective capstone.
                    achievements = (snapshot?.Achievements ?? new List<AchievementDisplayItem>())
                        .Where(item => item?.Unlocked == true && item.IsCapstone)
                        .OrderByDescending(item => item.UnlockTimeUtc);
                    break;
                case ShowcaseMosaicSource.Pinned:
                    achievements = ResolvePinnedAchievements(
                            snapshot,
                            ShowcasePinService.ResolveAchievementCollection(
                                settings,
                                ShowcaseWidgetOptions.GetPinCollectionId(instance))?.Pins)
                        .Where(item => !item.IsMissing)
                        .Select(item => item.Achievement);
                    break;
                default:
                    achievements = (snapshot?.RecentAchievements ?? new List<AchievementDisplayItem>())
                        .Where(item => item != null)
                        .OrderByDescending(item => item.UnlockTimeUtc);
                    break;
            }

            return achievements.Take(count).ToList();
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
                LabelKey = "LOCPlayAch_Showcase_Other",
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
