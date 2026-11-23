using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Tests
{
    /// <summary>
    /// A batch composes each game's updates into one write, written for every game together when
    /// the scope ends, with each game's events raised once.
    /// </summary>
    [TestClass]
    [DoNotParallelize]
    public class GameCustomDataStoreBatchTests
    {
        [TestMethod]
        public void Updates_InABatch_ComposePerGame_AndRaiseOnceEach_WhenTheScopeEnds()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "PA_BatchTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var gameA = Guid.NewGuid();
            var gameB = Guid.NewGuid();

            try
            {
                var store = new GameCustomDataStore(tempDir);
                var written = new List<GameCustomDataWrittenEventArgs>();
                var changed = new List<Guid>();
                store.CustomDataWritten += (_, args) => written.Add(args);
                store.CustomDataChanged += (_, args) => changed.Add(args.PlayniteGameId);

                using (store.BeginBatch())
                {
                    store.Update(gameA, data => data.GoalAchievementApiNames = new List<string> { "first" });
                    store.Update(gameB, data => data.FilteredAchievementApiNames = new List<string> { "b1" });
                    store.Update(gameA, data => data.FilteredAchievementApiNames = new List<string> { "a1" });

                    Assert.AreEqual(0, written.Count, "nothing is written inside the scope");
                    Assert.IsFalse(store.TryLoad(gameA, out _), "reads see the batch once it ends");

                    using (store.BeginBatch())
                    {
                        store.Update(gameB, data => data.GoalAchievementApiNames = new List<string> { "nested" });
                    }

                    Assert.AreEqual(0, written.Count, "a nested scope is part of the outer one");
                }

                CollectionAssert.AreEqual(new[] { gameA, gameB }, written.Select(args => args.PlayniteGameId).ToList());
                CollectionAssert.AreEqual(new[] { gameA, gameB }, changed);

                Assert.IsTrue(store.TryLoad(gameA, out var a));
                CollectionAssert.AreEqual(new[] { "first" }, a.GoalAchievementApiNames, "the first update stays under the second");
                CollectionAssert.AreEqual(new[] { "a1" }, a.FilteredAchievementApiNames);

                Assert.IsTrue(store.TryLoad(gameB, out var b));
                CollectionAssert.AreEqual(new[] { "nested" }, b.GoalAchievementApiNames);
                CollectionAssert.AreEqual(new[] { "b1" }, b.FilteredAchievementApiNames);

                store.Update(gameA, data => data.GoalAchievementApiNames = new List<string> { "after" });
                Assert.AreEqual(3, written.Count, "outside a scope an update writes at once");
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
        }
    }
}
