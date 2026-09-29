using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// Reset-all measured ~565ms on a 641-row game: ~170ms to write the reverted rows, then a
    /// full reload whose two largest costs were the collection Reset the DataGrid reacts to
    /// (~136ms) and the viewport re-realization it forces (~162ms). A reset changes values but
    /// not which achievements exist, so those two are avoidable.
    ///
    /// The view model is not linked into this project, so the gate is asserted against the
    /// source; the copy mechanism it relies on is covered behaviourally by
    /// ObservableStateCopierTests.
    /// </summary>
    [TestClass]
    public class RowsInPlaceRefreshDefinitionTests
    {
        [TestMethod]
        public void BothPathsSurvive_AndTheResetIsOnlyTakenWhenTheRowSetChanges()
        {
            var source = ReadViewModel();

            StringAssert.Contains(source, "TryCopyRowsInPlace(materializedRows)");
            StringAssert.Contains(
                source,
                "AchievementRows.ReplaceAll(materializedRows)",
                "The Reset path must stay: adding, removing or reordering rows has to go through " +
                "the collection or the grid never learns about it.");
            StringAssert.Contains(source, "CopyStateFrom(materializedRows[index], notify)");
        }

        [TestMethod]
        public void TheGate_RequiresTheSameAchievementsInTheSameOrder()
        {
            var body = Between(
                ReadViewModel(),
                "private bool TryCopyRowsInPlace(",
                "private bool TryMoveItems(");

            StringAssert.Contains(
                body,
                "AchievementRows.Count != incoming.Count",
                "A different row count is an add or a remove, which the in-place path cannot " +
                "express.");
            StringAssert.Contains(
                body,
                "OriginalApiName",
                "Identity is the achievement's key: the incoming rows are always freshly built, " +
                "so comparing row instances would never match.");

            // Position by position, not as a set. A reorder must go through the collection so
            // the grid actually moves the rows rather than silently rewriting them in place.
            StringAssert.Contains(body, "for (var i = 0; i < incoming.Count; i++)");
            StringAssert.Contains(body, "StringComparison.OrdinalIgnoreCase");
        }

        [TestMethod]
        public void TheGate_RefusesRowsWithoutAKey()
        {
            var body = Between(
                ReadViewModel(),
                "private bool TryCopyRowsInPlace(",
                "private bool TryMoveItems(");

            StringAssert.Contains(
                body,
                "string.IsNullOrWhiteSpace(existingKey)",
                "An unkeyed row cannot be matched, so the copy must be refused rather than " +
                "guessed -- a wrong pairing would show one achievement's values on another.");
        }

        [TestMethod]
        public void TheCopy_RunsWhileTheRowsAreDetached()
        {
            var body = Between(
                ReadViewModel(),
                "private void ReplaceRows(",
                "private bool TryCopyRowsInPlace(");

            // The rows are detached at the top of ReplaceRows and reattached by AttachRow, so no
            // property change raised during the copy can reach the persistence hook. That is the
            // second half of why this is safe, alongside the copier never driving a setter.
            var detach = body.IndexOf("DetachRow(row)", StringComparison.Ordinal);
            var copy = body.IndexOf("CopyStateFrom(materializedRows[index], notify)", StringComparison.Ordinal);
            var attach = body.IndexOf("AttachRow(row, useSeparateLockedIcons)", StringComparison.Ordinal);

            Assert.IsTrue(detach >= 0, "The detach loop must remain.");
            Assert.IsTrue(attach > detach, "Rows are reattached after being detached.");
            Assert.IsTrue(copy > detach, "The copy must happen after the rows are detached.");
        }

        [TestMethod]
        public void OnlyChangedRows_AreCopiedAndNotified()
        {
            var source = ReadViewModel();

            StringAssert.Contains(source, "FindChangedRows(materializedRows)");
            StringAssert.Contains(
                source,
                "ObservableStateCopier.StateEquals(AchievementRows[i], incoming[i])",
                "An unchanged row needs neither the copy nor the notification, and the " +
                "notification is the expensive half.");
            StringAssert.Contains(
                source,
                "var index = changedRows[i]",
                "The copy must be driven by the changed set, not by every position.");
        }

        [TestMethod]
        public void OnlyRowsSomethingIsBoundTo_AreNotified()
        {
            var source = ReadViewModel();

            // Raising "every property changed" per row defers its real cost to later dispatcher
            // passes, so it never appears in the loop that causes it. Measured with the stall
            // watchdog: announcing to all 641 rows stalled 670-870ms, worse than the ~440ms
            // Reset it replaced. Bounding notifications by the viewport is what makes the
            // in-place path scale, since a viewport is a dozen rows however much changed.
            StringAssert.Contains(source, "var notify = ShouldNotifyRow(target)");
            StringAssert.Contains(source, "target.CopyStateFrom(materializedRows[index], notify)");
        }

        [TestMethod]
        public void TheSelectedRowAndBulkRow_AreAlwaysNotified()
        {
            var body = Between(
                ReadViewModel(),
                "private bool ShouldNotifyRow(",
                "private List<int> FindChangedRows(");

            // The details pane binds the selected row whether or not the grid has realized it,
            // so skipping it would leave the pane showing pre-reset values.
            StringAssert.Contains(body, "ReferenceEquals(row, SelectedRow) || row.IsBulkRow");

            // Correctness over speed when the view has not wired the tracker.
            StringAssert.Contains(body, "IsRowRealized == null || IsRowRealized(row)");
        }

        [TestMethod]
        public void TheViewTracksRealizedRowsOnBothEdges()
        {
            var source = ReadRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml.cs");

            // Tracking only additions would leave scrolled-away rows marked realized forever,
            // growing the notify set back to the whole list.
            StringAssert.Contains(source, "LoadingRow += AchievementsGrid_LoadingRow");
            StringAssert.Contains(source, "UnloadingRow += AchievementsGrid_UnloadingRow");
            StringAssert.Contains(source, "LoadingRow -= AchievementsGrid_LoadingRow");
            StringAssert.Contains(source, "UnloadingRow -= AchievementsGrid_UnloadingRow");
            StringAssert.Contains(source, "_realizedRows.Add(realized)");
            StringAssert.Contains(source, "_realizedRows.Remove(row)");
            StringAssert.Contains(source, "viewModel.IsRowRealized = row =>");

            // The realization counter is gated on tracing; the tracking must not be, or the set
            // is empty in a normal build and every refresh silently shows stale rows.
            var loading = Between(
                source,
                "private void AchievementsGrid_LoadingRow(",
                "private void ReportRowRealization(");
            var add = loading.IndexOf("_realizedRows.Add(realized)", StringComparison.Ordinal);
            var gate = loading.IndexOf("PerfScope.PerfTracingEnabled", StringComparison.Ordinal);
            Assert.IsTrue(add >= 0 && gate > add, "Tracking must precede the tracing gate.");
        }

        [TestMethod]
        public void TheInPlacePath_IsInstrumentedSeparatelyFromTheReset()
        {
            var source = ReadViewModel();

            // Two distinct tags so a log says which path a reload took, and what it cost.
            StringAssert.Contains(source, "\"Editor.ReplaceRows.CopyInPlace\"");
            StringAssert.Contains(source, "\"Editor.ReplaceRows.Reset\"");
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
