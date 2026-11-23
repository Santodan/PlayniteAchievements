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
        public string TopGameBackgroundPath { get; set; }
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

        // Fan of the runners-up, back to front: rank 5, 4, 3, 2. The leader sits in front.
        private static readonly (double X, double Y, double Angle)[] FanSlots =
        {
            (1050, 330, 16), (900, 300, 9), (750, 285, 2), (600, 300, -5)
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
                            Games = data.GamesWithUnlocks
                        });
                        break;
                    case YearReviewSlideKind.Months:
                        slides.Add(Months(data));
                        break;
                    case YearReviewSlideKind.TopGames:
                        slides.Add(TopGames(data, art));
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
                        slides.Add(Platforms(data));
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
            }

            return slides;
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
                    Delay = 200 + index * 60
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

        private static YearReviewTopGamesSlide TopGames(YearReviewData data, YearReviewArt art)
        {
            var cards = new List<YearReviewCoverCard>();
            var runnersUp = data.TopGames.Skip(1).ToList();
            for (var rank = runnersUp.Count; rank >= 1; rank--)
            {
                var entry = runnersUp[rank - 1];
                var slot = FanSlots[FanSlots.Length - rank];
                cards.Add(Card(entry.Game, rank + 1, entry.Unlocks, slot.X, slot.Y, slot.Angle, 180, 270,
                    300 + (FanSlots.Length - rank) * 160));
            }

            var lead = data.TopGames.FirstOrDefault();
            if (lead != null)
            {
                cards.Add(Card(lead.Game, 1, lead.Unlocks, 300, 250, -9, 250, 375, 1000));
            }

            return new YearReviewTopGamesSlide
            {
                BackgroundPath = art.TopGameBackgroundPath,
                LeadUnlocks = lead?.Unlocks ?? 0,
                LeadCaption = lead == null ? string.Empty : Format("LOCPlayAch_YearReview_UnlocksInGame", lead.Game.Name),
                Cards = cards
            };
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

                var diameter = 70 + 360 * Math.Sqrt((double)count / max);
                bubbles.Add(new YearReviewBubble
                {
                    Label = tiers[i].ToDisplayText(),
                    Unlocks = count,
                    Diameter = diameter,
                    NumberHeight = diameter * 0.3,
                    Fill = tiers[i].ToBrush(),
                    X = BubbleCentres[i].X - diameter / 2,
                    Y = BubbleCentres[i].Y - diameter / 2,
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
            var shown = data.Platforms.Take(MaxPlatforms)
                .Select(platform => (
                    Name: ProviderRegistry.GetLocalizedName(platform.ProviderKey),
                    Count: platform.Unlocks,
                    Fill: HexBrush(ProviderRegistry.ResolveProviderVisualsOrFallback(platform.ProviderKey).colorHex)))
                .ToList();
            var rest = data.Platforms.Skip(MaxPlatforms).Sum(platform => platform.Unlocks);
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

        private static string Number(int value) => value.ToString("N0", FormattingCulture.Current);

        private static string MonthDay(DateTime day) => day.ToString("M", FormattingCulture.Current);

        private static string Format(string key, params object[] args) =>
            string.Format(FormattingCulture.Current, ResourceProvider.GetString(key) ?? string.Empty, args);
    }
}
