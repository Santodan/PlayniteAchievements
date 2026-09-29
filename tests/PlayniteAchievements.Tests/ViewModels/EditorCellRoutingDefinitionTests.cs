using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The editor's grid cells edit the whole selection, the way the details pane does. That rests
    /// on each interactive cell displaying one way and committing through the routed target: a
    /// two-way binding would quietly write its own row only, so the same control would mean
    /// different things depending on whether it was clicked in the grid or in the pane.
    ///
    /// The editor view and view model are not linked into this project, so the contract is pinned
    /// against the markup and the handler source, the way the bulk tick contract is.
    /// </summary>
    [TestClass]
    public class EditorCellRoutingDefinitionTests
    {
        /// <summary>The facet columns, plus the editable ones that predate them.</summary>
        private static readonly string[] RoutedColumnKeys =
        {
            "EditorUnlocked",
            "EditorName",
            "EditorDescription",
            "EditorUnlockTime",
            "EditorHidden",
            "EditorGoal",
            "EditorCapstone",
            "EditorRarity",
            "EditorTrophy",
            "EditorPoints",
            "EditorProgress",
            "EditorCategory",
            "EditorType",
            "EditorFilter",
            "EditorNote"
        };

        [TestMethod]
        public void EveryEditableColumn_DeclaresThatItRoutesToTheSelection()
        {
            var markup = ReadEditorTabMarkup();

            foreach (var key in RoutedColumnKeys)
            {
                var column = ExtractColumnDeclaration(markup, key);
                StringAssert.Contains(
                    column,
                    "EditorCellRouting.RoutesToSelection=\"True\"",
                    $"{key} is editable, so a press in it has to settle the selection before the " +
                    "cell's own control swallows the click.");
            }
        }

        [TestMethod]
        public void RoutedColumns_AreReadOnlySoTheCellControlTakesTheClick()
        {
            var markup = ReadEditorTabMarkup();

            foreach (var key in RoutedColumnKeys)
            {
                var column = ExtractColumnDeclaration(markup, key);
                StringAssert.Contains(
                    column,
                    "IsReadOnly=\"True\"",
                    $"{key} holds a live control, so the grid's own edit mode must stay out of it. " +
                    "Without this a checkbox needs two clicks: one to enter edit mode, one to tick.");
            }
        }

        [TestMethod]
        public void RoutedCells_DoNotBindTwoWay()
        {
            var cells = ReadRoutedCellTemplates();

            var twoWay = Regex.Matches(cells, @"Mode=TwoWay")
                .Cast<Match>()
                .Count();

            Assert.AreEqual(
                0,
                twoWay,
                "A two-way binding in a routed cell writes that cell's own row and nothing else, " +
                "which is the behaviour these columns exist to avoid. Bind one way and commit " +
                "through EditorCellRouting.ResolveTarget instead.");
        }

        [TestMethod]
        public void RoutedSelectors_ApplyTheirPickInCode()
        {
            var cells = ReadRoutedCellTemplates();

            // A selector re-resolves its selection when its container is re-bound, so a two-way
            // binding fires for a refresh no user caused. Both of these carry a handler instead.
            foreach (var pair in new[]
            {
                new[] { "<DatePicker", "SelectedDateChanged=" },
                new[] { "<ComboBox", "SelectionChanged=" }
            })
            {
                if (!cells.Contains(pair[0]))
                {
                    continue;
                }

                StringAssert.Contains(
                    cells,
                    pair[1],
                    $"{pair[0]} in a routed cell must apply its pick through a handler that can " +
                    "tell a user gesture from a recycled container's refresh.");
            }
        }

        [TestMethod]
        public void SelectorGuards_CompareAgainstTheCellsOwnRow()
        {
            var handlers = ReadEditorTabCodeBehind();

            // Comparing against the edit target would let a recycled container's refresh through
            // and stamp one row's value across the whole selection. Comparing against the row the
            // cell is bound to is what makes the refresh a no-op.
            StringAssert.Contains(
                handlers,
                "picker.SelectedDate == row.UnlockDate",
                "The date guard has to compare against the cell's own row.");
            StringAssert.Contains(
                handlers,
                "row.SelectedTimeModeText",
                "The time mode guard has to compare against the cell's own row.");
        }

        [TestMethod]
        public void FacetColumns_DefaultToHidden()
        {
            var codeBehind = ReadEditorTabCodeBehind();
            var defaults = ExtractBlock(codeBehind, "DefaultColumnVisibility");

            foreach (var key in new[]
            {
                "EditorHidden",
                "EditorGoal",
                "EditorCapstone",
                "EditorRarity",
                "EditorTrophy",
                "EditorPoints",
                "EditorProgress",
                "EditorCategory",
                "EditorType",
                "EditorFilter",
                "EditorNote"
            })
            {
                StringAssert.Contains(
                    defaults,
                    $"[\"{key}\"] = false",
                    $"{key} has to be named as hidden rather than left out: a key the map says " +
                    "nothing about keeps whatever the markup declared, which is visible.");
            }
        }

        [TestMethod]
        public void PinnedColumns_AreNeitherHideableNorReorderable()
        {
            var markup = ReadEditorTabMarkup();
            var codeBehind = ReadEditorTabCodeBehind();
            var pinned = ExtractBlock(codeBehind, "PinnedColumnKeys");

            foreach (var key in new[] { "EditorOrder", "EditorStatus", "EditorUnlocked" })
            {
                StringAssert.Contains(pinned, $"\"{key}\"", $"{key} is one of the pinned columns.");

                var column = ExtractColumnDeclaration(markup, key);
                StringAssert.Contains(
                    column,
                    "CanUserReorder=\"False\"",
                    $"{key} stays at the left edge, so it is not draggable.");
                StringAssert.Contains(
                    column,
                    "CanUserResize=\"False\"",
                    $"{key} is fixed width.");
            }
        }

        [TestMethod]
        public void PinnedOrderColumn_LeadsTheDeclaredColumns()
        {
            var markup = ReadEditorTabMarkup();

            var order = markup.IndexOf("\"EditorOrder\"", StringComparison.Ordinal);
            var status = markup.IndexOf("\"EditorStatus\"", StringComparison.Ordinal);
            var unlocked = markup.IndexOf("\"EditorUnlocked\"", StringComparison.Ordinal);

            Assert.IsTrue(order > 0 && status > order && unlocked > status,
                "The pinned three are declared in the order they are pinned in, so the clamp and " +
                "the markup agree. The drag handle in particular is recognised by its display " +
                "index, so it has to lead.");
        }

        /// <summary>
        /// A column's opening tag, up to the start of its cell template.
        /// </summary>
        private static string ExtractColumnDeclaration(string markup, string columnKey)
        {
            var keyIndex = markup.IndexOf($"ColumnKey=\"{columnKey}\"", StringComparison.Ordinal);
            Assert.IsTrue(keyIndex > 0, $"No column is keyed {columnKey}.");

            var start = markup.LastIndexOf("<DataGridTemplateColumn", keyIndex, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, $"The {columnKey} key is not on a template column.");

            var end = markup.IndexOf(">", keyIndex, StringComparison.Ordinal);
            Assert.IsTrue(end > start, $"The {columnKey} column tag does not close.");

            return markup.Substring(start, end - start);
        }

        /// <summary>
        /// The cell templates of the routed columns, concatenated, so a binding mode can be
        /// asserted across all of them without catching the details pane's own controls.
        /// </summary>
        private static string ReadRoutedCellTemplates()
        {
            var markup = ReadEditorTabMarkup();
            var templates = new System.Text.StringBuilder();

            foreach (var key in RoutedColumnKeys)
            {
                var keyIndex = markup.IndexOf($"ColumnKey=\"{key}\"", StringComparison.Ordinal);
                Assert.IsTrue(keyIndex > 0, $"No column is keyed {key}.");

                var start = markup.LastIndexOf("<DataGridTemplateColumn", keyIndex, StringComparison.Ordinal);
                var end = markup.IndexOf("</DataGridTemplateColumn>", keyIndex, StringComparison.Ordinal);
                Assert.IsTrue(end > start, $"The {key} column does not close.");

                templates.AppendLine(markup.Substring(start, end - start));
            }

            return templates.ToString();
        }

        /// <summary>
        /// A braced initializer or array body, by the name it is declared under.
        /// </summary>
        private static string ExtractBlock(string source, string memberName)
        {
            var nameIndex = source.IndexOf(memberName, StringComparison.Ordinal);
            Assert.IsTrue(nameIndex > 0, $"No member named {memberName}.");

            var open = source.IndexOf('{', nameIndex);
            Assert.IsTrue(open > 0, $"{memberName} has no initializer.");

            var close = source.IndexOf("};", open, StringComparison.Ordinal);
            Assert.IsTrue(close > open, $"{memberName}'s initializer does not close.");

            return source.Substring(open, close - open);
        }

        private static string ReadEditorTabMarkup()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml"));
        }

        private static string ReadEditorTabCodeBehind()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml.cs"));
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
