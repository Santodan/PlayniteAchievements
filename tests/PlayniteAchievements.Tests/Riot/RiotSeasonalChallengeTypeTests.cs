using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Riot;

namespace PlayniteAchievements.Riot.Tests
{
    [TestClass]
    public class RiotSeasonalChallengeTypeTests
    {
        private static readonly DateTime Now = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Mirrors the live CommunityDragon seasonal shape: a "2023 Seasonal" capstone carrying a
        /// season tag, a leaf under it carrying a seasons list, a leaf of a current-year set, an
        /// archived non-seasonal-id challenge, and an ordinary open-ended one. None has an end date.
        /// </summary>
        private const string MetadataJson = @"{
            ""challenges"": {
                ""2023000"": {
                    ""name"": ""2023 Seasonal"", ""tags"": { ""isCapstone"": ""Y"", ""season"": ""14"" },
                    ""seasons"": [], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                },
                ""2023013"": {
                    ""name"": ""All In, Together: 2023"", ""tags"": { ""parent"": ""2023000"" },
                    ""seasons"": [13, 14], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                },
                ""2026100"": {
                    ""name"": ""2026 Seasonal: Split 1"", ""tags"": { ""isCapstone"": ""Y"", ""season"": ""18"" },
                    ""seasons"": [], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                },
                ""2026101"": {
                    ""name"": ""Current Split Leaf"", ""tags"": { ""parent"": ""2026100"" },
                    ""seasons"": [18], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                },
                ""301104"": {
                    ""name"": ""Two Shells are Better Than One"", ""tags"": { ""parent"": ""301100"" },
                    ""seasons"": [12], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                },
                ""101101"": {
                    ""name"": ""DPS Threat"", ""tags"": { ""parent"": ""101100"" },
                    ""seasons"": [], ""endTimestamp"": 0, ""levelToIconPath"": {},
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } }
                }
            }
        }";

        private const string ConfigJson = @"[
            { ""id"": 301104, ""state"": ""ARCHIVED"" },
            { ""id"": 2023013, ""state"": ""ENABLED"" },
            { ""id"": 101101, ""state"": ""ENABLED"" }
        ]";

        private static List<AchievementDetail> Build(IReadOnlyCollection<long> archivedIds)
        {
            var state = new RiotPlayerChallengeState
            {
                PlayerKey = "test-puuid",
                Challenges = new List<RiotChallengeInfoDto>(),
                ArchivedChallengeIds = archivedIds
            };

            return RiotChallengeMapper.BuildAchievements(
                RiotChallengeMapper.ParseMetadata(MetadataJson),
                state,
                new Dictionary<string, string>(),
                Now);
        }

        private static string TypeOf(List<AchievementDetail> results, string apiName) =>
            results.Single(a => a.ApiName == apiName).CategoryType;

        [TestMethod]
        public void ParseArchivedChallengeIds_KeepsOnlyArchived()
        {
            var archived = RiotChallengeMapper.ParseArchivedChallengeIds(ConfigJson);

            CollectionAssert.AreEquivalent(new[] { 301104L }, archived.ToArray());
            Assert.AreEqual(0, RiotChallengeMapper.ParseArchivedChallengeIds(null).Count);
        }

        [TestMethod]
        public void PastSeasonalChallenges_AreUnobtainable()
        {
            var results = Build(RiotChallengeMapper.ParseArchivedChallengeIds(ConfigJson));

            Assert.AreEqual("Unobtainable", TypeOf(results, "2023000:IRON"), "The season-tagged capstone reads its own id.");
            Assert.AreEqual("Unobtainable", TypeOf(results, "2023013:IRON"), "A leaf reads its own seasonal-set-shaped id.");
        }

        [TestMethod]
        public void CurrentYearSeasonalChallenges_AreMissable()
        {
            var results = Build(RiotChallengeMapper.ParseArchivedChallengeIds(ConfigJson));

            Assert.AreEqual("Missable", TypeOf(results, "2026100:IRON"));
            Assert.AreEqual("Missable", TypeOf(results, "2026101:IRON"));
        }

        [TestMethod]
        public void ArchivedChallenges_AreUnobtainableEvenWithoutAYearId()
        {
            var results = Build(RiotChallengeMapper.ParseArchivedChallengeIds(ConfigJson));

            Assert.AreEqual("Unobtainable", TypeOf(results, "301104:IRON"));
        }

        [TestMethod]
        public void SeasonalChallengeWithoutYearIdOrArchive_GetsNoType()
        {
            var results = Build(new HashSet<long>());

            Assert.IsNull(TypeOf(results, "301104:IRON"), "Seasonal, but no id in its chain carries a year.");
        }

        [TestMethod]
        public void OpenEndedChallenges_GetNoType()
        {
            var results = Build(RiotChallengeMapper.ParseArchivedChallengeIds(ConfigJson));

            Assert.IsNull(TypeOf(results, "101101:IRON"));
        }
    }
}
