using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>
    /// A single-game custom-data edit used to drop the whole hydrated summary memo, forcing the
    /// next read to re-run five unfiltered whole-library queries (638 game rows and every
    /// unlocked row in the library, 600-1800ms). The patcher replaces one game's contribution in
    /// place of that rebuild.
    ///
    /// The invariant that matters is that a patched result is indistinguishable from a full
    /// rebuild -- otherwise the overview quietly diverges from the database.
    /// </summary>
    [TestClass]
    public class OverviewSummaryPatcherTests
    {
        private static readonly Guid GameA = new Guid("11111111-1111-1111-1111-111111111111");
        private static readonly Guid GameB = new Guid("22222222-2222-2222-2222-222222222222");
        private static readonly Guid GameC = new Guid("33333333-3333-3333-3333-333333333333");

        private static readonly DateTime Day1 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Day2 = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// One game's contribution, in the shape the scoped read produces. Building the base from
        /// the same helper that builds the slice is what makes "patched == rebuilt" a real
        /// comparison rather than a tautology: the rebuild concatenates slices, the patch splices
        /// one in, and the two must land in the same place.
        /// </summary>
        private static CachedSummaryData Slice(
            Guid gameId,
            string cacheKey,
            int totalAchievements,
            params (string ApiName, DateTime? UnlockedAt)[] achievements)
        {
            var data = new CachedSummaryData();
            data.Games.Add(new CachedGameSummaryData
            {
                CacheKey = cacheKey,
                PlayniteGameId = gameId,
                GameName = cacheKey,
                TotalAchievements = totalAchievements,
                UnlockedAchievements = achievements.Count(a => a.UnlockedAt.HasValue),
                HasAchievements = true
            });

            foreach (var achievement in achievements)
            {
                data.Achievements.Add(new CachedRecentUnlockData
                {
                    CacheKey = cacheKey,
                    PlayniteGameId = gameId,
                    ApiName = achievement.ApiName,
                    DisplayName = achievement.ApiName,
                    Unlocked = achievement.UnlockedAt.HasValue,
                    UnlockTimeUtc = achievement.UnlockedAt
                });

                if (!achievement.UnlockedAt.HasValue)
                {
                    continue;
                }

                var date = achievement.UnlockedAt.Value.Date;
                if (!data.UnlockCountsByDateByGame.TryGetValue(gameId, out var counts))
                {
                    counts = new Dictionary<DateTime, int>();
                    data.UnlockCountsByDateByGame[gameId] = counts;
                }

                counts.TryGetValue(date, out var current);
                counts[date] = current + 1;

                data.GlobalUnlockCountsByDate.TryGetValue(date, out var globalCurrent);
                data.GlobalUnlockCountsByDate[date] = globalCurrent + 1;
            }

            data.RecentUnlocks = data.Achievements
                .Where(a => a.Unlocked && a.UnlockTimeUtc.HasValue)
                .ToList();

            return data;
        }

        /// <summary>The whole-library read: every slice concatenated and canonically ordered.</summary>
        private static CachedSummaryData Rebuild(params CachedSummaryData[] slices)
        {
            var result = new CachedSummaryData();
            foreach (var slice in slices)
            {
                result.Games.AddRange(slice.Games);
                result.Achievements.AddRange(slice.Achievements);

                foreach (var pair in slice.UnlockCountsByDateByGame)
                {
                    result.UnlockCountsByDateByGame[pair.Key] = pair.Value;
                }

                foreach (var pair in slice.GlobalUnlockCountsByDate)
                {
                    result.GlobalUnlockCountsByDate.TryGetValue(pair.Key, out var current);
                    result.GlobalUnlockCountsByDate[pair.Key] = current + pair.Value;
                }
            }

            result.Achievements = RecentUnlockOrder.Sorted(result.Achievements);
            result.RecentUnlocks = result.Achievements
                .Where(a => a?.Unlocked == true && a.UnlockTimeUtc.HasValue)
                .ToList();
            return result;
        }

        private static void AssertMatchesRebuild(CachedSummaryData patched, CachedSummaryData rebuilt)
        {
            // Games is compared as a set, deliberately. Its order out of this layer is not
            // load-bearing: OverviewDataBuilder re-sorts by LastPlayed and the view model sorts
            // again by the configured default, and the merger already appends custom-only games
            // after the query's own order. Asserting a sequence here would pin an order nothing
            // reads and that a patch has no way to reproduce.
            CollectionAssert.AreEquivalent(
                rebuilt.Games.Select(g => g.CacheKey + "/" + g.UnlockedAchievements + "/" + g.TotalAchievements).ToList(),
                patched.Games.Select(g => g.CacheKey + "/" + g.UnlockedAchievements + "/" + g.TotalAchievements).ToList(),
                "Game rows diverged from a full rebuild.");

            CollectionAssert.AreEqual(
                rebuilt.Achievements.Select(a => a.CacheKey + "/" + a.ApiName).ToList(),
                patched.Achievements.Select(a => a.CacheKey + "/" + a.ApiName).ToList(),
                "Achievement rows or their order diverged from a full rebuild.");

            CollectionAssert.AreEqual(
                rebuilt.RecentUnlocks.Select(a => a.CacheKey + "/" + a.ApiName).ToList(),
                patched.RecentUnlocks.Select(a => a.CacheKey + "/" + a.ApiName).ToList(),
                "Recent unlocks diverged from a full rebuild.");

            CollectionAssert.AreEquivalent(
                rebuilt.GlobalUnlockCountsByDate.Select(p => p.Key + "=" + p.Value).ToList(),
                patched.GlobalUnlockCountsByDate.Select(p => p.Key + "=" + p.Value).ToList(),
                "Global unlock counts diverged from a full rebuild.");

            CollectionAssert.AreEquivalent(
                rebuilt.UnlockCountsByDateByGame.Keys.ToList(),
                patched.UnlockCountsByDateByGame.Keys.ToList(),
                "Per-game timeline entries diverged from a full rebuild.");
        }

        [TestMethod]
        public void PatchingOneGame_MatchesAFullRebuild()
        {
            var a = Slice(GameA, "a", 2, ("a1", Day1), ("a2", null));
            var b = Slice(GameB, "b", 2, ("b1", Day1), ("b2", Day2));
            var c = Slice(GameC, "c", 1, ("c1", Day2));
            var bAfter = Slice(GameB, "b", 3, ("b1", Day1), ("b2", Day2), ("b3", Day2));

            var patched = OverviewSummaryPatcher.Patch(Rebuild(a, b, c), GameB, bAfter);

            Assert.IsNotNull(patched);
            AssertMatchesRebuild(patched, Rebuild(a, bAfter, c));
        }

        [TestMethod]
        public void PatchingOneGame_LeavesEveryOtherGamesRowsUntouched()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day1));
            var baseData = Rebuild(a, b);

            var untouchedGameRow = baseData.Games.Single(g => g.PlayniteGameId == GameA);
            var untouchedAchievement = baseData.Achievements.Single(x => x.PlayniteGameId == GameA);
            var unlockedBefore = untouchedGameRow.UnlockedAchievements;
            var apiNameBefore = untouchedAchievement.ApiName;

            var patched = OverviewSummaryPatcher.Patch(
                baseData, GameB, Slice(GameB, "b", 2, ("b1", Day1), ("b2", Day2)));

            Assert.AreSame(
                untouchedGameRow,
                patched.Games.Single(g => g.PlayniteGameId == GameA),
                "Untouched rows are carried by reference; copying them per edit would give back " +
                "the cost the patch exists to remove.");
            Assert.AreSame(
                untouchedAchievement,
                patched.Achievements.Single(x => x.PlayniteGameId == GameA));

            // Reference-identical is not enough: a re-hydration would mutate these in place.
            Assert.AreEqual(unlockedBefore, untouchedGameRow.UnlockedAchievements);
            Assert.AreEqual(apiNameBefore, untouchedAchievement.ApiName);
        }

        [TestMethod]
        public void PatchingTwice_IsIdempotent()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day1));
            var baseData = Rebuild(a, b);
            var bAfter = Slice(GameB, "b", 2, ("b1", Day1), ("b2", Day2));

            var once = OverviewSummaryPatcher.Patch(baseData, GameB, bAfter);
            var twice = OverviewSummaryPatcher.Patch(once, GameB, bAfter);

            // The direct guard against the merger's Accumulate incrementing and
            // AppendPlatinumApiNames appending when a row is hydrated a second time.
            AssertMatchesRebuild(twice, Rebuild(a, bAfter));
            Assert.AreEqual(
                once.Achievements.Count,
                twice.Achievements.Count,
                "Re-applying the same slice must not duplicate rows.");
        }

        [TestMethod]
        public void GlobalCounts_StayTheSumOfThePerGameCounts()
        {
            var a = Slice(GameA, "a", 2, ("a1", Day1), ("a2", Day2));
            var b = Slice(GameB, "b", 1, ("b1", Day1));

            var patched = OverviewSummaryPatcher.Patch(
                Rebuild(a, b), GameB, Slice(GameB, "b", 2, ("b1", Day2), ("b2", Day2)));

            foreach (var date in patched.GlobalUnlockCountsByDate.Keys)
            {
                var perGameSum = patched.UnlockCountsByDateByGame.Values
                    .Sum(counts => counts.TryGetValue(date, out var n) ? n : 0);

                Assert.AreEqual(
                    perGameSum,
                    patched.GlobalUnlockCountsByDate[date],
                    $"The global count for {date:d} must equal the sum over games.");
            }
        }

        [TestMethod]
        public void ADateWhoseLastContributorLeaves_IsRemovedRatherThanLeftAtZero()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day2));

            // Day2 existed only because of GameB; after the edit GameB unlocks on Day1 instead.
            var patched = OverviewSummaryPatcher.Patch(
                Rebuild(a, b), GameB, Slice(GameB, "b", 1, ("b1", Day1)));

            Assert.IsFalse(
                patched.GlobalUnlockCountsByDate.ContainsKey(Day2),
                "A full rebuild never produces a zero entry, so neither may a patch -- a stale " +
                "zero would draw an empty column on the unlock timeline.");
            Assert.AreEqual(2, patched.GlobalUnlockCountsByDate[Day1]);
        }

        [TestMethod]
        public void AGameThatBecomesExcluded_LosesItsRowsUnlocksAndCounts()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day2));

            // Exclusion reads back as an empty slice.
            var patched = OverviewSummaryPatcher.Patch(Rebuild(a, b), GameB, new CachedSummaryData());

            Assert.IsNotNull(patched, "An excluded game is a legitimate empty slice, not a failure.");
            Assert.IsFalse(patched.Games.Any(g => g.PlayniteGameId == GameB));
            Assert.IsFalse(patched.Achievements.Any(x => x.PlayniteGameId == GameB));
            Assert.IsFalse(patched.RecentUnlocks.Any(x => x.PlayniteGameId == GameB));
            Assert.IsFalse(patched.UnlockCountsByDateByGame.ContainsKey(GameB));
            Assert.IsFalse(patched.GlobalUnlockCountsByDate.ContainsKey(Day2));
        }

        [TestMethod]
        public void RecentUnlocks_ShareInstancesWithAchievements()
        {
            var a = Slice(GameA, "a", 2, ("a1", Day1), ("a2", null));
            var b = Slice(GameB, "b", 1, ("b1", Day2));

            var patched = OverviewSummaryPatcher.Patch(
                Rebuild(a, b), GameB, Slice(GameB, "b", 2, ("b1", Day2), ("b2", Day1)));

            foreach (var recent in patched.RecentUnlocks)
            {
                Assert.IsTrue(
                    patched.Achievements.Any(item => ReferenceEquals(item, recent)),
                    "At limit 0 the reader makes RecentUnlocks a view over the same instances as " +
                    "Achievements, and the merger appends one instance to both. Breaking that " +
                    "aliasing doubles what the memo retains.");
            }

            Assert.IsFalse(
                patched.RecentUnlocks.Any(x => x.UnlockTimeUtc == null),
                "Locked rows belong to Achievements only.");
        }

        [TestMethod]
        public void Ordering_IsNewestFirstAndBreaksTiesReproducibly()
        {
            // Same timestamp across two games: the tie-break is what a patch must reproduce.
            var a = Slice(GameA, "aaa", 1, ("z_last", Day1));
            var b = Slice(GameB, "bbb", 1, ("a_first", Day1));
            var c = Slice(GameC, "ccc", 1, ("c1", Day2));

            var patched = OverviewSummaryPatcher.Patch(
                Rebuild(a, b, c), GameB, Slice(GameB, "bbb", 1, ("a_first", Day1)));

            AssertMatchesRebuild(patched, Rebuild(a, b, c));

            var times = patched.Achievements
                .Where(x => x.UnlockTimeUtc.HasValue)
                .Select(x => x.UnlockTimeUtc.Value)
                .ToList();
            CollectionAssert.AreEqual(
                times.OrderByDescending(t => t).ToList(),
                times,
                "Unlock rows must stay newest-first.");

            // Day1 ties resolve on CacheKey ordinal: "aaa" before "bbb".
            var day1 = patched.Achievements.Where(x => x.UnlockTimeUtc == Day1).ToList();
            CollectionAssert.AreEqual(
                new[] { "aaa", "bbb" },
                day1.Select(x => x.CacheKey).ToArray(),
                "Ties break on CacheKey then ApiName, matching the query's ORDER BY.");
        }

        [TestMethod]
        public void PatchingSeveralGamesAtOnce_MatchesAFullRebuild()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day1));
            var c = Slice(GameC, "c", 1, ("c1", Day2));

            var aAfter = Slice(GameA, "a", 2, ("a1", Day2), ("a2", Day2));
            var cAfter = Slice(GameC, "c", 1, ("c1", Day1));

            var patched = OverviewSummaryPatcher.Patch(
                Rebuild(a, b, c),
                new[] { GameA, GameC },
                new Dictionary<Guid, CachedSummaryData> { [GameA] = aAfter, [GameC] = cAfter });

            Assert.IsNotNull(patched);
            AssertMatchesRebuild(patched, Rebuild(aAfter, b, cAfter));
        }

        [TestMethod]
        public void AMissingSlice_RefusesSoTheCallerRebuilds()
        {
            var baseData = Rebuild(Slice(GameA, "a", 1, ("a1", Day1)));

            Assert.IsNull(
                OverviewSummaryPatcher.Patch(baseData, GameA, null),
                "A slice the caller could not read is not the same as a game that now " +
                "contributes nothing; guessing would silently delete the game.");
            Assert.IsNull(
                OverviewSummaryPatcher.Patch(
                    baseData,
                    new[] { GameA, GameB },
                    new Dictionary<Guid, CachedSummaryData> { [GameA] = Slice(GameA, "a", 1) }),
                "Every named game must have been read.");
        }

        [TestMethod]
        public void AGameThatHadRowsButReadsBackPartial_RefusesSoTheCallerRebuilds()
        {
            var baseData = Rebuild(
                Slice(GameA, "a", 1, ("a1", Day1)),
                Slice(GameB, "b", 1, ("b1", Day1)));

            // Unlock rows but no summary row: the signature of a scoped read that matched only
            // some of its queries -- a GUID stored in a casing or format the predicate missed.
            var partial = new CachedSummaryData();
            partial.Achievements.Add(new CachedRecentUnlockData
            {
                CacheKey = "b",
                PlayniteGameId = GameB,
                ApiName = "b1",
                Unlocked = true,
                UnlockTimeUtc = Day1
            });

            Assert.IsNull(
                OverviewSummaryPatcher.Patch(baseData, GameB, partial),
                "Falling back to a full rebuild is what keeps a mismatched scoped read from " +
                "quietly dropping a game out of the overview.");
        }

        [TestMethod]
        public void NullOrEmptyInputs_Refuse()
        {
            var slice = Slice(GameA, "a", 1, ("a1", Day1));

            Assert.IsNull(OverviewSummaryPatcher.Patch(null, GameA, slice));
            Assert.IsNull(OverviewSummaryPatcher.Patch(Rebuild(slice), Guid.Empty, slice));
            Assert.IsNull(OverviewSummaryPatcher.Patch(
                Rebuild(slice), Array.Empty<Guid>(), new Dictionary<Guid, CachedSummaryData>()));
        }

        [TestMethod]
        public void TheBaseEnvelopeAndItsLists_AreNotMutated()
        {
            var a = Slice(GameA, "a", 1, ("a1", Day1));
            var b = Slice(GameB, "b", 1, ("b1", Day2));
            var baseData = Rebuild(a, b);

            var gamesBefore = baseData.Games.ToList();
            var achievementsBefore = baseData.Achievements.ToList();
            var globalBefore = baseData.GlobalUnlockCountsByDate.ToDictionary(p => p.Key, p => p.Value);

            OverviewSummaryPatcher.Patch(baseData, GameB, Slice(GameB, "b", 2, ("b1", Day1), ("b2", Day1)));

            // The base is the memoized instance other readers may still be holding.
            CollectionAssert.AreEqual(gamesBefore, baseData.Games);
            CollectionAssert.AreEqual(achievementsBefore, baseData.Achievements);
            CollectionAssert.AreEquivalent(
                globalBefore.Select(p => p.Key + "=" + p.Value).ToList(),
                baseData.GlobalUnlockCountsByDate.Select(p => p.Key + "=" + p.Value).ToList(),
                "Patching must never write through to the memoized envelope.");
        }
    }
}
