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
            StringAssert.Contains(source, "CopyStateFrom(materializedRows[index])");
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
            var copy = body.IndexOf("CopyStateFrom(materializedRows[index])", StringComparison.Ordinal);
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
                "AchievementRows[index].CopyStateFrom(materializedRows[index])",
                "The copy must be driven by the changed set, not by every position.");
        }

        [TestMethod]
        public void PastTheThreshold_OneResetIsPreferredToNotifyingEveryRow()
        {
            var source = ReadViewModel();

            // Raising "every property changed" per row defers its real cost to later dispatcher
            // passes, so it does not appear in the loop that causes it. Measured with the stall
            // watchdog: a Reset stalls ~440ms, notifying all 641 rows stalls 670-870ms. Without
            // this cap the in-place path is slower than the thing it replaced.
            StringAssert.Contains(source, "changedRows.Count <= InPlaceNotifyThreshold");
            StringAssert.Contains(source, "private const int InPlaceNotifyThreshold");
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
            var parts = new[]
            {
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"
            };

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

            Assert.Fail("Could not find ManageAchievementsEditorViewModel.cs.");
            return null;
        }
    }
}
