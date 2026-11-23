using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Tests.ViewModels
{
    /// <summary>
    /// A rebuild mints a fresh GameSummaryItem per game and the overview restores the selection by
    /// re-assigning it. The item carries no value equality, so the setter read that as a selection
    /// change and reran the whole load: a reported session logged 276 loads of one unchanged game,
    /// each also clearing that game's filters. The setter now adopts a same-game instance instead.
    ///
    /// The risk this creates is the reason these tests exist. The full-snapshot path used to get
    /// its selected-game reload for free from that same re-assignment, so suppressing it without
    /// replacing it would leave the right pane showing pre-refresh rows. OverviewViewModel is not
    /// linked into this project, so the invariants are pinned against the source the way the other
    /// wiring definition tests here do -- weaker than exercising the setter, but it does fail if
    /// the explicit reload is dropped in a later edit.
    /// </summary>
    [TestClass]
    public class OverviewSelectedGameReloadTests
    {
        [TestMethod]
        public void TheSetter_AdoptsARebuiltRowForTheSameGameWithoutReloading()
        {
            var source = ReadViewModel();

            StringAssert.Contains(
                source,
                "if (!ReferenceEquals(_selectedGame, value)",
                "The guard has to key on instance identity: the whole problem is that a rebuilt " +
                "row is a different instance for the same game.");
            StringAssert.Contains(
                source,
                "&& previousGameId == newGameId",
                "Only a same-game re-assignment may skip the load.");

            var shortCircuit = Between(
                source,
                "if (!ReferenceEquals(_selectedGame, value)",
                "if (SetValueAndReturn(ref _selectedGame, value))");
            StringAssert.Contains(
                shortCircuit,
                "RefreshSelectedGameHeaderCounts();",
                "The adopted row carries updated counts, so the header still has to be refreshed.");
            Assert.IsFalse(
                shortCircuit.Contains("_selectedGameControlBar.Reset"),
                "Resetting filters on a rebuild is what silently cleared the user's per-game " +
                "filters roughly twenty times a minute while they were editing.");
            Assert.IsFalse(
                shortCircuit.Contains("LoadSelectedGameAchievementsAndNotifyAsync"),
                "Skipping the redundant load is the point of the short circuit.");
        }

        [TestMethod]
        public void AFullSnapshot_StillAsksForTheSelectedGameReloadExplicitly()
        {
            var source = ReadViewModel();

            StringAssert.Contains(
                source,
                "_selectedGameReloadRequested = SelectedGame?.PlayniteGameId != null;",
                "A full snapshot replaces every game's rows, so the selected game's rows are " +
                "stale too. That reload used to happen as a side effect of the selection restore " +
                "re-assigning a new instance; it now has to be requested.");

            var applySnapshotTail = Between(
                source,
                "_selectedGameReloadRequested = SelectedGame?.PlayniteGameId != null;",
                "private bool IsTransientRebuildSnapshot");
            StringAssert.Contains(
                applySnapshotTail,
                "ReloadSelectedGameIfRequestedAsync()",
                "Requesting the reload is only half of it; the snapshot path has to run it.");
        }

        [TestMethod]
        public void EveryDataChange_ReloadsTheSummary_WhichReloadsTheSelectedGame()
        {
            var source = ReadViewModel();

            // A data change reaches the selected game's rows without going through the property
            // setter: each change queues one summary reload, and every applied summary reloads
            // the selected game in place.
            var handlers = Between(
                source,
                "private void OnAchievementDataChanged(object sender, AchievementDataChange e)",
                "private void QueueReload()");
            Assert.AreEqual(
                2,
                handlers.Split(new[] { "QueueReload();" }, StringSplitOptions.None).Length - 1,
                "The change feed, refresh saves included, and the summary-affecting custom data change each queue the reload.");
            Assert.IsFalse(source.Contains("CacheDeltaUpdated"), "refresh saves arrive on the change feed");
            StringAssert.Contains(handlers, "e.Touches(AchievementQuery.CurrentUser)");

            var tick = Between(source, "private async void OnReloadTimerTick(", "private void RecalculateOverviewStats()");
            StringAssert.Contains(tick, "await RefreshViewAsync();");
        }

        [TestMethod]
        public void TheReload_UpdatesRowsInPlace_AndKeepsTheSearch()
        {
            var source = ReadViewModel();

            // Every delta for the selected game reloads it. Handing the grid all-new instances
            // re-realized every row and stalled the UI for about a second per edit, and clearing
            // the search would wipe what the user typed.
            var reload = Between(
                source,
                "private async Task ReloadSelectedGameIfRequestedAsync()",
                "private void RemarkCapturePresence(");
            StringAssert.Contains(reload, "inPlace: true");

            var load = Between(
                source,
                "private async Task<bool> LoadSelectedGameAchievementsAsync(",
                "private bool IsSelectedGameLoadCurrent(");
            StringAssert.Contains(load, "bool inPlace = false");
            StringAssert.Contains(load, "if (!inPlace)");
            StringAssert.Contains(load, "items = MergeIntoShownSelectedGameRows(gameId, items);");
            StringAssert.Contains(
                load,
                "CollectionHelper.MergeByKey(",
                "The merge keeps each row's instance, keyed by ApiName.");
            StringAssert.Contains(load, "kept.UpdateFrom(source);");
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
            return File.ReadAllText(FindRepoFile("source", "ViewModels", "OverviewViewModel.cs"));
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
