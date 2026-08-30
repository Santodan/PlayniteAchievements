using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Riot;

namespace PlayniteAchievements.Riot.Tests
{
    [TestClass]
    public class RiotChallengeMapperTests
    {
        private static readonly DateTime Now = new DateTime(2026, 8, 30, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// Mirrors the real CommunityDragon shape: a top-level object whose challenge map is keyed
        /// by id string, with no id field inside the entries.
        ///
        /// "0" is the crystal root and "1" a category meter (both skipped); "101000" is a capstone
        /// parented to that category; "101001" and "101002" are leaves under the capstone; "900001"
        /// is a retired challenge; "900002" is reverse-direction.
        /// </summary>
        private const string MetadataJson = @"{
            ""challenges"": {
                ""0"": {
                    ""name"": ""CRYSTAL"", ""descriptionShort"": ""Special rules for Crystal"",
                    ""tags"": { ""isCapstone"": ""Y"", ""isCategory"": ""true"" },
                    ""endTimestamp"": 0, ""levelToIconPath"": {}, ""thresholds"": {}
                },
                ""1"": {
                    ""name"": ""IMAGINATION"", ""descriptionShort"": ""IMAGINATION capstone"",
                    ""tags"": { ""isCapstone"": ""Y"", ""isCategory"": ""true"", ""parent"": ""0"" },
                    ""endTimestamp"": 0, ""levelToIconPath"": {}, ""thresholds"": {}
                },
                ""101000"": {
                    ""name"": ""ARAM Authority"", ""description"": ""Earn progress from ARAM challenges."",
                    ""descriptionShort"": ""ARAM progress"",
                    ""tags"": { ""isCapstone"": ""Y"", ""parent"": ""1"" },
                    ""endTimestamp"": 0,
                    ""levelToIconPath"": {
                        ""IRON"": ""/lol-game-data/assets/ASSETS/Challenges/Config/101000/Tokens/IRON.png"",
                        ""GOLD"": ""/lol-game-data/assets/ASSETS/Challenges/Config/101000/Tokens/GOLD.png""
                    },
                    ""thresholds"": {
                        ""IRON"": { ""value"": 40.0 }, ""BRONZE"": { ""value"": 85.0 },
                        ""SILVER"": { ""value"": 140.0 }, ""GOLD"": { ""value"": 360.0 }
                    },
                    ""reverseDirection"": false
                },
                ""101001"": {
                    ""name"": ""Snowball Fight"", ""description"": ""Hit enemies with snowballs."",
                    ""descriptionShort"": ""Snowballs"",
                    ""tags"": { ""parent"": ""101000"" },
                    ""endTimestamp"": 0,
                    ""levelToIconPath"": {
                        ""IRON"": ""/lol-game-data/assets/ASSETS/Challenges/Config/101001/Tokens/IRON.png""
                    },
                    ""thresholds"": { ""IRON"": { ""value"": 10.0 }, ""BRONZE"": { ""value"": 25.0 } },
                    ""reverseDirection"": false
                },
                ""101002"": {
                    ""name"": ""Poro Patrol"", ""description"": """", ""descriptionShort"": ""Feed poros"",
                    ""tags"": { ""parent"": ""101000"" },
                    ""endTimestamp"": 0,
                    ""levelToIconPath"": {
                        ""IRON"": ""/lol-game-data/assets/ASSETS/Challenges/Config/101002/Tokens/IRON.png""
                    },
                    ""thresholds"": { ""IRON"": { ""value"": 5.0 } },
                    ""reverseDirection"": false
                },
                ""900001"": {
                    ""name"": ""Season Relic"", ""description"": ""A retired seasonal challenge."",
                    ""tags"": { ""parent"": ""101000"" },
                    ""endTimestamp"": 1600000000000,
                    ""levelToIconPath"": {
                        ""IRON"": ""/lol-game-data/assets/ASSETS/Challenges/Config/900001/Tokens/IRON.png""
                    },
                    ""thresholds"": { ""IRON"": { ""value"": 1.0 } },
                    ""reverseDirection"": false
                },
                ""900002"": {
                    ""name"": ""Flawless Run"", ""description"": ""Win with as few deaths as possible."",
                    ""tags"": { ""parent"": ""101000"" },
                    ""endTimestamp"": 0,
                    ""levelToIconPath"": {
                        ""IRON"": ""/lol-game-data/assets/ASSETS/Challenges/Config/900002/Tokens/IRON.png""
                    },
                    ""thresholds"": { ""IRON"": { ""value"": 10.0 }, ""BRONZE"": { ""value"": 5.0 } },
                    ""reverseDirection"": true
                }
            }
        }";

        private const string PlayerDataJson = @"{
            ""totalPoints"": { ""level"": ""GOLD"", ""current"": 4500.0, ""max"": 26500.0, ""percentile"": 0.25 },
            ""categoryPoints"": {},
            ""challenges"": [
                { ""challengeId"": 101000, ""percentile"": 0.04, ""level"": ""GOLD"", ""value"": 372.0, ""achievedTime"": 1756000000000 },
                { ""challengeId"": 101001, ""percentile"": 0.6, ""level"": ""IRON"", ""value"": 18.0, ""achievedTime"": 1750000000000 },
                { ""challengeId"": 900002, ""percentile"": 0.02, ""level"": ""BRONZE"", ""value"": 3.0, ""achievedTime"": 1740000000000 }
            ]
        }";

        /// <summary>
        /// "101002" has a populated Iron figure. "900001" has Riot's zero-as-absent gap at the
        /// lower tiers with a real figure higher up, the shape live data actually returns (Iron 0
        /// alongside Master 0.029, which cannot both be literal).
        /// </summary>
        private const string PercentilesJson = @"{
            ""101002"": { ""NONE"": 1.0, ""IRON"": 0.08 },
            ""900001"": { ""NONE"": 1.0, ""IRON"": 0.0, ""BRONZE"": 0.0, ""SILVER"": 0.12 },
            ""101000"": { ""NONE"": 1.0, ""IRON"": 0.4, ""GOLD"": 0.0 }
        }";

        private static readonly Dictionary<string, string> CategoryNames = new Dictionary<string, string>
        {
            ["1"] = "Imagination",
            ["2"] = "Expertise",
            ["3"] = "Veterancy",
            ["4"] = "Teamwork & Strategy",
            ["5"] = "Collection"
        };

        private static List<AchievementDetail> Build()
        {
            var metadata = RiotChallengeMapper.ParseMetadata(MetadataJson);
            var playerData = RiotChallengeMapper.ParsePlayerData(PlayerDataJson);

            var state = new RiotPlayerChallengeState
            {
                PlayerKey = "test-puuid",
                Challenges = playerData.Challenges,
                LevelPercentiles = RiotChallengeMapper.ParsePercentiles(PercentilesJson)
            };

            return RiotChallengeMapper.BuildAchievements(metadata, state, CategoryNames, Now);
        }

        private static AchievementDetail Get(string apiName) => Build().Single(a => a.ApiName == apiName);

        [TestMethod]
        public void BuildAchievements_SkipsCategoryAndRootNodes()
        {
            var achievements = Build();

            CollectionAssert.AreEquivalent(
                new[] { "101000", "101001", "101002", "900001", "900002" },
                achievements.Select(a => a.ApiName).ToArray(),
                "The crystal root and category meters are point totals, not achievements.");
        }

        [TestMethod]
        public void BuildAchievements_MapsUnlockTime()
        {
            var capstone = Get("101000");

            Assert.IsTrue(capstone.Unlocked);
            Assert.AreEqual(
                new DateTime(2025, 8, 24, 1, 46, 40, DateTimeKind.Utc),
                capstone.UnlockTimeUtc,
                "achievedTime is epoch milliseconds in UTC.");
        }

        [TestMethod]
        public void BuildAchievements_LeavesUnstartedChallengeLocked()
        {
            var locked = Get("101002");

            Assert.IsFalse(locked.Unlocked, "A challenge absent from player-data has never been started.");
            Assert.IsNull(locked.UnlockTimeUtc);
        }

        [TestMethod]
        public void BuildAchievements_NeverSetsTrophyType()
        {
            // TrophyType drives PlayStation trophy art and the platinum/gold/silver/bronze summary
            // counts. A challenge tier there renders as a PSN trophy - "platinum" reading as the
            // completion marker - and the tiers with no matching art render blank.
            Assert.IsTrue(
                Build().All(a => a.TrophyType == null),
                "No challenge may carry a trophy type; the tier is conveyed by its own token icon.");
        }

        [TestMethod]
        public void BuildAchievements_ProgressTargetsTheNextUnreachedTier()
        {
            var capstone = Get("101000");

            Assert.AreEqual(
                360,
                capstone.ProgressDenom,
                "GOLD is the highest defined tier here, so the bar targets GOLD itself.");
            Assert.AreEqual(
                360,
                capstone.ProgressNum,
                "The raw value of 372 is past GOLD, and the top tier pins the bar full.");

            var leaf = Get("101001");
            Assert.AreEqual(18, leaf.ProgressNum);
            Assert.AreEqual(25, leaf.ProgressDenom, "At IRON the next unreached tier is BRONZE.");
        }

        [TestMethod]
        public void BuildAchievements_NeverLetsProgressExceedItsDenominator()
        {
            foreach (var achievement in Build().Where(a => a.ProgressDenom.HasValue))
            {
                Assert.IsTrue(
                    achievement.ProgressNum <= achievement.ProgressDenom,
                    $"'{achievement.DisplayName}' rendered past 100%. Leaderboard-gated tiers let the " +
                    "raw value pass a threshold without the tier being awarded, so the bar must pin full.");
            }
        }

        [TestMethod]
        public void BuildAchievements_PinsProgressFullWhenTheValuePassesAnUnawardedTier()
        {
            // The player sits at GOLD with a value of 372 against a GOLD threshold of 360 — the
            // shape Riot returns when Grandmaster and Challenger are top-N leaderboard places.
            var capstone = Get("101000");

            Assert.AreEqual(360, capstone.ProgressDenom);
            Assert.AreEqual(360, capstone.ProgressNum);
        }

        [TestMethod]
        public void BuildAchievements_OmitsProgressForReverseDirectionChallenges()
        {
            var reverse = Get("900002");

            Assert.IsNull(reverse.ProgressNum, "Lower is better, so a rising bar would read backwards.");
            Assert.IsNull(reverse.ProgressDenom);
            Assert.IsTrue(reverse.Unlocked, "The challenge is still reported as earned.");
        }

        [TestMethod]
        public void BuildAchievements_ScalesPercentileToRarity()
        {
            var rare = Get("101000");
            Assert.AreEqual(4d, rare.GlobalPercentUnlocked.Value, 0.001, "Riot reports a 0..1 fraction.");
            Assert.AreEqual(RarityTier.UltraRare, rare.Rarity);

            var common = Get("101001");
            Assert.AreEqual(60d, common.GlobalPercentUnlocked.Value, 0.001);
            Assert.AreEqual(RarityTier.Common, common.Rarity);
        }

        [TestMethod]
        public void BuildAchievements_GivesLockedChallengesRarityFromTheIronPercentile()
        {
            var locked = Get("101002");

            Assert.AreEqual(
                8d,
                locked.GlobalPercentUnlocked.Value,
                0.001,
                "A locked challenge borrows the share of players who reached its first tier.");
            Assert.AreEqual(RarityTier.Rare, locked.Rarity);
        }

        [TestMethod]
        public void BuildAchievements_TreatsAZeroPercentileAsAbsentRatherThanUltraRare()
        {
            // Riot writes 0.0 where it has no figure. Reading it literally made every challenge
            // with a data gap look rarer than the rarest real achievement in the game.
            var gapped = Get("900001");

            Assert.AreEqual(
                12d,
                gapped.GlobalPercentUnlocked.Value,
                0.001,
                "Iron and Bronze are zero-filled, so the nearest populated tier supplies the figure.");
            Assert.AreEqual(RarityTier.Uncommon, gapped.Rarity);
        }

        [TestMethod]
        public void BuildAchievements_FallsBackToTheTableWhenThePlayerPercentileIsZeroFilled()
        {
            // 101000 is unlocked at GOLD, but its GOLD entry is zero-filled, so the nearest
            // populated tier below it answers instead.
            var capstone = Get("101000");

            Assert.AreEqual(4d, capstone.GlobalPercentUnlocked.Value, 0.001, "The player's own percentile wins when it is real.");

            var zeroed = RiotChallengeMapper.BuildAchievements(
                RiotChallengeMapper.ParseMetadata(MetadataJson),
                new RiotPlayerChallengeState
                {
                    Challenges = new List<RiotChallengeInfoDto>
                    {
                        new RiotChallengeInfoDto { ChallengeId = 101000, Level = "GOLD", Value = 372d, Percentile = 0d }
                    },
                    LevelPercentiles = RiotChallengeMapper.ParsePercentiles(PercentilesJson)
                },
                CategoryNames,
                Now).Single(a => a.ApiName == "101000");

            Assert.AreEqual(
                40d,
                zeroed.GlobalPercentUnlocked.Value,
                0.001,
                "With the player figure and the GOLD entry both zero-filled, IRON is the nearest real one.");
        }

        [TestMethod]
        public void BuildAchievements_LeavesRarityUnsetWhenNoPercentileExists()
        {
            var noPercentile = Get("900002");
            Assert.IsNotNull(noPercentile.GlobalPercentUnlocked, "This one is unlocked, so it has its own percentile.");

            var metadata = RiotChallengeMapper.ParseMetadata(MetadataJson);
            var bare = RiotChallengeMapper.BuildAchievements(
                metadata,
                new RiotPlayerChallengeState { Challenges = new List<RiotChallengeInfoDto>() },
                CategoryNames,
                Now);

            Assert.IsTrue(
                bare.All(a => !a.GlobalPercentUnlocked.HasValue),
                "With no player data and no percentile table there is nothing to derive rarity from.");
        }

        [TestMethod]
        public void BuildAchievements_UsesParentCapstoneNameAsCategory()
        {
            Assert.AreEqual("ARAM Authority", Get("101001").Category, "A leaf takes its parent capstone's name.");
            Assert.AreEqual(
                "Imagination",
                Get("101000").Category,
                "A capstone parented to a category takes the localized category label.");
        }

        [TestMethod]
        public void BuildAchievements_MarksRetiredChallengesMissable()
        {
            Assert.AreEqual("Missable", Get("900001").CategoryType, "Its end timestamp has passed.");
            Assert.IsNull(Get("101001").CategoryType, "An open-ended challenge carries no category type.");
        }

        [TestMethod]
        public void BuildAchievements_PicksTokenArtForTheCurrentTier()
        {
            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/101000/tokens/gold.png",
                Get("101000").UnlockedIconPath,
                "An unlocked challenge shows the art for the tier it is at.");

            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/101002/tokens/iron.png",
                Get("101002").UnlockedIconPath,
                "A locked challenge falls back to the lowest tier's art rather than showing nothing.");
        }

        [TestMethod]
        public void BuildAchievements_UsesTheSameArtForLockedAndUnlockedPaths()
        {
            var achievement = Get("101001");

            Assert.AreEqual(
                achievement.UnlockedIconPath,
                achievement.LockedIconPath,
                "Challenge tokens have no separate locked variant.");
        }

        [TestMethod]
        public void BuildAchievements_FallsBackToShortDescription()
        {
            Assert.AreEqual("Feed poros", Get("101002").Description, "description is empty, so descriptionShort wins.");
            Assert.AreEqual(
                "Hit enemies with snowballs.",
                Get("101001").Description,
                "The full description wins when it is present.");
        }

        [TestMethod]
        public void BuildAssetUrl_LowercasesTheAssetPathAndLeavesUrlsAlone()
        {
            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/1/tokens/iron.png",
                RiotChallengeMapper.BuildAssetUrl("/lol-game-data/assets/ASSETS/Challenges/Config/1/Tokens/IRON.png"));

            Assert.AreEqual(
                "https://example.com/Already.png",
                RiotChallengeMapper.BuildAssetUrl("https://example.com/Already.png"),
                "An absolute URL passes through untouched.");

            Assert.IsNull(RiotChallengeMapper.BuildAssetUrl(null));
        }

        [TestMethod]
        public void BuildAchievements_ReturnsEmptyWhenMetadataIsMissing()
        {
            Assert.AreEqual(0, RiotChallengeMapper.BuildAchievements(null, null, CategoryNames, Now).Count);
            Assert.AreEqual(
                0,
                RiotChallengeMapper.BuildAchievements(new CDragonChallengeFile(), null, CategoryNames, Now).Count);
        }

        [TestMethod]
        public void ParsePercentiles_KeysByNumericChallengeId()
        {
            var percentiles = RiotChallengeMapper.ParsePercentiles(PercentilesJson);

            Assert.IsTrue(percentiles.ContainsKey(101002L));
            Assert.AreEqual(0.08d, percentiles[101002L]["IRON"], 0.0001);
            Assert.AreEqual(0.08d, percentiles[101002L]["iron"], 0.0001, "Level lookup is case-insensitive.");
            Assert.AreEqual(0, RiotChallengeMapper.ParsePercentiles(null).Count);
        }
    }

    [TestClass]
    public class RiotChallengeLevelsTests
    {
        [TestMethod]
        public void GetRank_OrdersTheLadderAndTreatsUnknownAsNotStarted()
        {
            Assert.AreEqual(0, RiotChallengeLevels.GetRank("NONE"));
            Assert.AreEqual(0, RiotChallengeLevels.GetRank(null));
            Assert.AreEqual(0, RiotChallengeLevels.GetRank("UNRANKED"), "An unfamiliar tier degrades to locked, not an error.");
            Assert.IsTrue(RiotChallengeLevels.GetRank("CHALLENGER") > RiotChallengeLevels.GetRank("MASTER"));
            Assert.IsTrue(RiotChallengeLevels.GetRank("MASTER") > RiotChallengeLevels.GetRank("IRON"));
        }

        [TestMethod]
        public void Normalize_CanonicalisesCaseAndUnknownTiers()
        {
            Assert.AreEqual("PLATINUM", RiotChallengeLevels.Normalize("platinum"));
            Assert.AreEqual("IRON", RiotChallengeLevels.Normalize("iron"));
            Assert.AreEqual(RiotChallengeLevels.None, RiotChallengeLevels.Normalize("UNRANKED"));
            Assert.AreEqual(RiotChallengeLevels.None, RiotChallengeLevels.Normalize(null));
        }
    }

    [TestClass]
    public class RiotRegionsTests
    {
        [TestMethod]
        public void GetRegionalHost_MapsEveryPlatformToItsAccountV1Host()
        {
            Assert.AreEqual("https://americas.api.riotgames.com", RiotRegions.GetRegionalHost("na1"));
            Assert.AreEqual("https://europe.api.riotgames.com", RiotRegions.GetRegionalHost("euw1"));
            Assert.AreEqual("https://asia.api.riotgames.com", RiotRegions.GetRegionalHost("kr"));
            Assert.AreEqual("https://sea.api.riotgames.com", RiotRegions.GetRegionalHost("oc1"));
        }

        [TestMethod]
        public void GetPlatformHost_UsesThePlatformItself()
        {
            Assert.AreEqual("https://euw1.api.riotgames.com", RiotRegions.GetPlatformHost("EUW1"));
        }

        [TestMethod]
        public void NormalizePlatform_FallsBackToTheDefaultForUnknownInput()
        {
            Assert.AreEqual("na1", RiotRegions.NormalizePlatform("NA1"));
            Assert.AreEqual(RiotRegions.DefaultPlatform, RiotRegions.NormalizePlatform("not-a-region"));
            Assert.AreEqual(RiotRegions.DefaultPlatform, RiotRegions.NormalizePlatform(null));
        }

        [TestMethod]
        public void Choices_CoverEveryRoutableRegion()
        {
            Assert.IsTrue(RiotRegions.Choices.All(choice => RiotRegions.IsKnownPlatform(choice.Platform)));
            Assert.IsTrue(RiotRegions.Choices.Count >= 16, "Riot currently publishes 16 League platform routes.");
        }
    }
}
