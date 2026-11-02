using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageViewCatalogTests
    {
        [TestMethod]
        public void Views_OfferOneVisibleEntryPerVisibleShowcaseWidgetInCatalogOrder()
        {
            var visible = StartPageViewCatalog.Views.Where(view => !view.Hidden).ToList();
            var showcase = ShowcaseWidgetCatalog.Definitions.Where(definition => !definition.Hidden).ToList();

            CollectionAssert.AreEqual(
                showcase.Select(definition => (ShowcaseWidgetKind?)definition.Kind).ToArray(),
                visible.Select(view => view.ShowcaseWidgetKind).ToArray());
            for (var i = 0; i < showcase.Count; i++)
            {
                Assert.AreEqual(showcase[i].NameKey, visible[i].NameKey);
                Assert.IsTrue(visible[i].HasSettings);
            }
        }

        [TestMethod]
        public void Views_OfferedEntriesReuseExistingViewIds()
        {
            CollectionAssert.AreEqual(
                new[]
                {
                    StartPageViewCatalog.ShowcaseProfileViewId,
                    StartPageViewCatalog.ShowcaseDualScoresViewId,
                    StartPageViewCatalog.CompletedGamesPieViewId,
                    StartPageViewCatalog.ShowcaseTimelineViewId,
                    StartPageViewCatalog.ShowcaseStatisticsViewId,
                    StartPageViewCatalog.ShowcaseIconMosaicViewId,
                    StartPageViewCatalog.ShowcaseScreenshotSlideshowViewId,
                    StartPageViewCatalog.RecentUnlocksGridViewId,
                    StartPageViewCatalog.GameSummariesGridViewId,
                    StartPageViewCatalog.ShowcaseActivityCalendarViewId,
                    StartPageViewCatalog.ShowcaseImageViewId,
                    StartPageViewCatalog.ShowcaseTextViewId
                },
                StartPageViewCatalog.Views.Where(view => !view.Hidden).Select(view => view.ViewId).ToArray());
        }

        [TestMethod]
        public void Views_EveryShowcaseKindHasADedicatedStartPageKind()
        {
            foreach (var definition in ShowcaseWidgetCatalog.Definitions)
            {
                var view = StartPageViewCatalog.Views.First(candidate =>
                    candidate.ShowcaseWidgetKind == definition.Kind);
                Assert.IsTrue(view.WidgetKind.HasValue, definition.Kind.ToString());
            }
        }

        [DataTestMethod]
        [DataRow(StartPageViewCatalog.GameSummariesGridViewId, StartPageWidgetKind.GameSummariesGrid, ShowcaseWidgetKind.GameSummaries, false)]
        [DataRow(StartPageViewCatalog.RecentUnlocksGridViewId, StartPageWidgetKind.RecentUnlocksGrid, ShowcaseWidgetKind.RecentAchievements, false)]
        [DataRow(StartPageViewCatalog.CompletedGamesPieViewId, StartPageWidgetKind.CompletedGamesPie, ShowcaseWidgetKind.Pie, false)]
        [DataRow(StartPageViewCatalog.ProviderPieViewId, StartPageWidgetKind.ProviderPie, ShowcaseWidgetKind.Pie, true)]
        [DataRow(StartPageViewCatalog.RarityPieViewId, StartPageWidgetKind.RarityPie, ShowcaseWidgetKind.Pie, true)]
        [DataRow(StartPageViewCatalog.TrophyPieViewId, StartPageWidgetKind.TrophyPie, ShowcaseWidgetKind.Pie, true)]
        [DataRow(StartPageViewCatalog.CollectionScoreCardViewId, StartPageWidgetKind.CollectionScoreCard, ShowcaseWidgetKind.Scores, true)]
        [DataRow(StartPageViewCatalog.PrestigeScoreCardViewId, StartPageWidgetKind.PrestigeScoreCard, ShowcaseWidgetKind.Scores, true)]
        [DataRow(StartPageViewCatalog.ShowcaseProfileViewId, StartPageWidgetKind.ShowcaseProfile, ShowcaseWidgetKind.Profile, false)]
        [DataRow(StartPageViewCatalog.ShowcaseDualScoresViewId, StartPageWidgetKind.ShowcaseDualScores, ShowcaseWidgetKind.Scores, false)]
        [DataRow(StartPageViewCatalog.ShowcaseTimelineViewId, StartPageWidgetKind.ShowcaseTimeline, ShowcaseWidgetKind.Timeline, false)]
        [DataRow(StartPageViewCatalog.ShowcaseStatisticsViewId, StartPageWidgetKind.ShowcaseStatistics, ShowcaseWidgetKind.Statistics, false)]
        [DataRow(StartPageViewCatalog.ShowcaseNativePointsViewId, StartPageWidgetKind.ShowcaseNativePoints, ShowcaseWidgetKind.NativePoints, true)]
        [DataRow(StartPageViewCatalog.ShowcaseIconMosaicViewId, StartPageWidgetKind.ShowcaseIconMosaic, ShowcaseWidgetKind.IconMosaic, false)]
        [DataRow(StartPageViewCatalog.ShowcaseScreenshotSlideshowViewId, StartPageWidgetKind.ShowcaseScreenshotSlideshow, ShowcaseWidgetKind.ScreenshotSlideshow, false)]
        [DataRow(StartPageViewCatalog.ShowcaseActivityCalendarViewId, StartPageWidgetKind.ShowcaseActivityCalendar, ShowcaseWidgetKind.ActivityCalendar, false)]
        [DataRow(StartPageViewCatalog.ShowcaseImageViewId, StartPageWidgetKind.ShowcaseImage, ShowcaseWidgetKind.Image, false)]
        [DataRow(StartPageViewCatalog.ShowcaseTextViewId, StartPageWidgetKind.ShowcaseText, ShowcaseWidgetKind.Text, false)]
        public void TryGetDefinition_ResolvesEveryPlacedViewId(
            string viewId,
            StartPageWidgetKind widgetKind,
            ShowcaseWidgetKind showcaseKind,
            bool hidden)
        {
            Assert.IsTrue(StartPageViewCatalog.TryGetDefinition(viewId, out var definition));
            Assert.AreEqual(viewId, definition.ViewId);
            Assert.AreEqual(widgetKind, definition.WidgetKind);
            Assert.AreEqual(showcaseKind, definition.ShowcaseWidgetKind);
            Assert.AreEqual(hidden, definition.Hidden);
            Assert.IsTrue(definition.HasSettings);
        }

        [TestMethod]
        public void Views_HaveDistinctIds()
        {
            var views = StartPageViewCatalog.Views;

            Assert.AreEqual(18, views.Count);
            Assert.AreEqual(views.Count, views.Select(view => view.ViewId).Distinct().Count());
        }

        [DataTestMethod]
        [DataRow(StartPageViewCatalog.CompletedGamesPieViewId, ShowcasePieMode.CompletedGames)]
        [DataRow(StartPageViewCatalog.ProviderPieViewId, ShowcasePieMode.Provider)]
        [DataRow(StartPageViewCatalog.RarityPieViewId, ShowcasePieMode.Rarity)]
        [DataRow(StartPageViewCatalog.TrophyPieViewId, ShowcasePieMode.Trophy)]
        public void SeedViewOptions_SeedsEachPieViewsMode(string viewId, ShowcasePieMode expected)
        {
            var settings = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.Pie, "instance");

            StartPageViewCatalog.SeedViewOptions(viewId, settings);

            Assert.AreEqual(expected, ShowcaseWidgetOptions.GetPieMode(settings));
        }

        [DataTestMethod]
        [DataRow(StartPageViewCatalog.PrestigeScoreCardViewId, "Prestige")]
        [DataRow(StartPageViewCatalog.CollectionScoreCardViewId, "Collection")]
        [DataRow(StartPageViewCatalog.ShowcaseDualScoresViewId, "Collection")]
        public void SeedViewOptions_SeedsEachScoreCardViewsCard(string viewId, string expected)
        {
            var settings = ShowcaseWidgetSettingsFactory.CreateDefault(ShowcaseWidgetKind.Scores, "instance");

            StartPageViewCatalog.SeedViewOptions(viewId, settings);

            Assert.AreEqual(expected, ShowcaseWidgetOptions.GetScoreCardType(settings).Key);
        }

        [TestMethod]
        public void TryGetDefinition_ReturnsFalseForUnknownViewId()
        {
            var found = StartPageViewCatalog.TryGetDefinition("Unknown", out var definition);

            Assert.IsFalse(found);
            Assert.IsNull(definition);
        }

        [TestMethod]
        public void TryGetDefinition_AcceptsLegacyGamesOverviewViewId()
        {
            var found = StartPageViewCatalog.TryGetDefinition(
                StartPageViewCatalog.LegacyGamesOverviewGridViewId,
                out var definition);

            Assert.IsTrue(found);
            Assert.AreEqual(StartPageViewCatalog.GameSummariesGridViewId, definition.ViewId);
            Assert.AreEqual(StartPageWidgetKind.GameSummariesGrid, definition.WidgetKind);
            Assert.IsFalse(StartPageViewCatalog.Views.Any(view =>
                view.ViewId == StartPageViewCatalog.LegacyGamesOverviewGridViewId));
        }
    }
}
