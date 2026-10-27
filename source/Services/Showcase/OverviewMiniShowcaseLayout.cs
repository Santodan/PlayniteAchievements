using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.Showcase
{
    /// <summary>
    /// The overview's mini-showcase: a one-page, one-row <see cref="ShowcaseSettings"/> under
    /// the overview grids. It is a plain layout, so every <see cref="ShowcaseLayoutService"/>
    /// operation (split, merge, place, resize) works on it unchanged; this class owns only what
    /// differs from the Showcase page: the default, the single-row shape, the kinds it accepts,
    /// and its pixel height.
    /// </summary>
    public static class OverviewMiniShowcaseLayout
    {
        public const double MinHeight = 120;
        public const double MaxHeight = 600;
        public const double DefaultHeight = 225;

        private static readonly HashSet<ShowcaseWidgetKind> AllowedKindSet = new HashSet<ShowcaseWidgetKind>
        {
            ShowcaseWidgetKind.Pie,
            ShowcaseWidgetKind.Timeline,
            ShowcaseWidgetKind.Statistics,
            ShowcaseWidgetKind.ActivityCalendar
        };

        /// <summary>The widget kinds the mini-showcase offers; each one is linked to the overview.</summary>
        public static bool IsAllowedKind(ShowcaseWidgetKind kind) => AllowedKindSet.Contains(kind);

        public static double ClampHeight(double height)
        {
            if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
            {
                return DefaultHeight;
            }

            return Math.Max(MinHeight, Math.Min(MaxHeight, Math.Round(height)));
        }

        /// <summary>
        /// The strip the overview showed before it became editable: the four pies (completions,
        /// platform, rarity, trophy) at equal widths, then a one-year timeline at the width of two
        /// with its window picker showing, as the strip's had.
        /// </summary>
        public static ShowcaseSettings CreateDefault()
        {
            return Create(
                new[]
                {
                    ShowcasePieMode.CompletedGames,
                    ShowcasePieMode.Provider,
                    ShowcasePieMode.Rarity,
                    ShowcasePieMode.Trophy
                },
                includeTimeline: true,
                configurePie: null,
                configureTimeline: timeline =>
                {
                    ShowcaseTimelineOptions.SetWindow(timeline, TimeWindow.FromPreset(TimelineRange.OneYear));
                    ShowcaseWidgetOptions.ShowOnlyControlBarItems(
                        timeline,
                        ShowcaseControlBarItems.TimelineItems,
                        ShowcaseControlBarItems.TimeWindow,
                        ShowcaseControlBarItems.SplitByPlatform);
                });
        }

        /// <summary>
        /// Builds a single-row layout holding one Pie widget per mode, in order, and optionally a
        /// Timeline after them at twice a pie's width. Pies start without a legend, as the
        /// overview's did.
        /// </summary>
        public static ShowcaseSettings Create(
            IReadOnlyList<ShowcasePieMode> pieModes,
            bool includeTimeline,
            Action<ShowcaseWidgetInstanceSettings> configurePie,
            Action<ShowcaseWidgetInstanceSettings> configureTimeline)
        {
            var settings = new ShowcaseSettings();
            var page = new ShowcasePageSettings { Name = "Overview", RowCount = 1 };
            var weights = new List<double>();
            var column = 0;
            foreach (var mode in pieModes ?? Array.Empty<ShowcasePieMode>())
            {
                var pie = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Pie);
                ShowcaseWidgetOptions.SetPieMode(pie, mode);
                ShowcaseWidgetOptions.SetShowLegend(pie, false);
                configurePie?.Invoke(pie);
                page.Blocks.Add(new ShowcaseBlockSettings { Row = 0, Column = column++, WidgetInstanceId = pie.InstanceId });
                weights.Add(1d);
            }

            if (includeTimeline)
            {
                var timeline = ShowcaseLayoutService.CreateWidget(settings, ShowcaseWidgetKind.Timeline);
                configureTimeline?.Invoke(timeline);
                page.Blocks.Add(new ShowcaseBlockSettings { Row = 0, Column = column++, WidgetInstanceId = timeline.InstanceId });
                weights.Add(2d);
            }

            if (column == 0)
            {
                page.Blocks.Add(new ShowcaseBlockSettings { Row = 0, Column = 0 });
                column = 1;
                weights = null;
            }

            page.ColumnCount = column;
            page.ColumnWeights = weights;
            settings.Pages.Add(page);
            settings.LastSelectedPageId = page.PageId;
            Normalize(settings);
            return settings;
        }

        /// <summary>
        /// Keeps the layout to exactly one page of one row before the shared normalizer runs,
        /// which would otherwise seed an empty layout with the Showcase page's default. A page
        /// that somehow holds more than one row is reset to a single empty block rather than
        /// guessed at, and widgets of kinds the strip does not offer are dropped.
        /// </summary>
        public static void Normalize(ShowcaseSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            settings.Pages = (settings.Pages ?? new List<ShowcasePageSettings>())
                .Where(page => page != null)
                .Take(1)
                .ToList();
            if (settings.Pages.Count == 0)
            {
                settings.Pages.Add(new ShowcasePageSettings
                {
                    Name = "Overview",
                    RowCount = 1,
                    ColumnCount = 1,
                    Blocks = new List<ShowcaseBlockSettings> { new ShowcaseBlockSettings() }
                });
            }

            var page = settings.Pages[0];
            if (page.RowCount != 1 ||
                (page.Blocks ?? new List<ShowcaseBlockSettings>()).Any(block => block == null || block.Row != 0 || block.RowSpan != 1))
            {
                page.RowCount = 1;
                page.ColumnCount = Math.Max(1, page.ColumnCount);
                page.RowWeights = null;
                page.ColumnWeights = null;
                page.Blocks = Enumerable.Range(0, page.ColumnCount)
                    .Select(column => new ShowcaseBlockSettings { Row = 0, Column = column })
                    .ToList();
            }

            page.RowWeights = null;
            settings.WidgetInstances = (settings.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                .Where(widget => widget != null && IsAllowedKind(widget.Kind))
                .ToList();
            ShowcaseLayoutService.Normalize(settings);
            ShowcaseLayoutService.PruneOrphanedWidgets(settings);
        }
    }
}
