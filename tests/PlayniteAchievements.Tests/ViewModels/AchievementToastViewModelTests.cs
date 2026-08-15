using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Tests.TestInfrastructure;
using PlayniteAchievements.ViewModels;
using System;
using System.IO;
using System.Windows;
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

            Assert.IsInstanceOfType(lines[lines.Count - 1], typeof(ToastDescriptionLine));
            Assert.IsTrue(lines[lines.Count - 1].IsBottomLine);
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
            // 100 = the original layer composited over itself: 1 - (1 - a)^2.
            var baseAlphas = new[] { 0x73 / 255.0, 0xF2 / 255.0, 0xD0 / 255.0 };
            foreach (var alpha in baseAlphas)
            {
                var stacked = 1.0 - Math.Pow(1.0 - alpha, 2.0);
                Assert.AreEqual(stacked, AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, 100), 1e-12);
                Assert.IsTrue(AchievementToastViewModel.ScaleVignetteStopAlpha(alpha, 100) < 1.0);
            }
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
