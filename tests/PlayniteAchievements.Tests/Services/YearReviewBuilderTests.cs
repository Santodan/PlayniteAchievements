using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.YearReview;

namespace PlayniteAchievements.Tests.Services
{
    /// <summary>Covers how a calendar year's review is computed from unlocks, facts, games and friend unlocks.</summary>
    [TestClass]
    public class YearReviewBuilderTests
    {
        private static readonly TimeZoneInfo PlusTen =
            TimeZoneInfo.CreateCustomTimeZone("YearReviewTests+10", TimeSpan.FromHours(10), "+10", "+10");

        private static readonly Guid GameA = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        private static readonly Guid GameB = new Guid("bbbbbbbb-0000-0000-0000-000000000002");

        private static YearReviewUnlock Unlock(Guid game, DateTime utc, double? percent = null, string provider = "Steam",
            RarityTier tier = RarityTier.Common, string name = null) =>
            new YearReviewUnlock
            {
                GameId = game,
                GameName = game == GameA ? "Alpha" : "Beta",
                AchievementName = name ?? "Achievement " + utc.Ticks,
                UnlockUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc),
                GlobalPercent = percent,
                Rarity = tier,
                ProviderKey = provider
            };

        private static YearReviewInputs Inputs(params YearReviewUnlock[] unlocks) =>
            new YearReviewInputs { Unlocks = unlocks, Today = new DateTime(2026, 11, 23), Zone = TimeZoneInfo.Utc };

        [TestMethod]
        public void LongestStreak_IsTheEarliestOfTiedRuns()
        {
            var days = new[]
            {
                new DateTime(2026, 3, 1), new DateTime(2026, 3, 2), new DateTime(2026, 3, 3),
                new DateTime(2026, 5, 10), new DateTime(2026, 5, 11), new DateTime(2026, 5, 12),
                new DateTime(2026, 7, 4)
            };

            var streak = UnlockStreaks.Longest(days);

            Assert.AreEqual(3, streak.Days);
            Assert.AreEqual(new DateTime(2026, 3, 1), streak.Start);
            Assert.AreEqual(new DateTime(2026, 3, 3), streak.End);
            Assert.AreEqual(0, UnlockStreaks.Longest(Array.Empty<DateTime>()).Days);
        }

        [TestMethod]
        public void CurrentStreak_StillCountsUntilTheDayItWouldBreak()
        {
            var days = new[] { new DateTime(2026, 11, 20), new DateTime(2026, 11, 21), new DateTime(2026, 11, 22) };

            Assert.AreEqual(3, UnlockStreaks.Current(days, new DateTime(2026, 11, 23)));
            Assert.AreEqual(0, UnlockStreaks.Current(days, new DateTime(2026, 11, 24)));
        }

        [TestMethod]
        public void Year_IsReadInTheLocalZone()
        {
            // 15:00 UTC on 31 December is already 1 January at +10.
            var inputs = Inputs(Unlock(GameA, new DateTime(2025, 12, 31, 15, 0, 0)));
            inputs.Zone = PlusTen;

            Assert.AreEqual(1, YearReviewBuilder.Build(2026, inputs).Unlocked);
            Assert.AreEqual(0, YearReviewBuilder.Build(2025, inputs).Unlocked);
        }

        [TestMethod]
        public void EmptyYear_ShowsOnlyTheEmptySlide()
        {
            var review = YearReviewBuilder.Build(2024, Inputs(Unlock(GameA, new DateTime(2026, 2, 1))));

            Assert.AreEqual(0, review.Unlocked);
            CollectionAssert.AreEqual(new[] { YearReviewSlideKind.Empty }, review.Slides.ToArray());
            Assert.AreEqual(-1, review.BusiestMonth);
        }

        [TestMethod]
        public void Months_BusiestDay_AndStreak_StayInsideTheYear()
        {
            var review = YearReviewBuilder.Build(2026, Inputs(
                Unlock(GameA, new DateTime(2025, 12, 30, 12, 0, 0)),
                Unlock(GameA, new DateTime(2025, 12, 31, 12, 0, 0)),
                Unlock(GameA, new DateTime(2026, 1, 1, 12, 0, 0)),
                Unlock(GameA, new DateTime(2026, 1, 2, 12, 0, 0)),
                Unlock(GameB, new DateTime(2026, 8, 15, 10, 0, 0)),
                Unlock(GameB, new DateTime(2026, 8, 15, 11, 0, 0)),
                Unlock(GameB, new DateTime(2026, 8, 15, 12, 0, 0))));

            Assert.AreEqual(5, review.Unlocked);
            Assert.AreEqual(2, review.Months[0]);
            Assert.AreEqual(3, review.Months[7]);
            Assert.AreEqual(7, review.BusiestMonth);
            Assert.AreEqual(new DateTime(2026, 8, 15), review.BusiestDay);
            Assert.AreEqual(3, review.BusiestDayUnlocks);
            Assert.AreEqual(2, review.LongestStreak.Days);
            Assert.AreEqual(new DateTime(2026, 1, 1), review.LongestStreak.Start);
            Assert.AreEqual(new DateTime(2026, 11, 23), review.LastDay);
        }

        [TestMethod]
        public void Rarest_IsTheLowestKnownPercent_EarliestOnATie()
        {
            var review = YearReviewBuilder.Build(2026, Inputs(
                Unlock(GameA, new DateTime(2026, 3, 1), null, name: "No data"),
                Unlock(GameA, new DateTime(2026, 4, 1), 0.4, name: "Later tie"),
                Unlock(GameA, new DateTime(2026, 2, 1), 0.4, name: "Earlier tie"),
                Unlock(GameA, new DateTime(2026, 1, 5), 12.5, name: "Common")));

            Assert.AreEqual("Earlier tie", review.Rarest.AchievementName);
            Assert.AreEqual("Common", review.First.AchievementName);
            Assert.AreEqual("Later tie", review.Latest.AchievementName);
        }

        [TestMethod]
        public void Highlights_AreTheRarestUnlocksWithAnIcon()
        {
            var withIcon = Unlock(GameA, new DateTime(2026, 3, 1), 2.0, name: "Rare");
            withIcon.IconPath = "rare.png";
            var noIcon = Unlock(GameA, new DateTime(2026, 3, 2), 0.1, name: "No icon");
            var common = Unlock(GameA, new DateTime(2026, 3, 3), 50, name: "Common");
            common.IconPath = "common.png";

            var review = YearReviewBuilder.Build(2026, Inputs(common, noIcon, withIcon));

            CollectionAssert.AreEqual(new[] { "Rare", "Common" }, review.Highlights.Select(unlock => unlock.AchievementName).ToArray());
        }

        [TestMethod]
        public void Completions_CountOnlyGamesFinishedInTheYear()
        {
            var inputs = Inputs(Unlock(GameA, new DateTime(2026, 6, 1)));
            inputs.Games = new[]
            {
                new YearReviewGame { GameId = GameA, Name = "Alpha", FinishUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), Badge = CompletionBadgeKind.Platinum },
                new YearReviewGame { GameId = GameB, Name = "Beta", FinishUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
                new YearReviewGame { GameId = Guid.NewGuid(), Name = "Unfinished" }
            };

            var review = YearReviewBuilder.Build(2026, inputs);

            Assert.AreEqual(1, review.Completed.Count);
            Assert.AreEqual(GameA, review.Completed[0].GameId);
            Assert.IsTrue(review.Slides.Contains(YearReviewSlideKind.Completions));
        }

        [TestMethod]
        public void Score_CountsTheYearsFacts_WithEarlierAndUndatedOnesAsTheStart()
        {
            var inputs = Inputs(Unlock(GameA, new DateTime(2026, 6, 1)));
            inputs.Facts = new[]
            {
                new UnlockFact { GameId = GameA, Day = null, CollectionScore = 100 },
                new UnlockFact { GameId = GameA, Day = new DateTime(2025, 5, 1), CollectionScore = 200 },
                new UnlockFact { GameId = GameA, Day = new DateTime(2026, 6, 1), CollectionScore = 50, PrestigeScore = 70 },
                new UnlockFact { GameId = GameA, Day = new DateTime(2027, 1, 1), CollectionScore = 999 }
            };

            var review = YearReviewBuilder.Build(2026, inputs);

            Assert.AreEqual(50, review.CollectionGained);
            Assert.AreEqual(50, review.DayCollection[new DateTime(2026, 6, 1)]);
            Assert.AreEqual(70, review.GamePrestige[GameA]);
            Assert.AreEqual(70, review.PrestigeGained);
            Assert.IsTrue(review.LevelAtEnd >= review.LevelAtStart);
            Assert.IsTrue(review.PrestigeLevelAtEnd >= review.PrestigeLevelAtStart);
        }

        [TestMethod]
        public void Friends_CountAchievementsUnlockedFirstOnce_AndPickTheFriendWithMostSharedGames()
        {
            var inputs = Inputs(
                Unlock(GameA, new DateTime(2026, 2, 1)),
                Unlock(GameB, new DateTime(2026, 3, 1)));
            var self = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            inputs.FriendUnlocks = new[]
            {
                // Both friends unlocked "a1" after the user: one achievement unlocked first.
                new YearReviewFriendUnlock { FriendKey = "s|1", FriendName = "Mara", GameId = GameA, AchievementKey = "a1", SelfUnlockUtc = self, FriendUnlockUtc = self.AddDays(3) },
                new YearReviewFriendUnlock { FriendKey = "s|2", FriendName = "Ode", GameId = GameA, AchievementKey = "a1", SelfUnlockUtc = self, FriendUnlockUtc = self.AddDays(9) },
                // The friend was first here.
                new YearReviewFriendUnlock { FriendKey = "s|2", FriendName = "Ode", GameId = GameA, AchievementKey = "a2", SelfUnlockUtc = self, FriendUnlockUtc = self.AddDays(-1) },
                // The user's unlock was in another year.
                new YearReviewFriendUnlock { FriendKey = "s|2", FriendName = "Ode", GameId = GameA, AchievementKey = "a3", SelfUnlockUtc = self.AddYears(-1), FriendUnlockUtc = self },
                new YearReviewFriendUnlock { FriendKey = "s|1", FriendName = "Mara", GameId = GameB, AchievementKey = "b1", FriendUnlockUtc = self.AddDays(40) }
            };

            var review = YearReviewBuilder.Build(2026, inputs);

            Assert.AreEqual(1, review.Friends.UnlockedFirst);
            Assert.AreEqual("Mara", review.Friends.TopFriendName);

            var ode = review.Friends.PerFriend.Single(friend => friend.Name == "Ode");
            Assert.AreEqual(1, ode.UnlockedFirst);
            Assert.AreEqual(1, ode.TheyFirst);
            CollectionAssert.Contains(ode.UnlockedFirstKeys.ToList(), "a1");
            CollectionAssert.Contains(ode.TheyFirstKeys.ToList(), "a2");
            Assert.AreEqual("Mara", review.Friends.PerFriend[0].Name);
            Assert.AreEqual(2, review.Friends.SharedGameIds.Count);
            Assert.IsTrue(review.Slides.Contains(YearReviewSlideKind.Friends));
        }

        [TestMethod]
        public void Slides_SkipPlatformsAndFriendsWithoutData()
        {
            var review = YearReviewBuilder.Build(2026, Inputs(
                Unlock(GameA, new DateTime(2026, 2, 1)),
                Unlock(GameA, new DateTime(2026, 2, 2))));

            Assert.IsFalse(review.Slides.Contains(YearReviewSlideKind.Platforms));
            Assert.IsFalse(review.Slides.Contains(YearReviewSlideKind.Friends));
            Assert.IsFalse(review.Slides.Contains(YearReviewSlideKind.Rarest));
            Assert.AreEqual(YearReviewSlideKind.Intro, review.Slides.First());
            Assert.AreEqual(YearReviewSlideKind.Summary, review.Slides.Last());
        }

        [TestMethod]
        public void AvailableYears_AreDistinct_NewestFirst()
        {
            var years = YearReviewBuilder.AvailableYears(new[]
            {
                new DateTime(2024, 1, 1), new DateTime(2026, 5, 5), new DateTime(2024, 9, 9)
            });

            CollectionAssert.AreEqual(new[] { 2026, 2024 }, years.ToArray());
        }
    }
}
