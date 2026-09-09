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
        /// parented to that category with four tiers; "101001" and "101002" are leaves under the
        /// capstone; "900001" is retired; "900002" is reverse-direction.
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
        /// "101000" carries a distinct figure per tier plus Riot's zero-as-absent gap at GOLD.
        /// "900001" has the gap at its only tier, with a real figure higher up - the shape live data
        /// returns (IRON 0 alongside a populated MASTER, which cannot both be literal).
        /// </summary>
        private const string PercentilesJson = @"{
            ""101000"": { ""IRON"": 0.6, ""BRONZE"": 0.3, ""SILVER"": 0.04, ""GOLD"": 0.0 },
            ""101001"": { ""IRON"": 0.6, ""BRONZE"": 0.2 },
            ""101002"": { ""NONE"": 1.0, ""IRON"": 0.08 },
            ""900001"": { ""NONE"": 1.0, ""IRON"": 0.0, ""BRONZE"": 0.0, ""SILVER"": 0.12 }
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
        public void BuildAchievements_EmitsOneAchievementPerThresholdTier()
        {
            var achievements = Build();

            // 101000 has four tiers, 101001 and 900002 two each, 101002 and 900001 one each.
            Assert.AreEqual(10, achievements.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    "101000:IRON", "101000:BRONZE", "101000:SILVER", "101000:GOLD",
                    "101001:IRON", "101001:BRONZE",
                    "101002:IRON",
                    "900001:IRON",
                    "900002:IRON", "900002:BRONZE"
                },
                achievements.Select(a => a.ApiName).ToArray());
        }

        [TestMethod]
        public void BuildAchievements_SkipsCategoryAndRootNodes()
        {
            Assert.IsFalse(
                Build().Any(a => a.ApiName.StartsWith("0:") || a.ApiName.StartsWith("1:")),
                "The crystal root and category meters are point totals, not achievements.");
        }

        [TestMethod]
        public void BuildAchievements_UnlocksEveryTierUpToThePlayersOwn()
        {
            // The player holds GOLD on 101000, so all four tiers up to it are earned.
            foreach (var tier in new[] { "IRON", "BRONZE", "SILVER", "GOLD" })
            {
                Assert.IsTrue(Get("101000:" + tier).Unlocked, $"{tier} is at or below the player's tier.");
            }

            // The player holds IRON on 101001, so BRONZE is still to come.
            Assert.IsTrue(Get("101001:IRON").Unlocked);
            Assert.IsFalse(Get("101001:BRONZE").Unlocked);
        }

        [TestMethod]
        public void BuildAchievements_TimestampsOnlyTheTierThePlayerCurrentlyHolds()
        {
            // Riot reports one achievedTime, for the current tier. Backfilling the tiers below it
            // would invent unlock moments the unlock feed then orders by.
            Assert.AreEqual(
                new DateTime(2025, 8, 24, 1, 46, 40, DateTimeKind.Utc),
                Get("101000:GOLD").UnlockTimeUtc);

            foreach (var tier in new[] { "IRON", "BRONZE", "SILVER" })
            {
                var lower = Get("101000:" + tier);
                Assert.IsTrue(lower.Unlocked, $"{tier} is earned.");
                Assert.IsNull(lower.UnlockTimeUtc, $"{tier} is earned but Riot does not say when.");
            }
        }

        [TestMethod]
        public void BuildAchievements_LeavesUnstartedChallengeLocked()
        {
            var locked = Get("101002:IRON");

            Assert.IsFalse(locked.Unlocked, "A challenge absent from player-data has never been started.");
            Assert.IsNull(locked.UnlockTimeUtc);
        }

        [TestMethod]
        public void BuildAchievements_NeverSetsTrophyTypeOrCapstone()
        {
            // TrophyType drives PlayStation trophy art and the platinum/gold/silver/bronze summary
            // counts; a challenge tier there renders as a PSN trophy.
            var achievements = Build();
            Assert.IsTrue(achievements.All(a => a.TrophyType == null), "Tiers are conveyed by token art.");
            Assert.IsTrue(achievements.All(a => !a.IsCapstone), "Riot's capstones are grouping nodes, not completion markers.");
        }

        [TestMethod]
        public void BuildAchievements_MeasuresProgressAgainstEachTiersOwnThreshold()
        {
            // An earned tier reads full; the tier above shows how far the same running value came.
            Assert.AreEqual(10, Get("101001:IRON").ProgressNum);
            Assert.AreEqual(10, Get("101001:IRON").ProgressDenom);

            Assert.AreEqual(18, Get("101001:BRONZE").ProgressNum);
            Assert.AreEqual(25, Get("101001:BRONZE").ProgressDenom);
        }

        [TestMethod]
        public void BuildAchievements_NeverLetsProgressExceedItsDenominator()
        {
            foreach (var achievement in Build().Where(a => a.ProgressDenom.HasValue))
            {
                Assert.IsTrue(
                    achievement.ProgressNum <= achievement.ProgressDenom,
                    $"'{achievement.ApiName}' rendered past 100%.");
            }
        }

        [TestMethod]
        public void BuildAchievements_OmitsProgressForReverseDirectionChallenges()
        {
            var reverse = Get("900002:BRONZE");

            Assert.IsNull(reverse.ProgressNum, "Lower is better, so a rising bar would read backwards.");
            Assert.IsNull(reverse.ProgressDenom);
            Assert.IsTrue(reverse.Unlocked, "The tier is still reported as earned.");
        }

        [TestMethod]
        public void BuildAchievements_ReadsRarityFromEachTiersOwnPercentile()
        {
            // Per-tier achievements read the table at their own tier, so rarity climbs with the tier
            // instead of every tier sharing one figure.
            Assert.AreEqual(60d, Get("101000:IRON").GlobalPercentUnlocked.Value, 0.001);
            Assert.AreEqual(RarityTier.Common, Get("101000:IRON").Rarity);

            Assert.AreEqual(30d, Get("101000:BRONZE").GlobalPercentUnlocked.Value, 0.001);
            Assert.AreEqual(RarityTier.Uncommon, Get("101000:BRONZE").Rarity);

            Assert.AreEqual(4d, Get("101000:SILVER").GlobalPercentUnlocked.Value, 0.001);
            Assert.AreEqual(RarityTier.UltraRare, Get("101000:SILVER").Rarity);
        }

        [TestMethod]
        public void BuildAchievements_TreatsAZeroPercentileAsAbsentRatherThanUltraRare()
        {
            // Riot writes 0.0 where it has no figure. Reading it literally made every tier with a
            // data gap look rarer than the rarest real achievement in the game.
            Assert.AreEqual(
                4d,
                Get("101000:GOLD").GlobalPercentUnlocked.Value,
                0.001,
                "GOLD is zero-filled, so the nearest populated tier below it answers.");

            Assert.AreEqual(
                12d,
                Get("900001:IRON").GlobalPercentUnlocked.Value,
                0.001,
                "Nothing populated below IRON, so the search continues upward.");
        }

        [TestMethod]
        public void BuildAchievements_LeavesRarityUnsetWhenNoPercentileExists()
        {
            var bare = RiotChallengeMapper.BuildAchievements(
                RiotChallengeMapper.ParseMetadata(MetadataJson),
                new RiotPlayerChallengeState { Challenges = new List<RiotChallengeInfoDto>() },
                CategoryNames,
                Now);

            Assert.IsTrue(
                bare.All(a => !a.GlobalPercentUnlocked.HasValue),
                "With no percentile table there is nothing to derive rarity from.");
        }

        [TestMethod]
        public void BuildAchievements_NestsALeafUnderItsCapstoneAndCategory()
        {
            Assert.AreEqual(
                "Imagination::ARAM Authority",
                Get("101001:IRON").Category,
                "A leaf climbs past its capstone to the top-level category.");
            Assert.AreEqual(
                "Imagination",
                Get("101000:GOLD").Category,
                "A capstone parented to a category takes the localized category label.");
        }

        [TestMethod]
        public void BuildAchievements_MarksRetiredChallengesMissable()
        {
            Assert.AreEqual("Missable", Get("900001:IRON").CategoryType, "Its end timestamp has passed.");
            Assert.IsNull(Get("101001:IRON").CategoryType, "An open-ended challenge carries no category type.");
        }

        [TestMethod]
        public void BuildAchievements_GivesEachTierItsOwnTokenArt()
        {
            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/101000/tokens/gold.png",
                Get("101000:GOLD").UnlockedIconPath);

            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/101000/tokens/iron.png",
                Get("101000:IRON").UnlockedIconPath,
                "Each tier shows its own token, which is what tells the rows apart.");
        }

        [TestMethod]
        public void BuildAchievements_FallsBackToTheLowestAvailableArtForATierWithNone()
        {
            // 101000 ships art only for IRON and GOLD, so BRONZE and SILVER borrow rather than
            // rendering nothing.
            Assert.AreEqual(
                "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/challenges/config/101000/tokens/iron.png",
                Get("101000:SILVER").UnlockedIconPath);
        }

        [TestMethod]
        public void BuildAchievements_UsesTheSameArtForLockedAndUnlockedPaths()
        {
            var achievement = Get("101001:BRONZE");

            Assert.AreEqual(
                achievement.UnlockedIconPath,
                achievement.LockedIconPath,
                "Challenge tokens have no separate locked variant.");
        }

        [TestMethod]
        public void BuildAchievements_NamesEveryTierAfterItsChallenge()
        {
            foreach (var tier in new[] { "IRON", "BRONZE", "SILVER", "GOLD" })
            {
                Assert.AreEqual(
                    "ARAM Authority",
                    Get("101000:" + tier).DisplayName,
                    "Tiers share the challenge name; the token art distinguishes them.");
            }
        }

        [TestMethod]
        public void BuildAchievements_FallsBackToShortDescription()
        {
            Assert.AreEqual("Feed poros", Get("101002:IRON").Description, "description is empty, so descriptionShort wins.");
            Assert.AreEqual("Hit enemies with snowballs.", Get("101001:IRON").Description);
        }

        [TestMethod]
        public void BuildAchievements_OrdersTiersOfAChallengeTogetherAndAscending()
        {
            var ordered = Build()
                .Select(a => a.ApiName)
                .Where(name => name.StartsWith("101000:"))
                .ToArray();

            CollectionAssert.AreEqual(
                new[] { "101000:IRON", "101000:BRONZE", "101000:SILVER", "101000:GOLD" },
                ordered,
                "Provider order drives the default grid order, so tiers must climb.");
        }

        [TestMethod]
        public void BuildTierApiName_IsStableAndCanonical()
        {
            Assert.AreEqual("101000:GOLD", RiotChallengeMapper.BuildTierApiName(101000, "gold"));
            Assert.AreEqual("101000:GOLD", RiotChallengeMapper.BuildTierApiName(101000, "GOLD"));
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
