using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.ViewModels;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

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
        public void ComputeNotifyReadyUtc_NoDelayOrExemptArgs_AlwaysReady()
        {
            var enqueued = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            var observed = enqueued.AddSeconds(-2);

            Assert.AreEqual(
                default(DateTime),
                AchievementToastViewModel.ComputeNotifyReadyUtc(
                    new AchievementUnlockedEventArgs { ObservedUtc = observed },
                    0,
                    enqueued),
                "zero delay must not stamp a ready time");

            Assert.AreEqual(
                default(DateTime),
                AchievementToastViewModel.ComputeNotifyReadyUtc(
                    new AchievementUnlockedEventArgs { ObservedUtc = observed },
                    -1,
                    enqueued),
                "a negative delay must not stamp a ready time");

            var delayed = 5;
            Assert.AreEqual(
                default(DateTime),
                AchievementToastViewModel.ComputeNotifyReadyUtc(
                    new AchievementUnlockedEventArgs { ObservedUtc = observed, IsPreview = true },
                    delayed,
                    enqueued),
                "previews are exempt from the notification delay");

            Assert.AreEqual(
                default(DateTime),
                AchievementToastViewModel.ComputeNotifyReadyUtc(
                    new AchievementUnlockedEventArgs { ObservedUtc = observed, IsTestFire = true },
                    delayed,
                    enqueued),
                "test fires are exempt from the notification delay");

            Assert.AreEqual(
                default(DateTime),
                AchievementToastViewModel.ComputeNotifyReadyUtc(null, delayed, enqueued),
                "null args must not stamp a ready time");
        }

        [TestMethod]
        public void ComputeNotifyReadyUtc_AnchorsOnObservationSoLatencyCountsTowardDelay()
        {
            var observed = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);
            // Enqueue lands later than the observation (dispatcher/queue latency); the ready
            // instant must still be observation + delay, not enqueue + delay.
            var enqueued = observed.AddSeconds(0.8);

            var readyAt = AchievementToastViewModel.ComputeNotifyReadyUtc(
                new AchievementUnlockedEventArgs { ObservedUtc = observed },
                5,
                enqueued);

            Assert.AreEqual(observed.AddSeconds(5), readyAt);
        }

        [TestMethod]
        public void ComputeNotifyReadyUtc_NoObservationStampFallsBackToEnqueueAnchor()
        {
            // Friend unlocks carry no ObservedUtc; the enqueue instant anchors the delay instead.
            var enqueued = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

            var readyAt = AchievementToastViewModel.ComputeNotifyReadyUtc(
                new AchievementUnlockedEventArgs { IsFriendUnlock = true },
                3,
                enqueued);

            Assert.AreEqual(enqueued.AddSeconds(3), readyAt);
        }

        [TestMethod]
        public void ComputeNotifyReadyUtc_NormalizesNonUtcObservationKind()
        {
            var localObserved = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Local);

            var readyAt = AchievementToastViewModel.ComputeNotifyReadyUtc(
                new AchievementUnlockedEventArgs { ObservedUtc = localObserved },
                2,
                localObserved.ToUniversalTime());

            Assert.AreEqual(localObserved.ToUniversalTime().AddSeconds(2), readyAt);
            Assert.AreEqual(DateTimeKind.Utc, readyAt.Kind);
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
            Assert.AreEqual(UnlockSoundTier.Capstone, viewModel.SoundTier);
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

            Assert.AreEqual(UnlockSoundTier.Rare, optedOut.SoundTier);
            Assert.AreEqual(3, optedOut.SoundTierRank);

            var optedIn = new AchievementToastViewModel(
                args,
                new PersistedSettings { UseHiddenUnlockSound = true });

            Assert.AreEqual(UnlockSoundTier.Hidden, optedIn.SoundTier);
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

            Assert.AreEqual(UnlockSoundTier.UltraRare, viewModel.SoundTier);
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

            // The tier order puts hidden first, the rank order keeps capstone on top: a hidden
            // capstone plays the hidden sound while still ranking as a capstone in its wave.
            Assert.AreEqual(UnlockSoundTier.Hidden, viewModel.SoundTier);
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
        public void CapstoneUnlock_GlowsWithCompletionColorsNotRarity()
        {
            var settings = new PersistedSettings
            {
                NotificationStyle = new NotificationStyleSettings
                {
                    Toast = new NotificationSurfaceStyle { ShowRarityGlow = true, NotificationBorderGlow = true },
                    Frame = new NotificationSurfaceStyle { ShowRarityGlow = true }
                }
            };
            var capstone = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "UltraRare",
                    GlobalPercent = 1.2,
                    IsCapstone = true
                },
                settings);
            var regular = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    RarityTier = "UltraRare",
                    GlobalPercent = 1.2
                },
                settings);

            var completedColor = ((DropShadowEffect)RarityAppearanceHelper
                .GetCompletedGlow(useEndColor: true, settings)).Color;

            // The capstone's halo, frame halo, and card border glow all take the completion
            // color, matching its completion-colored accent; a regular unlock keeps its tier's.
            Assert.IsTrue(capstone.UsesCompletionColors);
            Assert.AreEqual(completedColor, ((DropShadowEffect)capstone.RarityGlowEffect).Color);
            Assert.AreEqual(completedColor, ((DropShadowEffect)capstone.FrameRarityGlowEffect).Color);
            Assert.AreEqual(completedColor, ((DropShadowEffect)capstone.BorderGlowEffect).Color);
            Assert.IsFalse(regular.UsesCompletionColors);
            Assert.AreNotEqual(completedColor, ((DropShadowEffect)regular.RarityGlowEffect).Color);
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
        public void ToastBackgroundRenderSource_DefaultsToTheCompatiblePathBinding()
        {
            var style = NotificationStyleSettings.CreateDefault();
            style.ToastBackgroundImagePath = "background.gif";
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs(),
                new PersistedSettings(),
                styleOverride: style);

            Assert.AreEqual("background.gif", viewModel.ToastBackgroundRenderSource);
            Assert.AreEqual("background.gif", viewModel.ToastBackgroundImagePath);
        }

        [TestMethod]
        public void ToastBackgroundRenderSource_PreviewCanInjectAStableImageSourceOrPendingNull()
        {
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var source = BitmapSource.Create(
                    1,
                    1,
                    96,
                    96,
                    System.Windows.Media.PixelFormats.Bgra32,
                    null,
                    new byte[] { 1, 2, 3, 255 },
                    4);

                var ready = new AchievementToastViewModel(
                    new AchievementUnlockedEventArgs(),
                    new PersistedSettings(),
                    toastBackgroundRenderSourceOverride: source,
                    useToastBackgroundRenderSourceOverride: true);
                var pending = new AchievementToastViewModel(
                    new AchievementUnlockedEventArgs(),
                    new PersistedSettings(),
                    toastBackgroundRenderSourceOverride: null,
                    useToastBackgroundRenderSourceOverride: true);

                Assert.AreSame(source, ready.ToastBackgroundRenderSource);
                Assert.IsNull(pending.ToastBackgroundRenderSource);

                var changed = 0;
                pending.PropertyChanged += (sender, args) =>
                {
                    if (args.PropertyName == nameof(AchievementToastViewModel.ToastBackgroundRenderSource))
                    {
                        changed++;
                    }
                };
                pending.SetToastBackgroundRenderSourceOverride(source);

                Assert.AreSame(source, pending.ToastBackgroundRenderSource);
                Assert.AreEqual(1, changed);
            });
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

        private static AchievementToastViewModel BuildLineToast(
            NotificationSurfaceStyle toast,
            AchievementUnlockedEventArgs args = null)
        {
            return new AchievementToastViewModel(
                args ?? new AchievementUnlockedEventArgs
                {
                    DisplayName = "Deep Diver",
                    Description = "Reach the deepest point of the map.",
                    GameName = "Some Game"
                },
                new PersistedSettings
                {
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = toast,
                        Frame = new NotificationSurfaceStyle()
                    }
                });
        }

        private static NotificationSurfaceStyle AllLinesVisible()
        {
            return new NotificationSurfaceStyle
            {
                ShowHeader = true,
                ShowName = true,
                ShowDescription = true,
                ShowGameName = true
            };
        }

        private static AchievementUnlockedEventArgs ProgressArgs(int? previous = 3, int current = 4, int denominator = 10)
        {
            return new AchievementUnlockedEventArgs
            {
                DisplayName = "Headhunter",
                Description = "Kill 10 enemies with headshots.",
                GameName = "Some Game",
                RarityTier = "Rare",
                GlobalPercent = 9.3,
                IsProgressUpdate = true,
                PreviousProgressNum = previous,
                ProgressNum = current,
                ProgressDenom = denominator
            };
        }

        private static ToastProgressLine FindProgressLine(System.Collections.Generic.IReadOnlyList<ToastLineDescriptor> lines)
        {
            foreach (var line in lines)
            {
                if (line is ToastProgressLine progress)
                {
                    return progress;
                }
            }

            return null;
        }

        [TestMethod]
        public void ProgressNotification_ExposesProgressValuesAndHidesRarityVisuals()
        {
            var style = new NotificationSurfaceStyle
            {
                ShowRarityBadge = true,
                ShowRarityPercent = true,
                ShowRarityGlow = true,
                NotificationBorderGlow = true
            };
            var viewModel = BuildLineToast(style, ProgressArgs());

            Assert.IsTrue(viewModel.IsProgressUpdate);
            Assert.IsTrue(viewModel.HasProgress);
            Assert.AreEqual(4, viewModel.ProgressNum);
            Assert.AreEqual(10, viewModel.ProgressDenom);
            Assert.AreEqual(3, viewModel.PreviousProgressNum);
            Assert.AreEqual(0.4, viewModel.ProgressFraction, 1e-9);
            Assert.AreEqual(0.3, viewModel.PreviousProgressFraction, 1e-9);
            Assert.AreEqual("4/10", viewModel.ProgressText);

            // Rarity is beside the point for a still-locked achievement.
            Assert.IsFalse(viewModel.ShowBadge);
            Assert.IsFalse(viewModel.ShowInlineBadge);
            Assert.IsFalse(viewModel.ShowRightBadge);
            Assert.IsFalse(viewModel.ShowPercent);
            Assert.IsFalse(viewModel.ShowRightPercent);
            Assert.IsFalse(viewModel.HasIconFooter);
            Assert.IsNull(viewModel.RarityGlowEffect);
            Assert.IsFalse(viewModel.HasBorderGlow);
            Assert.IsFalse(viewModel.ShowRayBurst);
            Assert.IsFalse(viewModel.ShowCardRayBurst);

            // Silent kind: no sound tier at all.
            Assert.IsNull(viewModel.SoundTier);
            Assert.AreEqual(0, viewModel.SoundTierRank);
        }

        [TestMethod]
        public void ProgressNotification_ProgressLineIsVisibleOnTheToastAndAbsentFromTheFrame()
        {
            var viewModel = BuildLineToast(AllLinesVisible(), ProgressArgs());

            var toastLine = FindProgressLine(viewModel.ToastLines);
            Assert.IsNotNull(toastLine, "The toast line list carries the progress row.");
            Assert.AreEqual(Visibility.Visible, toastLine.LineVisibility);
            Assert.IsTrue(toastLine.ShowProgress);
            Assert.AreEqual("4/10", toastLine.ProgressText);
            Assert.AreEqual(0.4, toastLine.ProgressFraction, 1e-9);
            Assert.IsNotNull(toastLine.BarBrush);
            Assert.IsNotNull(toastLine.TrackBrush);
            Assert.IsTrue(toastLine.BarHeight >= 4);
            Assert.AreEqual(toastLine.BarHeight / 2, toastLine.BarCornerRadius.TopLeft);
            Assert.IsTrue(toastLine.LineBoxHeight >= toastLine.FontSize, "The bar row is at least one text line tall.");

            Assert.IsNull(FindProgressLine(viewModel.FrameLines), "Frames never render progress notifications.");
        }

        [TestMethod]
        public void NamedLines_AreTheListEntriesWithRowIndexFollowingTheStoredOrder()
        {
            var toast = AllLinesVisible();
            toast.LineOrder = new System.Collections.Generic.List<string>
            {
                NotificationSurfaceStyle.LineGameCategory,
                NotificationSurfaceStyle.LineHeader,
                NotificationSurfaceStyle.LineTitle,
                NotificationSurfaceStyle.LineDescription
            };
            var viewModel = BuildLineToast(toast);

            Assert.AreEqual(0, viewModel.GameCategoryLine.RowIndex);
            Assert.AreEqual(1, viewModel.HeaderLine.RowIndex);
            Assert.AreEqual(2, viewModel.TitleLine.RowIndex);
            Assert.AreEqual(3, viewModel.DescriptionLine.RowIndex);
            Assert.AreEqual(4, viewModel.ProgressLine.RowIndex, "The progress line is appended to a stored four-line order.");

            // The named properties are the very descriptors in the list, so a template mixing the
            // two patterns sees one set of resolved values.
            for (var i = 0; i < viewModel.ToastLines.Count; i++)
            {
                Assert.AreEqual(i, viewModel.ToastLines[i].RowIndex);
            }

            Assert.AreSame(viewModel.ToastLines[1], viewModel.HeaderLine);
            Assert.AreSame(viewModel.ToastLines[2], viewModel.TitleLine);
        }

        [TestMethod]
        public void FrameNamedLines_HaveCompactRowIndicesAndNoProgressLine()
        {
            var settings = new PersistedSettings();
            settings.NotificationStyle.Frame.LineOrder = new System.Collections.Generic.List<string>
            {
                NotificationSurfaceStyle.LineTitle,
                NotificationSurfaceStyle.LineProgress,
                NotificationSurfaceStyle.LineHeader,
                NotificationSurfaceStyle.LineDescription,
                NotificationSurfaceStyle.LineGameCategory
            };
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs { DisplayName = "Deep Diver", Description = "Dive.", GameName = "Some Game" },
                settings);

            Assert.AreEqual(4, viewModel.FrameLines.Count, "The frame skips the progress token.");
            Assert.AreEqual(0, viewModel.FrameTitleLine.RowIndex);
            Assert.AreEqual(1, viewModel.FrameHeaderLine.RowIndex, "Row indices stay compact across the skipped token.");
            Assert.AreEqual(2, viewModel.FrameDescriptionLine.RowIndex);
            Assert.AreEqual(3, viewModel.FrameGameCategoryLine.RowIndex);
            Assert.AreSame(viewModel.FrameLines[0], viewModel.FrameTitleLine);
        }

        [TestMethod]
        public void ProgressNotification_IgnoresTheNameLineOffsetLikeCompletion()
        {
            var style = AllLinesVisible();
            style.TitleLineOffset = 24;

            var unlock = BuildLineToast(style);
            var progress = BuildLineToast(style, ProgressArgs());

            ToastLineDescriptor UnlockTitle() { foreach (var l in unlock.ToastLines) if (l is ToastTitleLine) return l; return null; }
            ToastLineDescriptor ProgressTitle() { foreach (var l in progress.ToastLines) if (l is ToastTitleLine) return l; return null; }

            Assert.AreEqual(24, UnlockTitle().LeftIndent, "The unlock toast keeps the user's name-line offset.");
            Assert.AreEqual(0, ProgressTitle().LeftIndent, "No inline badge on a progress toast, so nothing to make room for.");
            foreach (var line in progress.ToastLines)
            {
                Assert.AreEqual(0, line.LeftIndent);
            }
        }

        private static AchievementUnlockedEventArgs RareUnlockArgs()
        {
            return new AchievementUnlockedEventArgs
            {
                DisplayName = "Deep Diver",
                Description = "Reach the deepest point of the map.",
                GameName = "Some Game",
                RarityTier = "Rare",
                GlobalPercent = 9.3
            };
        }

        [TestMethod]
        public void InlineGameBadge_DrawsOnTheGameLineAndTheOffsetFollowsIt()
        {
            var style = AllLinesVisible();
            style.InlineGameRarityBadge = true;
            style.TitleLineOffset = 24;

            var viewModel = BuildLineToast(style, RareUnlockArgs());

            Assert.IsTrue(viewModel.GameCategoryLine.ShowInlineBadge);
            Assert.IsFalse(viewModel.TitleLine.ShowInlineBadge);
            Assert.AreEqual(24, viewModel.GameCategoryLine.LeftIndent, "A positive offset indents the badge line.");
            foreach (var line in viewModel.ToastLines)
            {
                if (!(line is ToastGameCategoryLine))
                {
                    Assert.AreEqual(0, line.LeftIndent);
                }
            }
        }

        [TestMethod]
        public void InlineGameBadge_NegativeOffsetIndentsEveryOtherLine()
        {
            var style = AllLinesVisible();
            style.InlineGameRarityBadge = true;
            style.TitleLineOffset = -24;

            var viewModel = BuildLineToast(style, RareUnlockArgs());

            Assert.AreEqual(0, viewModel.GameCategoryLine.LeftIndent);
            foreach (var line in viewModel.ToastLines)
            {
                if (!(line is ToastGameCategoryLine))
                {
                    Assert.AreEqual(24, line.LeftIndent);
                }
            }
        }

        [TestMethod]
        public void InlineGameBadge_CollapsesWithAnEmptyGameLine()
        {
            var style = AllLinesVisible();
            style.InlineGameRarityBadge = true;
            style.ShowGameName = false;

            var viewModel = BuildLineToast(style, RareUnlockArgs());

            Assert.AreEqual(Visibility.Collapsed, viewModel.GameCategoryLine.LineVisibility);
        }

        [TestMethod]
        public void UnlockNotification_ProgressLineCollapsesAndProgressValuesAreEmpty()
        {
            var viewModel = BuildLineToast(AllLinesVisible());

            Assert.IsFalse(viewModel.IsProgressUpdate);
            Assert.IsFalse(viewModel.HasProgress);
            Assert.AreEqual(string.Empty, viewModel.ProgressText);
            Assert.AreEqual(0, viewModel.ProgressFraction);

            var line = FindProgressLine(viewModel.ToastLines);
            Assert.IsNotNull(line, "The row is always in the list so the user's line order is stable.");
            Assert.AreEqual(Visibility.Collapsed, line.LineVisibility);
        }

        [TestMethod]
        public void ProgressNotification_FractionsClampAndUnknownPreviousReadsAsZero()
        {
            var overshoot = BuildLineToast(AllLinesVisible(), ProgressArgs(previous: null, current: 12, denominator: 10));

            Assert.AreEqual(1.0, overshoot.ProgressFraction);
            Assert.AreEqual(0.0, overshoot.PreviousProgressFraction);
            Assert.IsNull(overshoot.PreviousProgressNum);
        }

        [TestMethod]
        public void ProgressNotification_HeaderHonorsTheProgressHeaderEdit()
        {
            var style = AllLinesVisible();
            style.HeaderTexts.ProgressHeader = "Getting there";
            style.HeaderTexts.UnlockHeader = "Unlocked!";

            var viewModel = BuildLineToast(style, ProgressArgs());

            Assert.AreEqual("Getting there", viewModel.HeaderText);
            Assert.AreEqual("Getting there", viewModel.ProgressHeaderText);
            foreach (var line in viewModel.ToastLines)
            {
                if (line is ToastHeaderLine header)
                {
                    Assert.AreEqual("Getting there", header.HeaderText);
                }
            }
        }

        [TestMethod]
        public void CompletionNotification_TitleHonorsTheCompletionTitleEdit()
        {
            var style = AllLinesVisible();
            style.HeaderTexts.CompletionTitle = "{0} mastered";

            var viewModel = BuildLineToast(style, new AchievementUnlockedEventArgs
            {
                GameName = "Some Game",
                IsGameCompleted = true
            });

            Assert.AreEqual("Some Game mastered", viewModel.CompletionTitleText);
            foreach (var line in viewModel.ToastLines)
            {
                if (line is ToastTitleLine title)
                {
                    Assert.AreEqual("Some Game mastered", title.CompletionTitleText);
                }
            }
        }

        [TestMethod]
        public void GameCategoryLine_CategoryMatchingTheGameShowsTheNameOnce()
        {
            Assert.AreEqual("Some Game", GameCategoryTextFor("Some Game", "some game "));
        }

        [TestMethod]
        public void GameCategoryLine_DistinctCategoryShowsBoth()
        {
            StringAssert.Contains(GameCategoryTextFor("Some Game", "Expansion"), "Expansion");
            StringAssert.StartsWith(GameCategoryTextFor("Some Game", "Expansion"), "Some Game");
        }

        private static string GameCategoryTextFor(string gameName, string category)
        {
            var viewModel = BuildLineToast(AllLinesVisible(), new AchievementUnlockedEventArgs
            {
                DisplayName = "Deep Diver",
                GameName = gameName,
                Category = category
            });
            foreach (var line in viewModel.ToastLines)
            {
                if (line is ToastGameCategoryLine row)
                {
                    return row.GameCategoryText;
                }
            }

            Assert.Fail("No game/category line.");
            return null;
        }

        [TestMethod]
        public void CompletionTitle_StrayBraceShowsAsTyped()
        {
            Assert.AreEqual("Done {", NotificationHeaderTextService.FormatCompletionTitle("Done {", "Some Game"));
        }

        [TestMethod]
        public void CompletionTitle_TextWithoutPlaceholderShowsAsTyped()
        {
            Assert.AreEqual("All done", NotificationHeaderTextService.FormatCompletionTitle("All done", "Some Game"));
        }

        [TestMethod]
        public void DescenderSlack_LandsOnlyOnTheBottomVisibleLine()
        {
            var viewModel = BuildLineToast(AllLinesVisible());
            var lines = viewModel.ToastLines;

            var visible = new System.Collections.Generic.List<ToastLineDescriptor>();
            foreach (var line in lines)
            {
                if (line.LineVisibility == Visibility.Visible)
                {
                    visible.Add(line);
                }
            }

            Assert.IsTrue(visible.Count > 1, "Test needs more than one visible line to be meaningful.");

            for (var i = 0; i < visible.Count; i++)
            {
                var isLast = i == visible.Count - 1;
                Assert.AreEqual(isLast, visible[i].IsBottomLine,
                    $"Line {i} ({visible[i].GetType().Name}) bottom-line flag.");
                if (isLast)
                {
                    Assert.IsTrue(visible[i].DescenderSlack > 0, "Bottom line must reserve slack.");
                }
                else
                {
                    Assert.AreEqual(0, visible[i].DescenderSlack, "Only the bottom line reserves slack.");
                }
            }
        }

        [TestMethod]
        public void DescenderSlack_FollowsTheUserLineOrder()
        {
            // The game/category line normally sits last; moving it up hands the slack to whatever
            // the user put at the bottom instead.
            var toast = AllLinesVisible();
            toast.LineOrder = new System.Collections.Generic.List<string>
            {
                NotificationSurfaceStyle.LineGameCategory,
                NotificationSurfaceStyle.LineHeader,
                NotificationSurfaceStyle.LineTitle,
                NotificationSurfaceStyle.LineDescription
            };

            var lines = BuildLineToast(toast).ToastLines;

            // The progress line is appended to a stored four-line order and collapses on an unlock
            // toast, so the bottom line is the last line that actually renders, not the last entry.
            Assert.IsInstanceOfType(lines[lines.Count - 1], typeof(ToastProgressLine));
            Assert.AreEqual(Visibility.Collapsed, lines[lines.Count - 1].LineVisibility);
            Assert.IsFalse(lines[lines.Count - 1].IsBottomLine);
            Assert.IsInstanceOfType(lines[lines.Count - 2], typeof(ToastDescriptionLine));
            Assert.IsTrue(lines[lines.Count - 2].IsBottomLine);
            Assert.IsFalse(lines[0].IsBottomLine);
        }

        [TestMethod]
        public void DescenderSlack_SkipsCollapsedLines()
        {
            // An achievement with no description collapses that row, so the slack has to fall to
            // the last row that actually renders rather than the last row in the order.
            var toast = AllLinesVisible();
            toast.ShowGameName = false;
            toast.ShowCategory = false;

            var lines = BuildLineToast(
                toast,
                new AchievementUnlockedEventArgs { DisplayName = "Deep Diver", Description = null }).ToastLines;

            foreach (var line in lines)
            {
                if (line.LineVisibility != Visibility.Visible)
                {
                    Assert.IsFalse(line.IsBottomLine, "A collapsed line must never take the slack.");
                }
            }

            var bottom = default(ToastLineDescriptor);
            foreach (var line in lines)
            {
                if (line.IsBottomLine)
                {
                    bottom = line;
                }
            }

            Assert.IsNotNull(bottom, "Some visible line must carry the slack.");
            Assert.AreEqual(Visibility.Visible, bottom.LineVisibility);
        }

        [TestMethod]
        public void DescriptionClamp_AddsSlackOnlyWhenTheDescriptionIsBottomMost()
        {
            // Game/category present: it sits below the description, so the description keeps the
            // exact clamp it always had.
            var withGameRow = BuildLineToast(AllLinesVisible());
            var description = FindDescription(withGameRow.ToastLines);
            Assert.IsFalse(description.IsBottomLine);
            Assert.AreEqual(
                (description.LineBoxHeight * description.MaxLines) + 0.5,
                description.MaxTextHeight,
                1e-9);

            // No game/category row: the description is bottom-most and raises its own ceiling, or
            // an over-long description would be layout-clipped before its descenders.
            var toast = AllLinesVisible();
            toast.ShowGameName = false;
            toast.ShowCategory = false;
            var bottomDescription = FindDescription(BuildLineToast(toast).ToastLines);

            Assert.IsTrue(bottomDescription.IsBottomLine);
            Assert.AreEqual(
                (bottomDescription.LineBoxHeight * bottomDescription.MaxLines)
                    + bottomDescription.DescenderSlack + 0.5,
                bottomDescription.MaxTextHeight,
                1e-9);
            Assert.IsTrue(bottomDescription.DescenderSlack > 0);
        }

        private static ToastDescriptionLine FindDescription(
            System.Collections.Generic.IReadOnlyList<ToastLineDescriptor> lines)
        {
            foreach (var line in lines)
            {
                if (line is ToastDescriptionLine description)
                {
                    return description;
                }
            }

            Assert.Fail("No description line was built.");
            return null;
        }

        [TestMethod]
        public void RarityText_FollowsTheSurfaceFamilyUntilOverridden()
        {
            var inherited = BuildLineToast(new NotificationSurfaceStyle { FontFamily = "Consolas" });
            Assert.AreEqual("Consolas", inherited.ToastRarityText.FontFamily.Source);

            var overridden = BuildLineToast(new NotificationSurfaceStyle
            {
                FontFamily = "Consolas",
                RarityFontFamily = "Georgia"
            });
            Assert.AreEqual("Georgia", overridden.ToastRarityText.FontFamily.Source);
        }

        [TestMethod]
        public void RarityText_AppliesEmphasisWithoutTheTitleLineBoldRamp()
        {
            var plain = BuildLineToast(new NotificationSurfaceStyle());
            Assert.AreEqual(FontWeights.Normal, plain.ToastRarityText.FontWeight);
            Assert.AreEqual(FontStyles.Normal, plain.ToastRarityText.FontStyle);
            Assert.IsNull(plain.ToastRarityText.TextDecorations);

            var emphasized = BuildLineToast(new NotificationSurfaceStyle
            {
                RarityEmphasis = NotificationLineEmphasis.Bold
                    | NotificationLineEmphasis.Italic
                    | NotificationLineEmphasis.Underline
            });

            // Bold stops at Bold here; only the title line ramps to Black to clear its SemiBold base.
            Assert.AreEqual(FontWeights.Bold, emphasized.ToastRarityText.FontWeight);
            Assert.AreEqual(FontStyles.Italic, emphasized.ToastRarityText.FontStyle);
            Assert.IsNotNull(emphasized.ToastRarityText.TextDecorations);
        }

        [TestMethod]
        public void RarityText_IsNotPartOfTheLineOrder()
        {
            // It must never take the bottom-line slack or appear among the reorderable lines.
            var viewModel = BuildLineToast(AllLinesVisible());

            foreach (var line in viewModel.ToastLines)
            {
                Assert.IsNotInstanceOfType(line, typeof(ToastRarityTextLine));
            }

            Assert.IsFalse(viewModel.ToastRarityText.IsBottomLine);
            Assert.AreEqual(0, viewModel.ToastRarityText.DescenderSlack);
        }

        [TestMethod]
        public void ScaleVignetteStopAlpha_DefaultAndFiftyReturnTheOriginalAlpha()
        {
            var baseAlphas = new[] { 0x73 / 255.0, 0xF2 / 255.0, 0xD0 / 255.0 };
            foreach (var alpha in baseAlphas)
            {
                Assert.AreEqual(alpha, AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, null));
                Assert.AreEqual(alpha, AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, 50));
            }
        }

        [TestMethod]
        public void ScaleVignetteStopAlpha_LowerHalfFadesLinearlyToNothing()
        {
            const double baseAlpha = 0xD0 / 255.0;
            Assert.AreEqual(0, AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, 0));
            Assert.AreEqual(baseAlpha / 2, AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, 25), 1e-12);
        }

        [TestMethod]
        public void ScaleVignetteStopAlpha_UpperHalfScreenStacksWithoutClipping()
        {
            // 100 = the original layer composited over itself twice more: 1 - (1 - a)^3.
            var baseAlphas = new[] { 0x73 / 255.0, 0xF2 / 255.0, 0xD0 / 255.0 };
            foreach (var alpha in baseAlphas)
            {
                var stacked = 1.0 - Math.Pow(1.0 - alpha, 3.0);
                Assert.AreEqual(stacked, AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, 100), 1e-12);
                Assert.IsTrue(AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, 100) < 1.0);
            }
        }

        [TestMethod]
        public void FrameVignetteImage_ReplacesBothVignetteLayers_AndShowsOnlyAnExistingImage()
        {
            var settings = new PersistedSettings();
            settings.NotificationStyle.Frame.FrameVignette = FrameVignetteStyle.Image;
            settings.NotificationStyle.FrameBackgroundImagePath =
                Path.Combine(Path.GetTempPath(), "PlayAchMissing_" + Guid.NewGuid().ToString("N") + ".png");

            var missing = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);
            Assert.IsFalse(missing.FrameShowRadialVignette);
            Assert.IsFalse(missing.FrameShowBottomWash);
            Assert.IsFalse(missing.FrameShowBackgroundImage);
            Assert.IsNull(missing.FrameBackgroundImage);

            var path = Path.Combine(Path.GetTempPath(), "PlayAchFrameBg_" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(path, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIW2NkYGD4DwABBAEAgh8sXQAAAABJRU5ErkJggg=="));
            try
            {
                settings.NotificationStyle.FrameBackgroundImagePath = path;
                var present = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);
                Assert.IsTrue(present.FrameShowBackgroundImage);
                Assert.IsNotNull(present.FrameBackgroundImage);

                settings.NotificationStyle.Frame.FrameVignette = FrameVignetteStyle.Full;
                var full = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);
                Assert.IsFalse(full.FrameShowBackgroundImage, "a kept path does not draw outside the Image choice");
                Assert.IsTrue(full.FrameShowRadialVignette);
                Assert.IsTrue(full.FrameShowBottomWash);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void ImageKeyframe_FrameDrawsThatMoment_AndTheToastCapturePinsOnlyItsOwnSlots()
        {
            var gif = GifFixture.WriteTempGif(GifFixture.BuildGif(
                1, 1, new[] { 0xFF0000, 0x0000FF },
                new GifFixture.Frame { Width = 1, Height = 1, Indices = new byte[] { 0 }, DelayCentiseconds = 10 },
                new GifFixture.Frame { Width = 1, Height = 1, Indices = new byte[] { 1 }, DelayCentiseconds = 10 }));
            try
            {
                var settings = new PersistedSettings();
                var style = settings.NotificationStyle;
                style.Frame.FrameVignette = FrameVignetteStyle.Image;
                style.FrameBackgroundImagePath = gif;
                style.ToastBackgroundImagePath = gif;

                var firstFrame = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);
                Assert.AreEqual(0xFF0000, PixelOf(firstFrame.FrameBackgroundImage));
                Assert.IsNull(firstFrame.ToastCaptureStill(gif), "no keyframe leaves the capture live");

                style.Frame.ImageKeyframeMs = 150;
                style.Toast.ImageKeyframeMs = 150;
                var keyed = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);
                Assert.AreEqual(0x0000FF, PixelOf(keyed.FrameBackgroundImage));
                Assert.AreEqual(0x0000FF, PixelOf(keyed.ToastCaptureStill(gif)));
                Assert.IsNull(keyed.ToastCaptureStill(Path.Combine(Path.GetTempPath(), "other.gif")),
                    "an image outside the style's slots is left as it is");
            }
            finally
            {
                GifFixture.DeleteTempPayload(gif);
            }
        }

        [TestMethod]
        public void FrameAnimationTime_PlaysFromTheFirstFrameAndLoops_IgnoringTheKeyframe()
        {
            var gif = GifFixture.WriteTempGif(GifFixture.BuildGif(
                1, 1, new[] { 0xFF0000, 0x0000FF },
                new GifFixture.Frame { Width = 1, Height = 1, Indices = new byte[] { 0 }, DelayCentiseconds = 10 },
                new GifFixture.Frame { Width = 1, Height = 1, Indices = new byte[] { 1 }, DelayCentiseconds = 10 }));
            try
            {
                var settings = new PersistedSettings();
                settings.NotificationStyle.Frame.FrameVignette = FrameVignetteStyle.Image;
                settings.NotificationStyle.Frame.ImageKeyframeMs = 150;
                settings.NotificationStyle.FrameBackgroundImagePath = gif;
                var vm = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), settings);

                Assert.AreEqual(1, vm.FrameAnimationTimelines().Count);
                Assert.AreEqual(0x0000FF, PixelOf(vm.FrameBackgroundImage), "the screenshot uses the keyframe");

                vm.FrameAnimationTimeMs = 0;
                Assert.AreEqual(0xFF0000, PixelOf(vm.FrameBackgroundImage), "the video starts at the first frame");
                vm.FrameAnimationTimeMs = 150;
                Assert.AreEqual(0x0000FF, PixelOf(vm.FrameBackgroundImage));
                vm.FrameAnimationTimeMs = 250;
                Assert.AreEqual(0xFF0000, PixelOf(vm.FrameBackgroundImage), "the loop starts over");
            }
            finally
            {
                GifFixture.DeleteTempPayload(gif);
            }
        }

        private static int PixelOf(ImageSource image)
        {
            Assert.IsInstanceOfType(image, typeof(BitmapSource));
            var pixels = new int[1];
            new FormatConvertedBitmap((BitmapSource)image, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 4, 0);
            return pixels[0] & 0xFFFFFF;
        }

        [TestMethod]
        public void FrameVignetteBrushes_UpperHalfGrowsCoverageBeyondAlphaStacking()
        {
            var strongSettings = new PersistedSettings();
            strongSettings.NotificationStyle.Frame.FrameVignetteStrength = 100;
            var strong = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), strongSettings);
            var builtIn = new AchievementToastViewModel(new AchievementUnlockedEventArgs(), new PersistedSettings());

            // The radial's clear center shrinks and the mid darkening moves inward.
            var strongRadial = (RadialGradientBrush)strong.FrameRadialVignetteBrush;
            var builtInRadial = (RadialGradientBrush)builtIn.FrameRadialVignetteBrush;
            Assert.IsTrue(strongRadial.GradientStops[1].Offset < builtInRadial.GradientStops[1].Offset);
            Assert.IsTrue(strongRadial.GradientStops[2].Offset < builtInRadial.GradientStops[2].Offset);

            // The wash's mid stop bows above the linear ramp, pulling darkness up the band.
            var strongWash = (LinearGradientBrush)strong.FrameBottomWashBrush;
            var strongMid = strongWash.GradientStops[2];
            var strongEnd = strongWash.GradientStops[strongWash.GradientStops.Count - 1];
            Assert.AreEqual(0.5, strongMid.Offset);
            Assert.IsTrue(strongMid.Color.A > strongEnd.Color.A * 0.5);
        }

        [TestMethod]
        public void FrameBottomWashBrush_BuiltInStrengthKeepsTheOriginalLinearRamp()
        {
            // At the default strength the intermediate stops must sit exactly on the original
            // two-stop linear ramp (0 -> 0xD0), changing nothing about the classic look.
            var wash = (LinearGradientBrush)new AchievementToastViewModel(
                new AchievementUnlockedEventArgs(), new PersistedSettings()).FrameBottomWashBrush;

            var end = wash.GradientStops[wash.GradientStops.Count - 1];
            Assert.AreEqual(0xD0, end.Color.A);
            foreach (var stop in wash.GradientStops)
            {
                Assert.AreEqual((byte)Math.Round(0xD0 * stop.Offset), stop.Color.A);
            }
        }

        [TestMethod]
        public void ProgressNotification_PublishesAPlainIconPath()
        {
            WithTempIcon(iconFile =>
            {
                var args = ProgressArgs();
                args.IconPath = iconFile;

                var viewModel = new AchievementToastViewModel(args, new PersistedSettings());

                // The display source carries the decoration the image cache understands; the
                // published path is what a template can hand straight to Image.Source.
                StringAssert.Contains(viewModel.IconDisplaySource, "gray:");
                Assert.AreEqual(iconFile, viewModel.IconPath);
            });
        }

        [TestMethod]
        public void ProgressNotification_ClampsTheDescriptionToOneLineForTheBar()
        {
            var style = new NotificationSurfaceStyle
            {
                ShowHeader = true,
                ShowName = true,
                ShowDescription = true,
                ShowGameName = false,
                ShowCategory = false
            };

            var unlock = BuildLineToast(style);
            var progress = new AchievementToastViewModel(
                ProgressArgs(),
                new PersistedSettings
                {
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = style,
                        // The frame carries the same visible lines, so the only thing that can
                        // explain a different budget below is the progress bar.
                        Frame = new NotificationSurfaceStyle
                        {
                            ShowHeader = true,
                            ShowName = true,
                            ShowDescription = true,
                            ShowGameName = false,
                            ShowCategory = false
                        }
                    }
                });

            Assert.AreEqual(2, unlock.DescriptionLine.MaxLines, "An unlock toast with no game line keeps both lines.");
            Assert.AreEqual(1, progress.DescriptionLine.MaxLines, "The progress bar takes the second line.");
            Assert.AreEqual(
                2,
                progress.FrameDescriptionLine.MaxLines,
                "The frame draws no progress bar, so its description keeps both lines.");
        }

        [TestMethod]
        public void ProgressNotification_MasksHiddenAchievementsPerTheVisibilitySettings()
        {
            WithTempIcon(iconFile =>
            {
                var args = ProgressArgs();
                args.IsHidden = true;
                args.IconPath = iconFile;

                var masked = new AchievementToastViewModel(
                    args,
                    new PersistedSettings
                    {
                        ShowHiddenTitle = false,
                        ShowHiddenDescription = false,
                        ShowHiddenIcon = false
                    });

                Assert.AreEqual("Hidden Achievement", masked.TitleText);
                Assert.AreEqual(
                    " ",
                    masked.Description,
                    "A masked description prints nothing readable, not a reveal prompt.");
                Assert.AreEqual(
                    Visibility.Visible,
                    masked.DescriptionLine.LineVisibility,
                    "The blank line is kept so a hidden card is spaced like every other one.");
                Assert.AreEqual(
                    AchievementIconResolver.GetHiddenFallbackIcon(),
                    masked.IconDisplaySource,
                    "A hidden achievement's own art never reaches a notification.");
                Assert.AreEqual(
                    AchievementIconResolver.GetHiddenFallbackIcon(),
                    masked.IconPath);
            });
        }

        [TestMethod]
        public void ProgressNotification_ShowsRealDetailsWhenTheSettingsAllowIt()
        {
            var args = ProgressArgs();
            args.IsHidden = true;

            var revealed = new AchievementToastViewModel(
                args,
                new PersistedSettings
                {
                    ShowHiddenTitle = true,
                    ShowHiddenDescription = true,
                    ShowHiddenIcon = true,
                    ShowLockedIcon = true,
                    ShowHiddenSuffix = false
                });

            Assert.AreEqual("Headhunter", revealed.TitleText);
            Assert.AreEqual("Kill 10 enemies with headshots.", revealed.Description);
        }

        [TestMethod]
        public void ProgressNotification_CoversTheIconWhenLockedIconsAreHidden()
        {
            WithTempIcon(iconFile =>
            {
                var args = ProgressArgs();
                args.IconPath = iconFile;

                var covered = new AchievementToastViewModel(
                    args, new PersistedSettings { ShowLockedIcon = false });
                var shown = new AchievementToastViewModel(
                    args, new PersistedSettings { ShowLockedIcon = true });

                Assert.AreEqual(
                    AchievementIconResolver.GetLockedFallbackIcon(),
                    covered.IconDisplaySource);
                StringAssert.Contains(
                    shown.IconDisplaySource,
                    iconFile,
                    "With locked icons shown, the real art comes through (grayscaled).");
            });
        }

        // A rooted file the icon resolver will accept: it cache-busts from the file's own write
        // time and length, so the source has to exist on disk for the decorated form to appear.
        private static void WithTempIcon(Action<string> test)
        {
            var iconFile = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(iconFile, new byte[] { 0x89, 0x50, 0x4E, 0x47 });
            try
            {
                test(iconFile);
            }
            finally
            {
                File.Delete(iconFile);
            }
        }

        [TestMethod]
        public void UnlockNotification_IsNeverMasked()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    DisplayName = "Deep Diver",
                    Description = "Reach the deepest point of the map.",
                    IsHidden = true
                },
                new PersistedSettings
                {
                    ShowHiddenTitle = false,
                    ShowHiddenDescription = false,
                    ShowHiddenIcon = false,
                    ShowLockedIcon = false
                });

            Assert.AreEqual("Deep Diver", viewModel.TitleText, "Unlocking reveals the achievement.");
            Assert.AreEqual("Reach the deepest point of the map.", viewModel.Description);
        }

        [TestMethod]
        public void FriendAvatar_IsTheCachedPathAndNeverARemoteUrl()
        {
            var viewModel = new AchievementToastViewModel(
                new AchievementUnlockedEventArgs
                {
                    IsFriendUnlock = true,
                    FriendAvatarUrl = "https://example.invalid/avatar.png"
                },
                new PersistedSettings());

            Assert.IsNull(viewModel.FriendAvatar, "Notifications never fetch over the network.");
        }

        [DataTestMethod]
        [DataRow("Default")]
        [DataRow("default")]
        public void DefaultCategory_IsHiddenOnToastAndFrame(string category)
        {
            var viewModel = CategoryToast(category);

            Assert.IsFalse(viewModel.ShowCategory);
            Assert.IsFalse(viewModel.FrameShowCategory);
            Assert.IsFalse(viewModel.ShowGameCategorySeparator);
        }

        [TestMethod]
        public void NamedCategory_IsShownOnToastAndFrame()
        {
            var viewModel = CategoryToast("Story");

            Assert.IsTrue(viewModel.ShowCategory);
            Assert.IsTrue(viewModel.FrameShowCategory);
        }

        [TestMethod]
        public void NestedCategory_ShowsOnlyItsLeaf()
        {
            var viewModel = CategoryToast("DLC::Season Pass");

            Assert.AreEqual("Season Pass", viewModel.Category);
            Assert.IsTrue(viewModel.ShowCategory);
        }

        private static AchievementToastViewModel CategoryToast(string category)
        {
            return new AchievementToastViewModel(
                new AchievementUnlockedEventArgs { GameName = "Some Game", Category = category },
                new PersistedSettings
                {
                    NotificationStyle = new NotificationStyleSettings
                    {
                        Toast = new NotificationSurfaceStyle { ShowCategory = true, ShowGameName = true },
                        Frame = new NotificationSurfaceStyle { ShowCategory = true, ShowGameName = true }
                    }
                });
        }

        [TestMethod]
        public void PlatinumCapstone_DrawsThePlatinumTrophyBadge()
        {
            LocalizationAssemblyInitializer.RunOnSta(() =>
            {
                var settings = new PersistedSettings();
                var platinum = new AchievementToastViewModel(
                    new AchievementUnlockedEventArgs { IsCapstone = true, TrophyType = "platinum", RarityTier = "Rare" },
                    settings);
                var plain = new AchievementToastViewModel(
                    new AchievementUnlockedEventArgs { IsCapstone = true, RarityTier = "Rare" },
                    settings);

                var trophyPixels = RenderPixels(RarityAppearanceHelper.CreateTrophyPreview("TrophyPlatinum", settings));
                var completedPixels = RenderPixels(RarityAppearanceHelper.CreateCompletedBadgePreview(settings));

                CollectionAssert.AreEqual(trophyPixels, RenderPixels(platinum.BadgeImage));
                CollectionAssert.AreEqual(completedPixels, RenderPixels(plain.BadgeImage));
                CollectionAssert.AreNotEqual(trophyPixels, completedPixels);

                // The swap is the badge only: the platinum capstone keeps the capstone's colors and sound.
                Assert.IsTrue(platinum.UsesCompletionColors);
                Assert.AreEqual(UnlockSoundTier.Capstone, platinum.SoundTier);
            });
        }

        private static byte[] RenderPixels(ImageSource source)
        {
            Assert.IsNotNull(source);
            const int size = 32;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawImage(source, new Rect(0, 0, size, size));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var pixels = new byte[size * size * 4];
            bitmap.CopyPixels(pixels, size * 4, 0);
            return pixels;
        }

        [TestMethod]
        public void ScaleVignetteStopAlpha_OutOfRangeStrengthClamps()
        {
            const double baseAlpha = 0x73 / 255.0;
            Assert.AreEqual(
                AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, 0),
                AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, -20));
            Assert.AreEqual(
                AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, 100),
                AchievementToastViewModel.ScaleVignetteStopAlpha(baseAlpha, 250));
        }
    }
}
