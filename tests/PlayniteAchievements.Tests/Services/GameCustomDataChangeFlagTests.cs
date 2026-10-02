using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.IO;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// The change event's flags let a writer that cannot have moved the override mirror or the
    /// summary data say so, and consumers skip a store load and a write-connection query on that
    /// word. The raise site dropped AffectsOverrideMirror by building the args without it, so the
    /// opt-out silently did nothing; these pin both flags to what the caller passed.
    /// </summary>
    [TestClass]
    public class GameCustomDataChangeFlagTests
    {
        [TestMethod]
        public void NotifyChanged_CarriesBothFlagsToSubscribers()
        {
            RunWithStore((store, gameId) =>
            {
                GameCustomDataChangedEventArgs observed = null;
                store.CustomDataChanged += (_, args) => observed = args;

                store.NotifyChanged(gameId, affectsSummaryData: true, affectsOverrideMirror: false);

                Assert.IsNotNull(observed, "The change event did not reach the subscriber.");
                Assert.AreEqual(gameId, observed.PlayniteGameId);
                Assert.IsTrue(observed.AffectsSummaryData);
                Assert.IsFalse(
                    observed.AffectsOverrideMirror,
                    "A capstone-style edit opts out of the mirror resync; forwarding true makes " +
                    "every consumer pay the clone and the write-connection query anyway.");
            });
        }

        [TestMethod]
        public void NotifyChanged_DefaultsBothFlagsToSignificant()
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
                Assert.IsTrue(observed.AffectsOverrideMirror);
            });
        }

        [TestMethod]
        public void NotifyChanged_CarriesAReorderOnlyOptOut()
        {
            RunWithStore((store, gameId) =>
            {
                GameCustomDataChangedEventArgs observed = null;
                store.CustomDataChanged += (_, args) => observed = args;

                store.NotifyChanged(gameId, affectsSummaryData: false, affectsOverrideMirror: false);

                Assert.IsNotNull(observed);
                Assert.IsFalse(observed.AffectsSummaryData);
                Assert.IsFalse(observed.AffectsOverrideMirror);
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
