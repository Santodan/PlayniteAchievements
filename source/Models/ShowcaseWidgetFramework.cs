using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Models
{
    public enum WidgetViewportDensity
    {
        Compact,
        Standard,
        Expanded
    }

    public enum WidgetViewportOrientation
    {
        Wide,
        Balanced,
        Tall
    }

    public sealed class WidgetViewportState
    {
        public WidgetViewportDensity Density { get; private set; }

        public WidgetViewportOrientation Orientation { get; private set; }

        public static WidgetViewportState Classify(double width, double height)
        {
            width = Math.Max(0, width);
            height = Math.Max(0, height);

            var density = width < 260 || height < 160
                ? WidgetViewportDensity.Compact
                : width >= 520 && height >= 320
                    ? WidgetViewportDensity.Expanded
                    : WidgetViewportDensity.Standard;

            var ratio = height <= 0 ? 1 : width / height;
            var orientation = ratio >= 1.35
                ? WidgetViewportOrientation.Wide
                : ratio <= 0.74
                    ? WidgetViewportOrientation.Tall
                    : WidgetViewportOrientation.Balanced;

            return new WidgetViewportState
            {
                Density = density,
                Orientation = orientation
            };
        }
    }

    public sealed class ShowcaseWidgetDefinition
    {
        public ShowcaseWidgetKind Kind { get; set; }

        public string NameKey { get; set; }

        public bool AllowMultipleInstances { get; set; }

        public bool SingleInstancePerPage { get; set; }

        /// <summary>
        /// Hidden kinds are omitted from the add-widget pickers but keep rendering already-placed
        /// widgets. NativePoints is parked here as underbaked rather than deleted.
        /// </summary>
        public bool Hidden { get; set; }
    }

    public static class ShowcaseWidgetCatalog
    {
        private static readonly IReadOnlyList<ShowcaseWidgetDefinition> DefinitionsValue =
            new List<ShowcaseWidgetDefinition>
            {
                Define(ShowcaseWidgetKind.Profile, "LOCPlayAch_Showcase_Widget_Profile", false, true),
                Define(ShowcaseWidgetKind.Scores, "LOCPlayAch_Showcase_Widget_Scores", true, false),
                Define(ShowcaseWidgetKind.Pie, "LOCPlayAch_Showcase_Widget_Pie", true, false),
                Define(ShowcaseWidgetKind.Timeline, "LOCPlayAch_Showcase_Widget_Timeline", true, false),
                Define(ShowcaseWidgetKind.Statistics, "LOCPlayAch_Showcase_Widget_Statistics", true, false),
                Define(ShowcaseWidgetKind.NativePoints, "LOCPlayAch_Showcase_Widget_NativePoints", true, false, hidden: true),
                Define(ShowcaseWidgetKind.IconMosaic, "LOCPlayAch_Showcase_Widget_IconMosaic", true, false),
                Define(ShowcaseWidgetKind.ScreenshotSlideshow, "LOCPlayAch_Showcase_Widget_ScreenshotSlideshow", true, false),
                Define(ShowcaseWidgetKind.RecentAchievements, "LOCPlayAch_Showcase_Widget_RecentAchievements", true, false),
                Define(ShowcaseWidgetKind.GameSummaries, "LOCPlayAch_Showcase_Widget_GameSummaries", true, false),
                Define(ShowcaseWidgetKind.ActivityCalendar, "LOCPlayAch_Showcase_Widget_ActivityCalendar", true, false),
                Define(ShowcaseWidgetKind.Image, "LOCPlayAch_Showcase_Widget_Image", true, false),
                Define(ShowcaseWidgetKind.Text, "LOCPlayAch_Showcase_Widget_Text", true, false)
            };

        public static IReadOnlyList<ShowcaseWidgetDefinition> Definitions => DefinitionsValue;

        public static ShowcaseWidgetDefinition Get(ShowcaseWidgetKind kind)
        {
            return DefinitionsValue.First(definition => definition.Kind == kind);
        }

        private static ShowcaseWidgetDefinition Define(
            ShowcaseWidgetKind kind,
            string nameKey,
            bool allowMultipleInstances,
            bool singleInstancePerPage,
            bool hidden = false)
        {
            return new ShowcaseWidgetDefinition
            {
                Kind = kind,
                NameKey = nameKey,
                AllowMultipleInstances = allowMultipleInstances,
                SingleInstancePerPage = singleInstancePerPage,
                Hidden = hidden
            };
        }
    }

    /// <summary>
    /// Builds and maintains the per-instance grid surface keys used by showcase grid widgets.
    /// Orphaned surfaces are pruned against the live widget instances.
    /// </summary>
    public static class ShowcaseGridSurfaces
    {
        public const string RecentAchievements = "ShowcaseRecentAchievements";
        public const string GameSummaries = "ShowcaseGameSummaries";

        private const char InstanceSeparator = ':';

        public static string ForInstance(string baseKey, string instanceId)
        {
            return string.IsNullOrWhiteSpace(instanceId)
                ? baseKey
                : baseKey + InstanceSeparator + instanceId.Trim();
        }

        public static string GetBaseKey(string columnSettingsKey)
        {
            if (string.IsNullOrWhiteSpace(columnSettingsKey))
            {
                return columnSettingsKey;
            }

            var separator = columnSettingsKey.IndexOf(InstanceSeparator);
            return separator < 0 ? columnSettingsKey : columnSettingsKey.Substring(0, separator);
        }

        public static bool IsAchievementSurface(string columnSettingsKey)
        {
            return string.Equals(
                GetBaseKey(columnSettingsKey),
                RecentAchievements,
                StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsGameSurface(string columnSettingsKey)
        {
            return string.Equals(
                GetBaseKey(columnSettingsKey),
                GameSummaries,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Resolves the per-instance grid surface key owned by a widget, or null for kinds that
        /// do not host a grid.
        /// </summary>
        public static string ResolveWidgetSurface(ShowcaseWidgetKind kind, string instanceId)
        {
            switch (kind)
            {
                case ShowcaseWidgetKind.RecentAchievements:
                    return ForInstance(RecentAchievements, instanceId);
                case ShowcaseWidgetKind.GameSummaries:
                    return ForInstance(GameSummaries, instanceId);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Copies the grid surface owned by <paramref name="source"/> onto the surface owned by
        /// <paramref name="copy"/>, so a cloned widget keeps its columns, widths, and sort.
        /// No-op for kinds that do not host a grid.
        /// </summary>
        public static void CopySurface(
            GridOptionsCatalog catalog,
            ShowcaseWidgetInstanceSettings source,
            ShowcaseWidgetInstanceSettings copy)
        {
            if (catalog == null || source == null || copy == null)
            {
                return;
            }

            var sourceKey = ResolveWidgetSurface(source.Kind, source.InstanceId);
            var targetKey = ResolveWidgetSurface(copy.Kind, copy.InstanceId);
            if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
            {
                return;
            }

            if (IsAchievementSurface(sourceKey) && IsAchievementSurface(targetKey))
            {
                catalog.SetAchievement(targetKey, catalog.GetAchievement(sourceKey));
            }
            else if (IsGameSurface(sourceKey) && IsGameSurface(targetKey))
            {
                catalog.SetGameSummaries(targetKey, catalog.GetGameSummaries(sourceKey));
            }
        }

        /// <summary>
        /// Removes persisted per-instance grid surfaces whose widget instance no longer exists
        /// (dashboard or start-page hosted). Bare base keys are never pruned.
        /// </summary>
        public static void PruneOrphaned(GridOptionsCatalog catalog, ShowcaseSettings showcase)
        {
            if (catalog == null || showcase == null)
            {
                return;
            }

            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var widget in showcase.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
            {
                if (!string.IsNullOrWhiteSpace(widget?.InstanceId))
                {
                    live.Add(widget.InstanceId.Trim());
                }
            }

            foreach (var widget in (showcase.StartPageInstances ??
                new Dictionary<string, ShowcaseWidgetInstanceSettings>()).Values)
            {
                if (!string.IsNullOrWhiteSpace(widget?.InstanceId))
                {
                    live.Add(widget.InstanceId.Trim());
                }
            }

            foreach (var key in catalog.Achievement.Keys
                .Where(key => IsOrphanedInstanceKey(key, IsAchievementSurface, live))
                .ToList())
            {
                catalog.RemoveAchievement(key);
            }

            foreach (var key in catalog.GameSummaries.Keys
                .Where(key => IsOrphanedInstanceKey(key, IsGameSurface, live))
                .ToList())
            {
                catalog.RemoveGameSummaries(key);
            }
        }

        private static bool IsOrphanedInstanceKey(
            string key,
            Func<string, bool> isShowcaseSurface,
            HashSet<string> liveInstanceIds)
        {
            if (string.IsNullOrWhiteSpace(key) || !isShowcaseSurface(key))
            {
                return false;
            }

            var separator = key.IndexOf(InstanceSeparator);
            if (separator < 0)
            {
                return false;
            }

            var instanceId = key.Substring(separator + 1).Trim();
            return instanceId.Length > 0 && !liveInstanceIds.Contains(instanceId);
        }
    }

    /// <summary>
    /// The time window shared by the Timeline, Scores, and Activity Calendar widgets, stored under
    /// one option key as the <see cref="TimeWindow"/> canonical string (a preset name or a custom
    /// range), plus the Timeline chart's granularity override.
    /// </summary>
    public static class ShowcaseTimelineOptions
    {
        /// <summary>Option keys, for a widget's own controls to save their choices under.</summary>
        public const string RangeOption = "TimelineRange";
        public const string GranularityOption = "TimelineGranularity";
        private const string LegacyRangeDaysOption = "RangeDays";

        public static readonly TimeWindow DefaultWindow = TimeWindow.FromPreset(TimelineRange.ThreeMonths);

        public static TimeWindow GetWindow(ShowcaseWidgetInstanceSettings instance)
        {
            if (instance?.Options != null &&
                instance.Options.TryGetValue(RangeOption, out var raw) &&
                TimeWindow.TryParse(raw, out var window))
            {
                return window;
            }

            var legacyDays = instance?.GetOption(LegacyRangeDaysOption, 90) ?? 90;
            if (legacyDays <= 31)
            {
                return TimeWindow.FromPreset(TimelineRange.OneMonth);
            }

            if (legacyDays <= 93)
            {
                return TimeWindow.FromPreset(TimelineRange.ThreeMonths);
            }

            if (legacyDays <= 366)
            {
                return TimeWindow.FromPreset(TimelineRange.OneYear);
            }

            return TimeWindow.All;
        }

        public static void SetWindow(ShowcaseWidgetInstanceSettings instance, TimeWindow window)
        {
            if (instance == null)
            {
                return;
            }

            instance.SetOption(RangeOption, (window ?? DefaultWindow).ToKey());
            instance.Options?.Remove(LegacyRangeDaysOption);
        }

        public static TimelineGranularity GetGranularity(ShowcaseWidgetInstanceSettings instance) =>
            instance?.GetOption(GranularityOption, TimelineGranularity.Auto) ?? TimelineGranularity.Auto;

        public static void SetGranularity(ShowcaseWidgetInstanceSettings instance, TimelineGranularity value) =>
            instance?.SetOption(GranularityOption, value);

        /// <summary>Splits each Timeline bar into one stacked segment per platform.</summary>
        public static bool GetSplitByPlatform(ShowcaseWidgetInstanceSettings instance) =>
            instance?.GetOption(SplitByPlatformOption, false) ?? false;

        public static void SetSplitByPlatform(ShowcaseWidgetInstanceSettings instance, bool value) =>
            instance?.SetOption(SplitByPlatformOption, value);

        public const string SplitByPlatformOption = "TimelineSplitByPlatform";
    }

    /// <summary>
    /// Control bar item IDs, which name an item's visibility option. Stable: persisted per widget.
    /// </summary>
    public static class ShowcaseControlBarItems
    {
        public const string Search = "Search";
        public const string Platform = "Platform";
        public const string Game = "Game";
        public const string UnlockDate = "UnlockDate";
        public const string Rarity = "Rarity";
        public const string Trophy = "Trophy";
        public const string Progress = "Progress";
        public const string Activity = "Activity";

        /// <summary>The Timeline and Activity Calendar time window picker.</summary>
        public const string TimeWindow = "TimeWindow";

        /// <summary>The Timeline's split-by-platform toggle.</summary>
        public const string SplitByPlatform = "SplitByPlatform";
    }

    /// <summary>
    /// Owns persisted option names, defaults, and defensive range validation.
    /// Renderers and editors should not interpret the option dictionary directly.
    /// </summary>
    public static class ShowcaseWidgetOptions
    {
        private const string Mode = "Mode";
        private const string ScoreHistory = "ScoreHistory";
        private const string Grouping = "Grouping";
        private const string TopN = "TopN";
        private const string Source = "Source";
        private const string Count = "Count";
        private const string Variant = "Variant";
        private const string IntervalSeconds = "IntervalSeconds";
        private const string FitMode = "FitMode";
        private const string Shuffle = "Shuffle";
        private const string HideCompleted = "HideCompleted";
        private const string ShowRarityGlow = "ShowRarityGlow";
        private const string ShowRarityBar = "ShowRarityBar";
        // The whole-bar option per-item visibility replaced; read only as the per-item fallback.
        private const string ShowControlBar = "ShowControlBar";
        private const string ControlBarItemPrefix = "ShowControlBarItem.";
        private const string UseCoverImages = "UseCoverImages";
        private const string ShowCompletionGlow = "ShowCompletionGlow";
        private const string ShowCompletionFrame = "ShowCompletionFrame";
        private const string CenterMode = "CenterMode";
        private const string LegacyShowCenterPercentage = "ShowCenterPercentage";
        private const string ShowLegend = "ShowLegend";
        private const string ShowIcons = "ShowIcons";
        private const string LegendPosition = "LegendPosition";
        // Legacy Scores keys: read only to migrate a widget saved before the card slots existed.
        private const string CollectionBadgePosition = "CollectionBadgePosition";
        private const string PrestigeBadgePosition = "PrestigeBadgePosition";
        private const string ScoreCard = "ScoreCard";
        private const string ScoreCardBadgePositionKey = "ScoreCardBadgePosition";
        private const string ScoreHistoryShown = "ScoreHistoryShown";
        private const string ScoreCardUseCustomColors = "ScoreCardUseCustomColors";
        private const string ScoreCardShowTotal = "ScoreCardShowTotal";
        private const string IncludeLocked = "IncludeLocked";
        private const string SmallSliceMode = "SmallSliceMode";
        private const string ActivityScope = "ActivityScope";
        internal const string PinCollectionId = "PinCollectionId";
        private const string CollectionType = "CollectionType";
        private const string LegacyPinnedSource = "Pinned";
        private const string Content = "Content";
        private const string ProfileStats = "ProfileStats";
        private const string StatisticsStats = "StatisticsStats";
        private const string ProfileMedals = "ProfileMedals";
        private const string ProfileFullBleed = "ProfileFullBleed";
        private const string ProfileLayoutOption = "ProfileLayout";
        private const string ProfileLinks = "ProfileLinks";
        private const string MosaicIconSize = "MosaicIconSize";
        private const string MosaicCoverWidth = "MosaicCoverWidth";
        private const string MosaicSpacing = "MosaicSpacing";
        private const string Sort = "Sort";
        private const string SortDescending = "SortDescending";
        private const string UnlockNextCriterionOption = "UnlockNextCriterion";
        private const string LastPlayedWindow = "LastPlayedWindow";
        private const string FinishNextCriterionOption = "FinishNextCriterion";
        private const string FinishNextMinimumProgress = "FinishNextMinimumProgress";
        private const string FinishNextMaxRemaining = "FinishNextMaxRemaining";
        private const string FinishNextIncludeUnplayed = "FinishNextIncludeUnplayed";
        private const string MaxPerGame = "MaxPerGame";
        private const string IncludeHiddenAchievements = "IncludeHiddenAchievements";
        private const string InfoPanel = "InfoPanel";
        private const string ImagePath = "ImagePath";
        private const string TextContent = "Text";
        private const string HorizontalAlign = "HorizontalAlignment";
        private const string TitleAlign = "TitleAlignment";
        private const string ShowBackground = "ShowBackground";
        private const string VerticalAlign = "VerticalAlignment";
        private const string TextSizeOption = "TextSize";
        private const string TextPixelSizeOption = "TextPixelSize";
        private const string FontFamilyOption = "FontFamily";

        /// <summary>Option key of the Image widget's stored file, which travels as a bundled file rather than as an option value.</summary>
        public const string ImagePathKey = ImagePath;

        /// <summary>Stat keys the profile stat slots show when the option is unset.</summary>
        public static readonly IReadOnlyList<string> DefaultProfileStatKeys = new[]
        {
            "completedGames",
            "completion",
            "playtime",
            "activeDayRate"
        };

        /// <summary>
        /// Keys of the overall statistics filling the profile widget's stat slots, one per
        /// slot in slot order, unbounded. An unset option means
        /// <see cref="DefaultProfileStatKeys"/>; a stored empty value means no slots and the
        /// strip hides.
        /// </summary>
        public static IReadOnlyList<string> GetProfileStatKeys(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Options == null ||
                !settings.Options.TryGetValue(ProfileStats, out var raw))
            {
                return DefaultProfileStatKeys;
            }

            return (raw ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim())
                .Where(key => key.Length > 0)
                .ToList();
        }

        public static void SetProfileStatKeys(
            ShowcaseWidgetInstanceSettings settings,
            IEnumerable<string> keys)
        {
            if (settings == null)
            {
                return;
            }

            if (settings.Options == null)
            {
                settings.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            settings.Options[ProfileStats] = string.Join(
                ",",
                (keys ?? Enumerable.Empty<string>())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim()));
        }

        /// <summary>
        /// Keys of the statistics a Statistics widget shows, in order; null while unset, which
        /// means every statistic.
        /// </summary>
        public static IReadOnlyList<string> GetStatisticsKeys(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Options == null || !settings.Options.TryGetValue(StatisticsStats, out var raw))
            {
                return null;
            }

            return (raw ?? string.Empty)
                .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(key => key.Trim())
                .Where(key => key.Length > 0)
                .ToList();
        }

        public static void SetStatisticsKeys(ShowcaseWidgetInstanceSettings settings, IEnumerable<string> keys)
        {
            settings?.SetOption(
                StatisticsStats,
                string.Join(",", (keys ?? Enumerable.Empty<string>())
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key.Trim())));
        }

        /// <summary>
        /// Which kind of pin collection the widget is limited to; the collection itself is
        /// <see cref="GetPinCollectionId"/>. Only achievement widgets take achievement
        /// collections, and only widgets with a game or achievement list take one at all. A widget
        /// saved with the former Pinned source reads as limited to that source's collection.
        /// </summary>
        public static ShowcaseCollectionType GetCollectionType(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings == null)
            {
                return ShowcaseCollectionType.None;
            }

            var type = settings.Options?.ContainsKey(CollectionType) == true
                ? GetEnum(settings, CollectionType, ShowcaseCollectionType.None)
                : LegacyCollectionType(settings);
            switch (type)
            {
                case ShowcaseCollectionType.Achievements:
                    return OffersAchievementCollections(settings) ? type : ShowcaseCollectionType.None;
                case ShowcaseCollectionType.Games:
                    return OffersCollections(settings) ? type : ShowcaseCollectionType.None;
                default:
                    return ShowcaseCollectionType.None;
            }
        }

        public static void SetCollectionType(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseCollectionType value) => settings?.SetOption(CollectionType, value);

        /// <summary>Whether the widget lists achievements, so it can take an achievement collection.</summary>
        public static bool OffersAchievementCollections(ShowcaseWidgetInstanceSettings settings)
        {
            switch (settings?.Kind)
            {
                case ShowcaseWidgetKind.RecentAchievements:
                    return true;
                case ShowcaseWidgetKind.IconMosaic:
                    return GetMosaicContent(settings) == ShowcaseMosaicContent.Achievements;
                default:
                    return false;
            }
        }

        /// <summary>Whether the widget lists games or achievements, so it can take a game collection.</summary>
        public static bool OffersCollections(ShowcaseWidgetInstanceSettings settings)
        {
            switch (settings?.Kind)
            {
                case ShowcaseWidgetKind.RecentAchievements:
                case ShowcaseWidgetKind.GameSummaries:
                case ShowcaseWidgetKind.IconMosaic:
                case ShowcaseWidgetKind.Pie:
                    return true;
                default:
                    return false;
            }
        }

        // A source saved as the former "Pinned" stands for its collection: an achievement one on
        // the achievement grid and achievement mosaic, a game one on the game grid and game mosaic.
        private static ShowcaseCollectionType LegacyCollectionType(ShowcaseWidgetInstanceSettings settings)
        {
            if (!IsLegacyPinnedSource(settings))
            {
                return ShowcaseCollectionType.None;
            }

            return OffersAchievementCollections(settings)
                ? ShowcaseCollectionType.Achievements
                : ShowcaseCollectionType.Games;
        }

        private static bool IsLegacyPinnedSource(ShowcaseWidgetInstanceSettings settings) =>
            settings?.Options != null &&
            settings.Options.TryGetValue(Source, out var raw) &&
            string.Equals(raw?.Trim(), LegacyPinnedSource, StringComparison.OrdinalIgnoreCase);

        // Writes a former Pinned source's collection type before a source or content change
        // replaces the value it is read from.
        private static void KeepLegacyCollection(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings != null &&
                settings.Options?.ContainsKey(CollectionType) != true &&
                IsLegacyPinnedSource(settings))
            {
                SetCollectionType(settings, LegacyCollectionType(settings));
            }
        }

        public static string GetPinCollectionId(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Options == null ||
                !settings.Options.TryGetValue(PinCollectionId, out var value))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        public static void SetPinCollectionId(
            ShowcaseWidgetInstanceSettings settings,
            string collectionId)
        {
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(collectionId))
            {
                settings.Options?.Remove(PinCollectionId);
                return;
            }

            if (settings.Options == null)
            {
                settings.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            settings.Options[PinCollectionId] = collectionId.Trim();
        }

        /// <summary>
        /// The score a Scores widget shows. A widget saved before the card option existed has
        /// only the legacy Mode: Prestige reads as Prestige, anything else as Collection. Layouts
        /// split a legacy Both widget in two on load (see ShowcaseLayoutService.Normalize).
        /// </summary>
        public static ScoreCardType GetScoreCardType(ShowcaseWidgetInstanceSettings settings)
        {
            if (HasOption(settings, ScoreCard))
            {
                return ScoreCardType.ParseOrDefault(settings.Options[ScoreCard], ScoreCardType.Collection);
            }

            return GetEnum(settings, Mode, ShowcaseScoreMode.Dual) == ShowcaseScoreMode.Prestige
                ? ScoreCardType.Prestige
                : ScoreCardType.Collection;
        }

        public static void SetScoreCardType(ShowcaseWidgetInstanceSettings settings, ScoreCardType value) =>
            settings?.SetOption(ScoreCard, value.Key);

        /// <summary>
        /// Which side the card's badge sits on. A widget saved before the option existed keeps the
        /// legacy Collection or Prestige badge side of the score it shows.
        /// </summary>
        public static ScoreCardBadgePosition GetScoreCardBadgePosition(ShowcaseWidgetInstanceSettings settings)
        {
            if (HasOption(settings, ScoreCardBadgePositionKey))
            {
                return GetEnum(settings, ScoreCardBadgePositionKey, ScoreCardBadgePosition.Left);
            }

            return GetEnum(
                settings,
                GetScoreCardType(settings) == ScoreCardType.Prestige ? PrestigeBadgePosition : CollectionBadgePosition,
                ScoreCardBadgePosition.Left);
        }

        public static void SetScoreCardBadgePosition(
            ShowcaseWidgetInstanceSettings settings,
            ScoreCardBadgePosition value) => settings?.SetOption(ScoreCardBadgePositionKey, value);

        /// <summary>
        /// Whether the card carries the score-over-time line. A widget saved before the option
        /// existed shows it when the legacy choice was Both or named the score it shows; unset
        /// everywhere means shown, so a layout saved before either option keeps its chart.
        /// </summary>
        public static bool GetScoreHistoryShown(ShowcaseWidgetInstanceSettings settings)
        {
            if (HasOption(settings, ScoreHistoryShown))
            {
                return settings.GetOption(ScoreHistoryShown, true);
            }

            switch (GetEnum(settings, ScoreHistory, ShowcaseScoreHistoryMode.Dual))
            {
                case ShowcaseScoreHistoryMode.None:
                    return false;
                case ShowcaseScoreHistoryMode.Collection:
                    return GetScoreCardType(settings) == ScoreCardType.Collection;
                case ShowcaseScoreHistoryMode.Prestige:
                    return GetScoreCardType(settings) == ScoreCardType.Prestige;
                default:
                    return true;
            }
        }

        public static void SetScoreHistoryShown(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ScoreHistoryShown, value);

        /// <summary>Whether the card uses the configured rarity colors; unset means the shipped ones.</summary>
        public static bool GetScoreCardUseCustomColors(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ScoreCardUseCustomColors, false) ?? false;

        public static void SetScoreCardUseCustomColors(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ScoreCardUseCustomColors, value);

        /// <summary>Whether a points card shows earned out of total; unset means shown. Earned alone otherwise.</summary>
        public static bool GetScoreCardShowTotal(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ScoreCardShowTotal, true) ?? true;

        public static void SetScoreCardShowTotal(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ScoreCardShowTotal, value);

        /// <summary>True for a Scores widget saved with the legacy Mode of Both and no card option.</summary>
        public static bool IsLegacyDualScores(ShowcaseWidgetInstanceSettings settings) =>
            settings?.Kind == ShowcaseWidgetKind.Scores &&
            !HasOption(settings, ScoreCard) &&
            GetEnum(settings, Mode, ShowcaseScoreMode.Dual) == ShowcaseScoreMode.Dual;

        /// <summary>
        /// Writes a Scores widget's legacy options out as the one-card options (card, badge side,
        /// chart) and removes the legacy keys. A legacy Both widget becomes its Collection card.
        /// </summary>
        public static void MigrateLegacyScoreOptions(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Kind != ShowcaseWidgetKind.Scores)
            {
                return;
            }

            var type = GetScoreCardType(settings);
            SetScoreCardType(settings, type);
            SetScoreCardBadgePosition(settings, GetScoreCardBadgePosition(settings));
            SetScoreHistoryShown(settings, GetScoreHistoryShown(settings));
            foreach (var key in new[] { Mode, ScoreHistory, CollectionBadgePosition, PrestigeBadgePosition })
            {
                settings.Options?.Remove(key);
            }
        }

        /// <summary>
        /// Splits a legacy Both widget: this widget becomes its Collection card and the returned
        /// copy, under a new instance id, its Prestige card, each with that score's badge side and
        /// chart choice. Null for any other widget.
        /// </summary>
        public static ShowcaseWidgetInstanceSettings SplitLegacyDualScores(ShowcaseWidgetInstanceSettings settings)
        {
            if (!IsLegacyDualScores(settings))
            {
                return null;
            }

            var prestige = settings.Clone();
            prestige.InstanceId = Guid.NewGuid().ToString("N");
            SetScoreCardType(prestige, ScoreCardType.Prestige);
            MigrateLegacyScoreOptions(prestige);
            MigrateLegacyScoreOptions(settings);
            return prestige;
        }

        private static bool HasOption(ShowcaseWidgetInstanceSettings settings, string key) =>
            settings?.Options != null &&
            settings.Options.TryGetValue(key, out var raw) &&
            !string.IsNullOrWhiteSpace(raw);
        public static ShowcasePieMode GetPieMode(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Mode, ShowcasePieMode.CompletedGames);

        public static void SetPieMode(ShowcaseWidgetInstanceSettings settings, ShowcasePieMode value) =>
            settings?.SetOption(Mode, value);

        /// <summary>
        /// Falls back to the legacy show-percentage toggle for widgets saved before the center
        /// mode existed: on reads as <see cref="PieCenterMode.Percentage"/>, off as
        /// <see cref="PieCenterMode.Empty"/>.
        /// </summary>
        public static PieCenterMode GetPieCenterMode(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Options != null && settings.Options.ContainsKey(CenterMode))
            {
                return GetEnum(settings, CenterMode, PieCenterMode.Percentage);
            }

            return settings?.GetOption(LegacyShowCenterPercentage, true) ?? true
                ? PieCenterMode.Percentage
                : PieCenterMode.Empty;
        }

        public static void SetPieCenterMode(ShowcaseWidgetInstanceSettings settings, PieCenterMode value)
        {
            if (settings == null)
            {
                return;
            }

            settings.SetOption(CenterMode, value);
            settings.Options?.Remove(LegacyShowCenterPercentage);
        }

        /// <summary>
        /// Whether the pie draws its trailing locked slice. Ignored by the completions mode,
        /// whose trailing slice counts unfinished games rather than locked achievements.
        /// </summary>
        public static bool GetPieIncludeLocked(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(IncludeLocked, true) ?? true;

        public static void SetPieIncludeLocked(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(IncludeLocked, value);

        public static bool GetPieShowLegend(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowLegend, true) ?? true;

        public static void SetPieShowLegend(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ShowLegend, value);

        public static PieLegendPosition GetPieLegendPosition(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, LegendPosition, PieLegendPosition.Right);

        public static void SetPieLegendPosition(ShowcaseWidgetInstanceSettings settings, PieLegendPosition value) =>
            settings?.SetOption(LegendPosition, value);

        public static bool GetPieShowIcons(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowIcons, true) ?? true;

        public static void SetPieShowIcons(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ShowIcons, value);

        public static OverviewPieSmallSliceMode GetPieSmallSliceMode(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, SmallSliceMode, OverviewPieSmallSliceMode.Round);

        public static void SetPieSmallSliceMode(
            ShowcaseWidgetInstanceSettings settings,
            OverviewPieSmallSliceMode value) => settings?.SetOption(SmallSliceMode, value);

        /// <summary>
        /// Prefix of the widget options holding control bar filter state (search text and filter
        /// selections). Those options belong to the user, not the layout: an export strips them,
        /// and the widget settings dialog leaves them as the bar last saved them.
        /// </summary>
        public const string ControlBarStatePrefix = "ControlBar.";

        /// <summary>
        /// Whether the widget's control bar shows the item <paramref name="itemId"/> (one of
        /// <see cref="ShowcaseControlBarItems"/>). A hidden item's filter stays in effect, and its
        /// editor stays in the widget settings. Without a per-item choice, the widget's former
        /// whole-bar option decides: <paramref name="legacyVisible"/> when the caller has one (a
        /// grid widget's surface option), else the widget's own Show Control Bar option.
        /// </summary>
        public static bool GetControlBarItemVisible(
            ShowcaseWidgetInstanceSettings settings,
            string itemId,
            bool? legacyVisible = null)
        {
            if (settings == null || string.IsNullOrWhiteSpace(itemId))
            {
                return false;
            }

            if (settings.Options != null &&
                settings.Options.TryGetValue(ControlBarItemPrefix + itemId, out var raw) &&
                bool.TryParse(raw, out var visible))
            {
                return visible;
            }

            return legacyVisible ?? settings.GetOption(ShowControlBar, false);
        }

        public static void SetControlBarItemVisible(
            ShowcaseWidgetInstanceSettings settings,
            string itemId,
            bool value)
        {
            if (!string.IsNullOrWhiteSpace(itemId))
            {
                settings?.SetOption(ControlBarItemPrefix + itemId, value);
            }
        }

        /// <summary>
        /// The settings dialog's edited options applied over <paramref name="target"/>'s: every
        /// option comes from <paramref name="edited"/> except the control bar filter state, which
        /// the bar saves to the live widget while the dialog is open and the edited copy only
        /// holds as it was when the dialog opened.
        /// </summary>
        public static Dictionary<string, string> MergeEditedOptions(
            ShowcaseWidgetInstanceSettings target,
            ShowcaseWidgetInstanceSettings edited)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in edited?.Options ?? new Dictionary<string, string>())
            {
                if (!IsControlBarState(pair.Key))
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in target?.Options ?? new Dictionary<string, string>())
            {
                if (IsControlBarState(pair.Key))
                {
                    merged[pair.Key] = pair.Value;
                }
            }

            return merged;
        }

        public static bool IsControlBarState(string key) =>
            key != null && key.StartsWith(ControlBarStatePrefix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Which counts the profile medal row shows. Trophy is only meaningful for a library
        /// holding PlayStation-shaped games; every other library sums to zero and the row hides.
        /// </summary>
        public static ShowcaseProfileMedalMode GetProfileMedalMode(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, ProfileMedals, ShowcaseProfileMedalMode.Rarity);

        public static void SetProfileMedalMode(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseProfileMedalMode value) => settings?.SetOption(ProfileMedals, value);

        /// <summary>
        /// Whether the profile background fills the whole card, edge to edge, instead of sitting
        /// inside the body inset. The foreground content keeps the inset either way.
        /// </summary>
        public static bool GetProfileFullBleed(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ProfileFullBleed, false) ?? false;

        public static void SetProfileFullBleed(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ProfileFullBleed, value);

        /// <summary>How the profile arranges its blocks: left-aligned, centered as they are, or stacked.</summary>
        public static ShowcaseProfileLayout GetProfileLayout(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, ProfileLayoutOption, ShowcaseProfileLayout.Left);

        public static void SetProfileLayout(ShowcaseWidgetInstanceSettings settings, ShowcaseProfileLayout value) =>
            settings?.SetOption(ProfileLayoutOption, value);

        /// <summary>Whether the profile shows its row of clickable platform profile links.</summary>
        public static bool GetProfileShowLinks(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ProfileLinks, true) ?? true;

        public static void SetProfileShowLinks(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ProfileLinks, value);

        public static GameActivityScope GetGameActivityScope(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, ActivityScope, GameActivityScope.All);

        public static void SetGameActivityScope(
            ShowcaseWidgetInstanceSettings settings,
            GameActivityScope value) => settings?.SetOption(ActivityScope, value);

        public static ShowcasePointsGrouping GetPointsGrouping(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Grouping, ShowcasePointsGrouping.Provider);

        public static void SetPointsGrouping(
            ShowcaseWidgetInstanceSettings settings,
            ShowcasePointsGrouping value) => settings?.SetOption(Grouping, value);

        public static int GetTopN(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(TopN, 8) ?? 8, 1, 25);

        public static void SetTopN(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(TopN, Clamp(value, 1, 25));

        public static ShowcaseMosaicSource GetMosaicSource(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Source, ShowcaseMosaicSource.Recent);

        public static void SetMosaicSource(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseMosaicSource value)
        {
            KeepLegacyCollection(settings);
            settings?.SetOption(Source, value);
        }

        /// <summary>Standard-density achievement icon size, shown when no size is set.</summary>
        public const int DefaultMosaicIconSize = 42;

        /// <summary>Standard-density game cover width, shown when no size is set.</summary>
        public const int DefaultMosaicCoverWidth = 56;

        /// <summary>
        /// A fixed achievement icon size in pixels, or null to follow the widget's density (the
        /// original behaviour). The editor shows <see cref="DefaultMosaicIconSize"/> until set.
        /// </summary>
        public static int? GetMosaicIconSizeOverride(ShowcaseWidgetInstanceSettings settings) =>
            GetPixelOverride(settings, MosaicIconSize);

        public static int GetMosaicIconSize(ShowcaseWidgetInstanceSettings settings) =>
            GetMosaicIconSizeOverride(settings) ?? DefaultMosaicIconSize;

        public static void SetMosaicIconSize(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(MosaicIconSize, Clamp(value, MinMosaicTileSize, MaxMosaicTileSize));

        /// <summary>A fixed game cover width in pixels, or null to follow the widget's density.</summary>
        public static int? GetMosaicCoverWidthOverride(ShowcaseWidgetInstanceSettings settings) =>
            GetPixelOverride(settings, MosaicCoverWidth);

        public static int GetMosaicCoverWidth(ShowcaseWidgetInstanceSettings settings) =>
            GetMosaicCoverWidthOverride(settings) ?? DefaultMosaicCoverWidth;

        public static void SetMosaicCoverWidth(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(MosaicCoverWidth, Clamp(value, MinMosaicTileSize, MaxMosaicTileSize));

        private const int MinMosaicTileSize = 16;
        private const int MaxMosaicTileSize = 256;

        private static int? GetPixelOverride(ShowcaseWidgetInstanceSettings settings, string key)
        {
            if (settings?.Options == null || !settings.Options.ContainsKey(key))
            {
                return null;
            }

            return Clamp(settings.GetOption(key, 0), MinMosaicTileSize, MaxMosaicTileSize);
        }

        /// <summary>Space around each mosaic tile, in pixels (0-40); 6 is the original fixed margin.</summary>
        public static int GetMosaicSpacing(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(MosaicSpacing, 6) ?? 6, 0, 40);

        public static void SetMosaicSpacing(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(MosaicSpacing, Clamp(value, 0, 40));

        public static int GetMosaicCount(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(Count, 24) ?? 24, 1, 200);

        public static void SetMosaicCount(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(Count, Clamp(value, 1, 200));

        /// <summary>Whether achievement mosaic tiles show the rarity bar along their bottom edge.</summary>
        public static bool GetMosaicShowRarityBar(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowRarityBar, false) ?? false;

        public static void SetMosaicShowRarityBar(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ShowRarityBar, value);

        public static bool GetMosaicShowRarityGlow(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowRarityGlow, true) ?? true;

        public static void SetMosaicShowRarityGlow(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ShowRarityGlow, value);

        // Mosaic sort is stored alongside Source and Count, so the achievement and game contents
        // share the option keys. Enum options round-trip by name, so a mode belonging to the
        // other content fails to parse and falls back to that content's source order.
        public static CompactListSortMode GetMosaicSort(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Sort, CompactListSortMode.None);

        public static void SetMosaicSort(
            ShowcaseWidgetInstanceSettings settings,
            CompactListSortMode value) => settings?.SetOption(Sort, value);

        public static GameSummariesSortMode GetGameMosaicSort(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Sort, GameSummariesSortMode.PinOrder);

        public static void SetGameMosaicSort(
            ShowcaseWidgetInstanceSettings settings,
            GameSummariesSortMode value) => settings?.SetOption(Sort, value);

        public static bool GetMosaicSortDescending(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(SortDescending, true) ?? true;

        public static void SetMosaicSortDescending(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(SortDescending, value);

        public static UnlockNextCriterion GetUnlockNextCriterion(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, UnlockNextCriterionOption, UnlockNextCriterion.ClosestToCompletion);

        public static void SetUnlockNextCriterion(
            ShowcaseWidgetInstanceSettings settings,
            UnlockNextCriterion value) => settings?.SetOption(UnlockNextCriterionOption, value);

        /// <summary>
        /// How recently a game must have been played to contribute to Unlock Next / Finish Next.
        /// Shares no key with <see cref="ShowcaseTimelineOptions"/>, whose range means a chart
        /// window rather than a library filter.
        /// </summary>
        public static FinishNextCriterion GetFinishNextCriterion(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, FinishNextCriterionOption, FinishNextCriterion.ClosestToCompletion);

        public static void SetFinishNextCriterion(
            ShowcaseWidgetInstanceSettings settings,
            FinishNextCriterion value) => settings?.SetOption(FinishNextCriterionOption, value);

        /// <summary>Finish Next only lists games at least this far along, in percent (0-99).</summary>
        public static int GetFinishNextMinimumProgress(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(FinishNextMinimumProgress, 0) ?? 0, 0, 99);

        public static void SetFinishNextMinimumProgress(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(FinishNextMinimumProgress, Clamp(value, 0, 99));

        /// <summary>Finish Next only lists games with at most this many achievements left; 0 means any.</summary>
        public static int GetFinishNextMaxRemaining(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(FinishNextMaxRemaining, 0) ?? 0, 0, 9999);

        public static void SetFinishNextMaxRemaining(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(FinishNextMaxRemaining, Clamp(value, 0, 9999));

        /// <summary>
        /// Whether Finish Next keeps games with no last-played date inside a played-within window
        /// (they have nothing to compare, so a window otherwise drops them).
        /// </summary>
        public static bool GetFinishNextIncludeUnplayed(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(FinishNextIncludeUnplayed, false) ?? false;

        public static void SetFinishNextIncludeUnplayed(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(FinishNextIncludeUnplayed, value);

        public static readonly TimeWindow DefaultLastPlayedWindow = TimeWindow.FromPreset(TimelineRange.OneMonth);

        /// <summary>
        /// The "played within" window for Unlock Next and Finish Next: a preset or a custom range
        /// the game's last-played day must fall inside. Stored as the <see cref="TimeWindow"/> key.
        /// </summary>
        public static TimeWindow GetLastPlayedTimeWindow(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings?.Options != null &&
                settings.Options.TryGetValue(LastPlayedWindow, out var raw) &&
                TimeWindow.TryParse(raw, out var window))
            {
                return window;
            }

            return DefaultLastPlayedWindow;
        }

        public static void SetLastPlayedTimeWindow(ShowcaseWidgetInstanceSettings settings, TimeWindow window) =>
            settings?.SetOption(LastPlayedWindow, (window ?? DefaultLastPlayedWindow).ToKey());

        /// <summary>
        /// Most rows a single game may contribute to Unlock Next. The pool the overview builder
        /// hydrates retains a bounded slice per game, so the value caps at 10, well below it,
        /// rather than accepting an amount the pool could not honour.
        /// </summary>
        public static int GetMaxPerGame(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(MaxPerGame, 1) ?? 1, 1, 10);

        public static void SetMaxPerGame(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(MaxPerGame, Clamp(value, 1, 10));

        public static bool GetIncludeHiddenAchievements(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(IncludeHiddenAchievements, false) ?? false;

        public static void SetIncludeHiddenAchievements(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(IncludeHiddenAchievements, value);

        /// <summary>
        /// Whether this widget instance draws from the Unlock Next candidate pool. The overview
        /// builder only pays for the pool when some live widget asks for it, so the builder, the
        /// dashboard refresh, and the plugin's invalidation hook all test it through here.
        /// </summary>
        public static bool RequiresUnlockNextPool(ShowcaseWidgetInstanceSettings settings)
        {
            if (settings == null)
            {
                return false;
            }

            switch (settings.Kind)
            {
                case ShowcaseWidgetKind.IconMosaic:
                    return GetMosaicContent(settings) == ShowcaseMosaicContent.Achievements &&
                        GetMosaicSource(settings) == ShowcaseMosaicSource.UnlockNext;
                case ShowcaseWidgetKind.RecentAchievements:
                    return GetAchievementGridSource(settings) == ShowcaseAchievementGridSource.UnlockNext;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Whether any live widget - dashboard or start page - draws from the Unlock Next pool.
        /// </summary>
        public static bool RequiresUnlockNextPool(ShowcaseSettings showcase)
        {
            if (showcase == null)
            {
                return false;
            }

            if ((showcase.WidgetInstances ?? new List<ShowcaseWidgetInstanceSettings>())
                .Any(RequiresUnlockNextPool))
            {
                return true;
            }

            return (showcase.StartPageInstances ??
                    new Dictionary<string, ShowcaseWidgetInstanceSettings>())
                .Values
                .Any(RequiresUnlockNextPool);
        }

        public static ShowcaseMosaicContent GetMosaicContent(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Content, ShowcaseMosaicContent.Achievements);

        public static void SetMosaicContent(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseMosaicContent value)
        {
            KeepLegacyCollection(settings);
            settings?.SetOption(Content, value);
        }

        public static ShowcaseAchievementGridSource GetAchievementGridSource(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Source, ShowcaseAchievementGridSource.All);

        public static void SetAchievementGridSource(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseAchievementGridSource value)
        {
            KeepLegacyCollection(settings);
            settings?.SetOption(Source, value);
        }

        public static ShowcaseGameGridSource GetGameGridSource(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Source, ShowcaseGameGridSource.Library);

        public static void SetGameGridSource(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseGameGridSource value)
        {
            KeepLegacyCollection(settings);
            settings?.SetOption(Source, value);
        }

        public static ShowcaseSlideshowSource GetSlideshowSource(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Source, ShowcaseSlideshowSource.All);

        public static void SetSlideshowSource(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseSlideshowSource value) => settings?.SetOption(Source, value);

        public static ShowcaseScreenshotVariant GetScreenshotVariant(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, Variant, ShowcaseScreenshotVariant.All);

        public static void SetScreenshotVariant(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseScreenshotVariant value) => settings?.SetOption(Variant, value);

        public static int GetSlideshowIntervalSeconds(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(IntervalSeconds, 8) ?? 8, 1, 300);

        public static void SetSlideshowIntervalSeconds(
            ShowcaseWidgetInstanceSettings settings,
            int value) => settings?.SetOption(IntervalSeconds, Clamp(value, 1, 300));

        public static ShowcaseImageFitMode GetImageFitMode(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, FitMode, ShowcaseImageFitMode.Fit);

        public static void SetImageFitMode(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseImageFitMode value) => settings?.SetOption(FitMode, value);

        public static bool GetShuffle(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(Shuffle, true) ?? true;

        public static void SetShuffle(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(Shuffle, value);

        public static ShowcaseInfoPanelPosition GetInfoPanelPosition(
            ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, InfoPanel, ShowcaseInfoPanelPosition.Off);

        public static void SetInfoPanelPosition(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseInfoPanelPosition value) => settings?.SetOption(InfoPanel, value);

        public static bool GetHideCompleted(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(HideCompleted, false) ?? false;

        public static void SetHideCompleted(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(HideCompleted, value);

        // A former Pinned game mosaic showed every pinned game, so it reads as All with its collection.
        public static ShowcaseGameMosaicSource GetGameMosaicSource(ShowcaseWidgetInstanceSettings settings) =>
            IsLegacyPinnedSource(settings)
                ? ShowcaseGameMosaicSource.All
                : GetEnum(settings, Source, ShowcaseGameMosaicSource.Completed);

        public static void SetGameMosaicSource(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseGameMosaicSource value)
        {
            KeepLegacyCollection(settings);
            settings?.SetOption(Source, value);
        }

        public static int GetGameMosaicCount(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(Count, 24) ?? 24, 1, 200);

        public static void SetGameMosaicCount(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(Count, Clamp(value, 1, 200));

        public static bool GetGameMosaicUseCovers(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(UseCoverImages, true) ?? true;

        public static void SetGameMosaicUseCovers(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(UseCoverImages, value);

        public static bool GetGameMosaicShowCompletionGlow(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowCompletionGlow, true) ?? true;

        public static void SetGameMosaicShowCompletionGlow(
            ShowcaseWidgetInstanceSettings settings,
            bool value) => settings?.SetOption(ShowCompletionGlow, value);

        /// <summary>
        /// Whether completed games' tiles get the completion frame: a band in the completed brush
        /// and the completion badge on the cover's bottom edge.
        /// </summary>
        public static bool GetGameMosaicShowCompletionFrame(ShowcaseWidgetInstanceSettings settings) =>
            settings?.GetOption(ShowCompletionFrame, false) ?? false;

        public static void SetGameMosaicShowCompletionFrame(
            ShowcaseWidgetInstanceSettings settings,
            bool value) => settings?.SetOption(ShowCompletionFrame, value);

        /// <summary>The Image widget's stored file, or null while none is picked.</summary>
        public static string GetImagePath(ShowcaseWidgetInstanceSettings settings) =>
            GetString(settings, ImagePath);

        public static void SetImagePath(ShowcaseWidgetInstanceSettings settings, string value) =>
            SetString(settings, ImagePath, value);

        /// <summary>The Text widget's text, in inline markdown; null while empty.</summary>
        public static string GetText(ShowcaseWidgetInstanceSettings settings) =>
            GetString(settings, TextContent, trim: false);

        public static void SetText(ShowcaseWidgetInstanceSettings settings, string value) =>
            SetString(settings, TextContent, value, trim: false);

        public static ShowcaseHorizontalAlignment GetHorizontalAlignment(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, HorizontalAlign, ShowcaseHorizontalAlignment.Center);

        public static void SetHorizontalAlignment(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseHorizontalAlignment value) => settings?.SetOption(HorizontalAlign, value);

        /// <summary>Where a widget's custom title sits in its header; unset means left.</summary>
        public static ShowcaseHorizontalAlignment GetTitleAlignment(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, TitleAlign, ShowcaseHorizontalAlignment.Left);

        public static void SetTitleAlignment(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseHorizontalAlignment value) => settings?.SetOption(TitleAlign, value);

        /// <summary>
        /// Whether the widget draws its card surface and outline. Unset means the host's default:
        /// shown on the showcase, hidden on the start page, where widgets sit on the page itself.
        /// </summary>
        public static bool GetShowBackground(ShowcaseWidgetInstanceSettings settings, bool hostDefault) =>
            settings?.GetOption(ShowBackground, hostDefault) ?? hostDefault;

        public static void SetShowBackground(ShowcaseWidgetInstanceSettings settings, bool value) =>
            settings?.SetOption(ShowBackground, value);

        public static ShowcaseVerticalAlignment GetVerticalAlignment(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, VerticalAlign, ShowcaseVerticalAlignment.Center);

        public static void SetVerticalAlignment(
            ShowcaseWidgetInstanceSettings settings,
            ShowcaseVerticalAlignment value) => settings?.SetOption(VerticalAlign, value);

        public static ShowcaseTextSize GetTextSize(ShowcaseWidgetInstanceSettings settings) =>
            GetEnum(settings, TextSizeOption, ShowcaseTextSize.Normal);

        public static void SetTextSize(ShowcaseWidgetInstanceSettings settings, ShowcaseTextSize value) =>
            settings?.SetOption(TextSizeOption, value);

        /// <summary>The Text widget's size in pixels while its size is Custom.</summary>
        public static int GetTextPixelSize(ShowcaseWidgetInstanceSettings settings) =>
            Clamp(settings?.GetOption(TextPixelSizeOption, 24) ?? 24, 6, 200);

        public static void SetTextPixelSize(ShowcaseWidgetInstanceSettings settings, int value) =>
            settings?.SetOption(TextPixelSizeOption, Clamp(value, 6, 200));

        /// <summary>The Text widget's font family name; null inherits the theme font.</summary>
        public static string GetFontFamily(ShowcaseWidgetInstanceSettings settings) =>
            GetString(settings, FontFamilyOption);

        public static void SetFontFamily(ShowcaseWidgetInstanceSettings settings, string value) =>
            SetString(settings, FontFamilyOption, value);

        private static string GetString(ShowcaseWidgetInstanceSettings settings, string key, bool trim = true)
        {
            if (settings?.Options == null || !settings.Options.TryGetValue(key, out var value))
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(value) ? null : trim ? value.Trim() : value;
        }

        // A blank value removes the key, so an unset option and a cleared one read the same.
        private static void SetString(ShowcaseWidgetInstanceSettings settings, string key, string value, bool trim = true)
        {
            if (settings == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                settings.Options?.Remove(key);
                return;
            }

            if (settings.Options == null)
            {
                settings.Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            settings.Options[key] = trim ? value.Trim() : value;
        }

        private static T GetEnum<T>(
            ShowcaseWidgetInstanceSettings settings,
            string key,
            T fallback)
            where T : struct
        {
            var value = settings?.GetOption(key, fallback) ?? fallback;
            return Enum.IsDefined(typeof(T), value) ? value : fallback;
        }

        private static int Clamp(int value, int minimum, int maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));
    }

    public static class ShowcaseWidgetSettingsFactory
    {
        public static ShowcaseWidgetInstanceSettings CreateDefault(
            ShowcaseWidgetKind kind,
            string instanceId = null)
        {
            var settings = new ShowcaseWidgetInstanceSettings
            {
                Kind = kind
            };
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                settings.InstanceId = instanceId.Trim();
            }

            switch (kind)
            {
                case ShowcaseWidgetKind.Scores:
                    ShowcaseWidgetOptions.SetScoreCardType(settings, ScoreCardType.Collection);
                    ShowcaseWidgetOptions.SetScoreCardBadgePosition(settings, ScoreCardBadgePosition.Left);
                    ShowcaseWidgetOptions.SetScoreHistoryShown(settings, true);
                    ShowcaseTimelineOptions.SetWindow(settings, TimeWindow.FromPreset(TimelineRange.ThreeMonths));
                    break;
                case ShowcaseWidgetKind.Pie:
                    ShowcaseWidgetOptions.SetPieMode(settings, ShowcasePieMode.CompletedGames);
                    ShowcaseWidgetOptions.SetPieCenterMode(settings, PieCenterMode.Percentage);
                    ShowcaseWidgetOptions.SetPieShowLegend(settings, true);
                    ShowcaseWidgetOptions.SetPieLegendPosition(settings, PieLegendPosition.Right);
                    ShowcaseWidgetOptions.SetPieShowIcons(settings, true);
                    ShowcaseWidgetOptions.SetPieIncludeLocked(settings, true);
                    ShowcaseWidgetOptions.SetPieSmallSliceMode(settings, OverviewPieSmallSliceMode.Round);
                    break;
                case ShowcaseWidgetKind.Profile:
                    // A new profile card starts blank and shows the provider identity until edited.
                    settings.Profile = new ShowcaseProfileSettings();
                    ShowcaseWidgetOptions.SetProfileMedalMode(settings, ShowcaseProfileMedalMode.Rarity);
                    ShowcaseWidgetOptions.SetProfileFullBleed(settings, false);
                    ShowcaseWidgetOptions.SetProfileLayout(settings, ShowcaseProfileLayout.Left);
                    ShowcaseWidgetOptions.SetProfileShowLinks(settings, true);
                    break;
                case ShowcaseWidgetKind.Timeline:
                    ShowcaseTimelineOptions.SetWindow(settings, TimeWindow.FromPreset(TimelineRange.ThreeMonths));
                    ShowcaseTimelineOptions.SetGranularity(settings, TimelineGranularity.Auto);
                    break;
                case ShowcaseWidgetKind.NativePoints:
                    ShowcaseWidgetOptions.SetPointsGrouping(settings, ShowcasePointsGrouping.Provider);
                    ShowcaseWidgetOptions.SetTopN(settings, 8);
                    break;
                case ShowcaseWidgetKind.IconMosaic:
                    ShowcaseWidgetOptions.SetMosaicContent(settings, ShowcaseMosaicContent.Achievements);
                    ShowcaseWidgetOptions.SetMosaicSource(settings, ShowcaseMosaicSource.Recent);
                    ShowcaseWidgetOptions.SetMosaicCount(settings, 24);
                    ShowcaseWidgetOptions.SetMosaicShowRarityGlow(settings, true);
                    ShowcaseWidgetOptions.SetMosaicSort(settings, CompactListSortMode.None);
                    ShowcaseWidgetOptions.SetMosaicSortDescending(settings, true);
                    break;
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                    ShowcaseWidgetOptions.SetSlideshowSource(settings, ShowcaseSlideshowSource.All);
                    ShowcaseWidgetOptions.SetScreenshotVariant(settings, ShowcaseScreenshotVariant.All);
                    ShowcaseWidgetOptions.SetShuffle(settings, true);
                    ShowcaseWidgetOptions.SetSlideshowIntervalSeconds(settings, 8);
                    ShowcaseWidgetOptions.SetImageFitMode(settings, ShowcaseImageFitMode.Fit);
                    ShowcaseWidgetOptions.SetInfoPanelPosition(settings, ShowcaseInfoPanelPosition.Off);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    ShowcaseWidgetOptions.SetAchievementGridSource(settings, ShowcaseAchievementGridSource.All);
                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    ShowcaseWidgetOptions.SetGameGridSource(settings, ShowcaseGameGridSource.Library);
                    ShowcaseWidgetOptions.SetHideCompleted(settings, false);
                    ShowcaseWidgetOptions.SetGameActivityScope(settings, GameActivityScope.All);
                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    ShowcaseTimelineOptions.SetWindow(settings, TimeWindow.All);
                    break;
                case ShowcaseWidgetKind.Image:
                    ShowcaseWidgetOptions.SetImageFitMode(settings, ShowcaseImageFitMode.Fill);
                    ShowcaseWidgetOptions.SetHorizontalAlignment(settings, ShowcaseHorizontalAlignment.Center);
                    ShowcaseWidgetOptions.SetVerticalAlignment(settings, ShowcaseVerticalAlignment.Center);
                    break;
                case ShowcaseWidgetKind.Text:
                    ShowcaseWidgetOptions.SetHorizontalAlignment(settings, ShowcaseHorizontalAlignment.Center);
                    ShowcaseWidgetOptions.SetVerticalAlignment(settings, ShowcaseVerticalAlignment.Center);
                    ShowcaseWidgetOptions.SetTextSize(settings, ShowcaseTextSize.Normal);
                    break;
            }

            return settings;
        }
    }

    /// <summary>
    /// The one image file a widget carries as page content: a profile card's background or an
    /// Image widget's picture. Export bundles it, import and library updates restore it, and the
    /// image store keeps it from being pruned. The profile avatar identifies the user and is not
    /// part of this.
    /// </summary>
    public static class ShowcaseWidgetImages
    {
        public static string GetFilePath(ShowcaseWidgetInstanceSettings widget)
        {
            switch (widget?.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    return string.IsNullOrWhiteSpace(widget.Profile?.BackgroundPath) ? null : widget.Profile.BackgroundPath;
                case ShowcaseWidgetKind.Image:
                    return ShowcaseWidgetOptions.GetImagePath(widget);
                default:
                    return null;
            }
        }

        public static void SetFilePath(ShowcaseWidgetInstanceSettings widget, string path)
        {
            switch (widget?.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    if (widget.Profile != null || !string.IsNullOrWhiteSpace(path))
                    {
                        widget.Profile = widget.Profile ?? new ShowcaseProfileSettings();
                        widget.Profile.BackgroundPath = path;
                    }

                    break;
                case ShowcaseWidgetKind.Image:
                    ShowcaseWidgetOptions.SetImagePath(widget, path);
                    break;
            }
        }
    }
}
