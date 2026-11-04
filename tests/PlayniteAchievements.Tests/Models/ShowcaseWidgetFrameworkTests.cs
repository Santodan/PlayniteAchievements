using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements.Scoring;
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

            Assert.AreEqual(TimeWindow.FromPreset(expected), ShowcaseTimelineOptions.GetWindow(instance));
        }

        [TestMethod]
        public void TimelineRange_PersistsEstablishedRangeAndRemovesLegacyOption()
        {
            var instance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.Timeline
            };
            instance.SetOption("RangeDays", 30);

            ShowcaseTimelineOptions.SetWindow(instance, TimeWindow.FromPreset(TimelineRange.OneYear));

            Assert.AreEqual(TimeWindow.FromPreset(TimelineRange.OneYear), ShowcaseTimelineOptions.GetWindow(instance));
            Assert.IsFalse(instance.Options.ContainsKey("RangeDays"));
        }

        [DataTestMethod]
        [DataRow(true, PieCenterMode.Percentage)]
        [DataRow(false, PieCenterMode.Empty)]
        public void PieCenterMode_ReadsLegacyShowCenterPercentage(bool showPercentage, PieCenterMode expected)
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Pie };
            instance.SetOption("ShowCenterPercentage", showPercentage);

            Assert.AreEqual(expected, ShowcaseWidgetOptions.GetPieCenterMode(instance));
        }

        [TestMethod]
        public void PieCenterMode_PersistsAndRemovesLegacyOption()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Pie };
            instance.SetOption("ShowCenterPercentage", false);

            ShowcaseWidgetOptions.SetPieCenterMode(instance, PieCenterMode.Filled);

            Assert.AreEqual(PieCenterMode.Filled, ShowcaseWidgetOptions.GetPieCenterMode(instance));
            Assert.IsFalse(instance.Options.ContainsKey("ShowCenterPercentage"));
        }

        [TestMethod]
        public void CollectionType_ReadsAFormerPinnedSourceAsItsCollection()
        {
            var grid = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            grid.Options["Source"] = "Pinned";
            var games = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            games.Options["Source"] = "Pinned";
            var gameMosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(gameMosaic, ShowcaseMosaicContent.Games);
            gameMosaic.Options["Source"] = "Pinned";

            Assert.AreEqual(ShowcaseCollectionType.Achievements, ShowcaseWidgetOptions.GetCollectionType(grid));
            Assert.AreEqual(ShowcaseAchievementGridSource.All, ShowcaseWidgetOptions.GetAchievementGridSource(grid));
            Assert.AreEqual(ShowcaseCollectionType.Games, ShowcaseWidgetOptions.GetCollectionType(games));
            Assert.AreEqual(ShowcaseGameGridSource.Library, ShowcaseWidgetOptions.GetGameGridSource(games));
            Assert.AreEqual(ShowcaseCollectionType.Games, ShowcaseWidgetOptions.GetCollectionType(gameMosaic));
            Assert.AreEqual(ShowcaseGameMosaicSource.All, ShowcaseWidgetOptions.GetGameMosaicSource(gameMosaic));
        }

        [TestMethod]
        public void CollectionType_ASourceChangeKeepsAFormerPinnedSourcesCollection()
        {
            var grid = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            grid.Options["Source"] = "Pinned";

            ShowcaseWidgetOptions.SetAchievementGridSource(grid, ShowcaseAchievementGridSource.UnlockNext);

            Assert.AreEqual(ShowcaseCollectionType.Achievements, ShowcaseWidgetOptions.GetCollectionType(grid));
            Assert.AreEqual(ShowcaseAchievementGridSource.UnlockNext, ShowcaseWidgetOptions.GetAchievementGridSource(grid));
        }

        [TestMethod]
        public void CollectionType_AchievementCollectionsOnlyApplyToAchievementWidgets()
        {
            var games = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            ShowcaseWidgetOptions.SetCollectionType(games, ShowcaseCollectionType.Achievements);
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetCollectionType(mosaic, ShowcaseCollectionType.Achievements);
            var pie = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Pie };
            ShowcaseWidgetOptions.SetCollectionType(pie, ShowcaseCollectionType.Games);
            var text = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Text };
            ShowcaseWidgetOptions.SetCollectionType(text, ShowcaseCollectionType.Games);

            Assert.AreEqual(ShowcaseCollectionType.None, ShowcaseWidgetOptions.GetCollectionType(games));
            Assert.AreEqual(ShowcaseCollectionType.Achievements, ShowcaseWidgetOptions.GetCollectionType(mosaic));
            ShowcaseWidgetOptions.SetMosaicContent(mosaic, ShowcaseMosaicContent.Games);
            Assert.AreEqual(ShowcaseCollectionType.None, ShowcaseWidgetOptions.GetCollectionType(mosaic));
            Assert.AreEqual(ShowcaseCollectionType.Games, ShowcaseWidgetOptions.GetCollectionType(pie));
            Assert.AreEqual(ShowcaseCollectionType.None, ShowcaseWidgetOptions.GetCollectionType(text));
        }

        [TestMethod]
        public void ShowControlBar_IsOffByDefault_AndHidesEveryItem()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Pie };
            ShowcaseWidgetOptions.SetControlBarItemVisible(instance, ShowcaseControlBarItems.Platform, true);

            Assert.IsFalse(ShowcaseWidgetOptions.GetShowControlBar(instance));
            Assert.IsFalse(ShowcaseWidgetOptions.GetControlBarItemVisible(instance, ShowcaseControlBarItems.Platform));
            Assert.IsTrue(ShowcaseWidgetOptions.GetControlBarItemChecked(instance, ShowcaseControlBarItems.Platform));
        }

        [TestMethod]
        public void ShowControlBar_On_ShowsEveryItemNotTurnedOff()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            instance.SetOption("ShowControlBar", true);

            Assert.IsTrue(ShowcaseWidgetOptions.GetControlBarItemVisible(instance, ShowcaseControlBarItems.Search));

            ShowcaseWidgetOptions.SetControlBarItemVisible(instance, ShowcaseControlBarItems.Search, false);

            Assert.IsFalse(ShowcaseWidgetOptions.GetControlBarItemVisible(instance, ShowcaseControlBarItems.Search));
            Assert.IsTrue(ShowcaseWidgetOptions.GetControlBarItemVisible(instance, ShowcaseControlBarItems.Rarity));
        }

        [TestMethod]
        public void ShowControlBar_FallsBackToTheGridSurface_UntilTheWidgetHasItsOwn()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };

            Assert.IsTrue(ShowcaseWidgetOptions.GetControlBarItemVisible(
                instance,
                ShowcaseControlBarItems.Search,
                gridSurfaceVisible: true));
            Assert.IsFalse(ShowcaseWidgetOptions.GetControlBarItemVisible(
                instance,
                ShowcaseControlBarItems.Search,
                gridSurfaceVisible: false));

            ShowcaseWidgetOptions.SetShowControlBar(instance, false);
            Assert.IsFalse(ShowcaseWidgetOptions.GetShowControlBar(instance, gridSurfaceVisible: true));
        }

        [TestMethod]
        public void ShowOnlyControlBarItems_TurnsTheBarOnWithJustThoseItems()
        {
            var timeline = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };

            ShowcaseWidgetOptions.ShowOnlyControlBarItems(
                timeline,
                ShowcaseControlBarItems.TimelineItems,
                ShowcaseControlBarItems.TimeWindow);

            Assert.IsTrue(ShowcaseWidgetOptions.GetControlBarItemVisible(timeline, ShowcaseControlBarItems.TimeWindow));
            Assert.IsFalse(ShowcaseWidgetOptions.GetControlBarItemVisible(timeline, ShowcaseControlBarItems.Platform));
            Assert.IsFalse(ShowcaseWidgetOptions.GetControlBarItemVisible(timeline, ShowcaseControlBarItems.SplitByPlatform));
        }

        [TestMethod]
        public void MergeEditedOptions_KeepsTheLiveControlBarState()
        {
            var live = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            live.Options["ControlBar.Achievements"] = "{\"SearchText\":\"new\"}";
            var edited = live.Clone();
            edited.Options["ControlBar.Achievements"] = "{\"SearchText\":\"old\"}";
            edited.Options["ControlBar.Games"] = "{}";
            ShowcaseWidgetOptions.SetControlBarItemVisible(edited, ShowcaseControlBarItems.Search, true);

            var merged = ShowcaseWidgetOptions.MergeEditedOptions(live, edited);

            Assert.AreEqual("{\"SearchText\":\"new\"}", merged["ControlBar.Achievements"]);
            Assert.IsFalse(merged.ContainsKey("ControlBar.Games"));
            Assert.AreEqual("True", merged["ShowControlBarItem.Search"]);
        }

        [TestMethod]
        public void PieCenterMode_DefaultsToPercentage()
        {
            Assert.AreEqual(
                PieCenterMode.Percentage,
                ShowcaseWidgetOptions.GetPieCenterMode(new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Pie }));
        }

        [TestMethod]
        public void TimeWindow_ReadsLegacyPresetNameAndRoundTripsCustomRanges()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            instance.Options["TimelineRange"] = "OneYear";
            Assert.AreEqual(TimeWindow.FromPreset(TimelineRange.OneYear), ShowcaseTimelineOptions.GetWindow(instance));

            var custom = TimeWindow.Custom(new System.DateTime(2024, 1, 1), null);
            instance.SetOption("RangeDays", 30);
            ShowcaseTimelineOptions.SetWindow(instance, custom);

            Assert.AreEqual(custom, ShowcaseTimelineOptions.GetWindow(instance));
            Assert.AreEqual("Custom:2024-01-01..", instance.Options["TimelineRange"]);
            Assert.IsFalse(instance.Options.ContainsKey("RangeDays"));
        }

        [TestMethod]
        public void TimelineGranularity_DefaultsToAutoAndPersists()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            Assert.AreEqual(TimelineGranularity.Auto, ShowcaseTimelineOptions.GetGranularity(instance));

            ShowcaseTimelineOptions.SetGranularity(instance, TimelineGranularity.Week);

            Assert.AreEqual(TimelineGranularity.Week, ShowcaseTimelineOptions.GetGranularity(instance));
        }

        [TestMethod]
        public void LastPlayedTimeWindow_DefaultsToOneMonthAndAcceptsCustomRanges()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            Assert.AreEqual(TimeWindow.FromPreset(TimelineRange.OneMonth), ShowcaseWidgetOptions.GetLastPlayedTimeWindow(instance));

            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(instance, TimeWindow.FromPreset(TimelineRange.All));
            Assert.AreEqual(TimeWindow.All, ShowcaseWidgetOptions.GetLastPlayedTimeWindow(instance));

            var custom = TimeWindow.Custom(new System.DateTime(2025, 3, 1), new System.DateTime(2025, 3, 31));
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(instance, custom);
            Assert.AreEqual(custom, ShowcaseWidgetOptions.GetLastPlayedTimeWindow(instance));
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

            Assert.AreEqual(ScoreCardType.Collection, ShowcaseWidgetOptions.GetScoreCardType(instance));
            // A layout saved before the option existed, or with a junk value, keeps its chart.
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreHistoryShown(new ShowcaseWidgetInstanceSettings()));
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreHistoryShown(instance));
            instance.SetOption("ScoreCard", 999);
            Assert.AreEqual(ScoreCardType.Collection, ShowcaseWidgetOptions.GetScoreCardType(instance));
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
            Assert.AreEqual(ShowcaseGameMosaicSource.All, ShowcaseWidgetOptions.GetGameMosaicSource(instance));

            instance.SetOption("Count", 0);
            Assert.AreEqual(1, ShowcaseWidgetOptions.GetGameMosaicCount(instance));
        }

        [TestMethod]
        public void GameMosaicShowCompletionFrame_DefaultsOffAndRoundTrips()
        {
            Assert.IsFalse(ShowcaseWidgetOptions.GetGameMosaicShowCompletionFrame(null));

            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            Assert.IsFalse(ShowcaseWidgetOptions.GetGameMosaicShowCompletionFrame(instance));

            ShowcaseWidgetOptions.SetGameMosaicShowCompletionFrame(instance, true);

            Assert.IsTrue(ShowcaseWidgetOptions.GetGameMosaicShowCompletionFrame(instance));
            // Its own key: the completion glow keeps its default.
            Assert.IsTrue(ShowcaseWidgetOptions.GetGameMosaicShowCompletionGlow(instance));
        }

        [TestMethod]
        public void MigrateCompletedGameMosaic_MovesTheCompletedSourceToAProgressFilter()
        {
            var completed = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(completed, ShowcaseMosaicContent.Games);
            completed.Options["Source"] = "Completed";
            completed.Options["ControlBar.Games"] = "{\"SearchText\":\"zelda\",\"Progress\":[1]}";
            var unset = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(unset, ShowcaseMosaicContent.Games);
            var favorites = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(favorites, ShowcaseMosaicContent.Games);
            ShowcaseWidgetOptions.SetGameMosaicSource(favorites, ShowcaseGameMosaicSource.PlayniteFavorites);
            var achievements = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };

            Assert.IsTrue(ShowcaseWidgetOptions.MigrateCompletedGameMosaic(completed));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateCompletedGameMosaic(unset));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateCompletedGameMosaic(favorites));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateCompletedGameMosaic(achievements));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateCompletedGameMosaic(completed), "runs once");

            Assert.AreEqual(ShowcaseGameMosaicSource.All, ShowcaseWidgetOptions.GetGameMosaicSource(completed));
            var state = Newtonsoft.Json.Linq.JObject.Parse(completed.Options["ControlBar.Games"]);
            Assert.AreEqual("zelda", (string)state["SearchText"]);
            CollectionAssert.AreEqual(new[] { 0 }, state["Progress"].ToObject<int[]>());
            StringAssert.Contains(unset.Options["ControlBar.Games"], "\"Progress\":[0]");
        }

        [TestMethod]
        public void MigrateAchievementMosaicSource_TurnsFormerSourcesIntoSortAndFilter()
        {
            ShowcaseWidgetInstanceSettings Mosaic(string source)
            {
                var widget = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
                widget.Options["Source"] = source;
                return widget;
            }

            var recent = Mosaic("Recent");
            var rarest = Mosaic("Rarest");
            var sortedRarest = Mosaic("Rarest");
            ShowcaseWidgetOptions.SetMosaicSort(sortedRarest, CompactListSortMode.UnlockTime);
            var capstones = Mosaic("Capstones");
            capstones.Options["ControlBar.Achievements"] = "{\"SearchText\":\"boss\"}";
            var unlockNext = Mosaic("UnlockNext");
            var games = Mosaic("Recent");
            ShowcaseWidgetOptions.SetMosaicContent(games, ShowcaseMosaicContent.Games);

            Assert.IsTrue(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(recent));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(rarest));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(sortedRarest));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(capstones));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(unlockNext));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(games), "a game mosaic's source is its own");
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateAchievementMosaicSource(rarest), "runs once");

            Assert.AreEqual("All", recent.Options["Source"]);
            Assert.AreEqual(CompactListSortMode.Rarity, ShowcaseWidgetOptions.GetMosaicSort(rarest));
            Assert.IsFalse(ShowcaseWidgetOptions.GetMosaicSortDescending(rarest), "rarest first");
            Assert.AreEqual(CompactListSortMode.UnlockTime, ShowcaseWidgetOptions.GetMosaicSort(sortedRarest), "a chosen sort stays");
            var state = Newtonsoft.Json.Linq.JObject.Parse(capstones.Options["ControlBar.Achievements"]);
            Assert.AreEqual("boss", (string)state["SearchText"]);
            Assert.IsTrue((bool)state["Capstones"]);
            Assert.AreEqual(ShowcaseMosaicSource.All, ShowcaseWidgetOptions.GetMosaicSource(capstones));
        }

        [TestMethod]
        public void MigrateGameGridScope_MovesHideCompletedAndActivityIntoTheBar()
        {
            var library = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            library.Options["HideCompleted"] = "True";
            library.Options["ActivityScope"] = "Played";
            var narrowed = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            narrowed.Options["HideCompleted"] = "True";
            narrowed.Options["ControlBar.Games"] = "{\"SearchText\":\"zelda\",\"Progress\":[0,1]}";
            var allScope = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            allScope.Options["ActivityScope"] = "All";
            allScope.Options["HideCompleted"] = "False";
            var favorites = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            ShowcaseWidgetOptions.SetGameGridSource(favorites, ShowcaseGameGridSource.PlayniteFavorites);
            favorites.Options["HideCompleted"] = "True";
            var untouched = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };

            Assert.IsTrue(ShowcaseWidgetOptions.MigrateGameGridScope(library));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateGameGridScope(narrowed));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateGameGridScope(allScope));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateGameGridScope(favorites));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateGameGridScope(untouched));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateGameGridScope(library), "runs once");

            var state = Newtonsoft.Json.Linq.JObject.Parse(library.Options["ControlBar.Games"]);
            CollectionAssert.AreEqual(new[] { 1, 2 }, state["Progress"].ToObject<int[]>());
            CollectionAssert.AreEqual(new[] { 0 }, state["Activity"].ToObject<int[]>());
            Assert.IsFalse(library.Options.ContainsKey("HideCompleted"));
            Assert.IsFalse(library.Options.ContainsKey("ActivityScope"));

            var narrowedState = Newtonsoft.Json.Linq.JObject.Parse(narrowed.Options["ControlBar.Games"]);
            Assert.AreEqual("zelda", (string)narrowedState["SearchText"]);
            CollectionAssert.AreEqual(new[] { 1 }, narrowedState["Progress"].ToObject<int[]>(), "completed drops out of the picks");

            Assert.IsFalse(allScope.Options.ContainsKey("ControlBar.Games"), "nothing to filter");
            Assert.IsFalse(favorites.Options.ContainsKey("ControlBar.Games"), "the option did nothing on favorites");
            Assert.IsFalse(favorites.Options.ContainsKey("HideCompleted"));
        }

        [TestMethod]
        public void TimelineSplit_ReadsTheFormerPlatformSwitchAndReplacesIt()
        {
            var legacy = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            legacy.Options["TimelineSplitByPlatform"] = "True";
            var unset = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            var junk = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            junk.Options["TimelineSplit"] = "999";

            Assert.AreEqual(ShowcaseTimelineSplit.Platform, ShowcaseTimelineOptions.GetSplit(legacy));
            Assert.AreEqual(ShowcaseTimelineSplit.None, ShowcaseTimelineOptions.GetSplit(unset));
            Assert.AreEqual(ShowcaseTimelineSplit.None, ShowcaseTimelineOptions.GetSplit(junk));

            ShowcaseTimelineOptions.SetSplit(legacy, ShowcaseTimelineSplit.Trophy);
            Assert.AreEqual(ShowcaseTimelineSplit.Trophy, ShowcaseTimelineOptions.GetSplit(legacy));
            Assert.IsFalse(legacy.Options.ContainsKey("TimelineSplitByPlatform"));
        }
        [TestMethod]
        public void MigrateSlideshowVariant_MovesTheStyleIntoTheControlBarState()
        {
            var framed = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            framed.Options["Variant"] = "Framed";
            framed.Options["ControlBar.Achievements"] = "{\"SearchText\":\"boss\"}";
            var all = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            all.Options["Variant"] = "All";
            var junk = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            junk.Options["Variant"] = "999";
            var grid = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            grid.Options["Variant"] = "Framed";

            Assert.IsTrue(ShowcaseWidgetOptions.MigrateSlideshowVariant(framed));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateSlideshowVariant(all));
            Assert.IsTrue(ShowcaseWidgetOptions.MigrateSlideshowVariant(junk));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateSlideshowVariant(grid));
            Assert.IsFalse(ShowcaseWidgetOptions.MigrateSlideshowVariant(framed), "runs once");

            Assert.IsFalse(framed.Options.ContainsKey("Variant"));
            var state = Newtonsoft.Json.Linq.JObject.Parse(framed.Options["ControlBar.Achievements"]);
            Assert.AreEqual("boss", (string)state["SearchText"]);
            Assert.AreEqual("Framed", (string)state["Variant"]);
            Assert.IsFalse(all.Options.ContainsKey("ControlBar.Achievements"), "every style is the bar's default");
            Assert.IsFalse(junk.Options.ContainsKey("ControlBar.Achievements"));
            Assert.AreEqual("Framed", grid.Options["Variant"]);
        }

        [TestMethod]
        public void CollectionType_ReadsASlideshowsFormerCollectionSource()
        {
            var games = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            games.Options["Source"] = "GameCollection";
            var achievements = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            achievements.Options["Source"] = "AchievementCollection";
            var all = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ScreenshotSlideshow };
            all.Options["Source"] = "All";

            Assert.AreEqual(ShowcaseCollectionType.Games, ShowcaseWidgetOptions.GetCollectionType(games));
            Assert.AreEqual(ShowcaseCollectionType.Achievements, ShowcaseWidgetOptions.GetCollectionType(achievements));
            Assert.AreEqual(ShowcaseCollectionType.None, ShowcaseWidgetOptions.GetCollectionType(all));

            ShowcaseWidgetOptions.SetCollectionType(all, ShowcaseCollectionType.Achievements);
            Assert.AreEqual(ShowcaseCollectionType.Achievements, ShowcaseWidgetOptions.GetCollectionType(all));
        }
        [TestMethod]
        public void WidgetFactory_SeedsDefaultsForGridMosaicAndCalendarKinds()
        {
            var summaries = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.GameSummaries);
            Assert.IsFalse(summaries.Options.ContainsKey("HideCompleted"), "progress is a control bar filter");

            var mosaic = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.IconMosaic);
            Assert.AreEqual(ShowcaseMosaicContent.Achievements, ShowcaseWidgetOptions.GetMosaicContent(mosaic));
            Assert.AreEqual(ShowcaseMosaicSource.All, ShowcaseWidgetOptions.GetMosaicSource(mosaic));
            Assert.AreEqual(ShowcaseGameMosaicSource.All, ShowcaseWidgetOptions.GetGameMosaicSource(mosaic));
            Assert.AreEqual(24, ShowcaseWidgetOptions.GetGameMosaicCount(mosaic));

            var achievementGrid = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.RecentAchievements);
            Assert.AreEqual(
                ShowcaseAchievementGridSource.All,
                ShowcaseWidgetOptions.GetAchievementGridSource(achievementGrid));
            Assert.AreEqual(
                ShowcaseGameGridSource.Library,
                ShowcaseWidgetOptions.GetGameGridSource(summaries));

            var scores = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.Scores);
            Assert.AreEqual(ScoreCardType.Collection, ShowcaseWidgetOptions.GetScoreCardType(scores));
            Assert.AreEqual(ScoreCardBadgePosition.Left, ShowcaseWidgetOptions.GetScoreCardBadgePosition(scores));
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreHistoryShown(scores));
            Assert.IsFalse(ShowcaseWidgetOptions.GetScoreCardUseCustomColors(scores));
            ShowcaseWidgetOptions.SetScoreCardUseCustomColors(scores, true);
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreCardUseCustomColors(scores));
            Assert.IsTrue(ShowcaseWidgetOptions.GetScoreCardShowTotal(scores));
            ShowcaseWidgetOptions.SetScoreCardShowTotal(scores, false);
            Assert.IsFalse(ShowcaseWidgetOptions.GetScoreCardShowTotal(scores));
            Assert.IsFalse(ShowcaseWidgetOptions.IsLegacyDualScores(scores));
            Assert.AreEqual(TimeWindow.FromPreset(TimelineRange.ThreeMonths), ShowcaseTimelineOptions.GetWindow(scores));

            var calendar = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.ActivityCalendar);
            Assert.AreEqual(TimeWindow.All, ShowcaseTimelineOptions.GetWindow(calendar));
        }

        [TestMethod]
        public void WidgetCatalog_ParksOnlyNativePoints()
        {
            var achievements = ShowcaseWidgetCatalog.Get(ShowcaseWidgetKind.RecentAchievements);

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
        public void GridSurfaces_AchievementWidgetsOwnTheirCategoryGridRecord()
        {
            var catalog = new GridOptionsCatalog();
            var source = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements, InstanceId = "a" };
            var copy = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements, InstanceId = "b" };

            var categoryKey = ShowcaseGridSurfaces.ResolveCategorySurface("ShowcaseRecentAchievements:a");
            Assert.AreEqual("ShowcaseAchievementCategories:a", categoryKey);
            Assert.IsNull(ShowcaseGridSurfaces.ResolveCategorySurface("ShowcaseGameSummaries:a"));
            Assert.AreEqual(categoryKey, GridOptionsCatalog.ResolveCategorySummariesId(categoryKey), "not the View Achievements record");

            catalog.GetCategorySummaries(categoryKey).UseCoverImages = true;
            ShowcaseGridSurfaces.CopySurface(catalog, source, copy);
            Assert.IsTrue(catalog.GetCategorySummaries("ShowcaseAchievementCategories:b").UseCoverImages);

            ShowcaseGridSurfaces.PruneOrphaned(catalog, new ShowcaseSettings
            {
                WidgetInstances = new System.Collections.Generic.List<ShowcaseWidgetInstanceSettings> { copy }
            });
            Assert.IsFalse(catalog.CategorySummaries.ContainsKey(categoryKey));
            Assert.IsTrue(catalog.CategorySummaries.ContainsKey("ShowcaseAchievementCategories:b"));
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
            Assert.IsFalse(instance.Options.ContainsKey("Variant"), "the style lives in the control bar");
            Assert.AreEqual(8, ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(instance));
            Assert.AreEqual(ShowcaseImageFitMode.Fit,
                ShowcaseWidgetOptions.GetImageFitMode(instance));
            Assert.IsTrue(ShowcaseWidgetOptions.GetShuffle(instance));
            Assert.AreEqual(ShowcaseInfoPanelPosition.Off,
                ShowcaseWidgetOptions.GetInfoPanelPosition(instance));
        }

        [TestMethod]
        public void WidgetOptions_InfoPanelDefaultsOffAndRejectsJunk()
        {
            // A layout saved before the option existed shows the image alone.
            Assert.AreEqual(
                ShowcaseInfoPanelPosition.Off,
                ShowcaseWidgetOptions.GetInfoPanelPosition(new ShowcaseWidgetInstanceSettings()));

            var instance = new ShowcaseWidgetInstanceSettings();
            instance.SetOption("InfoPanel", 999);
            Assert.AreEqual(
                ShowcaseInfoPanelPosition.Off,
                ShowcaseWidgetOptions.GetInfoPanelPosition(instance));

            ShowcaseWidgetOptions.SetInfoPanelPosition(instance, ShowcaseInfoPanelPosition.Right);
            Assert.AreEqual(
                ShowcaseInfoPanelPosition.Right,
                ShowcaseWidgetOptions.GetInfoPanelPosition(instance));
        }
    }
}
