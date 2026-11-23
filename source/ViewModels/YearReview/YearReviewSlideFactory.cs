using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.ViewModels.YearReview
{
    /// <summary>Art the slides show that is read per review rather than per game.</summary>
    public sealed class YearReviewArt
    {
        /// <summary>A game's background art, or its cover when it has none; null for neither.</summary>
        public Func<Guid, string> BackdropOf { get; set; } = id => null;

        public string TopFriendAvatarPath { get; set; }
    }

    /// <summary>
    /// Turns a year's review into its slides: localized text, the views a slide can switch
    /// between, and the placement of every element on the 1280 x 770 slide canvas.
    /// </summary>
    public static partial class YearReviewSlideFactory
    {
        private const double MonthBarMax = 430;
        private const int MaxPlatforms = 4;
        private const int MaxFriendViews = 30;
        private const int MinRatedUnlocksForAverage = 3;

        // The runners-up in a loose row, rank 5, 4, 3, 2, each tilted a little. They sit apart,
        // so every caption stays readable; the leader is larger, to their left.
        private static readonly (double X, double Y, double Angle)[] RowSlots =
        {
            (1110, 300, 3), (945, 272, -2), (780, 290, 2), (615, 262, -3)
        };

        // Completed games tossed on a pile, in drop order.
        private static readonly (double X, double Y, double Angle)[] PileSlots =
        {
            (520, 170, -8), (700, 120, 6), (880, 190, -4), (1050, 110, 9), (600, 410, 5),
            (790, 440, -10), (960, 420, 3), (450, 450, -14), (1110, 380, -6)
        };

        public static IReadOnlyList<YearReviewSlide> Create(YearReviewData data, YearReviewArt art)
        {
            art = art ?? new YearReviewArt();
            var platforms = MergedPlatforms(data);
            var slides = new List<YearReviewSlide>();
            foreach (var kind in data.Slides)
            {
                YearReviewSlide slide = null;
                switch (kind)
                {
                    case YearReviewSlideKind.Intro:
                        slide = new YearReviewIntroSlide
                        {
                            YearText = data.Year.ToString(FormattingCulture.Current),
                            RangeText = Format("LOCPlayAch_YearReview_DateRange", MonthDay(data.FirstDay), MonthDay(data.LastDay))
                        };
                        break;
                    case YearReviewSlideKind.Numbers:
                        slide = new YearReviewNumbersSlide
                        {
                            Unlocked = data.Unlocked,
                            CollectionGained = data.CollectionGained,
                            PrestigeGained = data.PrestigeGained,
                            LevelText = Number(data.LevelAtStart) + " → " + Number(data.LevelAtEnd),
                            PrestigeLevelText = Number(data.PrestigeLevelAtStart) + " → " + Number(data.PrestigeLevelAtEnd),
                            Games = data.GamesWithUnlocks,
                            AllGroup = GroupOf(Text("LOCPlayAch_YearReview_AchievementsUnlocked"), data.Unlocks),
                            Icons = Scatter(data)
                        };
                        break;
                    case YearReviewSlideKind.Months:
                        slide = Months(data);
                        break;
                    case YearReviewSlideKind.TopGames:
                        slide = TopGames(data);
                        break;
                    case YearReviewSlideKind.Completions:
                        slide = Completions(data);
                        break;
                    case YearReviewSlideKind.Rarest:
                        slide = Rarest(data, platforms);
                        break;
                    case YearReviewSlideKind.Rarity:
                        slide = Rarity(data, platforms);
                        break;
                    case YearReviewSlideKind.Platforms:
                        slide = Platforms(data, platforms);
                        break;
                    case YearReviewSlideKind.FirstAndLatest:
                        slide = FirstAndLatest(data);
                        break;
                    case YearReviewSlideKind.Friends:
                        slide = Friends(data, art);
                        break;
                    case YearReviewSlideKind.Summary:
                        slide = Summary(data);
                        break;
                    case YearReviewSlideKind.Empty:
                        slide = new YearReviewEmptySlide
                        {
                            Text = Format("LOCPlayAch_YearReview_NoUnlocks", data.Year.ToString(FormattingCulture.Current))
                        };
                        break;
                }

                if (slide == null)
                {
                    continue;
                }

                slide.Title = TitleOf(kind);
                slide.CurrentView = slide.Views.FirstOrDefault();
                if (BackdropGame(data, kind) is Guid backdrop)
                {
                    slide.BackdropPath = art.BackdropOf(backdrop);
                }

                foreach (var view in slide.Views)
                {
                    if (ViewBackdropGame(view) is Guid game)
                    {
                        view.BackdropPath = art.BackdropOf(game);
                    }
                }

                slides.Add(slide);
            }

            return slides;
        }

        private static string TitleOf(YearReviewSlideKind kind)
        {
            switch (kind)
            {
                case YearReviewSlideKind.Numbers: return Text("LOCPlayAch_YearReview_AchievementsUnlocked");
                case YearReviewSlideKind.Months: return Text("LOCPlayAch_YearReview_ByMonth");
                case YearReviewSlideKind.TopGames: return Text("LOCPlayAch_YearReview_TopGames");
                case YearReviewSlideKind.Completions: return Text("LOCPlayAch_YearReview_GamesCompleted");
                case YearReviewSlideKind.Rarest: return Text("LOCPlayAch_YearReview_RarestUnlock");
                case YearReviewSlideKind.Rarity: return Text("LOCPlayAch_YearReview_UnlocksByRarity");
                case YearReviewSlideKind.Platforms: return Text("LOCPlayAch_YearReview_UnlocksByPlatform");
                case YearReviewSlideKind.FirstAndLatest: return Text("LOCPlayAch_YearReview_FirstAndLatest");
                case YearReviewSlideKind.Friends: return Text("LOCPlayAch_Settings_Friends");
                default: return null;
            }
        }

        /// <summary>
        /// The game whose art sits behind a slide: the slide's own game where it has one, else
        /// one of the top games, so consecutive slides change backdrop.
        /// </summary>
        public static Guid? BackdropGame(YearReviewData data, YearReviewSlideKind kind)
        {
            Guid? Top(int rank) =>
                (data.TopGames.ElementAtOrDefault(rank) ?? data.TopGames.FirstOrDefault())?.Game.GameId;

            switch (kind)
            {
                case YearReviewSlideKind.Intro:
                case YearReviewSlideKind.TopGames:
                case YearReviewSlideKind.Summary:
                    return Top(0);
                case YearReviewSlideKind.Numbers:
                    return Top(1);
                case YearReviewSlideKind.Months:
                    return Top(2);
                case YearReviewSlideKind.Completions:
                    return data.Completed.FirstOrDefault()?.GameId ?? Top(0);
                case YearReviewSlideKind.Rarest:
                    return data.Rarest?.GameId ?? Top(0);
                case YearReviewSlideKind.Rarity:
                    return Top(3);
                case YearReviewSlideKind.Platforms:
                    return Top(4);
                case YearReviewSlideKind.FirstAndLatest:
                    return data.First?.GameId ?? Top(0);
                case YearReviewSlideKind.Friends:
                    return data.Friends.SharedGameIds.FirstOrDefault() is Guid shared && shared != Guid.Empty ? shared : Top(0);
                default:
                    return null;
            }
        }

        /// <summary>The game a view is about, whose art goes behind it; null keeps the slide's art.</summary>
        private static Guid? ViewBackdropGame(YearReviewSlideView view)
        {
            switch (view)
            {
                case YearReviewTopGamesView top:
                    return top.Cards.LastOrDefault()?.GameId;
                case YearReviewFriendsView friend:
                    return friend.SharedCards.FirstOrDefault()?.GameId;
                case YearReviewRarestView rarest:
                    return rarest.Entries.FirstOrDefault()?.GameId ?? rarest.GameId;
                case YearReviewRarityView rarity:
                    return rarity.TopGameId;
                default:
                    return null;
            }
        }

        private static YearReviewMonthsSlide Months(YearReviewData data)
        {
            var culture = FormattingCulture.Current;
            var zone = TimeZoneInfo.Local;
            var byDay = data.Unlocks
                .GroupBy(unlock => DateTimeUtilities.ToLocalDay(unlock.UnlockUtc, zone))
                .ToDictionary(group => group.Key, group => group.ToList());
            var groups = Enumerable.Range(0, 12)
                .Select(index => new YearReviewUnlockGroup(
                    Format("LOCPlayAch_Format_Pair", culture.DateTimeFormat.GetMonthName(index + 1), Number(data.Months[index])),
                    data.Months[index],
                    () => Rows(data.Unlocks.Where(unlock => DateTimeUtilities.ToLocalDay(unlock.UnlockUtc, zone).Month == index + 1), true)))
                .ToList();

            YearReviewMonthsView View(string label, IReadOnlyDictionary<DateTime, int> perDay)
            {
                var months = new int[12];
                foreach (var pair in perDay)
                {
                    months[pair.Key.Month - 1] += pair.Value;
                }

                var max = Math.Max(1, months.Max());
                var peak = months.Max() > 0 ? Array.IndexOf(months, months.Max()) : -1;
                var best = perDay.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).FirstOrDefault();
                return new YearReviewMonthsView
                {
                    Label = label,
                    Months = months
                        .Select((value, index) => new YearReviewMonthBar
                        {
                            Label = culture.DateTimeFormat.GetAbbreviatedMonthName(index + 1),
                            Unlocks = value,
                            Height = Math.Max(4, value * MonthBarMax / max),
                            IsPeak = index == peak,
                            Delay = 200 + index * 60,
                            ToolTip = Format("LOCPlayAch_Format_Pair", culture.DateTimeFormat.GetMonthName(index + 1), Number(value)),
                            Group = groups[index].Count > 0 ? groups[index] : null
                        })
                        .ToList(),
                    BusiestDayText = best.Value <= 0
                        ? string.Empty
                        : Format("LOCPlayAch_YearReview_BusiestDay",
                            Format("LOCPlayAch_Format_Pair", best.Key.ToString("D", culture), Number(best.Value)))
                };
            }

            var heat = new List<YearReviewHeatCell>();
            var busiest = Math.Max(1, data.BusiestDayUnlocks);
            var offset = (int)data.FirstDay.DayOfWeek;
            for (var day = data.FirstDay; day <= data.LastDay; day = day.AddDays(1))
            {
                data.Days.TryGetValue(day, out var count);
                var column = ((day - data.FirstDay).Days + offset) / 7;
                var share = (double)count / busiest;
                heat.Add(new YearReviewHeatCell
                {
                    X = column * 10,
                    Y = (int)day.DayOfWeek * 10,
                    Level = count == 0 ? 0 : share <= 0.25 ? 1 : share <= 0.5 ? 2 : share <= 0.75 ? 3 : 4,
                    ToolTip = Format("LOCPlayAch_Format_Pair", day.ToString("D", culture), Number(count)),
                    Group = byDay.TryGetValue(day, out var dayUnlocks) ? GroupOf(day.ToString("D", culture), dayUnlocks, false) : null,
                    Delay = 1250 + column * 14
                });
            }

            var streak = data.LongestStreak;
            return new YearReviewMonthsSlide
            {
                Views = new YearReviewSlideView[]
                {
                    View(Text("LOCPlayAch_Column_Unlocks"), data.Days),
                    View(Text("LOCPlayAch_Score_Collection"), data.DayCollection),
                    View(Text("LOCPlayAch_Score_Prestige"), data.DayPrestige)
                },
                StreakDays = streak.Days,
                StreakGroup = streak.Days > 0
                    ? GroupOf(
                        Format("LOCPlayAch_YearReview_DateRange", MonthDay(streak.Start), MonthDay(streak.End)),
                        data.Unlocks.Where(unlock =>
                        {
                            var day = DateTimeUtilities.ToLocalDay(unlock.UnlockUtc, zone);
                            return day >= streak.Start && day <= streak.End;
                        }).ToList())
                    : null,
                StreakRangeText = streak.Days > 1
                    ? Format("LOCPlayAch_YearReview_DateRange", MonthDay(streak.Start), MonthDay(streak.End))
                    : MonthDay(streak.Start),
                Heat = heat
            };
        }

        private static YearReviewTopGamesSlide TopGames(YearReviewData data)
        {
            YearReviewTopGamesView View(string label, IReadOnlyList<(YearReviewGame Game, double Value, string Text)> ranked, Func<YearReviewGame, string> caption, bool percent)
            {
                if (ranked.Count == 0)
                {
                    return null;
                }

                var cards = new List<YearReviewCoverCard>();
                var runnersUp = ranked.Skip(1).Take(RowSlots.Length).ToList();
                for (var rank = runnersUp.Count; rank >= 1; rank--)
                {
                    var entry = runnersUp[rank - 1];
                    var slot = RowSlots[RowSlots.Length - rank];
                    cards.Add(Card(data, entry.Game, rank + 1, entry.Text, slot.X, slot.Y, slot.Angle, 145, 218,
                        300 + (RowSlots.Length - rank) * 160));
                }

                var lead = ranked[0];
                cards.Add(Card(data, lead.Game, 1, lead.Text, 370, 170, -4, 210, 315, 1000));
                return new YearReviewTopGamesView
                {
                    Label = label,
                    LeadValue = lead.Value,
                    LeadIsPercent = percent,
                    LeadCaption = caption(lead.Game),
                    Cards = cards
                };
            }

            IReadOnlyList<(YearReviewGame, double, string)> ByScore(IReadOnlyDictionary<Guid, int> scores) =>
                scores
                    .Where(pair => pair.Value > 0)
                    .OrderByDescending(pair => pair.Value)
                    .Take(YearReviewBuilderTopCount)
                    .Select(pair => (GameOf(data, pair.Key), (double)pair.Value, Number(pair.Value)))
                    .ToList();

            var byUnlocks = data.TopGames
                .Select(entry => (entry.Game, (double)entry.Unlocks, Number(entry.Unlocks)))
                .ToList();
            var byRarity = data.Unlocks
                .Where(unlock => unlock.GlobalPercent.HasValue)
                .GroupBy(unlock => unlock.GameId)
                .Where(group => group.Count() >= MinRatedUnlocksForAverage)
                .Select(group => (Game: GameOf(data, group.Key), Average: group.Average(unlock => unlock.GlobalPercent.Value)))
                .OrderBy(entry => entry.Average)
                .Take(YearReviewBuilderTopCount)
                .Select(entry => (entry.Game, entry.Average, PercentFormatter.Format(entry.Average, PercentDecimals(entry.Average))))
                .ToList();

            string ScoreCaption(string metric, YearReviewGame game) => Format("LOCPlayAch_YearReview_MetricInGame", metric, game.Name);

            return new YearReviewTopGamesSlide
            {
                Views = new[]
                {
                    View(Text("LOCPlayAch_Column_Unlocks"), byUnlocks, game => ScoreCaption(Text("LOCPlayAch_Column_Unlocks"), game), false),
                    View(Text("LOCPlayAch_Score_Collection"), ByScore(data.GameCollection), game => ScoreCaption(Text("LOCPlayAch_Score_Collection"), game), false),
                    View(Text("LOCPlayAch_Score_Prestige"), ByScore(data.GamePrestige), game => ScoreCaption(Text("LOCPlayAch_Score_Prestige"), game), false),
                    View(Text("LOCPlayAch_YearReview_Rarest"), byRarity, game => Format("LOCPlayAch_YearReview_AverageRateInGame", game.Name), true)
                }
                .Where(view => view != null)
                .Cast<YearReviewSlideView>()
                .ToList()
            };
        }

        private const int YearReviewBuilderTopCount = 5;

        private static YearReviewGame GameOf(YearReviewData data, Guid gameId) =>
            data.Games.TryGetValue(gameId, out var game) && game != null
                ? game
                : new YearReviewGame
                {
                    GameId = gameId,
                    Name = data.Unlocks.FirstOrDefault(unlock => unlock.GameId == gameId)?.GameName
                };

        /// <summary>
        /// The year's rarest icons strewn under the big figure in two loose rows, each at its own
        /// size and tilt.
        /// </summary>
        private static IReadOnlyList<YearReviewIcon> Scatter(YearReviewData data)
        {
            var spots = new (double X, double Y, double Size, double Angle)[]
            {
                (70, 500, 96, -8), (186, 530, 78, 6), (284, 492, 88, -3), (392, 540, 72, 10), (484, 500, 92, -6),
                (110, 628, 74, 5), (204, 640, 86, -10), (318, 620, 70, 4), (412, 636, 82, -4), (520, 616, 76, 8)
            };

            return (data.Highlights ?? Array.Empty<YearReviewUnlock>())
                .Take(spots.Length)
                .Select((unlock, index) => new YearReviewIcon
                {
                    IconPath = unlock.IconPath,
                    GameId = unlock.GameId,
                    Group = GameGroup(data, unlock.GameId, unlock.GameName),
                    ToolTip = Format("LOCPlayAch_Format_Pair", unlock.AchievementName, unlock.GameName),
                    X = spots[index].X,
                    Y = spots[index].Y,
                    Size = spots[index].Size,
                    Angle = spots[index].Angle,
                    Delay = 1500 + index * 90
                })
                .ToList();
        }

        private static YearReviewCompletionsSlide Completions(YearReviewData data)
        {
            var cards = data.Completed
                .Take(PileSlots.Length)
                .Select((game, index) =>
                {
                    var slot = PileSlots[index];
                    var card = Card(data, game, 0, null, slot.X, slot.Y, slot.Angle, 140, 210, 250 + index * 120);
                    card.Badge = game.Badge;
                    card.ShowBadge = true;
                    card.BadgeDelay = 650 + index * 120;
                    return card;
                })
                .ToList();

            var lines = new[]
                {
                    (CompletionBadgeKind.Platinum, "LOCPlayAch_Trophy_Platinum"),
                    (CompletionBadgeKind.Mythic, "LOCPlayAch_Badge_Mythic"),
                    (CompletionBadgeKind.Completed, "LOCPlayAch_Completed")
                }
                .Select(kind => (Count: data.Completed.Count(game => game.Badge == kind.Item1), Key: kind.Item2))
                .Where(line => line.Count > 0)
                .Select(line => Number(line.Count) + " " + Text(line.Key));

            var finished = new HashSet<Guid>(data.Completed.Select(game => game.GameId));
            return new YearReviewCompletionsSlide
            {
                Count = data.Completed.Count,
                BreakdownText = string.Join(Environment.NewLine, lines),
                AllGroup = GroupOf(Text("LOCPlayAch_YearReview_GamesCompleted"), data.Unlocks.Where(unlock => finished.Contains(unlock.GameId)).ToList()),
                Cards = cards
            };
        }

        private static YearReviewFirstAndLatestSlide FirstAndLatest(YearReviewData data)
        {
            string Detail(YearReviewUnlock unlock)
            {
                var local = DateTimeUtilities.AsLocalFromUtc(unlock.UnlockUtc);
                return Format("LOCPlayAch_Format_Pair",
                    unlock.GameName,
                    MonthDay(local) + " " + local.ToString("t", FormattingCulture.Current));
            }

            return new YearReviewFirstAndLatestSlide
            {
                FirstLabel = Format("LOCPlayAch_YearReview_FirstUnlockOfYear", data.Year.ToString(FormattingCulture.Current)),
                FirstName = data.First.AchievementName,
                FirstIconPath = data.First.IconPath,
                FirstGroup = GameGroup(data, data.First.GameId, data.First.GameName),
                FirstDetail = Detail(data.First),
                LatestName = data.Latest.AchievementName,
                LatestIconPath = data.Latest.IconPath,
                LatestGroup = GameGroup(data, data.Latest.GameId, data.Latest.GameName),
                LatestDetail = Detail(data.Latest),
                DaysApart = (DateTimeUtilities.AsLocalFromUtc(data.Latest.UnlockUtc).Date
                             - DateTimeUtilities.AsLocalFromUtc(data.First.UnlockUtc).Date).Days
            };
        }

        private static YearReviewFriendsSlide Friends(YearReviewData data, YearReviewArt art)
        {
            IReadOnlyList<YearReviewCoverCard> Shared(IReadOnlyList<Guid> gameIds) =>
                gameIds
                    .Take(3)
                    .Select((id, index) =>
                    {
                        var card = Card(data, GameOf(data, id), 0, null, 760 + index * 110, 240 + index % 2 * 30,
                            new[] { -10.0, 4, 14 }[index], 130, 195, 800 + index * 120);
                        card.ShowCaption = false;
                        return card;
                    })
                    .ToList();

            YearReviewUnlockGroup Keyed(string label, IReadOnlyCollection<string> keys) =>
                GroupOf(label, data.Unlocks.Where(unlock => unlock.Key != null && keys.Contains(unlock.Key)).ToList());

            YearReviewUnlockGroup SharedGames(string name, IReadOnlyList<Guid> gameIds)
            {
                var games = new HashSet<Guid>(gameIds);
                return GroupOf(name, data.Unlocks.Where(unlock => games.Contains(unlock.GameId)).ToList());
            }

            string Initial(string name) =>
                string.IsNullOrEmpty(name) ? string.Empty : name.Substring(0, 1).ToUpper(FormattingCulture.Current);

            var friends = data.Friends;
            var topName = friends.TopFriendName ?? string.Empty;
            var views = new List<YearReviewSlideView>
            {
                new YearReviewFriendsView
                {
                    Label = Text("LOCPlayAch_Common_All"),
                    BigNumber = friends.UnlockedFirst,
                    BigLabel = Text("LOCPlayAch_YearReview_UnlockedFirst"),
                    BigGroup = Keyed(Text("LOCPlayAch_YearReview_UnlockedFirst"), friends.UnlockedFirstKeys),
                    SharedGroup = SharedGames(topName, friends.SharedGameIds),
                    HasFriend = friends.SharedGameIds.Count > 0,
                    FriendKey = friends.TopFriendKey,
                    FriendName = topName,
                    FriendInitial = Initial(topName),
                    AvatarPath = art.TopFriendAvatarPath,
                    AvatarRequested = true,
                    InCommonText = Format("LOCPlayAch_YearReview_GamesInCommon", Number(friends.SharedGameIds.Count)),
                    SharedCards = Shared(friends.SharedGameIds)
                }
            };

            foreach (var friend in friends.PerFriend.Take(MaxFriendViews))
            {
                var name = friend.Name ?? string.Empty;
                views.Add(new YearReviewFriendsView
                {
                    Label = name,
                    FriendKey = friend.Key,
                    BigNumber = friend.UnlockedFirst,
                    BigLabel = Format("LOCPlayAch_YearReview_UnlockedBeforeFriend", name),
                    BigGroup = Keyed(Format("LOCPlayAch_YearReview_UnlockedBeforeFriend", name), friend.UnlockedFirstKeys),
                    HasSecond = true,
                    SecondNumber = friend.TheyFirst,
                    SecondLabel = Format("LOCPlayAch_YearReview_FriendUnlockedFirst", name),
                    SecondGroup = Keyed(Format("LOCPlayAch_YearReview_FriendUnlockedFirst", name), friend.TheyFirstKeys),
                    SharedGroup = SharedGames(name, friend.SharedGameIds),
                    HasFriend = true,
                    FriendName = name,
                    FriendInitial = Initial(name),
                    InCommonText = Format("LOCPlayAch_YearReview_GamesInCommon", Number(friend.SharedGameIds.Count)),
                    SharedCards = Shared(friend.SharedGameIds)
                });
            }

            return new YearReviewFriendsSlide { Views = views };
        }

        private static YearReviewSummarySlide Summary(YearReviewData data)
        {
            var figures = new List<KeyValuePair<string, string>>
            {
                Figure(Number(data.Unlocked), "LOCPlayAch_Achievements"),
                Figure("+" + Number(data.CollectionGained), "LOCPlayAch_Score_Collection"),
                Figure("+" + Number(data.PrestigeGained), "LOCPlayAch_Score_Prestige"),
                Figure(Number(data.Completed.Count), "LOCPlayAch_YearReview_GamesCompleted"),
                Figure(Format("LOCPlayAch_Showcase_Days", Number(data.LongestStreak.Days)), "LOCPlayAch_Showcase_Stat_LongestStreak")
            };

            if (data.Rarest?.GlobalPercent is double percent)
            {
                figures.Add(Figure(PercentFormatter.Format(percent, PercentDecimals(percent)), "LOCPlayAch_YearReview_RarestUnlock"));
            }

            var top = data.TopGames.FirstOrDefault()?.Game;
            if (top != null)
            {
                figures.Add(Figure(top.Name, "LOCPlayAch_YearReview_TopGame"));
            }

            var summary = new YearReviewSummarySlide
            {
                YearText = data.Year.ToString(FormattingCulture.Current),
                TopGameName = top?.Name,
                TopCoverPath = top?.CoverPath,
                Figures = figures,
                TopFigures = figures.Take(4).ToList(),
                Covers = data.TopGames.Select(entry => entry.Game)
                    .Concat(data.Completed)
                    .GroupBy(game => game.GameId)
                    .Select(group => group.First().CoverPath)
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .Take(6)
                    .ToList()
            };
            var fan = summary.Covers.Take(3).ToList();
            var fanSlots = new[] { (X: 0.0, Y: 34.0, Angle: -4.0), (X: 16.0, Y: 14.0, Angle: 3.0), (X: 30.0, Y: 0.0, Angle: 8.0) };
            summary.FanCovers = fan
                .Select((path, index) => new YearReviewFanCover
                {
                    Path = path,
                    X = fanSlots[index].X,
                    Y = fanSlots[index].Y,
                    Angle = fanSlots[index].Angle
                })
                .Reverse()
                .ToList();
            summary.Views = new YearReviewSlideView[]
            {
                new YearReviewClassicCard { Label = Text("LOCPlayAch_YearReview_CardClassic"), Card = summary },
                new YearReviewPosterCard { Label = Text("LOCPlayAch_YearReview_CardPoster"), Card = summary },
                new YearReviewBannerCard { Label = Text("LOCPlayAch_YearReview_CardBanner"), Card = summary },
                new YearReviewMosaicCard { Label = Text("LOCPlayAch_Showcase_Widget_IconMosaic"), Card = summary }
            };
            return summary;
        }
    }
}
