using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// One edit in the Manage Achievements window raises a cache invalidation on top of the
    /// CustomDataChanged the store already raised for the same write.
    ///
    /// That used to be ruinous because the consumers dropped the scope those args carry, so each
    /// edit discarded the whole-library projection and forced a full summary re-read: a reported
    /// 35-minute editing session logged 245 of them. Both consumers now honour it --
    /// LibraryProjectionService defers its warm (pinned by LibraryProjectionScopeDefinitionTests)
    /// and AchievementDataService patches the summary memo per game rather than clearing it
    /// (pinned by OverviewSummaryMemoDefinitionTests). So the second raise is now cheap, and the
    /// reason to keep it is unchanged.
    ///
    /// Removing it is only safe while every consumer still hears about the edit on the
    /// CustomDataChanged path, which is also the path that filters on AffectsSummaryData. These
    /// pin both ends of that.
    /// </summary>
    [TestClass]
    public class ManageCustomDataInvalidationDefinitionTests
    {
        [TestMethod]
        public void AnEdit_RaisesTheScopedInvalidationTheOverviewNeeds()
        {
            var source = ReadManageViewModel();
            var core = Between(
                source,
                "private void NotifyCustomDataChangedCore(",
                "internal void NotifyIconOverridesChanged(");

            // This was once removed as redundant with the store's own CustomDataChanged. It is
            // not, and the reason is the flag: the editor's category, category-type, filter and
            // icon writes pass affectsSummaryData false, and OverviewViewModel.OnCustomDataChanged
            // returns early on exactly that. The scoped invalidation is the only thing that routes
            // those edits to the overview's per-game fragment path.
            StringAssert.Contains(
                core,
                "NotifyCacheInvalidated(new[] { _gameId })",
                "Scoped to this game -- without it, an editor category or icon edit reaches the " +
                "store and nothing on screen re-reads it.");
            StringAssert.Contains(
                core,
                "ScheduleShellReload();",
                "The window still reloads per edit -- that is what shows the user their own edit. " +
                "It is now deferred rather than synchronous: the CustomDataRevision bump on the " +
                "next line rehydrates the visible tab's snapshot in this same call stack, so a " +
                "reload that lands after it reads a warm snapshot instead of forcing its own " +
                "cold load on the UI thread (measured at 369ms per edit). Deferred, never " +
                "suppressed -- a self-write can still move the shell's totals.");

            // Ordering is the point: schedule, then bump. Reversed, the reload would fire before
            // the thing that warms the snapshot it reads.
            var schedule = core.IndexOf("ScheduleShellReload();", StringComparison.Ordinal);
            var bump = core.IndexOf("CustomDataRevision = unchecked(", StringComparison.Ordinal);
            Assert.IsTrue(
                schedule >= 0 && bump > schedule,
                "The shell reload must be scheduled before the revision bump.");
        }

        [TestMethod]
        public void TheOverviewStillDropsChangesThatClaimNoSummaryImpact()
        {
            // The other half of the contract above. If this filter ever goes, the scoped
            // invalidation becomes genuinely redundant -- and if the flag on those writes ever
            // becomes true, the projection stops deferring and the whole-library rebuilds come
            // back. Either way the pair has to be read together.
            var overview = ReadRepoFile("source", "ViewModels", "OverviewViewModel.cs");
            var handler = Between(
                overview,
                "private void OnCustomDataChanged(object sender, GameCustomDataChangedEventArgs e)",
                "private void QueueOverviewDelta(");

            StringAssert.Contains(handler, "!e.AffectsSummaryData");
            StringAssert.Contains(handler, "return;");
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
