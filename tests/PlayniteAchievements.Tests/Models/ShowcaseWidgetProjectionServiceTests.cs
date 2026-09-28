using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Tests.Models
{
    [TestClass]
    public class ShowcaseWidgetProjectionServiceTests
    {
        [TestMethod]
        public void Statistics_CalculateRatesPlaytimeAndStreaksFromSharedSnapshot()
        {
            var today = new DateTime(2026, 7, 31);
            var snapshot = new OverviewDataSnapshot
            {
                TotalUnlocked = 12,
                TotalGames = 3,
                CompletedGames = 1,
                GlobalProgressionPercent = 62.5,
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                {
                    [today] = 2,
                    [today.AddDays(-1)] = 3,
                    [today.AddDays(-4)] = 1
                },
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem { PlaytimeSeconds = 3600, LastPlayed = today },
                    new GameSummaryItem { PlaytimeSeconds = 7200 },
                    new GameSummaryItem()
                },
                Achievements = new List<AchievementDisplayItem>
                {
                    new AchievementDisplayItem { Unlocked = true, GlobalPercentUnlocked = 10 },
                    new AchievementDisplayItem { Unlocked = true, GlobalPercentUnlocked = 30 }
                }
            };

            var statistics = ShowcaseWidgetProjectionService.BuildStatistics(snapshot, today);

            Assert.AreEqual(2, statistics.Single(item => item.Key == "currentStreak").Value);
            Assert.AreEqual(2, statistics.Single(item => item.Key == "longestStreak").Value);
            // Timestamp-less/imported unlocks can contribute to TotalUnlocked, but cannot be
            // assigned to an active day. The rate therefore uses the dated timeline numerator.
            Assert.AreEqual(2, statistics.Single(item => item.Key == "activeDayRate").Value);
            Assert.AreEqual(0.2, statistics.Single(item => item.Key == "thirtyDayRate").Value, 0.001);
            Assert.AreEqual(20, statistics.Single(item => item.Key == "averageGlobalUnlock").Value);
            Assert.AreEqual(2, statistics.Single(item => item.Key == "playedGames").Value);
            Assert.AreEqual(10800, statistics.Single(item => item.Key == "playtime").Value);
            Assert.IsTrue(statistics.All(item => !string.IsNullOrWhiteSpace(item.LabelKey)));

            var profile = ShowcaseWidgetProjectionService.Build(
                snapshot,
                new ShowcaseSettings(),
                new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Profile },
                today);
            Assert.AreEqual(2, profile.Statistics.Single(item =>
                item.Key == "currentStreak").Value);
        }

        [TestMethod]
        public void NativePoints_GroupByProviderAndGameAndAddsOtherBucket()
        {
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    Game("Alpha", "Steam", "Steam", 100),
                    Game("Beta", "Steam", "Steam", 80),
                    Game("Gamma", "Xbox", "Xbox", 50),
                    Game("No points", "Xbox", "Xbox", 0)
                }
            };
            var instance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.NativePoints
            };
            instance.SetOption("TopN", 1);

            var providers = ShowcaseWidgetProjectionService.BuildNativePoints(snapshot, instance);
            Assert.AreEqual(2, providers.Count);
            Assert.AreEqual("Steam", providers[0].Label);
            Assert.AreEqual(180, providers[0].Value);
            Assert.AreEqual("Other", providers[1].Key);
            Assert.AreEqual(50, providers[1].Value);
            Assert.AreEqual("LOCPlayAch_Showcase_Other", providers[1].LabelKey);

            instance.SetOption("Grouping", ShowcasePointsGrouping.Game);
            var games = ShowcaseWidgetProjectionService.BuildNativePoints(snapshot, instance);
            Assert.AreEqual("Alpha", games[0].Label);
            Assert.AreEqual(130, games[1].Value);
        }

        [TestMethod]
        public void PinsFavoritesAndMosaicPreserveConfiguredIdentityAndOrder()
        {
            var firstGameId = Guid.NewGuid();
            var secondGameId = Guid.NewGuid();
            var firstAchievement = new AchievementDisplayItem
            {
                PlayniteGameId = firstGameId,
                ApiName = "first",
                DisplayName = "First unlock",
                GameName = "First game",
                Unlocked = true,
                Rarity = RarityTier.Common,
                GlobalPercentUnlocked = 55,
                RaritySortValue = AchievementRarityResolver.GetSortValue(
                    55,
                    RarityTier.Common),
                UnlockTimeUtc = new DateTime(2026, 7, 30)
            };
            var rareAchievement = new AchievementDisplayItem
            {
                PlayniteGameId = secondGameId,
                ApiName = "rare",
                DisplayName = "Rare unlock",
                GameName = "Second game",
                Unlocked = true,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = 1,
                RaritySortValue = AchievementRarityResolver.GetSortValue(
                    1,
                    RarityTier.UltraRare),
                UnlockTimeUtc = new DateTime(2026, 7, 29)
            };
            var tierOnlyUltraRare = new AchievementDisplayItem
            {
                PlayniteGameId = Guid.NewGuid(),
                ApiName = "tier-only",
                DisplayName = "Tier-only ultra rare unlock",
                GameName = "Third game",
                Unlocked = true,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = null,
                RaritySortValue = AchievementRarityResolver.GetSortValue(
                    null,
                    RarityTier.UltraRare),
                UnlockTimeUtc = new DateTime(2026, 7, 28)
            };
            var timestampLessRare = new AchievementDisplayItem
            {
                PlayniteGameId = Guid.NewGuid(),
                ApiName = "timestamp-less",
                DisplayName = "Imported rare unlock",
                GameName = "Fourth game",
                Unlocked = true,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = 0.5,
                RaritySortValue = AchievementRarityResolver.GetSortValue(
                    0.5,
                    RarityTier.UltraRare),
                UnlockTimeUtc = null
            };
            var lockedRare = new AchievementDisplayItem
            {
                PlayniteGameId = Guid.NewGuid(),
                ApiName = "locked",
                Unlocked = false,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = 0.1,
                RaritySortValue = AchievementRarityResolver.GetSortValue(
                    0.1,
                    RarityTier.UltraRare)
            };
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>
                {
                    firstAchievement,
                    rareAchievement,
                    tierOnlyUltraRare,
                    timestampLessRare,
                    lockedRare
                },
                RecentAchievements = new List<AchievementDisplayItem>
                {
                    firstAchievement,
                    rareAchievement
                },
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem
                    {
                        PlayniteGameId = firstGameId,
                        GameName = "First game",
                        IsFavorite = false
                    },
                    new GameSummaryItem
                    {
                        PlayniteGameId = secondGameId,
                        GameName = "Second game",
                        IsFavorite = true
                    }
                }
            };
            var missingGameId = Guid.NewGuid();
            var settings = new ShowcaseSettings();
            settings.GamePinCollections[0].GameIds =
                new List<Guid> { secondGameId, firstGameId };
            settings.AchievementPinCollections[0].Pins =
                new List<PinnedAchievementReference>
                {
                    new PinnedAchievementReference { GameId = firstGameId, ApiName = "first" },
                    new PinnedAchievementReference
                    {
                        GameId = missingGameId,
                        ApiName = "missing",
                        LastKnownGameName = "Removed game",
                        LastKnownAchievementName = "Remembered unlock"
                    },
                    new PinnedAchievementReference
                    {
                        GameId = lockedRare.PlayniteGameId.Value,
                        ApiName = lockedRare.ApiName
                    }
                };

            var resolvedPins = ShowcaseWidgetProjectionService.ResolvePinnedAchievements(
                snapshot,
                settings.AchievementPinCollections[0].Pins);
            Assert.AreSame(firstAchievement, resolvedPins[0].Achievement);
            Assert.IsTrue(resolvedPins[1].IsMissing);
            Assert.AreEqual("Remembered unlock", resolvedPins[1].Name);
            Assert.AreSame(lockedRare, resolvedPins[2].Achievement);

            var gameGridInstance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.GameSummaries
            };
            ShowcaseWidgetOptions.SetGameGridSource(gameGridInstance, ShowcaseGameGridSource.Pinned);
            var pinnedGames = ShowcaseWidgetProjectionService.Build(
                snapshot,
                settings,
                gameGridInstance,
                new DateTime(2026, 7, 31)).Games;
            CollectionAssert.AreEqual(
                new[] { secondGameId, firstGameId },
                pinnedGames.Select(game => game.PlayniteGameId.Value).ToArray());

            ShowcaseWidgetOptions.SetGameGridSource(gameGridInstance, ShowcaseGameGridSource.PlayniteFavorites);
            var liveFavorites = ShowcaseWidgetProjectionService.Build(
                snapshot,
                settings,
                gameGridInstance,
                new DateTime(2026, 7, 31)).Games;
            Assert.AreEqual(secondGameId, liveFavorites.Single().PlayniteGameId);

            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            mosaic.SetOption("Source", ShowcaseMosaicSource.Rarest);
            var rarest = ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic);
            CollectionAssert.AreEqual(
                new[]
                {
                    timestampLessRare,
                    rareAchievement,
                    tierOnlyUltraRare,
                    firstAchievement
                },
                rarest.ToArray());
            mosaic.SetOption("Source", ShowcaseMosaicSource.Pinned);
            var pinnedMosaic = ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic);
            CollectionAssert.AreEqual(new[] { firstAchievement, lockedRare }, pinnedMosaic.ToArray());
        }

        [TestMethod]
        public void UnlockNext_RanksLockedCandidatesByCriterionAndSpreadsAcrossGames()
        {
            var nearlyDone = Guid.NewGuid();
            var barelyStarted = Guid.NewGuid();
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem
                    {
                        PlayniteGameId = nearlyDone,
                        GameName = "Nearly done",
                        TotalAchievements = 10,
                        UnlockedAchievements = 9,
                        LastPlayed = DateTime.Now.AddDays(-2)
                    },
                    new GameSummaryItem
                    {
                        PlayniteGameId = barelyStarted,
                        GameName = "Barely started",
                        TotalAchievements = 10,
                        UnlockedAchievements = 1,
                        LastPlayed = DateTime.Now.AddDays(-3)
                    }
                },
                UnlockNextPoolBuilt = true,
                UnlockNextCandidates = new List<AchievementDisplayItem>
                {
                    LockedCandidate(nearlyDone, "near-first", order: 0, percent: 4),
                    LockedCandidate(nearlyDone, "near-easy", order: 7, percent: 90),
                    LockedCandidate(barelyStarted, "barely-first", order: 1, percent: 30),
                    LockedCandidate(barelyStarted, "barely-easy", order: 9, percent: 75)
                }
            };
            var settings = new ShowcaseSettings();
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicSource(mosaic, ShowcaseMosaicSource.UnlockNext);
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.All));

            // One per game by default. Closest to completion (the default) leads with the nearly
            // finished game, and within each game with its most commonly earned leftover.
            CollectionAssert.AreEqual(
                new[] { "near-easy", "barely-easy" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            // The nearly finished game fills its cap before the next game starts.
            ShowcaseWidgetOptions.SetMaxPerGame(mosaic, 2);
            CollectionAssert.AreEqual(
                new[] { "near-easy", "near-first", "barely-easy", "barely-first" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            // Easiest is one list by global percentage across games.
            ShowcaseWidgetOptions.SetUnlockNextCriterion(mosaic, UnlockNextCriterion.Easiest);
            CollectionAssert.AreEqual(
                new[] { "near-easy", "barely-easy", "barely-first", "near-first" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            // The count caps the ranked list.
            ShowcaseWidgetOptions.SetMosaicCount(mosaic, 3);
            CollectionAssert.AreEqual(
                new[] { "near-easy", "barely-easy", "barely-first" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));
        }

        [TestMethod]
        public void UnlockNext_MaxPerGameIsACapNotAQuota()
        {
            var easyGame = Guid.NewGuid();
            var hardGame = Guid.NewGuid();
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem
                    {
                        PlayniteGameId = easyGame,
                        TotalAchievements = 10,
                        UnlockedAchievements = 5,
                        LastPlayed = DateTime.Now.AddDays(-1)
                    },
                    new GameSummaryItem
                    {
                        PlayniteGameId = hardGame,
                        TotalAchievements = 10,
                        UnlockedAchievements = 8,
                        LastPlayed = DateTime.Now.AddDays(-1)
                    }
                },
                UnlockNextPoolBuilt = true,
                UnlockNextCandidates = new List<AchievementDisplayItem>
                {
                    LockedCandidate(easyGame, "easy-90", order: 0, percent: 90),
                    LockedCandidate(easyGame, "easy-80", order: 1, percent: 80),
                    LockedCandidate(easyGame, "easy-70", order: 2, percent: 70),
                    LockedCandidate(hardGame, "hard-30", order: 0, percent: 30),
                    LockedCandidate(hardGame, "hard-5", order: 1, percent: 5)
                }
            };
            var settings = new ShowcaseSettings();
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicSource(mosaic, ShowcaseMosaicSource.UnlockNext);
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.All));
            ShowcaseWidgetOptions.SetUnlockNextCriterion(mosaic, UnlockNextCriterion.Easiest);
            ShowcaseWidgetOptions.SetMosaicCount(mosaic, 3);

            ShowcaseWidgetOptions.SetMaxPerGame(mosaic, 2);
            CollectionAssert.AreEqual(
                new[] { "easy-90", "easy-80", "hard-30" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            // With room for three per game, the hard game earns no slot at all.
            ShowcaseWidgetOptions.SetMaxPerGame(mosaic, 3);
            CollectionAssert.AreEqual(
                new[] { "easy-90", "easy-80", "easy-70" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));
        }

        [TestMethod]
        public void UnlockNext_StoredNextInLineFallsBackToClosestToCompletion()
        {
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            instance.Options["UnlockNextCriterion"] = "NextInLine";

            Assert.AreEqual(
                UnlockNextCriterion.ClosestToCompletion,
                ShowcaseWidgetOptions.GetUnlockNextCriterion(instance));
        }

        [TestMethod]
        public void UnlockNext_FiltersWindowHiddenAndAchievementsUnlockedSinceThePoolWasBuilt()
        {
            var recent = Guid.NewGuid();
            var stale = Guid.NewGuid();
            var claimed = LockedCandidate(recent, "claimed", order: 0, percent: 50);
            var hidden = LockedCandidate(recent, "hidden", order: 1, percent: 50);
            hidden.Hidden = true;
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem>
                {
                    new GameSummaryItem
                    {
                        PlayniteGameId = recent,
                        TotalAchievements = 10,
                        UnlockedAchievements = 5,
                        LastPlayed = DateTime.Now.AddDays(-1)
                    },
                    new GameSummaryItem
                    {
                        PlayniteGameId = stale,
                        TotalAchievements = 10,
                        UnlockedAchievements = 5,
                        LastPlayed = DateTime.Now.AddYears(-2)
                    }
                },
                UnlockNextPoolBuilt = true,
                UnlockNextCandidates = new List<AchievementDisplayItem>
                {
                    claimed,
                    hidden,
                    LockedCandidate(recent, "open", order: 2, percent: 50),
                    LockedCandidate(stale, "stale", order: 0, percent: 99)
                },
                // A delta tick carries the pool forward, so an achievement unlocked since must be
                // dropped on the strength of the snapshot's own unlocked rows.
                Achievements = new List<AchievementDisplayItem>
                {
                    new AchievementDisplayItem
                    {
                        PlayniteGameId = recent,
                        ApiName = "claimed",
                        Unlocked = true
                    }
                }
            };
            var settings = new ShowcaseSettings();
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicSource(mosaic, ShowcaseMosaicSource.UnlockNext);
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.OneMonth));
            ShowcaseWidgetOptions.SetMaxPerGame(mosaic, 10);

            CollectionAssert.AreEqual(
                new[] { "open" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            ShowcaseWidgetOptions.SetIncludeHiddenAchievements(mosaic, true);
            CollectionAssert.AreEqual(
                new[] { "hidden", "open" },
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)));

            // Widening the window lets the stale game back in.
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.All));
            CollectionAssert.Contains(
                ApiNames(ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, mosaic)),
                "stale");
        }

        [TestMethod]
        public void UnlockNext_WithoutAPoolResolvesEmpty()
        {
            var snapshot = new OverviewDataSnapshot();
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicSource(mosaic, ShowcaseMosaicSource.UnlockNext);

            Assert.AreEqual(
                0,
                ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, new ShowcaseSettings(), mosaic).Count);
        }

        [TestMethod]
        public void FinishNext_OptionsFilterAndRankByTheChosenCriterion()
        {
            // 9/10, one left, all rare. 12/20, eight left, all common. 2/10, eight left. Unplayed 5/6.
            var almost = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Almost",
                TotalAchievements = 10,
                UnlockedAchievements = 9,
                TotalRarePossible = 10,
                RareCount = 9,
                LastPlayed = DateTime.Now.AddDays(-1)
            };
            var easy = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Easy",
                TotalAchievements = 20,
                UnlockedAchievements = 12,
                TotalCommonPossible = 20,
                CommonCount = 12,
                LastPlayed = DateTime.Now.AddDays(-1)
            };
            var barely = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Barely",
                TotalAchievements = 10,
                UnlockedAchievements = 2,
                LastPlayed = DateTime.Now.AddDays(-1)
            };
            var unplayed = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Unplayed",
                TotalAchievements = 6,
                UnlockedAchievements = 5
            };
            var games = new[] { barely, easy, almost, unplayed };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(instance, TimeWindow.FromPreset(TimelineRange.OneMonth));

            CollectionAssert.AreEqual(
                new[] { almost, easy, barely },
                ShowcaseWidgetProjectionService.ResolveFinishNextGames(games, instance).ToArray(),
                "Closest to completion; the unplayed game is outside the window.");

            ShowcaseWidgetOptions.SetFinishNextIncludeUnplayed(instance, true);
            ShowcaseWidgetOptions.SetFinishNextMinimumProgress(instance, 50);
            CollectionAssert.AreEqual(
                new[] { almost, unplayed, easy },
                ShowcaseWidgetProjectionService.ResolveFinishNextGames(games, instance).ToArray(),
                "Unplayed kept, and games under 50% dropped.");

            ShowcaseWidgetOptions.SetFinishNextCriterion(instance, FinishNextCriterion.EasiestRemaining);
            CollectionAssert.AreEqual(
                new[] { easy, unplayed, almost },
                ShowcaseWidgetProjectionService.ResolveFinishNextGames(games, instance).ToArray(),
                "All-common remainder first, no tier data in the middle, all-rare last.");

            ShowcaseWidgetOptions.SetFinishNextCriterion(instance, FinishNextCriterion.FewestRemaining);
            ShowcaseWidgetOptions.SetFinishNextMaxRemaining(instance, 1);
            CollectionAssert.AreEqual(
                new[] { almost, unplayed },
                ShowcaseWidgetProjectionService.ResolveFinishNextGames(games, instance).ToArray(),
                "At most one left.");
        }

        [TestMethod]
        public void FinishNext_RanksUnfinishedGamesByCompletionWithinTheWindow()
        {
            var almost = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Almost",
                TotalAchievements = 10,
                UnlockedAchievements = 9,
                LastPlayed = DateTime.Now.AddDays(-1)
            };
            var halfway = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Halfway",
                TotalAchievements = 10,
                UnlockedAchievements = 5,
                LastPlayed = DateTime.Now.AddDays(-2)
            };
            var finished = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Finished",
                TotalAchievements = 10,
                UnlockedAchievements = 10,
                LastPlayed = DateTime.Now.AddDays(-1)
            };
            var forgotten = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Forgotten",
                TotalAchievements = 10,
                UnlockedAchievements = 8,
                LastPlayed = DateTime.Now.AddYears(-3)
            };
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem> { halfway, finished, almost, forgotten }
            };
            var settings = new ShowcaseSettings();
            var mosaic = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(mosaic, ShowcaseMosaicContent.Games);
            ShowcaseWidgetOptions.SetGameMosaicSource(mosaic, ShowcaseGameMosaicSource.FinishNext);
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.OneMonth));

            CollectionAssert.AreEqual(
                new[] { almost, halfway },
                ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, mosaic).ToArray());

            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(mosaic, TimeWindow.FromPreset(TimelineRange.All));
            CollectionAssert.AreEqual(
                new[] { almost, forgotten, halfway },
                ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, mosaic).ToArray());
        }

        private static AchievementDisplayItem LockedCandidate(
            Guid gameId,
            string apiName,
            int order,
            double percent)
        {
            return new AchievementDisplayItem
            {
                PlayniteGameId = gameId,
                ApiName = apiName,
                DisplayName = apiName,
                Unlocked = false,
                DefaultOrderIndex = order,
                GlobalPercentUnlocked = percent
            };
        }

        private static string[] ApiNames(IEnumerable<AchievementDisplayItem> items) =>
            items.Select(item => item.ApiName).ToArray();

        [TestMethod]
        public void ActivityCalendar_DensifiesSundayAlignedTrailingYear()
        {
            var endDate = new DateTime(2026, 7, 31); // a Friday
            var snapshot = new OverviewDataSnapshot
            {
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                {
                    [endDate] = 2,
                    [endDate.AddHours(-30)] = 3,           // same day as endDate-2 once dated
                    [endDate.AddDays(-1).AddHours(6)] = 1, // duplicate-day key collapses
                    [endDate.AddDays(-1)] = 4,
                    [endDate.AddDays(-500)] = 99,          // outside the window
                    [endDate.AddDays(-3)] = -7             // negative clamps to zero
                }
            };

            var calendar = ShowcaseWidgetProjectionService.BuildActivityCalendar(
                snapshot,
                CalendarInstance(TimelineRange.OneYear),
                endDate);

            Assert.AreEqual(DayOfWeek.Sunday, calendar.StartDate.DayOfWeek);
            Assert.IsTrue(calendar.StartDate <= endDate.AddDays(-364));
            Assert.IsTrue(calendar.StartDate > endDate.AddDays(-364 - 7));
            Assert.AreEqual(endDate, calendar.EndDate);
            Assert.AreEqual((endDate - calendar.StartDate).Days + 1, calendar.Days.Count);
            Assert.AreEqual(calendar.StartDate, calendar.Days[0].Date);
            Assert.AreEqual(endDate, calendar.Days[calendar.Days.Count - 1].Date);

            var byDate = calendar.Days.ToDictionary(day => day.Date);
            Assert.AreEqual(5, byDate[endDate.AddDays(-1)].Count);
            Assert.AreEqual(2, byDate[endDate].Count);
            Assert.AreEqual(0, byDate[endDate.AddDays(-3)].Count);
            Assert.AreEqual(0, byDate[endDate.AddDays(-10)].Count);
            Assert.AreEqual(5, calendar.MaxCount);
            Assert.AreEqual(10, calendar.TotalCount);
            Assert.AreEqual(3, calendar.ActiveDayCount);
        }

        [TestMethod]
        public void ActivityCalendar_BucketsIntensityAtActiveDayPercentiles()
        {
            var endDate = new DateTime(2026, 7, 31);
            var snapshot = new OverviewDataSnapshot
            {
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                {
                    [endDate] = 8,
                    [endDate.AddDays(-1)] = 1,
                    [endDate.AddDays(-2)] = 2,
                    [endDate.AddDays(-3)] = 3,
                    [endDate.AddDays(-4)] = 5,
                    [endDate.AddDays(-5)] = 7
                }
            };

            var byDate = ShowcaseWidgetProjectionService.BuildActivityCalendar(
                    snapshot,
                    CalendarInstance(TimelineRange.OneYear),
                    endDate)
                .Days.ToDictionary(day => day.Date);

            // Active counts sorted [1,2,3,5,7,8]: nearest-rank P25=2, P50=3, P75=7.
            Assert.AreEqual(4, byDate[endDate].Intensity);
            Assert.AreEqual(1, byDate[endDate.AddDays(-1)].Intensity);
            Assert.AreEqual(1, byDate[endDate.AddDays(-2)].Intensity);
            Assert.AreEqual(2, byDate[endDate.AddDays(-3)].Intensity);
            Assert.AreEqual(3, byDate[endDate.AddDays(-4)].Intensity);
            Assert.AreEqual(3, byDate[endDate.AddDays(-5)].Intensity);
            Assert.AreEqual(0, byDate[endDate.AddDays(-6)].Intensity);

            // One outlier day no longer washes typical days into the lightest tier.
            var outlier = ShowcaseWidgetProjectionService.BuildActivityCalendar(
                    new OverviewDataSnapshot
                    {
                        GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                        {
                            [endDate] = 60,
                            [endDate.AddDays(-1)] = 1,
                            [endDate.AddDays(-2)] = 1,
                            [endDate.AddDays(-3)] = 2,
                            [endDate.AddDays(-4)] = 2,
                            [endDate.AddDays(-5)] = 3
                        }
                    },
                    CalendarInstance(TimelineRange.OneYear),
                    endDate)
                .Days.ToDictionary(day => day.Date);
            Assert.AreEqual(4, outlier[endDate].Intensity);
            Assert.AreEqual(1, outlier[endDate.AddDays(-1)].Intensity);
            Assert.AreEqual(2, outlier[endDate.AddDays(-3)].Intensity);
            Assert.AreEqual(3, outlier[endDate.AddDays(-5)].Intensity);

            var single = ShowcaseWidgetProjectionService.BuildActivityCalendar(
                new OverviewDataSnapshot
                {
                    GlobalUnlockCountsByDate = new Dictionary<DateTime, int> { [endDate] = 1 }
                },
                CalendarInstance(TimelineRange.OneYear),
                endDate);
            Assert.AreEqual(4, single.Days.Last().Intensity);

            var built = ShowcaseWidgetProjectionService.Build(
                snapshot,
                new ShowcaseSettings(),
                CalendarInstance(TimelineRange.OneYear),
                endDate);
            Assert.AreEqual(byDate.Count, built.ActivityCalendar.Days.Count);

            // A shorter range narrows the window while keeping the Sunday alignment.
            var quarter = ShowcaseWidgetProjectionService.BuildActivityCalendar(
                snapshot,
                CalendarInstance(TimelineRange.ThreeMonths),
                endDate);
            Assert.AreEqual(DayOfWeek.Sunday, quarter.StartDate.DayOfWeek);
            Assert.IsTrue(quarter.Days.Count < 120);
        }

        private static ShowcaseWidgetInstanceSettings CalendarInstance(TimelineRange range)
        {
            var instance = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.ActivityCalendar
            };
            ShowcaseTimelineOptions.SetWindow(instance, TimeWindow.FromPreset(range));
            return instance;
        }

        [TestMethod]
        public void ActivityCalendar_CustomWindow_EndsOnItsToDate()
        {
            var today = new DateTime(2026, 7, 31);
            var snapshot = new OverviewDataSnapshot
            {
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                {
                    [new DateTime(2026, 3, 10)] = 2,
                    [new DateTime(2026, 4, 20)] = 5,   // after To, outside the window
                    [today] = 1
                }
            };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ActivityCalendar };
            ShowcaseTimelineOptions.SetWindow(instance, TimeWindow.Custom(new DateTime(2026, 3, 1), new DateTime(2026, 4, 15)));

            var calendar = ShowcaseWidgetProjectionService.BuildActivityCalendar(snapshot, instance, today);

            Assert.AreEqual(new DateTime(2026, 4, 15), calendar.EndDate);
            Assert.AreEqual(DayOfWeek.Sunday, calendar.StartDate.DayOfWeek);
            Assert.IsTrue(calendar.StartDate <= new DateTime(2026, 3, 1));
            Assert.AreEqual(2, calendar.TotalCount, "only the unlock inside From..To counts");
        }

        [TestMethod]
        public void Build_DifferentCustomWindows_DoNotShareCachedCalendars()
        {
            var today = new DateTime(2026, 7, 31);
            var snapshot = new OverviewDataSnapshot
            {
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int> { [today.AddDays(-3)] = 4 }
            };
            var march = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ActivityCalendar };
            ShowcaseTimelineOptions.SetWindow(march, TimeWindow.Custom(new DateTime(2026, 3, 1), new DateTime(2026, 3, 31)));
            var june = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.ActivityCalendar };
            ShowcaseTimelineOptions.SetWindow(june, TimeWindow.Custom(new DateTime(2026, 6, 1), new DateTime(2026, 6, 30)));

            var first = ShowcaseWidgetProjectionService.Build(snapshot, new ShowcaseSettings(), march, today).ActivityCalendar;
            var second = ShowcaseWidgetProjectionService.Build(snapshot, new ShowcaseSettings(), june, today).ActivityCalendar;

            Assert.AreEqual(new DateTime(2026, 3, 31), first.EndDate);
            Assert.AreEqual(new DateTime(2026, 6, 30), second.EndDate);
            Assert.AreSame(first, ShowcaseWidgetProjectionService.Build(snapshot, new ShowcaseSettings(), march, today).ActivityCalendar, "same window reuses the cached calendar");
        }

        [TestMethod]
        public void Build_Timeline_HandsOverUnwindowedDayCounts()
        {
            var today = new DateTime(2026, 7, 31);
            var snapshot = new OverviewDataSnapshot
            {
                GlobalUnlockCountsByDate = new Dictionary<DateTime, int>
                {
                    [new DateTime(2019, 1, 1)] = 3,
                    [today.AddDays(-2)] = 1,
                    [today.AddDays(-1)] = -5   // negatives clamp to zero
                }
            };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Timeline };
            ShowcaseTimelineOptions.SetWindow(instance, TimeWindow.FromPreset(TimelineRange.SevenDays));

            var timeline = ShowcaseWidgetProjectionService.Build(snapshot, new ShowcaseSettings(), instance, today).Timeline;

            Assert.AreEqual(3, timeline[new DateTime(2019, 1, 1)], "the chart windows the counts itself");
            Assert.AreEqual(1, timeline[today.AddDays(-2)]);
            Assert.AreEqual(0, timeline[today.AddDays(-1)]);
        }

        [TestMethod]
        public void FinishNext_CustomWindow_BoundsLastPlayedOnBothEnds()
        {
            var now = new DateTime(2026, 7, 31, 12, 0, 0);
            GameSummaryItem GameLastPlayed(string name, int daysAgo) => new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = name,
                TotalAchievements = 10,
                UnlockedAchievements = 5,
                LastPlayed = new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc).AddDays(-daysAgo)
            };
            var recent = GameLastPlayed("Recent", 10);
            var inside = GameLastPlayed("Inside", 40);
            var old = GameLastPlayed("Old", 100);
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(
                instance,
                TimeWindow.Custom(now.Date.AddDays(-50), now.Date.AddDays(-20)));

            var games = ShowcaseWidgetProjectionService
                .ResolveFinishNextGames(new[] { recent, inside, old }, instance, now)
                .ToArray();

            CollectionAssert.AreEqual(new[] { inside }, games);

            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(instance, TimeWindow.All);
            Assert.AreEqual(
                3,
                ShowcaseWidgetProjectionService.ResolveFinishNextGames(new[] { recent, inside, old }, instance, now).Count());
        }

        [TestMethod]
        public void ScoreHistory_AccumulatesPerAchievementScoresWithUndatedBaseline()
        {
            var endDate = new DateTime(2026, 7, 31);
            var dated = new AchievementDisplayItem
            {
                Unlocked = true,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = 1,
                UnlockTimeUtc = endDate.AddDays(-2)
            };
            var datedSameDay = new AchievementDisplayItem
            {
                Unlocked = true,
                Rarity = RarityTier.Common,
                GlobalPercentUnlocked = 60,
                UnlockTimeUtc = endDate.AddDays(-2).AddHours(5)
            };
            var undated = new AchievementDisplayItem
            {
                Unlocked = true,
                Rarity = RarityTier.Rare,
                GlobalPercentUnlocked = 10,
                UnlockTimeUtc = null
            };
            var locked = new AchievementDisplayItem
            {
                Unlocked = false,
                Rarity = RarityTier.UltraRare,
                GlobalPercentUnlocked = 0.2
            };
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem> { dated, datedSameDay, undated, locked }
            };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Scores };

            var points = ShowcaseWidgetProjectionService.BuildScoreHistory(snapshot, instance, endDate);

            Assert.IsTrue(points.Count >= 2);
            var expectedCollection = dated.CollectionScore + datedSameDay.CollectionScore + undated.CollectionScore;
            var expectedPrestige = dated.PrestigeScore + datedSameDay.PrestigeScore + undated.PrestigeScore;
            var last = points[points.Count - 1];
            Assert.AreEqual(endDate, last.Date);
            Assert.AreEqual(expectedCollection, last.CollectionScore);
            Assert.AreEqual(expectedPrestige, last.PrestigeScore);
            // The undated baseline is present from the very first point.
            Assert.AreEqual(undated.CollectionScore, points[0].CollectionScore);
            // Gap days carry the cumulative value forward.
            var afterUnlock = points.First(point => point.Date >= endDate.AddDays(-1));
            Assert.AreEqual(expectedCollection, afterUnlock.CollectionScore);
        }

        [TestMethod]
        public void ScoreHistory_DownsamplesAllTimeToBoundedPointCount()
        {
            var endDate = new DateTime(2026, 7, 31);
            var achievements = new List<AchievementDisplayItem>();
            for (var i = 0; i < 60; i++)
            {
                achievements.Add(new AchievementDisplayItem
                {
                    Unlocked = true,
                    Rarity = RarityTier.Common,
                    GlobalPercentUnlocked = 70,
                    UnlockTimeUtc = endDate.AddDays(-i * 30) // spans ~5 years
                });
            }

            var snapshot = new OverviewDataSnapshot { Achievements = achievements };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.Scores };
            instance.SetOption("TimelineRange", TimelineRange.All);

            var points = ShowcaseWidgetProjectionService.BuildScoreHistory(snapshot, instance, endDate);

            Assert.IsTrue(points.Count <= 130, $"Expected bounded point count, got {points.Count}");
            Assert.AreEqual(endDate, points[points.Count - 1].Date);
            Assert.AreEqual(
                achievements.Sum(item => item.CollectionScore),
                points[points.Count - 1].CollectionScore);
            for (var i = 1; i < points.Count; i++)
            {
                Assert.IsTrue(points[i].CollectionScore >= points[i - 1].CollectionScore);
            }

            Assert.AreEqual(
                0,
                ShowcaseWidgetProjectionService.BuildScoreHistory(
                    new OverviewDataSnapshot(),
                    instance,
                    endDate).Count);

            var built = ShowcaseWidgetProjectionService.Build(
                snapshot,
                new ShowcaseSettings(),
                instance,
                endDate);
            Assert.AreEqual(points.Count, built.ScoreHistory.Count);
        }

        [TestMethod]
        public void AchievementsGrid_ProjectsAllRowsUncappedAndResolvesPerInstanceGridOptions()
        {
            var items = Enumerable.Range(0, 30)
                .Select(i => new AchievementDisplayItem
                {
                    ApiName = $"a{i}",
                    Unlocked = i % 3 != 2,
                    UnlockTimeUtc = new DateTime(2026, 7, 1).AddDays(-i)
                })
                .ToList();
            var snapshot = new OverviewDataSnapshot { Achievements = items };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };

            // The resolver hands back every unlocked achievement uncapped, newest first. Locked rows
            // (the snapshot's pinned goals) stay out of this source. The widget view model applies
            // the MaxRows cap after its control-bar search filter. Repeat calls reuse the
            // per-snapshot cached list.
            var unlocked = items.Where(item => item.Unlocked)
                .OrderByDescending(item => item.UnlockTimeUtc)
                .ToList();
            var rows = ShowcaseWidgetProjectionService.ResolveAllAchievements(snapshot);
            CollectionAssert.AreEqual(unlocked, rows.ToList());
            Assert.AreSame(rows, ShowcaseWidgetProjectionService.ResolveAllAchievements(snapshot));

            var catalog = new GridOptionsCatalog();
            var surfaceKey = ShowcaseGridSurfaces.ForInstance(
                ShowcaseGridSurfaces.RecentAchievements,
                instance.InstanceId);
            var built = ShowcaseWidgetProjectionService.Build(
                snapshot,
                new ShowcaseSettings(),
                instance,
                gridOptions: catalog);
            Assert.AreEqual(unlocked.Count, built.AchievementRows.Count);
            Assert.AreSame(catalog.GetAchievement(surfaceKey), built.GridWidgetOptions);
        }

        [TestMethod]
        public void AchievementsGrid_UnlockNextSourceDrawsFromThePoolAndRequestsIt()
        {
            var gameId = Guid.NewGuid();
            var candidate = new AchievementDisplayItem
            {
                PlayniteGameId = gameId,
                ApiName = "next",
                Unlocked = false,
                GlobalPercentUnlocked = 40
            };
            var snapshot = new OverviewDataSnapshot
            {
                UnlockNextCandidates = new List<AchievementDisplayItem> { candidate },
                UnlockNextPoolBuilt = true
            };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.RecentAchievements };
            ShowcaseWidgetOptions.SetAchievementGridSource(instance, ShowcaseAchievementGridSource.UnlockNext);
            ShowcaseWidgetOptions.SetLastPlayedTimeWindow(instance, TimeWindow.FromPreset(TimelineRange.All));

            var built = ShowcaseWidgetProjectionService.Build(snapshot, new ShowcaseSettings(), instance);

            CollectionAssert.AreEqual(new[] { candidate }, built.AchievementRows.ToList());
            Assert.IsTrue(ShowcaseWidgetOptions.RequiresUnlockNextPool(instance));
        }

        [TestMethod]
        public void GameSummaries_AppliesActivityScopeAndHideCompletedWithoutSortingOrCapping()
        {
            var oldest = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Alpha",
                UnlockedAchievements = 4,
                TotalAchievements = 10,
                PlaytimeSeconds = 50,
                LastUnlockUtc = new DateTime(2026, 1, 1)
            };
            var newest = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Beta",
                UnlockedAchievements = 9,
                TotalAchievements = 10,
                PlaytimeSeconds = 500,
                LastUnlockUtc = new DateTime(2026, 7, 1)
            };
            var completed = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Gamma",
                UnlockedAchievements = 10,
                TotalAchievements = 10,
                IsCompleted = true,
                PlaytimeSeconds = 5,
                LastUnlockUtc = new DateTime(2026, 6, 1)
            };
            var neverUnlocked = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "Delta",
                TotalAchievements = 10,
                PlaytimeSeconds = 5000
            };
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem> { oldest, newest, completed, neverUnlocked }
            };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.GameSummaries };

            // Sorting and the MaxRows cap live in the widget view model; the resolver keeps
            // snapshot order and only applies the activity scope and HideCompleted options.
            var resolved = ShowcaseWidgetProjectionService.ResolveGameSummaries(snapshot, instance);
            CollectionAssert.AreEqual(
                new[] { oldest, newest, completed, neverUnlocked },
                resolved.ToArray());

            instance.SetOption("HideCompleted", true);
            var withoutCompleted = ShowcaseWidgetProjectionService.ResolveGameSummaries(snapshot, instance);
            Assert.IsFalse(withoutCompleted.Contains(completed));
            Assert.AreEqual(3, withoutCompleted.Count);

            instance.SetOption("HideCompleted", false);
            ShowcaseWidgetOptions.SetGameActivityScope(instance, GameActivityScope.Played);
            CollectionAssert.AreEqual(
                new[] { oldest, newest, completed },
                ShowcaseWidgetProjectionService.ResolveGameSummaries(snapshot, instance).ToArray());

            ShowcaseWidgetOptions.SetGameActivityScope(instance, GameActivityScope.Unplayed);
            CollectionAssert.AreEqual(
                new[] { neverUnlocked },
                ShowcaseWidgetProjectionService.ResolveGameSummaries(snapshot, instance).ToArray());
        }

        [TestMethod]
        public void GameMosaic_SourcesFilterAndOrder()
        {
            var pinnedFirst = Guid.NewGuid();
            var pinnedSecond = Guid.NewGuid();
            var completedOld = new GameSummaryItem
            {
                PlayniteGameId = pinnedSecond,
                GameName = "Old completed",
                IsCompleted = true,
                LastUnlockUtc = new DateTime(2026, 1, 1)
            };
            var completedNew = new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = "New completed",
                IsCompleted = true,
                LastUnlockUtc = new DateTime(2026, 7, 1)
            };
            var favorite = new GameSummaryItem
            {
                PlayniteGameId = pinnedFirst,
                GameName = "A favorite",
                IsFavorite = true,
                LastUnlockUtc = new DateTime(2026, 3, 1)
            };
            var snapshot = new OverviewDataSnapshot
            {
                GameSummaries = new List<GameSummaryItem> { completedOld, completedNew, favorite }
            };
            var settings = new ShowcaseSettings();
            settings.GamePinCollections[0].GameIds = new List<Guid> { pinnedFirst, pinnedSecond };
            var instance = new ShowcaseWidgetInstanceSettings { Kind = ShowcaseWidgetKind.IconMosaic };
            ShowcaseWidgetOptions.SetMosaicContent(instance, ShowcaseMosaicContent.Games);

            var completed = ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, instance);
            CollectionAssert.AreEqual(new[] { completedNew, completedOld }, completed.ToArray());

            instance.SetOption("Source", ShowcaseGameMosaicSource.All);
            var all = ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, instance);
            Assert.AreEqual(3, all.Count);
            Assert.AreSame(completedNew, all[0]);

            instance.SetOption("Source", ShowcaseGameMosaicSource.Pinned);
            CollectionAssert.AreEqual(
                new[] { favorite, completedOld },
                ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, instance).ToArray());

            instance.SetOption("Source", ShowcaseGameMosaicSource.PlayniteFavorites);
            Assert.AreSame(favorite,
                ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, instance).Single());

            instance.SetOption("Source", ShowcaseGameMosaicSource.All);
            instance.SetOption("Count", 2);
            Assert.AreEqual(2,
                ShowcaseWidgetProjectionService.ResolveGameMosaic(snapshot, settings, instance).Count);
        }

        [TestMethod]
        public void PinRows_MaterializeMissingPinsAsPlaceholders()
        {
            var gameId = Guid.NewGuid();
            var missingGameId = Guid.NewGuid();
            var resolved = new AchievementDisplayItem
            {
                PlayniteGameId = gameId,
                ApiName = "real",
                DisplayName = "Real unlock",
                GameName = "Real game",
                Unlocked = true
            };
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem> { resolved }
            };
            var pins = new List<PinnedAchievementReference>
            {
                new PinnedAchievementReference { GameId = gameId, ApiName = "real" },
                new PinnedAchievementReference
                {
                    GameId = missingGameId,
                    ApiName = "gone",
                    LastKnownGameName = "Removed game",
                    LastKnownAchievementName = "Remembered unlock"
                }
            };

            var rows = ShowcaseWidgetProjectionService.MaterializePinRows(
                ShowcaseWidgetProjectionService.ResolvePinnedAchievements(snapshot, pins));

            Assert.AreEqual(2, rows.Count);
            Assert.AreSame(resolved, rows[0]);
            Assert.AreEqual(missingGameId, rows[1].PlayniteGameId);
            Assert.AreEqual("gone", rows[1].ApiName);
            Assert.AreEqual("Remembered unlock", rows[1].DisplayName);
            Assert.AreEqual("Removed game", rows[1].GameName);
            Assert.IsFalse(rows[1].Unlocked);

            var settings = new ShowcaseSettings();
            settings.AchievementPinCollections[0].Pins = pins;
            var pinnedGrid = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.RecentAchievements
            };
            ShowcaseWidgetOptions.SetAchievementGridSource(
                pinnedGrid,
                ShowcaseAchievementGridSource.Pinned);
            var built = ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGrid);
            Assert.AreEqual(2, built.AchievementRows.Count);
        }

        [TestMethod]
        public void PinRows_AreReferenceStableAcrossRebuildsOfTheSameSnapshot()
        {
            var gameId = Guid.NewGuid();
            var missingGameId = Guid.NewGuid();
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>
                {
                    new AchievementDisplayItem
                    {
                        PlayniteGameId = gameId,
                        ApiName = "real",
                        Unlocked = true
                    }
                }
            };
            var settings = new ShowcaseSettings();
            settings.AchievementPinCollections[0].Pins = new List<PinnedAchievementReference>
            {
                new PinnedAchievementReference { GameId = gameId, ApiName = "real" },
                new PinnedAchievementReference { GameId = missingGameId, ApiName = "gone" }
            };
            var pinnedGrid = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.RecentAchievements
            };
            ShowcaseWidgetOptions.SetAchievementGridSource(
                pinnedGrid,
                ShowcaseAchievementGridSource.Pinned);

            var first = ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGrid);
            var second = ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGrid);

            // Same snapshot + same pins: the rows (missing-pin placeholders included) must be
            // the same instances so the widgets' SameRows short-circuit holds across rebuilds.
            Assert.AreSame(first.AchievementRows, second.AchievementRows);

            // A pin edit changes the memo key, so the rows rebuild.
            settings.AchievementPinCollections[0].Pins = new List<PinnedAchievementReference>
            {
                new PinnedAchievementReference { GameId = gameId, ApiName = "real" }
            };
            var edited = ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGrid);
            Assert.AreNotSame(first.AchievementRows, edited.AchievementRows);
            Assert.AreEqual(1, edited.AchievementRows.Count);
        }

        [TestMethod]
        public void PinnedSourceWidgets_UseSelectedCollectionAndFallBackToDefault()
        {
            var defaultGameId = Guid.NewGuid();
            var selectedGameId = Guid.NewGuid();
            var defaultAchievement = new AchievementDisplayItem
            {
                PlayniteGameId = defaultGameId,
                ApiName = "default-achievement",
                DisplayName = "Default achievement",
                Unlocked = true
            };
            var selectedAchievement = new AchievementDisplayItem
            {
                PlayniteGameId = selectedGameId,
                ApiName = "selected-achievement",
                DisplayName = "Selected achievement",
                Unlocked = true
            };
            var defaultGame = new GameSummaryItem
            {
                PlayniteGameId = defaultGameId,
                GameName = "Default game",
                IsFavorite = true
            };
            var selectedGame = new GameSummaryItem
            {
                PlayniteGameId = selectedGameId,
                GameName = "Selected game"
            };
            var snapshot = new OverviewDataSnapshot
            {
                Achievements = new List<AchievementDisplayItem>
                {
                    defaultAchievement,
                    selectedAchievement
                },
                GameSummaries = new List<GameSummaryItem> { defaultGame, selectedGame }
            };
            var settings = new ShowcaseSettings();
            settings.AchievementPinCollections[0].Pins.Add(new PinnedAchievementReference
            {
                GameId = defaultGameId,
                ApiName = defaultAchievement.ApiName
            });
            settings.GamePinCollections[0].GameIds.Add(defaultGameId);
            var achievementCollection = new PinnedAchievementCollection
            {
                CollectionId = "achievement-selection",
                Name = "Selection",
                Pins = new List<PinnedAchievementReference>
                {
                    new PinnedAchievementReference
                    {
                        GameId = selectedGameId,
                        ApiName = selectedAchievement.ApiName
                    }
                }
            };
            var gameCollection = new PinnedGameCollection
            {
                CollectionId = "game-selection",
                Name = "Selection",
                GameIds = new List<Guid> { selectedGameId }
            };
            settings.AchievementPinCollections.Add(achievementCollection);
            settings.GamePinCollections.Add(gameCollection);

            var pinnedAchievements = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.RecentAchievements
            };
            ShowcaseWidgetOptions.SetAchievementGridSource(
                pinnedAchievements,
                ShowcaseAchievementGridSource.Pinned);
            ShowcaseWidgetOptions.SetPinCollectionId(
                pinnedAchievements,
                achievementCollection.CollectionId);
            var pinnedProjection = ShowcaseWidgetProjectionService.Build(
                snapshot,
                settings,
                pinnedAchievements);
            Assert.AreEqual(achievementCollection.CollectionId, pinnedProjection.ResolvedPinCollectionId);
            Assert.AreSame(selectedAchievement, pinnedProjection.AchievementRows.Single());

            var pinnedGames = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.GameSummaries
            };
            ShowcaseWidgetOptions.SetGameGridSource(pinnedGames, ShowcaseGameGridSource.Pinned);
            ShowcaseWidgetOptions.SetPinCollectionId(pinnedGames, gameCollection.CollectionId);
            Assert.AreSame(
                selectedGame,
                ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGames)
                    .Games.Single());

            var iconMosaic = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.IconMosaic
            };
            iconMosaic.SetOption("Source", ShowcaseMosaicSource.Pinned);
            ShowcaseWidgetOptions.SetPinCollectionId(iconMosaic, achievementCollection.CollectionId);
            Assert.AreSame(
                selectedAchievement,
                ShowcaseWidgetProjectionService.ResolveMosaic(snapshot, settings, iconMosaic).Single());

            var gameMosaic = new ShowcaseWidgetInstanceSettings
            {
                Kind = ShowcaseWidgetKind.IconMosaic
            };
            ShowcaseWidgetOptions.SetMosaicContent(gameMosaic, ShowcaseMosaicContent.Games);
            gameMosaic.SetOption("Source", ShowcaseGameMosaicSource.Pinned);
            ShowcaseWidgetOptions.SetPinCollectionId(gameMosaic, gameCollection.CollectionId);
            Assert.AreSame(
                selectedGame,
                ShowcaseWidgetProjectionService.ResolveGameMosaic(
                    snapshot,
                    settings,
                    gameMosaic).Single());

            ShowcaseWidgetOptions.SetPinCollectionId(pinnedAchievements, "missing-collection");
            var fallback = ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedAchievements);
            Assert.AreEqual(settings.DefaultAchievementPinCollectionId, fallback.ResolvedPinCollectionId);
            Assert.AreSame(defaultAchievement, fallback.AchievementRows.Single());

            ShowcaseWidgetOptions.SetGameGridSource(pinnedGames, ShowcaseGameGridSource.PlayniteFavorites);
            ShowcaseWidgetOptions.SetPinCollectionId(pinnedGames, gameCollection.CollectionId);
            Assert.AreSame(
                defaultGame,
                ShowcaseWidgetProjectionService.Build(snapshot, settings, pinnedGames)
                    .Games.Single());
        }

        [TestMethod]
        public void Profile_ResolvedProfilePrefersProviderIdentityWithManualOverride()
        {
            var snapshot = new OverviewDataSnapshot
            {
                CurrentUserIdentities = new List<PlayniteAchievements.Models.Friends.FriendIdentity>
                {
                    new PlayniteAchievements.Models.Friends.FriendIdentity
                    {
                        ProviderKey = "Steam",
                        DisplayName = "SteamName",
                        AvatarPath = @"C:\avatar.png"
                    }
                }
            };
            var settings = new ShowcaseSettings();

            var built = ShowcaseWidgetProjectionService.Build(
                snapshot,
                settings,
                new ShowcaseWidgetInstanceSettings
                {
                    Kind = ShowcaseWidgetKind.Profile,
                    Profile = new ShowcaseProfileSettings { Subtitle = "Completionist" }
                });

            Assert.AreEqual("SteamName", built.ResolvedProfile.DisplayName);
            Assert.AreEqual(@"C:\avatar.png", built.ResolvedProfile.AvatarPath);
            Assert.AreEqual("Completionist", built.ResolvedProfile.Subtitle);
            Assert.IsTrue(built.ResolvedProfile.FromProviderIdentity);
        }

        private static GameSummaryItem Game(
            string name,
            string provider,
            string providerKey,
            int points)
        {
            return new GameSummaryItem
            {
                PlayniteGameId = Guid.NewGuid(),
                GameName = name,
                Provider = provider,
                ProviderKey = providerKey,
                ProviderGameKey = name,
                Points = points
            };
        }
    }
}
