using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Tests.TestSupport;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class PagedRowsTests
    {
        private sealed class Row
        {
            public int Index { get; set; }
            public int Version { get; set; }
            public bool IsPlaceholder { get; set; }
            public AchievementSort Sort { get; set; }
        }

        private readonly ManualReadScheduler _scheduler = new ManualReadScheduler();
        private readonly AchievementDataFeed _feed = new AchievementDataFeed(null, null);
        // Each page read as the query paged to the keys it was given.
        private readonly List<AchievementQuery> _pageReads = new List<AchievementQuery>();
        private readonly List<AchievementQuery> _keyReads = new List<AchievementQuery>();
        private readonly List<NotifyCollectionChangedEventArgs> _events = new List<NotifyCollectionChangedEventArgs>();
        private int _total;
        private int _version;

        private PagedRows<Row> Create(int total, int maxRows = 0, AchievementQuery query = null)
        {
            _total = total;
            var rows = new PagedRows<Row>(
                _feed,
                query ?? AchievementQuery.CurrentUser,
                ReadKeys,
                ReadPage,
                () => new Row { IsPlaceholder = true },
                _scheduler,
                maxRows);
            rows.CollectionChanged += (_, e) => _events.Add(e);
            return rows;
        }

        private IReadOnlyList<AchievementQuerySql.RowKey> ReadKeys(AchievementQuery query)
        {
            _keyReads.Add(query);
            return Enumerable.Range(0, query.Limit > 0 ? Math.Min(query.Limit, _total) : _total)
                .Select(index => new AchievementQuerySql.RowKey { DefinitionId = index })
                .ToList();
        }

        private IReadOnlyList<Row> ReadPage(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            _pageReads.Add(query.Page(keys.Count, keys.Count == 0 ? 0 : (int)keys[0].DefinitionId));
            return keys.Where(key => key.DefinitionId < _total)
                .Select(key => new Row { Index = (int)key.DefinitionId, Version = _version, Sort = query.Sort })
                .ToList();
        }

        private static Row At(PagedRows<Row> rows, int index) => (Row)rows[index];

        private IEnumerable<int> ReadOffsets => _pageReads.Select(query => query.Offset);

        private int Count(NotifyCollectionChangedAction action) => _events.Count(e => e.Action == action);

        [TestMethod]
        public void Open_SetsTheFullCountAndTheFirstPagesInOneApply()
        {
            var rows = Create(2000);
            Assert.AreEqual(0, rows.Count, "nothing shows before the first read");

            _scheduler.Drain();

            Assert.AreEqual(2000, rows.Count);
            Assert.AreEqual(1, _events.Count);
            Assert.AreEqual(NotifyCollectionChangedAction.Reset, _events[0].Action);
            Assert.AreEqual(0, _pageReads.Single().Offset, "the first two pages in one read");
            Assert.AreEqual(2 * PagedRows<Row>.PageSize, _pageReads.Single().Limit);
            Assert.AreEqual(150, At(rows, 150).Index);
            Assert.IsFalse(At(rows, 199).IsPlaceholder);
            Assert.AreEqual(1, _keyReads.Count, "the keys are read once");
        }

        [TestMethod]
        public void PageReads_SliceTheKeptKeys_WithoutReadingKeysAgain()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _keyReads.Clear();

            At(rows, 1550);
            _scheduler.Drain();

            Assert.AreEqual(1550, At(rows, 1550).Index);
            Assert.AreEqual(0, _keyReads.Count, "a page reads the columns of its keys only");

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();
            Assert.AreEqual(1, _keyReads.Count, "a touching change reads the keys again");
        }

        [TestMethod]
        public void Open_WithFewRows_AddsThem()
        {
            var rows = Create(30);
            _scheduler.Drain();

            Assert.AreEqual(30, rows.Count);
            Assert.AreEqual(30, Count(NotifyCollectionChangedAction.Add));
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));
            Assert.AreEqual(29, At(rows, 29).Index);
            Assert.AreEqual(30, _pageReads.Single().Limit);
        }

        [TestMethod]
        public void UnloadedRow_IsAPlaceholder_ReplacedInPlaceWithItsNeighbours()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _events.Clear();
            _pageReads.Clear();

            var placeholder = At(rows, 550);
            Assert.IsTrue(placeholder.IsPlaceholder);
            Assert.AreSame(placeholder, At(rows, 550), "the same placeholder until the row arrives");
            Assert.AreEqual(550, rows.IndexOf(placeholder));

            _scheduler.Drain();

            CollectionAssert.AreEquivalent(new[] { 400, 500, 600 }, ReadOffsets.ToList());
            var replace = _events.Single();
            Assert.AreEqual(NotifyCollectionChangedAction.Replace, replace.Action);
            Assert.AreEqual(550, replace.NewStartingIndex);
            Assert.AreSame(placeholder, replace.OldItems[0]);
            Assert.AreEqual(550, ((Row)replace.NewItems[0]).Index);
            Assert.AreEqual(550, At(rows, 550).Index);
            Assert.AreEqual(2000, rows.Count);
        }

        [TestMethod]
        public void FastDrag_LoadsOnlyWhereItStops()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _pageReads.Clear();

            At(rows, 300);
            At(rows, 600);
            At(rows, 950);
            _scheduler.Drain();

            CollectionAssert.AreEqual(new[] { 300, 900 }, ReadOffsets.Take(2).ToList(),
                "the page read when the drag started finishes; then the page where it stopped");
            CollectionAssert.AreEquivalent(new[] { 800, 1000 }, ReadOffsets.Skip(2).ToList(), "then its neighbours, and nothing between");
            Assert.AreEqual(950, At(rows, 950).Index);
        }

        [TestMethod]
        public void RowCap_CapsTheCountAndTheReads()
        {
            var rows = Create(2000, maxRows: 1250);
            _scheduler.Drain();
            Assert.AreEqual(1250, rows.Count);

            At(rows, 1249);
            _scheduler.Drain();

            Assert.AreEqual(1249, At(rows, 1249).Index);
            Assert.AreEqual(1250, _keyReads.Last().Limit, "the keys read stops at the cap");
            Assert.IsTrue(_pageReads.All(query => query.Offset + query.Limit <= 1250));
            Assert.AreEqual(50, _pageReads.Single(query => query.Offset == 1200).Limit);

            _pageReads.Clear();
            var small = Create(2000, maxRows: 250);
            _scheduler.Drain();
            Assert.AreEqual(250, small.Count);
            Assert.AreEqual(250, _pageReads.Single().Limit, "a cap within the whole-load size reads the capped rows once");
        }

        [TestMethod]
        public void FeedChange_SameCount_ReplacesTheRowsAroundTheLastReadInPlace()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            At(rows, 550);
            _scheduler.Drain();
            var top = At(rows, 0);
            At(rows, 550);
            _events.Clear();
            _pageReads.Clear();

            _version = 1;
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();

            Assert.AreEqual(2000, rows.Count);
            Assert.AreEqual(400, _pageReads.Single().Offset, "the pages around the last read index in one read");
            Assert.AreEqual(300, _pageReads.Single().Limit);
            Assert.AreEqual(300, _events.Count);
            Assert.IsTrue(_events.All(e => e.Action == NotifyCollectionChangedAction.Replace));
            Assert.AreEqual(1, ((Row)rows[551]).Version);

            Assert.AreSame(top, At(rows, 0), "a page away from the scroll keeps its rows until read again");
            _scheduler.Drain();
            Assert.AreEqual(1, At(rows, 0).Version, "reading it reloads it");
        }

        [TestMethod]
        public void FeedChange_SmallCountChange_AddsOrRemovesAtTheEnd()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _events.Clear();

            _total = 2005;
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();

            Assert.AreEqual(2005, rows.Count);
            Assert.AreEqual(5, Count(NotifyCollectionChangedAction.Add));
            Assert.AreEqual(2000, _events.First(e => e.Action == NotifyCollectionChangedAction.Add).NewStartingIndex);
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));

            _events.Clear();
            _total = 1990;
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();

            Assert.AreEqual(1990, rows.Count);
            Assert.AreEqual(15, Count(NotifyCollectionChangedAction.Remove));
            Assert.AreEqual(2004, _events.First().OldStartingIndex, "removed from the end, last first");
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));
        }

        [TestMethod]
        public void FeedChange_LargeCountChange_ResetsTheList()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _events.Clear();

            _total = 5000;
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();

            Assert.AreEqual(5000, rows.Count);
            Assert.AreEqual(NotifyCollectionChangedAction.Reset, _events.Single().Action);
        }

        [TestMethod]
        public void SortBy_ReadsInTheColumnsOrder_AndDropsPagesOfTheOldOrder()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            At(rows, 550);
            _pageReads.Clear();

            rows.SortBy(AchievementSort.UnlockTime, descending: true);
            _scheduler.Drain();

            Assert.AreEqual(AchievementSort.UnlockTime, _pageReads.Last().Sort);
            Assert.IsTrue(_pageReads.Last().Descending);
            Assert.AreEqual(AchievementSort.UnlockTime, At(rows, 550).Sort,
                "the page read in the old order is dropped for the one read in the new order");
            Assert.AreEqual(AchievementSort.Display, rows.Query.Sort, "the query as given keeps its own order");

            _pageReads.Clear();
            rows.SortBy(null, false);
            _scheduler.Drain();
            Assert.IsTrue(_pageReads.All(query => query.Sort == AchievementSort.Display && !query.Descending));
        }

        [TestMethod]
        public void SetQuery_KeepsTheColumnSort()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            rows.SortBy(AchievementSort.GlobalPercent, descending: false);
            _scheduler.Drain();
            _pageReads.Clear();

            rows.SetQuery(AchievementQuery.CurrentUser.WhereUnlocked(true));
            _scheduler.Drain();

            Assert.IsTrue(_pageReads.All(query => query.Unlocked == true && query.Sort == AchievementSort.GlobalPercent));
        }

        [TestMethod]
        public void LoadedPages_AreBounded()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            for (var index = 0; index < 2000; index += 100)
            {
                At(rows, index);
                _scheduler.Drain();
            }

            Assert.IsTrue(_events.Any(e => e.Action == NotifyCollectionChangedAction.Replace &&
                                           ((Row)e.NewItems[0]).IsPlaceholder &&
                                           e.NewStartingIndex < 1000),
                "a page far from the scroll goes back to placeholders");
            Assert.IsTrue(At(rows, 0).IsPlaceholder);
        }

        [TestMethod]
        public void Open_WithUpToAThousandRows_LoadsThemWholeInOneRead()
        {
            var rows = Create(PagedRows<Row>.WholeLoadRows);
            _scheduler.Drain();

            var read = _pageReads.Single();
            Assert.AreEqual(0, read.Offset);
            Assert.AreEqual(PagedRows<Row>.WholeLoadRows, read.Limit);
            Assert.AreEqual(PagedRows<Row>.WholeLoadRows, rows.LoadedItems.Cast<Row>().Count(), "every row is loaded");
            Assert.IsFalse(At(rows, 999).IsPlaceholder);
            Assert.AreEqual(0, _scheduler.PendingReads, "reading any row loads nothing more");
        }

        [TestMethod]
        public void Enumeration_HandsOutLoadedRowsAndOneSharedPlaceholder_WithoutLoadingOrKeepingAny()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            _pageReads.Clear();

            var all = ((IEnumerable<Row>)rows).ToList();

            Assert.AreEqual(5000, all.Count, "as many items as the count");
            Assert.AreEqual(200, all.Count(row => !row.IsPlaceholder), "the loaded rows");
            Assert.AreEqual(1, all.Where(row => row.IsPlaceholder).Distinct().Count(), "one placeholder for every unloaded index");
            Assert.AreEqual(-1, rows.IndexOf(all[4000]), "the shared placeholder stands for no index");
            Assert.AreEqual(0, _scheduler.PendingReads, "enumerating loads nothing");
            Assert.AreEqual(0, _pageReads.Count);
            CollectionAssert.AreEqual(
                Enumerable.Range(0, 200).ToList(),
                rows.LoadedItems.Cast<Row>().Select(row => row.Index).ToList(),
                "LoadedItems lists the loaded rows in index order");

            var placeholder = At(rows, 4000);
            Assert.AreNotSame(all[4000], placeholder, "reading an index hands out its own placeholder");
            Assert.AreEqual(4000, rows.IndexOf(placeholder));
        }

        [TestMethod]
        public void SetQuery_FromStart_LoadsTheTopFirst()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            At(rows, 3000);
            _scheduler.Drain();
            _pageReads.Clear();

            rows.SetQuery(AchievementQuery.CurrentUser.WhereUnlocked(true));
            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { 2900 }, ReadOffsets.ToList(), "a filter keeps the place");

            _pageReads.Clear();
            rows.SetQuery(AchievementQuery.CurrentUser.WhereUnlocked(false), fromStart: true);
            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { 0 }, ReadOffsets.ToList(), "a new selection starts at the top");
        }

        [TestMethod]
        public void Dispose_DropsReadsInFlightAndStopsReloading()
        {
            var rows = Create(2000);
            rows.Dispose();
            _scheduler.Drain();
            Assert.AreEqual(0, rows.Count);
            Assert.AreEqual(0, _events.Count);

            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            Assert.AreEqual(0, _scheduler.PendingTimers);
        }
    }
}
