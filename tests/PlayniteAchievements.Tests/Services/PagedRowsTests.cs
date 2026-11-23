using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
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
            public long Key { get; set; }
            public int Version { get; set; }
            public bool IsPlaceholder { get; set; }
            public AchievementSort Sort { get; set; }
            public int Updates { get; set; }
        }

        private readonly ManualReadScheduler _scheduler = new ManualReadScheduler();
        private readonly AchievementDataFeed _feed = new AchievementDataFeed(null, null);
        // Each page read as the query paged to the keys it was given.
        private readonly List<AchievementQuery> _pageReads = new List<AchievementQuery>();
        private readonly List<AchievementQuery> _keyReads = new List<AchievementQuery>();
        private readonly List<NotifyCollectionChangedEventArgs> _events = new List<NotifyCollectionChangedEventArgs>();
        private readonly List<string> _properties = new List<string>();
        // The key each index has; the index itself unless a test moves one.
        private readonly Dictionary<int, long> _movedKeys = new Dictionary<int, long>();
        private int _total;
        private int _version;

        private PagedRows<Row> Create(int total, int maxRows = 0, AchievementQuery query = null, bool update = false, int pendingRows = 0, List<Row> placed = null)
        {
            _total = total;
            var rows = new PagedRows<Row>(
                _feed,
                query ?? AchievementQuery.CurrentUser,
                ReadKeys,
                ReadPage,
                () => new Row { IsPlaceholder = true },
                _scheduler,
                maxRows,
                update: update
                    ? (kept, fresh) =>
                    {
                        kept.Index = fresh.Index;
                        kept.Key = fresh.Key;
                        kept.IsPlaceholder = fresh.IsPlaceholder;
                        kept.Version = fresh.Version;
                        kept.Sort = fresh.Sort;
                        kept.Updates++;
                    }
                    : (Action<Row, Row>)null,
                pendingRows: pendingRows,
                placed: placed == null ? (Action<Row>)null : placed.Add);
            rows.CollectionChanged += (_, e) => _events.Add(e);
            rows.PropertyChanged += (_, e) => _properties.Add(e.PropertyName);
            return rows;
        }

        private IReadOnlyList<AchievementQuerySql.RowKey> ReadKeys(AchievementQuery query)
        {
            _keyReads.Add(query);
            return Enumerable.Range(0, query.Limit > 0 ? Math.Min(query.Limit, _total) : _total)
                .Select(index => new AchievementQuerySql.RowKey { DefinitionId = _movedKeys.TryGetValue(index, out var key) ? key : index })
                .ToList();
        }

        // Reads the rows of the keys; a row's index is its key, or where a moved key was put.
        private IReadOnlyList<Row> ReadPage(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            _pageReads.Add(query.Page(keys.Count, keys.Count == 0 ? 0 : IndexOf(keys[0].DefinitionId.Value)));
            return keys.Where(key => IndexOf(key.DefinitionId.Value) < _total)
                .Select(key => new Row { Index = IndexOf(key.DefinitionId.Value), Key = key.DefinitionId.Value, Version = _version, Sort = query.Sort })
                .ToList();
        }

        private int IndexOf(long key) => _movedKeys.Where(pair => pair.Value == key).Select(pair => (int?)pair.Key).FirstOrDefault() ?? (int)key;

        private static Row At(PagedRows<Row> rows, int index) => (Row)rows[index];

        private IEnumerable<int> ReadOffsets => _pageReads.Select(query => query.Offset);

        private int Count(NotifyCollectionChangedAction action) => _events.Count(e => e.Action == action);

        private void Publish()
        {
            _feed.Publish(AchievementDataChange.Full);
            _scheduler.Drain();
            _scheduler.Elapse();
            _scheduler.Drain();
        }

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
        public void PendingRows_ArePlaceholdersFilledInPlaceByTheFirstRead()
        {
            var rows = Create(5, update: true, pendingRows: 12);
            Assert.AreEqual(12, rows.Count);
            Assert.IsTrue(rows.IsPending);
            var first = At(rows, 0);
            Assert.IsTrue(first.IsPlaceholder);
            Assert.AreEqual(0, _pageReads.Count, "a pending row reads nothing");

            _scheduler.Drain();

            Assert.IsFalse(rows.IsPending);
            Assert.AreEqual(5, rows.Count);
            Assert.AreSame(first, At(rows, 0), "the placeholder shown becomes the row");
            Assert.IsFalse(first.IsPlaceholder);
            Assert.AreEqual(7, Count(NotifyCollectionChangedAction.Remove), "the extra placeholders go");
        }

        [TestMethod]
        public void Placed_ReportsRowsPutInPlaceAsThemselves_NotThoseCopiedIntoAHandedOutRow()
        {
            var reset = new List<Row>();
            var large = Create(2000, placed: reset);
            _scheduler.Drain();
            CollectionAssert.AreEqual(Enumerable.Range(0, 200).ToList(), reset.Select(row => row.Index).ToList(), "the first pages of a reset");
            Assert.IsTrue(reset.All(row => ReferenceEquals(row, At(large, row.Index))));

            var placed = new List<Row>();
            var rows = Create(5, update: true, pendingRows: 12, placed: placed);
            var first = At(rows, 0);
            _scheduler.Drain();

            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4 }, placed.Select(row => row.Index).ToList(), "index 0 was copied into its placeholder");
            Assert.IsFalse(placed.Contains(first));
        }

        [TestMethod]
        public void PendingRows_GoWhenTheFirstReadFinds_None()
        {
            var rows = Create(0, pendingRows: 12);
            _scheduler.Drain();

            Assert.AreEqual(0, rows.Count);
            Assert.IsFalse(rows.IsPending);
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

            Publish();
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
        public void UnloadedRow_WithAnUpdate_IsFilledInPlace_WithoutAReplace()
        {
            var rows = Create(2000, update: true);
            _scheduler.Drain();
            _events.Clear();
            _properties.Clear();

            var placeholder = At(rows, 550);
            var copy = ((IEnumerable<Row>)rows).ToList();
            _scheduler.Drain();

            Assert.AreSame(placeholder, At(rows, 550), "the object handed out is the row");
            Assert.IsFalse(placeholder.IsPlaceholder);
            Assert.AreEqual(550, placeholder.Index);
            Assert.AreEqual(1, placeholder.Updates);
            Assert.AreSame(copy[1999], At(rows, 1999), "a placeholder enumeration handed out is filled too");
            Assert.AreEqual(1999, copy[1999].Index);
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Replace), "nothing is replaced");
            Assert.IsTrue(_properties.Contains("Item[]"), "the fill is announced as an indexer change");
            Assert.AreEqual(550, rows.IndexOf(placeholder));
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().Contains(placeholder));

            _version = 1;
            Publish();
            Assert.AreSame(placeholder, At(rows, 550), "a rerun keeps the filled row");
            Assert.AreEqual(1, placeholder.Version);
        }

        [TestMethod]
        public void FastDrag_LoadsOnlyWhereItStops()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _pageReads.Clear();

            // A virtualizing grid realizes a screenful at each place the thumb passes.
            foreach (var stop in new[] { 300, 600, 950 })
            {
                for (var index = stop; index < stop + 30; index++)
                {
                    At(rows, index);
                }
            }

            _scheduler.Drain();

            CollectionAssert.AreEqual(new[] { 300, 900 }, ReadOffsets.Take(2).ToList(),
                "the page read when the drag started finishes; then the page where it stopped");
            CollectionAssert.AreEquivalent(new[] { 800, 1000 }, ReadOffsets.Skip(2).ToList(), "then its neighbours, and nothing between");
            Assert.AreEqual(950, At(rows, 950).Index);
            Assert.IsTrue(At(rows, 600).IsPlaceholder, "a page passed over is not loaded");
        }

        [TestMethod]
        public void RealizedRows_PlaceTheList_AndAGridReadingEveryIndexKeepsNothing()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            _pageReads.Clear();

            rows.Realized(2500);
            // A virtualizing grid whose rows differ in height reads every index on each layout pass.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var index = 0; index < 5000; index++)
                {
                    At(rows, index);
                }
            }

            _scheduler.Drain();

            CollectionAssert.AreEquivalent(new[] { 2400, 2500, 2600 }, ReadOffsets.ToList(), "only the pages around the realized row");
            Assert.AreEqual(2500, At(rows, 2500).Index);
            Assert.IsTrue(At(rows, 4000).IsPlaceholder, "a page the grid only read is not loaded");
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().All(row => row.Index < 200 || (row.Index >= 2400 && row.Index < 2700)));
        }

        [TestMethod]
        public void RealizedRows_ALandedPageFillsOnePlaceholderPerIdleTurn_TopDown()
        {
            var rows = Create(5000, update: true);
            _scheduler.Drain();
            rows.Realized(0);

            // The grid reads each row, then reports it realized.
            var shown = new List<Row>();
            for (var index = 2500; index < 2510; index++)
            {
                shown.Add(At(rows, index));
                rows.Realized(index);
            }

            _scheduler.RunReads();
            _scheduler.RunUi();

            Assert.IsTrue(shown.All(row => row.IsPlaceholder), "the landed page fills nothing in the same turn");
            Assert.IsFalse(rows.LoadedItems.Cast<Row>().Any(row => shown.Contains(row)), "a waiting placeholder is not a loaded row");
            Assert.AreEqual(1, _scheduler.PendingIdle);

            _scheduler.RunIdle();

            Assert.IsFalse(shown[0].IsPlaceholder, "the top row of the realized run fills first");
            Assert.AreEqual(2500, shown[0].Index);
            Assert.IsTrue(shown.Skip(1).All(row => row.IsPlaceholder), "one row per idle turn");

            _scheduler.RunIdle();

            Assert.IsFalse(shown[1].IsPlaceholder, "then the row below it");
            Assert.IsTrue(shown.Skip(2).All(row => row.IsPlaceholder));

            _scheduler.Drain();

            for (var offset = 0; offset < shown.Count; offset++)
            {
                Assert.AreSame(shown[offset], At(rows, 2500 + offset), "the placeholder handed out becomes the row");
                Assert.AreEqual(2500 + offset, shown[offset].Index);
                Assert.AreEqual(1, shown[offset].Updates);
            }
        }

        [TestMethod]
        public void RealizedRows_AWaitingFillOfAPageDroppedSinceIsNotApplied()
        {
            var rows = Create(5000, update: true);
            _scheduler.Drain();
            rows.Realized(0);
            var left = At(rows, 2500);
            rows.Realized(2500);
            _scheduler.RunReads();
            _scheduler.RunUi();

            // The thumb moves on before an idle turn comes; the pages near where it stops push
            // the one it left out of those kept.
            for (var stop = 3000; stop < 5000; stop += 100)
            {
                At(rows, stop);
                rows.Realized(stop);
                while (_scheduler.PendingReads > 0)
                {
                    _scheduler.RunReads();
                    _scheduler.RunUi();
                }
            }

            _scheduler.Drain();

            Assert.IsTrue(left.IsPlaceholder, "a row the grid has left is not filled once its page is dropped");
            Assert.AreEqual(0, left.Updates);
        }

        [TestMethod]
        public void VirtualizedScroll_KeepsAboutTenPages_AndDropsFarOnesWithoutEvents()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            for (var top = 0; top < 2000; top += 40)
            {
                // A screenful of rows each step, as a virtualizing grid realizes them.
                for (var index = top; index < top + 40; index++)
                {
                    At(rows, index);
                }

                _scheduler.Drain();
            }

            Assert.IsFalse(_events.Any(e => e.Action == NotifyCollectionChangedAction.Replace && ((Row)e.NewItems[0]).IsPlaceholder),
                "a dropped page raises nothing");
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().Count() <= (PagedRows<Row>.MaxLoadedPages + 1) * PagedRows<Row>.PageSize);
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().All(row => row.Index >= 900), "the pages kept are those near the scroll");
            Assert.IsTrue(At(rows, 0).IsPlaceholder, "a far page reads as a placeholder again");
            _scheduler.Drain();
            Assert.AreEqual(0, At(rows, 0).Index, "and loads when read");
        }

        [TestMethod]
        public void NonVirtualizingRealization_LoadsEveryRow_FromTheTop_ReplacingWhatItWasHanded()
        {
            var rows = Create(3000);
            _scheduler.Drain();
            _events.Clear();
            _pageReads.Clear();

            // A list that does not virtualize reads every index in one pass.
            var handed = Enumerable.Range(0, 3000).Select(index => At(rows, index)).ToList();
            _scheduler.Drain();

            Assert.AreEqual(200, ReadOffsets.First(), "the page wanted when the pass started");
            CollectionAssert.AreEqual(
                Enumerable.Range(3, 27).Select(page => page * 100).ToList(),
                ReadOffsets.Skip(1).ToList(),
                "then every other page, from the top");
            var replaced = _events.Where(e => e.Action == NotifyCollectionChangedAction.Replace).ToList();
            Assert.AreEqual(2800, replaced.Count, "each placeholder handed out is replaced once");
            Assert.IsTrue(replaced.All(e => ReferenceEquals(handed[e.NewStartingIndex], e.OldItems[0])), "naming the object handed out");
            CollectionAssert.AreEqual(Enumerable.Range(0, 3000).ToList(), rows.LoadedItems.Cast<Row>().Select(row => row.Index).ToList(),
                "no page is dropped");
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));

            // A rerun reads every row it holds again.
            _pageReads.Clear();
            Publish();
            Assert.AreEqual(3000, _pageReads.Sum(read => read.Limit));
        }

        [TestMethod]
        public void Enumeration_HandsOutAPlaceholderPerIndex_ThenReplacesEachWhenItsPageLoads()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            _events.Clear();

            var copy = ((IEnumerable<Row>)rows).ToList();

            Assert.AreEqual(5000, copy.Count, "as many items as the count");
            Assert.AreEqual(4800, copy.Count(row => row.IsPlaceholder));
            Assert.AreEqual(4800, copy.Where(row => row.IsPlaceholder).Distinct().Count(), "a placeholder per index");
            Assert.AreEqual(4000, rows.IndexOf(copy[4000]));
            Assert.AreSame(copy[4000], At(rows, 4000), "the indexer hands out the same placeholder");

            _scheduler.Drain();

            var replaced = _events.Where(e => e.Action == NotifyCollectionChangedAction.Replace).ToList();
            Assert.AreEqual(4800, replaced.Count);
            Assert.IsTrue(replaced.All(e => ReferenceEquals(copy[e.NewStartingIndex], e.OldItems[0])),
                "a copy of the list finds each placeholder it holds");
            Assert.AreEqual(5000, rows.LoadedItems.Cast<Row>().Count(), "a copied list keeps every row");
        }

        [TestMethod]
        public void CountAndIndexerBindings_AreNotified_AndARereadIndexLoads()
        {
            var rows = Create(5000, update: true);
            _scheduler.Drain();
            CollectionAssert.AreEqual(new[] { "Count", "Item[]" }, _properties);

            // A theme binding to an index reads it again on each change.
            Row bound = null;
            rows.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Item[]")
                {
                    bound = At(rows, 4000);
                }
            };
            At(rows, 1000);
            _scheduler.Drain();
            At(rows, 2000);
            _scheduler.Drain();

            Assert.AreEqual(4000, bound.Index, "the bound index loads though nothing scrolled there");
            Assert.AreEqual(4000, rows.LoadedItems.Cast<Row>().First(row => row.Index >= 3000).Index, "and stays loaded");

            _properties.Clear();
            _total = 5050;
            Publish();
            CollectionAssert.AreEqual(new[] { "Count", "Item[]" }, _properties);
            Assert.AreEqual(5050, rows.Count);

            _properties.Clear();
            Publish();
            CollectionAssert.AreEqual(new string[0], _properties, "a rerun that changes nothing raises nothing");
        }

        [TestMethod]
        public void Rerun_KeepsEachRowWhoseKeyIsUnchanged_UpdatedInPlace_AndReplacesOnlyRowsWhoseKeyMoved()
        {
            var rows = Create(2000, update: true);
            _scheduler.Drain();
            At(rows, 550);
            _scheduler.Drain();
            var top = At(rows, 0);
            var shown = At(rows, 550);
            var moved = At(rows, 560);
            _events.Clear();

            _version = 1;
            _movedKeys[560] = 9999;
            Publish();

            Assert.AreSame(top, At(rows, 0), "a page away from the scroll keeps its objects too");
            Assert.AreSame(shown, At(rows, 550));
            Assert.AreEqual(1, shown.Version, "brought up to date in place");
            Assert.AreEqual(1, top.Version);
            var replace = _events.Single();
            Assert.AreEqual(NotifyCollectionChangedAction.Replace, replace.Action, "only the row whose key changed is replaced");
            Assert.AreEqual(560, replace.NewStartingIndex);
            Assert.AreSame(moved, replace.OldItems[0]);
            Assert.AreEqual(9999, ((Row)rows[560]).Key);
        }

        [TestMethod]
        public void Rerun_WithoutAnUpdate_ReplacesEveryLoadedRow_AndReadsEveryLoadedPage()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            At(rows, 550);
            _scheduler.Drain();
            _events.Clear();
            _pageReads.Clear();

            _version = 1;
            Publish();

            CollectionAssert.AreEquivalent(new[] { 0, 400 }, ReadOffsets.ToList(), "the loaded pages, a read per run of pages");
            Assert.AreEqual(500, _pageReads.Sum(read => read.Limit));
            Assert.AreEqual(500, Count(NotifyCollectionChangedAction.Replace));
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().All(row => row.Version == 1));
        }

        [TestMethod]
        public void QueryChange_WithASmallCountChange_RefreshesEveryLoadedPage()
        {
            var rows = Create(5000, update: true);
            _scheduler.Drain();
            At(rows, 3000);
            _scheduler.Drain();
            var shown = At(rows, 3000);

            _total = 5020;
            rows.SetQuery(AchievementQuery.CurrentUser.OrderBy(AchievementSort.Name, descending: false), fromStart: true);
            _scheduler.Drain();

            Assert.AreEqual(5020, rows.Count);
            Assert.AreEqual(AchievementSort.Name, shown.Sort, "a row the grid shows is read under the new query");
            Assert.IsTrue(rows.LoadedItems.Cast<Row>().All(row => row.Sort == AchievementSort.Name), "no loaded row is from the old query");
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
        public void FeedChange_SmallCountChange_AddsOrRemovesAtTheEnd_NamingWhatWasHandedOut()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _events.Clear();

            _total = 2005;
            Publish();

            Assert.AreEqual(2005, rows.Count);
            Assert.AreEqual(5, Count(NotifyCollectionChangedAction.Add));
            Assert.AreEqual(2000, _events.First(e => e.Action == NotifyCollectionChangedAction.Add).NewStartingIndex);
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));

            var last = At(rows, 2004);
            _events.Clear();
            _total = 1990;
            Publish();

            Assert.AreEqual(1990, rows.Count);
            Assert.AreEqual(15, Count(NotifyCollectionChangedAction.Remove));
            Assert.AreEqual(2004, _events.First().OldStartingIndex, "removed from the end, last first");
            Assert.AreSame(last, _events.First().OldItems[0], "naming the row handed out");
            Assert.AreEqual(0, Count(NotifyCollectionChangedAction.Reset));
        }

        [TestMethod]
        public void FeedChange_LargeCountChange_ResetsTheList()
        {
            var rows = Create(2000);
            _scheduler.Drain();
            _events.Clear();

            _total = 5000;
            Publish();

            Assert.AreEqual(5000, rows.Count);
            Assert.AreEqual(NotifyCollectionChangedAction.Reset, _events.Single().Action);
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
        public void SetQuery_FromStart_LoadsTheTopFirst()
        {
            var rows = Create(5000);
            _scheduler.Drain();
            At(rows, 3000);
            _scheduler.Drain();
            _pageReads.Clear();

            rows.SetQuery(AchievementQuery.CurrentUser.WhereUnlocked(true));
            _scheduler.Drain();
            Assert.AreEqual(2900, ReadOffsets.Max(), "a filter keeps the place");

            _pageReads.Clear();
            rows.SetQuery(AchievementQuery.CurrentUser.WhereUnlocked(false), fromStart: true);
            _scheduler.Drain();
            Assert.AreEqual(0, ReadOffsets.First(), "a new selection starts at the top");
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
