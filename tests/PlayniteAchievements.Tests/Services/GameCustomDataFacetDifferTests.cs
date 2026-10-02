using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// The differ turns a write into the list of things it moved, which is what an undo entry
    /// holds. Two properties matter most: it must not miss a change, or the edit cannot be
    /// reversed, and it must not report one that did not happen, or an undo reaches further than
    /// the edit did.
    /// </summary>
    [TestClass]
    public class GameCustomDataFacetDifferTests
    {
        [TestMethod]
        public void IdenticalRecords_YieldNoPatches()
        {
            var before = new GameCustomDataFile
            {
                AchievementOrder = new List<string> { "a", "b" }
            };

            var patches = GameCustomDataFacetDiffer.Diff(before, before.Clone());

            Assert.AreEqual(0, patches.Count, "A write that changed nothing is not an undo step.");
        }

        [TestMethod]
        public void AnOrderChange_YieldsOnlyTheOrderFacet()
        {
            var before = new GameCustomDataFile { AchievementOrder = new List<string> { "a", "b" } };
            var after = new GameCustomDataFile { AchievementOrder = new List<string> { "b", "a" } };

            var patches = GameCustomDataFacetDiffer.Diff(before, after);

            CollectionAssert.AreEqual(
                new[] { GameCustomDataFacet.AchievementOrder },
                patches.Select(patch => patch.Facet).ToArray());
        }

        [TestMethod]
        public void ANoteChange_YieldsTheOverrideFacetAndNotTheProjectedMaps()
        {
            // Notes, and the two icon maps beside them, are re-derived from the override records
            // whenever the store normalizes. Recording them as well would journal three copies of
            // one change, and restoring them would be undone by the next normalize.
            var before = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = new AchievementOverride { Note = "before" }
                },
                AchievementNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = "before"
                }
            };
            var after = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = new AchievementOverride { Note = "after" }
                },
                AchievementNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = "after"
                }
            };

            var patches = GameCustomDataFacetDiffer.Diff(before, after);

            CollectionAssert.AreEqual(
                new[] { GameCustomDataFacet.AchievementOverrides },
                patches.Select(patch => patch.Facet).ToArray(),
                "Only the override record is state; the note map is a projection of it.");
        }

        [TestMethod]
        public void AMapHoldingTheSameEntriesInAnotherOrder_IsNotAChange()
        {
            // The assignment maps are rebuilt from the rows on every write, so their insertion
            // order is incidental. Comparing them as serialized state would call a reordering a
            // change and record an undo step for a write that moved nothing.
            var before = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = new AchievementOverride { Points = 10 },
                    ["two"] = new AchievementOverride { Points = 20 }
                }
            };
            var after = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["two"] = new AchievementOverride { Points = 20 },
                    ["one"] = new AchievementOverride { Points = 10 }
                }
            };

            Assert.AreEqual(0, GameCustomDataFacetDiffer.Diff(before, after).Count);
        }

        [TestMethod]
        public void AListReordered_IsAChange()
        {
            // Unlike a map, position is part of what a list means here.
            var before = new GameCustomDataFile { AchievementOrder = new List<string> { "a", "b" } };
            var after = new GameCustomDataFile { AchievementOrder = new List<string> { "b", "a" } };

            Assert.AreEqual(1, GameCustomDataFacetDiffer.Diff(before, after).Count);
        }

        [TestMethod]
        public void AFieldAddedToAnOverride_IsStillNoticed()
        {
            // Compared as serialized values per entry, so a field added to the override record
            // later is covered without this differ being updated. A hand-written field-by-field
            // comparison is what would quietly stop noticing.
            var before = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = new AchievementOverride { Note = "same", TrophyType = "gold" }
                }
            };
            var after = new GameCustomDataFile
            {
                AchievementOverrides = new Dictionary<string, AchievementOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = new AchievementOverride { Note = "same", TrophyType = "silver" }
                }
            };

            CollectionAssert.AreEqual(
                new[] { GameCustomDataFacet.AchievementOverrides },
                GameCustomDataFacetDiffer.Diff(before, after).Select(patch => patch.Facet).ToArray());
        }

        [TestMethod]
        public void MaterializingCapstones_YieldsOneFacetCoveringTheFlag()
        {
            // The flag is what separates "no capstones" from "fall back to the provider", so it
            // has to travel with the list rather than as a facet of its own.
            var before = new GameCustomDataFile { CapstonesMaterialized = false, Capstones = null };
            var after = new GameCustomDataFile
            {
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment>()
            };

            var patches = GameCustomDataFacetDiffer.Diff(before, after);

            CollectionAssert.AreEqual(
                new[] { GameCustomDataFacet.Capstones },
                patches.Select(patch => patch.Facet).ToArray());
        }

        [TestMethod]
        public void ApplyBefore_RestoresTheFlagAlongsideTheList()
        {
            var before = new GameCustomDataFile { CapstonesMaterialized = false, Capstones = null };
            var after = new GameCustomDataFile
            {
                CapstonesMaterialized = true,
                Capstones = new List<CapstoneAssignment> { new CapstoneAssignment { ApiName = "one" } }
            };

            var patch = GameCustomDataFacetDiffer.Diff(before, after).Single();
            var target = after.Clone();
            patch.ApplyBefore(target);

            Assert.IsFalse(target.CapstonesMaterialized, "Restoring the list without the flag changes what an empty list means.");
            Assert.IsNull(target.Capstones);
        }

        [TestMethod]
        public void ApplyBeforeThenApplyAfter_RoundTripsTheFacet()
        {
            var before = new GameCustomDataFile
            {
                AchievementCategoryOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = "Combat"
                }
            };
            var after = new GameCustomDataFile
            {
                AchievementCategoryOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["one"] = "Exploration"
                }
            };

            var patch = GameCustomDataFacetDiffer.Diff(before, after).Single();
            var target = after.Clone();

            patch.ApplyBefore(target);
            Assert.AreEqual("Combat", target.AchievementCategoryOverrides["one"]);

            patch.ApplyAfter(target);
            Assert.AreEqual("Exploration", target.AchievementCategoryOverrides["one"]);
        }

        [TestMethod]
        public void APatch_DoesNotShareStateWithTheRecordsItCameFrom()
        {
            var before = new GameCustomDataFile { AchievementOrder = new List<string> { "a" } };
            var after = new GameCustomDataFile { AchievementOrder = new List<string> { "b" } };

            var patch = GameCustomDataFacetDiffer.Diff(before, after).Single();

            // The journal outlives the write, so a later mutation of either record must not reach
            // back into a recorded step.
            before.AchievementOrder.Add("mutated");
            after.AchievementOrder.Add("mutated");

            var target = new GameCustomDataFile();
            patch.ApplyBefore(target);
            CollectionAssert.AreEqual(new[] { "a" }, target.AchievementOrder);
        }

        [TestMethod]
        public void ChangesOutsideTheEditorsReach_AreNotReported()
        {
            // An undo must never reverse a write that came from another surface, so those members
            // are not facets at all.
            var before = new GameCustomDataFile { ExcludedFromRefreshes = false };
            var after = new GameCustomDataFile { ExcludedFromRefreshes = true };

            var patches = GameCustomDataFacetDiffer.Diff(before, after);

            Assert.AreEqual(0, patches.Count);
        }

        [TestMethod]
        public void ANullRecord_YieldsNoPatches()
        {
            // A write with no pre-image means the state before it is unknown, which the journal
            // has to handle as "cannot record" rather than as "nothing was there".
            Assert.AreEqual(0, GameCustomDataFacetDiffer.Diff(null, new GameCustomDataFile()).Count);
            Assert.AreEqual(0, GameCustomDataFacetDiffer.Diff(new GameCustomDataFile(), null).Count);
        }

        [TestMethod]
        public void ElementCount_ReflectsWhatThePatchHolds()
        {
            var before = new GameCustomDataFile { AchievementOrder = new List<string> { "a", "b" } };
            var after = new GameCustomDataFile { AchievementOrder = new List<string> { "a", "b", "c" } };

            var patch = GameCustomDataFacetDiffer.Diff(before, after).Single();

            Assert.AreEqual(5, patch.ElementCount, "Both sides are held, so both sides count toward the cap.");
        }
    }
}
