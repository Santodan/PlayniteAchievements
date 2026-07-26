using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.Tagging;

namespace PlayniteAchievements.Models.Tests
{
    [TestClass]
    public class PersistedSettingsDefaultsTests
    {
        [TestMethod]
        public void Constructor_DefaultsAchievementDataGridMaxHeight()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(
                PersistedSettings.DefaultAchievementDataGridMaxHeight,
                settings.AchievementDataGridMaxHeight);
        }

        [TestMethod]
        public void Constructor_DefaultsProgressColumnToRightAcrossSurfaces()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.ProgressColumnAlignmentDefaulted);
            Assert.AreEqual(
                GridAlignment.Right,
                settings.OverviewGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(
                GridAlignment.Right,
                settings.StartPageGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(
                GridAlignment.Right,
                settings.ViewAchievementsGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(
                GridAlignment.Right,
                settings.FriendsOverviewGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(
                GridAlignment.Right,
                settings.FriendsOverviewSelectedFriendGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
        }

        [TestMethod]
        public void Constructor_DefaultsRetroAchievementsAutoDiscoveryEnabled()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.IsFriendAutoDiscoverEnabled("Steam"));
            Assert.IsTrue(settings.IsFriendAutoDiscoverEnabled("RetroAchievements"));
        }

        [TestMethod]
        public void Constructor_DefaultsExophaseSteamFriendOwnershipReplacementDisabled()
        {
            var settings = new PersistedSettings();

            Assert.IsFalse(settings.UseExophaseForSteamFriendOwnership);
        }

        [TestMethod]
        public void Constructor_DefaultsEnableFriendsFeaturesOn()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.EnableFriendsFeatures);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveEnableFriendsFeatures()
        {
            var source = new PersistedSettings
            {
                EnableFriendsFeatures = false
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.EnableFriendsFeatures);
            Assert.IsFalse(target.EnableFriendsFeatures);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveExophaseSteamFriendOwnershipReplacement()
        {
            var source = new PersistedSettings
            {
                UseExophaseForSteamFriendOwnership = true
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(clone.UseExophaseForSteamFriendOwnership);
            Assert.IsTrue(target.UseExophaseForSteamFriendOwnership);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveUseTrophiesForRarity()
        {
            var source = new PersistedSettings
            {
                UseTrophiesForRarity = true
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(clone.UseTrophiesForRarity);
            Assert.IsTrue(target.UseTrophiesForRarity);
        }

        [TestMethod]
        public void Constructor_DefaultsRoundRarityPercentagesOff()
        {
            var settings = new PersistedSettings();

            Assert.IsFalse(settings.RoundRarityPercentages);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveRoundRarityPercentages()
        {
            var source = new PersistedSettings
            {
                RoundRarityPercentages = true
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(clone.RoundRarityPercentages);
            Assert.IsTrue(target.RoundRarityPercentages);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveProgressColumnAlignmentDefaultedFlag()
        {
            var source = new PersistedSettings { ProgressColumnAlignmentDefaulted = false };

            var clone = source.Clone();
            Assert.IsFalse(clone.ProgressColumnAlignmentDefaulted);

            var target = new PersistedSettings();
            target.CopyFrom(source);
            Assert.IsFalse(target.ProgressColumnAlignmentDefaulted);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveInGamePollingAndToastSettings()
        {
            var source = new PersistedSettings
            {
                EnableInGamePolling = false,
                InGamePollIntervalSeconds = 21,
                InGamePollRefreshFriends = true,
                InGameFriendRefreshMultiplier = 5,
                InGameFriendBatchSize = 7,
                EnableUnlockToasts = false,
                EnableFriendUnlockToasts = false,
                ToastShowRarityGlow = false,
                ToastRarityColoredName = false,
                ToastShowRarityPercent = false,
                ToastShowDescription = false,
                ToastShowCategory = false,
                ToastShowGameName = false,
                ToastDurationSeconds = 8,
                MaxConcurrentToasts = 4,
                ToastPosition = ToastScreenCorner.TopLeft
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            AssertInGamePollingAndToastSettings(source, clone);
            AssertInGamePollingAndToastSettings(source, target);
        }

        [TestMethod]
        public void InGamePollingAndToastSettings_ClampUnsafeValues()
        {
            var settings = new PersistedSettings
            {
                InGamePollIntervalSeconds = 1,
                InGameFriendRefreshMultiplier = 0,
                InGameFriendBatchSize = -1,
                ToastDurationSeconds = 0,
                MaxConcurrentToasts = 0
            };

            Assert.AreEqual(10, settings.InGamePollIntervalSeconds);
            Assert.AreEqual(1, settings.InGameFriendRefreshMultiplier);
            Assert.AreEqual(0, settings.InGameFriendBatchSize);
            Assert.AreEqual(2, settings.ToastDurationSeconds);
            Assert.AreEqual(1, settings.MaxConcurrentToasts);
        }

        [TestMethod]
        public void Constructor_DefaultsUnlockScreenshotsOff()
        {
            var settings = new PersistedSettings();

            Assert.IsFalse(settings.EnableUnlockScreenshots);
            Assert.IsTrue(string.IsNullOrEmpty(settings.UnlockScreenshotDirectory));
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveUnlockScreenshotSettings()
        {
            var source = new PersistedSettings
            {
                EnableUnlockScreenshots = true,
                UnlockScreenshotDirectory = @"C:\Shots\PlayniteAchievements"
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(clone.EnableUnlockScreenshots);
            Assert.AreEqual(source.UnlockScreenshotDirectory, clone.UnlockScreenshotDirectory);
            Assert.IsTrue(target.EnableUnlockScreenshots);
            Assert.AreEqual(source.UnlockScreenshotDirectory, target.UnlockScreenshotDirectory);
        }

        [TestMethod]
        public void Constructor_DefaultsOverviewColumnRatio()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(
                PersistedSettings.DefaultOverviewLeftColumnRatio,
                settings.OverviewLeftColumnRatio);
            Assert.AreEqual(
                PersistedSettings.DefaultFriendsOverviewFriendColumnRatio,
                settings.FriendsOverviewFriendColumnRatio);
            Assert.AreEqual(
                PersistedSettings.DefaultFriendsOverviewGameColumnRatio,
                settings.FriendsOverviewGameColumnRatio);
        }

        [TestMethod]
        public void Constructor_DefaultsOverviewScoreCardsVisible()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.ShowOverviewCollectionScoreCard);
            Assert.IsTrue(settings.ShowOverviewPrestigeScoreCard);
        }

        [TestMethod]
        public void Constructor_DefaultsViewAchievementsTimelineState()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(TimelineRange.OneYear, settings.ViewAchievementsTimelineRange);
            Assert.IsFalse(settings.ViewAchievementsTimelineVisible);
        }

        [TestMethod]
        public void Constructor_DefaultsColumnHeadersVisible()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.IsTrue(settings.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.IsTrue(settings.ShowDesktopThemeAchievementGridColumnHeaders);
            Assert.IsTrue(settings.ShowOverviewGameSummariesGridControlBar);
            Assert.IsTrue(settings.ShowOverviewRecentAchievementsGridControlBar);
            Assert.IsTrue(settings.ShowOverviewSelectedGameGridControlBar);
            Assert.IsTrue(settings.ShowViewAchievementsAchievementGridControlBar);
            Assert.IsTrue(settings.ShowDesktopThemeAchievementGridControlBar);
            Assert.IsTrue(settings.ShowFriendsOverviewFriendSummariesGridControlBar);
            Assert.IsTrue(settings.ShowFriendsOverviewGameSummariesGridControlBar);
            Assert.IsTrue(settings.ShowFriendsOverviewAchievementsGridControlBar);
            Assert.IsFalse(settings.OverviewSelectedGameAchievementsStartInCategoryMode);
            Assert.IsFalse(settings.ViewAchievementsAchievementGridStartInCategoryMode);
            Assert.IsFalse(settings.FriendsOverviewAchievementsStartInCategoryMode);
            Assert.IsFalse(settings.DesktopThemeAchievementGridStartInCategoryMode);
            Assert.IsFalse(settings.StartPageGameSummariesGrid.ShowControlBar);
            Assert.IsFalse(settings.StartPageRecentUnlocksGrid.ShowControlBar);
        }

        [TestMethod]
        public void Constructor_DefaultsGridAlignmentSettings()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(GridAlignment.Center, settings.GridColumnHeaderAlignment);
            Assert.AreEqual(GridAlignment.Left, settings.GridCellAlignment);
            Assert.AreEqual(GridVerticalAlignment.Center, settings.GridCellVerticalAlignment);
        }

        [TestMethod]
        public void Constructor_DefaultsGridLayoutSettings()
        {
            var settings = new PersistedSettings();

            Assert.IsNull(settings.SingleGameGridRowHeight);
            Assert.IsNull(settings.OverviewGameSummariesGridRowHeight);
            Assert.IsNull(settings.OverviewRecentAchievementsGridRowHeight);
            Assert.IsNull(settings.OverviewSelectedGameGridRowHeight);
            Assert.IsNull(settings.StartPageGameSummariesGridRowHeight);
            Assert.IsNull(settings.StartPageRecentAchievementsGridRowHeight);
            Assert.IsNull(settings.DesktopThemeAchievementGridRowHeight);

            Assert.IsNull(settings.SingleGameGridMaxRows);
            Assert.IsNull(settings.OverviewGameSummariesGridMaxRows);
            Assert.IsNull(settings.OverviewRecentAchievementsGridMaxRows);
            Assert.IsNull(settings.OverviewSelectedGameGridMaxRows);
            Assert.AreEqual(PersistedSettings.DefaultStartPageGridMaxRows, settings.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(PersistedSettings.DefaultStartPageGridMaxRows, settings.StartPageRecentAchievementsGridMaxRows);
            Assert.IsNull(settings.DesktopThemeAchievementGridMaxRows);
        }

        [TestMethod]
        public void Constructor_DefaultsStartPageScopes()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(GameActivityScope.Played, settings.StartPageActivityScope);
            Assert.AreEqual(
                GameProgressScope.Completed | GameProgressScope.InProgress,
                settings.StartPageProgressScope);
        }

        [TestMethod]
        public void Constructor_DefaultsAchievementHotkeys()
        {
            var settings = new PersistedSettings();

            Assert.IsTrue(settings.EnableAchievementHotkeys);
            Assert.IsFalse(settings.EnableGlobalAchievementHotkeys);
            Assert.AreEqual(PersistedSettings.DefaultViewAchievementsHotkey, settings.ViewAchievementsHotkey);
            Assert.AreEqual(PersistedSettings.DefaultManageAchievementsHotkey, settings.ManageAchievementsHotkey);
            Assert.AreEqual(PersistedSettings.DefaultOverviewHotkey, settings.OverviewHotkey);
            Assert.AreEqual(PersistedSettings.DefaultOpenSettingsHotkey, settings.OpenSettingsHotkey);
            Assert.AreEqual(PersistedSettings.DefaultCategoryModeHotkey, settings.CategoryModeHotkey);
        }

        [TestMethod]
        public void GridLayoutSettings_NormalizeInvalidValues()
        {
            var settings = new PersistedSettings();

            settings.SingleGameGridRowHeight = 12d;
            Assert.AreEqual(PersistedSettings.MinimumGridRowHeight, settings.SingleGameGridRowHeight);

            settings.SingleGameGridRowHeight = 64d;
            Assert.AreEqual(64d, settings.SingleGameGridRowHeight);

            settings.SingleGameGridRowHeight = 0d;
            Assert.IsNull(settings.SingleGameGridRowHeight);

            settings.SingleGameGridRowHeight = double.NaN;
            Assert.IsNull(settings.SingleGameGridRowHeight);

            settings.SingleGameGridRowHeight = double.PositiveInfinity;
            Assert.IsNull(settings.SingleGameGridRowHeight);

            settings.SingleGameGridMaxRows = 0;
            Assert.IsNull(settings.SingleGameGridMaxRows);

            settings.SingleGameGridMaxRows = -4;
            Assert.IsNull(settings.SingleGameGridMaxRows);

            settings.SingleGameGridMaxRows = 1;
            Assert.AreEqual(1, settings.SingleGameGridMaxRows);
        }

        [TestMethod]
        public void OverviewColumnRatio_ClampsInvalidValues()
        {
            var settings = new PersistedSettings();

            settings.OverviewLeftColumnRatio = -1d;
            Assert.AreEqual(
                PersistedSettings.MinOverviewLeftColumnRatio,
                settings.OverviewLeftColumnRatio);

            settings.OverviewLeftColumnRatio = 2d;
            Assert.AreEqual(
                PersistedSettings.MaxOverviewLeftColumnRatio,
                settings.OverviewLeftColumnRatio);

            settings.OverviewLeftColumnRatio = double.NaN;
            Assert.AreEqual(
                PersistedSettings.DefaultOverviewLeftColumnRatio,
                settings.OverviewLeftColumnRatio);

            settings.FriendsOverviewFriendColumnRatio = -1d;
            Assert.AreEqual(
                PersistedSettings.MinFriendsOverviewColumnRatio,
                settings.FriendsOverviewFriendColumnRatio);

            settings.FriendsOverviewGameColumnRatio = 2d;
            Assert.AreEqual(
                PersistedSettings.MaxFriendsOverviewColumnRatio,
                settings.FriendsOverviewGameColumnRatio);

            settings.FriendsOverviewFriendColumnRatio = double.NaN;
            Assert.AreEqual(
                PersistedSettings.DefaultFriendsOverviewFriendColumnRatio,
                settings.FriendsOverviewFriendColumnRatio);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveOverviewColumnRatio()
        {
            var source = new PersistedSettings
            {
                OverviewLeftColumnRatio = 0.64d,
                FriendsOverviewFriendColumnRatio = 0.24d,
                FriendsOverviewGameColumnRatio = 0.32d
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(0.64d, clone.OverviewLeftColumnRatio);
            Assert.AreEqual(0.64d, target.OverviewLeftColumnRatio);
            Assert.AreEqual(0.24d, clone.FriendsOverviewFriendColumnRatio);
            Assert.AreEqual(0.24d, target.FriendsOverviewFriendColumnRatio);
            Assert.AreEqual(0.32d, clone.FriendsOverviewGameColumnRatio);
            Assert.AreEqual(0.32d, target.FriendsOverviewGameColumnRatio);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveDefaultOverviewRefreshMode()
        {
            var source = new PersistedSettings
            {
                DefaultOverviewRefreshMode = RefreshModeType.Recent
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(RefreshModeType.Recent, clone.DefaultOverviewRefreshMode);
            Assert.AreEqual(RefreshModeType.Recent, target.DefaultOverviewRefreshMode);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreservePerSurfaceDateDisplayModes()
        {
            var source = new PersistedSettings
            {
                OverviewGameSummariesLastPlayedDateMode = DateDisplayMode.DateOnly,
                ViewAchievementsGameSummariesLastPlayedDateMode = DateDisplayMode.Relative,
                StartPageGameSummariesLastPlayedDateMode = DateDisplayMode.DateOnly,
                OverviewRecentAchievementsUnlockDateMode = DateDisplayMode.Relative,
                OverviewSelectedGameAchievementsUnlockDateMode = DateDisplayMode.DateOnly,
                ViewAchievementsAchievementsUnlockDateMode = DateDisplayMode.Relative,
                StartPageAchievementsUnlockDateMode = DateDisplayMode.DateOnly,
                DesktopThemeAchievementsUnlockDateMode = DateDisplayMode.Relative,
                FriendsOverviewFriendSummariesLastUnlockDateMode = DateDisplayMode.DateOnly,
                FriendsOverviewGameSummariesLastPlayedDateMode = DateDisplayMode.Relative,
                FriendsOverviewAchievementsUnlockDateMode = DateDisplayMode.DateOnly,
                UnlockDateDisplayMode = DateDisplayMode.Relative,
                PlaytimeDisplayMode = PlaytimeDisplayMode.HoursOnly
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            foreach (var copy in new[] { clone, target })
            {
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.OverviewGameSummariesLastPlayedDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.ViewAchievementsGameSummariesLastPlayedDateMode);
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.StartPageGameSummariesLastPlayedDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.OverviewRecentAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.OverviewSelectedGameAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.ViewAchievementsAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.StartPageAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.DesktopThemeAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.FriendsOverviewFriendSummariesLastUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.FriendsOverviewGameSummariesLastPlayedDateMode);
                Assert.AreEqual(DateDisplayMode.DateOnly, copy.FriendsOverviewAchievementsUnlockDateMode);
                Assert.AreEqual(DateDisplayMode.Relative, copy.UnlockDateDisplayMode);
                Assert.AreEqual(PlaytimeDisplayMode.HoursOnly, copy.PlaytimeDisplayMode);
            }
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveFriendsOverviewDisplaySettings()
        {
            var defaults = new PersistedSettings();
            var source = new PersistedSettings
            {
                FriendsOverviewGameSummariesUseCoverImages = !defaults.FriendsOverviewGameSummariesUseCoverImages,
                FriendsOverviewGameSummariesShowMetadataPlatform = !defaults.FriendsOverviewGameSummariesShowMetadataPlatform,
                FriendsOverviewGameSummariesShowMetadataPlaytime = !defaults.FriendsOverviewGameSummariesShowMetadataPlaytime,
                FriendsOverviewGameSummariesShowMetadataRegion = !defaults.FriendsOverviewGameSummariesShowMetadataRegion,
                FriendsOverviewAchievementsUseCoverImages = !defaults.FriendsOverviewAchievementsUseCoverImages,
                FriendsOverviewAchievementsShowRarityGlow = !defaults.FriendsOverviewAchievementsShowRarityGlow,
                FriendsOverviewAchievementsColorNamesByRarity = !defaults.FriendsOverviewAchievementsColorNamesByRarity,
                FriendsOverviewAchievementsColorRarityColumnsByRarity = !defaults.FriendsOverviewAchievementsColorRarityColumnsByRarity,
                FriendsOverviewAchievementsStartInCategoryMode = !defaults.FriendsOverviewAchievementsStartInCategoryMode,
                ShowFriendsOverviewFriendSummariesGridColumnHeaders = !defaults.ShowFriendsOverviewFriendSummariesGridColumnHeaders,
                ShowFriendsOverviewGameSummariesGridColumnHeaders = !defaults.ShowFriendsOverviewGameSummariesGridColumnHeaders,
                ShowFriendsOverviewAchievementsGridColumnHeaders = !defaults.ShowFriendsOverviewAchievementsGridColumnHeaders,
                ShowFriendsOverviewFriendSummariesGridControlBar = !defaults.ShowFriendsOverviewFriendSummariesGridControlBar,
                ShowFriendsOverviewGameSummariesGridControlBar = !defaults.ShowFriendsOverviewGameSummariesGridControlBar,
                ShowFriendsOverviewAchievementsGridControlBar = !defaults.ShowFriendsOverviewAchievementsGridControlBar,
                FriendsOverviewFriendSummariesGridRowHeight = 41d,
                FriendsOverviewGameSummariesGridRowHeight = 42d,
                FriendsOverviewAchievementsGridRowHeight = 43d,
                FriendsOverviewFriendSummariesGridMaxRows = 11,
                FriendsOverviewGameSummariesGridMaxRows = 12,
                FriendsOverviewAchievementsGridMaxRows = 13,
                FriendsOverviewAchievementColumnVisibility = new Dictionary<string, bool> { ["Friend"] = false },
                FriendsOverviewAchievementColumnWidths = new Dictionary<string, double> { ["Friend"] = 144d },
                FriendsOverviewAchievementColumnOrder = new Dictionary<string, int> { ["Friend"] = 2 },
                FriendsOverviewAchievementColumnAlignments = new Dictionary<string, GridAlignment> { ["Friend"] = GridAlignment.Center },
                FriendsOverviewAchievementColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment> { ["Friend"] = GridVerticalAlignment.Bottom },
                FriendsOverviewAchievementColumnHeaderAlignments = new Dictionary<string, GridAlignment> { ["Friend"] = GridAlignment.Right },
                FriendsOverviewFriendSummariesColumnVisibility = new Dictionary<string, bool> { ["FriendSummaryFriend"] = false },
                FriendsOverviewFriendSummariesColumnWidths = new Dictionary<string, double> { ["FriendSummaryFriend"] = 188d },
                FriendsOverviewFriendSummariesColumnOrder = new Dictionary<string, int> { ["FriendSummaryFriend"] = 3 },
                FriendsOverviewFriendSummariesColumnAlignments = new Dictionary<string, GridAlignment> { ["FriendSummaryFriend"] = GridAlignment.Center },
                FriendsOverviewFriendSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment> { ["FriendSummaryFriend"] = GridVerticalAlignment.Top },
                FriendsOverviewFriendSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment> { ["FriendSummaryFriend"] = GridAlignment.Right },
                FriendsOverviewGameSummariesColumnVisibility = new Dictionary<string, bool> { ["GameSummaryName"] = false },
                FriendsOverviewGameSummariesColumnWidths = new Dictionary<string, double> { ["GameSummaryName"] = 96d },
                FriendsOverviewGameSummariesColumnOrder = new Dictionary<string, int> { ["GameSummaryName"] = 4 },
                FriendsOverviewGameSummariesColumnAlignments = new Dictionary<string, GridAlignment> { ["GameSummaryName"] = GridAlignment.Right },
                FriendsOverviewGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment> { ["GameSummaryName"] = GridVerticalAlignment.Bottom },
                FriendsOverviewGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment> { ["GameSummaryName"] = GridAlignment.Center },
                FriendsOverviewSelectedFriendGameSummariesColumnVisibility = new Dictionary<string, bool> { ["GameSummaryLastUnlock"] = true },
                FriendsOverviewSelectedFriendGameSummariesColumnWidths = new Dictionary<string, double> { ["GameSummaryLastUnlock"] = 112d },
                FriendsOverviewSelectedFriendGameSummariesColumnOrder = new Dictionary<string, int> { ["GameSummaryLastUnlock"] = 5 },
                FriendsOverviewSelectedFriendGameSummariesColumnAlignments = new Dictionary<string, GridAlignment> { ["GameSummaryLastUnlock"] = GridAlignment.Left },
                FriendsOverviewSelectedFriendGameSummariesColumnVerticalAlignments = new Dictionary<string, GridVerticalAlignment> { ["GameSummaryLastUnlock"] = GridVerticalAlignment.Top },
                FriendsOverviewSelectedFriendGameSummariesColumnHeaderAlignments = new Dictionary<string, GridAlignment> { ["GameSummaryLastUnlock"] = GridAlignment.Right }
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            foreach (var copy in new[] { clone, target })
            {
                Assert.AreEqual(source.FriendsOverviewGameSummariesUseCoverImages, copy.FriendsOverviewGameSummariesUseCoverImages);
                Assert.AreEqual(source.FriendsOverviewGameSummariesShowMetadataPlatform, copy.FriendsOverviewGameSummariesShowMetadataPlatform);
                Assert.AreEqual(source.FriendsOverviewGameSummariesShowMetadataPlaytime, copy.FriendsOverviewGameSummariesShowMetadataPlaytime);
                Assert.AreEqual(source.FriendsOverviewGameSummariesShowMetadataRegion, copy.FriendsOverviewGameSummariesShowMetadataRegion);
                Assert.AreEqual(source.FriendsOverviewAchievementsUseCoverImages, copy.FriendsOverviewAchievementsUseCoverImages);
                Assert.AreEqual(source.FriendsOverviewAchievementsShowRarityGlow, copy.FriendsOverviewAchievementsShowRarityGlow);
                Assert.AreEqual(source.FriendsOverviewAchievementsColorNamesByRarity, copy.FriendsOverviewAchievementsColorNamesByRarity);
                Assert.AreEqual(source.FriendsOverviewAchievementsColorRarityColumnsByRarity, copy.FriendsOverviewAchievementsColorRarityColumnsByRarity);
                Assert.AreEqual(source.FriendsOverviewAchievementsStartInCategoryMode, copy.FriendsOverviewAchievementsStartInCategoryMode);
                Assert.AreEqual(source.ShowFriendsOverviewFriendSummariesGridColumnHeaders, copy.ShowFriendsOverviewFriendSummariesGridColumnHeaders);
                Assert.AreEqual(source.ShowFriendsOverviewGameSummariesGridColumnHeaders, copy.ShowFriendsOverviewGameSummariesGridColumnHeaders);
                Assert.AreEqual(source.ShowFriendsOverviewAchievementsGridColumnHeaders, copy.ShowFriendsOverviewAchievementsGridColumnHeaders);
                Assert.AreEqual(source.ShowFriendsOverviewFriendSummariesGridControlBar, copy.ShowFriendsOverviewFriendSummariesGridControlBar);
                Assert.AreEqual(source.ShowFriendsOverviewGameSummariesGridControlBar, copy.ShowFriendsOverviewGameSummariesGridControlBar);
                Assert.AreEqual(source.ShowFriendsOverviewAchievementsGridControlBar, copy.ShowFriendsOverviewAchievementsGridControlBar);
                Assert.AreEqual(41d, copy.FriendsOverviewFriendSummariesGridRowHeight);
                Assert.AreEqual(42d, copy.FriendsOverviewGameSummariesGridRowHeight);
                Assert.AreEqual(43d, copy.FriendsOverviewAchievementsGridRowHeight);
                Assert.AreEqual(11, copy.FriendsOverviewFriendSummariesGridMaxRows);
                Assert.AreEqual(12, copy.FriendsOverviewGameSummariesGridMaxRows);
                Assert.AreEqual(13, copy.FriendsOverviewAchievementsGridMaxRows);
                Assert.IsFalse(copy.FriendsOverviewAchievementColumnVisibility["Friend"]);
                Assert.AreEqual(144d, copy.FriendsOverviewAchievementColumnWidths["Friend"]);
                Assert.AreEqual(2, copy.FriendsOverviewAchievementColumnOrder["Friend"]);
                Assert.AreEqual(GridAlignment.Center, copy.FriendsOverviewAchievementColumnAlignments["Friend"]);
                Assert.AreEqual(GridVerticalAlignment.Bottom, copy.FriendsOverviewAchievementColumnVerticalAlignments["Friend"]);
                Assert.AreEqual(GridAlignment.Right, copy.FriendsOverviewAchievementColumnHeaderAlignments["Friend"]);
                Assert.IsFalse(copy.FriendsOverviewFriendSummariesColumnVisibility["FriendSummaryFriend"]);
                Assert.AreEqual(188d, copy.FriendsOverviewFriendSummariesColumnWidths["FriendSummaryFriend"]);
                Assert.AreEqual(3, copy.FriendsOverviewFriendSummariesColumnOrder["FriendSummaryFriend"]);
                Assert.AreEqual(GridAlignment.Center, copy.FriendsOverviewFriendSummariesColumnAlignments["FriendSummaryFriend"]);
                Assert.AreEqual(GridVerticalAlignment.Top, copy.FriendsOverviewFriendSummariesColumnVerticalAlignments["FriendSummaryFriend"]);
                Assert.AreEqual(GridAlignment.Right, copy.FriendsOverviewFriendSummariesColumnHeaderAlignments["FriendSummaryFriend"]);
                Assert.IsFalse(copy.FriendsOverviewGameSummariesColumnVisibility["GameSummaryName"]);
                Assert.AreEqual(96d, copy.FriendsOverviewGameSummariesColumnWidths["GameSummaryName"]);
                Assert.AreEqual(4, copy.FriendsOverviewGameSummariesColumnOrder["GameSummaryName"]);
                Assert.AreEqual(GridAlignment.Right, copy.FriendsOverviewGameSummariesColumnAlignments["GameSummaryName"]);
                Assert.AreEqual(GridVerticalAlignment.Bottom, copy.FriendsOverviewGameSummariesColumnVerticalAlignments["GameSummaryName"]);
                Assert.AreEqual(GridAlignment.Center, copy.FriendsOverviewGameSummariesColumnHeaderAlignments["GameSummaryName"]);
                Assert.IsTrue(copy.FriendsOverviewSelectedFriendGameSummariesColumnVisibility["GameSummaryLastUnlock"]);
                Assert.AreEqual(112d, copy.FriendsOverviewSelectedFriendGameSummariesColumnWidths["GameSummaryLastUnlock"]);
                Assert.AreEqual(5, copy.FriendsOverviewSelectedFriendGameSummariesColumnOrder["GameSummaryLastUnlock"]);
                Assert.AreEqual(GridAlignment.Left, copy.FriendsOverviewSelectedFriendGameSummariesColumnAlignments["GameSummaryLastUnlock"]);
                Assert.AreEqual(GridVerticalAlignment.Top, copy.FriendsOverviewSelectedFriendGameSummariesColumnVerticalAlignments["GameSummaryLastUnlock"]);
                Assert.AreEqual(GridAlignment.Right, copy.FriendsOverviewSelectedFriendGameSummariesColumnHeaderAlignments["GameSummaryLastUnlock"]);
            }

            Assert.AreNotSame(source.FriendsOverviewAchievementColumnVisibility, clone.FriendsOverviewAchievementColumnVisibility);
            Assert.AreNotSame(source.FriendsOverviewAchievementColumnWidths, target.FriendsOverviewAchievementColumnWidths);
            Assert.AreNotSame(source.FriendsOverviewFriendSummariesColumnOrder, clone.FriendsOverviewFriendSummariesColumnOrder);
            Assert.AreNotSame(source.FriendsOverviewFriendSummariesColumnAlignments, target.FriendsOverviewFriendSummariesColumnAlignments);
            Assert.AreNotSame(source.FriendsOverviewGameSummariesColumnVerticalAlignments, clone.FriendsOverviewGameSummariesColumnVerticalAlignments);
            Assert.AreNotSame(source.FriendsOverviewGameSummariesColumnHeaderAlignments, target.FriendsOverviewGameSummariesColumnHeaderAlignments);
            Assert.AreNotSame(source.FriendsOverviewSelectedFriendGameSummariesColumnVisibility, clone.FriendsOverviewSelectedFriendGameSummariesColumnVisibility);
            Assert.AreNotSame(source.FriendsOverviewSelectedFriendGameSummariesColumnWidths, target.FriendsOverviewSelectedFriendGameSummariesColumnWidths);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveControlTogglesAndViewAchievementsGameSummaries()
        {
            var defaults = new PersistedSettings();
            var source = new PersistedSettings
            {
                // Use the inverse of each default so an omission would surface as a reset.
                OverviewRecentAchievementsColorNamesByRarity = !defaults.OverviewRecentAchievementsColorNamesByRarity,
                OverviewSelectedGameColorNamesByRarity = !defaults.OverviewSelectedGameColorNamesByRarity,
                OverviewRecentAchievementsColorRarityColumnsByRarity = !defaults.OverviewRecentAchievementsColorRarityColumnsByRarity,
                OverviewSelectedGameColorRarityColumnsByRarity = !defaults.OverviewSelectedGameColorRarityColumnsByRarity,
                ViewAchievementsAchievementGridColorRarityColumnsByRarity = !defaults.ViewAchievementsAchievementGridColorRarityColumnsByRarity,
                ViewFriendsAchievementsColorRarityColumnsByRarity = !defaults.ViewFriendsAchievementsColorRarityColumnsByRarity,
                OverviewSelectedGameAchievementsStartInCategoryMode = !defaults.OverviewSelectedGameAchievementsStartInCategoryMode,
                ModernDataGridColorNamesByRarity = !defaults.ModernDataGridColorNamesByRarity,
                ModernDataGridColorRarityColumnsByRarity = !defaults.ModernDataGridColorRarityColumnsByRarity,
                ShowDesktopThemeAchievementGridControlBar = !defaults.ShowDesktopThemeAchievementGridControlBar,
                EnableAchievementCompactListControl = !defaults.EnableAchievementCompactListControl,
                EnableAchievementDataGridControl = !defaults.EnableAchievementDataGridControl,
                EnableAchievementCompactUnlockedListControl = !defaults.EnableAchievementCompactUnlockedListControl,
                EnableAchievementCompactLockedListControl = !defaults.EnableAchievementCompactLockedListControl,
                EnableAchievementProgressBarControl = !defaults.EnableAchievementProgressBarControl,
                EnableAchievementStatsControl = !defaults.EnableAchievementStatsControl,
                EnableAchievementButtonControl = !defaults.EnableAchievementButtonControl,
                EnableAchievementViewItemControl = !defaults.EnableAchievementViewItemControl,
                EnableAchievementPieChartControl = !defaults.EnableAchievementPieChartControl,
                EnableAchievementBarChartControl = !defaults.EnableAchievementBarChartControl,
                ViewAchievementsGameSummariesUseCoverImages = !defaults.ViewAchievementsGameSummariesUseCoverImages,
                ViewAchievementsGameSummariesShowMetadataPlatform = !defaults.ViewAchievementsGameSummariesShowMetadataPlatform,
                ViewAchievementsGameSummariesShowMetadataPlaytime = !defaults.ViewAchievementsGameSummariesShowMetadataPlaytime,
                ViewAchievementsGameSummariesShowMetadataRegion = !defaults.ViewAchievementsGameSummariesShowMetadataRegion,
                ViewAchievementsGameSummariesShowCompletionGlow = !defaults.ViewAchievementsGameSummariesShowCompletionGlow,
                ShowViewAchievementsGameSummariesGridColumnHeaders = !defaults.ShowViewAchievementsGameSummariesGridColumnHeaders,
                ShowViewAchievementsAchievementGridControlBar = !defaults.ShowViewAchievementsAchievementGridControlBar,
                ViewAchievementsAchievementGridStartInCategoryMode = !defaults.ViewAchievementsAchievementGridStartInCategoryMode,
                DesktopThemeAchievementGridStartInCategoryMode = !defaults.DesktopThemeAchievementGridStartInCategoryMode,
                ViewAchievementsGameSummariesGridRowHeight = 88d,
                ViewAchievementsGameSummariesColumnVisibility = new System.Collections.Generic.Dictionary<string, bool>
                {
                    ["GameSummaryName"] = false
                },
                ViewAchievementsGameSummariesColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["GameSummaryName"] = 3
                }
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            foreach (var copy in new[] { clone, target })
            {
                Assert.AreEqual(source.OverviewRecentAchievementsColorNamesByRarity, copy.OverviewRecentAchievementsColorNamesByRarity);
                Assert.AreEqual(source.OverviewSelectedGameColorNamesByRarity, copy.OverviewSelectedGameColorNamesByRarity);
                Assert.AreEqual(source.OverviewRecentAchievementsColorRarityColumnsByRarity, copy.OverviewRecentAchievementsColorRarityColumnsByRarity);
                Assert.AreEqual(source.OverviewSelectedGameColorRarityColumnsByRarity, copy.OverviewSelectedGameColorRarityColumnsByRarity);
                Assert.AreEqual(source.ViewAchievementsAchievementGridColorRarityColumnsByRarity, copy.ViewAchievementsAchievementGridColorRarityColumnsByRarity);
                Assert.AreEqual(source.ViewFriendsAchievementsColorRarityColumnsByRarity, copy.ViewFriendsAchievementsColorRarityColumnsByRarity);
                Assert.AreEqual(source.OverviewSelectedGameAchievementsStartInCategoryMode, copy.OverviewSelectedGameAchievementsStartInCategoryMode);
                Assert.AreEqual(source.ModernDataGridColorNamesByRarity, copy.ModernDataGridColorNamesByRarity);
                Assert.AreEqual(source.ModernDataGridColorRarityColumnsByRarity, copy.ModernDataGridColorRarityColumnsByRarity);
                Assert.AreEqual(source.ShowDesktopThemeAchievementGridControlBar, copy.ShowDesktopThemeAchievementGridControlBar);
                Assert.AreEqual(source.EnableAchievementCompactListControl, copy.EnableAchievementCompactListControl);
                Assert.AreEqual(source.EnableAchievementDataGridControl, copy.EnableAchievementDataGridControl);
                Assert.AreEqual(source.EnableAchievementCompactUnlockedListControl, copy.EnableAchievementCompactUnlockedListControl);
                Assert.AreEqual(source.EnableAchievementCompactLockedListControl, copy.EnableAchievementCompactLockedListControl);
                Assert.AreEqual(source.EnableAchievementProgressBarControl, copy.EnableAchievementProgressBarControl);
                Assert.AreEqual(source.EnableAchievementStatsControl, copy.EnableAchievementStatsControl);
                Assert.AreEqual(source.EnableAchievementButtonControl, copy.EnableAchievementButtonControl);
                Assert.AreEqual(source.EnableAchievementViewItemControl, copy.EnableAchievementViewItemControl);
                Assert.AreEqual(source.EnableAchievementPieChartControl, copy.EnableAchievementPieChartControl);
                Assert.AreEqual(source.EnableAchievementBarChartControl, copy.EnableAchievementBarChartControl);
                Assert.AreEqual(source.ViewAchievementsGameSummariesUseCoverImages, copy.ViewAchievementsGameSummariesUseCoverImages);
                Assert.AreEqual(source.ViewAchievementsGameSummariesShowMetadataPlatform, copy.ViewAchievementsGameSummariesShowMetadataPlatform);
                Assert.AreEqual(source.ViewAchievementsGameSummariesShowMetadataPlaytime, copy.ViewAchievementsGameSummariesShowMetadataPlaytime);
                Assert.AreEqual(source.ViewAchievementsGameSummariesShowMetadataRegion, copy.ViewAchievementsGameSummariesShowMetadataRegion);
                Assert.AreEqual(source.ViewAchievementsGameSummariesShowCompletionGlow, copy.ViewAchievementsGameSummariesShowCompletionGlow);
                Assert.AreEqual(source.ShowViewAchievementsGameSummariesGridColumnHeaders, copy.ShowViewAchievementsGameSummariesGridColumnHeaders);
                Assert.AreEqual(source.ShowViewAchievementsAchievementGridControlBar, copy.ShowViewAchievementsAchievementGridControlBar);
                Assert.AreEqual(source.ViewAchievementsAchievementGridStartInCategoryMode, copy.ViewAchievementsAchievementGridStartInCategoryMode);
                Assert.AreEqual(source.DesktopThemeAchievementGridStartInCategoryMode, copy.DesktopThemeAchievementGridStartInCategoryMode);
                Assert.AreEqual(88d, copy.ViewAchievementsGameSummariesGridRowHeight);
                Assert.IsFalse(copy.ViewAchievementsGameSummariesColumnVisibility["GameSummaryName"]);
                Assert.AreEqual(3, copy.ViewAchievementsGameSummariesColumnOrder["GameSummaryName"]);
                Assert.AreNotSame(source.ViewAchievementsGameSummariesColumnVisibility, copy.ViewAchievementsGameSummariesColumnVisibility);
                Assert.AreNotSame(source.ViewAchievementsGameSummariesColumnOrder, copy.ViewAchievementsGameSummariesColumnOrder);
            }
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveOverviewScoreCardVisibility()
        {
            var source = new PersistedSettings
            {
                ShowOverviewCollectionScoreCard = false,
                ShowOverviewPrestigeScoreCard = false
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.ShowOverviewCollectionScoreCard);
            Assert.IsFalse(clone.ShowOverviewPrestigeScoreCard);
            Assert.IsFalse(target.ShowOverviewCollectionScoreCard);
            Assert.IsFalse(target.ShowOverviewPrestigeScoreCard);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveViewAchievementsTimelineState()
        {
            var source = new PersistedSettings
            {
                ViewAchievementsTimelineRange = TimelineRange.All,
                ViewAchievementsTimelineVisible = true
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(TimelineRange.All, clone.ViewAchievementsTimelineRange);
            Assert.IsTrue(clone.ViewAchievementsTimelineVisible);
            Assert.AreEqual(TimelineRange.All, target.ViewAchievementsTimelineRange);
            Assert.IsTrue(target.ViewAchievementsTimelineVisible);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveAchievementHotkeySettings()
        {
            var source = new PersistedSettings
            {
                EnableAchievementHotkeys = false,
                EnableGlobalAchievementHotkeys = true,
                ViewAchievementsHotkey = "F8",
                ManageAchievementsHotkey = "Shift+F9",
                OverviewHotkey = "F10",
                OpenSettingsHotkey = "F11",
                CategoryModeHotkey = "Shift+G"
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.EnableAchievementHotkeys);
            Assert.IsTrue(clone.EnableGlobalAchievementHotkeys);
            Assert.AreEqual("F8", clone.ViewAchievementsHotkey);
            Assert.AreEqual("Shift+F9", clone.ManageAchievementsHotkey);
            Assert.AreEqual("F10", clone.OverviewHotkey);
            Assert.AreEqual("F11", clone.OpenSettingsHotkey);
            Assert.AreEqual("Shift+G", clone.CategoryModeHotkey);

            Assert.IsFalse(target.EnableAchievementHotkeys);
            Assert.IsTrue(target.EnableGlobalAchievementHotkeys);
            Assert.AreEqual("F8", target.ViewAchievementsHotkey);
            Assert.AreEqual("Shift+F9", target.ManageAchievementsHotkey);
            Assert.AreEqual("F10", target.OverviewHotkey);
            Assert.AreEqual("F11", target.OpenSettingsHotkey);
            Assert.AreEqual("Shift+G", target.CategoryModeHotkey);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveColumnHeaderVisibilityAndColumnOrder()
        {
            var source = new PersistedSettings
            {
                ShowOverviewGameSummariesGridColumnHeaders = false,
                ShowOverviewRecentAchievementsGridColumnHeaders = false,
                ShowDesktopThemeAchievementGridColumnHeaders = false,
                ShowOverviewGameSummariesGridControlBar = false,
                ShowOverviewRecentAchievementsGridControlBar = false,
                ShowOverviewSelectedGameGridControlBar = false,
                ShowDesktopThemeAchievementGridControlBar = false,
                GridColumnHeaderAlignment = GridAlignment.Right,
                GridCellAlignment = GridAlignment.Center,
                GridCellVerticalAlignment = GridVerticalAlignment.Bottom,
                OverviewRecentAchievementColumnVisibility = new System.Collections.Generic.Dictionary<string, bool>
                {
                    ["Title"] = false
                },
                OverviewRecentAchievementColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["Title"] = 2
                },
                OverviewRecentAchievementColumnAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["Title"] = GridAlignment.Center
                },
                OverviewSelectedGameAchievementColumnVisibility = new System.Collections.Generic.Dictionary<string, bool>
                {
                    ["Rarity"] = false
                },
                OverviewSelectedGameAchievementColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["Rarity"] = 3
                },
                OverviewSelectedGameAchievementColumnAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["Rarity"] = GridAlignment.Right
                },
                SingleGameColumnVisibility = new System.Collections.Generic.Dictionary<string, bool>
                {
                    ["Achievement"] = false
                },
                SingleGameColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["Achievement"] = 1
                },
                SingleGameColumnAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["Achievement"] = GridAlignment.Left
                },
                DesktopThemeColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["Points"] = 4
                },
                DesktopThemeColumnAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["Points"] = GridAlignment.Right
                },
                OverviewGameSummariesColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["GameSummaryName"] = 1
                },
                OverviewGameSummariesColumnAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["GameSummaryName"] = GridAlignment.Center
                },
                OverviewGameSummariesColumnVerticalAlignments = new System.Collections.Generic.Dictionary<string, GridVerticalAlignment>
                {
                    ["GameSummaryName"] = GridVerticalAlignment.Bottom
                },
                OverviewGameSummariesColumnHeaderAlignments = new System.Collections.Generic.Dictionary<string, GridAlignment>
                {
                    ["GameSummaryName"] = GridAlignment.Right
                },
                DataGridColumnOrder = new System.Collections.Generic.Dictionary<string, int>
                {
                    ["Legacy"] = 5
                }
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.IsFalse(clone.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.IsFalse(clone.ShowDesktopThemeAchievementGridColumnHeaders);
            Assert.IsFalse(clone.ShowOverviewGameSummariesGridControlBar);
            Assert.IsFalse(clone.ShowOverviewRecentAchievementsGridControlBar);
            Assert.IsFalse(clone.ShowOverviewSelectedGameGridControlBar);
            Assert.IsFalse(clone.ShowDesktopThemeAchievementGridControlBar);
            Assert.IsFalse(target.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.IsFalse(target.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.IsFalse(target.ShowDesktopThemeAchievementGridColumnHeaders);
            Assert.IsFalse(target.ShowOverviewGameSummariesGridControlBar);
            Assert.IsFalse(target.ShowOverviewRecentAchievementsGridControlBar);
            Assert.IsFalse(target.ShowOverviewSelectedGameGridControlBar);
            Assert.IsFalse(target.ShowDesktopThemeAchievementGridControlBar);
            Assert.AreEqual(GridAlignment.Right, clone.GridColumnHeaderAlignment);
            Assert.AreEqual(GridAlignment.Center, clone.GridCellAlignment);
            Assert.AreEqual(GridVerticalAlignment.Bottom, clone.GridCellVerticalAlignment);
            Assert.AreEqual(GridAlignment.Right, target.GridColumnHeaderAlignment);
            Assert.AreEqual(GridAlignment.Center, target.GridCellAlignment);
            Assert.AreEqual(GridVerticalAlignment.Bottom, target.GridCellVerticalAlignment);

            Assert.IsFalse(clone.OverviewRecentAchievementColumnVisibility["Title"]);
            Assert.IsFalse(clone.OverviewSelectedGameAchievementColumnVisibility["Rarity"]);
            Assert.IsFalse(clone.SingleGameColumnVisibility["Achievement"]);
            Assert.AreEqual(2, clone.OverviewRecentAchievementColumnOrder["Title"]);
            Assert.AreEqual(3, clone.OverviewSelectedGameAchievementColumnOrder["Rarity"]);
            Assert.AreEqual(1, clone.SingleGameColumnOrder["Achievement"]);
            Assert.AreEqual(4, clone.DesktopThemeColumnOrder["Points"]);
            Assert.AreEqual(1, clone.OverviewGameSummariesColumnOrder["GameSummaryName"]);
            Assert.AreEqual(5, clone.DataGridColumnOrder["Legacy"]);
            Assert.AreEqual(GridAlignment.Center, clone.OverviewRecentAchievementColumnAlignments["Title"]);
            Assert.AreEqual(GridAlignment.Right, clone.OverviewSelectedGameAchievementColumnAlignments["Rarity"]);
            Assert.AreEqual(GridAlignment.Left, clone.SingleGameColumnAlignments["Achievement"]);
            Assert.AreEqual(GridAlignment.Right, clone.DesktopThemeColumnAlignments["Points"]);
            Assert.AreEqual(GridAlignment.Center, clone.OverviewGameSummariesColumnAlignments["GameSummaryName"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, clone.OverviewGameSummariesColumnVerticalAlignments["GameSummaryName"]);
            Assert.AreEqual(GridAlignment.Right, clone.OverviewGameSummariesColumnHeaderAlignments["GameSummaryName"]);

            Assert.IsFalse(target.OverviewRecentAchievementColumnVisibility["Title"]);
            Assert.IsFalse(target.OverviewSelectedGameAchievementColumnVisibility["Rarity"]);
            Assert.IsFalse(target.SingleGameColumnVisibility["Achievement"]);
            Assert.AreEqual(2, target.OverviewRecentAchievementColumnOrder["Title"]);
            Assert.AreEqual(3, target.OverviewSelectedGameAchievementColumnOrder["Rarity"]);
            Assert.AreEqual(1, target.SingleGameColumnOrder["Achievement"]);
            Assert.AreEqual(4, target.DesktopThemeColumnOrder["Points"]);
            Assert.AreEqual(1, target.OverviewGameSummariesColumnOrder["GameSummaryName"]);
            Assert.AreEqual(5, target.DataGridColumnOrder["Legacy"]);
            Assert.AreEqual(GridAlignment.Center, target.OverviewRecentAchievementColumnAlignments["Title"]);
            Assert.AreEqual(GridAlignment.Right, target.OverviewSelectedGameAchievementColumnAlignments["Rarity"]);
            Assert.AreEqual(GridAlignment.Left, target.SingleGameColumnAlignments["Achievement"]);
            Assert.AreEqual(GridAlignment.Right, target.DesktopThemeColumnAlignments["Points"]);
            Assert.AreEqual(GridAlignment.Center, target.OverviewGameSummariesColumnAlignments["GameSummaryName"]);
            Assert.AreEqual(GridVerticalAlignment.Bottom, target.OverviewGameSummariesColumnVerticalAlignments["GameSummaryName"]);
            Assert.AreEqual(GridAlignment.Right, target.OverviewGameSummariesColumnHeaderAlignments["GameSummaryName"]);

            Assert.AreNotSame(source.OverviewRecentAchievementColumnOrder, clone.OverviewRecentAchievementColumnOrder);
            Assert.AreNotSame(source.OverviewRecentAchievementColumnVisibility, clone.OverviewRecentAchievementColumnVisibility);
            Assert.AreNotSame(source.OverviewSelectedGameAchievementColumnOrder, target.OverviewSelectedGameAchievementColumnOrder);
            Assert.AreNotSame(source.OverviewSelectedGameAchievementColumnVisibility, target.OverviewSelectedGameAchievementColumnVisibility);
            Assert.AreNotSame(source.DesktopThemeColumnOrder, clone.DesktopThemeColumnOrder);
            Assert.AreNotSame(source.OverviewGameSummariesColumnOrder, target.OverviewGameSummariesColumnOrder);
            Assert.AreNotSame(source.OverviewRecentAchievementColumnAlignments, clone.OverviewRecentAchievementColumnAlignments);
            Assert.AreNotSame(source.OverviewSelectedGameAchievementColumnAlignments, target.OverviewSelectedGameAchievementColumnAlignments);
            Assert.AreNotSame(source.SingleGameColumnVisibility, clone.SingleGameColumnVisibility);
            Assert.AreNotSame(source.SingleGameColumnAlignments, clone.SingleGameColumnAlignments);
            Assert.AreNotSame(source.DesktopThemeColumnAlignments, clone.DesktopThemeColumnAlignments);
            Assert.AreNotSame(source.OverviewGameSummariesColumnAlignments, target.OverviewGameSummariesColumnAlignments);
            Assert.AreNotSame(source.OverviewGameSummariesColumnVerticalAlignments, target.OverviewGameSummariesColumnVerticalAlignments);
            Assert.AreNotSame(source.OverviewGameSummariesColumnHeaderAlignments, target.OverviewGameSummariesColumnHeaderAlignments);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveGridLayoutSettings()
        {
            var source = new PersistedSettings
            {
                SingleGameGridRowHeight = 72d,
                OverviewGameSummariesGridRowHeight = 84d,
                OverviewRecentAchievementsGridRowHeight = 96d,
                OverviewSelectedGameGridRowHeight = 108d,
                StartPageGameSummariesGridRowHeight = 120d,
                StartPageRecentAchievementsGridRowHeight = 132d,
                DesktopThemeAchievementGridRowHeight = 144d,
                FriendsOverviewFriendSummariesGridRowHeight = 145d,
                FriendsOverviewGameSummariesGridRowHeight = 146d,
                FriendsOverviewAchievementsGridRowHeight = 147d,
                SingleGameGridMaxRows = 2,
                OverviewGameSummariesGridMaxRows = 3,
                OverviewRecentAchievementsGridMaxRows = 4,
                OverviewSelectedGameGridMaxRows = 5,
                StartPageGameSummariesGridMaxRows = 6,
                StartPageRecentAchievementsGridMaxRows = 7,
                DesktopThemeAchievementGridMaxRows = 8,
                FriendsOverviewFriendSummariesGridMaxRows = 9,
                FriendsOverviewGameSummariesGridMaxRows = 10,
                FriendsOverviewAchievementsGridMaxRows = 11
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(72d, clone.SingleGameGridRowHeight);
            Assert.AreEqual(84d, clone.OverviewGameSummariesGridRowHeight);
            Assert.AreEqual(96d, clone.OverviewRecentAchievementsGridRowHeight);
            Assert.AreEqual(108d, clone.OverviewSelectedGameGridRowHeight);
            Assert.AreEqual(120d, clone.StartPageGameSummariesGridRowHeight);
            Assert.AreEqual(132d, clone.StartPageRecentAchievementsGridRowHeight);
            Assert.AreEqual(144d, clone.DesktopThemeAchievementGridRowHeight);
            Assert.AreEqual(145d, clone.FriendsOverviewFriendSummariesGridRowHeight);
            Assert.AreEqual(146d, clone.FriendsOverviewGameSummariesGridRowHeight);
            Assert.AreEqual(147d, clone.FriendsOverviewAchievementsGridRowHeight);
            Assert.AreEqual(2, clone.SingleGameGridMaxRows);
            Assert.AreEqual(3, clone.OverviewGameSummariesGridMaxRows);
            Assert.AreEqual(4, clone.OverviewRecentAchievementsGridMaxRows);
            Assert.AreEqual(5, clone.OverviewSelectedGameGridMaxRows);
            Assert.AreEqual(6, clone.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(7, clone.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(8, clone.DesktopThemeAchievementGridMaxRows);
            Assert.AreEqual(9, clone.FriendsOverviewFriendSummariesGridMaxRows);
            Assert.AreEqual(10, clone.FriendsOverviewGameSummariesGridMaxRows);
            Assert.AreEqual(11, clone.FriendsOverviewAchievementsGridMaxRows);

            Assert.AreEqual(clone.SingleGameGridRowHeight, target.SingleGameGridRowHeight);
            Assert.AreEqual(clone.OverviewGameSummariesGridRowHeight, target.OverviewGameSummariesGridRowHeight);
            Assert.AreEqual(clone.OverviewRecentAchievementsGridRowHeight, target.OverviewRecentAchievementsGridRowHeight);
            Assert.AreEqual(clone.OverviewSelectedGameGridRowHeight, target.OverviewSelectedGameGridRowHeight);
            Assert.AreEqual(clone.StartPageGameSummariesGridRowHeight, target.StartPageGameSummariesGridRowHeight);
            Assert.AreEqual(clone.StartPageRecentAchievementsGridRowHeight, target.StartPageRecentAchievementsGridRowHeight);
            Assert.AreEqual(clone.DesktopThemeAchievementGridRowHeight, target.DesktopThemeAchievementGridRowHeight);
            Assert.AreEqual(clone.FriendsOverviewFriendSummariesGridRowHeight, target.FriendsOverviewFriendSummariesGridRowHeight);
            Assert.AreEqual(clone.FriendsOverviewGameSummariesGridRowHeight, target.FriendsOverviewGameSummariesGridRowHeight);
            Assert.AreEqual(clone.FriendsOverviewAchievementsGridRowHeight, target.FriendsOverviewAchievementsGridRowHeight);
            Assert.AreEqual(clone.SingleGameGridMaxRows, target.SingleGameGridMaxRows);
            Assert.AreEqual(clone.OverviewGameSummariesGridMaxRows, target.OverviewGameSummariesGridMaxRows);
            Assert.AreEqual(clone.OverviewRecentAchievementsGridMaxRows, target.OverviewRecentAchievementsGridMaxRows);
            Assert.AreEqual(clone.OverviewSelectedGameGridMaxRows, target.OverviewSelectedGameGridMaxRows);
            Assert.AreEqual(clone.StartPageGameSummariesGridMaxRows, target.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(clone.StartPageRecentAchievementsGridMaxRows, target.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(clone.DesktopThemeAchievementGridMaxRows, target.DesktopThemeAchievementGridMaxRows);
            Assert.AreEqual(clone.FriendsOverviewFriendSummariesGridMaxRows, target.FriendsOverviewFriendSummariesGridMaxRows);
            Assert.AreEqual(clone.FriendsOverviewGameSummariesGridMaxRows, target.FriendsOverviewGameSummariesGridMaxRows);
            Assert.AreEqual(clone.FriendsOverviewAchievementsGridMaxRows, target.FriendsOverviewAchievementsGridMaxRows);
        }

        [TestMethod]
        public void ResetDisplaySettingsToDefaults_ResetsFriendsOverviewGridSettings()
        {
            var defaults = new PersistedSettings();
            var settings = new PersistedSettings
            {
                FriendsOverviewGameSummariesUseCoverImages = !defaults.FriendsOverviewGameSummariesUseCoverImages,
                FriendsOverviewGameSummariesShowMetadataPlatform = !defaults.FriendsOverviewGameSummariesShowMetadataPlatform,
                FriendsOverviewGameSummariesShowMetadataPlaytime = !defaults.FriendsOverviewGameSummariesShowMetadataPlaytime,
                FriendsOverviewGameSummariesShowMetadataRegion = !defaults.FriendsOverviewGameSummariesShowMetadataRegion,
                FriendsOverviewAchievementsUseCoverImages = !defaults.FriendsOverviewAchievementsUseCoverImages,
                FriendsOverviewAchievementsShowRarityGlow = !defaults.FriendsOverviewAchievementsShowRarityGlow,
                FriendsOverviewAchievementsColorNamesByRarity = !defaults.FriendsOverviewAchievementsColorNamesByRarity,
                FriendsOverviewAchievementsColorRarityColumnsByRarity = !defaults.FriendsOverviewAchievementsColorRarityColumnsByRarity,
                ShowFriendsOverviewFriendSummariesGridColumnHeaders = !defaults.ShowFriendsOverviewFriendSummariesGridColumnHeaders,
                ShowFriendsOverviewGameSummariesGridColumnHeaders = !defaults.ShowFriendsOverviewGameSummariesGridColumnHeaders,
                ShowFriendsOverviewAchievementsGridColumnHeaders = !defaults.ShowFriendsOverviewAchievementsGridColumnHeaders,
                ShowFriendsOverviewFriendSummariesGridControlBar = !defaults.ShowFriendsOverviewFriendSummariesGridControlBar,
                ShowFriendsOverviewGameSummariesGridControlBar = !defaults.ShowFriendsOverviewGameSummariesGridControlBar,
                ShowFriendsOverviewAchievementsGridControlBar = !defaults.ShowFriendsOverviewAchievementsGridControlBar,
                FriendsOverviewFriendSummariesGridRowHeight = 45d,
                FriendsOverviewGameSummariesGridRowHeight = 46d,
                FriendsOverviewAchievementsGridRowHeight = 47d,
                FriendsOverviewFriendSummariesGridMaxRows = 2,
                FriendsOverviewGameSummariesGridMaxRows = 3,
                FriendsOverviewAchievementsGridMaxRows = 4,
                FriendsOverviewFriendColumnRatio = 0.22d,
                FriendsOverviewGameColumnRatio = 0.36d
            };
            settings.FriendsOverviewAchievementColumnVisibility["Friend"] = false;
            settings.FriendsOverviewAchievementColumnWidths["Friend"] = 144d;
            settings.FriendsOverviewAchievementColumnOrder["Friend"] = 1;
            settings.FriendsOverviewFriendSummariesColumnVisibility["FriendSummaryFriend"] = false;
            settings.FriendsOverviewFriendSummariesColumnWidths["FriendSummaryFriend"] = 188d;
            settings.FriendsOverviewFriendSummariesColumnOrder["FriendSummaryFriend"] = 2;
            settings.FriendsOverviewGameSummariesColumnVisibility["GameSummaryName"] = false;
            settings.FriendsOverviewGameSummariesColumnWidths["GameSummaryName"] = 96d;
            settings.FriendsOverviewGameSummariesColumnOrder["GameSummaryName"] = 3;
            settings.FriendsOverviewGameSummariesColumnAlignments["GameSummaryName"] = GridAlignment.Right;
            settings.FriendsOverviewSelectedFriendGameSummariesColumnVisibility["GameSummaryLastUnlock"] = true;
            settings.FriendsOverviewSelectedFriendGameSummariesColumnWidths["GameSummaryLastUnlock"] = 112d;
            settings.FriendsOverviewSelectedFriendGameSummariesColumnOrder["GameSummaryLastUnlock"] = 4;
            settings.FriendsOverviewSelectedFriendGameSummariesColumnAlignments["GameSummaryLastUnlock"] = GridAlignment.Left;

            settings.ResetDisplaySettingsToDefaults();

            Assert.AreEqual(defaults.FriendsOverviewGameSummariesUseCoverImages, settings.FriendsOverviewGameSummariesUseCoverImages);
            Assert.AreEqual(defaults.FriendsOverviewGameSummariesShowMetadataPlatform, settings.FriendsOverviewGameSummariesShowMetadataPlatform);
            Assert.AreEqual(defaults.FriendsOverviewGameSummariesShowMetadataPlaytime, settings.FriendsOverviewGameSummariesShowMetadataPlaytime);
            Assert.AreEqual(defaults.FriendsOverviewGameSummariesShowMetadataRegion, settings.FriendsOverviewGameSummariesShowMetadataRegion);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsUseCoverImages, settings.FriendsOverviewAchievementsUseCoverImages);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsShowRarityGlow, settings.FriendsOverviewAchievementsShowRarityGlow);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsColorNamesByRarity, settings.FriendsOverviewAchievementsColorNamesByRarity);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsColorRarityColumnsByRarity, settings.FriendsOverviewAchievementsColorRarityColumnsByRarity);
            Assert.AreEqual(defaults.ShowFriendsOverviewFriendSummariesGridColumnHeaders, settings.ShowFriendsOverviewFriendSummariesGridColumnHeaders);
            Assert.AreEqual(defaults.ShowFriendsOverviewGameSummariesGridColumnHeaders, settings.ShowFriendsOverviewGameSummariesGridColumnHeaders);
            Assert.AreEqual(defaults.ShowFriendsOverviewAchievementsGridColumnHeaders, settings.ShowFriendsOverviewAchievementsGridColumnHeaders);
            Assert.AreEqual(defaults.ShowFriendsOverviewFriendSummariesGridControlBar, settings.ShowFriendsOverviewFriendSummariesGridControlBar);
            Assert.AreEqual(defaults.ShowFriendsOverviewGameSummariesGridControlBar, settings.ShowFriendsOverviewGameSummariesGridControlBar);
            Assert.AreEqual(defaults.ShowFriendsOverviewAchievementsGridControlBar, settings.ShowFriendsOverviewAchievementsGridControlBar);
            Assert.AreEqual(defaults.FriendsOverviewFriendSummariesGridRowHeight, settings.FriendsOverviewFriendSummariesGridRowHeight);
            Assert.AreEqual(defaults.FriendsOverviewGameSummariesGridRowHeight, settings.FriendsOverviewGameSummariesGridRowHeight);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsGridRowHeight, settings.FriendsOverviewAchievementsGridRowHeight);
            Assert.AreEqual(defaults.FriendsOverviewFriendSummariesGridMaxRows, settings.FriendsOverviewFriendSummariesGridMaxRows);
            Assert.AreEqual(defaults.FriendsOverviewGameSummariesGridMaxRows, settings.FriendsOverviewGameSummariesGridMaxRows);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsGridMaxRows, settings.FriendsOverviewAchievementsGridMaxRows);
            Assert.AreEqual(defaults.FriendsOverviewFriendColumnRatio, settings.FriendsOverviewFriendColumnRatio);
            Assert.AreEqual(defaults.FriendsOverviewGameColumnRatio, settings.FriendsOverviewGameColumnRatio);
            Assert.AreEqual(0, settings.FriendsOverviewAchievementColumnVisibility.Count);
            Assert.AreEqual(0, settings.FriendsOverviewAchievementColumnWidths.Count);
            Assert.AreEqual(0, settings.FriendsOverviewAchievementColumnOrder.Count);
            Assert.AreEqual(0, settings.FriendsOverviewFriendSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.FriendsOverviewFriendSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.FriendsOverviewFriendSummariesColumnOrder.Count);
            Assert.AreEqual(0, settings.FriendsOverviewGameSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.FriendsOverviewGameSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.FriendsOverviewGameSummariesColumnOrder.Count);
            Assert.AreEqual(1, settings.FriendsOverviewGameSummariesColumnAlignments.Count);
            Assert.AreEqual(GridAlignment.Right, settings.FriendsOverviewGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(0, settings.FriendsOverviewSelectedFriendGameSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.FriendsOverviewSelectedFriendGameSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.FriendsOverviewSelectedFriendGameSummariesColumnOrder.Count);
            Assert.AreEqual(1, settings.FriendsOverviewSelectedFriendGameSummariesColumnAlignments.Count);
            Assert.AreEqual(GridAlignment.Right, settings.FriendsOverviewSelectedFriendGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
        }

        [TestMethod]
        public void ResetDisplaySettingsToDefaults_ResetsDisplayLayoutAndPreservesUserData()
        {
            var gameId = Guid.NewGuid();
            var excludedGameId = Guid.NewGuid();
            var statusId = Guid.NewGuid();
            var defaults = new PersistedSettings();
            var settings = new PersistedSettings
            {
                ProviderSettings = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Steam"] = JObject.Parse(@"{""ApiKey"":""secret""}")
                },
                EnablePeriodicUpdates = false,
                IncludeHiddenGamesInBulkScans = false,
                PeriodicUpdateHours = 48,
                RecentRefreshGamesCount = 7,
                FirstTimeSetupCompleted = true,
                SeenThemeMigration = true,
                ThemeMigrationVersionCache = new Dictionary<string, ThemeMigrationCacheEntry>(StringComparer.OrdinalIgnoreCase)
                {
                    ["theme"] = new ThemeMigrationCacheEntry
                    {
                        ThemeName = "Theme",
                        ThemePath = "C:\\Theme",
                        MigratedThemeVersion = "2.0",
                        MigratedAtUtc = new DateTime(2026, 1, 1)
                    }
                },
                ExcludedGameIds = new HashSet<Guid> { excludedGameId },
                ExcludedFromSummariesGameIds = new HashSet<Guid> { gameId },
                ManualCapstones = new Dictionary<Guid, string> { [gameId] = "capstone" },
                AchievementOrderOverrides = new Dictionary<Guid, List<string>>
                {
                    [gameId] = new List<string> { "first", "second" }
                },
                AchievementCategoryOverrides = new Dictionary<Guid, Dictionary<string, string>>
                {
                    [gameId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["first"] = "DLC"
                    }
                },
                AchievementCategoryTypeOverrides = new Dictionary<Guid, Dictionary<string, string>>
                {
                    [gameId] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["first"] = "Base"
                    }
                },
                WindowPlacements = new Dictionary<string, WindowPlacementState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["overview"] = new WindowPlacementState
                    {
                        Left = 10,
                        Top = 20,
                        Width = 900,
                        Height = 700,
                        IsMaximized = true
                    }
                },
                TaggingSettings = new TaggingSettings
                {
                    EnableTagging = true,
                    SetCompletionStatus = true,
                    CompletionStatusId = statusId
                },

                ShowHiddenIcon = true,
                OverviewRecentAchievementsShowRarityGlow = false,
                OverviewSelectedGameShowRarityGlow = false,
                ModernDataGridShowRarityGlow = false,
                ModernCompactListShowRarityGlow = false,
                ModernUnlockedListShowRarityGlow = false,
                UseUniformRarityBadges = true,
                UseTrophiesForRarity = true,
                RoundRarityPercentages = true,
                OverviewGameSummariesUseCoverImages = false,
                OverviewRecentAchievementsUseCoverImages = false,
                ShowOverviewCollectionScoreCard = false,
                ShowOverviewPrestigeScoreCard = false,
                ShowOverviewPieCharts = false,
                ShowOverviewBarCharts = false,
                ShowOverviewGameMetadataPlatform = false,
                ShowOverviewGameMetadataPlaytime = false,
                ShowOverviewGameMetadataRegion = false,
                ShowTopMenuBarButton = false,
                ShowCompletedProgressColoring = false,
                ShowCompactListRarityBar = false,
                ShowCompletionGlow = false,
                ShowOverviewGameSummariesGridColumnHeaders = false,
                ShowOverviewRecentAchievementsGridColumnHeaders = false,
                ShowDesktopThemeAchievementGridColumnHeaders = false,
                ShowOverviewGameSummariesGridControlBar = false,
                ShowOverviewRecentAchievementsGridControlBar = false,
                ShowOverviewSelectedGameGridControlBar = false,
                ShowViewAchievementsAchievementGridControlBar = false,
                ShowDesktopThemeAchievementGridControlBar = false,
                ShowFriendsOverviewFriendSummariesGridControlBar = false,
                ShowFriendsOverviewGameSummariesGridControlBar = false,
                ShowFriendsOverviewAchievementsGridControlBar = false,
                OverviewSelectedGameAchievementsStartInCategoryMode = true,
                ViewAchievementsAchievementGridStartInCategoryMode = true,
                FriendsOverviewAchievementsStartInCategoryMode = true,
                DesktopThemeAchievementGridStartInCategoryMode = true,
                GridColumnHeaderAlignment = GridAlignment.Right,
                GridCellAlignment = GridAlignment.Center,
                GridCellVerticalAlignment = GridVerticalAlignment.Bottom,
                EnableAchievementDataGridControl = false,
                OverviewGameSummariesGridSortMode = GameSummariesSortMode.Alphabetical,
                OverviewGameSummariesGridSortDescending = false,
                OverviewSelectedGameGridSortMode = CompactListSortMode.Rarity,
                OverviewSelectedGameGridSortDescending = false,
                AchievementDataGridMaxHeight = 333d,
                OverviewGameSummariesGridRowHeight = 64d,
                OverviewGameSummariesGridMaxRows = 4,
                OverviewLeftColumnRatio = 0.72d,
                FriendsOverviewFriendColumnRatio = 0.23d,
                FriendsOverviewGameColumnRatio = 0.37d,
                ViewAchievementsTimelineRange = TimelineRange.All,
                ViewAchievementsTimelineVisible = true
            };

            settings.TaggingSettings.CompletedConfig.DisplayName = "Done";
            settings.TaggingSettings.CompletedConfig.IsEnabled = false;
            settings.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            settings.StartPageGameSummariesGrid.ShowControlBar = true;
            settings.StartPageGameSummariesGrid.RowHeight = 70d;
            settings.StartPageGameSummariesGrid.MaxRows = 3;
            settings.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
            settings.StartPageRecentUnlocksGrid.ShowControlBar = true;
            settings.StartPageRecentUnlocksGrid.RowHeight = 72d;
            settings.StartPageRecentUnlocksGrid.MaxRows = 4;
            settings.StartPagePieCharts.ShowCenterPercentage = false;
            settings.StartPagePieCharts.SmallSliceMode = OverviewPieSmallSliceMode.Hide;
            settings.StartPageActivityScope = GameActivityScope.All;
            settings.StartPageProgressScope = GameProgressScope.NoProgress;
            settings.DataGridColumnVisibility["Title"] = false;
            settings.DataGridColumnWidths["Title"] = 100d;
            settings.DataGridColumnOrder["Title"] = 2;
            settings.OverviewRecentAchievementColumnVisibility["Title"] = false;
            settings.OverviewRecentAchievementColumnWidths["Title"] = 101d;
            settings.OverviewRecentAchievementColumnOrder["Title"] = 3;
            settings.OverviewRecentAchievementColumnAlignments["Title"] = GridAlignment.Center;
            settings.OverviewSelectedGameAchievementColumnVisibility["Rarity"] = false;
            settings.OverviewSelectedGameAchievementColumnWidths["Rarity"] = 102d;
            settings.OverviewSelectedGameAchievementColumnOrder["Rarity"] = 4;
            settings.OverviewSelectedGameAchievementColumnAlignments["Rarity"] = GridAlignment.Right;
            settings.SingleGameColumnVisibility["Points"] = false;
            settings.SingleGameColumnWidths["Points"] = 103d;
            settings.SingleGameColumnOrder["Points"] = 5;
            settings.SingleGameColumnAlignments["Points"] = GridAlignment.Right;
            settings.DesktopThemeColumnWidths["Points"] = 104d;
            settings.DesktopThemeColumnOrder["Points"] = 6;
            settings.DesktopThemeColumnAlignments["Points"] = GridAlignment.Right;
            settings.OverviewGameSummariesColumnVisibility["Cover"] = false;
            settings.OverviewGameSummariesColumnWidths["Cover"] = 105d;
            settings.OverviewGameSummariesColumnOrder["Cover"] = 7;
            settings.OverviewGameSummariesColumnAlignments["Cover"] = GridAlignment.Center;
            settings.StartPageAchievementColumnVisibility["Icon"] = false;
            settings.StartPageAchievementColumnWidths["Icon"] = 106d;
            settings.StartPageAchievementColumnOrder["Icon"] = 8;
            settings.StartPageAchievementColumnAlignments["Icon"] = GridAlignment.Center;
            settings.StartPageGameSummariesColumnVisibility["Cover"] = false;
            settings.StartPageGameSummariesColumnWidths["Cover"] = 107d;
            settings.StartPageGameSummariesColumnOrder["Cover"] = 9;
            settings.StartPageGameSummariesColumnAlignments["Cover"] = GridAlignment.Center;

            settings.ResetDisplaySettingsToDefaults();

            Assert.AreEqual(defaults.ShowHiddenIcon, settings.ShowHiddenIcon);
            Assert.AreEqual(defaults.OverviewRecentAchievementsShowRarityGlow, settings.OverviewRecentAchievementsShowRarityGlow);
            Assert.AreEqual(defaults.OverviewSelectedGameShowRarityGlow, settings.OverviewSelectedGameShowRarityGlow);
            Assert.AreEqual(defaults.ModernDataGridShowRarityGlow, settings.ModernDataGridShowRarityGlow);
            Assert.AreEqual(defaults.ModernCompactListShowRarityGlow, settings.ModernCompactListShowRarityGlow);
            Assert.AreEqual(defaults.ModernUnlockedListShowRarityGlow, settings.ModernUnlockedListShowRarityGlow);
            Assert.AreEqual(defaults.UseUniformRarityBadges, settings.UseUniformRarityBadges);
            Assert.AreEqual(defaults.UseTrophiesForRarity, settings.UseTrophiesForRarity);
            Assert.AreEqual(defaults.RoundRarityPercentages, settings.RoundRarityPercentages);
            Assert.AreEqual(defaults.OverviewGameSummariesUseCoverImages, settings.OverviewGameSummariesUseCoverImages);
            Assert.AreEqual(defaults.OverviewRecentAchievementsUseCoverImages, settings.OverviewRecentAchievementsUseCoverImages);
            Assert.AreEqual(defaults.ShowOverviewCollectionScoreCard, settings.ShowOverviewCollectionScoreCard);
            Assert.AreEqual(defaults.ShowOverviewPrestigeScoreCard, settings.ShowOverviewPrestigeScoreCard);
            Assert.AreEqual(defaults.ShowOverviewPieCharts, settings.ShowOverviewPieCharts);
            Assert.AreEqual(defaults.ShowOverviewGamesPieChart, settings.ShowOverviewGamesPieChart);
            Assert.AreEqual(defaults.ShowOverviewProviderPieChart, settings.ShowOverviewProviderPieChart);
            Assert.AreEqual(defaults.ShowOverviewRarityPieChart, settings.ShowOverviewRarityPieChart);
            Assert.AreEqual(defaults.ShowOverviewTrophyPieChart, settings.ShowOverviewTrophyPieChart);
            Assert.AreEqual(defaults.ShowOverviewBarCharts, settings.ShowOverviewBarCharts);
            Assert.AreEqual(defaults.ShowOverviewGameMetadataPlatform, settings.ShowOverviewGameMetadataPlatform);
            Assert.AreEqual(defaults.ShowOverviewGameMetadataPlaytime, settings.ShowOverviewGameMetadataPlaytime);
            Assert.AreEqual(defaults.ShowOverviewGameMetadataRegion, settings.ShowOverviewGameMetadataRegion);
            Assert.AreEqual(defaults.ShowTopMenuBarButton, settings.ShowTopMenuBarButton);
            Assert.AreEqual(defaults.ShowCompletedProgressColoring, settings.ShowCompletedProgressColoring);
            Assert.AreEqual(defaults.ShowCompactListRarityBar, settings.ShowCompactListRarityBar);
            Assert.AreEqual(defaults.ShowCompletionGlow, settings.ShowCompletionGlow);
            Assert.AreEqual(defaults.ShowOverviewGameSummariesGridColumnHeaders, settings.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.AreEqual(defaults.ShowOverviewRecentAchievementsGridColumnHeaders, settings.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.AreEqual(defaults.ShowDesktopThemeAchievementGridColumnHeaders, settings.ShowDesktopThemeAchievementGridColumnHeaders);
            Assert.AreEqual(defaults.ShowOverviewGameSummariesGridControlBar, settings.ShowOverviewGameSummariesGridControlBar);
            Assert.AreEqual(defaults.ShowOverviewRecentAchievementsGridControlBar, settings.ShowOverviewRecentAchievementsGridControlBar);
            Assert.AreEqual(defaults.ShowOverviewSelectedGameGridControlBar, settings.ShowOverviewSelectedGameGridControlBar);
            Assert.AreEqual(defaults.ShowViewAchievementsAchievementGridControlBar, settings.ShowViewAchievementsAchievementGridControlBar);
            Assert.AreEqual(defaults.ShowDesktopThemeAchievementGridControlBar, settings.ShowDesktopThemeAchievementGridControlBar);
            Assert.AreEqual(defaults.ShowFriendsOverviewFriendSummariesGridControlBar, settings.ShowFriendsOverviewFriendSummariesGridControlBar);
            Assert.AreEqual(defaults.ShowFriendsOverviewGameSummariesGridControlBar, settings.ShowFriendsOverviewGameSummariesGridControlBar);
            Assert.AreEqual(defaults.ShowFriendsOverviewAchievementsGridControlBar, settings.ShowFriendsOverviewAchievementsGridControlBar);
            Assert.AreEqual(defaults.OverviewSelectedGameAchievementsStartInCategoryMode, settings.OverviewSelectedGameAchievementsStartInCategoryMode);
            Assert.AreEqual(defaults.ViewAchievementsAchievementGridStartInCategoryMode, settings.ViewAchievementsAchievementGridStartInCategoryMode);
            Assert.AreEqual(defaults.FriendsOverviewAchievementsStartInCategoryMode, settings.FriendsOverviewAchievementsStartInCategoryMode);
            Assert.AreEqual(defaults.DesktopThemeAchievementGridStartInCategoryMode, settings.DesktopThemeAchievementGridStartInCategoryMode);
            Assert.AreEqual(defaults.GridColumnHeaderAlignment, settings.GridColumnHeaderAlignment);
            Assert.AreEqual(defaults.GridCellAlignment, settings.GridCellAlignment);
            Assert.AreEqual(defaults.GridCellVerticalAlignment, settings.GridCellVerticalAlignment);
            Assert.AreEqual(defaults.EnableAchievementDataGridControl, settings.EnableAchievementDataGridControl);
            Assert.AreEqual(defaults.OverviewGameSummariesGridSortMode, settings.OverviewGameSummariesGridSortMode);
            Assert.AreEqual(defaults.OverviewGameSummariesGridSortDescending, settings.OverviewGameSummariesGridSortDescending);
            Assert.AreEqual(defaults.OverviewSelectedGameGridSortMode, settings.OverviewSelectedGameGridSortMode);
            Assert.AreEqual(defaults.OverviewSelectedGameGridSortDescending, settings.OverviewSelectedGameGridSortDescending);
            Assert.AreEqual(defaults.AchievementDataGridMaxHeight, settings.AchievementDataGridMaxHeight);
            Assert.AreEqual(defaults.OverviewGameSummariesGridRowHeight, settings.OverviewGameSummariesGridRowHeight);
            Assert.AreEqual(defaults.OverviewGameSummariesGridMaxRows, settings.OverviewGameSummariesGridMaxRows);
            Assert.AreEqual(defaults.StartPageGameSummariesGrid.ShowColumnHeaders, settings.StartPageGameSummariesGrid.ShowColumnHeaders);
            Assert.AreEqual(defaults.StartPageGameSummariesGrid.ShowControlBar, settings.StartPageGameSummariesGrid.ShowControlBar);
            Assert.AreEqual(defaults.StartPageGameSummariesGridRowHeight, settings.StartPageGameSummariesGridRowHeight);
            Assert.AreEqual(defaults.StartPageGameSummariesGridMaxRows, settings.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(defaults.StartPageRecentUnlocksGrid.ShowColumnHeaders, settings.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.AreEqual(defaults.StartPageRecentUnlocksGrid.ShowControlBar, settings.StartPageRecentUnlocksGrid.ShowControlBar);
            Assert.AreEqual(defaults.StartPageRecentAchievementsGridRowHeight, settings.StartPageRecentAchievementsGridRowHeight);
            Assert.AreEqual(defaults.StartPageRecentAchievementsGridMaxRows, settings.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(defaults.StartPagePieCharts.ShowCenterPercentage, settings.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(defaults.StartPagePieCharts.SmallSliceMode, settings.StartPagePieCharts.SmallSliceMode);
            Assert.AreEqual(defaults.StartPageActivityScope, settings.StartPageActivityScope);
            Assert.AreEqual(defaults.StartPageProgressScope, settings.StartPageProgressScope);
            Assert.AreEqual(defaults.OverviewLeftColumnRatio, settings.OverviewLeftColumnRatio);
            Assert.AreEqual(defaults.FriendsOverviewFriendColumnRatio, settings.FriendsOverviewFriendColumnRatio);
            Assert.AreEqual(defaults.FriendsOverviewGameColumnRatio, settings.FriendsOverviewGameColumnRatio);
            Assert.AreEqual(defaults.ViewAchievementsTimelineRange, settings.ViewAchievementsTimelineRange);
            Assert.AreEqual(defaults.ViewAchievementsTimelineVisible, settings.ViewAchievementsTimelineVisible);

            Assert.AreEqual(0, settings.DataGridColumnVisibility.Count);
            Assert.AreEqual(0, settings.DataGridColumnWidths.Count);
            Assert.AreEqual(0, settings.DataGridColumnOrder.Count);
            Assert.AreEqual(0, settings.OverviewRecentAchievementColumnVisibility.Count);
            Assert.AreEqual(0, settings.OverviewRecentAchievementColumnWidths.Count);
            Assert.AreEqual(0, settings.OverviewRecentAchievementColumnOrder.Count);
            Assert.AreEqual(0, settings.OverviewRecentAchievementColumnAlignments.Count);
            Assert.AreEqual(0, settings.OverviewSelectedGameAchievementColumnVisibility.Count);
            Assert.AreEqual(0, settings.OverviewSelectedGameAchievementColumnWidths.Count);
            Assert.AreEqual(0, settings.OverviewSelectedGameAchievementColumnOrder.Count);
            Assert.AreEqual(0, settings.OverviewSelectedGameAchievementColumnAlignments.Count);
            Assert.AreEqual(0, settings.SingleGameColumnVisibility.Count);
            Assert.AreEqual(0, settings.SingleGameColumnWidths.Count);
            Assert.AreEqual(0, settings.SingleGameColumnOrder.Count);
            Assert.AreEqual(0, settings.SingleGameColumnAlignments.Count);
            Assert.AreEqual(0, settings.DesktopThemeColumnWidths.Count);
            Assert.AreEqual(0, settings.DesktopThemeColumnOrder.Count);
            Assert.AreEqual(0, settings.DesktopThemeColumnAlignments.Count);
            Assert.AreEqual(0, settings.OverviewGameSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.OverviewGameSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.OverviewGameSummariesColumnOrder.Count);
            // Progress column defaults to Right alignment (seeded in the ctor) so the footer keeps its
            // legacy layout now that it responds to alignment.
            Assert.AreEqual(1, settings.OverviewGameSummariesColumnAlignments.Count);
            Assert.AreEqual(GridAlignment.Right, settings.OverviewGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);
            Assert.AreEqual(0, settings.StartPageAchievementColumnVisibility.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnWidths.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnOrder.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnAlignments.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnOrder.Count);
            Assert.AreEqual(1, settings.StartPageGameSummariesColumnAlignments.Count);
            Assert.AreEqual(GridAlignment.Right, settings.StartPageGameSummariesColumnAlignments[PersistedSettings.ProgressColumnKey]);

            Assert.AreEqual("secret", settings.ProviderSettings["Steam"]["ApiKey"].Value<string>());
            Assert.IsFalse(settings.EnablePeriodicUpdates);
            Assert.IsFalse(settings.IncludeHiddenGamesInBulkScans);
            Assert.AreEqual(48, settings.PeriodicUpdateHours);
            Assert.AreEqual(7, settings.RecentRefreshGamesCount);
            Assert.IsTrue(settings.FirstTimeSetupCompleted);
            Assert.IsTrue(settings.SeenThemeMigration);
            Assert.AreEqual("2.0", settings.ThemeMigrationVersionCache["theme"].MigratedThemeVersion);
            Assert.IsTrue(settings.ExcludedGameIds.Contains(excludedGameId));
            Assert.IsTrue(settings.ExcludedFromSummariesGameIds.Contains(gameId));
            Assert.AreEqual("capstone", settings.ManualCapstones[gameId]);
            CollectionAssert.AreEqual(
                new List<string> { "first", "second" },
                settings.AchievementOrderOverrides[gameId]);
            Assert.AreEqual("DLC", settings.AchievementCategoryOverrides[gameId]["first"]);
            Assert.AreEqual("Base", settings.AchievementCategoryTypeOverrides[gameId]["first"]);
            Assert.AreEqual(900d, settings.WindowPlacements["overview"].Width);
            Assert.IsTrue(settings.WindowPlacements["overview"].IsMaximized);
            Assert.IsTrue(settings.TaggingSettings.EnableTagging);
            Assert.IsTrue(settings.TaggingSettings.SetCompletionStatus);
            Assert.AreEqual(statusId, settings.TaggingSettings.CompletionStatusId);
            Assert.AreEqual("Done", settings.TaggingSettings.CompletedConfig.DisplayName);
            Assert.IsFalse(settings.TaggingSettings.CompletedConfig.IsEnabled);
        }

        private static void AssertInGamePollingAndToastSettings(PersistedSettings expected, PersistedSettings actual)
        {
            Assert.AreEqual(expected.EnableInGamePolling, actual.EnableInGamePolling);
            Assert.AreEqual(expected.InGamePollIntervalSeconds, actual.InGamePollIntervalSeconds);
            Assert.AreEqual(expected.InGamePollRefreshFriends, actual.InGamePollRefreshFriends);
            Assert.AreEqual(expected.InGameFriendRefreshMultiplier, actual.InGameFriendRefreshMultiplier);
            Assert.AreEqual(expected.InGameFriendBatchSize, actual.InGameFriendBatchSize);
            Assert.AreEqual(expected.EnableUnlockToasts, actual.EnableUnlockToasts);
            Assert.AreEqual(expected.EnableFriendUnlockToasts, actual.EnableFriendUnlockToasts);
            Assert.AreEqual(expected.ToastShowRarityGlow, actual.ToastShowRarityGlow);
            Assert.AreEqual(expected.ToastRarityColoredName, actual.ToastRarityColoredName);
            Assert.AreEqual(expected.ToastShowRarityPercent, actual.ToastShowRarityPercent);
            Assert.AreEqual(expected.ToastShowDescription, actual.ToastShowDescription);
            Assert.AreEqual(expected.ToastShowCategory, actual.ToastShowCategory);
            Assert.AreEqual(expected.ToastShowGameName, actual.ToastShowGameName);
            Assert.AreEqual(expected.ToastDurationSeconds, actual.ToastDurationSeconds);
            Assert.AreEqual(expected.MaxConcurrentToasts, actual.MaxConcurrentToasts);
            Assert.AreEqual(expected.ToastPosition, actual.ToastPosition);
        }
    }
}
