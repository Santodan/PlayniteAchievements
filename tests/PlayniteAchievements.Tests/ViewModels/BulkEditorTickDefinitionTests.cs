using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// The details pane's unlocked/hidden/goal ticks bind to nullable properties so a multi-row
    /// selection that disagrees can show a blank. That blank has to stay a display state: as a
    /// third clickable state it sits between checked and unchecked, so unticking a selection that
    /// already agreed lands on it, and a blank is never applied. The editor view model is not
    /// linked into this project, so the contract is pinned against the markup.
    /// </summary>
    [TestClass]
    public class BulkEditorTickDefinitionTests
    {
        [TestMethod]
        public void SelectionTicks_AreNotThreeState()
        {
            var markup = ReadEditorTab();

            Assert.IsFalse(
                markup.Contains("IsThreeState"),
                "A three-state tick puts the blank between checked and unchecked, which swallows " +
                "the click that would untick a selection that agrees.");
        }

        [TestMethod]
        public void SelectionTicks_StillBindTheNullableState()
        {
            var markup = ReadEditorTab();

            foreach (var property in new[] { "UnlockedState", "HiddenState", "IsGoalState" })
            {
                Assert.IsTrue(
                    Regex.IsMatch(markup, @"IsChecked=""\{Binding " + property + @", Mode=TwoWay"),
                    $"{property} is what carries the blank for a selection that disagrees.");
            }
        }

        private static string ReadEditorTab()
        {
            return File.ReadAllText(FindRepoFile(
                "source", "Views", "ManageAchievements", "ManageAchievementsEditorTab.xaml"));
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
