using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.Showcase.Widgets;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Settings.Controls;
using static PlayniteAchievements.Views.Showcase.ShowcaseUiText;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// Shared option editor for Showcase and StartPage widget instances.
    /// Host-specific save and cancel behavior stays with the owning surface.
    /// </summary>
    internal sealed class ShowcaseWidgetOptionsControl : UserControl
    {
        private readonly ShowcaseWidgetInstanceSettings _settings;
        private readonly Action _persist;
        private readonly bool _publishChanges;
        private readonly bool _includeScoreHistory;
        private readonly bool? _backgroundDefault;
        private readonly UIElement _contentAfterSharedRows;
        private readonly ShowcaseWidgetInstanceSettings _liveWidget;
        private DebouncedSettingsPersist _gridOptionsPersist;

        // What a score card type change replaced on the live widget, kept so a cancelled dialog
        // can put it back; null until the type changes.
        private ScoreCardType? _liveTypeBeforeChange;
        private List<string> _gamesBeforeTypeChange;
        private FrameworkElement _scoreBarRows;

        // The control bar items' show checkboxes, which only apply while the widget shows its
        // control bar. Their filters apply either way, so the editors beside them stay enabled.
        private readonly List<FrameworkElement> _controlBarItemRows = new List<FrameworkElement>();

        // The widget's control bars, whose Game filters a collection choice limits.
        private readonly List<SharedControlBarAdapter> _controlBarAdapters = new List<SharedControlBarAdapter>();

        /// <param name="includeScoreHistory">
        /// False for a score card with no history chart (the Overview header's cards), which drops
        /// the history toggle and range.
        /// </param>
        /// <param name="backgroundDefault">
        /// The host's default for the widget background, which adds the title alignment and
        /// background rows every widget shares; null for an editor with no widget card around it.
        /// </param>
        /// <param name="contentAfterSharedRows">
        /// The host's own editor for the widget (the profile's identity fields), placed after the
        /// shared title alignment and background rows so those stay under the title box.
        /// </param>
        /// <param name="liveWidget">
        /// The widget on screen when <paramref name="settings"/> is a working copy. A score card's
        /// type applies to it at once, as its control bar filters do, so the filters list the new
        /// type's platforms and games; <see cref="RevertLivePreview"/> undoes that on cancel.
        /// </param>
        public ShowcaseWidgetOptionsControl(
            ShowcaseWidgetInstanceSettings settings,
            Action persist = null,
            bool publishChanges = true,
            Thickness? margin = null,
            bool loadStyles = true,
            bool includeScoreHistory = true,
            bool? backgroundDefault = null,
            UIElement contentAfterSharedRows = null,
            ShowcaseWidgetInstanceSettings liveWidget = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _liveWidget = ReferenceEquals(liveWidget, settings) ? null : liveWidget;
            _includeScoreHistory = includeScoreHistory;
            _backgroundDefault = backgroundDefault;
            _contentAfterSharedRows = contentAfterSharedRows;
            _persist = persist;
            _publishChanges = publishChanges;
            if (loadStyles)
            {
                Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "pack://application:,,,/PlayniteAchievements;component/Resources/PlayAchImplicitControlStyles.xaml",
                        UriKind.Absolute)
                });
            }
            Content = Build(margin ?? new Thickness(16));
            FormattingCulture.Apply(this);
        }

        public static bool HasOptions(ShowcaseWidgetKind kind)
        {
            switch (kind)
            {
                case ShowcaseWidgetKind.Profile:
                case ShowcaseWidgetKind.Scores:
                case ShowcaseWidgetKind.Pie:
                case ShowcaseWidgetKind.Timeline:
                case ShowcaseWidgetKind.Statistics:
                case ShowcaseWidgetKind.NativePoints:
                case ShowcaseWidgetKind.IconMosaic:
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                case ShowcaseWidgetKind.RecentAchievements:
                case ShowcaseWidgetKind.GameSummaries:
                case ShowcaseWidgetKind.ActivityCalendar:
                case ShowcaseWidgetKind.Image:
                case ShowcaseWidgetKind.Text:
                    return true;
                default:
                    return false;
            }
        }

        // Dropdowns and control bar item editors share one height (their labels do not take part,
        // so a wrapped label grows only its own row), so the two kinds of row space evenly.
        private const string ControlRowSizeGroup = "OptionControlRow";

        private UIElement Build(Thickness margin)
        {
            var panel = new StackPanel { Margin = margin };
            Grid.SetIsSharedSizeScope(panel, true);
            if (_backgroundDefault.HasValue)
            {
                AddChoice(
                    panel,
                    Localize("LOCPlayAch_Settings_Display_Alignment"),
                    new[]
                    {
                        ShowcaseHorizontalAlignment.Left,
                        ShowcaseHorizontalAlignment.Center,
                        ShowcaseHorizontalAlignment.Right
                    },
                    ShowcaseWidgetOptions.GetTitleAlignment(_settings),
                    value => ShowcaseWidgetOptions.SetTitleAlignment(_settings, value),
                    HorizontalAlignmentName);
                AddToggle(
                    panel,
                    Localize("LOCPlayAch_Showcase_ShowBackground"),
                    ShowcaseWidgetOptions.GetShowBackground(_settings, _backgroundDefault.Value),
                    value => ShowcaseWidgetOptions.SetShowBackground(_settings, value));
            }

            if (_contentAfterSharedRows != null)
            {
                panel.Children.Add(_contentAfterSharedRows);
            }

            switch (_settings.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileMedals"),
                        new[]
                        {
                            ShowcaseProfileMedalMode.Rarity,
                            ShowcaseProfileMedalMode.Trophy,
                            ShowcaseProfileMedalMode.Both,
                            ShowcaseProfileMedalMode.None
                        },
                        ShowcaseWidgetOptions.GetProfileMedalMode(_settings),
                        value => ShowcaseWidgetOptions.SetProfileMedalMode(_settings, value),
                        ProfileMedalModeName);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Common_Layout"),
                        new[]
                        {
                            ShowcaseProfileLayout.Left,
                            ShowcaseProfileLayout.Centered,
                            ShowcaseProfileLayout.Stacked
                        },
                        ShowcaseWidgetOptions.GetProfileLayout(_settings),
                        value => ShowcaseWidgetOptions.SetProfileLayout(_settings, value),
                        ProfileLayoutName);
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileFullBleed"),
                        ShowcaseWidgetOptions.GetProfileFullBleed(_settings),
                        value => ShowcaseWidgetOptions.SetProfileFullBleed(_settings, value));
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileLinks"),
                        ShowcaseWidgetOptions.GetProfileShowLinks(_settings),
                        value => ShowcaseWidgetOptions.SetProfileShowLinks(_settings, value));
                    // Prefilled with the defaults; left at them, the picture follows the widget's
                    // density. Width and height are separate so the picture need not be square.
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileAvatarWidth") + " (px)",
                        () => ShowcaseWidgetOptions.GetProfileAvatarWidth(_settings),
                        value => ShowcaseWidgetOptions.SetProfileAvatarWidth(_settings, value));
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileAvatarHeight") + " (px)",
                        () => ShowcaseWidgetOptions.GetProfileAvatarHeight(_settings),
                        value => ShowcaseWidgetOptions.SetProfileAvatarHeight(_settings, value));
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileLinkIconSize") + " (px)",
                        () => ShowcaseWidgetOptions.GetProfileLinkIconSize(_settings),
                        value => ShowcaseWidgetOptions.SetProfileLinkIconSize(_settings, value));
                    AddProfileStatSlots(panel);
                    AddCollectionChoice(panel, includeAchievementCollections: false);
                    AddControlBarRows<StatisticsControlBarAdapter>(panel);
                    break;
                case ShowcaseWidgetKind.Scores:
                    AddScoreCardOptions(panel);
                    // A points card sums one provider's points, which its bar narrows to games;
                    // the library-wide Collection and Prestige cards have no bar.
                    var scoreBarRows = new StackPanel();
                    _scoreBarRows = scoreBarRows;
                    panel.Children.Add(scoreBarRows);
                    AddControlBarRows<ChartControlBarAdapter>(scoreBarRows);
                    SyncScoreBarRows(ShowcaseWidgetOptions.GetScoreCardType(_settings));
                    break;
                case ShowcaseWidgetKind.Pie:
                    // Only a Provider pie can count games instead of achievements, and a pie
                    // counting games has no achievement filters.
                    FrameworkElement pieCountRow = null;
                    void ApplyPieRows(ShowcasePieMode mode)
                    {
                        if (pieCountRow != null)
                        {
                            pieCountRow.Visibility = mode == ShowcasePieMode.Provider
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        }

                        ApplyAchievementFilterRows();
                    }

                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Mode"),
                        new[]
                        {
                            ShowcasePieMode.CompletedGames,
                            ShowcasePieMode.Provider,
                            ShowcasePieMode.Rarity,
                            ShowcasePieMode.Trophy,
                            ShowcasePieMode.Tag
                        },
                        ShowcaseWidgetOptions.GetPieMode(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetPieMode(_settings, value);
                            ApplyPieRows(value);
                        },
                        PieModeName);
                    pieCountRow = AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Variant"),
                        new[] { ShowcasePieCount.Achievements, ShowcasePieCount.CompletedGames },
                        ShowcaseWidgetOptions.GetPieCount(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetPieCount(_settings, value);
                            ApplyAchievementFilterRows();
                        },
                        value => Localize(value == ShowcasePieCount.CompletedGames
                            ? "LOCPlayAch_Showcase_GameMosaicSource_Completed"
                            : "LOCPlayAch_Achievements"));
                    ApplyPieRows(ShowcaseWidgetOptions.GetPieMode(_settings));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_PieCenter"),
                        new[]
                        {
                            PieCenterMode.Percentage,
                            PieCenterMode.Empty,
                            PieCenterMode.Filled
                        },
                        ShowcaseWidgetOptions.GetPieCenterMode(_settings),
                        value => ShowcaseWidgetOptions.SetPieCenterMode(_settings, value),
                        PieCenterModeName);
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Showcase_ShowLegend"),
                        ShowcaseWidgetOptions.GetShowLegend(_settings),
                        value => ShowcaseWidgetOptions.SetShowLegend(_settings, value));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_PieLegendPosition"),
                        new[] { PieLegendPosition.Left, PieLegendPosition.Right },
                        ShowcaseWidgetOptions.GetPieLegendPosition(_settings),
                        value => ShowcaseWidgetOptions.SetPieLegendPosition(_settings, value),
                        PieLegendPositionName);
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Settings_ShowOverviewPieIcons"),
                        ShowcaseWidgetOptions.GetPieShowIcons(_settings),
                        value => ShowcaseWidgetOptions.SetPieShowIcons(_settings, value));
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Settings_PieIncludeLocked"),
                        ShowcaseWidgetOptions.GetPieIncludeLocked(_settings),
                        value => ShowcaseWidgetOptions.SetPieIncludeLocked(_settings, value));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_OverviewPieSmallSliceMode"),
                        new[]
                        {
                            OverviewPieSmallSliceMode.Round,
                            OverviewPieSmallSliceMode.Exact,
                            OverviewPieSmallSliceMode.Hide
                        },
                        ShowcaseWidgetOptions.GetPieSmallSliceMode(_settings),
                        value => ShowcaseWidgetOptions.SetPieSmallSliceMode(_settings, value),
                        SmallSliceModeName);
                    AddCollectionChoice(panel, includeAchievementCollections: false);
                    _achievementFilterRows = AddControlBarRows<PieControlBarAdapter>(panel);
                    ApplyAchievementFilterRows();
                    break;
                case ShowcaseWidgetKind.Timeline:
                    AddCountChoice(panel);

                    AddRangeChoice(panel);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Timeline_SplitBy"),
                        new[]
                        {
                            ShowcaseTimelineSplit.None,
                            ShowcaseTimelineSplit.Platform,
                            ShowcaseTimelineSplit.Rarity,
                            ShowcaseTimelineSplit.Trophy
                        },
                        ShowcaseTimelineOptions.GetSplit(_settings),
                        value => ShowcaseTimelineOptions.SetSplit(_settings, value),
                        TimelineSplitName);
                    AddTimeControlRows(panel, includeSplit: true);
                    _achievementFilterRows = AddControlBarRows<TimelineControlBarAdapter>(panel, header: false);
                    ApplyAchievementFilterRows();
                    break;
                case ShowcaseWidgetKind.Statistics:
                    AddStatisticsSlots(panel);
                    AddCollectionChoice(panel, includeAchievementCollections: false);
                    AddControlBarRows<StatisticsControlBarAdapter>(panel);
                    break;
                case ShowcaseWidgetKind.NativePoints:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Common_GroupBy"),
                        new[] { ShowcasePointsGrouping.Provider, ShowcasePointsGrouping.Game },
                        ShowcaseWidgetOptions.GetPointsGrouping(_settings),
                        value => ShowcaseWidgetOptions.SetPointsGrouping(_settings, value),
                        PointsGroupingName);
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_TopN"),
                        () => ShowcaseWidgetOptions.GetTopN(_settings),
                        value => ShowcaseWidgetOptions.SetTopN(_settings, value));
                    break;
                case ShowcaseWidgetKind.IconMosaic:
                    // The collapsed Mosaic widget: the Content choice flips between the
                    // achievement-icon rows and the game-cover rows; the count is shared.
                    var achievementMosaicPanel = new StackPanel();
                    var gameMosaicPanel = new StackPanel();
                    // Each content has its own control bar, listed last.
                    var achievementMosaicBarPanel = new StackPanel();
                    var gameMosaicBarPanel = new StackPanel();
                    void ApplyMosaicContent(ShowcaseMosaicContent content)
                    {
                        var achievements = content == ShowcaseMosaicContent.Achievements
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        var games = content == ShowcaseMosaicContent.Games
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                        achievementMosaicPanel.Visibility = achievements;
                        achievementMosaicBarPanel.Visibility = achievements;
                        gameMosaicPanel.Visibility = games;
                        gameMosaicBarPanel.Visibility = games;
                    }

                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Mode"),
                        new[] { ShowcaseMosaicContent.Achievements, ShowcaseMosaicContent.Games },
                        ShowcaseWidgetOptions.GetMosaicContent(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetMosaicContent(_settings, value);
                            ApplyMosaicContent(value);
                        },
                        MosaicContentName);
                    panel.Children.Add(achievementMosaicPanel);
                    panel.Children.Add(gameMosaicPanel);
                    ApplyMosaicContent(ShowcaseWidgetOptions.GetMosaicContent(_settings));

                    var unlockNextRows = new List<FrameworkElement>();
                    var achievementSortRows = new List<FrameworkElement>();
                    AddToggle(
                        achievementMosaicPanel,
                        MosaicSourceName(ShowcaseMosaicSource.UnlockNext),
                        ShowcaseWidgetOptions.GetMosaicSource(_settings) == ShowcaseMosaicSource.UnlockNext,
                        unlockNext =>
                        {
                            var value = unlockNext ? ShowcaseMosaicSource.UnlockNext : ShowcaseMosaicSource.All;
                            ShowcaseWidgetOptions.SetMosaicSource(_settings, value);
                            ApplyUnlockNextVisibility(unlockNextRows, achievementSortRows, value);
                        });

                    // Unlock Next ranks during selection, so its criterion replaces the generic
                    // sort rows rather than stacking with them.
                    unlockNextRows.AddRange(AddUnlockNextOptions(achievementMosaicPanel));


                    AddToggle(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Settings_ToastShowRarityGlow"),
                        ShowcaseWidgetOptions.GetMosaicShowRarityGlow(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicShowRarityGlow(_settings, value));
                    AddToggle(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Settings_ShowCompactListRarityBar"),
                        ShowcaseWidgetOptions.GetMosaicShowRarityBar(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicShowRarityBar(_settings, value));
                    achievementSortRows.Add(AddChoice(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Common_SortBy"),
                        new[]
                        {
                            CompactListSortMode.None,
                            CompactListSortMode.UnlockTime,
                            CompactListSortMode.Rarity
                        },
                        ShowcaseWidgetOptions.GetMosaicSort(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicSort(_settings, value),
                        MosaicSortName));
                    achievementSortRows.Add(AddSortDirectionChoice(achievementMosaicPanel));
                    ApplyUnlockNextVisibility(
                        unlockNextRows,
                        achievementSortRows,
                        ShowcaseWidgetOptions.GetMosaicSource(_settings));

                    FrameworkElement finishNextOptions = null;
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Common_Source"),
                        new[]
                        {
                            ShowcaseGameMosaicSource.All,
                            ShowcaseGameMosaicSource.PlayniteFavorites,
                            ShowcaseGameMosaicSource.FinishNext
                        },
                        ShowcaseWidgetOptions.GetGameMosaicSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetGameMosaicSource(_settings, value);
                            if (finishNextOptions != null)
                            {
                                finishNextOptions.Visibility = value == ShowcaseGameMosaicSource.FinishNext
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }
                        },
                        GameMosaicSourceName);
                    finishNextOptions = AddFinishNextOptions(gameMosaicPanel);
                    finishNextOptions.Visibility = ShowcaseWidgetOptions.GetGameMosaicSource(_settings) ==
                        ShowcaseGameMosaicSource.FinishNext
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    AddToggle(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_UseCoverImages"),
                        ShowcaseWidgetOptions.GetGameMosaicUseCovers(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicUseCovers(_settings, value));
                    AddToggle(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_ShowCompletionGlow"),
                        ShowcaseWidgetOptions.GetGameMosaicShowCompletionGlow(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicShowCompletionGlow(_settings, value));
                    AddToggle(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_ShowCompletionFrame"),
                        ShowcaseWidgetOptions.GetGameMosaicShowCompletionFrame(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicShowCompletionFrame(_settings, value));
                    // Completed games only: the capstone's rarity, or the rarest achievement
                    // when the game has no capstone.
                    AddToggle(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_ShowCompactListRarityBar"),
                        ShowcaseWidgetOptions.GetMosaicShowRarityBar(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicShowRarityBar(_settings, value));
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Common_SortBy"),
                        new[]
                        {
                            GameSummariesSortMode.PinOrder,
                            GameSummariesSortMode.RecentUnlock,
                            GameSummariesSortMode.LastPlayed,
                            GameSummariesSortMode.TotalAchievements,
                            GameSummariesSortMode.Progress,
                            GameSummariesSortMode.Alphabetical
                        },
                        ShowcaseWidgetOptions.GetGameMosaicSort(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicSort(_settings, value),
                        GameMosaicSortName);
                    AddSortDirectionChoice(gameMosaicPanel);

                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_ItemCount"),
                        () => ShowcaseWidgetOptions.GetMosaicCount(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicCount(_settings, value));
                    // Tile size in pixels, per content (icons and covers have different defaults),
                    // prefilled with the default; leaving it at the default keeps the size following
                    // the widget's density. Spacing is the gap around each tile. Both clamp on
                    // commit, and the box shows the stored value.
                    AddNumberRow(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Common_Size") + " (px)",
                        () => ShowcaseWidgetOptions.GetMosaicIconSize(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicIconSize(_settings, value));
                    AddNumberRow(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Common_Size") + " (px)",
                        () => ShowcaseWidgetOptions.GetMosaicCoverWidth(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicCoverWidth(_settings, value));
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_TileSpacing"),
                        () => ShowcaseWidgetOptions.GetMosaicSpacing(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicSpacing(_settings, value));
                    panel.Children.Add(achievementMosaicBarPanel);
                    panel.Children.Add(gameMosaicBarPanel);
                    AddCollectionChoice(achievementMosaicBarPanel, includeAchievementCollections: true);
                    AddControlBarRows<CrossGameAchievementControlBarAdapter>(achievementMosaicBarPanel);
                    AddCollectionChoice(gameMosaicBarPanel, includeAchievementCollections: false);
                    AddControlBarRows<GameSummaryGridControlBarAdapter>(gameMosaicBarPanel);
                    break;
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_Interval") + " (s)",
                        () => ShowcaseWidgetOptions.GetSlideshowIntervalSeconds(_settings),
                        value => ShowcaseWidgetOptions.SetSlideshowIntervalSeconds(_settings, value));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_FitMode"),
                        new[] { ShowcaseImageFitMode.Fit, ShowcaseImageFitMode.Fill },
                        ShowcaseWidgetOptions.GetImageFitMode(_settings),
                        value => ShowcaseWidgetOptions.SetImageFitMode(_settings, value),
                        FitModeName);
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Showcase_Shuffle"),
                        ShowcaseWidgetOptions.GetShuffle(_settings),
                        value => ShowcaseWidgetOptions.SetShuffle(_settings, value));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_InfoPanel"),
                        new[]
                        {
                            ShowcaseInfoPanelPosition.Off,
                            ShowcaseInfoPanelPosition.Left,
                            ShowcaseInfoPanelPosition.Right,
                            ShowcaseInfoPanelPosition.Bottom
                        },
                        ShowcaseWidgetOptions.GetInfoPanelPosition(_settings),
                        value => ShowcaseWidgetOptions.SetInfoPanelPosition(_settings, value),
                        InfoPanelPositionName);
                    AddCollectionChoice(panel, includeAchievementCollections: true);
                    AddControlBarRows<SlideshowControlBarAdapter>(panel);
                    break;
                case ShowcaseWidgetKind.Image:
                    ShowcaseProfileSettingsEditor.AddImagePicker(
                        panel,
                        Localize("LOCPlayAch_Showcase_Widget_Image"),
                        () => ShowcaseWidgetOptions.GetImagePath(_settings),
                        value => ShowcaseWidgetOptions.SetImagePath(_settings, value),
                        Committed);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_FitMode"),
                        new[] { ShowcaseImageFitMode.Fit, ShowcaseImageFitMode.Fill },
                        ShowcaseWidgetOptions.GetImageFitMode(_settings),
                        value => ShowcaseWidgetOptions.SetImageFitMode(_settings, value),
                        FitModeName);
                    AddAlignmentChoices(panel);
                    break;
                case ShowcaseWidgetKind.Text:
                    AddMultilineText(
                        panel,
                        Localize("LOCPlayAch_Settings_Appearance_Resource_Text"),
                        () => ShowcaseWidgetOptions.GetText(_settings),
                        value => ShowcaseWidgetOptions.SetText(_settings, value));
                    FrameworkElement textPixelSizeRow = null;
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_TextSize"),
                        new[]
                        {
                            ShowcaseTextSize.Small,
                            ShowcaseTextSize.Normal,
                            ShowcaseTextSize.Large,
                            ShowcaseTextSize.Huge,
                            ShowcaseTextSize.Custom
                        },
                        ShowcaseWidgetOptions.GetTextSize(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetTextSize(_settings, value);
                            if (textPixelSizeRow != null)
                            {
                                textPixelSizeRow.Visibility = value == ShowcaseTextSize.Custom
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }
                        },
                        TextSizeName);
                    textPixelSizeRow = AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_TextSize") + " (px)",
                        () => ShowcaseWidgetOptions.GetTextPixelSize(_settings),
                        value => ShowcaseWidgetOptions.SetTextPixelSize(_settings, value));
                    textPixelSizeRow.Visibility = ShowcaseWidgetOptions.GetTextSize(_settings) == ShowcaseTextSize.Custom
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    AddFontFamilyChoice(panel);
                    AddAlignmentChoices(panel);
                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    // The collapsed Game Summaries Grid: progress and activity are its control
                    // bar's filters.
                    FrameworkElement gameGridFinishNextOptions = null;
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Common_Source"),
                        new[]
                        {
                            ShowcaseGameGridSource.Library,
                            ShowcaseGameGridSource.PlayniteFavorites,
                            ShowcaseGameGridSource.FinishNext
                        },
                        ShowcaseWidgetOptions.GetGameGridSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetGameGridSource(_settings, value);
                            if (gameGridFinishNextOptions != null)
                            {
                                gameGridFinishNextOptions.Visibility = value == ShowcaseGameGridSource.FinishNext
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }
                        },
                        GameGridSourceName);
                    // Finish Next's options, the same set as on the Finish Next game mosaic.
                    gameGridFinishNextOptions = AddFinishNextOptions(panel);
                    gameGridFinishNextOptions.Visibility =
                        ShowcaseWidgetOptions.GetGameGridSource(_settings) == ShowcaseGameGridSource.FinishNext
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    AddCollectionChoice(panel, includeAchievementCollections: false);
                    AddControlBarRows<GameSummaryGridControlBarAdapter>(panel);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    // The collapsed Achievements Grid: every achievement, or the Unlock Next
                    // candidates; a collection limits either.
                    var achievementGridUnlockNextRows = new List<FrameworkElement>();
                    AddToggle(
                        panel,
                        AchievementGridSourceName(ShowcaseAchievementGridSource.UnlockNext),
                        ShowcaseWidgetOptions.GetAchievementGridSource(_settings) == ShowcaseAchievementGridSource.UnlockNext,
                        unlockNext =>
                        {
                            var value = unlockNext ? ShowcaseAchievementGridSource.UnlockNext : ShowcaseAchievementGridSource.All;
                            ShowcaseWidgetOptions.SetAchievementGridSource(_settings, value);
                            ApplyAchievementGridSourceRows(achievementGridUnlockNextRows, value);
                        });
                    achievementGridUnlockNextRows.AddRange(AddUnlockNextOptions(panel));
                    ApplyAchievementGridSourceRows(
                        achievementGridUnlockNextRows,
                        ShowcaseWidgetOptions.GetAchievementGridSource(_settings));
                    AddCollectionChoice(panel, includeAchievementCollections: true);
                    AddControlBarRows<CrossGameAchievementControlBarAdapter>(panel);
                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    AddCountChoice(panel);
                    AddRangeChoice(panel);
                    AddToggle(
                        panel,
                        Localize("LOCPlayAch_Showcase_ShowLegend"),
                        ShowcaseWidgetOptions.GetShowLegend(_settings),
                        value => ShowcaseWidgetOptions.SetShowLegend(_settings, value));
                    AddColorRow(
                        panel,
                        Localize("LOCPlayAch_Common_Color"),
                        () => ShowcaseWidgetOptions.GetActivityColor(_settings),
                        value => ShowcaseWidgetOptions.SetActivityColor(_settings, value));
                    AddTimeControlRows(panel, includeSplit: false);
                    _achievementFilterRows = AddControlBarRows<ActivityCalendarControlBarAdapter>(panel, header: false);
                    ApplyAchievementFilterRows();
                    break;
            }

            AppendGridOptionsEditor(panel);
            return panel;
        }

        /// <summary>
        /// Appends the shared grid display-options editor for the grid widget kinds. The editor
        /// binds the widget's LIVE catalog record (not the dialog's widget clone), so grid
        /// display edits are instant-apply, independent of the host's persist callback and of
        /// the dialog's Save/Cancel, which govern only the title and the widget's own option
        /// bag. Live grids react to the record directly (bindings plus the grid view models'
        /// record subscriptions), so edits only need persisting - debounced, because a full
        /// settings write per checkbox toggle makes the editor visibly laggy.
        /// </summary>
        private void AppendGridOptionsEditor(Panel panel)
        {
            var catalog = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions;
            var surfaceKey = ShowcaseGridSurfaces.ResolveWidgetSurface(_settings.Kind, _settings.InstanceId);
            if (catalog == null || surfaceKey == null)
            {
                return;
            }

            var editor = new GridOptionsEditor();
            editor.SetResourceReference(MarginProperty, "PlayAch.Thickness.Top.Md");
            object options;
            if (ShowcaseGridSurfaces.IsAchievementSurface(surfaceKey))
            {
                // The sort combo's Default (None) keeps the projection order: pin order for
                // pinned grids, unlock recency for recent grids.
                options = catalog.GetAchievement(surfaceKey);
                editor.SurfaceKind = GridOptionKind.Achievement;
            }
            else
            {
                options = catalog.GetGameSummaries(surfaceKey);
                editor.SurfaceKind = GridOptionKind.GameSummaries;
            }

            // Naming the surface rather than setting flags is what keeps this editor and the grid's
            // own display settings popup showing the same rows: both read GridDisplaySurfaces.
            editor.SurfaceKey = surfaceKey;
            editor.Options = options;
            AttachGridOptionsPersist(options);

            panel.Children.Add(editor);
            AppendCategoryGridEditor(panel, catalog, surfaceKey, options as AchievementGridOptions);
        }

        /// <summary>
        /// The achievement grid widget's category grid section, the same rows the grid display
        /// settings popup shows for any achievement grid with category mode: hide the category grid
        /// after a selection, and the category grid's own display options.
        /// </summary>
        private void AppendCategoryGridEditor(
            Panel panel,
            GridOptionsCatalog catalog,
            string surfaceKey,
            AchievementGridOptions achievementOptions)
        {
            var categorySurfaceKey = ShowcaseGridSurfaces.ResolveCategorySurface(surfaceKey);
            if (achievementOptions == null || categorySurfaceKey == null)
            {
                return;
            }

            // The section's heading style lives with the settings styles.
            var section = new StackPanel();
            section.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/PlayniteAchievements;component/Resources/SettingsStyles.xaml",
                    UriKind.Absolute)
            });
            GridDisplaySettingsDialog.AppendCategorySection(
                section,
                categorySurfaceKey,
                out var hideCategoryRow,
                out var categoryEditor);
            hideCategoryRow.SetBinding(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding(nameof(AchievementGridOptions.HideCategorySummaryRow))
                {
                    Source = achievementOptions,
                    Mode = System.Windows.Data.BindingMode.TwoWay
                });
            var categoryOptions = catalog.GetCategorySummaries(categorySurfaceKey);
            categoryEditor.Options = categoryOptions;
            _gridOptionsPersist?.Watch(categoryOptions);
            panel.Children.Add(section);
        }

        private void AttachGridOptionsPersist(object record)
        {
            _gridOptionsPersist = new DebouncedSettingsPersist(
                this,
                () => PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi(),
                () => PlayniteAchievementsPlugin.Instance?.IsSettingsEditSessionActive == true);
            _gridOptionsPersist.Watch(record);
        }

        /// <summary>
        /// The Unlock Next rows (criterion, last-played window, per-game cap, hidden achievements),
        /// shared by the mosaic and the achievements grid so both sources read the same options.
        /// </summary>
        private List<FrameworkElement> AddUnlockNextOptions(Panel panel)
        {
            return new List<FrameworkElement>
            {
                // Unlock Next ranks during selection, so its criterion replaces the generic sort
                // rows rather than stacking with them.
                AddChoice(
                    panel,
                    Localize("LOCPlayAch_Showcase_UnlockNextCriterion"),
                    new[]
                    {
                        UnlockNextCriterion.ClosestToCompletion,
                        UnlockNextCriterion.Easiest
                    },
                    ShowcaseWidgetOptions.GetUnlockNextCriterion(_settings),
                    value => ShowcaseWidgetOptions.SetUnlockNextCriterion(_settings, value),
                    UnlockNextCriterionName),
                AddLastPlayedWindowChoice(panel),
                AddNumberRow(
                    panel,
                    Localize("LOCPlayAch_Showcase_MaxPerGame"),
                    () => ShowcaseWidgetOptions.GetMaxPerGame(_settings),
                    value => ShowcaseWidgetOptions.SetMaxPerGame(_settings, value)),
                AddToggle(
                    panel,
                    Localize("LOCPlayAch_Showcase_IncludeHiddenAchievements"),
                    ShowcaseWidgetOptions.GetIncludeHiddenAchievements(_settings),
                    value => ShowcaseWidgetOptions.SetIncludeHiddenAchievements(_settings, value))
            };
        }

        /// <summary>
        /// The Finish Next rows (ranking, last-played window, minimum progress, most remaining,
        /// unplayed games) in one container the source choice shows or hides, shared by the
        /// game mosaic and the game grid so both read the same options.
        /// </summary>
        private FrameworkElement AddFinishNextOptions(Panel panel)
        {
            var container = new StackPanel();
            panel.Children.Add(container);
            AddChoice(
                container,
                Localize("LOCPlayAch_Showcase_UnlockNextCriterion"),
                new[]
                {
                    FinishNextCriterion.ClosestToCompletion,
                    FinishNextCriterion.FewestRemaining,
                    FinishNextCriterion.EasiestRemaining
                },
                ShowcaseWidgetOptions.GetFinishNextCriterion(_settings),
                value => ShowcaseWidgetOptions.SetFinishNextCriterion(_settings, value),
                FinishNextCriterionName);
            AddLastPlayedWindowChoice(container);
            AddToggle(
                container,
                Localize("LOCPlayAch_IncludeUnplayedGames"),
                ShowcaseWidgetOptions.GetFinishNextIncludeUnplayed(_settings),
                value => ShowcaseWidgetOptions.SetFinishNextIncludeUnplayed(_settings, value));
            AddNumberRow(
                container,
                Localize("LOCPlayAch_Showcase_FinishNextMinimumProgress"),
                () => ShowcaseWidgetOptions.GetFinishNextMinimumProgress(_settings),
                value => ShowcaseWidgetOptions.SetFinishNextMinimumProgress(_settings, value));
            AddNumberRow(
                container,
                Localize("LOCPlayAch_Showcase_FinishNextMaxRemaining"),
                () => ShowcaseWidgetOptions.GetFinishNextMaxRemaining(_settings),
                value => ShowcaseWidgetOptions.SetFinishNextMaxRemaining(_settings, value));
            return container;
        }

        private static void ApplyAchievementGridSourceRows(
            IEnumerable<FrameworkElement> unlockNextRows,
            ShowcaseAchievementGridSource source)
        {
            foreach (var row in unlockNextRows ?? Enumerable.Empty<FrameworkElement>())
            {
                row.Visibility = source == ShowcaseAchievementGridSource.UnlockNext
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        // Which of the time window (with the timeline's bar width) and the timeline's split
        // menu the widget shows with its chart. Their values are the rows above.
        private void AddTimeControlRows(Panel panel, bool includeSplit)
        {
            AddControlBarHeader(panel);
            AddControlBarItemRow(
                panel,
                ShowcaseControlBarItems.TimeWindow,
                Localize("LOCPlayAch_Showcase_Range"),
                editor: null);
            if (includeSplit)
            {
                AddControlBarItemRow(
                    panel,
                    ShowcaseControlBarItems.SplitByPlatform,
                    Localize("LOCPlayAch_Timeline_SplitBy"),
                    editor: null);
            }
        }

        /// <summary>
        /// One row per item of the widget's control bar: a checkbox that shows the item in the
        /// widget, saved with the other options, beside the item itself. The item is the object
        /// the widget's bar holds, so a filter set here applies to the widget at once, whether or
        /// not the bar shows it, and is saved by the bar's own state store.
        /// </summary>
        /// <param name="header">False when the rows follow others under the same header.</param>
        private Dictionary<string, FrameworkElement> AddControlBarRows<TAdapter>(Panel panel, bool header = true)
            where TAdapter : SharedControlBarAdapter, new()
        {
            var rows = new Dictionary<string, FrameworkElement>(StringComparer.OrdinalIgnoreCase);
            var adapter = ShowcaseControlBarStates.Get<TAdapter>(_settings.InstanceId);
            _controlBarAdapters.Add(adapter);
            var entries = adapter.Entries;
            if (entries.Count == 0)
            {
                return rows;
            }

            if (header)
            {
                AddControlBarHeader(panel);
            }

            foreach (var entry in entries)
            {
                var bar = new GridControlBarViewModel();
                if (entry is GridSearchControl search)
                {
                    bar.Search = search;
                }
                else if (entry is GridControlBarItem item)
                {
                    bar.Items.Add(item);
                }

                var editor = new GridControlBarControl
                {
                    ControlBar = bar,
                    ShowControlBar = true,
                    FillsWidth = true
                };
                rows[entry.Id] = AddControlBarItemRow(panel, entry.Id, Localize(entry.LabelKey), editor, entry);
            }

            return rows;
        }

        // The widget's Show Control Bar switch, heading the item rows like any other toggle: off
        // shows no bar, on shows the items checked below it.
        private void AddControlBarHeader(Panel panel)
        {
            AddToggle(
                panel,
                Localize("LOCPlayAch_Settings_ShowGridControlBar"),
                ShowcaseWidgetOptions.GetShowControlBar(_settings, LegacyControlBarVisible()),
                value =>
                {
                    ShowcaseWidgetOptions.SetShowControlBar(_settings, value);
                    ApplyControlBarItemRows();
                });
        }

        private void ApplyControlBarItemRows()
        {
            var shown = ShowcaseWidgetOptions.GetShowControlBar(_settings, LegacyControlBarVisible());
            foreach (var row in _controlBarItemRows)
            {
                row.IsEnabled = shown;
            }
        }

        // One item, laid out like the other option rows: its name in the label column, then its
        // show checkbox, its editor across the rest of the row, and a button clearing its filter.
        private FrameworkElement AddControlBarItemRow(
            Panel panel,
            string itemId,
            string label,
            FrameworkElement editor,
            IGridControlBarEntry entry = null)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var controls = new Grid { VerticalAlignment = VerticalAlignment.Center };
            if (editor != null)
            {
                controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = ControlRowSizeGroup });
            }

            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(controls, 1);
            row.Children.Add(controls);

            var checkBox = new CheckBox
            {
                IsChecked = ShowcaseWidgetOptions.GetControlBarItemChecked(_settings, itemId),
                ToolTip = Localize("LOCPlayAch_Settings_ShowGridControlBar"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            void Commit()
            {
                ShowcaseWidgetOptions.SetControlBarItemVisible(_settings, itemId, checkBox.IsChecked == true);
                _persist?.Invoke();
                if (_publishChanges)
                {
                    ShowcaseConfigurationEvents.RaiseChanged();
                }
            }

            checkBox.Checked += (_, __) => Commit();
            checkBox.Unchecked += (_, __) => Commit();
            controls.Children.Add(checkBox);
            if (editor != null)
            {
                // The bar's filter items keep a small left margin (the search box has none); this
                // lines the editor up with the other rows' controls.
                editor.Margin = new Thickness(entry is GridSearchControl ? 0 : -4, 0, 0, 0);
                editor.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(editor, 1);
                controls.Children.Add(editor);
            }

            if (entry != null)
            {
                var clear = new Button
                {
                    Content = "\u00D7",
                    ToolTip = Localize("LOCPlayAch_Common_Clear"),
                    Width = 30,
                    MinHeight = 0,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                clear.SetResourceReference(StyleProperty, "PlayAch.Button.ToolbarIconStyle");
                clear.SetResourceReference(HeightProperty, "PlayAch.Size.Control.Height");
                clear.SetResourceReference(Control.FontFamilyProperty, "PlayAch.FontFamily.Body");
                clear.Click += (_, __) => entry.ClearSelection();
                Grid.SetColumn(clear, 2);
                controls.Children.Add(clear);
            }

            panel.Children.Add(row);
            _controlBarItemRows.Add(checkBox);
            checkBox.IsEnabled = ShowcaseWidgetOptions.GetShowControlBar(_settings, LegacyControlBarVisible());
            return row;
        }

        // A grid widget's former whole-bar option lives on its grid surface, not in the widget's
        // options; the widget reads it the same way (ShowcaseGridWidgetViewModelBase).
        private bool? LegacyControlBarVisible()
        {
            var catalog = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.GridOptions;
            var surfaceKey = ShowcaseGridSurfaces.ResolveWidgetSurface(_settings.Kind, _settings.InstanceId);
            if (catalog == null || surfaceKey == null)
            {
                return null;
            }

            var options = ShowcaseGridSurfaces.IsAchievementSurface(surfaceKey)
                ? (GridCommonOptions)catalog.GetAchievement(surfaceKey)
                : catalog.GetGameSummaries(surfaceKey);
            return options?.ShowControlBar == true;
        }

        /// <summary>
        /// The shared time-window picker used by every range-windowed widget (Timeline, Scores,
        /// Activity Calendar). Only the Timeline chart has a bar granularity to override.
        /// </summary>
        private void AddRangeChoice(Panel panel)
        {
            var hasGranularity = _settings.Kind == ShowcaseWidgetKind.Timeline;
            AddTimeWindowRow(
                panel,
                Localize("LOCPlayAch_Showcase_Range"),
                TimeWindow.Presets,
                () => ShowcaseTimelineOptions.GetWindow(_settings),
                value => ShowcaseTimelineOptions.SetWindow(_settings, value),
                hasGranularity ? () => ShowcaseTimelineOptions.GetGranularity(_settings) : (Func<TimelineGranularity>)null,
                hasGranularity ? value => ShowcaseTimelineOptions.SetGranularity(_settings, value) : (Action<TimelineGranularity>)null);
        }

        /// <summary>
        /// A labeled <see cref="Controls.TimeWindowPicker"/> on its own row (the picker is wider than
        /// the two-column rows allow), persisting and publishing like every other row, gated on a
        /// real change so a no-op edit never republishes the dashboard.
        /// </summary>
        private FrameworkElement AddTimeWindowRow(
            Panel panel,
            string label,
            IReadOnlyList<TimelineRange> presets,
            Func<TimeWindow> read,
            Action<TimeWindow> apply,
            Func<TimelineGranularity> readGranularity = null,
            Action<TimelineGranularity> applyGranularity = null)
        {
            var container = new StackPanel();
            container.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 4),
                FontWeight = FontWeights.SemiBold
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            container.Children.Add(labelBlock);

            var picker = new Controls.TimeWindowPicker
            {
                Presets = presets,
                Window = read(),
                ShowGranularity = applyGranularity != null,
                Granularity = readGranularity?.Invoke() ?? TimelineGranularity.Auto,
                // A settings row has room: the pickers sit on their own line, no popup.
                InlineCustomEditor = true
            };
            picker.WindowChanged += (_, __) =>
            {
                RefreshGranularityAvailability(picker);
                if (Equals(read(), picker.Window))
                {
                    return;
                }

                apply(picker.Window);
                PersistAndPublish();
            };
            if (applyGranularity != null)
            {
                RefreshGranularityAvailability(picker);
                picker.GranularityChanged += (_, __) =>
                {
                    if (readGranularity() == picker.Granularity)
                    {
                        return;
                    }

                    applyGranularity(picker.Granularity);
                    PersistAndPublish();
                };
            }

            container.Children.Add(picker);
            panel.Children.Add(container);
            return container;
        }

        // The dialog has no chart width to hand the picker, so it disables only the units that
        // cannot fit at all (the absolute bar cap); the widget's own chart still escalates by width.
        private static void RefreshGranularityAvailability(Controls.TimeWindowPicker picker)
        {
            if (!picker.ShowGranularity)
            {
                return;
            }

            var range = (picker.Window ?? TimeWindow.All).Resolve(DateTime.Today, null);
            picker.UnavailableGranularities = new[] { TimelineGranularity.Day, TimelineGranularity.Week, TimelineGranularity.Month }
                .Where(candidate => !Services.Achievements.TimelineBucketing.Fits(
                    candidate, range.Start, range.End, Services.Achievements.TimelineBucketing.MaxOverrideBarCount))
                .ToList();
            if (picker.UnavailableGranularities.Contains(picker.Granularity))
            {
                picker.Granularity = TimelineGranularity.Auto;
            }
        }

        private void PersistAndPublish()
        {
            _persist?.Invoke();
            if (_publishChanges)
            {
                ShowcaseConfigurationEvents.RaiseChanged();
            }
        }

        /// <summary>
        /// The profile stat strip's slot editor: one combo per filled slot plus a trailing
        /// empty one, each picking a single overall statistic. Choosing a stat in the trailing
        /// combo appends a slot; choosing None in a filled combo removes it. Slots are
        /// unbounded and stored in slot order.
        /// </summary>
        private void AddProfileStatSlots(Panel panel)
        {
            AddStatSlots(
                panel,
                ShowcaseWidgetOptions.GetProfileStatKeys(_settings),
                keys => ShowcaseWidgetOptions.SetProfileStatKeys(_settings, keys));
        }

        // The Statistics widget's own slots: unset means every statistic, so the editor starts
        // from the full list in catalog order.
        private void AddStatisticsSlots(Panel panel)
        {
            var current = ShowcaseWidgetOptions.GetStatisticsKeys(_settings) ??
                ShowcaseWidgetProjectionService.BuildStatistics(null, DateTime.Now).Select(stat => stat.Key).ToList();
            AddStatSlots(
                panel,
                current,
                keys => ShowcaseWidgetOptions.SetStatisticsKeys(_settings, keys));
        }

        private void AddStatSlots(Panel panel, IReadOnlyList<string> initial, Action<List<string>> save)
        {
            // Key/label catalog only; the empty snapshot's values are never shown.
            var catalog = ShowcaseWidgetProjectionService.BuildStatistics(null, DateTime.Now);

            var labelBlock = new TextBlock
            {
                Text = Localize("LOCPlayAch_Showcase_Widget_Statistics"),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 2)
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            panel.Children.Add(labelBlock);

            var slots = (initial ?? Array.Empty<string>()).ToList();
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            var rebuilding = false;

            void Store()
            {
                save(slots);
                _persist?.Invoke();
                if (_publishChanges)
                {
                    ShowcaseConfigurationEvents.RaiseChanged();
                }
            }

            void Rebuild()
            {
                rebuilding = true;
                grid.Children.Clear();
                for (var index = 0; index <= slots.Count; index++)
                {
                    var slotIndex = index;
                    var combo = new ComboBox
                    {
                        MinHeight = 30,
                        Margin = new Thickness(0, 2, index % 2 == 0 ? 8 : 0, 2)
                    };
                    combo.Items.Add(new Choice<string>
                    {
                        Value = null,
                        Label = Localize("LOCPlayAch_Common_None")
                    });
                    foreach (var stat in catalog)
                    {
                        combo.Items.Add(new Choice<string>
                        {
                            Value = stat.Key,
                            Label = Localize(stat.LabelKey)
                        });
                    }

                    combo.DisplayMemberPath = nameof(Choice<string>.Label);
                    var current = slotIndex < slots.Count ? slots[slotIndex] : null;
                    // Unknown keys (hand-edited settings) fall back to the None choice.
                    combo.SelectedItem = combo.Items
                        .OfType<Choice<string>>()
                        .FirstOrDefault(choice => string.Equals(
                            choice.Value,
                            current,
                            StringComparison.Ordinal))
                        ?? combo.Items[0];
                    combo.SelectionChanged += (_, __) =>
                    {
                        if (rebuilding || !(combo.SelectedItem is Choice<string> choice))
                        {
                            return;
                        }

                        if (slotIndex >= slots.Count)
                        {
                            if (choice.Value == null)
                            {
                                return;
                            }

                            slots.Add(choice.Value);
                        }
                        else if (choice.Value == null)
                        {
                            slots.RemoveAt(slotIndex);
                        }
                        else
                        {
                            slots[slotIndex] = choice.Value;
                        }

                        Store();
                        Rebuild();
                    };
                    grid.Children.Add(combo);
                }

                rebuilding = false;
            }

            Rebuild();
            panel.Children.Add(grid);
        }

        /// <summary>
        /// The pin collection the widget is limited to: none, a game collection, or (for a widget
        /// listing achievements) an achievement collection. Names carry their kind when both kinds
        /// are listed.
        /// </summary>
        private FrameworkElement AddCollectionChoice(Panel panel, bool includeAchievementCollections)
        {
            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            var names = new Dictionary<(ShowcaseCollectionType, string), string>
            {
                [(ShowcaseCollectionType.None, null)] = Localize("LOCPlayAch_Common_None")
            };
            var choices = new List<(ShowcaseCollectionType, string)> { (ShowcaseCollectionType.None, null) };
            var gameKind = Localize("LOCPlayAch_Showcase_SlideshowSource_GameCollection");
            foreach (var collection in (showcase?.GamePinCollections ?? new List<PinnedGameCollection>())
                .Where(collection => collection != null))
            {
                var choice = (ShowcaseCollectionType.Games, collection.CollectionId);
                choices.Add(choice);
                names[choice] = includeAchievementCollections
                    ? PinCollectionName(collection.Name) + " (" + gameKind + ")"
                    : PinCollectionName(collection.Name);
            }

            if (includeAchievementCollections)
            {
                var achievementKind = Localize("LOCPlayAch_Showcase_SlideshowSource_AchievementCollection");
                foreach (var collection in (showcase?.AchievementPinCollections ?? new List<PinnedAchievementCollection>())
                    .Where(collection => collection != null))
                {
                    var choice = (ShowcaseCollectionType.Achievements, collection.CollectionId);
                    choices.Add(choice);
                    names[choice] = PinCollectionName(collection.Name) + " (" + achievementKind + ")";
                }
            }

            var type = ShowcaseWidgetOptions.GetCollectionType(_settings);
            var collectionId = ShowcaseWidgetOptions.GetPinCollectionId(_settings);
            var selected = type == ShowcaseCollectionType.Achievements
                ? (type, ShowcasePinService.ResolveAchievementCollection(showcase, collectionId)?.CollectionId)
                : type == ShowcaseCollectionType.Games
                    ? (type, ShowcasePinService.ResolveGameCollection(showcase, collectionId)?.CollectionId)
                    : (ShowcaseCollectionType.None, (string)null);
            if (!names.ContainsKey(selected))
            {
                selected = (ShowcaseCollectionType.None, null);
            }

            return AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_PinCollection"),
                choices.ToArray(),
                selected,
                value =>
                {
                    ShowcaseWidgetOptions.SetCollectionType(_settings, value.Item1);
                    if (value.Item1 != ShowcaseCollectionType.None)
                    {
                        ShowcaseWidgetOptions.SetPinCollectionId(_settings, value.Item2);
                    }

                    // The Game filters list the collection's games and drop picks outside it.
                    var gameIds = ShowcaseCollectionScope.Resolve(showcase, _settings)?.GameIds;
                    foreach (var adapter in _controlBarAdapters)
                    {
                        adapter.GameFilter.SetScope(gameIds, notify: true);
                    }
                },
                value => names.TryGetValue(value, out var name) ? name : string.Empty);
        }

        /// <summary>
        /// Free-form numeric entry row (label + TextBox). Commits on focus loss or Enter; the
        /// setter's own clamping normalizes the value, and the box reads the result back so the
        /// user sees what was actually stored.
        /// </summary>
        private Grid AddNumberRow(Panel panel, string label, Func<int> read, Action<int> apply)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var box = new TextBox
            {
                MinHeight = 30,
                VerticalContentAlignment = VerticalAlignment.Center,
                Text = read().ToString(FormattingCulture.Current)
            };
            void Commit()
            {
                if (int.TryParse(
                        box.Text,
                        System.Globalization.NumberStyles.Integer,
                        FormattingCulture.Current,
                        out var value) &&
                    value != read())
                {
                    apply(value);
                    _persist?.Invoke();
                    if (_publishChanges)
                    {
                        ShowcaseConfigurationEvents.RaiseChanged();
                    }
                }

                box.Text = read().ToString(FormattingCulture.Current);
            }

            box.LostFocus += (_, __) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter)
                {
                    Commit();
                    args.Handled = true;
                }
            };
            Grid.SetColumn(box, 1);
            row.Children.Add(box);
            panel.Children.Add(row);
            return row;
        }

        // Unlock Next and Finish Next share the "played within" filter, so both panels build it
        // the same way.
        private FrameworkElement AddLastPlayedWindowChoice(Panel panel) =>
            AddTimeWindowRow(
                panel,
                Localize("LOCPlayAch_Showcase_LastPlayedWindow"),
                TimeWindow.Presets,
                () => ShowcaseWidgetOptions.GetLastPlayedTimeWindow(_settings),
                value => ShowcaseWidgetOptions.SetLastPlayedTimeWindow(_settings, value));

        /// <summary>
        /// Unlock Next's criterion row replaces the generic sort rows: the criterion picks which
        /// locked achievements are shown, and sorting by unlock time over locked rows - every one
        /// of which has no unlock time - would only scramble that ranking.
        /// </summary>
        private static void ApplyUnlockNextVisibility(
            IReadOnlyList<FrameworkElement> unlockNextRows,
            IReadOnlyList<FrameworkElement> sortRows,
            ShowcaseMosaicSource source)
        {
            var unlockNext = source == ShowcaseMosaicSource.UnlockNext;
            foreach (var row in unlockNextRows)
            {
                if (row != null)
                {
                    row.Visibility = unlockNext ? Visibility.Visible : Visibility.Collapsed;
                }
            }

            foreach (var row in sortRows)
            {
                if (row != null)
                {
                    row.Visibility = unlockNext ? Visibility.Collapsed : Visibility.Visible;
                }
            }
        }

        // Both mosaic contents share the sort-direction option, so both panels get the same row.
        private Grid AddSortDirectionChoice(Panel panel) =>
            AddChoice(
                panel,
                Localize("LOCPlayAch_Common_SortDirection"),
                new[] { true, false },
                ShowcaseWidgetOptions.GetMosaicSortDescending(_settings),
                value => ShowcaseWidgetOptions.SetMosaicSortDescending(_settings, value),
                SortDirectionLabel);

        private static string SortDirectionLabel(bool descending) => descending
            ? Localize("LOCPlayAch_Common_Descending")
            : Localize("LOCPlayAch_Common_Ascending");

        // Both mosaic sort pickers name the mode that keeps the Source's own order "Default",
        // matching how the grid options editor labels CompactListSortMode.None.
        private static string MosaicSortName(CompactListSortMode value)
        {
            switch (value)
            {
                case CompactListSortMode.UnlockTime:
                    return Localize("LOCPlayAch_Common_UnlockTime");
                case CompactListSortMode.Rarity:
                    return Localize("LOCPlayAch_Column_Rarity");
                default:
                    return Localize("LOCPlayAch_Common_Default");
            }
        }

        private static string GameMosaicSortName(GameSummariesSortMode value)
        {
            switch (value)
            {
                case GameSummariesSortMode.RecentUnlock:
                    return Localize("LOCPlayAch_Common_LastUnlock");
                case GameSummariesSortMode.LastPlayed:
                    return Localize("LOCPlayAch_Common_LastPlayed");
                case GameSummariesSortMode.TotalAchievements:
                    return Localize("LOCPlayAch_Column_Total");
                case GameSummariesSortMode.Progress:
                    return Localize("LOCPlayAch_Progress");
                case GameSummariesSortMode.Alphabetical:
                    return Localize("LOCPlayAch_Common_Name");
                default:
                    return Localize("LOCPlayAch_Common_Default");
            }
        }

        private static string PieLegendPositionName(PieLegendPosition value) =>
            Localize(value == PieLegendPosition.Left
                ? "LOCPlayAch_Common_Left"
                : "LOCPlayAch_Common_Right");

        private static string ScoreBadgePositionName(ScoreCardBadgePosition value) =>
            Localize(value == ScoreCardBadgePosition.Right
                ? "LOCPlayAch_Common_Right"
                : "LOCPlayAch_Common_Left");

        private static string PieCenterModeName(PieCenterMode value)
        {
            switch (value)
            {
                case PieCenterMode.Empty:
                    return Localize("LOCPlayAch_Settings_PieCenter_Empty");
                case PieCenterMode.Filled:
                    return Localize("LOCPlayAch_Settings_PieCenter_Filled");
                default:
                    return Localize("LOCPlayAch_Settings_PieCenter_Percentage");
            }
        }

        private static string SmallSliceModeName(OverviewPieSmallSliceMode value)
        {
            switch (value)
            {
                case OverviewPieSmallSliceMode.Exact:
                    return Localize("LOCPlayAch_Settings_OverviewPieSmallSliceMode_Exact");
                case OverviewPieSmallSliceMode.Hide:
                    return Localize("LOCPlayAch_Common_Hide");
                default:
                    return Localize("LOCPlayAch_Settings_OverviewPieSmallSliceMode_Round");
            }
        }

        // The bar rows of the achievement filters, shown only while the widget counts achievements
        // (ShowcaseWidgetOptions.CountsAchievements); null for a widget without them.
        private IReadOnlyDictionary<string, FrameworkElement> _achievementFilterRows;

        private void ApplyAchievementFilterRows()
        {
            if (_achievementFilterRows == null)
            {
                return;
            }

            var visibility = ShowcaseWidgetOptions.CountsAchievements(_settings) ? Visibility.Visible : Visibility.Collapsed;
            foreach (var id in new[] { ShowcaseControlBarItems.Tag, ShowcaseControlBarItems.Rarity, ShowcaseControlBarItems.Trophy })
            {
                if (_achievementFilterRows.TryGetValue(id, out var row))
                {
                    row.Visibility = visibility;
                }
            }
        }

        // What a Timeline's bars or an Activity Calendar's days count: unlocks, or completed games.
        private void AddCountChoice(Panel panel)
        {
            AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_Variant"),
                new[] { ShowcaseTimelineCount.Unlocks, ShowcaseTimelineCount.Completions },
                ShowcaseTimelineOptions.GetCount(_settings),
                value =>
                {
                    ShowcaseTimelineOptions.SetCount(_settings, value);
                    ApplyAchievementFilterRows();
                },
                value => Localize(value == ShowcaseTimelineCount.Completions
                    ? "LOCPlayAch_Showcase_Stat_CompletedGames"
                    : "LOCPlayAch_Column_Unlocks"));
        }

        private static string TimelineSplitName(ShowcaseTimelineSplit value)
        {
            switch (value)
            {
                case ShowcaseTimelineSplit.Platform:
                    return Localize("LOCPlayAch_Common_Platform");
                case ShowcaseTimelineSplit.Rarity:
                    return Localize("LOCPlayAch_Column_Rarity");
                case ShowcaseTimelineSplit.Trophy:
                    return Localize("LOCPlayAch_Column_Trophy");
                default:
                    return Localize("LOCPlayAch_Common_None");
            }
        }

        // Persists and publishes one committed edit, as every row does.
        private void Committed()
        {
            _persist?.Invoke();
            if (_publishChanges)
            {
                ShowcaseConfigurationEvents.RaiseChanged();
            }
        }

        // The Image and Text widgets place their content with the same pair of rows.
        private void AddAlignmentChoices(Panel panel)
        {
            AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_HorizontalAlignment"),
                new[]
                {
                    ShowcaseHorizontalAlignment.Left,
                    ShowcaseHorizontalAlignment.Center,
                    ShowcaseHorizontalAlignment.Right
                },
                ShowcaseWidgetOptions.GetHorizontalAlignment(_settings),
                value => ShowcaseWidgetOptions.SetHorizontalAlignment(_settings, value),
                HorizontalAlignmentName);
            AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_VerticalAlignment"),
                new[]
                {
                    ShowcaseVerticalAlignment.Top,
                    ShowcaseVerticalAlignment.Center,
                    ShowcaseVerticalAlignment.Bottom
                },
                ShowcaseWidgetOptions.GetVerticalAlignment(_settings),
                value => ShowcaseWidgetOptions.SetVerticalAlignment(_settings, value),
                VerticalAlignmentName);
        }

        /// <summary>
        /// A label over the shared markdown toolbar and a multi-line box. Enter adds a line; the
        /// text commits on focus loss or Ctrl+Enter.
        /// </summary>
        private void AddMultilineText(Panel panel, string label, Func<string> read, Action<string> apply)
        {
            var row = new StackPanel();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 4),
                FontWeight = FontWeights.SemiBold
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var box = new TextBox
            {
                Text = read() ?? string.Empty,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 96,
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(7, 4, 7, 4)
            };
            Helpers.MarkdownEditing.SetShortcuts(box, true);
            void Commit()
            {
                var value = string.IsNullOrWhiteSpace(box.Text) ? null : box.Text;
                if (string.Equals(value, read(), StringComparison.Ordinal))
                {
                    return;
                }

                apply(value);
                Committed();
            }

            box.LostFocus += (_, __) => Commit();
            box.KeyDown += (_, args) =>
            {
                if (args.Key == System.Windows.Input.Key.Enter &&
                    (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
                {
                    Commit();
                    args.Handled = true;
                }
            };
            row.Children.Add(Helpers.MarkdownEditing.CreateToolbar(box));
            row.Children.Add(box);
            panel.Children.Add(row);
        }

        /// <summary>
        /// The Text widget's font: Theme default first, then every installed family, each drawn
        /// in its own face, with type-to-search. A stored family this machine lacks stays listed.
        /// </summary>
        private void AddFontFamilyChoice(Panel panel)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = Localize("LOCPlayAch_Settings_Style_FontFamily"),
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var current = ShowcaseWidgetOptions.GetFontFamily(_settings);
            var options = new List<FontFamilyOption>
            {
                new FontFamilyOption(
                    Localize("LOCPlayAch_Settings_Style_FontThemeDefault"),
                    familyName: null,
                    previewFamily: SystemFonts.MessageFontFamily)
            };
            options.AddRange(SystemFontCatalog.Families);
            var selected = current == null
                ? options[0]
                : options.FirstOrDefault(option =>
                    string.Equals(option.FamilyName, current, StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                selected = SystemFontCatalog.CreateUnlistedOption(current);
                options.Insert(1, selected);
            }

            var combo = new ComboBox
            {
                MinHeight = 30,
                MaxDropDownHeight = 360,
                ItemsSource = options,
                SelectedItem = selected,
                ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel))),
                ItemTemplate = FontOptionTemplate()
            };
            VirtualizingPanel.SetIsVirtualizing(combo, true);
            Helpers.FontPickerBehavior.SetPreviewSelectedFont(combo, true);
            combo.SelectionChanged += (_, __) =>
            {
                if (!(combo.SelectedItem is FontFamilyOption option) ||
                    string.Equals(option.FamilyName, ShowcaseWidgetOptions.GetFontFamily(_settings), StringComparison.Ordinal))
                {
                    return;
                }

                ShowcaseWidgetOptions.SetFontFamily(_settings, option.FamilyName);
                Committed();
            };
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            panel.Children.Add(row);
        }

        private static DataTemplate FontOptionTemplate()
        {
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FontFamilyOption.DisplayName)));
            text.SetBinding(TextBlock.FontFamilyProperty, new System.Windows.Data.Binding(nameof(FontFamilyOption.PreviewFamily)));
            return new DataTemplate { VisualTree = text };
        }

        /// <summary>
        /// The score card's options. Show-total applies to points cards and the rarity colors to
        /// Collection and Prestige, so each row shows only for the card type it affects.
        /// </summary>
        private void SyncScoreBarRows(ScoreCardType type)
        {
            if (_scoreBarRows != null)
            {
                _scoreBarRows.Visibility = type.IsPoints ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // A points card's bar, which lists one platform's games; null for a card with no widget.
        private ChartControlBarAdapter ScoreCardAdapter =>
            string.IsNullOrWhiteSpace(_settings.InstanceId)
                ? null
                : ShowcaseControlBarStates.Get<ChartControlBarAdapter>(_settings.InstanceId);

        // A new platform shows on the live card at once, so its bar lists that platform's games.
        // Game picks name the old platform's games and would empty the new card, so they clear.
        private void PreviewScoreCardType(ScoreCardType previous, ScoreCardType value)
        {
            var adapter = ScoreCardAdapter;
            if (adapter == null || previous == value)
            {
                return;
            }

            if (_gamesBeforeTypeChange == null)
            {
                _gamesBeforeTypeChange = adapter.GameFilter.Capture();
            }

            adapter.GameFilter.Clear();
            if (_liveWidget != null)
            {
                if (!_liveTypeBeforeChange.HasValue)
                {
                    _liveTypeBeforeChange = ShowcaseWidgetOptions.GetScoreCardType(_liveWidget);
                }

                ShowcaseWidgetOptions.SetScoreCardType(_liveWidget, value);
            }

            adapter.Reapply();
        }

        /// <summary>
        /// Puts back the live card's type and Game picks a type change replaced, for a dialog
        /// closed without saving.
        /// </summary>
        public void RevertLivePreview()
        {
            var adapter = ScoreCardAdapter;
            if (adapter == null || (!_liveTypeBeforeChange.HasValue && _gamesBeforeTypeChange == null))
            {
                return;
            }

            if (_liveWidget != null && _liveTypeBeforeChange.HasValue)
            {
                ShowcaseWidgetOptions.SetScoreCardType(_liveWidget, _liveTypeBeforeChange.Value);
            }

            if (_gamesBeforeTypeChange != null)
            {
                adapter.GameFilter.Restore(_gamesBeforeTypeChange);
            }

            _liveTypeBeforeChange = null;
            _gamesBeforeTypeChange = null;
            adapter.Reapply();
        }

        // The one-game options only act on a card that sums one game, so they are disabled while
        // the card's filters keep more; before any card has counted, they stay enabled.
        private void TrackOneGameRow(FrameworkElement row)
        {
            var adapter = ScoreCardAdapter;
            if (row == null || adapter == null)
            {
                return;
            }

            ToolTipService.SetShowOnDisabled(row, true);
            void Sync()
            {
                var count = adapter.ShownGameCount;
                row.IsEnabled = count < 0 || count == 1;
                row.ToolTip = row.IsEnabled ? null : Localize("LOCPlayAch_Showcase_ScoreCardArtHint");
            }

            void OnAdapterChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(ChartControlBarAdapter.ShownGameCount))
                {
                    Sync();
                }
            }

            // The adapter outlives this editor, so the handler is held only while it is shown.
            Loaded += (_, __) =>
            {
                adapter.PropertyChanged -= OnAdapterChanged;
                adapter.PropertyChanged += OnAdapterChanged;
                Sync();
            };
            Unloaded += (_, __) => adapter.PropertyChanged -= OnAdapterChanged;
            Sync();
        }

        private void AddScoreCardOptions(Panel panel)
        {
            Grid showTotalRow = null;
            Grid rarityBadgesRow = null;
            Grid artRow = null;
            Grid customColorsRow = null;
            void SyncTypeRows(ScoreCardType type)
            {
                // Points-only rows.
                foreach (var row in new[] { showTotalRow, rarityBadgesRow, artRow })
                {
                    if (row != null)
                    {
                        row.Visibility = type.IsPoints ? Visibility.Visible : Visibility.Collapsed;
                    }
                }

                if (customColorsRow != null)
                {
                    customColorsRow.Visibility = type.IsPoints ? Visibility.Collapsed : Visibility.Visible;
                }

                SyncScoreBarRows(type);
            }

            AddChoice(
                panel,
                Localize("LOCPlayAch_Common_Type"),
                ScoreCardChoices.Build(ShowcaseWidgetOptions.GetScoreCardType(_settings)).ToArray(),
                ShowcaseWidgetOptions.GetScoreCardType(_settings),
                value =>
                {
                    var previous = ShowcaseWidgetOptions.GetScoreCardType(_settings);
                    ShowcaseWidgetOptions.SetScoreCardType(_settings, value);
                    SyncTypeRows(value);
                    PreviewScoreCardType(previous, value);
                },
                ScoreCardTypeName);
            AddChoice(
                panel,
                Localize("LOCPlayAch_Settings_ScoreCardBadgePosition"),
                new[] { ScoreCardBadgePosition.Left, ScoreCardBadgePosition.Right },
                ShowcaseWidgetOptions.GetScoreCardBadgePosition(_settings),
                value => ShowcaseWidgetOptions.SetScoreCardBadgePosition(_settings, value),
                ScoreBadgePositionName);
            showTotalRow = AddToggle(
                panel,
                Localize("LOCPlayAch_Settings_ScoreCardShowTotal"),
                ShowcaseWidgetOptions.GetScoreCardShowTotal(_settings),
                value => ShowcaseWidgetOptions.SetScoreCardShowTotal(_settings, value));
            rarityBadgesRow = AddToggle(
                panel,
                Localize("LOCPlayAch_Settings_ShowRarityBadgesBelowProgress"),
                ShowcaseWidgetOptions.GetScoreCardShowRarityBadges(_settings),
                value => ShowcaseWidgetOptions.SetScoreCardShowRarityBadges(_settings, value));
            artRow = AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_ScoreCardArt"),
                new[] { ShowcaseScoreCardArt.ProviderIcon, ShowcaseScoreCardArt.GameCover, ShowcaseScoreCardArt.GameIcon },
                ShowcaseWidgetOptions.GetScoreCardArt(_settings),
                value => ShowcaseWidgetOptions.SetScoreCardArt(_settings, value),
                ScoreCardArtName);
            TrackOneGameRow(artRow);
            customColorsRow = AddToggle(
                panel,
                Localize("LOCPlayAch_Settings_ScoreCardUseCustomColors"),
                ShowcaseWidgetOptions.GetScoreCardUseCustomColors(_settings),
                value => ShowcaseWidgetOptions.SetScoreCardUseCustomColors(_settings, value));
            SyncTypeRows(ShowcaseWidgetOptions.GetScoreCardType(_settings));

            if (!_includeScoreHistory)
            {
                return;
            }

            AddToggle(
                panel,
                Localize("LOCPlayAch_Showcase_ScoreHistory"),
                ShowcaseWidgetOptions.GetScoreHistoryShown(_settings),
                value => ShowcaseWidgetOptions.SetScoreHistoryShown(_settings, value));
            AddRangeChoice(panel);
        }

        private Grid AddChoice<T>(
            Panel panel,
            string label,
            T[] values,
            T selected,
            Action<T> apply,
            Func<T, string> display)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var combo = new ComboBox { MinHeight = 30 };
            Choice<T> selectedChoice = null;
            foreach (var value in values)
            {
                var choice = new Choice<T> { Value = value, Label = display(value) };
                combo.Items.Add(choice);
                if (Equals(value, selected))
                {
                    selectedChoice = choice;
                }
            }

            if (selectedChoice == null)
            {
                selectedChoice = new Choice<T>
                {
                    Value = selected,
                    Label = display(selected)
                };
                combo.Items.Insert(0, selectedChoice);
            }

            combo.DisplayMemberPath = nameof(Choice<T>.Label);
            combo.SelectedItem = selectedChoice;
            combo.SelectionChanged += (_, __) =>
            {
                if (!(combo.SelectedItem is Choice<T> choice))
                {
                    return;
                }

                apply(choice.Value);
                _persist?.Invoke();
                if (_publishChanges)
                {
                    ShowcaseConfigurationEvents.RaiseChanged();
                }
            };
            var comboHost = new Grid { VerticalAlignment = VerticalAlignment.Center };
            comboHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto, SharedSizeGroup = ControlRowSizeGroup });
            comboHost.Children.Add(combo);
            Grid.SetColumn(comboHost, 1);
            row.Children.Add(comboHost);
            panel.Children.Add(row);
            return row;
        }

        /// <summary>
        /// An on/off option: the same label column as <see cref="AddChoice{T}"/>, with a checkbox
        /// instead of an On/Off dropdown, persisting and publishing the same way.
        /// </summary>
        private Grid AddToggle(Panel panel, string label, bool value, Action<bool> apply)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var checkBox = new CheckBox
            {
                IsChecked = value,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            void Commit()
            {
                apply(checkBox.IsChecked == true);
                _persist?.Invoke();
                if (_publishChanges)
                {
                    ShowcaseConfigurationEvents.RaiseChanged();
                }
            }

            checkBox.Checked += (_, __) => Commit();
            checkBox.Unchecked += (_, __) => Commit();
            Grid.SetColumn(checkBox, 1);
            row.Children.Add(checkBox);
            panel.Children.Add(row);
            return row;
        }

        /// <summary>
        /// A color option: a swatch that opens the color picker, and a reset that clears the
        /// stored color so the theme accent applies again.
        /// </summary>
        private Grid AddColorRow(Panel panel, string label, Func<string> read, Action<string> apply)
        {
            var row = new Grid();
            row.SetResourceReference(MarginProperty, "PlayAch.Thickness.Bottom.Md");
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            var labelBlock = new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 8, 0),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            labelBlock.SetResourceReference(TextBlock.ForegroundProperty, "PlayAch.Brush.Text");
            row.Children.Add(labelBlock);

            var swatch = new Border
            {
                Width = 36,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1)
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, "PlayAch.Brush.Border");
            var pick = new Button
            {
                Content = swatch,
                MinHeight = 30,
                Padding = new Thickness(8, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = Localize("LOCPlayAch_ColorPicker_WindowTitle")
            };
            var reset = new Button
            {
                Content = Localize("LOCPlayAch_Settings_Style_ResetToDefault"),
                MinHeight = 30,
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(8, 0, 8, 0)
            };

            void ShowStored()
            {
                var stored = read();
                if (AlphaColorPickerDialog.TryParseColor(stored, out var color))
                {
                    swatch.Background = new SolidColorBrush(color);
                }
                else
                {
                    swatch.SetResourceReference(Border.BackgroundProperty, "PlayAch.Brush.Accent");
                }

                reset.IsEnabled = stored != null;
            }

            void Commit(string value)
            {
                apply(value);
                Committed();
                ShowStored();
            }

            pick.Click += (_, __) =>
            {
                var picked = PlayniteAchievementsPlugin.Instance?.PickColor(Window.GetWindow(this), read());
                if (!string.IsNullOrWhiteSpace(picked))
                {
                    Commit(picked);
                }
            };
            reset.Click += (_, __) => Commit(null);
            ShowStored();

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(pick);
            buttons.Children.Add(reset);
            Grid.SetColumn(buttons, 1);
            row.Children.Add(buttons);
            panel.Children.Add(row);
            return row;
        }

        private sealed class Choice<T>
        {
            public T Value { get; set; }

            public string Label { get; set; }
        }
    }
}
