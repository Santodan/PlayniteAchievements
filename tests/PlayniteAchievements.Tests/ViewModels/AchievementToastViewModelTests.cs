using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.ViewModels;
using System;
using System.IO;

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
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = new NotificationSurfaceStyle { ShowRarityBadge = true },
                        Frame = new NotificationSurfaceStyle { ShowRarityBadge = true }
                    }
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
            Assert.AreEqual(6, viewModel.SoundTierRank);
        }

        [TestMethod]
        public void HiddenUnlock_UsesHiddenSoundOnlyWhenTheSettingIsOn()
        {
            var args = new AchievementUnlockedEventArgs
            {
                RarityTier = "Rare",
                IsHidden = true
            };

            var optedOut = new AchievementToastViewModel(
                args,
                new PersistedSettings { UseHiddenUnlockSound = false });

            Assert.AreEqual("rareachievement", optedOut.SoundTierSegment);
            Assert.AreEqual(3, optedOut.SoundTierRank);

            var optedIn = new AchievementToastViewModel(
                args,
                new PersistedSettings { UseHiddenUnlockSound = true });

            Assert.AreEqual(AchievementToastViewModel.HiddenSoundSegment, optedIn.SoundTierSegment);
            // Hidden outranks every rarity tier so it wins its wave, but stays under capstone.
            Assert.AreEqual(5, optedIn.SoundTierRank);
        }

        [TestMethod]
        public void HiddenUnlock_LeavesNonHiddenUnlocksOnTheirRarityTier()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "UltraRare",
                    IsHidden = false
                },
                new PersistedSettings { UseHiddenUnlockSound = true });

            Assert.AreEqual("ultrarareachievement", viewModel.SoundTierSegment);
            Assert.AreEqual(4, viewModel.SoundTierRank);
        }

        [TestMethod]
        public void HiddenCapstone_PlaysHiddenButKeepsCapstoneWaveRank()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "Rare",
                    IsHidden = true,
                    IsCapstone = true
                },
                new PersistedSettings { UseHiddenUnlockSound = true });

            // The segment order puts hidden first, the rank order keeps capstone on top: a hidden
            // capstone plays the hidden sound while still ranking as a capstone in its wave.
            Assert.AreEqual(AchievementToastViewModel.HiddenSoundSegment, viewModel.SoundTierSegment);
            Assert.AreEqual(6, viewModel.SoundTierRank);
        }

        [TestMethod]
        public void CompletionPalette_AlwaysAvailableRegardlessOfKind()
        {
            // CompletedBadgeImage builds from pack://.../PlayniteAchievements;component/Resources/
            // RarityBadges.xaml geometry, which must be materialized on an STA apartment.
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var viewModel = new AchievementToastViewModel(
                    new AchievementUnlockedEventArgs
                    {
                        RarityTier = "Rare",
                        GlobalPercent = 9.3
                    },
                    new PersistedSettings
                    {
                        NotificationStyle = new NotificationStyleSettings
                        {
                            Toast = new NotificationSurfaceStyle { ShowRarityGlow = true },
                            Frame = new NotificationSurfaceStyle { ShowRarityGlow = true }
                        }
                    });

                Assert.IsFalse(viewModel.IsGameCompleted);
                Assert.IsNotNull(viewModel.CompletedBrush);
                Assert.IsNotNull(viewModel.CompletedGlowEffect);
                Assert.IsNotNull(viewModel.FrameCompletedGlowEffect);
                Assert.IsNotNull(viewModel.CompletedBadgeImage);
                Assert.IsNotNull(viewModel.RarityBrush);
            });
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
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = new NotificationSurfaceStyle { ShowRarityGlow = false },
                        Frame = new NotificationSurfaceStyle { ShowRarityGlow = false }
                    }
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
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = new NotificationSurfaceStyle { ShowRarityBadge = true },
                        Frame = new NotificationSurfaceStyle { ShowRarityBadge = true }
                    }
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

        [TestMethod]
        public void GameAppearance_AppliesToOwnFriendAndCompletionViewModels()
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "PlayniteAchievementsTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);

            try
            {
                var gameId = Guid.NewGuid();
                var settings = new PersistedSettings();
                settings.NotificationStyle.Toast.ShowGameName = true;
                settings.NotificationStyle.Frame.ShowGameName = true;

                var gameStyle = NotificationStyleSettings.CreateDefault();
                gameStyle.Toast.ShowGameName = false;
                gameStyle.Frame.ShowGameName = false;
                var store = new GameCustomDataStore(tempDirectory);
                store.Save(gameId, new GameCustomDataFile
                {
                    PlayniteGameId = gameId,
                    NotificationAppearanceOverride = new GameNotificationAppearanceOverride
                    {
                        Style = gameStyle,
                        ToastUseThemeStyling = false,
                        FrameUseThemeStyling = false
                    }
                });

                foreach (var args in new[]
                {
                    new AchievementUnlockedEventArgs(),
                    new AchievementUnlockedEventArgs { IsFriendUnlock = true },
                    new AchievementUnlockedEventArgs { IsGameCompleted = true }
                })
                {
                    args.PlayniteGameId = gameId;
                    args.GameName = "Test Game";
                    var viewModel = new AchievementToastViewModel(
                        args,
                        settings,
                        styleOverride: null,
                        gameCustomDataStore: store);

                    Assert.IsFalse(viewModel.ShowGameName);
                    Assert.IsFalse(viewModel.FrameShowGameName);
                    Assert.IsFalse(viewModel.ToastUseThemeStyling);
                    Assert.IsFalse(viewModel.FrameUseThemeStyling);
                }
            }
            finally
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }
    }
}
