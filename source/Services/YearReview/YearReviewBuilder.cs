using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Overview;

namespace PlayniteAchievements.Services.YearReview
{
    /// <summary>
    /// Builds a calendar year's review from plain inputs. Pure: every read happens at the
    /// boundary (<see cref="YearReviewReader"/>), so the same inputs always give the same review.
    /// </summary>
    public static class YearReviewBuilder
    {
        public const int TopGameCount = 5;
        public const int HighlightCount = 10;

        /// <summary>Every year with at least one dated unlock, newest first.</summary>
        public static IReadOnlyList<int> AvailableYears(IEnumerable<DateTime> unlockDays) =>
            (unlockDays ?? Enumerable.Empty<DateTime>())
                .Select(day => day.Year)
                .Distinct()
                .OrderByDescending(year => year)
                .ToList();

        public static YearReviewData Build(int year, YearReviewInputs inputs)
        {
            inputs = inputs ?? new YearReviewInputs();
            var zone = inputs.Zone ?? TimeZoneInfo.Local;
            var firstDay = new DateTime(year, 1, 1);
            var yearEnd = new DateTime(year, 12, 31);
            var today = inputs.Today.Date;
            var data = new YearReviewData
            {
                Year = year,
                FirstDay = firstDay,
                LastDay = today >= firstDay && today < yearEnd ? today : yearEnd
            };

            DateTime DayOf(DateTime utc) => DateTimeUtilities.ToLocalDay(utc, zone);
            bool InYear(DateTime day) => day >= firstDay && day <= yearEnd;

            var unlocks = (inputs.Unlocks ?? Array.Empty<YearReviewUnlock>())
                .Where(unlock => unlock != null && InYear(DayOf(unlock.UnlockUtc)))
                .OrderBy(unlock => unlock.UnlockUtc)
                .ToList();
            var gamesById = (inputs.Games ?? Array.Empty<YearReviewGame>())
                .Where(game => game != null)
                .GroupBy(game => game.GameId)
                .ToDictionary(group => group.Key, group => group.First());

            data.Unlocked = unlocks.Count;
            data.GamesWithUnlocks = unlocks.Select(unlock => unlock.GameId).Distinct().Count();

            ApplyDays(data, unlocks.Select(unlock => DayOf(unlock.UnlockUtc)).ToList());
            ApplyScore(data, inputs, firstDay, yearEnd);
            data.TopGames = TopGames(unlocks, gamesById);
            data.Completed = gamesById.Values
                .Where(game => game.FinishUtc.HasValue && InYear(DayOf(game.FinishUtc.Value)))
                .OrderBy(game => game.FinishUtc.Value)
                .ToList();

            data.Rarest = unlocks
                .Where(unlock => unlock.GlobalPercent.HasValue)
                .OrderBy(unlock => unlock.GlobalPercent.Value)
                .ThenBy(unlock => unlock.UnlockUtc)
                .FirstOrDefault();
            data.Highlights = unlocks
                .Where(unlock => !string.IsNullOrWhiteSpace(unlock.IconPath))
                .OrderBy(unlock => unlock.GlobalPercent ?? 100)
                .ThenBy(unlock => unlock.UnlockUtc)
                .Take(HighlightCount)
                .ToList();
            data.First = unlocks.FirstOrDefault();
            data.Latest = unlocks.LastOrDefault();

            data.Rarities = Enum.GetValues(typeof(RarityTier))
                .Cast<RarityTier>()
                .ToDictionary(tier => tier, tier => unlocks.Count(unlock => unlock.Rarity == tier));
            data.Platforms = unlocks
                .GroupBy(unlock => unlock.ProviderKey ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => new YearReviewPlatformCount(group.First().ProviderKey, group.Count()))
                .OrderByDescending(platform => platform.Unlocks)
                .ThenBy(platform => platform.ProviderKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            data.Friends = Friends(inputs.FriendUnlocks, unlocks, DayOf, InYear);
            data.Slides = Slides(data);
            return data;
        }

        private static void ApplyDays(YearReviewData data, IReadOnlyList<DateTime> days)
        {
            var counts = days
                .GroupBy(day => day)
                .ToDictionary(group => group.Key, group => group.Count());
            data.Days = counts;

            var months = new int[12];
            foreach (var pair in counts)
            {
                months[pair.Key.Month - 1] += pair.Value;
            }

            data.Months = months;
            data.BusiestMonth = counts.Count == 0 ? -1 : Array.IndexOf(months, months.Max());

            if (counts.Count > 0)
            {
                var busiest = counts.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First();
                data.BusiestDay = busiest.Key;
                data.BusiestDayUnlocks = busiest.Value;
            }

            data.LongestStreak = UnlockStreaks.Longest(counts.Keys);
        }

        /// <summary>
        /// Collection score earned in the year, and the level before and after it. Unlocks
        /// without a date count toward the starting score, as in the score history chart.
        /// </summary>
        private static void ApplyScore(YearReviewData data, YearReviewInputs inputs, DateTime firstDay, DateTime yearEnd)
        {
            var before = 0;
            var during = 0;
            foreach (var fact in inputs.Facts ?? Array.Empty<UnlockFact>())
            {
                if (fact == null)
                {
                    continue;
                }

                if (!fact.Day.HasValue || fact.Day.Value < firstDay)
                {
                    before += fact.CollectionScore;
                }
                else if (fact.Day.Value <= yearEnd)
                {
                    during += fact.CollectionScore;
                }
            }

            data.ScoreGained = during;
            data.LevelAtStart = DisplayLevel(before);
            data.LevelAtEnd = DisplayLevel(before + during);
        }

        private static int DisplayLevel(int score)
        {
            var level = AchievementLevelCalculator.CalculateModern(score);
            return level == null ? 0 : level.DisplayLevel > 0 ? level.DisplayLevel : level.Level;
        }

        private static IReadOnlyList<YearReviewGameCount> TopGames(
            IReadOnlyList<YearReviewUnlock> unlocks,
            IReadOnlyDictionary<Guid, YearReviewGame> gamesById)
        {
            return unlocks
                .GroupBy(unlock => unlock.GameId)
                .Select(group =>
                {
                    if (!gamesById.TryGetValue(group.Key, out var game))
                    {
                        game = new YearReviewGame { GameId = group.Key, Name = group.First().GameName };
                    }

                    return new YearReviewGameCount(game, group.Count());
                })
                .OrderByDescending(entry => entry.Unlocks)
                .ThenBy(entry => entry.Game.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(TopGameCount)
                .ToList();
        }

        private static YearReviewFriendSummary Friends(
            IReadOnlyList<YearReviewFriendUnlock> friendUnlocks,
            IReadOnlyList<YearReviewUnlock> unlocks,
            Func<DateTime, DateTime> dayOf,
            Func<DateTime, bool> inYear)
        {
            var rows = (friendUnlocks ?? Array.Empty<YearReviewFriendUnlock>()).Where(row => row != null).ToList();
            if (rows.Count == 0)
            {
                return new YearReviewFriendSummary();
            }

            var unlockedFirst = rows
                .Where(row => row.SelfUnlockUtc.HasValue
                              && inYear(dayOf(row.SelfUnlockUtc.Value))
                              && row.SelfUnlockUtc.Value < row.FriendUnlockUtc
                              && !string.IsNullOrEmpty(row.AchievementKey))
                .Select(row => row.AchievementKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            var myGames = new HashSet<Guid>(unlocks.Select(unlock => unlock.GameId));
            var top = rows
                .Where(row => row.GameId.HasValue
                              && myGames.Contains(row.GameId.Value)
                              && inYear(dayOf(row.FriendUnlockUtc))
                              && !string.IsNullOrEmpty(row.FriendKey))
                .GroupBy(row => row.FriendKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Key = group.Key,
                    Name = group.Select(row => row.FriendName).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)),
                    Games = group.Select(row => row.GameId.Value).Distinct().ToList()
                })
                .OrderByDescending(friend => friend.Games.Count)
                .ThenBy(friend => friend.Name, StringComparer.CurrentCultureIgnoreCase)
                .FirstOrDefault();

            return new YearReviewFriendSummary
            {
                UnlockedFirst = unlockedFirst,
                TopFriendKey = top?.Key,
                TopFriendName = top?.Name,
                SharedGameIds = top?.Games ?? (IReadOnlyList<Guid>)Array.Empty<Guid>()
            };
        }

        /// <summary>The slides with something to show, in order.</summary>
        public static IReadOnlyList<YearReviewSlideKind> Slides(YearReviewData data)
        {
            if (data == null || data.Unlocked == 0)
            {
                return new[] { YearReviewSlideKind.Empty };
            }

            var slides = new List<YearReviewSlideKind>
            {
                YearReviewSlideKind.Intro,
                YearReviewSlideKind.Numbers,
                YearReviewSlideKind.Months,
                YearReviewSlideKind.TopGames
            };

            if (data.Completed.Count > 0)
            {
                slides.Add(YearReviewSlideKind.Completions);
            }

            if (data.Rarest != null)
            {
                slides.Add(YearReviewSlideKind.Rarest);
            }

            slides.Add(YearReviewSlideKind.Rarity);
            if (data.Platforms.Count > 1)
            {
                slides.Add(YearReviewSlideKind.Platforms);
            }

            if (data.Unlocked > 1)
            {
                slides.Add(YearReviewSlideKind.FirstAndLatest);
            }

            if (data.Friends.HasData)
            {
                slides.Add(YearReviewSlideKind.Friends);
            }

            slides.Add(YearReviewSlideKind.Summary);
            return slides;
        }
    }
}
