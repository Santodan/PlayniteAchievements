using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Services
{
    [TestClass]
    public class ProviderPlatformFilterTests
    {
        private const string Placeholder = "Platform";

        [TestMethod]
        public void ApplyFilter_NoSelection_ReturnsAllGames()
        {
            var games = CreateGames();

            var result = Filter(games, NoneSelected());

            CollectionAssert.AreEqual(
                games.Select(g => g.GameName).ToList(),
                result);
        }

        [TestMethod]
        public void ApplyFilter_FullProviderSelection_IncludesGamesWithNoPlatform()
        {
            var games = CreateGames();
            var psn = Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }, "PlayStation 4", "PlayStation 5");

            var result = Filter(games, new[] { psn });

            // "PSN No Platform" has no platform metadata but its provider is fully selected.
            CollectionAssert.AreEquivalent(
                new[] { "PSN PS4", "PSN PS5", "PSN No Platform" },
                result);
        }

        [TestMethod]
        public void ApplyFilter_PartialProviderSelection_MatchesOverlappingPlatforms()
        {
            var games = CreateGames();
            var psn = Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }, "PlayStation 5");

            var result = Filter(games, new[] { psn });

            CollectionAssert.AreEqual(new[] { "PSN PS5" }, result);
        }

        [TestMethod]
        public void ApplyFilter_PartialSelection_ExcludesGamesWithNoPlatform()
        {
            var games = CreateGames();
            var psn = Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }, "PlayStation 4");

            var result = Filter(games, new[] { psn });

            CollectionAssert.AreEqual(new[] { "PSN PS4" }, result);
            CollectionAssert.DoesNotContain(result, "PSN No Platform");
        }

        [TestMethod]
        public void ApplyFilter_SinglePlatformProvider_MatchesByProvider()
        {
            var games = CreateGames();
            var steam = Group("Steam", new[] { "PC" }, "PC");

            var result = Filter(games, new[] { steam });

            CollectionAssert.AreEqual(new[] { "Steam Game" }, result);
        }

        [TestMethod]
        public void BuildText_NoSelection_ReturnsPlaceholder()
        {
            var text = OverviewGameSummaryFilters.BuildProviderFilterText(NoneSelected(), Placeholder);

            Assert.AreEqual(Placeholder, text);
        }

        [TestMethod]
        public void BuildText_FullProvider_ShowsProviderName()
        {
            var psn = Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }, "PlayStation 4", "PlayStation 5");

            var text = OverviewGameSummaryFilters.BuildProviderFilterText(new[] { psn }, Placeholder);

            Assert.AreEqual("PSN", text);
        }

        [TestMethod]
        public void BuildText_PartialAndFullMix_ListsPlatformsThenProviderName()
        {
            var ra = Group("RetroAchievements", new[] { "Game Boy", "NES", "PlayStation 2" }, "NES", "Game Boy");
            var psn = Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }, "PlayStation 4", "PlayStation 5");

            var text = OverviewGameSummaryFilters.BuildProviderFilterText(new[] { ra, psn }, Placeholder);

            // RetroAchievements is partial (platform names, in display order), PSN is full (its name).
            Assert.AreEqual("Game Boy, NES, PSN", text);
        }

        private static List<string> Filter(
            List<GameSummaryItem> games,
            IEnumerable<ProviderFilterGroup> groups)
        {
            return OverviewGameSummaryFilters
                .ApplyProviderPlatformFilter(games, groups)
                .Select(g => g.GameName)
                .ToList();
        }

        private static IEnumerable<ProviderFilterGroup> NoneSelected()
        {
            return new[]
            {
                Group("PSN", new[] { "PlayStation 4", "PlayStation 5" }),
                Group("Steam", new[] { "PC" })
            };
        }

        private static ProviderFilterGroup Group(
            string providerKey,
            string[] platforms,
            params string[] selected)
        {
            var selectedSet = new HashSet<string>(selected);
            return new ProviderFilterGroup(
                providerKey,
                providerKey,
                platforms,
                name => selectedSet.Contains(name),
                () => { });
        }

        private static List<GameSummaryItem> CreateGames()
        {
            return new List<GameSummaryItem>
            {
                new GameSummaryItem
                {
                    GameName = "PSN PS4",
                    ProviderKey = "PSN",
                    Platforms = new[] { "PlayStation 4" }
                },
                new GameSummaryItem
                {
                    GameName = "PSN PS5",
                    ProviderKey = "PSN",
                    Platforms = new[] { "PlayStation 5" }
                },
                new GameSummaryItem
                {
                    GameName = "PSN No Platform",
                    ProviderKey = "PSN",
                    Platforms = new string[0]
                },
                new GameSummaryItem
                {
                    GameName = "Steam Game",
                    ProviderKey = "Steam",
                    Platforms = new[] { "PC" }
                }
            };
        }
    }
}
