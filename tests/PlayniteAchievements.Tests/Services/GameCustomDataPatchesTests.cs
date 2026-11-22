using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The library editor writes a game's lists from a page of its rows, so each patch changes
    /// only the achievements it names and keeps every other entry as stored.
    /// </summary>
    [TestClass]
    public class GameCustomDataPatchesTests
    {
        [TestMethod]
        public void SetOverrideField_KeepsTheEntriesItWasNotGiven()
        {
            var data = WithOverrides(
                ("a", new AchievementOverride { Category = "Story" }),
                ("b", new AchievementOverride { Category = "Side", Note = "keep" }),
                ("c", new AchievementOverride { Category = "Extra" }));

            GameCustomDataPatches.SetOverrideField(
                data,
                new Dictionary<string, string> { ["a"] = "Main", ["b"] = null, ["d"] = "New" },
                (entry, value) => entry.Category = value);

            Assert.AreEqual("Main", data.AchievementOverrides["a"].Category);
            Assert.IsNull(data.AchievementOverrides["b"].Category, "a null value clears the field");
            Assert.AreEqual("keep", data.AchievementOverrides["b"].Note, "the record's other fields stay");
            Assert.AreEqual("Extra", data.AchievementOverrides["c"].Category, "an achievement not named stays");
            Assert.AreEqual("New", data.AchievementOverrides["d"].Category);
        }

        [TestMethod]
        public void SetOverrideField_DropsARecordLeftEmpty()
        {
            var data = WithOverrides(("a", new AchievementOverride { Category = "Story" }));

            GameCustomDataPatches.SetOverrideField(
                data,
                new Dictionary<string, string> { ["a"] = " ", ["z"] = null },
                (entry, value) => entry.Category = value);

            Assert.IsNull(data.AchievementOverrides, "nothing left to store");
        }

        [TestMethod]
        public void SetTagOverrides_NullTakesTheProvidersTags_EmptyRemovesEvery()
        {
            var data = WithOverrides(
                ("a", new AchievementOverride { Tags = "Missable", Note = "n" }),
                ("b", new AchievementOverride { Tags = "Missable" }));

            GameCustomDataPatches.SetTagOverrides(
                data,
                new Dictionary<string, string> { ["a"] = null, ["c"] = string.Empty });

            Assert.IsNull(data.AchievementOverrides["a"].Tags);
            Assert.IsFalse(data.AchievementOverrides["a"].ClearTags);
            Assert.AreEqual("Missable", data.AchievementOverrides["b"].Tags, "an achievement not named stays");
            Assert.IsTrue(data.AchievementOverrides["c"].ClearTags, "empty is an override that removes every tag");
        }

        [TestMethod]
        public void RemoveOverrides_DropsOnlyTheNamedRecords()
        {
            var data = WithOverrides(
                ("a", new AchievementOverride { Note = "x" }),
                ("b", new AchievementOverride { Note = "y" }));

            GameCustomDataPatches.RemoveOverrides(data, new[] { " A " });

            Assert.IsFalse(data.AchievementOverrides.ContainsKey("a"));
            Assert.AreEqual("y", data.AchievementOverrides["b"].Note);
        }

        [TestMethod]
        public void SetMembership_AddsAndRemovesNamedEntries_KeepingTheRestInPlace()
        {
            var result = GameCustomDataPatches.SetMembership(
                new[] { "a", "b", "c" },
                new Dictionary<string, bool> { ["b"] = false, ["d"] = true, ["a"] = true });

            CollectionAssert.AreEqual(new[] { "a", "c", "d" }, result);
        }

        [TestMethod]
        public void SetMembership_IsNull_WhenNothingIsLeft()
        {
            Assert.IsNull(GameCustomDataPatches.SetMembership(
                new[] { "a" },
                new Dictionary<string, bool> { ["a"] = false }));
        }

        private static GameCustomDataFile WithOverrides(params (string ApiName, AchievementOverride Entry)[] entries)
        {
            var overrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase);
            foreach (var (apiName, entry) in entries)
            {
                overrides[apiName] = entry;
            }

            return new GameCustomDataFile { AchievementOverrides = overrides };
        }
    }
}
