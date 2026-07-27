using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK.Events;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    public partial class ViewAchievementsControl : UserControl, IFullscreenControllerNavigable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ICacheManager _cacheManager;
        private DataGridRow _pendingRightClickRow;
        private string _pendingFocusAchievementId;

        public ViewAchievementsControl()
        {
            InitializeComponent();
        }

        public ViewAchievementsControl(
            Guid gameId,
            RefreshRuntime refreshRuntime,
            AchievementDataService achievementDataService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            AchievementOverridesService achievementOverridesService,
            ICacheManager cacheManager)
        {
            InitializeComponent();

            _settings = settings;
            _logger = logger;
            _playniteApi = playniteApi;
            _achievementOverridesService = achievementOverridesService;
            _cacheManager = cacheManager;
            DataContext = new ViewAchievementsViewModel(gameId, refreshRuntime, achievementDataService, playniteApi, logger, settings);
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            }

            // Subscribe to settings saved event to refresh when credentials change
            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            RefreshView();
            AchievementsDataGridControl?.Refresh();
            UpdateDefaultSortIndicator();
        }

        private ViewAchievementsViewModel ViewModel => DataContext as ViewAchievementsViewModel;

        public string WindowTitle => ViewModel?.GameName != null
            ? $"{ViewModel.GameName} - Achievements"
            : "Achievements";

        public void RefreshView()
        {
            ViewModel?.RefreshView();
        }

        public void Cleanup()
        {
            PlayniteAchievementsPlugin.SettingsSaved -= Plugin_SettingsSaved;
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            ViewModel?.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateDefaultSortIndicator();
        }

        // Invoked by AchievementHotkeyService when F5 is pressed while focus is within this view.
        // Refreshes this single game.
        public void TriggerHotkeyRefresh()
        {
            var command = ViewModel?.RefreshGameCommand;
            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(ViewAchievementsViewModel.HasCustomAchievementOrder))
            {
                Dispatcher.BeginInvoke(new Action(UpdateDefaultSortIndicator));
            }

            if (e.PropertyName == nameof(ViewAchievementsViewModel.IsLoading))
            {
                Dispatcher.BeginInvoke(
                    new Action(ApplyPendingAchievementFocus),
                    System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        /// <summary>
        /// Requests that the achievement identified by <paramref name="achievementId"/>
        /// (ApiName, or DisplayName as a fallback) is selected and scrolled into view once
        /// the achievement data has loaded.
        /// </summary>
        public void FocusAchievement(string achievementId)
        {
            if (string.IsNullOrWhiteSpace(achievementId))
            {
                return;
            }

            _pendingFocusAchievementId = achievementId;
            Dispatcher.BeginInvoke(
                new Action(ApplyPendingAchievementFocus),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ApplyPendingAchievementFocus()
        {
            var achievementId = _pendingFocusAchievementId;
            if (string.IsNullOrWhiteSpace(achievementId) || ViewModel == null || ViewModel.IsLoading)
            {
                return;
            }

            // Data has loaded; the request is consumed whether or not a match is found so a
            // later reload does not yank the scroll position back.
            _pendingFocusAchievementId = null;

            var items = ViewModel.Achievements;
            var target = items?.FirstOrDefault(item =>
                             !string.IsNullOrWhiteSpace(item?.ApiName) &&
                             string.Equals(item.ApiName, achievementId, StringComparison.OrdinalIgnoreCase))
                         ?? items?.FirstOrDefault(item =>
                             string.Equals(item?.DisplayName, achievementId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                return;
            }

            // Drills into the achievement's category first when the grid opened in category
            // mode, so the clicked achievement ends up visible and highlighted.
            AchievementsDataGridControl?.FocusAchievementItem(target);
        }

        private void UpdateDefaultSortIndicator()
        {
            if (ViewModel == null)
            {
                AchievementsDataGridControl?.SetSortIndicator(null, null);
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                ViewModel.CurrentSortPath,
                ViewModel.CurrentSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.SingleGame,
                (sortPath, sortDirection) => AchievementsDataGridControl?.SetSortIndicator(sortPath, sortDirection));
        }

        private void OnGridSorting(object sender, DataGridSortingEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                ViewModel.CurrentSortPath,
                ViewModel.CurrentSortDirection,
                _settings?.Persisted,
                AchievementSortSurface.SingleGame,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            e.Handled = true;

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                ViewModel.ResetSortToDefault();
                AchievementsDataGridControl?.SetSortIndicator(null, null);
                return;
            }
            else if (sortAction.Direction.HasValue)
            {
                ViewModel.SortDataGrid(sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            UpdateDefaultSortIndicator();
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (FullscreenControllerNavigationService.IsBackInput(input))
            {
                Window.GetWindow(this)?.Close();
                return true;
            }

            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return TryOpenFocusedSelectorContextMenu() ||
                       (AchievementsDataGridControl?.IsColumnHeaderFocusedForController() == true &&
                        AchievementsDataGridControl.OpenColumnVisibilityMenuForController()) ||
                       TryOpenSelectedAchievementContextMenu();
            }

            if (FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                if (AchievementsDataGridControl?.IsColumnHeaderFocusedForController() == true)
                {
                    return AchievementsDataGridControl.ActivateFocusedColumnHeaderForController();
                }

                if (AchievementsDataGridControl?.IsKeyboardFocusWithin == true)
                {
                    return AchievementsDataGridControl.ActivateSelectedItem();
                }

                return FullscreenControllerNavigationService.ActivateFocusedElement();
            }

            return false;
        }

        private DataGridRow _pendingSummaryRightClickRow;

        private void GameSummaryRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingSummaryRightClickRow = row;
            }
        }

        private void GameSummaryRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                var targetRow = _pendingSummaryRightClickRow ?? row;
                _pendingSummaryRightClickRow = null;
                OpenGameSummaryContextMenu(targetRow);
            }
        }

        private void OpenGameSummaryContextMenu(DataGridRow row)
        {
            if (row == null || row.DataContext == null)
            {
                return;
            }

            var menu = GameRowContextMenuBuilder.BuildGameMenu(
                row.DataContext,
                this,
                ViewModel?.RefreshGameCommand,
                ViewModel?.OpenGameInLibraryCommand,
                gameId => PlayniteAchievementsPlugin.Instance?.OpenManageAchievementsView(gameId),
                _playniteApi,
                _achievementOverridesService,
                _cacheManager,
                _logger);
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        }

        private void GameNameBreadcrumb_Click(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel?.IsCategorySelected == true)
            {
                AchievementsDataGridControl.ExitDrilledCategory();
            }
        }

        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                OpenContextMenuForRow(targetRow);
            }
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }

        private bool TryOpenSelectedAchievementContextMenu()
        {
            var row = FullscreenControllerNavigationService.GetTargetDataGridRow(
                AchievementsDataGridControl?.InternalDataGrid);
            if (row == null)
            {
                return false;
            }

            return OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null)
            {
                return false;
            }

            var menu = new ContextMenu();
            AchievementRowOptionsMenuBuilder.AppendAchievementOptions(
                menu,
                row.DataContext,
                this,
                RefreshAfterRowOptionsChanged);
            if (menu.Items.Count == 0)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            if (useControllerPlacement)
            {
                return FullscreenControllerNavigationService.OpenContextMenu(row, menu);
            }

            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        private void RefreshAfterRowOptionsChanged()
        {
            RefreshView();
            AchievementsDataGridControl?.Refresh();
            UpdateDefaultSortIndicator();
        }

        private bool TryOpenFocusedSelectorContextMenu()
        {
            if (AchievementsDataGridControl?.OpenFocusedControlBarMenuForController() == true)
            {
                return true;
            }

            return false;
        }
    }
}


