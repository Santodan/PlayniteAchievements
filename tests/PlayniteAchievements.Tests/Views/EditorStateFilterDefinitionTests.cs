using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.Views
{
    // The editor's state filter is the one facet of the deleted Filters tab that had no
    // counterpart in the merged editor. It spans a view model and a toolbar button, and a filter
    // that is declared but never consulted narrows nothing while still looking live.
    [TestClass]
    public class EditorStateFilterDefinitionTests
    {
        [TestMethod]
        public void StateFilter_IsDeclared_BuiltAndRefreshedLikeTheOtherFacets()
        {
            var code = ReadEditorViewModel();

            foreach (var expected in new[]
            {
                "public GridMultiSelectFilter StateFilter { get; }",
                "StateFilter = BuildStateFilter();",
                "private GridMultiSelectFilter BuildStateFilter()",
                "StateFilter?.Refresh();"
            })
            {
                Assert.IsTrue(
                    code.Contains(expected),
                    "The editor view model is missing: " + expected);
            }
        }

        [TestMethod]
        public void StateFilter_NarrowsTheGridAndCountsAsFiltering()
        {
            var code = ReadEditorViewModel();

            Assert.IsTrue(
                code.Contains("_selectedStateFilters.Count > 0 && !MatchesSelectedStates(row)"),
                "MatchesFilter must consult the state selection, or ticking a state narrows nothing.");

            // IsFiltering drives the "showing a subset" affordances; a facet missing from it
            // leaves the grid silently filtered.
            var isFiltering = Between(code, "public bool IsFiltering =>", ";");
            Assert.IsTrue(
                isFiltering.Contains("_selectedStateFilters.Count > 0"),
                "IsFiltering must include the state selection.");
        }

        [TestMethod]
        public void StateFilter_MatchesHiddenAsAnAlternativeNotAnExclusion()
        {
            var code = ReadEditorViewModel();

            // A hidden achievement is also locked or unlocked. Treating Hidden as a third
            // alternative keeps "Locked" from dropping the hidden locked rows.
            Assert.IsTrue(
                code.Contains("row.Hidden && _selectedStateFilters.Contains(HiddenFilterKey)"),
                "Hidden must match on its own rather than gating the locked/unlocked test.");
            Assert.IsTrue(
                code.Contains("row.Unlocked ? UnlockedFilterKey : LockedFilterKey"),
                "Every row must fall on one side of the unlocked/locked pair.");
        }

        [TestMethod]
        public void StateFilter_ReusesExistingLocalizationKeys()
        {
            var code = ReadEditorViewModel();
            var english = File.ReadAllText(FindRepoFile(
                "source", "Localization", "en_US.xaml"));

            // The filter introduces no new strings; each label already exists for other UI.
            foreach (var key in new[]
            {
                "LOCPlayAch_Common_Unlocked",
                "LOCPlayAch_Common_Locked",
                "LOCPlayAch_ManageAchievements_Custom_Hidden",
                "LOCPlayAch_Column_Status"
            })
            {
                Assert.IsTrue(
                    code.Contains("\"" + key + "\""),
                    "The state filter no longer uses " + key + ".");
                Assert.IsTrue(
                    english.Contains("x:Key=\"" + key + "\""),
                    key + " must already exist in en_US.xaml.");
            }
        }

        [TestMethod]
        public void StateFilterButton_SitsWithTheOtherFilterButtons()
        {
            var xaml = File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml"));

            var button = Between(xaml, "<Button x:Name=\"StateFilterButton\"", "</Button>");
            Assert.IsTrue(
                button.Contains("DataContext=\"{Binding StateFilter}\""),
                "The state filter button must bind to StateFilter.");
            Assert.IsTrue(
                button.Contains("Click=\"MultiSelectFilter_Click\""),
                "The state filter must open through the shared multi-select handler.");

            // Only the facet drop-downs; the toolbar also carries a plain Clear button.
            var facets = new[]
            {
                "CategoryFilterButton", "TypeFilterButton", "StateFilterButton", "CustomizationFilterButton"
            };
            var order = Regex.Matches(xaml, "<Button x:Name=\"(\\w+FilterButton)\"")
                .Cast<Match>()
                .Select(match => match.Groups[1].Value)
                .Where(name => facets.Contains(name))
                .ToList();
            CollectionAssert.AreEqual(
                facets,
                order,
                "Filter buttons run from what an achievement is to what the user did to it. "
                    + "Found: " + string.Join(", ", order));
        }

        private static string ReadEditorViewModel()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsEditorViewModel.cs"));
        }

        private static string Between(string content, string startMarker, string endMarker)
        {
            var start = content.IndexOf(startMarker, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, "Could not find " + startMarker + " in the source.");

            var end = content.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            Assert.IsTrue(end > start, "Could not find the end of " + startMarker + ".");

            return content.Substring(start, end - start);
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

            Assert.Fail("Could not find " + Path.Combine(parts));
            return null;
        }
    }
}
