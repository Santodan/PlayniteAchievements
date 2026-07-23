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
        public void Constructor_DefaultsOverviewColumnRatio()
        {
            var settings = new PersistedSettings();

            Assert.AreEqual(
                PersistedSettings.DefaultOverviewLeftColumnRatio,
                settings.OverviewLeftColumnRatio);
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
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveOverviewColumnRatio()
        {
            var source = new PersistedSettings
            {
                OverviewLeftColumnRatio = 0.64d
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(0.64d, clone.OverviewLeftColumnRatio);
            Assert.AreEqual(0.64d, target.OverviewLeftColumnRatio);
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
        public void CloneAndCopyFrom_PreserveManualSortSourceOrderSelections()
        {
            var source = new PersistedSettings
            {
                GamesOverviewCustomSortUsesSourceOrder = true,
                RecentAchievementsCustomSortUsesSourceOrder = true,
                SidebarAllAchievementsCustomSortUsesSourceOrder = true,
                SidebarSelectedGameCustomSortUsesSourceOrder = true
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsTrue(clone.GamesOverviewCustomSortUsesSourceOrder);
            Assert.IsTrue(clone.RecentAchievementsCustomSortUsesSourceOrder);
            Assert.IsTrue(clone.SidebarAllAchievementsCustomSortUsesSourceOrder);
            Assert.IsTrue(clone.SidebarSelectedGameCustomSortUsesSourceOrder);
            Assert.IsTrue(target.GamesOverviewCustomSortUsesSourceOrder);
            Assert.IsTrue(target.RecentAchievementsCustomSortUsesSourceOrder);
            Assert.IsTrue(target.SidebarAllAchievementsCustomSortUsesSourceOrder);
            Assert.IsTrue(target.SidebarSelectedGameCustomSortUsesSourceOrder);
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
                OverviewHotkey = "F10"
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.IsFalse(clone.EnableAchievementHotkeys);
            Assert.IsTrue(clone.EnableGlobalAchievementHotkeys);
            Assert.AreEqual("F8", clone.ViewAchievementsHotkey);
            Assert.AreEqual("Shift+F9", clone.ManageAchievementsHotkey);
            Assert.AreEqual("F10", clone.OverviewHotkey);

            Assert.IsFalse(target.EnableAchievementHotkeys);
            Assert.IsTrue(target.EnableGlobalAchievementHotkeys);
            Assert.AreEqual("F8", target.ViewAchievementsHotkey);
            Assert.AreEqual("Shift+F9", target.ManageAchievementsHotkey);
            Assert.AreEqual("F10", target.OverviewHotkey);
        }

        [TestMethod]
        public void CloneAndCopyFrom_PreserveColumnHeaderVisibilityAndColumnOrder()
        {
            var source = new PersistedSettings
            {
                ShowOverviewGameSummariesGridColumnHeaders = false,
                ShowOverviewRecentAchievementsGridColumnHeaders = false,
                ShowDesktopThemeAchievementGridColumnHeaders = false,
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
            Assert.IsFalse(target.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.IsFalse(target.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.IsFalse(target.ShowDesktopThemeAchievementGridColumnHeaders);
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
                SingleGameGridMaxRows = 2,
                OverviewGameSummariesGridMaxRows = 3,
                OverviewRecentAchievementsGridMaxRows = 4,
                OverviewSelectedGameGridMaxRows = 5,
                StartPageGameSummariesGridMaxRows = 6,
                StartPageRecentAchievementsGridMaxRows = 7,
                DesktopThemeAchievementGridMaxRows = 8
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
            Assert.AreEqual(2, clone.SingleGameGridMaxRows);
            Assert.AreEqual(3, clone.OverviewGameSummariesGridMaxRows);
            Assert.AreEqual(4, clone.OverviewRecentAchievementsGridMaxRows);
            Assert.AreEqual(5, clone.OverviewSelectedGameGridMaxRows);
            Assert.AreEqual(6, clone.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(7, clone.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(8, clone.DesktopThemeAchievementGridMaxRows);

            Assert.AreEqual(clone.SingleGameGridRowHeight, target.SingleGameGridRowHeight);
            Assert.AreEqual(clone.OverviewGameSummariesGridRowHeight, target.OverviewGameSummariesGridRowHeight);
            Assert.AreEqual(clone.OverviewRecentAchievementsGridRowHeight, target.OverviewRecentAchievementsGridRowHeight);
            Assert.AreEqual(clone.OverviewSelectedGameGridRowHeight, target.OverviewSelectedGameGridRowHeight);
            Assert.AreEqual(clone.StartPageGameSummariesGridRowHeight, target.StartPageGameSummariesGridRowHeight);
            Assert.AreEqual(clone.StartPageRecentAchievementsGridRowHeight, target.StartPageRecentAchievementsGridRowHeight);
            Assert.AreEqual(clone.DesktopThemeAchievementGridRowHeight, target.DesktopThemeAchievementGridRowHeight);
            Assert.AreEqual(clone.SingleGameGridMaxRows, target.SingleGameGridMaxRows);
            Assert.AreEqual(clone.OverviewGameSummariesGridMaxRows, target.OverviewGameSummariesGridMaxRows);
            Assert.AreEqual(clone.OverviewRecentAchievementsGridMaxRows, target.OverviewRecentAchievementsGridMaxRows);
            Assert.AreEqual(clone.OverviewSelectedGameGridMaxRows, target.OverviewSelectedGameGridMaxRows);
            Assert.AreEqual(clone.StartPageGameSummariesGridMaxRows, target.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(clone.StartPageRecentAchievementsGridMaxRows, target.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(clone.DesktopThemeAchievementGridMaxRows, target.DesktopThemeAchievementGridMaxRows);
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
                ShowRarityGlow = false,
                UseUniformRarityBadges = true,
                UseCoverImages = false,
                ShowOverviewCollectionScoreCard = false,
                ShowOverviewPrestigeScoreCard = false,
                ShowOverviewPieCharts = false,
                ShowOverviewBarCharts = false,
                ShowOverviewGameMetadata = false,
                ShowTopMenuBarButton = false,
                ShowCompactListRarityBar = false,
                ShowCompletionBorder = false,
                ShowOverviewGameSummariesGridColumnHeaders = false,
                ShowOverviewRecentAchievementsGridColumnHeaders = false,
                ShowDesktopThemeAchievementGridColumnHeaders = false,
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
                ViewAchievementsTimelineRange = TimelineRange.All,
                ViewAchievementsTimelineVisible = true
            };

            settings.TaggingSettings.CompletedConfig.DisplayName = "Done";
            settings.TaggingSettings.CompletedConfig.IsEnabled = false;
            settings.StartPageGameSummariesGrid.ShowColumnHeaders = false;
            settings.StartPageGameSummariesGrid.RowHeight = 70d;
            settings.StartPageGameSummariesGrid.MaxRows = 3;
            settings.StartPageRecentUnlocksGrid.ShowColumnHeaders = false;
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
            Assert.AreEqual(defaults.ShowRarityGlow, settings.ShowRarityGlow);
            Assert.AreEqual(defaults.UseUniformRarityBadges, settings.UseUniformRarityBadges);
            Assert.AreEqual(defaults.UseCoverImages, settings.UseCoverImages);
            Assert.AreEqual(defaults.ShowOverviewCollectionScoreCard, settings.ShowOverviewCollectionScoreCard);
            Assert.AreEqual(defaults.ShowOverviewPrestigeScoreCard, settings.ShowOverviewPrestigeScoreCard);
            Assert.AreEqual(defaults.ShowOverviewPieCharts, settings.ShowOverviewPieCharts);
            Assert.AreEqual(defaults.ShowOverviewGamesPieChart, settings.ShowOverviewGamesPieChart);
            Assert.AreEqual(defaults.ShowOverviewProviderPieChart, settings.ShowOverviewProviderPieChart);
            Assert.AreEqual(defaults.ShowOverviewRarityPieChart, settings.ShowOverviewRarityPieChart);
            Assert.AreEqual(defaults.ShowOverviewTrophyPieChart, settings.ShowOverviewTrophyPieChart);
            Assert.AreEqual(defaults.ShowOverviewBarCharts, settings.ShowOverviewBarCharts);
            Assert.AreEqual(defaults.ShowOverviewGameMetadata, settings.ShowOverviewGameMetadata);
            Assert.AreEqual(defaults.ShowTopMenuBarButton, settings.ShowTopMenuBarButton);
            Assert.AreEqual(defaults.ShowCompactListRarityBar, settings.ShowCompactListRarityBar);
            Assert.AreEqual(defaults.ShowCompletionBorder, settings.ShowCompletionBorder);
            Assert.AreEqual(defaults.ShowOverviewGameSummariesGridColumnHeaders, settings.ShowOverviewGameSummariesGridColumnHeaders);
            Assert.AreEqual(defaults.ShowOverviewRecentAchievementsGridColumnHeaders, settings.ShowOverviewRecentAchievementsGridColumnHeaders);
            Assert.AreEqual(defaults.ShowDesktopThemeAchievementGridColumnHeaders, settings.ShowDesktopThemeAchievementGridColumnHeaders);
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
            Assert.AreEqual(defaults.StartPageGameSummariesGridRowHeight, settings.StartPageGameSummariesGridRowHeight);
            Assert.AreEqual(defaults.StartPageGameSummariesGridMaxRows, settings.StartPageGameSummariesGridMaxRows);
            Assert.AreEqual(defaults.StartPageRecentUnlocksGrid.ShowColumnHeaders, settings.StartPageRecentUnlocksGrid.ShowColumnHeaders);
            Assert.AreEqual(defaults.StartPageRecentAchievementsGridRowHeight, settings.StartPageRecentAchievementsGridRowHeight);
            Assert.AreEqual(defaults.StartPageRecentAchievementsGridMaxRows, settings.StartPageRecentAchievementsGridMaxRows);
            Assert.AreEqual(defaults.StartPagePieCharts.ShowCenterPercentage, settings.StartPagePieCharts.ShowCenterPercentage);
            Assert.AreEqual(defaults.StartPagePieCharts.SmallSliceMode, settings.StartPagePieCharts.SmallSliceMode);
            Assert.AreEqual(defaults.StartPageActivityScope, settings.StartPageActivityScope);
            Assert.AreEqual(defaults.StartPageProgressScope, settings.StartPageProgressScope);
            Assert.AreEqual(defaults.OverviewLeftColumnRatio, settings.OverviewLeftColumnRatio);
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
            Assert.AreEqual(0, settings.OverviewGameSummariesColumnAlignments.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnVisibility.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnWidths.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnOrder.Count);
            Assert.AreEqual(0, settings.StartPageAchievementColumnAlignments.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnVisibility.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnWidths.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnOrder.Count);
            Assert.AreEqual(0, settings.StartPageGameSummariesColumnAlignments.Count);

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

        [TestMethod]
        public void CloneAndCopyFrom_PreserveLastAllGamesScoreSnapshot()
        {
            var source = new PersistedSettings
            {
                LastAllGamesCollectorScore = 1234,
                LastAllGamesCollectorLevel = 7,
                LastAllGamesCollectorLevelProgress = 42.5,
                LastAllGamesCollectorRank = "Silver1",
                LastAllGamesPrestigeScore = 5678,
                LastAllGamesPrestigeLevel = 11,
                LastAllGamesPrestigeLevelProgress = 66.25,
                LastAllGamesPrestigeRank = "Gold2"
            };

            var clone = source.Clone();
            var target = new PersistedSettings();
            target.CopyFrom(source);

            Assert.AreEqual(1234, clone.LastAllGamesCollectorScore);
            Assert.AreEqual(7, clone.LastAllGamesCollectorLevel);
            Assert.AreEqual(42.5, clone.LastAllGamesCollectorLevelProgress);
            Assert.AreEqual("Silver1", clone.LastAllGamesCollectorRank);
            Assert.AreEqual(5678, clone.LastAllGamesPrestigeScore);
            Assert.AreEqual(11, clone.LastAllGamesPrestigeLevel);
            Assert.AreEqual(66.25, clone.LastAllGamesPrestigeLevelProgress);
            Assert.AreEqual("Gold2", clone.LastAllGamesPrestigeRank);

            Assert.AreEqual(1234, target.LastAllGamesCollectorScore);
            Assert.AreEqual(7, target.LastAllGamesCollectorLevel);
            Assert.AreEqual(42.5, target.LastAllGamesCollectorLevelProgress);
            Assert.AreEqual("Silver1", target.LastAllGamesCollectorRank);
            Assert.AreEqual(5678, target.LastAllGamesPrestigeScore);
            Assert.AreEqual(11, target.LastAllGamesPrestigeLevel);
            Assert.AreEqual(66.25, target.LastAllGamesPrestigeLevelProgress);
            Assert.AreEqual("Gold2", target.LastAllGamesPrestigeRank);
        }
    }
}
