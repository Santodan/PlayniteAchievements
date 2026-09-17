using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// Every editor mutation funnels through the store's save, which already computes the record
    /// before and after it. The write event hands both to a recorder so an edit can be reversed
    /// without the recorder loading anything itself.
    ///
    /// The flags matter as much as the records: a reversal has to report what the original write
    /// reported, or the mirrors keyed off them resync for the change and not for its undo.
    /// </summary>
    [TestClass]
    public class GameCustomDataWriteEventTests
    {
        [TestMethod]
        public void Update_RaisesWrittenWithBothRecords()
        {
            RunWithStore((store, gameId) =>
            {
                // The achievement order rather than the notes: notes are re-derived from the
                // override records on every normalize, so they are a projection of state and not
                // state itself, and a write straight to them does not survive the save.
                store.Update(gameId, data => data.AchievementOrder = new List<string> { "a", "b" });

                GameCustomDataWrittenEventArgs observed = null;
                store.CustomDataWritten += (_, args) => observed = args;

                store.Update(gameId, data => data.AchievementOrder = new List<string> { "b", "a" });

                Assert.IsNotNull(observed, "The write event did not reach the subscriber.");
                Assert.AreEqual(gameId, observed.PlayniteGameId);
                Assert.IsNotNull(observed.Previous, "The pre-image is what an undo restores.");
                Assert.IsNotNull(observed.Persisted);
                CollectionAssert.AreEqual(new[] { "a", "b" }, observed.Previous.AchievementOrder);
                CollectionAssert.AreEqual(new[] { "b", "a" }, observed.Persisted.AchievementOrder);
            });
        }

        [TestMethod]
        public void Update_CarriesTheCallersFlagsRatherThanTheDefaults()
        {
            RunWithStore((store, gameId) =>
            {
                GameCustomDataWrittenEventArgs observed = null;
                store.CustomDataWritten += (_, args) => observed = args;

                store.Update(
                    gameId,
                    data => data.AchievementNotes = new Dictionary<string, string> { ["a"] = "b" },
                    affectsSummaryData: true,
                    affectsOverrideMirror: false);

                Assert.IsNotNull(observed);
                Assert.IsTrue(observed.AffectsSummaryData);
                Assert.IsFalse(
                    observed.AffectsOverrideMirror,
                    "Forwarding the default instead of the caller's flag makes an undo resync " +
                    "work the original write opted out of.");
            });
        }

        [TestMethod]
        public void Update_RaisesWrittenBeforeChanged()
        {
            RunWithStore((store, gameId) =>
            {
                var sequence = new System.Collections.Generic.List<string>();
                store.CustomDataWritten += (_, __) => sequence.Add("written");
                store.CustomDataChanged += (_, __) => sequence.Add("changed");

                store.Update(gameId, data => data.AchievementNotes = new Dictionary<string, string> { ["a"] = "b" });

                CollectionAssert.AreEqual(
                    new[] { "written", "changed" },
                    sequence,
                    "The write has to be recorded before the subscribers that rebuild from it run.");
            });
        }

        [TestMethod]
        public void Save_WithoutAPreImage_ReportsTheStateBeforeAsUnknown()
        {
            RunWithStore((store, gameId) =>
            {
                var data = store.LoadOrDefault(gameId);
                data.AchievementNotes = new Dictionary<string, string> { ["a"] = "b" };

                GameCustomDataWrittenEventArgs observed = null;
                store.CustomDataWritten += (_, args) => observed = args;

                store.Save(gameId, data);

                Assert.IsNotNull(observed);
                Assert.IsNull(
                    observed.Previous,
                    "A null pre-image means the state before the write is unknown, which a " +
                    "recorder has to treat differently from an empty record.");
            });
        }

        [TestMethod]
        public void WriteSubscriberThatThrows_DoesNotFailTheWrite()
        {
            RunWithStore((store, gameId) =>
            {
                store.CustomDataWritten += (_, __) => throw new InvalidOperationException("recorder broke");

                store.Update(gameId, data => data.AchievementNotes = new Dictionary<string, string> { ["a"] = "b" });

                // The write already reached the repository by the time the event runs, so losing
                // the recording must not lose the edit.
                Assert.AreEqual("b", store.LoadOrDefault(gameId).AchievementNotes["a"]);
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
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                    // A leftover temp directory is not worth failing a test over.
                }
            }
        }
    }
}
