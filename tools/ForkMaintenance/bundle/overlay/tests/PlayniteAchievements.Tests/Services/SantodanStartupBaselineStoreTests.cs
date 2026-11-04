using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.InGameMonitoring;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class SantodanStartupBaselineStoreTests
    {
        [TestMethod]
        public void KnownPathOnlyBypassesPresenceDiscoveryWhenCacheAndSettingsStillMatch()
        {
            var folder = Path.Combine(Path.GetTempPath(), "startup-file-" + Guid.NewGuid());
            Directory.CreateDirectory(folder);
            try
            {
                var file = Path.Combine(folder, "achievements.json");
                File.WriteAllText(file, "{}");
                var game = Guid.NewGuid();
                var now = DateTime.UtcNow;
                var store = new SantodanStartupBaselineStore(folder);
                store.Save(game, 123, now, "source", now, "configuration", file);
                store = new SantodanStartupBaselineStore(folder);
                Assert.IsTrue(store.HasExistingAchievementFile(game, 123, now, "configuration", now.AddSeconds(1), now.AddSeconds(1)));
                Assert.IsFalse(store.HasExistingAchievementFile(game, 123, now, "changed-settings", now.AddSeconds(1), now.AddSeconds(1)));
                Assert.IsFalse(store.HasExistingAchievementFile(game, 123, now.AddTicks(1), "configuration", now.AddSeconds(1), now.AddSeconds(1)));
                Assert.IsFalse(store.HasExistingAchievementFile(game, 123, now, "configuration", now.AddHours(25), now.AddHours(25)));
                // Presence is not progress validation: a changed source still forces refresh.
                File.WriteAllText(file, "{changed}");
                Assert.IsTrue(store.HasExistingAchievementFile(game, 123, now, "configuration", now.AddSeconds(1), now.AddSeconds(1)));
                Assert.IsFalse(store.Matches(game, 123, now, "changed-source", now.AddSeconds(1)));
                File.Delete(file);
                Assert.IsFalse(store.HasExistingAchievementFile(game, 123, now, "configuration", now.AddSeconds(1), now.AddSeconds(1)));
                store.Save(game, 123, now, "source", now); // An older receipt has no path hint.
                Assert.IsFalse(store.HasExistingAchievementFile(game, 123, now, "configuration", now.AddSeconds(1), now.AddSeconds(1)));
            }
            finally { Directory.Delete(folder, true); }
        }

        [TestMethod]
        public void ReceiptSurvivesRestartButRejectsSourceCacheAccountAndAgeChanges()
        {
            var folder = Path.Combine(Path.GetTempPath(), "startup-baseline-" + Guid.NewGuid());
            try
            {
                var game = Guid.NewGuid();
                var now = DateTime.UtcNow;
                var store = new SantodanStartupBaselineStore(folder);
                Assert.IsFalse(store.Matches(game, 123, now, "source", now));
                store.Save(game, 123, now, "source", now);
                store = new SantodanStartupBaselineStore(folder);
                Assert.IsTrue(store.Matches(game, 123, now, "source", now.AddMinutes(1)));
                Assert.IsFalse(store.Matches(game, 123, now, "changed-during-startup", now));
                Assert.IsFalse(store.Matches(game, 123, now.AddTicks(1), "source", now));
                Assert.IsFalse(store.Matches(game, 456, now, "source", now));
                Assert.IsFalse(store.Matches(Guid.NewGuid(), 123, now, "source", now));
                Assert.IsFalse(store.Matches(game, 123, now, "source", now.AddHours(25)));
                Assert.IsFalse(store.Matches(game, 123, now, "source", now.AddMinutes(-1)));
                Assert.IsFalse(store.Matches(game, 123, now, null, now));
                Assert.IsFalse(store.Matches(game, 123, now, "source", now.AddMinutes(1), now.AddSeconds(-1)),
                    "A refresh performed during the current game must not become its startup baseline.");
                Assert.IsTrue(store.Matches(game, 123, now, "source", now.AddMinutes(1), now.AddSeconds(1)));
                // Replacing a validation record is atomic and leaves no temporary files.
                store.Save(game, 123, now.AddSeconds(1), "new-source", now.AddSeconds(1));
                Assert.IsTrue(store.Matches(game, 123, now.AddSeconds(1), "new-source", now.AddSeconds(1)));
                Assert.AreEqual(1, Directory.GetFiles(Path.Combine(folder, "SantodanPollingBaselines")).Length);
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }

        [TestMethod]
        public void CorruptValidationRecordFallsBackInsteadOfTrustingCachedData()
        {
            var folder = Path.Combine(Path.GetTempPath(), "startup-baseline-" + Guid.NewGuid());
            try
            {
                var game = Guid.NewGuid();
                var now = DateTime.UtcNow;
                var store = new SantodanStartupBaselineStore(folder);
                store.Save(game, 123, now, "source", now);
                File.WriteAllText(Path.Combine(folder, "SantodanPollingBaselines", game.ToString("N") + ".json"), "{truncated");
                Assert.IsFalse(store.Matches(game, 123, now, "source", now));
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        }
    }
}
