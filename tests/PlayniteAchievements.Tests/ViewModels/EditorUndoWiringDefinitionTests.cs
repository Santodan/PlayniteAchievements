using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The undo history itself is tested directly. What cannot be, because neither the editor view
    /// nor its view model is linked into this project, is how it is wired in - and three parts of
    /// that wiring are the kind that break quietly.
    /// </summary>
    [TestClass]
    public class EditorUndoWiringDefinitionTests
    {
        [TestMethod]
        public void TheShortcut_LeavesAFocusedTextBoxItsOwnUndo()
        {
            var source = ReadCodeBehind();

            StringAssert.Contains(
                source,
                "Keyboard.FocusedElement is TextBoxBase",
                "Every editable box here commits on losing focus, so while one has focus nothing " +
                "has reached the store and WPF's in-box undo is the correct behaviour.");
            StringAssert.Contains(source, "ModifierKeys.Control");
            StringAssert.Contains(source, "case Key.Z:");
            StringAssert.Contains(source, "case Key.Y:");
            StringAssert.Contains(
                source,
                "ModifierKeys.Shift",
                "Ctrl+Shift+Z redoes, as it does everywhere else.");
        }

        [TestMethod]
        public void TheShortcut_IsScopedToTheTabRatherThanTheWindow()
        {
            var source = ReadCodeBehind();

            StringAssert.Contains(
                source,
                "PreviewKeyDown += EditorTab_PreviewKeyDown",
                "On the control: a window-wide Ctrl+Z would fight the other tabs and their boxes.");
            Assert.IsFalse(
                source.Contains("_achievementNavigationHost.PreviewKeyDown += EditorTab_PreviewKeyDown"),
                "The arrow-key handler is the window's because stepping achievements is what the " +
                "window is for. Undo is not, so it must not be promoted to that scope.");
            StringAssert.Contains(
                source,
                "PreviewKeyDown -= EditorTab_PreviewKeyDown",
                "Attached in the constructor, so it comes off in the explicit teardown.");
        }

        [TestMethod]
        public void TheViewModel_ReleasesEverythingTheHistoryHolds()
        {
            var source = ReadViewModel();
            var detach = ExtractMethod(source, "public void Detach()");

            StringAssert.Contains(
                detach,
                "CustomDataWritten -= GameCustomDataStore_CustomDataWritten",
                "The store outlives every window, so a missed unsubscribe roots this view model " +
                "and every row behind it for the process.");
            StringAssert.Contains(
                detach,
                "_undoStepTimer.Tick -= UndoStepTimer_Tick",
                "A running DispatcherTimer is rooted by the dispatcher and holds its handler.");
            StringAssert.Contains(detach, "_undoJournal.Clear()");
        }

        [TestMethod]
        public void AReversal_GoesBackThroughTheStoreWithTheRecordedFlags()
        {
            var source = ReadViewModel();
            var apply = ExtractMethod(source, "private void ApplyHistoryStep(EditorUndoEntry entry, bool reverse)");

            StringAssert.Contains(
                apply,
                "_gameCustomDataStore.Update(",
                "Writing the repository directly would put the record back and leave the cache " +
                "mirror, the theme state and the tag sync stale.");
            StringAssert.Contains(
                apply,
                "entry.AffectsSummaryData",
                "A reversal has to report what the original write reported.");
            StringAssert.Contains(apply, "entry.AffectsOverrideMirror");
        }

        [TestMethod]
        public void TheGestureBoundary_CoversEveryEditingEntryPoint()
        {
            var source = ReadViewModel();

            // A missing label splits one gesture into two undo steps rather than losing it, but
            // these are the paths that would be noticeably wrong.
            foreach (var entryPoint in new[]
            {
                "private void AddRow()",
                "private void DuplicateSelected()",
                "private void DeleteSelected()",
                "private void ResetOrder()",
                "public void SetCategoryForSelection(string categoryLabel)",
                "public void SetCapstoneForSelection(bool isCapstone)",
                "public void SetGoalForSelection(bool isGoal)",
                "public void SetFilterScopeForSelection(AchievementFilterScope scope)",
                "private void Row_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)",
                "private void BulkRow_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)"
            })
            {
                var body = ExtractMethod(source, entryPoint);
                StringAssert.Contains(
                    body,
                    "MarkUndoIntent(",
                    $"{entryPoint} writes to the store, so it has to say which gesture it is.");
            }
        }

        [TestMethod]
        public void AFanOutEdit_StaysOneGestureAcrossItsPropertyBurst()
        {
            var source = ReadViewModel();
            var resolve = ExtractMethod(source, "private static string ResolveFieldGesture(string propertyName)");

            // Setting a timestamp raises the date, the time text, the mode and the flag together.
            // Treated as four gestures they would need four presses to undo.
            foreach (var property in new[] { "UnlockDate", "TimeText", "SelectedTimeModeText", "HasUnlockTime" })
            {
                StringAssert.Contains(resolve, property, $"{property} belongs to the timestamp gesture.");
            }

            StringAssert.Contains(resolve, "ProgressNumText");
            StringAssert.Contains(resolve, "ProgressDenomText");
        }

        [TestMethod]
        public void TheReversalItself_IsNotRecordedAsANewStep()
        {
            var source = ReadViewModel();

            StringAssert.Contains(
                source,
                "_isApplyingUndo",
                "Without the guard, undoing would record its own write and the history would " +
                "never empty.");

            var handler = ExtractMethod(
                source,
                "private void GameCustomDataStore_CustomDataWritten(object sender, GameCustomDataWrittenEventArgs e)");
            StringAssert.Contains(handler, "_isApplyingUndo");
            StringAssert.Contains(
                handler,
                "e.PlayniteGameId != _gameId",
                "Another game's write is not this editor's history.");
        }

        [TestMethod]
        public void AnIconChange_IsRememberedByAFreshCopyEachTime()
        {
            // The managed icon path is a slot whose contents change while its name stays the
            // same. Keying kept copies by that path returns the first image copied for every
            // later change, which is what limited the history to a single level.
            var retain = ExtractMethod(ReadViewModel(), "private object RetainIconValue(object value)");

            StringAssert.Contains(
                retain,
                "File.Copy(",
                "A path alone does not survive the art behind it being replaced.");
            Assert.IsFalse(
                retain.Contains("ContainsKey") || retain.Contains("TryGetValue"),
                "Reusing a copy already taken for this path is the one-level bug: the slot " +
                "keeps its name while its contents change.");
        }

        /// <summary>
        /// A method body, by its signature line, matched to the closing brace at its own indent.
        /// </summary>
        private static string ExtractMethod(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"Could not find {signature}.");

            var open = source.IndexOf('{', start);
            Assert.IsTrue(open > start, $"{signature} has no body.");

            var depth = 0;
            for (var index = open; index < source.Length; index++)
            {
                if (source[index] == '{')
                {
                    depth++;
                }
                else if (source[index] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, index - open + 1);
                    }
                }
            }

            Assert.Fail($"{signature} does not close.");
            return null;
        }

        private static string ReadCodeBehind()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml.cs"));
        }

        private static string ReadViewModel()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"));
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (directory != null)
            {
                var path = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail($"Could not find {string.Join(Path.DirectorySeparatorChar.ToString(), parts)}.");
            return null;
        }
    }
}
