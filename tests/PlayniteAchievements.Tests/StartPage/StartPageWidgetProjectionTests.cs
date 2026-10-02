using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.StartPage
{
    [TestClass]
    public class StartPageWidgetProjectionTests
    {
        [TestMethod]
        public void GameSummaryControlBarAdapter_AppliesProviderProgressAndActivityFilters()
        {
            var items = new[]
            {
                new GameSummaryItem
                {
                    GameName = "Steam Complete",
                    ProviderKey = "Steam",
                    Platforms = new[] { "PC" },
                    IsCompleted = true,
                    UnlockedAchievements = 10,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 1)
                },
                new GameSummaryItem
                {
                    GameName = "Steam No Progress",
                    ProviderKey = "Steam",
                    Platforms = new[] { "Steam Deck" },
                    UnlockedAchievements = 0,
                    TotalAchievements = 10
                },
                new GameSummaryItem
                {
                    GameName = "Xbox Complete",
                    ProviderKey = "Xbox",
                    Platforms = new[] { "Xbox" },
                    IsCompleted = true,
                    UnlockedAchievements = 10,
                    TotalAchievements = 10,
                    LastPlayed = new DateTime(2026, 1, 2)
                }
            };
            var toolbar = new GameSummaryGridControlBarAdapter();
            toolbar.UpdateOptions(items);

            toolbar.ProviderFilterGroups.Single(group => group.ProviderKey == "Steam").SetAll(true);
            toolbar.SetProgressFilterSelected(toolbar.ProgressFilterOptions[0], true);
            toolbar.SetActivityFilterSelected(toolbar.ActivityFilterOptions[0], true);

            var result = toolbar.Apply(items)
                .Select(item => item.GameName)
                .ToList();

            CollectionAssert.AreEqual(new[] { "Steam Complete" }, result);
        }
    }
}
