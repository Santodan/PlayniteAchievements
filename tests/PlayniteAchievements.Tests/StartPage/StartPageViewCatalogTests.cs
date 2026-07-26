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
        public void Views_RegisterNineWidgetsWithExpectedLocalizationKeys()
        {
            var views = StartPageViewCatalog.Views;

            Assert.AreEqual(9, views.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    StartPageWidgetKind.GameSummariesGrid,
                    StartPageWidgetKind.RecentUnlocksGrid,
                    StartPageWidgetKind.FriendsRecentUnlocksGrid,
                    StartPageWidgetKind.CompletedGamesPie,
                    StartPageWidgetKind.ProviderPie,
                    StartPageWidgetKind.RarityPie,
                    StartPageWidgetKind.TrophyPie,
                    StartPageWidgetKind.CollectionScoreCard,
                    StartPageWidgetKind.PrestigeScoreCard
                },
                views.Select(view => view.WidgetKind).ToArray());

            Assert.IsTrue(views.Any(view =>
                view.ViewId == StartPageViewCatalog.FriendsRecentUnlocksGridViewId &&
                view.WidgetKind == StartPageWidgetKind.FriendsRecentUnlocksGrid &&
                view.NameKey == "LOCPlayAch_StartPage_FriendsRecentAchievements"));
            Assert.IsTrue(views.All(view => string.IsNullOrWhiteSpace(view.DescriptionKey)));
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
