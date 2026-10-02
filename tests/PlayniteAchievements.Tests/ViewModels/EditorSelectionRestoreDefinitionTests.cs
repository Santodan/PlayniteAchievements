using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// Undo, redo and reset-all all restore whatever was selected, and the case that matters is
    /// "everything", because that is how a reset-all is made. Restoring it by adding rows to the
    /// DataGrid's SelectedItems one at a time makes the grid redo its selection bookkeeping per
    /// row; on a 641-row game that is what read as the window locking.
    ///
    /// It was invisible in a log because the restore is dispatched at Background priority after
    /// the reload, so none of the reload's own scopes covered it -- the stall watchdog put the
    /// freeze squarely after every scope this plugin owns.
    ///
    /// The view is not linked into this project, so these assert against the source.
    /// </summary>
    [TestClass]
    public class EditorSelectionRestoreDefinitionTests
    {
        [TestMethod]
        public void RestoringEverything_GoesThroughTheGridsBatchedSelectAll()
        {
            var body = ReadRestoreBody();

            StringAssert.Contains(
                body,
                "matches.Count == rows.Count",
                "Selecting every row is the case undo, redo and reset-all produce, and the only " +
                "one the grid can batch.");
            StringAssert.Contains(
                body,
                "CustomAchievementsGrid.SelectAll()",
                "SelectAll is one batched selection change whatever the row count; adding rows " +
                "individually is O(rows) of grid bookkeeping.");
        }

        [TestMethod]
        public void APartialSelection_IsStillRestoredRowByRow()
        {
            var body = ReadRestoreBody();

            // Bounded by how many rows were actually selected, so it is not worth a special
            // case -- and there is no batched API for an arbitrary subset.
            StringAssert.Contains(body, "CustomAchievementsGrid.SelectedItems.Clear()");
            StringAssert.Contains(body, "CustomAchievementsGrid.SelectedItems.Add(row)");
        }

        [TestMethod]
        public void TheRestore_IsInstrumentedWithHowMuchItSelected()
        {
            var body = ReadRestoreBody();

            // Without the counts a duration cannot say whether the batched path was taken.
            StringAssert.Contains(body, "\"Editor.RestoreSelection\"");
            StringAssert.Contains(body, "\"rows=\" + rows.Count + \" selected=\" + matches.Count");
        }

        [TestMethod]
        public void TheRowsThatStayBound_AreReattachedAfterAnInPlaceRefresh()
        {
            var source = ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs");

            // ReplaceRows detaches every row up front. On the in-place path the rows that stay
            // are the existing ones, so attaching the freshly built rows instead left the live
            // ones with no persistence hook, no reveal handler and no undo recorder: the grid
            // showed the right values and the next edit to any row quietly did nothing.
            StringAssert.Contains(
                source,
                "foreach (var row in AchievementRows)\r\n                    {\r\n                        AttachRow(row, useSeparateLockedIcons);",
                "The in-place path must reattach the rows that remain in the collection.");

            StringAssert.Contains(
                source,
                "foreach (var row in materializedRows)\r\n                    {\r\n                        AttachRow(row, useSeparateLockedIcons);",
                "The Reset path must still attach the rows it is about to bind.");
        }

        [TestMethod]
        public void TheReattach_HappensAfterTheCopyNotBefore()
        {
            var source = ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs");

            var copy = source.IndexOf(
                "target.CopyStateFrom(materializedRows[index], notify)", StringComparison.Ordinal);
            var attach = source.IndexOf(
                "\"Editor.ReplaceRows.Attach\"", StringComparison.Ordinal);

            Assert.IsTrue(copy >= 0 && attach > copy,
                "AttachRow subscribes Row_PropertyChanged, so a copy made while it is live would " +
                "run the persistence hook for every field of every changed row.");
        }

        private static string ReadRestoreBody()
        {
            var source = ReadRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml.cs");

            var start = source.IndexOf(
                "private void RestoreSelectionByApiNames(", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Could not find RestoreSelectionByApiNames.");

            var end = source.IndexOf("private void ", start + 20, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of RestoreSelectionByApiNames.");

            return source.Substring(start, end - start);
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
