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
