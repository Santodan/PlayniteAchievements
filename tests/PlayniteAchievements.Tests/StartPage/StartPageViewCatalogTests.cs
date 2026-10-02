using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.StartPage;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageViewCatalogTests
    {
        [TestMethod]
        public void Views_PreserveOriginalNineIdsAndRegisterShowcaseWidgets()
        {
            var views = StartPageViewCatalog.Views;

            // 8 original views plus the 8 shared showcase views. PinnedAchievements,
            // FavoriteGames, and GameMosaic are retired (see ShowcaseWidgetKind), so they
            // no longer contribute views of their own.
            Assert.AreEqual(16, views.Count);
            CollectionAssert.AreEqual(
                new[]
                {
                    StartPageViewCatalog.GameSummariesGridViewId,
                    StartPageViewCatalog.RecentUnlocksGridViewId,
                    StartPageViewCatalog.CompletedGamesPieViewId,
                    StartPageViewCatalog.ProviderPieViewId,
                    StartPageViewCatalog.RarityPieViewId,
                    StartPageViewCatalog.TrophyPieViewId,
                    StartPageViewCatalog.CollectionScoreCardViewId,
                    StartPageViewCatalog.PrestigeScoreCardViewId
                },
                views.Take(8).Select(view => view.ViewId).ToArray());
            CollectionAssert.IsSubsetOf(
                new[]
                {
                    StartPageWidgetKind.GameSummariesGrid,
                    StartPageWidgetKind.RecentUnlocksGrid,
                    StartPageWidgetKind.CompletedGamesPie,
                    StartPageWidgetKind.ProviderPie,
                    StartPageWidgetKind.RarityPie,
                    StartPageWidgetKind.TrophyPie,
                    StartPageWidgetKind.CollectionScoreCard,
                    StartPageWidgetKind.PrestigeScoreCard
                },
                views.Select(view => view.WidgetKind).ToArray());

            // The grid and pie views ride the showcase widget path under their original ids.
            Assert.IsTrue(views
                .Where(view => view.ViewId == StartPageViewCatalog.RecentUnlocksGridViewId ||
                    view.ViewId == StartPageViewCatalog.GameSummariesGridViewId ||
                    view.ViewId == StartPageViewCatalog.CompletedGamesPieViewId ||
                    view.ViewId == StartPageViewCatalog.ProviderPieViewId ||
                    view.ViewId == StartPageViewCatalog.RarityPieViewId ||
                    view.ViewId == StartPageViewCatalog.TrophyPieViewId)
                .All(view => view.ShowcaseWidgetKind.HasValue &&
                    view.HasSettings &&
                    view.AllowMultipleInstances));
            Assert.IsTrue(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseTimelineViewId)
                .AllowMultipleInstances);
            Assert.IsTrue(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseNativePointsViewId)
                .HasSettings);
            Assert.IsFalse(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseProfileViewId)
                .AllowMultipleInstances);
            Assert.IsTrue(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseDualScoresViewId)
                .HasSettings);
            Assert.IsTrue(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseActivityCalendarViewId)
                .HasSettings);
            // NativePoints is the only parked view: resolvable for already-placed widgets but
            // omitted from the add list.
            Assert.IsTrue(views.Single(view =>
                view.ViewId == StartPageViewCatalog.ShowcaseNativePointsViewId)
                .Hidden);
            Assert.AreEqual(1, views.Count(view => view.Hidden));
            Assert.AreEqual(views.Count, views.Select(view => view.ViewId).Distinct().Count());
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
