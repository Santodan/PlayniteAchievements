using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class CollectionHelperReplaceTests
    {
        private sealed class Row
        {
            public Row(int id)
            {
                Id = id;
            }

            public int Id { get; }
        }

        private static List<Row> Rows(int count, int firstId = 0)
        {
            return Enumerable.Range(firstId, count).Select(id => new Row(id)).ToList();
        }

        private static List<NotifyCollectionChangedAction> Record<T>(ObservableCollection<T> collection)
        {
            var actions = new List<NotifyCollectionChangedAction>();
            collection.CollectionChanged += (_, e) => actions.Add(e.Action);
            return actions;
        }

        private static int Count(List<NotifyCollectionChangedAction> actions, NotifyCollectionChangedAction action)
        {
            return actions.Count(recorded => recorded == action);
        }

        [TestMethod]
        public void OneReplacedRow_AmongKeptInstances_IsPatchedInPlace()
        {
            var rows = Rows(500);
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(rows);
            var source = rows.ToList();
            source[250] = new Row(9999);
            var actions = Record(target);

            CollectionHelper.Replace(target, source);

            Assert.AreEqual(0, Count(actions, NotifyCollectionChangedAction.Reset));
            Assert.AreEqual(1, Count(actions, NotifyCollectionChangedAction.Remove));
            Assert.AreEqual(1, Count(actions, NotifyCollectionChangedAction.Add));
            CollectionAssert.AreEqual(source, target.ToList());
        }

        [TestMethod]
        public void OneRowMovedToTheFront_RaisesOneMove()
        {
            var rows = Rows(500);
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(rows);
            var source = rows.ToList();
            var moved = source[400];
            source.RemoveAt(400);
            source.Insert(0, moved);
            var actions = Record(target);

            CollectionHelper.Replace(target, source);

            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Move }, actions);
            CollectionAssert.AreEqual(source, target.ToList());
        }

        [TestMethod]
        public void AllNewInstances_ResetOnce()
        {
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(Rows(500));
            var source = Rows(500, firstId: 1000);
            var actions = Record(target);

            CollectionHelper.Replace(target, source);

            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, actions);
            CollectionAssert.AreEqual(source, target.ToList());
        }

        [TestMethod]
        public void FeedToSubset_ResetsOnce()
        {
            var rows = Rows(2000);
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(rows);
            var subset = rows.Where(row => row.Id % 10 == 0).ToList();
            var actions = Record(target);

            CollectionHelper.Replace(target, subset);

            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, actions);
            CollectionAssert.AreEqual(subset, target.ToList());
        }

        [TestMethod]
        public void EmptyToFilled_ResetsOnce()
        {
            var target = new BulkObservableCollection<Row>();
            var source = Rows(500);
            var actions = Record(target);

            CollectionHelper.Replace(target, source);

            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, actions);
            CollectionAssert.AreEqual(source, target.ToList());
        }

        [TestMethod]
        public void FilledToEmpty_ResetsOnce()
        {
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(Rows(500));
            var actions = Record(target);

            CollectionHelper.Replace(target, new List<Row>());

            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Reset }, actions);
            Assert.AreEqual(0, target.Count);
        }

        [TestMethod]
        public void EmptyToEmpty_RaisesNothing()
        {
            var target = new BulkObservableCollection<Row>();
            var actions = Record(target);

            CollectionHelper.Replace(target, new List<Row>());

            Assert.AreEqual(0, actions.Count);
        }

        [TestMethod]
        public void FullReversal_StopsMovingAndResets()
        {
            var rows = Rows(2000);
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(rows);
            var reversed = rows.AsEnumerable().Reverse().ToList();
            var actions = Record(target);

            CollectionHelper.Replace(target, reversed);

            Assert.AreEqual(NotifyCollectionChangedAction.Reset, actions.Last());
            Assert.AreEqual(1, Count(actions, NotifyCollectionChangedAction.Reset));
            Assert.IsTrue(Count(actions, NotifyCollectionChangedAction.Move) <= 128,
                $"moves before the reset: {Count(actions, NotifyCollectionChangedAction.Move)}");
            CollectionAssert.AreEqual(reversed, target.ToList());
        }

        [TestMethod]
        public void PlainObservableCollection_IsAlwaysSynchronizedPerRow()
        {
            var target = new ObservableCollection<Row>(Rows(50));
            var source = Rows(50, firstId: 1000);
            var actions = Record(target);

            CollectionHelper.Replace(target, source);

            Assert.AreEqual(0, Count(actions, NotifyCollectionChangedAction.Reset));
            Assert.AreEqual(50, Count(actions, NotifyCollectionChangedAction.Remove));
            Assert.AreEqual(50, Count(actions, NotifyCollectionChangedAction.Add));
            CollectionAssert.AreEqual(source, target.ToList());
        }

        [TestMethod]
        public void SynchronizeCollection_IsNeverBounded()
        {
            var rows = Rows(2000);
            var target = new BulkObservableCollection<Row>();
            target.ReplaceAll(rows);
            var reversed = rows.AsEnumerable().Reverse().ToList();
            var actions = Record(target);

            CollectionHelper.SynchronizeCollection(target, reversed);

            Assert.AreEqual(0, Count(actions, NotifyCollectionChangedAction.Reset));
            CollectionAssert.AreEqual(reversed, target.ToList());
        }
    }
}
