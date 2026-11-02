using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Services.StartPage
{
    public sealed class StartPageViewDefinition
    {
        public string ViewId { get; set; }

        /// <summary>
        /// The stable start page kind registered for this view, or null for a view minted from a
        /// showcase widget kind that has no dedicated start page kind.
        /// </summary>
        public StartPageWidgetKind? WidgetKind { get; set; }

        public string NameKey { get; set; }

        public ShowcaseWidgetKind? ShowcaseWidgetKind { get; set; }

        public bool HasSettings { get; set; }

        /// <summary>
        /// Hidden views are omitted from the StartPage add list but still resolve by id, so
        /// views placed before their widget kind was collapsed or disabled keep rendering.
        /// </summary>
        public bool Hidden { get; set; }
    }

    /// <summary>
    /// The start page views mirror the showcase widget catalog: one view per showcase widget kind,
    /// in the catalog's order, named and flagged by the showcase definition. Legacy views that
    /// predate the merge onto the showcase widget path follow, hidden, so already-placed start
    /// page widgets keep resolving by their saved ids.
    /// </summary>
    public static class StartPageViewCatalog
    {
        public const string GameSummariesGridViewId = "PlayniteAchievements_GameSummariesGrid";
        public const string LegacyGamesOverviewGridViewId = "PlayniteAchievements_GamesOverviewGrid";
        public const string RecentUnlocksGridViewId = "PlayniteAchievements_RecentUnlocksGrid";
        public const string CompletedGamesPieViewId = "PlayniteAchievements_CompletedGamesPie";
        public const string ProviderPieViewId = "PlayniteAchievements_ProviderPie";
        public const string RarityPieViewId = "PlayniteAchievements_RarityPie";
        public const string TrophyPieViewId = "PlayniteAchievements_TrophyPie";
        public const string CollectionScoreCardViewId = "PlayniteAchievements_CollectionScoreCard";
        public const string PrestigeScoreCardViewId = "PlayniteAchievements_PrestigeScoreCard";
        public const string ShowcaseProfileViewId = "PlayniteAchievements_Showcase_Profile";
        public const string ShowcaseDualScoresViewId = "PlayniteAchievements_Showcase_DualScores";
        public const string ShowcaseTimelineViewId = "PlayniteAchievements_Showcase_Timeline";
        public const string ShowcaseStatisticsViewId = "PlayniteAchievements_Showcase_Statistics";
        public const string ShowcaseNativePointsViewId = "PlayniteAchievements_Showcase_NativePoints";
        public const string ShowcaseIconMosaicViewId = "PlayniteAchievements_Showcase_IconMosaic";
        public const string ShowcaseScreenshotSlideshowViewId = "PlayniteAchievements_Showcase_ScreenshotSlideshow";
        public const string ShowcaseActivityCalendarViewId = "PlayniteAchievements_Showcase_ActivityCalendar";
        public const string ShowcaseImageViewId = "PlayniteAchievements_Showcase_Image";
        public const string ShowcaseTextViewId = "PlayniteAchievements_Showcase_Text";

        private const string MintedViewIdPrefix = "PlayniteAchievements_Showcase_";

        /// <summary>
        /// The view each showcase widget kind is offered under. Each reuses a view id that start
        /// pages already hold, so a widget placed before the start page mirrored the showcase is
        /// the same entry the add list offers now. A showcase kind missing here is offered under
        /// a view id minted from its name.
        /// </summary>
        private static readonly IReadOnlyDictionary<ShowcaseWidgetKind, (string ViewId, StartPageWidgetKind WidgetKind)> KindViews =
            new Dictionary<ShowcaseWidgetKind, (string, StartPageWidgetKind)>
            {
                [ShowcaseWidgetKind.Profile] = (ShowcaseProfileViewId, StartPageWidgetKind.ShowcaseProfile),
                // The former dual-score view; its saved Both choice reads as the Collection card.
                [ShowcaseWidgetKind.Scores] = (ShowcaseDualScoresViewId, StartPageWidgetKind.ShowcaseDualScores),
                // The former completed-games pie, which seeds the showcase default pie mode.
                [ShowcaseWidgetKind.Pie] = (CompletedGamesPieViewId, StartPageWidgetKind.CompletedGamesPie),
                [ShowcaseWidgetKind.Timeline] = (ShowcaseTimelineViewId, StartPageWidgetKind.ShowcaseTimeline),
                [ShowcaseWidgetKind.Statistics] = (ShowcaseStatisticsViewId, StartPageWidgetKind.ShowcaseStatistics),
                [ShowcaseWidgetKind.NativePoints] = (ShowcaseNativePointsViewId, StartPageWidgetKind.ShowcaseNativePoints),
                [ShowcaseWidgetKind.IconMosaic] = (ShowcaseIconMosaicViewId, StartPageWidgetKind.ShowcaseIconMosaic),
                [ShowcaseWidgetKind.ScreenshotSlideshow] = (ShowcaseScreenshotSlideshowViewId, StartPageWidgetKind.ShowcaseScreenshotSlideshow),
                // The two self grids seed their per-instance surfaces from the fixed StartPage
                // surfaces on first load.
                [ShowcaseWidgetKind.RecentAchievements] = (RecentUnlocksGridViewId, StartPageWidgetKind.RecentUnlocksGrid),
                [ShowcaseWidgetKind.GameSummaries] = (GameSummariesGridViewId, StartPageWidgetKind.GameSummariesGrid),
                [ShowcaseWidgetKind.ActivityCalendar] = (ShowcaseActivityCalendarViewId, StartPageWidgetKind.ShowcaseActivityCalendar),
                [ShowcaseWidgetKind.Image] = (ShowcaseImageViewId, StartPageWidgetKind.ShowcaseImage),
                [ShowcaseWidgetKind.Text] = (ShowcaseTextViewId, StartPageWidgetKind.ShowcaseText)
            };

        /// <summary>
        /// Views a showcase kind's entry replaced. They stay hidden from the add list and seed
        /// the option they always showed at instance creation (see
        /// GetOrCreateStartPageWidgetSettings): each pie its pie mode, each score card its card
        /// type.
        /// </summary>
        private static readonly IReadOnlyList<(string ViewId, StartPageWidgetKind WidgetKind, ShowcaseWidgetKind ShowcaseKind)> LegacyViews =
            new List<(string, StartPageWidgetKind, ShowcaseWidgetKind)>
            {
                (ProviderPieViewId, StartPageWidgetKind.ProviderPie, ShowcaseWidgetKind.Pie),
                (RarityPieViewId, StartPageWidgetKind.RarityPie, ShowcaseWidgetKind.Pie),
                (TrophyPieViewId, StartPageWidgetKind.TrophyPie, ShowcaseWidgetKind.Pie),
                (CollectionScoreCardViewId, StartPageWidgetKind.CollectionScoreCard, ShowcaseWidgetKind.Scores),
                (PrestigeScoreCardViewId, StartPageWidgetKind.PrestigeScoreCard, ShowcaseWidgetKind.Scores)
            };

        private static readonly IReadOnlyList<StartPageViewDefinition> ViewDefinitions = BuildViews();

        public static IReadOnlyList<StartPageViewDefinition> Views => ViewDefinitions;

        public static bool TryGetDefinition(string viewId, out StartPageViewDefinition definition)
        {
            definition = ViewDefinitions.FirstOrDefault(view =>
                string.Equals(view.ViewId, viewId, StringComparison.Ordinal));
            if (definition == null &&
                string.Equals(viewId, LegacyGamesOverviewGridViewId, StringComparison.Ordinal))
            {
                definition = ViewDefinitions.FirstOrDefault(view =>
                    string.Equals(view.ViewId, GameSummariesGridViewId, StringComparison.Ordinal));
            }

            return definition != null;
        }

        /// <summary>
        /// Seeds a new start page instance with the option its view id has always shown: the
        /// four pie views share the showcase Pie kind and each seeds its distribution, and the
        /// former standalone Prestige score card seeds the Prestige card. Every other view keeps
        /// the showcase defaults.
        /// </summary>
        public static void SeedViewOptions(string viewId, ShowcaseWidgetInstanceSettings settings)
        {
            if (settings == null)
            {
                return;
            }

            if (settings.Kind == PlayniteAchievements.Models.Settings.ShowcaseWidgetKind.Pie)
            {
                ShowcaseWidgetOptions.SetPieMode(settings, ResolvePieMode(viewId));
            }

            if (settings.Kind == PlayniteAchievements.Models.Settings.ShowcaseWidgetKind.Scores &&
                string.Equals(viewId, PrestigeScoreCardViewId, StringComparison.Ordinal))
            {
                ShowcaseWidgetOptions.SetScoreCardType(settings, ScoreCardType.Prestige);
            }
        }

        private static ShowcasePieMode ResolvePieMode(string viewId)
        {
            switch (viewId)
            {
                case ProviderPieViewId:
                    return ShowcasePieMode.Provider;
                case RarityPieViewId:
                    return ShowcasePieMode.Rarity;
                case TrophyPieViewId:
                    return ShowcasePieMode.Trophy;
                default:
                    return ShowcasePieMode.CompletedGames;
            }
        }

        private static IReadOnlyList<StartPageViewDefinition> BuildViews()
        {
            var views = new List<StartPageViewDefinition>();
            foreach (var showcase in ShowcaseWidgetCatalog.Definitions)
            {
                var view = KindViews.TryGetValue(showcase.Kind, out var known)
                    ? FromShowcase(known.ViewId, known.WidgetKind, showcase)
                    : FromShowcase(MintedViewIdPrefix + showcase.Kind, null, showcase);
                views.Add(view);
            }

            foreach (var legacy in LegacyViews)
            {
                var view = FromShowcase(
                    legacy.ViewId,
                    legacy.WidgetKind,
                    ShowcaseWidgetCatalog.Get(legacy.ShowcaseKind));
                view.Hidden = true;
                views.Add(view);
            }

            return views;
        }

        // Every showcase widget kind edits per-instance options in the shared options control,
        // so every view offers settings.
        private static StartPageViewDefinition FromShowcase(
            string viewId,
            StartPageWidgetKind? startPageKind,
            ShowcaseWidgetDefinition showcase)
        {
            return new StartPageViewDefinition
            {
                ViewId = viewId,
                WidgetKind = startPageKind,
                ShowcaseWidgetKind = showcase.Kind,
                NameKey = showcase.NameKey,
                HasSettings = true,
                Hidden = showcase.Hidden
            };
        }
    }
}
