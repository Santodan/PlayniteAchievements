using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.GuildWars2;

namespace PlayniteAchievements.GuildWars2.Tests
{
    [TestClass]
    public class Gw2ProgressSnapshotTests
    {
        private static Dictionary<int, Gw2ProgressSignature> Snapshot(string json)
            => Gw2ProgressSnapshot.Build(JsonConvert.DeserializeObject<List<Gw2AccountAchievement>>(json));

        [TestMethod]
        public void AreEquivalent_IdenticalPayloads_AreEquivalent()
        {
            const string json = @"[
                { ""id"": 1, ""current"": 60, ""max"": 100, ""done"": false },
                { ""id"": 2, ""done"": true }
            ]";

            Assert.IsTrue(Gw2ProgressSnapshot.AreEquivalent(Snapshot(json), Snapshot(json)));
        }

        [TestMethod]
        public void AreEquivalent_NullPrevious_IsNeverEquivalent()
        {
            // The first poll of a session must always do the full work.
            Assert.IsFalse(Gw2ProgressSnapshot.AreEquivalent(null, Snapshot("[]")));
        }

        [DataTestMethod]
        [DataRow(@"[ { ""id"": 1, ""current"": 61, ""done"": false } ]", "current moved")]
        [DataRow(@"[ { ""id"": 1, ""current"": 60, ""done"": true } ]", "done flipped")]
        [DataRow(@"[ { ""id"": 1, ""current"": 60, ""done"": false, ""repeated"": 1 } ]", "repeated moved")]
        [DataRow(@"[ { ""id"": 1, ""current"": 60, ""done"": false }, { ""id"": 9, ""current"": 1, ""done"": false } ]", "new achievement appeared")]
        public void AreEquivalent_AnyMovement_IsNotEquivalent(string afterJson, string because)
        {
            var before = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");

            Assert.IsFalse(Gw2ProgressSnapshot.AreEquivalent(before, Snapshot(afterJson)), because);
        }

        [TestMethod]
        public void GetChangedIds_ReportsOnlyWhatMoved()
        {
            var before = Snapshot(@"[
                { ""id"": 1, ""current"": 60, ""done"": false },
                { ""id"": 2, ""current"": 5, ""done"": false }
            ]");
            var after = Snapshot(@"[
                { ""id"": 1, ""current"": 60, ""done"": false },
                { ""id"": 2, ""current"": 9, ""done"": false }
            ]");

            CollectionAssert.AreEqual(new[] { 2 }, Gw2ProgressSnapshot.GetChangedIds(before, after));
        }

        [TestMethod]
        public void GetChangedIds_FirstAppearanceCountsAsChanged()
        {
            // The account endpoint omits achievements with no progress, so an id arriving for the
            // first time is itself the first progress on it.
            var after = Snapshot(@"[ { ""id"": 7, ""current"": 1, ""done"": false } ]");

            CollectionAssert.AreEqual(new[] { 7 }, Gw2ProgressSnapshot.GetChangedIds(Snapshot("[]"), after));
        }

        [TestMethod]
        public void GetChangedIds_VanishedEntryIsNotReported()
        {
            // Progress does not go backwards; a missing entry means a partial response, and
            // reporting it would relock something the player earned.
            var before = Snapshot(@"[ { ""id"": 1, ""done"": true }, { ""id"": 2, ""done"": true } ]");
            var after = Snapshot(@"[ { ""id"": 1, ""done"": true } ]");

            Assert.AreEqual(0, Gw2ProgressSnapshot.GetChangedIds(before, after).Count);
        }
    }

    [TestClass]
    public class Gw2InGameProgressMapperTests
    {
        /// <summary>A cached four-tier ladder for achievement 1, plus a single-tier achievement 2.</summary>
        private static GameAchievementData BuildCachedSchema()
        {
            return new GameAchievementData
            {
                ProviderKey = "GW2",
                Achievements = new List<AchievementDetail>
                {
                    new AchievementDetail { ApiName = "1:t1", ProgressDenom = 10 },
                    new AchievementDetail { ApiName = "1:t2", ProgressDenom = 50 },
                    new AchievementDetail { ApiName = "1:t3", ProgressDenom = 100 },
                    new AchievementDetail { ApiName = "1:t4", ProgressDenom = 1000 },
                    new AchievementDetail { ApiName = "2:t1", ProgressDenom = 1 },
                    new AchievementDetail { ApiName = "not-a-tier-key", ProgressDenom = 5 }
                }
            };
        }

        private static Dictionary<int, Gw2ProgressSignature> Snapshot(string json)
            => Gw2ProgressSnapshot.Build(JsonConvert.DeserializeObject<List<Gw2AccountAchievement>>(json));

        [TestMethod]
        public void BuildTierIndex_GroupsTiersByAchievementAndKeepsThresholds()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());

            Assert.AreEqual(2, index.Count);
            CollectionAssert.AreEqual(
                new[] { 10, 50, 100, 1000 },
                index[1].Select(t => t.Threshold).ToArray());
        }

        [TestMethod]
        public void BuildTierIndex_IgnoresRowsThatAreNotTierKeys()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());

            Assert.IsFalse(index.Values.SelectMany(t => t).Any(t => t.ApiName == "not-a-tier-key"));
        }

        [TestMethod]
        public void BuildObservations_MatchesTheFullRefreshUnlockRules()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, snapshot);

            Assert.AreEqual(4, observations.Count);
            Assert.IsTrue(observations.Single(o => o.ApiName == "1:t1").Unlocked);
            Assert.IsTrue(observations.Single(o => o.ApiName == "1:t2").Unlocked);
            Assert.IsFalse(observations.Single(o => o.ApiName == "1:t3").Unlocked);
            Assert.IsFalse(observations.Single(o => o.ApiName == "1:t4").Unlocked);
        }

        [TestMethod]
        public void BuildObservations_ClampsProgressToEachTiersOwnThreshold()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, snapshot);

            var earned = observations.Single(o => o.ApiName == "1:t1");
            Assert.AreEqual(10, earned.ProgressNum);
            Assert.AreEqual(10, earned.ProgressDenom);

            var pending = observations.Single(o => o.ApiName == "1:t3");
            Assert.AreEqual(60, pending.ProgressNum);
            Assert.AreEqual(100, pending.ProgressDenom);
        }

        [TestMethod]
        public void BuildObservations_DoneUnlocksEveryTier()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""done"": true } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, snapshot);

            Assert.IsTrue(observations.All(o => o.Unlocked));
        }

        [TestMethod]
        public void BuildObservations_RepeatedUnlocksEveryTier()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""current"": 3, ""done"": false, ""repeated"": 2 } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, snapshot);

            Assert.IsTrue(observations.All(o => o.Unlocked));
        }

        [TestMethod]
        public void BuildObservations_NeverReportsAnUnlockTime()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""done"": true } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, snapshot);

            // The registration declares SourceObservation, so the monitor anchors on when it saw
            // the transition. The provider must not invent a stamp of its own.
            Assert.IsTrue(observations.All(o => o.UnlockTimeUtc == null));
        }

        [TestMethod]
        public void BuildObservations_EmitsNothingForUnchangedAchievements()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");

            Assert.AreEqual(0, Gw2InGameProgressMapper.BuildObservations(index, new int[0], snapshot).Count);
        }

        [TestMethod]
        public void BuildObservations_SkipsChangedIdsWithNoCachedTiers()
        {
            // An achievement added by a game build the cached schema predates.
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var snapshot = Snapshot(@"[ { ""id"": 999, ""done"": true } ]");

            Assert.AreEqual(0, Gw2InGameProgressMapper.BuildObservations(index, new[] { 999 }, snapshot).Count);
        }

        [TestMethod]
        public void BuildObservations_SkipsTiersAlreadySettledAtThePreviousRead()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var before = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");
            var after = Snapshot(@"[ { ""id"": 1, ""current"": 120, ""done"": false } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, after, before);

            // Tiers 1 and 2 (10 and 50) were already earned with full bars at 60 and cannot move.
            CollectionAssert.AreEquivalent(
                new[] { "1:t3", "1:t4" },
                observations.Select(o => o.ApiName).ToArray());
        }

        [TestMethod]
        public void BuildObservations_StillReportsTheTierThatJustUnlocked()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var before = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");
            var after = Snapshot(@"[ { ""id"": 1, ""current"": 120, ""done"": false } ]");

            var observations = Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, after, before);

            var crossed = observations.Single(o => o.ApiName == "1:t3");
            Assert.IsTrue(crossed.Unlocked, "120 crossed the 100 threshold");
            Assert.AreEqual(100, crossed.ProgressNum);
        }

        [TestMethod]
        public void BuildObservations_EmitsNothingForAnAlreadyFinishedLadder()
        {
            // Only the repeat count moved, which changes no tier.
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var before = Snapshot(@"[ { ""id"": 1, ""done"": true, ""repeated"": 1 } ]");
            var after = Snapshot(@"[ { ""id"": 1, ""done"": true, ""repeated"": 2 } ]");

            Assert.AreEqual(0, Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, after, before).Count);
        }

        [TestMethod]
        public void BuildObservations_WithNoPreviousReadReportsEveryTier()
        {
            var index = Gw2InGameProgressMapper.BuildTierIndex(BuildCachedSchema());
            var after = Snapshot(@"[ { ""id"": 1, ""current"": 60, ""done"": false } ]");

            Assert.AreEqual(4, Gw2InGameProgressMapper.BuildObservations(index, new[] { 1 }, after, null).Count);
        }

        [DataTestMethod]
        [DataRow("1:t1", true, 1)]
        [DataRow("12345:t42", true, 12345)]
        [DataRow("not-a-tier-key", false, 0)]
        [DataRow(":t1", false, 0)]
        [DataRow("", false, 0)]
        [DataRow(null, false, 0)]
        public void TryParseTierApiName_RecoversTheAchievementId(string apiName, bool expected, int expectedId)
        {
            var parsed = Gw2AchievementMapper.TryParseTierApiName(apiName, out var id);

            Assert.AreEqual(expected, parsed, apiName ?? "null");
            Assert.AreEqual(expectedId, id);
        }

        [TestMethod]
        public void TryParseTierApiName_RoundTripsWhatBuildTierApiNameProduces()
        {
            var apiName = Gw2AchievementMapper.BuildTierApiName(4821, 7);

            Assert.IsTrue(Gw2AchievementMapper.TryParseTierApiName(apiName, out var id));
            Assert.AreEqual(4821, id);
        }
    }
}
