using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.YearReview;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.ViewModels.YearReview
{
    /// <summary>The slides split by platform and rarity, and the helpers every slide shares.</summary>
    public static partial class YearReviewSlideFactory
    {
        /// <summary>A platform as the reader sees it: provider keys that show the same name are one.</summary>
        private sealed class Platform
        {
            public string Name;
            public Brush Fill;
            public List<YearReviewUnlock> Unlocks;
        }

        /// <summary>
        /// The year's platforms, most unlocks first. Keys that show the same name, such as the
        /// Custom provider and a custom provider whose id no longer resolves, are one platform.
        /// </summary>
        private static IReadOnlyList<Platform> MergedPlatforms(YearReviewData data) =>
            data.Unlocks
                .GroupBy(unlock => ProviderRegistry.GetLocalizedName(unlock.ProviderKey), StringComparer.CurrentCultureIgnoreCase)
                .Select(group => new Platform
                {
                    Name = group.Key,
                    Fill = HexBrush(ProviderRegistry.ResolveProviderVisualsOrFallback(group.First().ProviderKey).colorHex),
                    Unlocks = group.ToList()
                })
                .OrderByDescending(platform => platform.Unlocks.Count)
                .ThenBy(platform => platform.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        private static YearReviewRarestSlide Rarest(YearReviewData data, IReadOnlyList<Platform> platforms)
        {
            var rarest = data.Rarest;
            var percent = rarest.GlobalPercent ?? 0;
            var views = new List<YearReviewSlideView>
            {
                new YearReviewRarestView
                {
                    Label = Text("LOCPlayAch_YearReview_RarestUnlock"),
                    GameId = rarest.GameId,
                    IsSingle = true,
                    Name = rarest.AchievementName,
                    IconPath = rarest.IconPath,
                    Group = GameGroup(data, rarest.GameId, rarest.GameName),
                    Percent = percent,
                    DetailText = Format("LOCPlayAch_YearReview_RarestDetail",
                        rarest.GameName,
                        MonthDay(DateTimeUtilities.AsLocalFromUtc(rarest.UnlockUtc)),
                        PercentFormatter.Format(percent, PercentDecimals(percent))),
                    TierText = rarest.Rarity.ToDisplayText().ToUpper(FormattingCulture.Current),
                    TierBrush = rarest.Rarity.ToBrush()
                }
            };

            var rated = data.Unlocks.Where(unlock => unlock.GlobalPercent.HasValue).ToList();
            var topFive = rated
                .OrderBy(unlock => unlock.GlobalPercent.Value)
                .ThenBy(unlock => unlock.UnlockUtc)
                .Take(5)
                .Select(unlock => (Unlock: unlock, Caption: unlock.GameName))
                .ToList();
            if (topFive.Count > 1)
            {
                views.Add(RareRow(data, Text("LOCPlayAch_YearReview_TopFiveRarest"), topFive));
            }

            var perPlatform = platforms
                .Select(platform => (
                    Unlock: platform.Unlocks
                        .Where(unlock => unlock.GlobalPercent.HasValue)
                        .OrderBy(unlock => unlock.GlobalPercent.Value)
                        .ThenBy(unlock => unlock.UnlockUtc)
                        .FirstOrDefault(),
                    Caption: platform.Name))
                .Where(entry => entry.Unlock != null)
                .OrderBy(entry => entry.Unlock.GlobalPercent.Value)
                .Take(5)
                .ToList();
            if (perPlatform.Count > 1)
            {
                views.Add(RareRow(data, Text("LOCPlayAch_YearReview_RarestPerPlatform"), perPlatform));
            }

            return new YearReviewRarestSlide { Views = views };
        }

        /// <summary>A row of rare unlocks, each an icon with its percent, staggered up and down.</summary>
        private static YearReviewRarestView RareRow(YearReviewData data, string label, IReadOnlyList<(YearReviewUnlock Unlock, string Caption)> entries)
        {
            const double width = 200;
            const double gap = 30;
            var left = (1280 - entries.Count * width - (entries.Count - 1) * gap) / 2;
            return new YearReviewRarestView
            {
                Label = label,
                IsSingle = false,
                TierBrush = entries[0].Unlock.Rarity.ToBrush(),
                Entries = entries
                    .Select((entry, index) =>
                    {
                        var percent = entry.Unlock.GlobalPercent ?? 0;
                        return new YearReviewRareEntry
                        {
                            GameId = entry.Unlock.GameId,
                            IconPath = entry.Unlock.IconPath,
                            Name = entry.Unlock.AchievementName,
                            Caption = entry.Caption,
                            PercentText = PercentFormatter.Format(percent, PercentDecimals(percent)),
                            TierBrush = entry.Unlock.Rarity.ToBrush(),
                            Group = GameGroup(data, entry.Unlock.GameId, entry.Unlock.GameName),
                            X = left + index * (width + gap),
                            Y = index % 2 == 0 ? 190 : 250,
                            Angle = new[] { -4.0, 3, -2, 4, -3 }[index % 5],
                            Delay = 200 + index * 160
                        };
                    })
                    .ToList()
            };
        }

        public static int PercentDecimals(double percent) => percent < 10 ? 1 : 0;

        private static YearReviewRaritySlide Rarity(YearReviewData data, IReadOnlyList<Platform> platforms)
        {
            var views = new List<YearReviewSlideView> { RarityView(Text("LOCPlayAch_Common_All"), data.Unlocks) };
            if (platforms.Count > 1)
            {
                foreach (var platform in platforms)
                {
                    var view = RarityView(platform.Name, platform.Unlocks);
                    view.TopGameId = platform.Unlocks
                        .GroupBy(unlock => unlock.GameId)
                        .OrderByDescending(group => group.Count())
                        .Select(group => (Guid?)group.Key)
                        .FirstOrDefault();
                    views.Add(view);
                }
            }

            return new YearReviewRaritySlide { Views = views };
        }

        /// <summary>
        /// One circle per rarity, its area following the count, in a row scaled so the circles sit
        /// side by side without touching and none outgrows the space under the title.
        /// </summary>
        private static YearReviewRarityView RarityView(string label, IReadOnlyList<YearReviewUnlock> unlocks)
        {
            var tiers = new[] { RarityTier.Common, RarityTier.Uncommon, RarityTier.Rare, RarityTier.UltraRare };
            var shown = tiers
                .Select(tier => (Tier: tier, Unlocks: unlocks.Where(unlock => unlock.Rarity == tier).ToList()))
                .Where(entry => entry.Unlocks.Count > 0)
                .ToList();
            var max = Math.Max(1, shown.Select(entry => entry.Unlocks.Count).DefaultIfEmpty(1).Max());

            const double gap = 28;
            const double left = 72;
            const double right = 1208;
            const double tallest = 470;
            const double centreY = 430;
            var diameters = shown.Select(entry => 90 + 380 * Math.Sqrt((double)entry.Unlocks.Count / max)).ToList();
            var fit = Math.Min(1, Math.Min(
                (right - left - gap * Math.Max(0, diameters.Count - 1)) / Math.Max(1, diameters.Sum()),
                tallest / Math.Max(1, diameters.DefaultIfEmpty(1).Max())));

            var bubbles = new List<YearReviewBubble>();
            var x = left;
            for (var i = 0; i < shown.Count; i++)
            {
                var diameter = diameters[i] * fit;
                var tier = shown[i].Tier;
                bubbles.Add(new YearReviewBubble
                {
                    Group = GroupOf(tier.ToDisplayText(), shown[i].Unlocks),
                    Label = tier.ToDisplayText(),
                    Unlocks = shown[i].Unlocks.Count,
                    Diameter = diameter,
                    NumberHeight = diameter * 0.3,
                    Fill = tier.ToBrush(),
                    TextBrush = TextOn(tier.ToBrush()),
                    X = x,
                    Y = Clamp(centreY + (i % 2 == 0 ? -30 : 40) - diameter / 2, 130, 720 - diameter),
                    Delay = 250 + i * 220
                });
                x += diameter + gap;
            }

            return new YearReviewRarityView
            {
                Label = label,
                Bubbles = bubbles,
                TotalText = Format("LOCPlayAch_YearReview_UnlocksInTotal", Number(unlocks.Count))
            };
        }

        private static YearReviewPlatformsSlide Platforms(YearReviewData data, IReadOnlyList<Platform> platforms)
        {
            if (platforms.Count < 2)
            {
                return null;
            }

            var shown = platforms.Take(MaxPlatforms).ToList();
            var rest = platforms.Skip(MaxPlatforms).SelectMany(platform => platform.Unlocks).ToList();
            if (rest.Count > 0)
            {
                shown.Add(new Platform
                {
                    Name = Text("LOCPlayAch_Common_Other"),
                    Fill = Application.Current?.TryFindResource("PlayAch.Brush.Text.Secondary") as Brush ?? Brushes.Gray,
                    Unlocks = rest
                });
            }

            // One row per platform filling the left half; the number and the cover strip scale
            // with the row, so two platforms get big rows and five get compact ones.
            const double top = 140;
            const double bottom = 740;
            var rowHeight = Math.Min(170, (bottom - top) / shown.Count);
            var max = Math.Max(1, shown.Max(platform => platform.Unlocks.Count));
            var total = Math.Max(1, shown.Sum(platform => platform.Unlocks.Count));
            var coverHeight = Math.Min(130, rowHeight - 18);

            var entries = new List<YearReviewPlatformEntry>();
            var segments = new List<YearReviewDonutSegment>();
            var start = -90.0;
            for (var i = 0; i < shown.Count; i++)
            {
                var platform = shown[i];
                var share = (double)platform.Unlocks.Count / total;
                var entry = new YearReviewPlatformEntry
                {
                    Group = GroupOf(platform.Name, platform.Unlocks),
                    Name = platform.Name,
                    Unlocks = platform.Unlocks.Count,
                    ShareText = PercentFormatter.Format(share * 100, 0),
                    Fill = platform.Fill,
                    RowHeight = rowHeight,
                    NumberHeight = Math.Max(28, (rowHeight - 24) * (0.55 + 0.45 * Math.Sqrt((double)platform.Unlocks.Count / max))),
                    CoverHeight = coverHeight,
                    CoverWidth = coverHeight * 2 / 3,
                    Covers = platform.Unlocks
                        .GroupBy(unlock => unlock.GameId)
                        .OrderByDescending(group => group.Count())
                        .Select(group => GameOf(data, group.Key).CoverPath)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Take(3)
                        .ToList(),
                    X = 72,
                    Y = top + i * rowHeight,
                    Delay = 300 + i * 160
                };
                entries.Add(entry);

                var sweep = 360.0 * platform.Unlocks.Count / total;
                segments.Add(new YearReviewDonutSegment
                {
                    Entry = entry,
                    StartAngle = start,
                    Sweep = sweep,
                    Fill = platform.Fill,
                    ToolTip = Format("LOCPlayAch_Format_Pair", platform.Name, entry.ShareText),
                    Group = entry.Group
                });
                start += sweep;
            }

            return new YearReviewPlatformsSlide
            {
                Entries = entries,
                Segments = segments,
                Total = data.Unlocks.Count,
                AllGroup = GroupOf(Text("LOCPlayAch_YearReview_UnlocksByPlatform"), data.Unlocks)
            };
        }

        /// <summary>The unlocks of <paramref name="unlocks"/>, headed by a name and their count.</summary>
        private static YearReviewUnlockGroup GroupOf(string name, IReadOnlyList<YearReviewUnlock> unlocks, bool withDate = true) =>
            unlocks == null || unlocks.Count == 0
                ? null
                : new YearReviewUnlockGroup(
                    Format("LOCPlayAch_Format_Pair", name, Number(unlocks.Count)),
                    unlocks.Count,
                    () => Rows(unlocks, withDate));

        private static YearReviewUnlockGroup GameGroup(YearReviewData data, Guid gameId, string name) =>
            GroupOf(name, data.Unlocks.Where(unlock => unlock.GameId == gameId).ToList());

        private static IReadOnlyList<YearReviewUnlockRow> Rows(IEnumerable<YearReviewUnlock> unlocks, bool withDate) =>
            unlocks
                .OrderBy(unlock => unlock.UnlockUtc)
                .Select(unlock =>
                {
                    var local = DateTimeUtilities.AsLocalFromUtc(unlock.UnlockUtc);
                    var time = local.ToString("t", FormattingCulture.Current);
                    return new YearReviewUnlockRow
                    {
                        IconPath = unlock.IconPath,
                        Name = unlock.AchievementName,
                        GameName = unlock.GameName,
                        TimeText = withDate ? MonthDay(local) + " " + time : time
                    };
                })
                .ToList();

        private static YearReviewCoverCard Card(
            YearReviewData data, YearReviewGame game, int rank, string valueText,
            double x, double y, double angle, double width, double height, double delay) =>
            new YearReviewCoverCard
            {
                GameId = game.GameId,
                Group = GameGroup(data, game.GameId, game.Name),
                Rank = rank,
                Name = game.Name,
                CoverPath = game.CoverPath,
                ValueText = valueText,
                X = x,
                Y = y,
                Angle = angle,
                Width = width,
                Height = height,
                Delay = delay
            };

        private static KeyValuePair<string, string> Figure(string value, string labelKey) =>
            new KeyValuePair<string, string>(value, Text(labelKey));

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

        private static readonly Brush DarkText = Frozen(Color.FromArgb(0xCC, 0, 0, 0));
        private static readonly Brush LightText = Frozen(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));

        /// <summary>Dark text on a light fill, light text on a dark one, by the fill's luminance.</summary>
        private static Brush TextOn(Brush fill)
        {
            if (!(fill is SolidColorBrush solid))
            {
                return DarkText;
            }

            var c = solid.Color;
            var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            return luminance > 0.5 ? DarkText : LightText;
        }

        private static Brush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));

        private static string Number(int value) => value.ToString("N0", FormattingCulture.Current);

        private static string MonthDay(DateTime day) => day.ToString("M", FormattingCulture.Current);

        private static string Text(string key) => ResourceProvider.GetString(key);

        private static string Format(string key, params object[] args) =>
            string.Format(FormattingCulture.Current, ResourceProvider.GetString(key) ?? string.Empty, args);
    }
}
