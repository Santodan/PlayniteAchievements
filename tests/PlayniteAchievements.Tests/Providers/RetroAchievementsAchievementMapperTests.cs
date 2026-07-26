using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.RetroAchievements.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Tests.Providers
{
    [TestClass]
    public class RetroAchievementsAchievementMapperTests
    {
        [TestMethod]
        public void ParseRaUtcTimestamp_AcceptsClassicAndIsoFormats()
        {
            var classic = RetroAchievementsAchievementMapper.ParseRaUtcTimestamp("2025-06-11 13:05:22");
            var iso = RetroAchievementsAchievementMapper.ParseRaUtcTimestamp("2025-06-11T13:05:22+00:00");

            Assert.AreEqual(new DateTime(2025, 6, 11, 13, 5, 22, DateTimeKind.Utc), classic);
            Assert.AreEqual(new DateTime(2025, 6, 11, 13, 5, 22, DateTimeKind.Utc), iso);
        }

        [TestMethod]
        public void NormalizeImageUrl_ExpandsRetroAchievementsRelativePaths()
        {
            Assert.AreEqual(
                "https://retroachievements.org/Images/123.png",
                RetroAchievementsAchievementMapper.NormalizeImageUrl("/Images/123.png"));
            Assert.AreEqual(
                "https://retroachievements.org/Images/123.png",
                RetroAchievementsAchievementMapper.NormalizeImageUrl("Images/123.png"));
            Assert.AreEqual(
                "https://cdn.example/image.png",
                RetroAchievementsAchievementMapper.NormalizeImageUrl("https://cdn.example/image.png"));
        }

        [TestMethod]
        public void ParseAchievements_MapsUnlockModeRarityBadgesAndCapstone()
        {
            var gameInfo = new RaGameInfoUserProgress
            {
                NumDistinctPlayers = 100,
                NumDistinctPlayersCasual = 100,
                NumDistinctPlayersHardcore = 20,
                Achievements = new Dictionary<string, RaAchievement>
                {
                    ["101"] = new RaAchievement
                    {
                        Title = "Soft Win",
                        Description = "Softcore unlock",
                        BadgeName = "12345",
                        DateEarned = "2025-06-11 13:05:22",
                        NumAwarded = 50,
                        NumAwardedHardcore = 10,
                        Points = 5,
                        TrueRatio = 10
                    },
                    ["102"] = new RaAchievement
                    {
                        Title = "Hard Win",
                        Description = "Hardcore unlock",
                        BadgeName = "67890",
                        DateEarned = "2025-06-10 01:00:00",
                        DateEarnedHardcore = "2025-06-12 02:00:00",
                        NumAwarded = 40,
                        NumAwardedHardcore = 4,
                        Points = 25,
                        TrueRatio = 100,
                        Type = "win_condition"
                    }
                }
            };

            var achievements = RetroAchievementsAchievementMapper.ParseAchievements(
                gameInfo,
                rarityStats: "casual",
                categoryLabel: "Base",
                enableAutomaticCapstoneAssignment: true,
                setCategoryType: "Base");

            Assert.AreEqual(2, achievements.Count);

            // Base-set achievements carry the "Base" type combined with the unlock mode.
            var soft = achievements.Single(item => item.ApiName == "101");
            Assert.AreEqual("Base", soft.Category);
            Assert.AreEqual("Base|Softcore", soft.CategoryType);
            Assert.AreEqual(new DateTime(2025, 6, 11, 13, 5, 22, DateTimeKind.Utc), soft.UnlockTimeUtc);
            Assert.AreEqual("https://i.retroachievements.org/Badge/12345.png", soft.UnlockedIconPath);
            Assert.AreEqual("https://i.retroachievements.org/Badge/12345_lock.png", soft.LockedIconPath);

            var hard = achievements.Single(item => item.ApiName == "102");
            Assert.AreEqual("Hardcore", hard.CategoryType);
            Assert.AreEqual(new DateTime(2025, 6, 12, 2, 0, 0, DateTimeKind.Utc), hard.UnlockTimeUtc);
            Assert.IsTrue(hard.IsCapstone);

            var rows = RetroAchievementsAchievementMapper.ToFriendRows(achievements);
            Assert.AreEqual(2, rows.Count);
            Assert.IsTrue(rows.All(row => row.Unlocked));
            Assert.AreEqual("https://i.retroachievements.org/Badge/12345.png", rows.Single(row => row.ApiName == "101").IconUrl);
        }

        [TestMethod]
        public void ParseAchievements_SubsetCombinesSubsetTypeWithUnlockMode()
        {
            var gameInfo = new RaGameInfoUserProgress
            {
                NumDistinctPlayers = 100,
                NumDistinctPlayersCasual = 100,
                NumDistinctPlayersHardcore = 20,
                Achievements = new Dictionary<string, RaAchievement>
                {
                    ["201"] = new RaAchievement
                    {
                        Title = "Soft Subset Win",
                        DateEarned = "2025-06-11 13:05:22",
                        NumAwarded = 50
                    },
                    ["202"] = new RaAchievement
                    {
                        Title = "Hard Subset Win",
                        DateEarned = "2025-06-10 01:00:00",
                        DateEarnedHardcore = "2025-06-12 02:00:00",
                        NumAwarded = 40,
                        NumAwardedHardcore = 4
                    },
                    ["203"] = new RaAchievement
                    {
                        Title = "Locked Subset",
                        NumAwarded = 10
                    }
                }
            };

            var achievements = RetroAchievementsAchievementMapper.ParseAchievements(
                gameInfo,
                rarityStats: "casual",
                categoryLabel: "Bonus",
                enableAutomaticCapstoneAssignment: false,
                setCategoryType: "Subset");

            // The free-form label is unchanged; only the canonical type gains "Subset",
            // combined with the unlock mode in canonical order (Subset before Softcore/Hardcore).
            Assert.AreEqual("Bonus", achievements.Single(item => item.ApiName == "201").Category);
            Assert.AreEqual("Subset|Softcore", achievements.Single(item => item.ApiName == "201").CategoryType);
            Assert.AreEqual("Subset|Hardcore", achievements.Single(item => item.ApiName == "202").CategoryType);
            Assert.AreEqual("Subset", achievements.Single(item => item.ApiName == "203").CategoryType);
        }

        [TestMethod]
        public void ExtractCategoryLabel_PreservesScannerSubsetPatterns()
        {
            Assert.AreEqual("Bonus", RetroAchievementsAchievementMapper.ExtractCategoryLabel("Game [Subset - Bonus]"));
            Assert.AreEqual("Hub", RetroAchievementsAchievementMapper.ExtractCategoryLabel("Game [Hub]"));
            Assert.AreEqual("Challenge", RetroAchievementsAchievementMapper.ExtractCategoryLabel("Game (Subset - Challenge)"));
            Assert.AreEqual("Shiny Pokemon+", RetroAchievementsAchievementMapper.ExtractCategoryLabel("Pokemon FireRed Version | Pokemon LeafGreen Version Subset Shiny Pokemon+"));
        }
    }
}
