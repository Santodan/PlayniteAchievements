using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Common;

namespace PlayniteAchievements.Tests.Common
{
    [TestClass]
    public class CollectionHelperMergeByKeyTests
    {
        private sealed class Row
        {
            public Row(string key, string value)
            {
                Key = key;
                Value = value;
            }

            public string Key { get; }

            public string Value { get; set; }
        }

        private static List<Row> Merge(IReadOnlyList<Row> existing, IReadOnlyList<Row> fresh)
        {
            return CollectionHelper.MergeByKey(existing, fresh, row => row.Key, (kept, source) => kept.Value = source.Value);
        }

        [TestMethod]
        public void MatchedRows_KeepTheirInstances_AndTakeTheFreshValues()
        {
            var a = new Row("a", "old-a");
            var b = new Row("b", "old-b");

            var merged = Merge(new[] { a, b }, new[] { new Row("a", "new-a"), new Row("b", "new-b") });

            Assert.AreSame(a, merged[0]);
            Assert.AreSame(b, merged[1]);
            CollectionAssert.AreEqual(new[] { "new-a", "new-b" }, merged.Select(row => row.Value).ToList());
        }

        [TestMethod]
        public void TheFreshOrder_Wins()
        {
            var a = new Row("a", "a");
            var b = new Row("b", "b");

            var merged = Merge(new[] { a, b }, new[] { new Row("b", "b"), new Row("a", "a") });

            Assert.AreSame(b, merged[0]);
            Assert.AreSame(a, merged[1]);
        }

        [TestMethod]
        public void AddedRowsComeFromTheFreshList_AndRemovedRowsAreDropped()
        {
            var a = new Row("a", "a");
            var gone = new Row("gone", "gone");
            var added = new Row("added", "added");

            var merged = Merge(new[] { a, gone }, new[] { new Row("a", "a"), added });

            Assert.AreEqual(2, merged.Count);
            Assert.AreSame(a, merged[0]);
            Assert.AreSame(added, merged[1]);
            Assert.IsFalse(merged.Contains(gone));
        }

        [TestMethod]
        public void KeysCompareIgnoringCase()
        {
            var a = new Row("Achievement_A", "old");

            var merged = Merge(new[] { a }, new[] { new Row("achievement_a", "new") });

            Assert.AreSame(a, merged[0]);
            Assert.AreEqual("new", a.Value);
        }

        [TestMethod]
        public void DuplicateKeys_PairUpInOrder()
        {
            var first = new Row("dup", "1");
            var second = new Row("dup", "2");

            var merged = Merge(new[] { first, second }, new[] { new Row("dup", "x"), new Row("dup", "y") });

            Assert.AreSame(first, merged[0]);
            Assert.AreSame(second, merged[1]);
            Assert.AreEqual("x", first.Value);
            Assert.AreEqual("y", second.Value);
        }

        [TestMethod]
        public void RowsWithoutAKey_AreTakenFresh()
        {
            var keyless = new Row(null, "old");
            var freshKeyless = new Row(null, "new");

            var merged = Merge(new[] { keyless }, new[] { freshKeyless });

            Assert.AreSame(freshKeyless, merged[0]);
            Assert.AreEqual("old", keyless.Value);
        }

        [TestMethod]
        public void MatchesAFromScratchBuild_ForEveryValue()
        {
            // What the pane shows after the merge must equal what a fresh load would show; only
            // the instances differ.
            var existing = new[] { new Row("a", "1"), new Row("b", "2"), new Row("c", "3") };
            var fresh = new[] { new Row("c", "30"), new Row("a", "10"), new Row("d", "40") };

            var merged = Merge(existing, fresh);

            CollectionAssert.AreEqual(
                fresh.Select(row => row.Key + "=" + row.Value).ToList(),
                merged.Select(row => row.Key + "=" + row.Value).ToList());
        }
    }
}
