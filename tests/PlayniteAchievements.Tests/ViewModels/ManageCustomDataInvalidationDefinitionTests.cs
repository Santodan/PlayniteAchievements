using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// One edit in the Manage Achievements window used to raise a cache invalidation on top of the
    /// CustomDataChanged the store already raised for the same write. The projection handler drops
    /// the scope those args carry, so each edit discarded the whole-library projection and forced a
    /// full summary re-read: a reported 35-minute editing session logged 245 of them.
    ///
    /// Removing it is only safe while every consumer still hears about the edit on the
    /// CustomDataChanged path, which is also the path that filters on AffectsSummaryData. These
    /// pin both ends of that.
    /// </summary>
    [TestClass]
    public class ManageCustomDataInvalidationDefinitionTests
    {
        [TestMethod]
        public void AnEdit_DoesNotAlsoRaiseALibraryWideCacheInvalidation()
        {
            var source = ReadManageViewModel();
            var core = Between(
                source,
                "private void NotifyCustomDataChangedCore(",
                "internal void NotifyIconOverridesChanged(");

            Assert.IsFalse(
                core.Contains("NotifyCacheInvalidated("),
                "The store's own CustomDataChanged already reaches every consumer for this write, " +
                "and it carries the scope and the AffectsSummaryData flag that this call dropped.");
            StringAssert.Contains(
                core,
                "Reload();",
                "The window still reloads per edit -- that is what shows the user their own edit.");
        }

        [TestMethod]
        public void EveryConsumerThatMattered_StillListensToCustomDataChanged()
        {
            // If any of these stops subscribing, the removal above starts losing updates.
            StringAssert.Contains(
                ReadRepoFile("source", "ViewModels", "OverviewViewModel.cs"),
                "_gameCustomDataStore.CustomDataChanged += OnCustomDataChanged",
                "The overview's per-game fragment path is how an edit reaches a visible overview.");
            StringAssert.Contains(
                ReadRepoFile("source", "Services", "Achievements", "AchievementDataService.cs"),
                "_gameCustomDataStore.CustomDataChanged += OnCustomDataChangedForOverview",
                "The summary memo has to drop the rows the edit changed.");
            StringAssert.Contains(
                ReadRepoFile("source", "Services", "Library", "LibraryProjectionService.cs"),
                "_customDataStore.CustomDataChanged += OnCustomDataChangedForProjection",
                "The library projection still invalidates -- on the filtered path, so a " +
                "reorder-only edit no longer discards it.");
            StringAssert.Contains(
                ReadRepoFile("source", "PlayniteAchievementsPlugin.cs"),
                "_gameCustomDataStore.CustomDataChanged += GameCustomDataStore_CustomDataChanged",
                "Start-page invalidation, tag sync and the theme fan-out hang off this one.");
        }

        [TestMethod]
        public void TheProjection_StillFiltersOnWhatCanMoveALibraryRollup()
        {
            var source = ReadRepoFile("source", "Services", "Library", "LibraryProjectionService.cs");
            var handler = Between(
                source,
                "private void OnCustomDataChangedForProjection(",
                "private void OnPersistedSettingsChanged(");

            StringAssert.Contains(
                handler,
                "!e.AffectsSummaryData",
                "This filter is what makes the CustomDataChanged path cheaper than the cache " +
                "invalidation it replaced: a reorder cannot move anything the projection derives.");
        }

        private static string Between(string source, string start, string end)
        {
            var startIndex = source.IndexOf(start, StringComparison.Ordinal);
            Assert.IsTrue(startIndex >= 0, $"Could not find '{start}'.");

            var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
            Assert.IsTrue(endIndex > startIndex, $"Could not find '{end}' after '{start}'.");

            return source.Substring(startIndex, endIndex - startIndex);
        }

        private static string ReadManageViewModel()
        {
            return ReadRepoFile(
                "source", "ViewModels", "ManageAchievements", "ManageAchievementsViewModel.cs");
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

            Assert.Fail($"Could not find {string.Join(Path.DirectorySeparatorChar.ToString(), parts)}.");
            return null;
        }
    }
}
