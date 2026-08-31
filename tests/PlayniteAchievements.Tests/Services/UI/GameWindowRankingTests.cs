using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services.Tests.UI
{
    /// <summary>
    /// The launcher shapes this ranking exists to get right. Each is named after the real structure
    /// it came from, because two earlier fixes failed by reasoning about a shape that was never
    /// verified.
    /// </summary>
    [TestClass]
    public class GameWindowRankingTests
    {
        private static readonly DateTime LauncherStart = new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime GameStart = LauncherStart.AddSeconds(8);

        private static GameWindowCandidate Candidate(
            int hwnd,
            int processTreeDepth,
            GameWindowEvidence evidence,
            DateTime? processStartUtc = null,
            DateTime? firstSeenUtc = null)
        {
            return new GameWindowCandidate(
                new IntPtr(hwnd),
                processTreeDepth,
                evidence,
                processStartUtc ?? GameStart,
                firstSeenUtc ?? GameStart);
        }

        // === Steam-wrapped: Playnite starts steam.exe, which renders its own UI from a deeper
        // process than the game it launches. Measured on a real machine: steam.exe at depth 0,
        // steamwebhelper.exe at depth 2, a game steam.exe starts at depth 1. Depth alone would
        // therefore pick Steam's own window, which is why evidence has to outrank depth.

        private static GameWindowCandidate SteamClientWindow()
        {
            return Candidate(1, 0, GameWindowEvidence.AttributedProcess, LauncherStart, LauncherStart);
        }

        private static GameWindowCandidate SteamWebHelperWindow()
        {
            return Candidate(2, 2, GameWindowEvidence.AttributedProcess, LauncherStart, LauncherStart);
        }

        private static GameWindowCandidate SteamLaunchedGameWindow()
        {
            return Candidate(3, 1, GameWindowEvidence.InstallDirectory);
        }

        // === Shenmue I & II: the game-picker launcher and both games live under one install folder,
        // so evidence ties and only tree depth separates the picker from the game.

        private static GameWindowCandidate ShenmuePickerWindow()
        {
            return Candidate(10, 1, GameWindowEvidence.InstallDirectory, LauncherStart, LauncherStart);
        }

        private static GameWindowCandidate ShenmueGameWindow()
        {
            return Candidate(11, 2, GameWindowEvidence.InstallDirectory);
        }

        [TestMethod]
        public void SteamShape_PrefersTheGameOverTheClientAndItsDeeperHelper()
        {
            var client = SteamClientWindow();
            var helper = SteamWebHelperWindow();
            var game = SteamLaunchedGameWindow();

            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { client, helper, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, helper, client }).Hwnd);
        }

        [TestMethod]
        public void SteamShape_DepthAloneMustNotWin()
        {
            // The helper is deeper than the game. If depth outranked evidence, clips would show the
            // Steam client instead of the game.
            var helper = SteamWebHelperWindow();
            var game = SteamLaunchedGameWindow();

            Assert.IsTrue(helper.ProcessTreeDepth > game.ProcessTreeDepth);
            Assert.IsTrue(GameWindowRanking.ShouldReplace(helper, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, helper));
        }

        [TestMethod]
        public void ShenmueShape_DepthSeparatesThePickerFromTheGame()
        {
            var picker = ShenmuePickerWindow();
            var game = ShenmueGameWindow();

            Assert.AreEqual(game.Evidence, picker.Evidence);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { picker, game }).Hwnd);
            Assert.AreEqual(game.Hwnd, GameWindowRanking.SelectBest(new[] { game, picker }).Hwnd);
            Assert.IsTrue(GameWindowRanking.ShouldReplace(picker, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, picker));
        }

        [TestMethod]
        public void LauncherAlone_IsStillChosen()
        {
            // The state at capture start: the game has no window yet, so the launcher is the best
            // answer available and capture must not be skipped.
            var picker = ShenmuePickerWindow();

            Assert.AreEqual(picker.Hwnd, GameWindowRanking.SelectBest(new[] { picker }).Hwnd);
            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), picker));
        }

        [TestMethod]
        public void DirectExeAndEmulator_ResolveAtDepthZero()
        {
            // Nothing wraps these: the process Playnite started owns the game window.
            var directGame = Candidate(20, 0, GameWindowEvidence.InstallDirectory);
            var emulator = Candidate(21, 0, GameWindowEvidence.AttributedProcess);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), directGame));
            Assert.IsTrue(GameWindowRanking.ShouldReplace(default(GameWindowCandidate), emulator));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(directGame, emulator));
        }

        [TestMethod]
        public void BrokenProcessChain_StillPrefersTheInstallDirectoryProcess()
        {
            // A library behind a junction, or a launcher that exited: depth degrades to 0, and
            // install-directory evidence is what keeps the game ahead of a deeper launcher process.
            var game = Candidate(30, 0, GameWindowEvidence.InstallDirectory);
            var deeperLauncher = Candidate(
                31, 3, GameWindowEvidence.AttributedProcess, LauncherStart, LauncherStart);

            Assert.IsTrue(GameWindowRanking.ShouldReplace(deeperLauncher, game));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(game, deeperLauncher));
        }

        [TestMethod]
        public void TwoWindowsOfOneProcess_PreferTheLaterOne()
        {
            var splash = Candidate(40, 2, GameWindowEvidence.InstallDirectory, firstSeenUtc: GameStart);
            var render = Candidate(
                41, 2, GameWindowEvidence.InstallDirectory, firstSeenUtc: GameStart.AddSeconds(4));

            Assert.AreEqual(render.Hwnd, GameWindowRanking.SelectBest(new[] { splash, render }).Hwnd);
            Assert.IsTrue(GameWindowRanking.ShouldReplace(splash, render));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(render, splash));
        }

        [TestMethod]
        public void IndistinguishableWindows_LeaveTheIncumbentInPlace()
        {
            // Every swap tears down a running capture and costs a segment boundary, so a lateral
            // move must never happen.
            var current = Candidate(50, 2, GameWindowEvidence.InstallDirectory);
            var rival = Candidate(51, 2, GameWindowEvidence.InstallDirectory);

            Assert.IsFalse(GameWindowRanking.ShouldReplace(current, rival));
            Assert.IsFalse(GameWindowRanking.ShouldReplace(rival, current));
        }

        [TestMethod]
        public void EmptyCandidates_AreNeverTaken()
        {
            Assert.IsFalse(GameWindowRanking.ShouldReplace(
                ShenmueGameWindow(), default(GameWindowCandidate)));
            Assert.IsTrue(GameWindowRanking.SelectBest(new GameWindowCandidate[0]).IsEmpty);
            Assert.IsTrue(GameWindowRanking.SelectBest(null).IsEmpty);
        }
    }
}
