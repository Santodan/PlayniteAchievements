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
        /// <summary>Each backdrop game's background art, or its cover when it has none.</summary>
        public IReadOnlyDictionary<Guid, string> Backdrops { get; set; } = new Dictionary<Guid, string>();

        public string TopFriendAvatarPath { get; set; }
    }

    /// <summary>
    /// Turns a year's review into its slides: localized text and the placement of every element
    /// on the 1280 x 770 slide canvas. Placements are fixed per slide and scale with the data.
    /// </summary>
    public static class YearReviewSlideFactory
    {
        private const double MonthBarMax = 430;
        private const int MaxPlatforms = 4;

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

        // Bubble centres by tier: common, uncommon, rare, ultra rare.
        private static readonly Point[] BubbleCentres =
        {
            new Point(500, 440), new Point(830, 290), new Point(900, 570), new Point(1120, 410)
        };

        private static readonly double[] PlatformIndents = { 72, 118, 84, 62, 100 };

        public static IReadOnlyList<YearReviewSlide> Create(YearReviewData data, YearReviewArt art)
        {
            art = art ?? new YearReviewArt();
            var gamesById = data.TopGames.Select(entry => entry.Game)
                .Concat(data.Completed)
                .GroupBy(game => game.GameId)
                .ToDictionary(group => group.Key, group => group.First());

            var slides = new List<YearReviewSlide>();
            foreach (var kind in data.Slides)
            {
                var before = slides.Count;
                switch (kind)
                {
                    case YearReviewSlideKind.Intro:
                        slides.Add(new YearReviewIntroSlide
                        {
                            YearText = data.Year.ToString(FormattingCulture.Current),
                            RangeText = Format("LOCPlayAch_YearReview_DateRange", MonthDay(data.FirstDay), MonthDay(data.LastDay))
                        });
                        break;
                    case YearReviewSlideKind.Numbers:
                        slides.Add(new YearReviewNumbersSlide
                        {
                            Unlocked = data.Unlocked,
                            ScoreGained = data.ScoreGained,
                            LevelText = Number(data.LevelAtStart) + " → " + Number(data.LevelAtEnd),
                            Games = data.GamesWithUnlocks,
                            Icons = Scatter(data.Highlights)
                        });
                        break;
                    case YearReviewSlideKind.Months:
                        slides.Add(Months(data));
                        break;
                    case YearReviewSlideKind.TopGames:
                        slides.Add(TopGames(data));
                        break;
                    case YearReviewSlideKind.Completions:
                        slides.Add(Completions(data));
                        break;
                    case YearReviewSlideKind.Rarest:
                        slides.Add(Rarest(data));
                        break;
                    case YearReviewSlideKind.Rarity:
                        slides.Add(Rarity(data));
                        break;
                    case YearReviewSlideKind.Platforms:
                        if (Platforms(data) is YearReviewPlatformsSlide platforms)
                        {
                            slides.Add(platforms);
                        }

                        break;
                    case YearReviewSlideKind.FirstAndLatest:
                        slides.Add(FirstAndLatest(data));
                        break;
                    case YearReviewSlideKind.Friends:
                        slides.Add(Friends(data, art, gamesById));
                        break;
                    case YearReviewSlideKind.Summary:
                        slides.Add(Summary(data));
                        break;
                    case YearReviewSlideKind.Empty:
                        slides.Add(new YearReviewEmptySlide
                        {
                            Text = Format("LOCPlayAch_YearReview_NoUnlocks", data.Year.ToString(FormattingCulture.Current))
                        });
                        break;
                }

                if (slides.Count > before && BackdropGame(data, kind) is Guid backdrop
                    && art.Backdrops.TryGetValue(backdrop, out var path))
                {
                    slides[slides.Count - 1].BackdropPath = path;
                }
            }

            return slides;
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

        private static YearReviewMonthsSlide Months(YearReviewData data)
        {
            var culture = FormattingCulture.Current;
            var max = Math.Max(1, data.Months.Max());
            var bars = data.Months
                .Select((count, index) => new YearReviewMonthBar
                {
                    Label = culture.DateTimeFormat.GetAbbreviatedMonthName(index + 1),
                    Unlocks = count,
                    Height = Math.Max(4, count * MonthBarMax / max),
                    IsPeak = index == data.BusiestMonth,
                    Delay = 200 + index * 60,
                    ToolTip = Format("LOCPlayAch_YearReview_GameAndDate", culture.DateTimeFormat.GetMonthName(index + 1), Number(count))
                })
                .ToList();

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
                    ToolTip = Format("LOCPlayAch_YearReview_GameAndDate", day.ToString("D", culture), Number(count)),
                    Delay = 1250 + column * 14
                });
            }

            var streak = data.LongestStreak;
            return new YearReviewMonthsSlide
            {
                Months = bars,
                BusiestDayText = data.BusiestDay.HasValue
                    ? Format("LOCPlayAch_YearReview_BusiestDay", data.BusiestDay.Value.ToString("D", culture), Number(data.BusiestDayUnlocks))
                    : string.Empty,
                StreakDays = streak.Days,
                StreakRangeText = streak.Days > 1
                    ? Format("LOCPlayAch_YearReview_DateRange", MonthDay(streak.Start), MonthDay(streak.End))
                    : MonthDay(streak.Start),
                Heat = heat
            };
        }

        private static YearReviewTopGamesSlide TopGames(YearReviewData data)
        {
            var cards = new List<YearReviewCoverCard>();
            var runnersUp = data.TopGames.Skip(1).ToList();
            for (var rank = runnersUp.Count; rank >= 1; rank--)
            {
                var entry = runnersUp[rank - 1];
                var slot = RowSlots[RowSlots.Length - rank];
                cards.Add(Card(entry.Game, rank + 1, entry.Unlocks, slot.X, slot.Y, slot.Angle, 145, 218,
                    300 + (RowSlots.Length - rank) * 160));
            }

            var lead = data.TopGames.FirstOrDefault();
            if (lead != null)
            {
                cards.Add(Card(lead.Game, 1, lead.Unlocks, 370, 170, -4, 210, 315, 1000));
            }

            return new YearReviewTopGamesSlide
            {
                LeadUnlocks = lead?.Unlocks ?? 0,
                LeadCaption = lead == null ? string.Empty : Format("LOCPlayAch_YearReview_UnlocksInGame", lead.Game.Name),
                Cards = cards
            };
        }

        /// <summary>
        /// The year's rarest icons strewn under the big figure in two loose rows, each at its own
        /// size and tilt.
        /// </summary>
        private static IReadOnlyList<YearReviewIcon> Scatter(IReadOnlyList<YearReviewUnlock> highlights)
        {
            var spots = new (double X, double Y, double Size, double Angle)[]
            {
                (70, 500, 96, -8), (186, 530, 78, 6), (284, 492, 88, -3), (392, 540, 72, 10), (484, 500, 92, -6),
                (110, 628, 74, 5), (204, 640, 86, -10), (318, 620, 70, 4), (412, 636, 82, -4), (520, 616, 76, 8)
            };

            return (highlights ?? Array.Empty<YearReviewUnlock>())
                .Take(spots.Length)
                .Select((unlock, index) => new YearReviewIcon
                {
                    IconPath = unlock.IconPath,
                    ToolTip = Format("LOCPlayAch_YearReview_GameAndDate", unlock.AchievementName, unlock.GameName),
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
                    var card = Card(game, 0, 0, slot.X, slot.Y, slot.Angle, 140, 210, 250 + index * 120);
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
                .Select(line => Number(line.Count) + " " + ResourceProvider.GetString(line.Key));

            return new YearReviewCompletionsSlide
            {
                Count = data.Completed.Count,
                BreakdownText = string.Join(Environment.NewLine, lines),
                Cards = cards
            };
        }

        private static YearReviewRarestSlide Rarest(YearReviewData data)
        {
            var rarest = data.Rarest;
            var percent = rarest.GlobalPercent ?? 0;
            var percentText = PercentFormatter.Format(percent, PercentDecimals(percent));
            return new YearReviewRarestSlide
            {
                Name = rarest.AchievementName,
                IconPath = rarest.IconPath,
                Percent = percent,
                DetailText = Format("LOCPlayAch_YearReview_RarestDetail",
                    rarest.GameName,
                    MonthDay(DateTimeUtilities.AsLocalFromUtc(rarest.UnlockUtc)),
                    percentText),
                Tier = rarest.Rarity,
                TierText = rarest.Rarity.ToDisplayText().ToUpper(FormattingCulture.Current),
                TierBrush = rarest.Rarity.ToBrush()
            };
        }

        public static int PercentDecimals(double percent) => percent < 10 ? 1 : 0;

        private static YearReviewRaritySlide Rarity(YearReviewData data)
        {
            var tiers = new[] { RarityTier.Common, RarityTier.Uncommon, RarityTier.Rare, RarityTier.UltraRare };
            var max = Math.Max(1, tiers.Max(tier => data.Rarities.TryGetValue(tier, out var count) ? count : 0));
            var bubbles = new List<YearReviewBubble>();
            for (var i = 0; i < tiers.Length; i++)
            {
                var count = data.Rarities.TryGetValue(tiers[i], out var value) ? value : 0;
                if (count == 0)
                {
                    continue;
                }

                var diameter = 70 + 330 * Math.Sqrt((double)count / max);
                bubbles.Add(new YearReviewBubble
                {
                    Label = tiers[i].ToDisplayText(),
                    Unlocks = count,
                    Diameter = diameter,
                    NumberHeight = diameter * 0.3,
                    Fill = tiers[i].ToBrush(),
                    // Kept whole on the canvas, below the title.
                    X = Clamp(BubbleCentres[i].X - diameter / 2, 40, 1240 - diameter),
                    Y = Clamp(BubbleCentres[i].Y - diameter / 2, 130, 740 - diameter),
                    Delay = 250 + bubbles.Count * 220
                });
            }

            return new YearReviewRaritySlide
            {
                Bubbles = bubbles,
                TotalText = Format("LOCPlayAch_YearReview_UnlocksInTotal", Number(data.Unlocked))
            };
        }

        private static YearReviewPlatformsSlide Platforms(YearReviewData data)
        {
            // Keys that show the same name, such as the Custom provider and a custom provider
            // whose id no longer resolves, are one platform to the reader.
            var merged = data.Platforms
                .GroupBy(platform => ProviderRegistry.GetLocalizedName(platform.ProviderKey), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => (
                    Name: group.Key,
                    Count: group.Sum(platform => platform.Unlocks),
                    Fill: HexBrush(ProviderRegistry.ResolveProviderVisualsOrFallback(group.First().ProviderKey).colorHex)))
                .OrderByDescending(platform => platform.Count)
                .ToList();
            if (merged.Count < 2)
            {
                return null;
            }

            var shown = merged.Take(MaxPlatforms).ToList();
            var rest = merged.Skip(MaxPlatforms).Sum(platform => platform.Count);
            if (rest > 0)
            {
                shown.Add((ResourceProvider.GetString("LOCPlayAch_Common_Other"), rest,
                    Application.Current?.TryFindResource("PlayAch.Brush.Text.Secondary") as Brush ?? Brushes.Gray));
            }

            var max = Math.Max(1, shown.Max(platform => platform.Count));
            var heights = shown.Select(platform => 44 + 120 * Math.Sqrt((double)platform.Count / max)).ToList();
            const double gap = 46;
            var available = 770 - 140 - 40;
            var needed = heights.Sum() + gap * heights.Count;
            var fit = needed > available ? available / needed : 1;

            var entries = new List<YearReviewPlatformEntry>();
            var y = 140.0;
            for (var i = 0; i < shown.Count; i++)
            {
                var height = heights[i] * fit;
                entries.Add(new YearReviewPlatformEntry
                {
                    Name = shown[i].Name,
                    Unlocks = shown[i].Count,
                    Fill = shown[i].Fill,
                    NumberHeight = height,
                    X = PlatformIndents[i % PlatformIndents.Length],
                    Y = y,
                    Delay = 300 + i * 180
                });
                y += height + gap * fit;
            }

            var total = Math.Max(1, shown.Sum(platform => platform.Count));
            var segments = new List<YearReviewDonutSegment>();
            var start = -90.0;
            foreach (var platform in shown)
            {
                var sweep = 360.0 * platform.Count / total;
                segments.Add(new YearReviewDonutSegment
                {
                    Geometry = RingSegment(new Point(350, 350), 350, 168, start, sweep),
                    Fill = platform.Fill
                });
                start += sweep;
            }

            return new YearReviewPlatformsSlide { Entries = entries, Segments = segments };
        }

        private static YearReviewFirstAndLatestSlide FirstAndLatest(YearReviewData data)
        {
            string Detail(YearReviewUnlock unlock)
            {
                var local = DateTimeUtilities.AsLocalFromUtc(unlock.UnlockUtc);
                return Format("LOCPlayAch_YearReview_GameAndDate",
                    unlock.GameName,
                    MonthDay(local) + " " + local.ToString("t", FormattingCulture.Current));
            }

            return new YearReviewFirstAndLatestSlide
            {
                FirstLabel = Format("LOCPlayAch_YearReview_FirstUnlockOfYear", data.Year.ToString(FormattingCulture.Current)),
                FirstName = data.First.AchievementName,
                FirstIconPath = data.First.IconPath,
                FirstDetail = Detail(data.First),
                LatestName = data.Latest.AchievementName,
                LatestIconPath = data.Latest.IconPath,
                LatestDetail = Detail(data.Latest),
                DaysApart = (DateTimeUtilities.AsLocalFromUtc(data.Latest.UnlockUtc).Date
                             - DateTimeUtilities.AsLocalFromUtc(data.First.UnlockUtc).Date).Days
            };
        }

        private static YearReviewFriendsSlide Friends(
            YearReviewData data,
            YearReviewArt art,
            IReadOnlyDictionary<Guid, YearReviewGame> gamesById)
        {
            var friends = data.Friends;
            var name = friends.TopFriendName ?? string.Empty;
            var shared = friends.SharedGameIds
                .Select(id => gamesById.TryGetValue(id, out var game) ? game : null)
                .Where(game => game != null)
                .Take(3)
                .Select((game, index) => Card(game, 0, 0, 760 + index * 110, 240 + index % 2 * 30,
                    new[] { -10.0, 4, 14 }[index], 130, 195, 800 + index * 120))
                .Select(card =>
                {
                    card.ShowCaption = false;
                    return card;
                })
                .ToList();

            return new YearReviewFriendsSlide
            {
                UnlockedFirst = friends.UnlockedFirst,
                HasTopFriend = friends.SharedGameIds.Count > 0,
                FriendName = name,
                FriendInitial = name.Length > 0 ? name.Substring(0, 1).ToUpper(FormattingCulture.Current) : string.Empty,
                AvatarPath = art.TopFriendAvatarPath,
                InCommonText = Format("LOCPlayAch_YearReview_GamesInCommon", Number(friends.SharedGameIds.Count)),
                SharedCards = shared
            };
        }

        private static YearReviewSummarySlide Summary(YearReviewData data)
        {
            var figures = new List<KeyValuePair<string, string>>
            {
                Figure(Number(data.Unlocked), "LOCPlayAch_Achievements"),
                Figure("+" + Number(data.ScoreGained), "LOCPlayAch_YearReview_ScoreGained"),
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

            return new YearReviewSummarySlide
            {
                YearText = data.Year.ToString(FormattingCulture.Current),
                TopGameName = top?.Name,
                TopCoverPath = top?.CoverPath,
                Figures = figures
            };
        }

        private static YearReviewCoverCard Card(
            YearReviewGame game, int rank, int unlocks, double x, double y, double angle, double width, double height, double delay) =>
            new YearReviewCoverCard
            {
                Rank = rank,
                Name = game.Name,
                CoverPath = game.CoverPath,
                Unlocks = unlocks,
                X = x,
                Y = y,
                Angle = angle,
                Width = width,
                Height = height,
                Delay = delay
            };

        /// <summary>A ring sector from <paramref name="start"/> degrees, clockwise by <paramref name="sweep"/>.</summary>
        public static Geometry RingSegment(Point center, double outer, double inner, double start, double sweep)
        {
            if (sweep >= 359.99)
            {
                var ring = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new EllipseGeometry(center, outer, outer),
                    new EllipseGeometry(center, inner, inner));
                ring.Freeze();
                return ring;
            }

            Point At(double radius, double degrees)
            {
                var radians = degrees * Math.PI / 180;
                return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
            }

            var large = sweep > 180;
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(At(outer, start), true, true);
                ctx.ArcTo(At(outer, start + sweep), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, false);
                ctx.LineTo(At(inner, start + sweep), true, false);
                ctx.ArcTo(At(inner, start), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, false);
            }

            geometry.Freeze();
            return geometry;
        }

        private static KeyValuePair<string, string> Figure(string value, string labelKey) =>
            new KeyValuePair<string, string>(value, ResourceProvider.GetString(labelKey));

        private static Brush HexBrush(string hex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch (FormatException)
            {
            }

            return Brushes.Gray;
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private static string Number(int value) => value.ToString("N0", FormattingCulture.Current);

        private static string MonthDay(DateTime day) => day.ToString("M", FormattingCulture.Current);

        private static string Format(string key, params object[] args) =>
            string.Format(FormattingCulture.Current, ResourceProvider.GetString(key) ?? string.Empty, args);
    }
}
