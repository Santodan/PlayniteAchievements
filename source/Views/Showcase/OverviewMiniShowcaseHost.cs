using System;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The overview's mini-showcase: one row of widgets linked to the overview. Each widget
    /// projects from the overview's snapshot narrowed by every grid filter except the ones it
    /// sets itself, and by the selected game where the overview's own charts followed it.
    /// </summary>
    internal sealed class OverviewMiniShowcaseHost : ShowcaseLayoutHost
    {
        private readonly OverviewViewModel _overview;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly Action _persist;

        public OverviewMiniShowcaseHost(
            OverviewViewModel overview,
            PlayniteAchievementsSettings settings,
            Action persist)
        {
            _overview = overview ?? throw new ArgumentNullException(nameof(overview));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _persist = persist ?? throw new ArgumentNullException(nameof(persist));
            _overview.LinkedDataChanged += Overview_LinkedDataChanged;
        }

        public override ShowcaseSettings Layout => _settings.Persisted.OverviewMiniShowcase;

        public override bool IsStrip => true;

        public override void Normalize() => OverviewMiniShowcaseLayout.Normalize(Layout);

        public override void Persist() => _persist();

        public override bool IsKindAllowed(ShowcaseWidgetKind kind) => OverviewMiniShowcaseLayout.IsAllowedKind(kind);

        public override double StripHeight
        {
            get => _settings.Persisted.OverviewMiniShowcaseHeight;
            set => _settings.Persisted.OverviewMiniShowcaseHeight = value;
        }

        public override OverviewDataSnapshot SnapshotFor(ShowcaseWidgetInstanceSettings widget)
        {
            var (exclude, selection, countsGames) = LinkRule(widget);
            return _overview.GetLinkedSnapshot(exclude, selection, countsGames);
        }

        public override void Decorate(ShowcaseWidgetProjection projection)
        {
            if (projection?.Instance == null)
            {
                return;
            }

            var widget = projection.Instance;
            projection.IsLinked = true;
            projection.ContextLabel = ContextLabel(widget);
            switch (widget.Kind)
            {
                case ShowcaseWidgetKind.Pie:
                    projection.LinkedSliceKeys = _overview.GetLinkedSliceKeys(ShowcaseWidgetOptions.GetPieMode(widget));
                    projection.LinkedOptionGames = _overview.LatestSnapshot?.GameSummaries;
                    break;
                case ShowcaseWidgetKind.Statistics:
                    projection.LinkedOptionGames = _overview.LatestSnapshot?.GameSummaries;
                    break;
                case ShowcaseWidgetKind.Timeline:
                    projection.HighlightedSpan = _overview.UnlockSpanFilter;
                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    projection.HighlightedSpan = _overview.UnlockSpanFilter;
                    break;
            }

            projection.LinkedStamp = Stamp(projection.ContextLabel, projection.LinkedSliceKeys, projection.HighlightedSpan);
        }

        // A widget is current when its snapshot is the one it would get now and the linked state
        // it was decorated with still holds, so a filter change that touches neither leaves it
        // exactly as drawn.
        public override bool IsProjectionCurrent(ShowcaseWidgetProjection projection, OverviewDataSnapshot snapshot)
        {
            if (!base.IsProjectionCurrent(projection, snapshot))
            {
                return false;
            }

            var widget = projection.Instance;
            var context = ContextLabel(widget);
            var sliceKeys = widget?.Kind == ShowcaseWidgetKind.Pie
                ? _overview.GetLinkedSliceKeys(ShowcaseWidgetOptions.GetPieMode(widget))
                : null;
            var span = widget?.Kind == ShowcaseWidgetKind.Timeline || widget?.Kind == ShowcaseWidgetKind.ActivityCalendar
                ? _overview.UnlockSpanFilter
                : null;
            return string.Equals(projection.LinkedStamp, Stamp(context, sliceKeys, span), StringComparison.Ordinal);
        }

        // The widget's title: the game it is narrowed to, else the platforms it is filtered to.
        private string ContextLabel(ShowcaseWidgetInstanceSettings widget)
        {
            var (exclude, selection, _) = LinkRule(widget);
            return _overview.GetLinkedContextLabel(exclude, selection);
        }

        private static string Stamp(
            string context,
            System.Collections.Generic.IEnumerable<string> sliceKeys,
            UnlockDaySpan? span)
        {
            var keys = string.Join(
                "\u001f",
                (sliceKeys ?? Array.Empty<string>()).OrderBy(key => key, StringComparer.OrdinalIgnoreCase));
            var days = span.HasValue ? span.Value.Start.Ticks + "-" + span.Value.End.Ticks : string.Empty;
            return context + "\u001e" + keys + "\u001e" + days;
        }

        /// <summary>
        /// Which filters a widget leaves out and when it follows the selected game: each pie
        /// follows every filter except its own, the rarity and trophy pies switch to the selected
        /// game when it has that data, and the timeline and calendar mark the unlock range they
        /// set rather than narrowing to it. Every other widget follows the range and the rarity
        /// and trophy filters: to the games with matching unlocks, and to those unlocks where it
        /// counts achievements. The completions pie, and a platform pie counting completed games,
        /// count whole games, so they keep every achievement of the games kept, locked ones
        /// included; a platform pie counting achievements counts only the matching unlocks.
        /// </summary>
        private static (OverviewLinkedFilter Exclude, OverviewLinkedSelection Selection, bool CountsGames) LinkRule(
            ShowcaseWidgetInstanceSettings widget)
        {
            switch (widget?.Kind)
            {
                case ShowcaseWidgetKind.Pie:
                    switch (ShowcaseWidgetOptions.GetPieMode(widget))
                    {
                        case ShowcasePieMode.Provider:
                            return (
                                OverviewLinkedFilter.Provider,
                                OverviewLinkedSelection.Ignore,
                                !ShowcaseWidgetOptions.CountsAchievements(widget));
                        case ShowcasePieMode.Rarity:
                            return (OverviewLinkedFilter.Rarity, OverviewLinkedSelection.WhenRarityData, false);
                        case ShowcasePieMode.Trophy:
                            return (OverviewLinkedFilter.Trophy, OverviewLinkedSelection.WhenTrophyData, false);
                        case ShowcasePieMode.Tag:
                            // A selected game narrows it to that game, whose own tags it then shows;
                            // its own slices set the tag filter, so that one is left out.
                            return (OverviewLinkedFilter.Tag, OverviewLinkedSelection.Always, false);
                        default:
                            return (OverviewLinkedFilter.Completeness, OverviewLinkedSelection.Ignore, true);
                    }

                case ShowcaseWidgetKind.Timeline:
                case ShowcaseWidgetKind.ActivityCalendar:
                    return (OverviewLinkedFilter.UnlockSpan, OverviewLinkedSelection.Always, false);
                default:
                    return (OverviewLinkedFilter.None, OverviewLinkedSelection.Always, false);
            }
        }

        private void Overview_LinkedDataChanged(object sender, EventArgs e) => RaiseDataChanged();

        public override void Dispose()
        {
            _overview.LinkedDataChanged -= Overview_LinkedDataChanged;
        }
    }
}
