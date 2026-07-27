using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Tests.ViewModels
{
    [TestClass]
    public class AchievementToastViewModelTests
    {
        [TestMethod]
        public void Rarity_ExposesParsedToastRarityForThemeBindings()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "UltraRare"
                },
                new PersistedSettings());

            Assert.AreEqual(RarityTier.UltraRare, viewModel.Rarity);
        }

        [TestMethod]
        public void Rarity_InvalidValueFallsBackToCommon()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "not-a-tier"
                },
                new PersistedSettings());

            Assert.AreEqual(RarityTier.Common, viewModel.Rarity);
        }

        [TestMethod]
        public void CompletionNotification_IsGameCompletedMarksTheStandaloneToast()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    IsGameCompleted = true
                },
                new PersistedSettings
                {
                    ToastShowRarityBadge = true,
                    FrameShowRarityBadge = true
                });

            Assert.IsTrue(viewModel.IsGameCompleted);
            Assert.IsFalse(viewModel.IsCapstone);
            // No capstone/trophy/rarity data on the completion notification, so the secondary
            // badge resolves to hidden/null without any completion special-casing.
            Assert.IsFalse(viewModel.ShowBadge);
            Assert.IsNull(viewModel.BadgeImage);
            Assert.IsFalse(viewModel.FrameShowBadge);
            // The capstone-tier sound covers the completion notification.
            Assert.AreEqual("capstoneachievement", viewModel.SoundTierSegment);
            Assert.AreEqual(5, viewModel.SoundTierRank);
        }

        [TestMethod]
        public void CompletionPalette_AlwaysAvailableRegardlessOfKind()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "Rare",
                    GlobalPercent = 9.3
                },
                new PersistedSettings
                {
                    ToastShowRarityGlow = true,
                    FrameShowRarityGlow = true
                });

            Assert.IsFalse(viewModel.IsGameCompleted);
            Assert.IsNotNull(viewModel.CompletedBrush);
            Assert.IsNotNull(viewModel.CompletedGlowEffect);
            Assert.IsNotNull(viewModel.FrameCompletedGlowEffect);
            Assert.IsNotNull(viewModel.CompletedBadgeImage);
            Assert.IsNotNull(viewModel.RarityBrush);
        }

        [TestMethod]
        public void CompletedGlows_HonorTheRarityGlowToggles()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    IsGameCompleted = true
                },
                new PersistedSettings
                {
                    ToastShowRarityGlow = false,
                    FrameShowRarityGlow = false
                });

            Assert.IsNull(viewModel.CompletedGlowEffect);
            Assert.IsNull(viewModel.FrameCompletedGlowEffect);
        }

        [TestMethod]
        public void RegularUnlock_KeepsAchievementIconAndOwnBadge()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    IconPath = "achievement.png",
                    RarityTier = "Rare",
                    GlobalPercent = 9.3
                },
                new PersistedSettings
                {
                    ToastShowRarityBadge = true,
                    FrameShowRarityBadge = true
                });

            Assert.AreEqual("achievement.png", viewModel.IconPath);
            Assert.IsFalse(viewModel.IsGameCompleted);
            Assert.IsTrue(viewModel.ShowBadge);
            Assert.IsTrue(viewModel.FrameShowBadge);
        }

        [TestMethod]
        public void DataBindings_ExposeTrophyCountsPointsAndGameState()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    TrophyType = "platinum",
                    UnlockedCount = 27,
                    TotalCount = 40,
                    Points = 90,
                    ScaledPoints = 180,
                    GameIconPath = @"c:\playnite\icon.png",
                    GameCoverPath = @"c:\playnite\cover.jpg",
                    IsCompletionAchievement = true
                },
                new PersistedSettings());

            Assert.AreEqual("Platinum", viewModel.TrophyType);
            Assert.AreEqual(27, viewModel.UnlockedCount);
            Assert.AreEqual(40, viewModel.TotalCount);
            Assert.AreEqual(90, viewModel.Points);
            Assert.AreEqual(180, viewModel.ScaledPoints);
            Assert.AreEqual(@"c:\playnite\icon.png", viewModel.GameIconPath);
            Assert.AreEqual(@"c:\playnite\cover.jpg", viewModel.GameCoverPath);
            // Game state, distinct from the completion-notification kind.
            Assert.IsTrue(viewModel.IsCompletionAchievement);
            Assert.IsFalse(viewModel.IsGameCompleted);
        }

        [TestMethod]
        public void TrophyType_EmptyWithoutTrophyData()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "Rare"
                },
                new PersistedSettings());

            Assert.AreEqual(string.Empty, viewModel.TrophyType);
            Assert.IsNull(viewModel.Points);
        }

        [TestMethod]
        public void FriendDisplayName_FallsBackWhenMissing()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    IsFriendUnlock = true,
                    IsGameCompleted = true
                },
                new PersistedSettings());

            Assert.AreEqual("Friend", viewModel.FriendDisplayName);
        }
    }
}
