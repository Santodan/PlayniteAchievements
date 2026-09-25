using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Settings;
using PlayniteAchievements.Services.Showcase;
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

        public ShowcaseWidgetOptionsControl(
            ShowcaseWidgetInstanceSettings settings,
            Action persist = null,
            bool publishChanges = true,
            Thickness? margin = null,
            bool loadStyles = true)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
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
                case ShowcaseWidgetKind.NativePoints:
                case ShowcaseWidgetKind.IconMosaic:
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                case ShowcaseWidgetKind.RecentAchievements:
                case ShowcaseWidgetKind.GameSummaries:
                case ShowcaseWidgetKind.ActivityCalendar:
                    return true;
                default:
                    return false;
            }
        }

        private UIElement Build(Thickness margin)
        {
            var panel = new StackPanel { Margin = margin };
            switch (_settings.Kind)
            {
                case ShowcaseWidgetKind.Profile:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileMedals"),
                        new[] { ShowcaseProfileMedalMode.Rarity, ShowcaseProfileMedalMode.Trophy, ShowcaseProfileMedalMode.Both },
                        ShowcaseWidgetOptions.GetProfileMedalMode(_settings),
                        value => ShowcaseWidgetOptions.SetProfileMedalMode(_settings, value),
                        ProfileMedalModeName);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_Display_Alignment"),
                        new[] { false, true },
                        ShowcaseWidgetOptions.GetProfileCentered(_settings),
                        value => ShowcaseWidgetOptions.SetProfileCentered(_settings, value),
                        value => Localize(value
                            ? "LOCPlayAch_Settings_GridAlignment_Center"
                            : "LOCPlayAch_Settings_GridAlignment_Left"));
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileFullBleed"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetProfileFullBleed(_settings),
                        value => ShowcaseWidgetOptions.SetProfileFullBleed(_settings, value),
                        OnOffLabel);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ProfileLinks"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetProfileShowLinks(_settings),
                        value => ShowcaseWidgetOptions.SetProfileShowLinks(_settings, value),
                        OnOffLabel);
                    AddProfileStatSlots(panel);
                    break;
                case ShowcaseWidgetKind.Scores:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ScoreCards"),
                        new[] { ShowcaseScoreMode.Dual, ShowcaseScoreMode.Collection, ShowcaseScoreMode.Prestige },
                        ShowcaseWidgetOptions.GetScoreMode(_settings),
                        value => ShowcaseWidgetOptions.SetScoreMode(_settings, value),
                        ScoreModeName);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ScoreHistory"),
                        new[]
                        {
                            ShowcaseScoreHistoryMode.Dual,
                            ShowcaseScoreHistoryMode.Collection,
                            ShowcaseScoreHistoryMode.Prestige,
                            ShowcaseScoreHistoryMode.None
                        },
                        ShowcaseWidgetOptions.GetScoreHistoryMode(_settings),
                        value => ShowcaseWidgetOptions.SetScoreHistoryMode(_settings, value),
                        ScoreHistoryModeName);
                    AddRangeChoice(panel);

                    break;
                case ShowcaseWidgetKind.Pie:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Mode"),
                        new[]
                        {
                            ShowcasePieMode.CompletedGames,
                            ShowcasePieMode.Provider,
                            ShowcasePieMode.Rarity,
                            ShowcasePieMode.Trophy
                        },
                        ShowcaseWidgetOptions.GetPieMode(_settings),
                        value => ShowcaseWidgetOptions.SetPieMode(_settings, value),
                        PieModeName);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_ShowOverviewPiePercentages"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetPieShowCenterPercentage(_settings),
                        value => ShowcaseWidgetOptions.SetPieShowCenterPercentage(_settings, value),
                        OnOffLabel);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_ShowLegend"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetPieShowLegend(_settings),
                        value => ShowcaseWidgetOptions.SetPieShowLegend(_settings, value),
                        OnOffLabel);
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Settings_PieIncludeLocked"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetPieIncludeLocked(_settings),
                        value => ShowcaseWidgetOptions.SetPieIncludeLocked(_settings, value),
                        OnOffLabel);
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
                    break;
                case ShowcaseWidgetKind.Timeline:
                    AddRangeChoice(panel);

                    break;
                case ShowcaseWidgetKind.NativePoints:
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_GroupBy"),
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
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Mode"),
                        new[] { ShowcaseMosaicContent.Achievements, ShowcaseMosaicContent.Games },
                        ShowcaseWidgetOptions.GetMosaicContent(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetMosaicContent(_settings, value);
                            achievementMosaicPanel.Visibility = value == ShowcaseMosaicContent.Achievements
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                            gameMosaicPanel.Visibility = value == ShowcaseMosaicContent.Games
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        },
                        MosaicContentName);
                    panel.Children.Add(achievementMosaicPanel);
                    panel.Children.Add(gameMosaicPanel);
                    achievementMosaicPanel.Visibility =
                        ShowcaseWidgetOptions.GetMosaicContent(_settings) == ShowcaseMosaicContent.Achievements
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    gameMosaicPanel.Visibility =
                        ShowcaseWidgetOptions.GetMosaicContent(_settings) == ShowcaseMosaicContent.Games
                            ? Visibility.Visible
                            : Visibility.Collapsed;

                    FrameworkElement achievementMosaicCollectionRow = null;
                    var unlockNextRows = new List<FrameworkElement>();
                    var achievementSortRows = new List<FrameworkElement>();
                    AddChoice(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Showcase_Source"),
                        new[]
                        {
                            ShowcaseMosaicSource.Recent,
                            ShowcaseMosaicSource.Rarest,
                            ShowcaseMosaicSource.Capstones,
                            ShowcaseMosaicSource.Pinned,
                            ShowcaseMosaicSource.UnlockNext
                        },
                        ShowcaseWidgetOptions.GetMosaicSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetMosaicSource(_settings, value);
                            if (achievementMosaicCollectionRow != null)
                            {
                                achievementMosaicCollectionRow.Visibility = value == ShowcaseMosaicSource.Pinned
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }

                            ApplyUnlockNextVisibility(unlockNextRows, achievementSortRows, value);
                        },
                        MosaicSourceName);
                    achievementMosaicCollectionRow = AddPinCollectionChoice(achievementMosaicPanel, achievementCollection: true);
                    achievementMosaicCollectionRow.Visibility = ShowcaseWidgetOptions.GetMosaicSource(_settings) ==
                        ShowcaseMosaicSource.Pinned
                        ? Visibility.Visible
                        : Visibility.Collapsed;

                    // Unlock Next ranks during selection, so its criterion replaces the generic
                    // sort rows rather than stacking with them.
                    unlockNextRows.AddRange(AddUnlockNextOptions(achievementMosaicPanel));


                    AddChoice(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Settings_ToastShowRarityGlow"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetMosaicShowRarityGlow(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicShowRarityGlow(_settings, value),
                        OnOffLabel);
                    achievementSortRows.Add(AddChoice(
                        achievementMosaicPanel,
                        Localize("LOCPlayAch_Settings_SortBy"),
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

                    FrameworkElement mosaicGameCollectionRow = null;
                    FrameworkElement finishNextOptions = null;
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Showcase_Source"),
                        new[]
                        {
                            ShowcaseGameMosaicSource.Completed,
                            ShowcaseGameMosaicSource.All,
                            ShowcaseGameMosaicSource.Pinned,
                            ShowcaseGameMosaicSource.PlayniteFavorites,
                            ShowcaseGameMosaicSource.FinishNext
                        },
                        ShowcaseWidgetOptions.GetGameMosaicSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetGameMosaicSource(_settings, value);
                            if (mosaicGameCollectionRow != null)
                            {
                                mosaicGameCollectionRow.Visibility = value == ShowcaseGameMosaicSource.Pinned
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }

                            if (finishNextOptions != null)
                            {
                                finishNextOptions.Visibility = value == ShowcaseGameMosaicSource.FinishNext
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }
                        },
                        GameMosaicSourceName);
                    mosaicGameCollectionRow = AddPinCollectionChoice(gameMosaicPanel, achievementCollection: false);
                    mosaicGameCollectionRow.Visibility = ShowcaseWidgetOptions.GetGameMosaicSource(_settings) ==
                        ShowcaseGameMosaicSource.Pinned
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    finishNextOptions = AddFinishNextOptions(gameMosaicPanel);
                    finishNextOptions.Visibility = ShowcaseWidgetOptions.GetGameMosaicSource(_settings) ==
                        ShowcaseGameMosaicSource.FinishNext
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_UseCoverImages"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetGameMosaicUseCovers(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicUseCovers(_settings, value),
                        OnOffLabel);
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_ShowCompletionGlow"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetGameMosaicShowCompletionGlow(_settings),
                        value => ShowcaseWidgetOptions.SetGameMosaicShowCompletionGlow(_settings, value),
                        OnOffLabel);
                    AddChoice(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_SortBy"),
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
                        Localize("LOCPlayAch_Settings_Style_SizeHeader") + " (px)",
                        () => ShowcaseWidgetOptions.GetMosaicIconSize(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicIconSize(_settings, value));
                    AddNumberRow(
                        gameMosaicPanel,
                        Localize("LOCPlayAch_Settings_Style_SizeHeader") + " (px)",
                        () => ShowcaseWidgetOptions.GetMosaicCoverWidth(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicCoverWidth(_settings, value));
                    AddNumberRow(
                        panel,
                        Localize("LOCPlayAch_Showcase_TileSpacing"),
                        () => ShowcaseWidgetOptions.GetMosaicSpacing(_settings),
                        value => ShowcaseWidgetOptions.SetMosaicSpacing(_settings, value));
                    break;
                case ShowcaseWidgetKind.ScreenshotSlideshow:
                    FrameworkElement slideshowGameCollectionRow = null;
                    FrameworkElement slideshowAchievementCollectionRow = null;
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Source"),
                        new[]
                        {
                            ShowcaseSlideshowSource.All,
                            ShowcaseSlideshowSource.GameCollection,
                            ShowcaseSlideshowSource.AchievementCollection
                        },
                        ShowcaseWidgetOptions.GetSlideshowSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetSlideshowSource(_settings, value);
                            if (slideshowGameCollectionRow != null)
                            {
                                slideshowGameCollectionRow.Visibility =
                                    value == ShowcaseSlideshowSource.GameCollection
                                        ? Visibility.Visible
                                        : Visibility.Collapsed;
                            }

                            if (slideshowAchievementCollectionRow != null)
                            {
                                slideshowAchievementCollectionRow.Visibility =
                                    value == ShowcaseSlideshowSource.AchievementCollection
                                        ? Visibility.Visible
                                        : Visibility.Collapsed;
                            }
                        },
                        SlideshowSourceName);
                    slideshowGameCollectionRow = AddPinCollectionChoice(panel, achievementCollection: false);
                    slideshowGameCollectionRow.Visibility =
                        ShowcaseWidgetOptions.GetSlideshowSource(_settings) ==
                        ShowcaseSlideshowSource.GameCollection
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    slideshowAchievementCollectionRow = AddPinCollectionChoice(panel, achievementCollection: true);
                    slideshowAchievementCollectionRow.Visibility =
                        ShowcaseWidgetOptions.GetSlideshowSource(_settings) ==
                        ShowcaseSlideshowSource.AchievementCollection
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Variant"),
                        new[]
                        {
                            ShowcaseScreenshotVariant.All,
                            ShowcaseScreenshotVariant.Clean,
                            ShowcaseScreenshotVariant.Notification,
                            ShowcaseScreenshotVariant.Framed
                        },
                        ShowcaseWidgetOptions.GetScreenshotVariant(_settings),
                        value => ShowcaseWidgetOptions.SetScreenshotVariant(_settings, value),
                        ScreenshotVariantName);
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
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Shuffle"),
                        new[] { true, false },
                        ShowcaseWidgetOptions.GetShuffle(_settings),
                        value => ShowcaseWidgetOptions.SetShuffle(_settings, value),
                        OnOffLabel);
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
                    break;
                case ShowcaseWidgetKind.GameSummaries:
                    // The collapsed Game Summaries Grid: library scope filters only apply to
                    // the Library source, so their rows hide for the pinned/favorites sources.
                    FrameworkElement gameGridCollectionRow = null;
                    FrameworkElement gameGridFinishNextOptions = null;
                    var gameGridLibraryPanel = new StackPanel();
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Source"),
                        new[]
                        {
                            ShowcaseGameGridSource.Library,
                            ShowcaseGameGridSource.Pinned,
                            ShowcaseGameGridSource.PlayniteFavorites,
                            ShowcaseGameGridSource.FinishNext
                        },
                        ShowcaseWidgetOptions.GetGameGridSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetGameGridSource(_settings, value);
                            if (gameGridCollectionRow != null)
                            {
                                gameGridCollectionRow.Visibility = value == ShowcaseGameGridSource.Pinned
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }

                            if (gameGridFinishNextOptions != null)
                            {
                                gameGridFinishNextOptions.Visibility = value == ShowcaseGameGridSource.FinishNext
                                    ? Visibility.Visible
                                    : Visibility.Collapsed;
                            }

                            gameGridLibraryPanel.Visibility = value == ShowcaseGameGridSource.Library
                                ? Visibility.Visible
                                : Visibility.Collapsed;
                        },
                        GameGridSourceName);
                    gameGridCollectionRow = AddPinCollectionChoice(panel, achievementCollection: false);
                    gameGridCollectionRow.Visibility =
                        ShowcaseWidgetOptions.GetGameGridSource(_settings) == ShowcaseGameGridSource.Pinned
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    // Finish Next's options, the same set as on the Finish Next game mosaic.
                    gameGridFinishNextOptions = AddFinishNextOptions(panel);
                    gameGridFinishNextOptions.Visibility =
                        ShowcaseWidgetOptions.GetGameGridSource(_settings) == ShowcaseGameGridSource.FinishNext
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    panel.Children.Add(gameGridLibraryPanel);
                    gameGridLibraryPanel.Visibility =
                        ShowcaseWidgetOptions.GetGameGridSource(_settings) == ShowcaseGameGridSource.Library
                            ? Visibility.Visible
                            : Visibility.Collapsed;
                    AddChoice(
                        gameGridLibraryPanel,
                        Localize("LOCPlayAch_Filter_ActivitySelectorPlaceholder"),
                        new[]
                        {
                            GameActivityScope.All,
                            GameActivityScope.Played,
                            GameActivityScope.Unplayed
                        },
                        ShowcaseWidgetOptions.GetGameActivityScope(_settings),
                        value => ShowcaseWidgetOptions.SetGameActivityScope(_settings, value),
                        ActivityScopeName);
                    AddChoice(
                        gameGridLibraryPanel,
                        Localize("LOCPlayAch_Showcase_HideCompleted"),
                        new[] { false, true },
                        ShowcaseWidgetOptions.GetHideCompleted(_settings),
                        value => ShowcaseWidgetOptions.SetHideCompleted(_settings, value),
                        OnOffLabel);
                    break;
                case ShowcaseWidgetKind.RecentAchievements:
                    // The collapsed Achievements Grid: every achievement, or a pin collection.
                    FrameworkElement achievementGridCollectionRow = null;
                    var achievementGridUnlockNextRows = new List<FrameworkElement>();
                    AddChoice(
                        panel,
                        Localize("LOCPlayAch_Showcase_Source"),
                        new[]
                        {
                            ShowcaseAchievementGridSource.All,
                            ShowcaseAchievementGridSource.Pinned,
                            ShowcaseAchievementGridSource.UnlockNext
                        },
                        ShowcaseWidgetOptions.GetAchievementGridSource(_settings),
                        value =>
                        {
                            ShowcaseWidgetOptions.SetAchievementGridSource(_settings, value);
                            ApplyAchievementGridSourceRows(
                                achievementGridCollectionRow,
                                achievementGridUnlockNextRows,
                                value);
                        },
                        AchievementGridSourceName);
                    achievementGridCollectionRow = AddPinCollectionChoice(panel, achievementCollection: true);
                    achievementGridUnlockNextRows.AddRange(AddUnlockNextOptions(panel));
                    ApplyAchievementGridSourceRows(
                        achievementGridCollectionRow,
                        achievementGridUnlockNextRows,
                        ShowcaseWidgetOptions.GetAchievementGridSource(_settings));
                    break;
                case ShowcaseWidgetKind.ActivityCalendar:
                    AddRangeChoice(panel);

                    break;
            }

            AddGridDisplaySettingsButton(panel);
            return panel;
        }

        /// <summary>
        /// For the grid widget kinds: opens the same display settings popup as the grid's own
        /// right-click Display Settings entry, for this widget's surface. The popup edits the live
        /// grid options record and persists itself, independent of this dialog's Save/Cancel.
        /// </summary>
        private void AddGridDisplaySettingsButton(Panel panel)
        {
            var surfaceKey = ShowcaseGridSurfaces.ResolveWidgetSurface(_settings.Kind, _settings.InstanceId);
            if (surfaceKey == null)
            {
                return;
            }

            var kind = ShowcaseGridSurfaces.IsAchievementSurface(surfaceKey)
                ? GridOptionKind.Achievement
                : GridOptionKind.GameSummaries;
            var button = new Button
            {
                Content = Localize("LOCPlayAch_Menu_DisplaySettings"),
                HorizontalAlignment = HorizontalAlignment.Left,
                MinWidth = 120
            };
            button.SetResourceReference(MarginProperty, "PlayAch.Thickness.Top.Md");
            button.Click += (_, __) => Dialogs.GridDisplaySettingsDialog.Show(kind, surfaceKey, null);
            panel.Children.Add(button);
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
                        UnlockNextCriterion.NextInLine,
                        UnlockNextCriterion.Easiest,
                        UnlockNextCriterion.ClosestToCompletion
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
                AddChoice(
                    panel,
                    Localize("LOCPlayAch_Showcase_IncludeHiddenAchievements"),
                    new[] { true, false },
                    ShowcaseWidgetOptions.GetIncludeHiddenAchievements(_settings),
                    value => ShowcaseWidgetOptions.SetIncludeHiddenAchievements(_settings, value),
                    OnOffLabel)
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
            AddChoice(
                container,
                Localize("LOCPlayAch_Showcase_FinishNextIncludeUnplayed"),
                new[] { false, true },
                ShowcaseWidgetOptions.GetFinishNextIncludeUnplayed(_settings),
                value => ShowcaseWidgetOptions.SetFinishNextIncludeUnplayed(_settings, value),
                OnOffLabel);
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
            FrameworkElement collectionRow,
            IEnumerable<FrameworkElement> unlockNextRows,
            ShowcaseAchievementGridSource source)
        {
            if (collectionRow != null)
            {
                collectionRow.Visibility = source == ShowcaseAchievementGridSource.Pinned
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            foreach (var row in unlockNextRows ?? Enumerable.Empty<FrameworkElement>())
            {
                row.Visibility = source == ShowcaseAchievementGridSource.UnlockNext
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private static readonly TimelineRange[] RangeChoices =
        {
            TimelineRange.OneMonth,
            TimelineRange.ThreeMonths,
            TimelineRange.OneYear,
            TimelineRange.All
        };

        /// <summary>The shared time-range picker used by every range-windowed widget.</summary>
        private void AddRangeChoice(Panel panel)
        {
            AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_Range"),
                RangeChoices,
                ShowcaseTimelineOptions.GetRange(_settings),
                value => ShowcaseTimelineOptions.SetRange(_settings, value),
                TimelineRangeName);
        }

        /// <summary>
        /// The profile stat strip's slot editor: one combo per filled slot plus a trailing
        /// empty one, each picking a single overall statistic. Choosing a stat in the trailing
        /// combo appends a slot; choosing None in a filled combo removes it. Slots are
        /// unbounded and stored in slot order.
        /// </summary>
        private void AddProfileStatSlots(Panel panel)
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

            var slots = ShowcaseWidgetOptions.GetProfileStatKeys(_settings).ToList();
            var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
            var rebuilding = false;

            void Store()
            {
                ShowcaseWidgetOptions.SetProfileStatKeys(_settings, slots);
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

        private FrameworkElement AddPinCollectionChoice(Panel panel, bool achievementCollection)
        {
            var showcase = PlayniteAchievementsPlugin.Instance?.Settings?.Persisted?.Showcase;
            if (achievementCollection)
            {
                var selected = ShowcasePinService.ResolveAchievementCollection(
                    showcase,
                    ShowcaseWidgetOptions.GetPinCollectionId(_settings));
                var collections = (showcase?.AchievementPinCollections ??
                    new System.Collections.Generic.List<PinnedAchievementCollection>())
                    .Where(collection => collection != null)
                    .ToArray();
                return AddChoice(
                    panel,
                    Localize("LOCPlayAch_Showcase_PinCollection"),
                    collections,
                    selected,
                    value => ShowcaseWidgetOptions.SetPinCollectionId(_settings, value?.CollectionId),
                    value => value?.Name ?? string.Empty);
            }

            var selectedGameCollection = ShowcasePinService.ResolveGameCollection(
                showcase,
                ShowcaseWidgetOptions.GetPinCollectionId(_settings));
            var gameCollections = (showcase?.GamePinCollections ??
                new System.Collections.Generic.List<PinnedGameCollection>())
                .Where(collection => collection != null)
                .ToArray();
            return AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_PinCollection"),
                gameCollections,
                selectedGameCollection,
                value => ShowcaseWidgetOptions.SetPinCollectionId(_settings, value?.CollectionId),
                value => value?.Name ?? string.Empty);
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
        private Grid AddLastPlayedWindowChoice(Panel panel) =>
            AddChoice(
                panel,
                Localize("LOCPlayAch_Showcase_LastPlayedWindow"),
                new[]
                {
                    TimelineRange.SevenDays,
                    TimelineRange.OneMonth,
                    TimelineRange.ThreeMonths,
                    TimelineRange.OneYear,
                    TimelineRange.All
                },
                ShowcaseWidgetOptions.GetLastPlayedWindow(_settings),
                value => ShowcaseWidgetOptions.SetLastPlayedWindow(_settings, value),
                TimelineRangeName);

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
                Localize("LOCMenuSortByDirection"),
                new[] { true, false },
                ShowcaseWidgetOptions.GetMosaicSortDescending(_settings),
                value => ShowcaseWidgetOptions.SetMosaicSortDescending(_settings, value),
                SortDirectionLabel);

        private static string OnOffLabel(bool value) => value
            ? Localize("LOCPlayAch_Settings_Override_On")
            : Localize("LOCPlayAch_Settings_Override_Off");

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
                    return Localize("LOCPlayAch_Column_LastPlayed");
                case GameSummariesSortMode.TotalAchievements:
                    return Localize("LOCPlayAch_Column_Total");
                case GameSummariesSortMode.Progress:
                    return Localize("LOCPlayAch_Progress");
                case GameSummariesSortMode.Alphabetical:
                    return Localize("LOCPlayAch_Column_Name");
                default:
                    return Localize("LOCPlayAch_Common_Default");
            }
        }

        private static string ActivityScopeName(GameActivityScope value)
        {
            switch (value)
            {
                case GameActivityScope.Played:
                    return Localize("LOCPlayAch_Filter_Played");
                case GameActivityScope.Unplayed:
                    return Localize("LOCPlayAch_Filter_Unplayed");
                default:
                    return Localize("LOCPlayAch_Common_All");
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
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
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
