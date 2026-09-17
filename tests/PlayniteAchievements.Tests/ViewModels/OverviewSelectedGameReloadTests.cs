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
                shortCircuit.Contains("_selectedGameControlBar.ResetFilters()"),
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
                "SyncRecentAchievementsDisplay();",
                "private bool IsTransientRebuildSnapshot");
            StringAssert.Contains(
                applySnapshotTail,
                "ReloadSelectedGameIfRequestedAsync()",
                "Requesting the reload is only half of it; the snapshot path has to run it.");
        }

        [TestMethod]
        public void APerGameDelta_StillReloadsWhenTheInPlaceRestampRefuses()
        {
            var source = ReadViewModel();

            // The delta path is the other half of the safety argument: a real data change for the
            // selected game reaches the rows without going through the property setter at all.
            StringAssert.Contains(
                source,
                "if (SelectedGame?.PlayniteGameId == gameId && !ApplySelectedGameIconOverrides(gameId))",
                "A delta for the selected game must still re-stamp its rows in place.");

            var deltaBranch = Between(
                source,
                "if (SelectedGame?.PlayniteGameId == gameId && !ApplySelectedGameIconOverrides(gameId))",
                "if (fragment.Achievements != null");
            StringAssert.Contains(
                deltaBranch,
                "_selectedGameReloadRequested = true;",
                "And when it cannot, it must fall back to a reload -- the path that keeps the " +
                "short circuit from swallowing a genuine data change.");
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
