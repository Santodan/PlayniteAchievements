using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services.Summaries
{
    [TestClass]
    public class CategorySummaryBuilderTests
    {
        [TestMethod]
        public void Build_GroupsByLabelInFirstSeenOrderWithAggregatedStats()
        {
            var items = new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true),
                DisplayItem("Base", unlocked: false),
                DisplayItem("DLC", unlocked: true)
            };

            var result = CategorySummaryBuilder.Build(items);

            Assert.AreEqual(2, result.Count);
            var dlc = (CategorySummaryItem)result[0];
            var baseCat = (CategorySummaryItem)result[1];

            // First-seen ordering: DLC appears before Base.
            Assert.AreEqual("DLC", dlc.CategoryLabel);
            Assert.AreEqual("Base", baseCat.CategoryLabel);

            // A non-default label is shown verbatim as the row name.
            Assert.AreEqual("DLC", dlc.GameName);

            Assert.AreEqual(2, dlc.TotalAchievements);
            Assert.AreEqual(2, dlc.UnlockedAchievements);
            Assert.AreEqual(1, baseCat.TotalAchievements);
            Assert.AreEqual(0, baseCat.UnlockedAchievements);
        }

        [TestMethod]
        public void Build_SharedCategoryArtFillsBothImageSlots()
        {
            var shared = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "art.png"),
                DisplayItem("DLC", unlocked: false, categoryArt: "art.png")
            });
            var sharedCategory = (CategorySummaryItem)shared.Single();
            Assert.AreEqual("art.png", sharedCategory.GameLogo);
            Assert.AreEqual("art.png", sharedCategory.GameCoverPath);

            var mixed = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "a.png"),
                DisplayItem("DLC", unlocked: false, categoryArt: "b.png")
            });
            var mixedCategory = (CategorySummaryItem)mixed.Single();
            Assert.IsNull(mixedCategory.GameLogo);
            Assert.IsNull(mixedCategory.GameCoverPath);
        }

        [TestMethod]
        public void Build_ArtlessCategoriesFallBackToSharedGameImages()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, gameIcon: "game-icon.png", gameCover: "game-cover.png"),
                DisplayItem("DLC", unlocked: false, gameIcon: "game-icon.png", gameCover: "game-cover.png")
            });
            var category = (CategorySummaryItem)result.Single();
            Assert.AreEqual("game-icon.png", category.GameLogo);
            Assert.AreEqual("game-cover.png", category.GameCoverPath);
        }

        [TestMethod]
        public void Build_SharedCategoryArtBeatsGameImages()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, categoryArt: "art.png", gameIcon: "game-icon.png", gameCover: "game-cover.png")
            });
            var category = (CategorySummaryItem)result.Single();
            Assert.AreEqual("art.png", category.GameLogo);
            Assert.AreEqual("art.png", category.GameCoverPath);
        }

        [TestMethod]
        public void Build_SetsSharedGameIdOnlyWhenCategoryBucketAgrees()
        {
            var gameId = Guid.NewGuid();
            var shared = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, playniteGameId: gameId),
                DisplayItem("DLC", unlocked: false, playniteGameId: gameId)
            });
            Assert.AreEqual(gameId, shared.Single().PlayniteGameId);

            var mixed = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true, playniteGameId: gameId),
                DisplayItem("DLC", unlocked: false, playniteGameId: Guid.NewGuid())
            });
            Assert.IsNull(mixed.Single().PlayniteGameId);
        }

        [TestMethod]
        public void Build_UsesCategoryOrderIndexBeforeFirstSeenRemainder()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: false, categoryOrderIndex: 1),
                DisplayItem("Event", unlocked: false),
                DisplayItem("DLC", unlocked: true, categoryOrderIndex: 0)
            });

            CollectionAssert.AreEqual(
                new[] { "DLC", "Base", "Event" },
                result.Cast<CategorySummaryItem>().Select(item => item.CategoryLabel).ToList());
        }

        [TestMethod]
        public void Build_BlankLabelsCollapseToASingleDefaultCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem(null, unlocked: true),
                DisplayItem("", unlocked: false)
            });

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(2, result[0].TotalAchievements);
        }

        [TestMethod]
        public void Build_NullOrEmptyReturnsEmptyList()
        {
            Assert.AreEqual(0, CategorySummaryBuilder.Build(null).Count);
            Assert.AreEqual(0, CategorySummaryBuilder.Build(new List<AchievementDisplayItem>()).Count);
        }

        [TestMethod]
        public void Build_FullyUnlockedCategoryIsCompleted()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("DLC", unlocked: true),
                DisplayItem("DLC", unlocked: true)
            });

            Assert.IsTrue(result.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_UnlockedCapstoneCompletesPartialCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true, isCapstone: true),
                DisplayItem("Base", unlocked: false)
            });

            Assert.IsTrue(result.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_LockedCapstoneOrPartialUnlocksStayIncomplete()
        {
            var lockedCapstone = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: false, isCapstone: true),
                DisplayItem("Base", unlocked: true)
            });
            Assert.IsFalse(lockedCapstone.Single().IsCompleted);

            var partial = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true),
                DisplayItem("Base", unlocked: false)
            });
            Assert.IsFalse(partial.Single().IsCompleted);
        }

        [TestMethod]
        public void Build_CapstoneOnlyCompletesItsOwnCategory()
        {
            var result = CategorySummaryBuilder.Build(new List<AchievementDisplayItem>
            {
                DisplayItem("Base", unlocked: true, isCapstone: true),
                DisplayItem("DLC", unlocked: false)
            });

            var byLabel = result.Cast<CategorySummaryItem>().ToDictionary(item => item.CategoryLabel);
            Assert.IsTrue(byLabel["Base"].IsCompleted);
            Assert.IsFalse(byLabel["DLC"].IsCompleted);
        }

        private static AchievementDisplayItem DisplayItem(
            string label,
            bool unlocked,
            string categoryArt = null,
            string gameIcon = null,
            string gameCover = null,
            int categoryOrderIndex = int.MaxValue,
            Guid? playniteGameId = null,
            bool isCapstone = false)
        {
            return new AchievementDisplayItem
            {
                Rarity = RarityTier.Common,
                PlayniteGameId = playniteGameId,
                CategoryLabel = label,
                Unlocked = unlocked,
                IsCapstone = isCapstone,
                CategoryArtPath = categoryArt,
                GameIconPath = gameIcon,
                GameCoverPath = gameCover,
                CategoryOrderIndex = categoryOrderIndex
            };
        }
    }
}
