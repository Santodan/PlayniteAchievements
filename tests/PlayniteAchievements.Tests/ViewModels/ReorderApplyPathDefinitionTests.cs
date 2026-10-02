using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// Dragging a block of rows was slow because the reorder synchronized the collection item by
    /// item: for each displaced row it scanned the rest of the list -- quadratic in the list,
    /// not bounded by how far the rows travelled as its comment claimed -- and raised a
    /// collection change per row for the grid to handle one at a time.
    ///
    /// A single Reset collapses all of that, but a Reset was measured on this grid at ~137ms for
    /// the DataGrid to react plus ~175ms to re-realize a viewport, and that price does not scale
    /// down with the size of the change. Using it unconditionally would make short drags slower
    /// than they were. Hence the threshold, and hence both paths staying in the code.
    ///
    /// The view model is not linked into this project, so these assert against the source.
    /// </summary>
    [TestClass]
    public class ReorderApplyPathDefinitionTests
    {
        [TestMethod]
        public void BothApplyPaths_SurviveAndAreChosenByHowMuchMoved()
        {
            var body = Between(
                ReadViewModel(),
                "var displaced = CountDisplacedPositions(",
                "PersistCurrentOrder()");

            StringAssert.Contains(
                body,
                "AchievementRows.ReplaceAll(reordered)",
                "The Reset path is what makes a block drag cheap.");
            StringAssert.Contains(
                body,
                "CollectionHelper.SynchronizeCollection(AchievementRows, reordered)",
                "The incremental path must stay: a Reset costs ~300ms whatever changed, so " +
                "using it for a two-row nudge would be a regression.");
            StringAssert.Contains(body, "displaced > ReorderResetThreshold");
        }

        [TestMethod]
        public void TheChoice_IsMadeOnDisplacedPositionsNotOnRowsDragged()
        {
            var source = ReadViewModel();

            // Dragging one row to the far end displaces everything in between, so the count of
            // rows the user grabbed says nothing about what either path will cost.
            StringAssert.Contains(source, "private static int CountDisplacedPositions(");

            var body = Between(
                source,
                "private static int CountDisplacedPositions(",
                "private bool TryMoveItems(");

            StringAssert.Contains(
                body,
                "!ReferenceEquals(current[i], reordered[i])",
                "Positions are compared by identity: the rows are the same instances in a new " +
                "order, so value equality would be both wrong and slower.");
            StringAssert.Contains(
                body,
                "current.Count != reordered.Count",
                "A length change is not a plain reorder and must not be treated as a small one.");
            StringAssert.Contains(body, "int.MaxValue");
        }

        [TestMethod]
        public void TheScrollOffset_IsCapturedBeforeTheMoveAndRestoredAfter()
        {
            var source = ReadReorderBehavior();

            // The prerequisite for using a Reset at all: without this the grid jumps to the top
            // on every block drag, which is why the incremental path was chosen originally.
            StringAssert.Contains(source, "_scrollViewer?.VerticalOffset ?? 0d");
            StringAssert.Contains(source, "RestoreScrollOffsetAfterReorder(");

            var restore = Between(
                source,
                "private void RestoreScrollOffsetAfterReorder(",
                "private void EnsureScrollViewer()");

            StringAssert.Contains(
                restore,
                "DispatcherPriority.Loaded",
                "The Reset re-runs layout, so an offset set before that lands is overwritten.");
            StringAssert.Contains(restore, "ScrollToVerticalOffset(");
        }

        [TestMethod]
        public void TheCapture_HappensBeforeTheMoveIsApplied()
        {
            var source = ReadReorderBehavior();

            var capture = source.IndexOf("_scrollViewer?.VerticalOffset ?? 0d", StringComparison.Ordinal);
            var move = source.IndexOf("_options.MoveItemsRelativeToTarget(", StringComparison.Ordinal);
            var restore = source.IndexOf("RestoreScrollOffsetAfterReorder(", StringComparison.Ordinal);

            Assert.IsTrue(capture >= 0 && move > capture, "The offset must be read before the move.");
            Assert.IsTrue(restore > move, "The restore must follow the move.");
        }

        [TestMethod]
        public void TheResetPath_IsInstrumentedWithWhichBranchItTook()
        {
            var source = ReadViewModel();

            StringAssert.Contains(source, "\"Editor.Reorder.Apply\"");
            StringAssert.Contains(
                source,
                "\" path=\" + (displaced > ReorderResetThreshold ? \"reset\" : \"incremental\")",
                "Which branch ran is the thing worth reading back out of a log; a duration " +
                "without it cannot say whether the threshold is set right.");

            StringAssert.Contains(
                source,
                "\"Editor.ResetCustomizations\"",
                "The reset-all path reached a full reload with no scope of its own, so it showed " +
                "up in a log with no visible cause.");
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadViewModel()
        {
            return ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs");
        }

        private static string ReadReorderBehavior()
        {
            return ReadRepoFile("source", "Views", "Helpers", "DataGridRowReorderBehavior.cs");
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
