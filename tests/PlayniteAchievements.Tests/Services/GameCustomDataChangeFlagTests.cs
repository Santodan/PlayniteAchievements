using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.IO;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// The change event's flag lets a writer that cannot have moved the summary data say so, and
    /// consumers skip a rebuild on that word. These pin the flag to what the caller passed.
    /// </summary>
    [TestClass]
    public class GameCustomDataChangeFlagTests
    {
        [TestMethod]
        public void NotifyChanged_DefaultsToSignificant()
        {
            RunWithStore((store, gameId) =>
            {
                GameCustomDataChangedEventArgs observed = null;
                store.CustomDataChanged += (_, args) => observed = args;

                store.NotifyChanged(gameId);

                Assert.IsNotNull(observed);
                Assert.IsTrue(
                    observed.AffectsSummaryData,
                    "A caller that says nothing must be treated as significant.");
            });
        }

        [TestMethod]
        public void NotifyChanged_CarriesAReorderOnlyOptOut()
        {
            RunWithStore((store, gameId) =>
            {
                GameCustomDataChangedEventArgs observed = null;
                store.CustomDataChanged += (_, args) => observed = args;

                store.NotifyChanged(gameId, affectsSummaryData: false);

                Assert.IsNotNull(observed, "The change event did not reach the subscriber.");
                Assert.AreEqual(gameId, observed.PlayniteGameId);
                Assert.IsFalse(observed.AffectsSummaryData);
            });
        }

        [TestMethod]
        public void NotifyChanged_IgnoresAnEmptyGameId()
        {
            RunWithStore((store, _) =>
            {
                var raised = false;
                store.CustomDataChanged += (_, __) => raised = true;

                store.NotifyChanged(Guid.Empty);

                Assert.IsFalse(raised, "There is no game to invalidate for an empty id.");
            });
        }

        private static void RunWithStore(Action<GameCustomDataStore, Guid> body)
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                body(
                    new GameCustomDataStore(Path.Combine(tempDirectory, "store")),
                    Guid.NewGuid());
            }
            finally
            {
                try { Directory.Delete(tempDirectory, true); } catch { }
            }
        }
    }
}
