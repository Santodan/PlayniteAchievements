using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcasePinServiceTests
    {
        [TestMethod]
        public void Collections_AllowMultipleMembershipAndRejectDuplicateNames()
        {
            var settings = new ShowcaseSettings();
            var gameId = Guid.NewGuid();

            Assert.IsTrue(ShowcasePinService.TryCreateAchievementCollection(
                settings,
                "Favorites",
                out var favorites));
            Assert.IsFalse(ShowcasePinService.TryCreateAchievementCollection(
                settings,
                " favorites ",
                out _));
            Assert.IsTrue(ShowcasePinService.TryCreateGameCollection(
                settings,
                "Favorites",
                out var favoriteGames));
            Assert.IsFalse(ShowcasePinService.TryCreateGameCollection(
                settings,
                "FAVORITES",
                out _));
            Assert.IsFalse(ShowcasePinService.TryCreateGameCollection(
                settings,
                "   ",
                out _));
            Assert.AreEqual("Favorites", favorites.Name);

            ShowcasePinService.ToggleAchievement(
                settings,
                settings.DefaultAchievementPinCollectionId,
                gameId,
                "first",
                "Game",
                "First");
            ShowcasePinService.ToggleAchievement(
                settings,
                favorites.CollectionId,
                gameId,
                "first",
                "Game",
                "First");

            Assert.IsTrue(ShowcasePinService.IsAchievementPinned(
                settings,
                settings.DefaultAchievementPinCollectionId,
                gameId,
                "FIRST"));
            Assert.IsTrue(ShowcasePinService.IsAchievementPinned(
                settings,
                favorites.CollectionId,
                gameId,
                "first"));

            ShowcasePinService.ToggleAchievement(
                settings,
                favorites.CollectionId,
                gameId,
                "first",
                "Game",
                "First");
            Assert.IsFalse(ShowcasePinService.IsAchievementPinned(
                settings,
                favorites.CollectionId,
                gameId,
                "first"));
            Assert.IsTrue(ShowcasePinService.IsAchievementPinned(
                settings,
                settings.DefaultAchievementPinCollectionId,
                gameId,
                "first"));

            ShowcasePinService.ToggleGame(
                settings,
                settings.DefaultGamePinCollectionId,
                gameId);
            ShowcasePinService.ToggleGame(
                settings,
                favoriteGames.CollectionId,
                gameId);
            Assert.IsTrue(ShowcasePinService.IsGamePinned(
                settings,
                settings.DefaultGamePinCollectionId,
                gameId));
            Assert.IsTrue(ShowcasePinService.IsGamePinned(
                settings,
                favoriteGames.CollectionId,
                gameId));
        }

        [TestMethod]
        public void DeleteCollection_ReassignsDashboardAndStartPageWidgetsToDefault()
        {
            var settings = new ShowcaseSettings();
            Assert.IsTrue(ShowcasePinService.TryCreateGameCollection(
                settings,
                "Backlog",
                out var backlog));
            Assert.IsTrue(ShowcasePinService.TryCreateAchievementCollection(
                settings,
                "Highlights",
                out var highlights));

            var dashboard = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.GameSummaries
            };
            var startPage = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.IconMosaic
            };
            var achievementDashboard = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.RecentAchievements
            };
            var achievementStartPage = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.IconMosaic
            };
            ShowcaseWidgetOptions.SetPinCollectionId(dashboard, backlog.CollectionId);
            ShowcaseWidgetOptions.SetPinCollectionId(startPage, backlog.CollectionId);
            ShowcaseWidgetOptions.SetPinCollectionId(achievementDashboard, highlights.CollectionId);
            ShowcaseWidgetOptions.SetPinCollectionId(achievementStartPage, highlights.CollectionId);
            settings.WidgetInstances.Add(dashboard);
            settings.WidgetInstances.Add(achievementDashboard);
            settings.StartPageInstances["game:one"] = startPage;
            settings.StartPageInstances["achievement:one"] = achievementStartPage;

            Assert.IsTrue(ShowcasePinService.DeleteGameCollection(settings, backlog.CollectionId));
            Assert.IsTrue(ShowcasePinService.DeleteAchievementCollection(
                settings,
                highlights.CollectionId));
            Assert.AreEqual(
                settings.DefaultGamePinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(dashboard));
            Assert.AreEqual(
                settings.DefaultGamePinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(startPage));
            Assert.AreEqual(
                settings.DefaultAchievementPinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(achievementDashboard));
            Assert.AreEqual(
                settings.DefaultAchievementPinCollectionId,
                ShowcaseWidgetOptions.GetPinCollectionId(achievementStartPage));
            Assert.AreSame(
                settings.GamePinCollections[0],
                ShowcasePinService.ResolveGameCollection(settings, backlog.CollectionId));
            Assert.AreSame(
                settings.AchievementPinCollections[0],
                ShowcasePinService.ResolveAchievementCollection(settings, highlights.CollectionId));
        }

        [TestMethod]
        public void DefaultCollection_CanRenameAndClearButCannotDelete()
        {
            var settings = new ShowcaseSettings();
            var gameId = Guid.NewGuid();
            ShowcasePinService.ToggleGame(
                settings,
                settings.DefaultGamePinCollectionId,
                gameId);

            Assert.IsTrue(ShowcasePinService.RenameGameCollection(
                settings,
                settings.DefaultGamePinCollectionId,
                "Main"));
            Assert.IsFalse(ShowcasePinService.DeleteGameCollection(
                settings,
                settings.DefaultGamePinCollectionId));
            Assert.IsTrue(ShowcasePinService.ClearGameCollection(
                settings,
                settings.DefaultGamePinCollectionId));
            Assert.AreEqual("Main", settings.GamePinCollections[0].Name);
            Assert.AreEqual(0, settings.GamePinCollections[0].GameIds.Count);
            Assert.IsFalse(ShowcasePinService.RenameGameCollection(
                settings,
                "missing-collection",
                "Should not rename Default"));
            Assert.IsFalse(ShowcasePinService.ClearGameCollection(
                settings,
                "missing-collection"));
            Assert.AreEqual("Main", settings.GamePinCollections[0].Name);
        }

        [TestMethod]
        public void Reorder_OnlyChangesTheSelectedCollection()
        {
            var settings = new ShowcaseSettings();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();
            settings.GamePinCollections[0].GameIds = new List<Guid> { first, second };
            Assert.IsTrue(ShowcasePinService.TryCreateGameCollection(
                settings,
                "Other",
                out var other));
            other.GameIds = new List<Guid> { first, second };

            Assert.IsTrue(ShowcasePinService.MoveGame(
                settings,
                other.CollectionId,
                second,
                -1));

            CollectionAssert.AreEqual(
                new[] { first, second },
                settings.GamePinCollections[0].GameIds);
            CollectionAssert.AreEqual(new[] { second, first }, other.GameIds);

            var defaultPins = new List<PinnedAchievementReference>
            {
                new PinnedAchievementReference { GameId = first, ApiName = "first" },
                new PinnedAchievementReference { GameId = second, ApiName = "second" }
            };
            settings.AchievementPinCollections[0].Pins = defaultPins;
            Assert.IsTrue(ShowcasePinService.TryCreateAchievementCollection(
                settings,
                "Achievement order",
                out var achievementOrder));
            achievementOrder.Pins = new List<PinnedAchievementReference>
            {
                defaultPins[0].Clone(),
                defaultPins[1].Clone()
            };

            Assert.IsTrue(ShowcasePinService.MoveAchievement(
                settings,
                achievementOrder.CollectionId,
                second,
                "SECOND",
                -1));

            Assert.AreEqual("first", settings.AchievementPinCollections[0].Pins[0].ApiName);
            Assert.AreEqual("second", achievementOrder.Pins[0].ApiName);
        }
    }
}
