using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseWidgetFrameworkTests
    {
        [DataTestMethod]
        [DataRow(259d, 500d, WidgetViewportDensity.Compact)]
        [DataRow(800d, 159d, WidgetViewportDensity.Compact)]
        [DataRow(260d, 160d, WidgetViewportDensity.Standard)]
        [DataRow(519d, 400d, WidgetViewportDensity.Standard)]
        [DataRow(520d, 320d, WidgetViewportDensity.Expanded)]
        public void Classify_UsesContainerThresholds(
            double width,
            double height,
            WidgetViewportDensity expected)
        {
            Assert.AreEqual(expected, WidgetViewportState.Classify(width, height).Density);
        }

        [DataTestMethod]
        [DataRow(600d, 300d, WidgetViewportOrientation.Wide)]
        [DataRow(300d, 600d, WidgetViewportOrientation.Tall)]
        [DataRow(400d, 400d, WidgetViewportOrientation.Balanced)]
        public void Classify_UsesContainerOrientation(
            double width,
            double height,
            WidgetViewportOrientation expected)
        {
            Assert.AreEqual(expected, WidgetViewportState.Classify(width, height).Orientation);
        }

        [DataTestMethod]
        [DataRow(30, TimelineRange.OneMonth)]
        [DataRow(90, TimelineRange.ThreeMonths)]
        [DataRow(365, TimelineRange.OneYear)]
        [DataRow(1095, TimelineRange.All)]
        public void TimelineRange_MigratesLegacyDayOptions(
            int days,
            TimelineRange expected)
        {
            var instance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Timeline
            };
            instance.SetOption("RangeDays", days);

            Assert.AreEqual(expected, ShowcaseTimelineOptions.GetRange(instance));
        }

        [TestMethod]
        public void TimelineRange_PersistsEstablishedRangeAndRemovesLegacyOption()
        {
            var instance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Timeline
            };
            instance.SetOption("RangeDays", 30);

            ShowcaseTimelineOptions.SetRange(instance, TimelineRange.OneYear);

            Assert.AreEqual(TimelineRange.OneYear, ShowcaseTimelineOptions.GetRange(instance));
            Assert.IsFalse(instance.Options.ContainsKey("RangeDays"));
        }

        [TestMethod]
        public void WidgetOptions_RejectInvalidEnumsAndClampNumericValues()
        {
            var instance = new ShowcaseWidgetInstanceSettings();
            instance.SetOption("Mode", 999);
            instance.SetOption("TopN", 100);
            instance.SetOption("Count", -5);
            instance.SetOption("IntervalSeconds", 1000);

            instance.SetOption("ScoreHistory", 999);

            Assert.AreEqual(ShowcaseScoreMode.Dual, ShowcaseWidgetOptions.GetScoreMode(instance));
            // A layout saved before the option existed, or with a junk value, keeps both charts.
            Assert.AreEqual(
                ShowcaseScoreHistoryMode.Dual,
                ShowcaseWidgetOptions.GetScoreHistoryMode(new ShowcaseWidgetInstanceSettings()));
            Assert.AreEqual(
                ShowcaseScoreHistoryMode.Dual,
                ShowcaseWidgetOptions.GetScoreHistoryMode(instance));
            Assert.AreEqual(25, ShowcaseWidgetOptions.GetTopN(instance));
            Assert.AreEqual(1, ShowcaseWidgetOptions.GetMosaicCount(instance));
            Assert.AreEqual(300, ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(instance));
        }

        [TestMethod]
        public void WidgetOptions_ClampGameMosaicValues()
        {
            var instance = new ShowcaseWidgetInstanceSettings();
            instance.SetOption("Count", 999);
            instance.SetOption("Mode", 999);
            instance.SetOption("Source", 999);

            Assert.AreEqual(200, ShowcaseWidgetOptions.GetGameMosaicCount(instance));
            Assert.AreEqual(ShowcaseGameMosaicSource.Completed, ShowcaseWidgetOptions.GetGameMosaicSource(instance));
            Assert.IsFalse(ShowcaseWidgetOptions.GetHideCompleted(instance));

            instance.SetOption("Count", 0);
            Assert.AreEqual(1, ShowcaseWidgetOptions.GetGameMosaicCount(instance));
        }

        [TestMethod]
        public void WidgetFactory_SeedsDefaultsForGridMosaicAndCalendarKinds()
        {
            var summaries = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.GameSummaries);
            Assert.IsFalse(ShowcaseWidgetOptions.GetHideCompleted(summaries));

            var mosaic = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.IconMosaic);
            Assert.AreEqual(ShowcaseMosaicContent.Achievements, ShowcaseWidgetOptions.GetMosaicContent(mosaic));
            Assert.AreEqual(ShowcaseMosaicSource.Recent, ShowcaseWidgetOptions.GetMosaicSource(mosaic));
            Assert.AreEqual(ShowcaseGameMosaicSource.Completed, ShowcaseWidgetOptions.GetGameMosaicSource(mosaic));
            Assert.AreEqual(24, ShowcaseWidgetOptions.GetGameMosaicCount(mosaic));

            var achievementGrid = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.RecentAchievements);
            Assert.AreEqual(
                ShowcaseAchievementGridSource.All,
                ShowcaseWidgetOptions.GetAchievementGridSource(achievementGrid));
            Assert.AreEqual(
                ShowcaseGameGridSource.Library,
                ShowcaseWidgetOptions.GetGameGridSource(summaries));

            var scores = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.Scores);
            Assert.AreEqual(ShowcaseScoreMode.Dual, ShowcaseWidgetOptions.GetScoreMode(scores));
            Assert.AreEqual(
                ShowcaseScoreHistoryMode.Dual,
                ShowcaseWidgetOptions.GetScoreHistoryMode(scores));
            Assert.AreEqual(TimelineRange.ThreeMonths, ShowcaseTimelineOptions.GetRange(scores));

            var calendar = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.ActivityCalendar);
            Assert.AreEqual(TimelineRange.OneYear, ShowcaseTimelineOptions.GetRange(calendar));
            Assert.IsTrue(ShowcaseWidgetCatalog.Get(ShowcaseWidgetKind.ActivityCalendar).AllowMultipleInstances);
        }

        [TestMethod]
        public void WidgetCatalog_AllowsMultipleGridPlacementsAndParksOnlyNativePoints()
        {
            var achievements = ShowcaseWidgetCatalog.Get(ShowcaseWidgetKind.RecentAchievements);
            var games = ShowcaseWidgetCatalog.Get(ShowcaseWidgetKind.GameSummaries);

            Assert.IsTrue(achievements.AllowMultipleInstances);
            Assert.IsFalse(achievements.SingleInstancePerPage);
            Assert.IsTrue(games.AllowMultipleInstances);
            Assert.IsFalse(games.SingleInstancePerPage);
            Assert.IsFalse(achievements.Hidden);
            Assert.IsTrue(ShowcaseWidgetCatalog.Get(ShowcaseWidgetKind.NativePoints).Hidden);
            Assert.AreEqual(1, ShowcaseWidgetCatalog.Definitions.Count(definition => definition.Hidden));
        }

        [TestMethod]
        public void GridSurfaces_BuildPerInstanceKeysAndResolveToDedicatedSurfaces()
        {
            var key = ShowcaseGridSurfaces.ForInstance(ShowcaseGridSurfaces.RecentAchievements, " abc ");
            Assert.AreEqual("ShowcaseRecentAchievements:abc", key);
            Assert.AreEqual(ShowcaseGridSurfaces.RecentAchievements, ShowcaseGridSurfaces.GetBaseKey(key));
            Assert.AreEqual(
                ShowcaseGridSurfaces.GameSummaries,
                ShowcaseGridSurfaces.ForInstance(ShowcaseGridSurfaces.GameSummaries, null));

            Assert.IsTrue(ShowcaseGridSurfaces.IsAchievementSurface(key));
            Assert.IsFalse(ShowcaseGridSurfaces.IsAchievementSurface("OverviewRecentAchievements"));
            Assert.IsTrue(ShowcaseGridSurfaces.IsGameSurface("ShowcaseGameSummaries:123"));
            Assert.IsFalse(ShowcaseGridSurfaces.IsGameSurface("StartPageGameSummaries"));

            // Showcase keys are their own persisted surface ids; other keys keep their mappings.
            Assert.AreEqual(key, PlayniteAchievements.Models.Settings.GridOptionsCatalog.ResolveAchievementId(key));
            Assert.AreEqual(
                "ShowcaseGameSummaries:123",
                PlayniteAchievements.Models.Settings.GridOptionsCatalog.ResolveGameSummariesId("ShowcaseGameSummaries:123"));
            Assert.AreNotEqual(
                "SomeUnknownKey",
                PlayniteAchievements.Models.Settings.GridOptionsCatalog.ResolveAchievementId("SomeUnknownKey"));
        }

        [TestMethod]
        public void GridSurfaces_ResolveWidgetSurfaceMapsGridKindsOnly()
        {
            Assert.AreEqual(
                "ShowcaseRecentAchievements:abc",
                ShowcaseGridSurfaces.ResolveWidgetSurface(ShowcaseWidgetKind.RecentAchievements, "abc"));
            Assert.AreEqual(
                "ShowcaseGameSummaries:abc",
                ShowcaseGridSurfaces.ResolveWidgetSurface(ShowcaseWidgetKind.GameSummaries, "abc"));
            Assert.IsNull(ShowcaseGridSurfaces.ResolveWidgetSurface(ShowcaseWidgetKind.Statistics, "abc"));
        }

        [TestMethod]
        public void GridSurfaces_CatalogSeedsShowcaseDefaults()
        {
            var catalog = new PlayniteAchievements.Models.Settings.GridOptionsCatalog();

            var recent = catalog.GetAchievement("ShowcaseRecentAchievements:x");
            Assert.AreEqual(
                PlayniteAchievements.Models.Settings.GridOptionsCatalog.DefaultShowcaseRecentMaxRows,
                recent.MaxRows);
            Assert.IsFalse(recent.ShowControlBar);

            var pinned = catalog.GetAchievement("ShowcaseRecentAchievements:pinned");
            Assert.IsFalse(pinned.ShowControlBar);
            var otherPinned = catalog.GetAchievement("ShowcaseRecentAchievements:other");
            pinned.ShowControlBar = true;
            pinned.Columns.Widths["Name"] = 321d;
            Assert.IsFalse(otherPinned.ShowControlBar);
            Assert.IsFalse(otherPinned.Columns.Widths.ContainsKey("Name"));

            var summaries = catalog.GetGameSummaries("ShowcaseGameSummaries:x");
            Assert.AreEqual(
                PlayniteAchievements.Models.Settings.GridOptionsCatalog.DefaultShowcaseGameSummariesMaxRows,
                summaries.MaxRows);
            Assert.IsFalse(summaries.ShowControlBar);

            var otherSummaries = catalog.GetGameSummaries("ShowcaseGameSummaries:other");
            summaries.ShowColumnHeaders = false;
            summaries.Columns.Widths["Game"] = 456d;
            Assert.IsTrue(otherSummaries.ShowColumnHeaders);
            Assert.IsFalse(otherSummaries.Columns.Widths.ContainsKey("Game"));
        }

        [TestMethod]
        public void GridSurfaces_PruneRemovesOrphanedInstanceSurfacesOnly()
        {
            var catalog = new PlayniteAchievements.Models.Settings.GridOptionsCatalog();
            catalog.GetAchievement("ShowcaseRecentAchievements:live");
            catalog.GetAchievement("ShowcaseRecentAchievements:gone");
            catalog.GetAchievement(ShowcaseGridSurfaces.RecentAchievements);
            catalog.GetGameSummaries("ShowcaseGameSummaries:startpage");
            catalog.GetGameSummaries("ShowcaseGameSummaries:gone");
            catalog.GetGameSummaries("OverviewGameSummaries");

            var showcase = new ShowcaseSettings
            {
                WidgetInstances = new System.Collections.Generic.List<ShowcaseWidgetInstanceSettings>
                {
                    new ShowcaseWidgetInstanceSettings { InstanceId = "live" }
                }
            };
            showcase.StartPageInstances["view:one"] = new ShowcaseWidgetInstanceSettings
            {
                InstanceId = "startpage"
            };

            ShowcaseGridSurfaces.PruneOrphaned(catalog, showcase);

            Assert.IsTrue(catalog.Achievement.ContainsKey("ShowcaseRecentAchievements:live"));
            Assert.IsFalse(catalog.Achievement.ContainsKey("ShowcaseRecentAchievements:gone"));
            Assert.IsTrue(catalog.Achievement.ContainsKey(ShowcaseGridSurfaces.RecentAchievements));
            Assert.IsTrue(catalog.GameSummaries.ContainsKey("ShowcaseGameSummaries:startpage"));
            Assert.IsFalse(catalog.GameSummaries.ContainsKey("ShowcaseGameSummaries:gone"));
            Assert.IsTrue(catalog.GameSummaries.ContainsKey("OverviewGameSummaries"));
        }

        [TestMethod]
        public void GridSurfaces_SeedFromClonesDonorOnlyWhenAbsent()
        {
            var catalog = new PlayniteAchievements.Models.Settings.GridOptionsCatalog();
            var donor = catalog.GetAchievement(
                PlayniteAchievements.Models.Settings.GridOptionKeys.Achievement.StartPageRecent);
            donor.ColorNamesByRarity = true;
            donor.MaxRows = 7;
            donor.Columns.Widths["Name"] = 123d;

            catalog.SeedAchievementFrom(
                "ShowcaseRecentAchievements:abc",
                PlayniteAchievements.Models.Settings.GridOptionKeys.Achievement.StartPageRecent);
            var seeded = catalog.GetAchievement("ShowcaseRecentAchievements:abc");
            Assert.AreNotSame(donor, seeded);
            Assert.IsTrue(seeded.ColorNamesByRarity);
            Assert.AreEqual(7, seeded.MaxRows);
            Assert.AreEqual(123d, seeded.Columns.Widths["Name"]);

            // The clone is independent, and an existing surface is never overwritten.
            seeded.ColorNamesByRarity = false;
            Assert.IsTrue(donor.ColorNamesByRarity);
            catalog.SeedAchievementFrom(
                "ShowcaseRecentAchievements:abc",
                PlayniteAchievements.Models.Settings.GridOptionKeys.Achievement.StartPageRecent);
            Assert.IsFalse(catalog.GetAchievement("ShowcaseRecentAchievements:abc").ColorNamesByRarity);

            var summariesDonor = catalog.GetGameSummaries(
                PlayniteAchievements.Models.Settings.GridOptionKeys.GameSummaries.StartPage);
            summariesDonor.ShowCompletionGlow = false;
            catalog.SeedGameSummariesFrom(
                "ShowcaseGameSummaries:abc",
                PlayniteAchievements.Models.Settings.GridOptionKeys.GameSummaries.StartPage);
            var seededSummaries = catalog.GetGameSummaries("ShowcaseGameSummaries:abc");
            Assert.AreNotSame(summariesDonor, seededSummaries);
            Assert.IsFalse(seededSummaries.ShowCompletionGlow);
        }

        [TestMethod]
        public void WidgetFactory_UsesTheSharedOptionContract()
        {
            var instance = ShowcaseWidgetSettingsFactory.CreateDefault(
                ShowcaseWidgetKind.ScreenshotSlideshow,
                " slideshow ");

            Assert.AreEqual("slideshow", instance.InstanceId);
            Assert.AreEqual(ShowcaseScreenshotVariant.All,
                ShowcaseWidgetOptions.GetScreenshotVariant(instance));
            Assert.AreEqual(8, ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(instance));
            Assert.AreEqual(ShowcaseImageFitMode.Fill,
                ShowcaseWidgetOptions.GetImageFitMode(instance));
            Assert.IsTrue(ShowcaseWidgetOptions.GetShuffle(instance));
        }
    }
}
