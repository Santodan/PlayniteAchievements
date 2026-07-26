using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views
{
    public partial class ViewFriendsAchievementsControl : UserControl, IFullscreenControllerNavigable
    {
        private readonly IPlayniteAPI _playniteApi;
        private readonly ILogger _logger;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly ICacheManager _cacheManager;
        private DataGridRow _pendingFriendRightClickRow;
        private DataGridRow _pendingSummaryRightClickRow;

        public ViewFriendsAchievementsControl()
        {
            InitializeComponent();
        }

        internal ViewFriendsAchievementsControl(
            Guid gameId,
            FriendGameAchievementsDataCoordinator dataCoordinator,
            RefreshRuntime refreshRuntime,
            RefreshEntryPoint refreshEntryPoint,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings,
            AchievementOverridesService achievementOverridesService,
            ICacheManager cacheManager)
        {
            InitializeComponent();
            _playniteApi = playniteApi;
            _logger = logger;
            _achievementOverridesService = achievementOverridesService;
            _cacheManager = cacheManager;
            DataContext = new ViewFriendsAchievementsViewModel(
                gameId,
                dataCoordinator,
                refreshRuntime,
                refreshEntryPoint,
                playniteApi,
                logger,
                settings);
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            }

            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
        }

        private ViewFriendsAchievementsViewModel ViewModel => DataContext as ViewFriendsAchievementsViewModel;

        public string WindowTitle => !string.IsNullOrWhiteSpace(ViewModel?.GameName)
            ? string.Format(
                ResourceProvider.GetString("LOCPlayAch_ViewFriendsAchievements_WindowTitle"),
                ViewModel.GameName)
            : ResourceProvider.GetString("LOCPlayAch_ViewFriendsAchievements_TitleFallback");

        public void RefreshView()
        {
            ViewModel?.RefreshView();
            GameSummaryGridControl?.Refresh();
            SelectedFriendGameSummaryGridControl?.Refresh();
            FriendSummariesGridControl?.Refresh();
            FriendsAchievementsGrid?.Refresh();
        }

        public void TriggerHotkeyRefresh()
        {
            var command = ViewModel?.RefreshCommand;
            if (command?.CanExecute(null) == true)
            {
                command.Execute(null);
            }
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
            GameSummaryGridControl?.Refresh();
            SelectedFriendGameSummaryGridControl?.Refresh();
            FriendSummariesGridControl?.Refresh();
            FriendsAchievementsGrid?.Refresh();
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            RefreshView();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(ViewFriendsAchievementsViewModel.GameName))
            {
                var window = Window.GetWindow(this);
                if (window != null)
                {
                    window.Title = WindowTitle;
                }
            }
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
                       (FriendSummariesGridControl?.OpenColumnVisibilityMenuForController() == true) ||
                       (FriendsAchievementsGrid?.IsColumnHeaderFocusedForController() == true &&
                        FriendsAchievementsGrid.OpenColumnVisibilityMenuForController());
            }

            if (FullscreenControllerNavigationService.IsAcceptInput(input))
            {
                if (FriendsAchievementsGrid?.IsColumnHeaderFocusedForController() == true)
                {
                    return FriendsAchievementsGrid.ActivateFocusedColumnHeaderForController();
                }

                if (FriendsAchievementsGrid?.IsKeyboardFocusWithin == true)
                {
                    return FriendsAchievementsGrid.ActivateSelectedItem();
                }

                return FullscreenControllerNavigationService.ActivateFocusedElement();
            }

            return false;
        }

        private bool TryOpenFocusedSelectorContextMenu()
        {
            if (FriendSummariesGridControl?.OpenFocusedControlBarMenuForController() == true)
            {
                return true;
            }

            return FriendsAchievementsGrid?.OpenFocusedControlBarMenuForController() == true;
        }

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
                ViewModel?.RefreshCommand,
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
                FriendsAchievementsGrid?.ExitDrilledCategory();
            }
        }

        // Clicking the already-selected friend clears the selection (same toggle behavior as the
        // Friends Overview summary grids).
        private void FriendRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveContextMenuRow(sender, e, out var row) ||
                !(row.DataContext is FriendSummaryItem))
            {
                return;
            }

            var grid = VisualTreeHelpers.FindVisualParent<DataGrid>(row);
            if (grid == null || !IsSelectedRow(row, grid))
            {
                return;
            }

            if (ViewModel != null)
            {
                ViewModel.SelectedFriend = null;
            }

            ClearGridSelection(grid);
            e.Handled = true;
        }

        private static bool IsSelectedRow(DataGridRow row, DataGrid grid)
        {
            return row != null &&
                   grid != null &&
                   (row.IsSelected ||
                   ReferenceEquals(grid.SelectedItem, row.DataContext) ||
                   ReferenceEquals(grid.CurrentItem, row.DataContext));
        }

        private static void ClearGridSelection(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            try
            {
                grid.SelectedItem = null;
                grid.UnselectAll();
                grid.CurrentItem = null;
                Keyboard.ClearFocus();
            }
            catch
            {
                // Best effort; focus clearing should not break row toggling.
            }
        }

        private void FriendRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingFriendRightClickRow = row;
            }
        }

        private void FriendRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveContextMenuRow(sender, e, out var row))
            {
                _pendingFriendRightClickRow = null;
                return;
            }

            e.Handled = true;
            var targetRow = _pendingFriendRightClickRow ?? row;
            _pendingFriendRightClickRow = null;
            if (!(targetRow.DataContext is FriendSummaryItem friend))
            {
                return;
            }

            var menu = BuildFriendContextMenu(friend);
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            menu.PlacementTarget = targetRow;
            menu.IsOpen = true;
        }

        private ContextMenu BuildFriendContextMenu(FriendSummaryItem friend)
        {
            var menu = new ContextMenu();
            menu.Items.Add(CreateMenuItem(
                "LOCPlayAch_ViewFriendsAchievements_RefreshSelectedFriend",
                "Refresh Friend for This Game",
                () =>
                {
                    var command = ViewModel?.RefreshSelectedFriendCommand;
                    if (command?.CanExecute(friend) == true)
                    {
                        command.Execute(friend);
                    }
                }));

            menu.Items.Add(CreateMenuItem(
                "LOCPlayAch_Menu_OpenGameInLibrary",
                "Open Game in Library",
                () =>
                {
                    var command = ViewModel?.OpenGameInLibraryCommand;
                    if (command?.CanExecute(null) == true)
                    {
                        command.Execute(null);
                    }
                }));

            return menu;
        }

        private static MenuItem CreateMenuItem(string resourceKey, string fallback, Action execute)
        {
            var header = ResourceProvider.GetString(resourceKey);
            return new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(header) ? fallback : header,
                Command = new PlayniteAchievements.Common.RelayCommand(_ => execute?.Invoke())
            };
        }

        private static bool TryResolveContextMenuRow(
            object sender,
            MouseButtonEventArgs e,
            out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }
    }
}
