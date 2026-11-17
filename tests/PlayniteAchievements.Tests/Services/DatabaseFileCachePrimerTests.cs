using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Database;
using System;
using System.IO;

namespace PlayniteAchievements.Services.Tests
{
    [TestClass]
    public class DatabaseFileCachePrimerTests
    {
        [TestMethod]
        public void PrimeIfDue_ReadsEveryExistingFileAndSkipsMissingOnes()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var database = Path.Combine(tempDir, "cache.db");
                var log = Path.Combine(tempDir, "cache.db-wal");
                File.WriteAllBytes(database, new byte[200000]);
                File.WriteAllBytes(log, new byte[1500]);

                var primer = new DatabaseFileCachePrimer(
                    null,
                    new[] { database, log, Path.Combine(tempDir, "missing.db"), null });

                Assert.AreEqual(201500, primer.PrimeIfDue());
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [TestMethod]
        public void PrimeIfDue_SkipsUntilTheIntervalHasPassed()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var database = Path.Combine(tempDir, "cache.db");
                File.WriteAllBytes(database, new byte[4096]);

                var now = TimeSpan.Zero;
                var primer = new DatabaseFileCachePrimer(
                    null,
                    new[] { database },
                    TimeSpan.FromMinutes(5),
                    () => now);

                Assert.AreEqual(4096, primer.PrimeIfDue());

                now = TimeSpan.FromMinutes(4);
                Assert.AreEqual(0, primer.PrimeIfDue());

                now = TimeSpan.FromMinutes(5);
                Assert.AreEqual(4096, primer.PrimeIfDue());
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [TestMethod]
        public void PrimeIfDue_ReadsAFileAnotherHandleHoldsOpenForWriting()
        {
            var tempDir = CreateTempDirectory();

            try
            {
                var database = Path.Combine(tempDir, "cache.db");
                File.WriteAllBytes(database, new byte[4096]);

                using (new FileStream(database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                    var primer = new DatabaseFileCachePrimer(null, new[] { database });

                    Assert.AreEqual(4096, primer.PrimeIfDue());
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        private static string CreateTempDirectory()
        {
            var path = Path.Combine(Path.GetTempPath(), "PlayAchPrimer_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
