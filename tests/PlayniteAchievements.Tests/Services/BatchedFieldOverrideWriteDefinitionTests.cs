using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Editor field writes are batched and flushed once per gesture, but the flush grouped them
    /// by (field, value) and took one store update per group. That collapses a batch only when
    /// every achievement takes the same value: setting one name across a selection does, undoing
    /// it does not, because each achievement gets its own previous value back.
    ///
    /// So undo and redo of a bulk edit degraded to one update per row -- each a load, a deep
    /// clone, three normalizations, a serialize, a SQLite write and a change cascade. Undoing a
    /// rename across 641 rows measured 215 store writes over 42 seconds with a 5.8s UI freeze,
    /// and the caller stack on GameCustomData.Save named this path outright:
    /// SetAchievementFieldOverride &lt;- FlushBatchedFieldWrites &lt;- ApplyRowValueStep &lt;- ApplyHistoryStep.
    ///
    /// Neither class is linked into this project, so these assert against the source.
    /// </summary>
    [TestClass]
    public class BatchedFieldOverrideWriteDefinitionTests
    {
        [TestMethod]
        public void TheFlush_WritesTheWholeBatchInOneCall()
        {
            var body = Between(
                ReadEditorViewModel(),
                "private void FlushBatchedFieldWrites()",
                "private void WriteProviderField(");

            StringAssert.Contains(
                body,
                "SetAchievementFieldOverrides(_gameId, pending)",
                "The batch must go to the store as one unit.");

            Assert.IsFalse(
                body.Contains("GroupBy"),
                "Grouping by value is the regression: a batch of distinct values becomes one " +
                "store update per distinct value, which for an undo is one per row.");
        }

        [TestMethod]
        public void TheMultiValueWriter_TakesExactlyOneStoreUpdate()
        {
            var body = Between(
                ReadOverridesService(),
                "public void SetAchievementFieldOverrides(",
                "private static void ApplyFieldToEntry(");

            Assert.AreEqual(
                1,
                CountOccurrences(body, "_gameCustomDataStore.Update("),
                "One update for the whole batch is the entire point; a second would reintroduce " +
                "the per-write cascade.");

            StringAssert.Contains(
                body,
                "foreach (var write in resolved)",
                "Each achievement's own value is applied inside that single update.");
        }

        [TestMethod]
        public void TheMultiValueWriter_ClonesTheOverrideMapOnceForTheBatch()
        {
            var body = Between(
                ReadOverridesService(),
                "public void SetAchievementFieldOverrides(",
                "private static void ApplyFieldToEntry(");

            // The outer half of the quadratic was one store write per row, fixed by the single
            // Update above. This is the inner half: MutateOverrides deep-copies the game's whole
            // override map and re-stores it on every call, so driving it per write inside that
            // one Update still cost N copies of an N-entry map. Undoing a bulk edit across 641
            // achievements came to roughly 400,000 entry clones for one gesture.
            Assert.IsFalse(
                body.Contains("MutateOverrides("),
                "The batch must not drive MutateOverrides per write: it clones and re-stores the " +
                "entire override map each call, which is quadratic across a bulk gesture.");

            Assert.AreEqual(
                1,
                CountOccurrences(body, "CloneOverrides(customData)"),
                "One clone of the override map for the whole batch.");

            Assert.AreEqual(
                1,
                CountOccurrences(body, "StoreOverrides(customData, overrides)"),
                "One store-back for the whole batch.");
        }

        [TestMethod]
        public void TheMultiValueWriter_KeepsLastWriteWinsPerAchievementAndField()
        {
            var body = Between(
                ReadOverridesService(),
                "public void SetAchievementFieldOverrides(",
                "private static void ApplyFieldToEntry(");

            // A sequence of individual calls would have left the last value; folding them into
            // one update must not change that, or a gesture that touches a row twice lands on
            // the wrong value.
            StringAssert.Contains(body, "resolved[existing] = (normalized, write.Field, write.Value)");
            StringAssert.Contains(body, "normalized + \"\\u0000\" + write.Field");
        }

        [TestMethod]
        public void TheMultiValueWriter_UnionsTheChangeFlagsOverTheBatch()
        {
            var body = Between(
                ReadOverridesService(),
                "public void SetAchievementFieldOverrides(",
                "private static void ApplyFieldToEntry(");

            // Understating either flag leaves the edit in the store with nothing on screen
            // re-reading it, or leaves the override mirror stale for points and trophy type.
            // Every field, the unlock time included, is shown on summary rows and moves their
            // unlock-date counts, so the summary flag is unconditional.
            StringAssert.Contains(body, "var affectsSummary = true;");
            StringAssert.Contains(body, "write.Field == AchievementEditableField.Points");
            StringAssert.Contains(body, "write.Field == AchievementEditableField.TrophyType");
        }

        [TestMethod]
        public void BothWriters_ShareOneFieldMutation()
        {
            var source = ReadOverridesService();

            // The single-value and multi-value paths must apply a field identically, or the same
            // edit made two ways lands differently in the record.
            StringAssert.Contains(source, "private static void ApplyFieldToEntry(");
            Assert.AreEqual(
                2,
                CountOccurrences(source, "ApplyFieldToEntry(entry, "),
                "Exactly the two writers, both going through the shared mutation.");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadEditorViewModel()
        {
            return ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs");
        }

        private static string ReadOverridesService()
        {
            return ReadRepoFile(
                "source", "Services", "Achievements", "AchievementOverridesService.cs");
        }

        private static string ReadRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {parts.Last()}.");
            return null;
        }
    }
}
