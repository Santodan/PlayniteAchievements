using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests.UI
{
    [TestClass]
    public class GameWindowRankingTests
    {
        private const long SmallWindow = 640 * 480;
        private const long GameWindow = 1920 * 1080;

        private static GameWindowCandidate Candidate(
            int hwnd,
            GameWindowEvidence evidence,
            bool isForeground = false,
            long clientArea = GameWindow)
        {
            return new GameWindowCandidate(new IntPtr(hwnd), evidence, isForeground, clientArea);
        }

        [TestMethod]
        public void SelectBest_PrefersTheGameWindowOverTheLaunchers_WhicheverIsEnumeratedFirst()
        {
            var launcher = Candidate(1, GameWindowEvidence.StartedProcess);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory);

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { launcher, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, launcher }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_KeepsAForegroundLauncherBelowABackgroundGameWindow()
        {
            // The report this ranking exists for: the launcher is what the user is looking at while
            // the game loads, and it must still not become the capture target.
            var launcher = Candidate(1, GameWindowEvidence.StartedProcess, isForeground: true);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory);

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { launcher, game }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_PrefersTheLargerWindowAtEqualEvidence()
        {
            var dialog = Candidate(1, GameWindowEvidence.InstallDirectory, clientArea: SmallWindow);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory, clientArea: GameWindow);

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, dialog }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { dialog, game }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_IsEmptyForNoCandidates()
        {
            Assert.IsTrue(GameWindowRanking.SelectBest(new GameWindowCandidate[0]).IsEmpty);
            Assert.IsTrue(GameWindowRanking.SelectBest(null).IsEmpty);
        }

        [TestMethod]
        public void ShouldReplace_TakesAnythingWhenNothingIsKnown()
        {
            var launcher = Candidate(1, GameWindowEvidence.StartedProcess);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), launcher));
        }

        [TestMethod]
        public void ShouldReplace_PromotesTheLauncherWindowToTheGameWindow()
        {
            var launcher = Candidate(1, GameWindowEvidence.StartedProcess);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(launcher, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, launcher));
        }

        [TestMethod]
        public void ShouldReplace_RefusesAnEqualCandidateOfTheSameSize()
        {
            // Two equally plausible windows must not trade the target back and forth: each swap
            // tears down a running capture and costs a segment boundary.
            var current = Candidate(1, GameWindowEvidence.InstallDirectory);
            var rival = Candidate(2, GameWindowEvidence.InstallDirectory);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(current, rival));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(rival, current));
        }

        [TestMethod]
        public void ShouldReplace_TakesASubstantiallyLargerWindowAtEqualEvidence()
        {
            var dialog = Candidate(1, GameWindowEvidence.InstallDirectory, clientArea: SmallWindow);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory, clientArea: GameWindow);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(dialog, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, dialog));
        }

        [TestMethod]
        public void ShouldReplace_IgnoresAMarginalSizeDifference()
        {
            var current = Candidate(1, GameWindowEvidence.InstallDirectory, clientArea: 1000);
            var barelyBigger = Candidate(2, GameWindowEvidence.InstallDirectory, clientArea: 1400);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(current, barelyBigger));
        }

        [TestMethod]
        public void ShouldReplace_UpgradesTheEvidenceOfTheWindowAlreadyInUse()
        {
            // Same handle, stronger observation: nothing is retargeted, but the record must carry
            // the better evidence so the scan can settle.
            var known = Candidate(1, GameWindowEvidence.InstallDirectory);
            var confirmed = Candidate(1, GameWindowEvidence.InstallDirectory, isForeground: true);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(known, confirmed));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(confirmed, known));
        }

        [TestMethod]
        public void ShouldReplace_NeverTakesAnEmptyCandidate()
        {
            var game = Candidate(1, GameWindowEvidence.InstallDirectory);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, default(GameWindowCandidate)));
        }

        [TestMethod]
        public void IsConclusive_HoldsOnlyForAForegroundConfirmedInstallDirectoryWindow()
        {
            Assert.IsTrue(GameWindowRanking.IsConclusive(
                Candidate(1, GameWindowEvidence.InstallDirectory, isForeground: true)));

            Assert.IsFalse(GameWindowRanking.IsConclusive(
                Candidate(1, GameWindowEvidence.InstallDirectory)));
            Assert.IsFalse(GameWindowRanking.IsConclusive(
                Candidate(1, GameWindowEvidence.ProcessTree, isForeground: true)));
            Assert.IsFalse(GameWindowRanking.IsConclusive(default(GameWindowCandidate)));
        }

        [TestMethod]
        public void IsConclusive_NeverHoldsForAWindowOfTheStartedProcess()
        {
            // A launcher-wrapped game reaches us as the launcher's process. No observation of a
            // window at that tier may stop the search, or the launcher owns the whole session.
            Assert.IsFalse(GameWindowRanking.IsConclusive(
                Candidate(1, GameWindowEvidence.StartedProcess, isForeground: true)));
        }
    }
}
