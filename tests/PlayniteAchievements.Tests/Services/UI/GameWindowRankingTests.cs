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

        private static readonly DateTime LauncherStart = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime GameStart = LauncherStart.AddSeconds(8);

        private static GameWindowCandidate Candidate(
            int hwnd,
            GameWindowEvidence evidence,
            bool isManagedUiShell = false,
            DateTime? processStartUtc = null,
            DateTime? firstSeenUtc = null,
            long clientArea = GameWindow)
        {
            return new GameWindowCandidate(
                new IntPtr(hwnd),
                evidence,
                isManagedUiShell,
                processStartUtc ?? GameStart,
                firstSeenUtc ?? GameStart,
                clientArea);
        }

        /// <summary>
        /// The shape behind the report: Playnite starts the store client, so the client's window is
        /// the only one that exists at first, and its process is the one Playnite started.
        /// </summary>
        private static GameWindowCandidate StoreClientWindow()
        {
            return Candidate(
                1,
                GameWindowEvidence.StartedProcess,
                processStartUtc: LauncherStart,
                firstSeenUtc: LauncherStart);
        }

        /// <summary>
        /// Shenmue I &amp; II: the game-picker launcher is a WinForms window living in the same
        /// install folder as both games, so evidence alone cannot separate it from the game.
        /// </summary>
        private static GameWindowCandidate ShenmuePickerWindow()
        {
            return Candidate(
                2,
                GameWindowEvidence.InstallDirectory,
                isManagedUiShell: true,
                processStartUtc: LauncherStart,
                firstSeenUtc: LauncherStart,
                clientArea: SmallWindow);
        }

        private static GameWindowCandidate GameRenderWindow(int hwnd = 3)
        {
            return Candidate(hwnd, GameWindowEvidence.InstallDirectory);
        }

        [TestMethod]
        public void SelectBest_PrefersTheGameOverTheStoreClient_WhicheverIsEnumeratedFirst()
        {
            var client = StoreClientWindow();
            var game = GameRenderWindow();

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { client, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, client }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_PrefersTheGameOverAPickerInTheSameInstallFolder()
        {
            var picker = ShenmuePickerWindow();
            var game = GameRenderWindow();

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { picker, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, picker }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_PrefersTheYoungerProcessWithinOneTier()
        {
            // A launcher's job is to start the game, so the game's process is always the younger.
            var launcher = Candidate(1, GameWindowEvidence.InstallDirectory, processStartUtc: LauncherStart);
            var game = Candidate(2, GameWindowEvidence.InstallDirectory, processStartUtc: GameStart);

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { launcher, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, launcher }).Hwnd);
        }

        [TestMethod]
        public void SelectBest_PrefersTheLaterWindowOfOneProcess()
        {
            // One process, two windows: the render window supersedes the splash.
            var splash = Candidate(1, GameWindowEvidence.InstallDirectory, firstSeenUtc: GameStart);
            var render = Candidate(2, GameWindowEvidence.InstallDirectory, firstSeenUtc: GameStart.AddSeconds(4));

            Assert.AreEqual(render.Hwnd, GameWindowRanking.SelectBest(new[] { splash, render }).Hwnd);
            Assert.AreEqual(render.Hwnd, GameWindowRanking.SelectBest(new[] { render, splash }).Hwnd);
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
            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), StoreClientWindow()));
        }

        [TestMethod]
        public void ShouldReplace_PromotesFromTheStoreClientToTheGame()
        {
            var client = StoreClientWindow();
            var game = GameRenderWindow();

            Assert.IsTrue(GameWindowRanking.ShouldReplace(client, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, client));
        }

        [TestMethod]
        public void ShouldReplace_PromotesFromTheShenmuePickerToTheGame()
        {
            // The case the first fix missed: same evidence tier, and the picker had focus while the
            // game started. Nothing about it may hold the target once the game has a window.
            var picker = ShenmuePickerWindow();
            var game = GameRenderWindow();

            Assert.IsTrue(GameWindowRanking.ShouldReplace(picker, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, picker));
        }

        [TestMethod]
        public void ShouldReplace_NeverFallsBackToAManagedShellFromARealGameWindow()
        {
            // A crash reporter or settings dialog opening mid-session must not steal the target,
            // even though its process and window are both younger than the game's.
            var game = GameRenderWindow();
            var dialogOpenedLater = Candidate(
                9,
                GameWindowEvidence.InstallDirectory,
                isManagedUiShell: true,
                processStartUtc: GameStart.AddMinutes(20),
                firstSeenUtc: GameStart.AddMinutes(20),
                clientArea: SmallWindow);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, dialogOpenedLater));
        }

        [TestMethod]
        public void ShouldReplace_RefusesAnIndistinguishableRivalOfTheSameSize()
        {
            // Two equally plausible windows must not trade the target back and forth: each swap
            // tears down a running capture and costs a segment boundary.
            var current = GameRenderWindow(1);
            var rival = GameRenderWindow(2);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(current, rival));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(rival, current));
        }

        [TestMethod]
        public void ShouldReplace_UsesSizeOnlyWhenNothingElseSeparatesThem()
        {
            var small = Candidate(1, GameWindowEvidence.InstallDirectory, clientArea: SmallWindow);
            var large = Candidate(2, GameWindowEvidence.InstallDirectory, clientArea: GameWindow);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(small, large));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(large, small));
        }

        [TestMethod]
        public void ShouldReplace_IgnoresAMarginalSizeDifference()
        {
            var current = Candidate(1, GameWindowEvidence.InstallDirectory, clientArea: 1000);
            var barelyBigger = Candidate(2, GameWindowEvidence.InstallDirectory, clientArea: 1400);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(current, barelyBigger));
        }

        [TestMethod]
        public void ShouldReplace_LetsWeakEvidenceWinOnlyWhenItIsTheOnlyCandidate()
        {
            // An emulator's window carries only started-process evidence; with nothing better
            // available it is still the right answer, and it must not be displaced by a store
            // client window of the same tier that appeared earlier.
            var emulator = Candidate(1, GameWindowEvidence.StartedProcess);
            var olderSameTier = Candidate(
                2,
                GameWindowEvidence.StartedProcess,
                processStartUtc: LauncherStart,
                firstSeenUtc: LauncherStart);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), emulator));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(emulator, olderSameTier));
        }

        [TestMethod]
        public void ShouldReplace_NeverTakesAnEmptyCandidate()
        {
            Assert.IsFalse(GameWindowRanking.ShouldReplace(GameRenderWindow(), default(GameWindowCandidate)));
        }

        [TestMethod]
        public void ShouldReplace_IsNotDecidedByFocus()
        {
            // Focus is not represented in a candidate at all. It cannot be: a launcher holds focus
            // exactly while the game is starting, which is when the target is first chosen.
            var picker = ShenmuePickerWindow();
            var game = GameRenderWindow();

            // Same inputs regardless of which of the two the user happens to be looking at.
            Assert.IsTrue(GameWindowRanking.ShouldReplace(picker, game));
        }
    }
}
