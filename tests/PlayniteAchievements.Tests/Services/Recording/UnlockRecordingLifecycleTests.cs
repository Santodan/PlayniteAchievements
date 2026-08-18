using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace PlayniteAchievements.Services.Tests.Recording
{
    [TestClass]
    public class UnlockRecordingLifecycleTests
    {
        [TestMethod]
        public void SessionShutdown_DrainsClipWithoutCancellingItsPendingToastTrack()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private async Task ShutdownSessionAsync", StringComparison.Ordinal);
            var end = source.IndexOf("// === Unlock handling ===", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var shutdown = source.Substring(start, end - start);
            StringAssert.Contains(shutdown, "Task.WhenAll(inFlight)");
            Assert.IsFalse(shutdown.Contains("TrackTcs?.TrySetResult(null)"),
                "Stopping a game must not discard an overlay track that is still rendering.");
        }

        [TestMethod]
        public void GameStart_GatesCaptureBeforeBuildingASession()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("public void OnGameStarted", StringComparison.Ordinal);
            var session = source.IndexOf("var session = new CaptureSession", start, StringComparison.Ordinal);
            var gate = source.IndexOf("ShouldCaptureGame(game, persisted", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && session > start);
            Assert.IsTrue(gate > start && gate < session,
                "Capture must be gated before a session, its buffer and its recorders are created.");
        }

        [TestMethod]
        public void CaptureGate_ChecksBothExclusionAndProviderCapability()
        {
            var source = File.ReadAllText(FindRepoFile(
                "source", "Services", "Recording", "UnlockRecordingService.cs"));
            var start = source.IndexOf("private bool ShouldCaptureGame", StringComparison.Ordinal);
            var end = source.IndexOf("public void OnGameStopped", start, StringComparison.Ordinal);

            Assert.IsTrue(start >= 0 && end > start);
            var gate = source.Substring(start, end - start);

            // A game the user excluded from refreshes must not be captured either: the exclusion is
            // the user saying the plugin should leave that game alone.
            StringAssert.Contains(gate, "GetExcludedRefreshGameIds");
            // And a game no enabled provider can service can never report an unlock, so a clip for
            // it can never be requested.
            StringAssert.Contains(gate, "_isAnyProviderCapable");
            // The capability delegate is optional, so missing wiring must not silently kill capture.
            StringAssert.Contains(gate, "_isAnyProviderCapable != null");
        }

        private static string FindRepoFile(params string[] parts)
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null)
            {
                var path = directory.FullName;
                foreach (var part in parts)
                {
                    path = Path.Combine(path, part);
                }

                if (File.Exists(path))
                {
                    return path;
                }

                directory = directory.Parent;
            }

            Assert.Fail("Repository file not found: " + Path.Combine(parts));
            return null;
        }
    }
}
