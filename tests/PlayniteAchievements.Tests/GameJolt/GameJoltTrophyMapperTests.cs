using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.GameJolt;

namespace PlayniteAchievements.GameJolt.Tests
{
    [TestClass]
    public class GameJoltTrophyMapperTests
    {
        private const string DefinitionsJson = @"{
            ""payload"": {
                ""trophies"": [
                    { ""id"": 101, ""game_id"": 42, ""title"": ""First Steps"", ""description"": ""Start the game."", ""difficulty"": 1, ""experience"": 20, ""secret"": false, ""img_thumbnail"": ""//m.gamejolt.net/a.png"" },
                    { ""id"": 102, ""game_id"": 42, ""title"": ""Halfway"", ""description"": ""Reach the midpoint."", ""difficulty"": 3, ""experience"": 100, ""secret"": true, ""img_thumbnail"": ""https://m.gamejolt.net/b.png"" },
                    { ""id"": 999, ""game_id"": 7, ""title"": ""Other Game"", ""description"": ""Should be filtered out."", ""difficulty"": 2, ""experience"": 50, ""secret"": false, ""img_thumbnail"": """" }
                ]
            }
        }";

        [TestMethod]
        public void BuildDefinitions_FiltersByGameIdAndMapsFields()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");

            Assert.AreEqual(2, achievements.Count, "Only trophies for game 42 should be returned.");

            var first = achievements.Single(a => a.ApiName == "101");
            Assert.AreEqual("First Steps", first.DisplayName);
            Assert.AreEqual("Start the game.", first.Description);
            Assert.AreEqual(20, first.Points, "experience maps to Points.");
            Assert.AreEqual("bronze", first.TrophyType, "difficulty 1 maps to bronze.");
            Assert.AreEqual(RarityTier.Common, first.Rarity, "difficulty 1 (bronze) maps to Common.");
            Assert.IsFalse(first.Hidden);
            Assert.IsFalse(first.Unlocked);
            Assert.AreEqual("https://m.gamejolt.net/a.png", first.UnlockedIconPath, "Protocol-relative URL should be normalized to https.");

            var second = achievements.Single(a => a.ApiName == "102");
            Assert.AreEqual(100, second.Points);
            Assert.AreEqual("gold", second.TrophyType, "difficulty 3 maps to gold.");
            Assert.AreEqual(RarityTier.Rare, second.Rarity, "difficulty 3 (gold) maps to Rare.");
            Assert.IsTrue(second.Hidden, "secret=true maps to Hidden.");
            Assert.AreEqual("https://m.gamejolt.net/b.png", second.UnlockedIconPath);
        }

        [TestMethod]
        public void ApplyUnlocks_MarksUnlockedAndConvertsEpochMillis()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");

            // 1700000000000 ms = 2023-11-14T22:13:20Z
            var profileJson = @"{
                ""payload"": {
                    ""trophies"": [
                        { ""game_id"": 42, ""game_trophy_id"": 101, ""logged_on"": 1700000000000, ""game_trophy"": { ""img_thumbnail"": ""//m.gamejolt.net/unlocked-101.png"" } },
                        { ""game_id"": 42, ""game_trophy_id"": 102, ""logged_on"": null, ""game_trophy"": { ""img_thumbnail"": null } }
                    ]
                }
            }";

            GameJoltTrophyMapper.ApplyUnlocks(achievements, profileJson, "42");

            var withDate = achievements.Single(a => a.ApiName == "101");
            Assert.IsTrue(withDate.Unlocked);
            Assert.AreEqual(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), withDate.UnlockTimeUtc);
            Assert.AreEqual("https://m.gamejolt.net/unlocked-101.png", withDate.UnlockedIconPath);

            var withoutDate = achievements.Single(a => a.ApiName == "102");
            Assert.IsTrue(withoutDate.Unlocked, "A trophy present with null logged_on is still unlocked.");
            Assert.IsNull(withoutDate.UnlockTimeUtc, "Null logged_on must not synthesize a sentinel date.");
        }

        [TestMethod]
        public void ApplyUnlocks_LeavesTrophiesAbsentFromProfileLocked()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");

            var profileJson = @"{ ""payload"": { ""trophies"": [
                { ""game_id"": 42, ""game_trophy_id"": 101, ""logged_on"": 1700000000000 }
            ] } }";

            GameJoltTrophyMapper.ApplyUnlocks(achievements, profileJson, "42");

            Assert.IsTrue(achievements.Single(a => a.ApiName == "101").Unlocked);
            Assert.IsFalse(achievements.Single(a => a.ApiName == "102").Unlocked, "Trophy not in the profile response stays locked.");
        }

        [TestMethod]
        public void BuildDefinitions_MalformedJson_ReturnsEmpty()
        {
            Assert.AreEqual(0, GameJoltTrophyMapper.BuildDefinitions("not json", "42").Count);
            Assert.AreEqual(0, GameJoltTrophyMapper.BuildDefinitions(null, "42").Count);
            Assert.AreEqual(0, GameJoltTrophyMapper.BuildDefinitions("{}", "42").Count);
        }

        [TestMethod]
        public void ApplyUnlocks_MalformedJson_LeavesAchievementsUnchanged()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");

            GameJoltTrophyMapper.ApplyUnlocks(achievements, "not json", "42");

            Assert.IsTrue(achievements.All(a => !a.Unlocked));
        }

        [TestMethod]
        public void ParseUsername_ReadsPayloadUser()
        {
            var json = @"{ ""payload"": { ""user"": { ""id"": 5, ""username"": ""cooldev"", ""img_avatar"": ""x"" } } }";
            Assert.AreEqual("cooldev", GameJoltTrophyMapper.ParseUsername(json));
        }

        [TestMethod]
        public void ParseUsername_MissingUser_ReturnsNull()
        {
            Assert.IsNull(GameJoltTrophyMapper.ParseUsername(@"{ ""payload"": {} }"));
            Assert.IsNull(GameJoltTrophyMapper.ParseUsername("garbage"));
            Assert.IsNull(GameJoltTrophyMapper.ParseUsername(null));
        }

        [TestMethod]
        public void EpochMillisToUtc_HandlesNullAndNonPositive()
        {
            Assert.IsNull(GameJoltTrophyMapper.EpochMillisToUtc(null));
            Assert.IsNull(GameJoltTrophyMapper.EpochMillisToUtc(0));
            Assert.AreEqual(
                DateTimeOffset.FromUnixTimeMilliseconds(1700000000000).UtcDateTime,
                GameJoltTrophyMapper.EpochMillisToUtc(1700000000000));
        }

        [TestMethod]
        public void ApplyAchievedRecords_MarksCompleteAchievedListFromDefinitionsPayload()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");

            // The definitions response also carries the user's full achieved list under trophiesAchieved.
            var definitionsWithAchieved = @"{
                ""payload"": {
                    ""trophies"": [
                        { ""id"": 101, ""game_id"": 42, ""title"": ""First Steps"", ""difficulty"": 1, ""experience"": 20 },
                        { ""id"": 102, ""game_id"": 42, ""title"": ""Halfway"", ""difficulty"": 3, ""experience"": 100 }
                    ],
                    ""trophiesAchieved"": [
                        { ""game_id"": 42, ""game_trophy_id"": 101, ""logged_on"": 1700000000000 },
                        { ""game_id"": 42, ""game_trophy_id"": 102, ""logged_on"": null },
                        { ""game_id"": 7, ""game_trophy_id"": 999, ""logged_on"": 1700000000000 }
                    ]
                }
            }";

            var applied = GameJoltTrophyMapper.ApplyAchievedRecords(achievements, definitionsWithAchieved, "42");

            Assert.AreEqual(2, applied, "Both game-42 achieved records apply; the game-7 record is ignored.");

            var withDate = achievements.Single(a => a.ApiName == "101");
            Assert.IsTrue(withDate.Unlocked);
            Assert.AreEqual(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), withDate.UnlockTimeUtc);

            var withoutDate = achievements.Single(a => a.ApiName == "102");
            Assert.IsTrue(withoutDate.Unlocked, "Achieved with null logged_on is still unlocked.");
            Assert.IsNull(withoutDate.UnlockTimeUtc);
        }

        [TestMethod]
        public void ApplyAchievedRecords_NoAchievedList_ReturnsZero()
        {
            var achievements = GameJoltTrophyMapper.BuildDefinitions(DefinitionsJson, "42");
            Assert.AreEqual(0, GameJoltTrophyMapper.ApplyAchievedRecords(achievements, @"{ ""payload"": { ""trophies"": [] } }", "42"));
            Assert.AreEqual(0, GameJoltTrophyMapper.ApplyAchievedRecords(achievements, "garbage", "42"));
            Assert.IsTrue(achievements.All(a => !a.Unlocked));
        }

        [TestMethod]
        public void ParsePercentage_ReadsPayloadPercentage()
        {
            Assert.AreEqual(12.5, GameJoltTrophyMapper.ParsePercentage(@"{ ""payload"": { ""percentage"": 12.5 } }"));
            Assert.AreEqual(0d, GameJoltTrophyMapper.ParsePercentage(@"{ ""payload"": { ""percentage"": 0 } }"));
            Assert.IsNull(GameJoltTrophyMapper.ParsePercentage(@"{ ""payload"": {} }"));
            Assert.IsNull(GameJoltTrophyMapper.ParsePercentage("garbage"));
            Assert.IsNull(GameJoltTrophyMapper.ParsePercentage(null));
        }

        [TestMethod]
        public void ApplyPercentage_SetsPercentAndPercentBasedRarity()
        {
            // Default stub thresholds: <=5 UltraRare, <=10 Rare, <=50 Uncommon, else Common.
            var rare = new AchievementDetail { Rarity = RarityTier.Common };
            GameJoltTrophyMapper.ApplyPercentage(rare, 3.0);
            Assert.AreEqual(3.0, rare.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.UltraRare, rare.Rarity);

            var common = new AchievementDetail { Rarity = RarityTier.UltraRare };
            GameJoltTrophyMapper.ApplyPercentage(common, 80.0);
            Assert.AreEqual(80.0, common.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.Common, common.Rarity);

            // Clamps out-of-range input.
            var clamped = new AchievementDetail();
            GameJoltTrophyMapper.ApplyPercentage(clamped, 150.0);
            Assert.AreEqual(100.0, clamped.GlobalPercentUnlocked);
        }

        [TestMethod]
        public void ApplyPercentage_NullLeavesDifficultyRarityIntact()
        {
            var achievement = new AchievementDetail { Rarity = RarityTier.Rare };
            GameJoltTrophyMapper.ApplyPercentage(achievement, null);
            Assert.IsNull(achievement.GlobalPercentUnlocked);
            Assert.AreEqual(RarityTier.Rare, achievement.Rarity, "Difficulty-based rarity must remain when no percentage is available.");
        }

        [TestMethod]
        public void ResolveRarity_MapsDifficultyTiers()
        {
            Assert.AreEqual(RarityTier.UltraRare, GameJoltTrophyMapper.ResolveRarity(4), "4 = platinum");
            Assert.AreEqual(RarityTier.Rare, GameJoltTrophyMapper.ResolveRarity(3), "3 = gold");
            Assert.AreEqual(RarityTier.Uncommon, GameJoltTrophyMapper.ResolveRarity(2), "2 = silver");
            Assert.AreEqual(RarityTier.Common, GameJoltTrophyMapper.ResolveRarity(1), "1 = bronze");
            Assert.AreEqual(RarityTier.Common, GameJoltTrophyMapper.ResolveRarity(0), "unknown = common");
        }

        [TestMethod]
        public void FormatUser_EnsuresSingleLeadingAt()
        {
            Assert.AreEqual("@cooldev", GameJoltTrophyMapper.FormatUser("cooldev"));
            Assert.AreEqual("@cooldev", GameJoltTrophyMapper.FormatUser("@cooldev"));
            Assert.AreEqual("@cooldev", GameJoltTrophyMapper.FormatUser("  cooldev  "));
        }

        [TestMethod]
        public void ExtractUsernameFromHtml_ReadsUsernameDiv()
        {
            var html = "<div class=\"-username\">Hey @cooldev</div>";
            Assert.AreEqual("cooldev", GameJoltTrophyMapper.ExtractUsernameFromHtml(html));

            Assert.IsNull(GameJoltTrophyMapper.ExtractUsernameFromHtml("<div>no username here</div>"));
            Assert.IsNull(GameJoltTrophyMapper.ExtractUsernameFromHtml(null));
        }
    }
}
