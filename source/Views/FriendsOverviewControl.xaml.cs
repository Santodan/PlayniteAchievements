using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayniteAchievements.Views
{
    public partial class FriendsOverviewControl : UserControl, IDisposable
    {
        public static readonly DependencyProperty IsEmbeddedProperty =
            DependencyProperty.Register(
                nameof(IsEmbedded),
                typeof(bool),
                typeof(FriendsOverviewControl),
                new PropertyMetadata(false));

        private readonly FriendsOverviewViewModel _viewModel;
        private readonly ILogger _logger;
        private readonly OverviewLaunchContext _launchContext;
        private readonly IPlayniteAPI _playniteApi;
        private readonly ICacheManager _cacheManager;
        private readonly IFriendCacheManager _friendCache;
        private readonly AchievementOverridesService _achievementOverridesService;
        private readonly Action _persistSettingsForUi;
        private const double FriendsOverviewColumnRatioChangeThreshold = 0.001d;
        private bool _loaded;
        private DataGridRow _pendingRightClickRow;

        public FriendsOverviewControl()
        {
            InitializeComponent();
        }

        public bool IsEmbedded
        {
            get => (bool)GetValue(IsEmbeddedProperty);
            set => SetValue(IsEmbeddedProperty, value);
        }

        internal IOverviewRefreshHeaderViewModel RefreshHeader => _viewModel;

        internal FriendsOverviewViewModel ViewModel => _viewModel;

        internal bool HasAnySelection => _viewModel?.HasAnySelection == true;

        internal FriendsOverviewControl(
            ILogger logger,
            IFriendCacheManager friendCache,
            RefreshEntryPoint refreshCoordinator,
            RefreshRuntime refreshRuntime,
            PlayniteAchievementsSettings settings,
            Action persistSettingsForUi,
            OverviewLaunchContext launchContext = OverviewLaunchContext.Sidebar,
            IPlayniteAPI playniteApi = null,
            ICacheManager cacheManager = null,
            AchievementOverridesService achievementOverridesService = null,
            FriendsOverviewDataCoordinator friendsOverviewDataCoordinator = null)
        {
            InitializeComponent();
            _logger = logger;
            _launchContext = launchContext;
            _playniteApi = playniteApi;
            _cacheManager = cacheManager;
            _friendCache = friendCache;
            _achievementOverridesService = achievementOverridesService;
            _persistSettingsForUi = persistSettingsForUi;
            _viewModel = new FriendsOverviewViewModel(
                friendCache,
                refreshCoordinator,
                refreshRuntime,
                settings,
                logger,
                playniteApi,
                (gameId, gameName) =>
                {
                    if (playniteApi == null || refreshRuntime == null)
                    {
                        return null;
                    }

                    return FriendCustomRefreshControl.TryShowDialog(
                        playniteApi,
                        refreshRuntime,
                        friendCache,
                        persistSettingsForUi,
                        settings,
                        logger,
                        gameId,
                        gameName,
                        friendsOverviewDataCoordinator,
                        out var options)
                        ? options
                        : null;
                },
                friendsOverviewDataCoordinator: friendsOverviewDataCoordinator);
            DataContext = _viewModel;
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private async void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            ApplyFriendsOverviewColumnRatios();

            if (_viewModel != null)
            {
                await _viewModel.LoadAsync().ConfigureAwait(true);
            }
        }

        public void Dispose()
        {
            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            FriendSummariesGridControl?.Dispose();
            FriendGameSummariesGridControl?.Dispose();
            SelectedFriendGameSummariesGridControl?.Dispose();
            FriendsAchievementsGrid?.Dispose();
            _viewModel?.Dispose();
        }

        internal void ClearSelectionFromHost()
        {
            ClearSelection();
        }

        internal bool OpenFocusedControlBarMenuForController()
        {
            return FriendSummariesGridControl?.OpenFocusedControlBarMenuForController() == true ||
                   FriendGameSummariesGridControl?.OpenFocusedControlBarMenuForController() == true ||
                   SelectedFriendGameSummariesGridControl?.OpenFocusedControlBarMenuForController() == true ||
                   FriendsAchievementsGrid?.OpenFocusedControlBarMenuForController() == true;
        }

        private void CloseViewButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_launchContext == OverviewLaunchContext.Popout)
                {
                    Window.GetWindow(this)?.Close();
                    return;
                }

                PlayniteUiProvider.RestoreMainView();
            }
            catch (Exception ex)
            {
                LogManager.GetLogger()?.Debug(ex, "Failed to close friends overview view.");
            }
        }

        private void ClearSelection_Click(object sender, RoutedEventArgs e)
        {
            ClearSelection();
        }

        private void GameNameBreadcrumb_Click(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel?.IsCategorySelected == true)
            {
                FriendsAchievementsGrid?.ExitDrilledCategory();
            }
        }

        private void ClearSelection()
        {
            _viewModel?.ClearSelection();
            ClearGridSelection(FriendSummariesGridControl?.InternalDataGrid);
            ClearGridSelection(FriendGameSummariesGridControl?.InternalDataGrid);
            ClearGridSelection(SelectedFriendGameSummariesGridControl?.InternalDataGrid);
            ClearGridSelection(FriendsAchievementsGrid?.InternalDataGrid);
        }

        private void ClearFriendSelection_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ClearFriendSelection();
            ClearGridSelection(FriendSummariesGridControl?.InternalDataGrid);
        }

        private void ClearGameSelection_Click(object sender, RoutedEventArgs e)
        {
            _viewModel?.ClearGameSelection();
            ClearGridSelection(FriendGameSummariesGridControl?.InternalDataGrid);
            ClearGridSelection(SelectedFriendGameSummariesGridControl?.InternalDataGrid);
        }

        private void RefreshModeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            OpenSingleSelectRefreshModeContextMenu(
                RefreshModeSelectionButton,
                _viewModel.FriendRefreshModes,
                _viewModel.SelectedRefreshMode,
                selectedKey => _viewModel.SelectedRefreshMode = selectedKey);
        }

        private static void OpenSingleSelectRefreshModeContextMenu(
            Button button,
            IEnumerable<RefreshMode> modes,
            string selectedModeKey,
            Action<string> setSelection)
        {
            if (button == null || setSelection == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var mode in modes?.Where(mode => mode != null && !string.IsNullOrWhiteSpace(mode.Key)) ?? Enumerable.Empty<RefreshMode>())
            {
                var modeKey = mode.Key;
                var item = new MenuItem
                {
                    Header = !string.IsNullOrWhiteSpace(mode.ShortDisplayName)
                        ? mode.ShortDisplayName
                        : (!string.IsNullOrWhiteSpace(mode.DisplayName) ? mode.DisplayName : modeKey),
                    IsCheckable = true,
                    IsChecked = string.Equals(modeKey, selectedModeKey, StringComparison.Ordinal)
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                item.Click += (_, __) => setSelection(modeKey);
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null || menu.Items.Count == 0)
            {
                return;
            }

            RoutedEventHandler onClosed = null;
            onClosed = (_, __) =>
            {
                menu.Closed -= onClosed;
                button.ReleaseMouseCapture();
            };

            menu.Closed += onClosed;
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
        }

        private void SummaryRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            var row = ResolveDataGridRow(sender, e);
            var grid = FindParentDataGrid(row);
            if (row == null || grid == null)
            {
                return;
            }

            if (row.DataContext is FriendSummaryItem)
            {
                if (IsSelectedRow(row, grid))
                {
                    _viewModel.ClearFriendSelection();
                    ClearGridSelection(grid);
                    QueueScrollAfterSummarySelection(row.DataContext);
                    e.Handled = true;
                }
                else
                {
                    QueueScrollAfterSummarySelection(row.DataContext);
                }
            }
            else if (row.DataContext is FriendGameSummaryItem)
            {
                if (IsSelectedRow(row, grid))
                {
                    _viewModel.ClearGameSelection();
                    ClearGridSelection(grid);
                    QueueScrollAfterSummarySelection(row.DataContext);
                    e.Handled = true;
                }
                else
                {
                    QueueScrollAfterSummarySelection(row.DataContext);
                }
            }
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveSelectedRow(sender, e, out var row, out var grid) ||
                !(row.DataContext is AchievementDisplayItem))
            {
                return;
            }

            ClearGridSelection(grid);
            e.Handled = true;
        }

        private void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void DataGridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                OpenContextMenuForRow(targetRow);
            }
        }

        private void FriendSummaries_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = sender as DataGrid;
            if (grid == null) return;

            var settings = _viewModel.Settings?.Persisted;
            var sortAction = FriendSummarySortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.FriendSortPath,
                _viewModel.FriendSortDirection,
                settings);
            if (sortAction.Kind == FriendSummariesGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == FriendSummariesGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultFriendSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetFriendSummariesSortDirection();
        }

        private void GameSummaries_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var grid = sender as DataGrid;
            if (grid == null) return;

            var settings = _viewModel.Settings?.Persisted;
            var sortAction = GameSummariesSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.GameSortPath,
                _viewModel.GameSortDirection,
                settings,
                GameSummariesSortSurface.FriendsOverview);
            if (sortAction.Kind == GameSummariesGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == GameSummariesGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultGameSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetGameSummariesSortDirection();
        }

        private void Achievements_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (_viewModel == null) return;
            e.Handled = true;

            var control = sender as Controls.AchievementDataGridControl;
            var grid = control?.InternalDataGrid;
            if (grid == null) return;

            var settings = _viewModel.Settings?.Persisted;
            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _viewModel.AchievementSortPath,
                _viewModel.AchievementSortDirection,
                settings,
                AchievementSortSurface.FriendsOverviewRecentAchievements,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                _viewModel.ApplyDefaultAchievementSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                _viewModel.SortDataGrid(grid, sortAction.SortMemberPath, sortAction.Direction.Value);
            }

            ResetAchievementsSortDirection();
        }

        private void ResetFriendSummariesSortDirection()
        {
            if (FriendSummariesGridControl == null)
            {
                return;
            }

            FriendSummarySortHelper.ApplySortIndicator(
                _viewModel?.FriendSortPath,
                _viewModel?.FriendSortDirection,
                _viewModel?.Settings?.Persisted,
                (sortPath, sortDirection) => FriendSummariesGridControl.SetSortIndicator(sortPath, sortDirection));
        }

        private void ResetGameSummariesSortDirection()
        {
            GameSummariesSortHelper.ApplySortIndicator(
                _viewModel?.GameSortPath,
                _viewModel?.GameSortDirection,
                _viewModel?.Settings?.Persisted,
                (sortPath, sortDirection) =>
                {
                    FriendGameSummariesGridControl?.SetSortIndicator(sortPath, sortDirection);
                    SelectedFriendGameSummariesGridControl?.SetSortIndicator(sortPath, sortDirection);
                },
                GameSummariesSortSurface.FriendsOverview);
        }

        private void ResetAchievementsSortDirection()
        {
            AchievementSortHelper.ApplySortIndicator(
                _viewModel?.AchievementSortPath,
                _viewModel?.AchievementSortDirection,
                _viewModel?.Settings?.Persisted,
                AchievementSortSurface.FriendsOverviewRecentAchievements,
                (sortPath, sortDirection) => FriendsAchievementsGrid?.SetSortIndicator(sortPath, sortDirection));
        }

        private void ApplyFriendsOverviewColumnRatios()
        {
            var persisted = _viewModel?.Settings?.Persisted;
            SetFriendsOverviewColumnRatios(
                persisted?.FriendsOverviewFriendColumnRatio ?? PersistedSettings.DefaultFriendsOverviewFriendColumnRatio,
                persisted?.FriendsOverviewGameColumnRatio ?? PersistedSettings.DefaultFriendsOverviewGameColumnRatio);
        }

        private void FriendsOverviewGridSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(PersistFriendsOverviewColumnRatios), DispatcherPriority.Background);
        }

        private void PersistFriendsOverviewColumnRatios()
        {
            var persisted = _viewModel?.Settings?.Persisted;
            if (persisted == null || !TryGetFriendsOverviewColumnRatios(out var friendRatio, out var gameRatio))
            {
                return;
            }

            if (Math.Abs(persisted.FriendsOverviewFriendColumnRatio - friendRatio) <= FriendsOverviewColumnRatioChangeThreshold &&
                Math.Abs(persisted.FriendsOverviewGameColumnRatio - gameRatio) <= FriendsOverviewColumnRatioChangeThreshold)
            {
                SetFriendsOverviewColumnRatios(friendRatio, gameRatio);
                return;
            }

            persisted.FriendsOverviewFriendColumnRatio = friendRatio;
            persisted.FriendsOverviewGameColumnRatio = gameRatio;
            SetFriendsOverviewColumnRatios(
                persisted.FriendsOverviewFriendColumnRatio,
                persisted.FriendsOverviewGameColumnRatio);
            _persistSettingsForUi?.Invoke();
        }

        private bool TryGetFriendsOverviewColumnRatios(out double friendRatio, out double gameRatio)
        {
            friendRatio = PersistedSettings.DefaultFriendsOverviewFriendColumnRatio;
            gameRatio = PersistedSettings.DefaultFriendsOverviewGameColumnRatio;

            var friend = FriendsOverviewFriendColumn?.ActualWidth ?? 0d;
            var game = FriendsOverviewGameColumn?.ActualWidth ?? 0d;
            var achievements = FriendsOverviewAchievementColumn?.ActualWidth ?? 0d;
            if (!ColumnWidthNormalization.IsValidWidth(friend) ||
                !ColumnWidthNormalization.IsValidWidth(game) ||
                !ColumnWidthNormalization.IsValidWidth(achievements))
            {
                return false;
            }

            var combined = friend + game + achievements;
            if (!ColumnWidthNormalization.IsValidWidth(combined))
            {
                return false;
            }

            friendRatio = NormalizeFriendsOverviewColumnRatio(friend / combined);
            gameRatio = NormalizeFriendsOverviewColumnRatio(game / combined);
            return friendRatio + gameRatio < 1d;
        }

        private void SetFriendsOverviewColumnRatios(double friendRatio, double gameRatio)
        {
            if (FriendsOverviewFriendColumn == null ||
                FriendsOverviewGameColumn == null ||
                FriendsOverviewAchievementColumn == null)
            {
                return;
            }

            friendRatio = NormalizeFriendsOverviewColumnRatio(friendRatio);
            gameRatio = NormalizeFriendsOverviewColumnRatio(gameRatio);
            if (friendRatio + gameRatio >= 1d)
            {
                friendRatio = PersistedSettings.DefaultFriendsOverviewFriendColumnRatio;
                gameRatio = PersistedSettings.DefaultFriendsOverviewGameColumnRatio;
            }

            FriendsOverviewFriendColumn.Width = new GridLength(friendRatio, GridUnitType.Star);
            FriendsOverviewGameColumn.Width = new GridLength(gameRatio, GridUnitType.Star);
            FriendsOverviewAchievementColumn.Width = new GridLength(1d - friendRatio - gameRatio, GridUnitType.Star);
        }

        private static double NormalizeFriendsOverviewColumnRatio(double ratio)
        {
            if (double.IsNaN(ratio) || double.IsInfinity(ratio) || ratio <= 0d || ratio >= 1d)
            {
                return PersistedSettings.MinFriendsOverviewColumnRatio;
            }

            return Math.Max(
                PersistedSettings.MinFriendsOverviewColumnRatio,
                Math.Min(PersistedSettings.MaxFriendsOverviewColumnRatio, ratio));
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null || e == null) return;

            if (e.PropertyName == nameof(FriendsOverviewViewModel.FriendSortPath) ||
                e.PropertyName == nameof(FriendsOverviewViewModel.FriendSortDirection))
            {
                ResetFriendSummariesSortDirection();
            }
            else if (e.PropertyName == nameof(FriendsOverviewViewModel.GameSortPath) ||
                     e.PropertyName == nameof(FriendsOverviewViewModel.GameSortDirection) ||
                     e.PropertyName == nameof(FriendsOverviewViewModel.HasFriendSelection))
            {
                ResetGameSummariesSortDirection();
            }
            else if (e.PropertyName == nameof(FriendsOverviewViewModel.AchievementSortPath) ||
                     e.PropertyName == nameof(FriendsOverviewViewModel.AchievementSortDirection))
            {
                ResetAchievementsSortDirection();
            }
        }

        private static bool TryResolveSelectedRow(
            object sender,
            MouseButtonEventArgs e,
            out DataGridRow row,
            out DataGrid grid)
        {
            row = ResolveDataGridRow(sender, e);
            grid = FindParentDataGrid(row);
            if (row == null || grid == null)
            {
                return false;
            }

            return IsSelectedRow(row, grid);
        }

        private static bool IsSelectedRow(DataGridRow row, DataGrid grid)
        {
            return row != null &&
                   grid != null &&
                   (row.IsSelected ||
                   ReferenceEquals(grid.SelectedItem, row.DataContext) ||
                   ReferenceEquals(grid.CurrentItem, row.DataContext));
        }

        private static DataGridRow ResolveDataGridRow(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow senderRow)
            {
                return senderRow;
            }

            if (e?.Source is DataGridRow sourceRow)
            {
                return sourceRow;
            }

            return VisualTreeHelpers.FindVisualParent<DataGridRow>(
                e?.OriginalSource as DependencyObject ?? e?.Source as DependencyObject);
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = ResolveDataGridRow(sender, e);
            return row != null;
        }

        private bool OpenContextMenuForRow(DataGridRow row)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null)
            {
                return false;
            }

            var menu = BuildRowContextMenu(row.DataContext);
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        private ContextMenu BuildRowContextMenu(object data)
        {
            if (data is FriendGameSummaryItem || data is GameSummaryItem)
            {
                return BuildGameMenu(data);
            }

            if (data is FriendSummaryItem friend)
            {
                return BuildFriendMenu(friend);
            }

            return null;
        }

        private ContextMenu BuildGameMenu(object data)
        {
            var menu = GameRowContextMenuBuilder.BuildGameMenu(
                data,
                this,
                _viewModel?.RefreshFriendSelectedGameCommand,
                _viewModel?.OpenGameInLibraryCommand,
                gameId => PlayniteAchievementsPlugin.Instance?.OpenManageAchievementsView(gameId),
                _playniteApi,
                _achievementOverridesService,
                _cacheManager,
                _logger);

            // Unowned (provider-only) friend games have no Playnite Guid, so the shared builder
            // offers them only Refresh. Add a Clear Data item that removes the game's cached
            // friend data across all friends, mirroring the owned-game Clear Data action.
            var addedMaintenanceSeparator = false;
            void EnsureMaintenanceSeparator()
            {
                if (!addedMaintenanceSeparator)
                {
                    menu.Items.Add(new Separator());
                    addedMaintenanceSeparator = true;
                }
            }

            if (menu != null && IsMappableExophaseFriendGame(data, out var exophaseGame))
            {
                EnsureMaintenanceSeparator();
                menu.Items.Add(CreateTextMenuItem(
                    exophaseGame.PlayniteGameId.HasValue
                        ? GetText("LOCPlayAch_Menu_ChangePlayniteMapping", "Change Playnite Mapping")
                        : GetText("LOCPlayAch_Menu_MapToPlayniteGame", "Map to Playnite Game"),
                    () => EditExophaseFriendGameMapping(exophaseGame)));

                if (HasManualExophaseFriendGameMapping(exophaseGame))
                {
                    menu.Items.Add(CreateTextMenuItem(
                        GetText("LOCPlayAch_Menu_ClearPlayniteMapping", "Clear Playnite Mapping"),
                        () => ClearExophaseFriendGameMapping(exophaseGame)));
                }
            }

            if (menu != null && IsClearableUnownedGame(data, out var unownedGame))
            {
                EnsureMaintenanceSeparator();
                menu.Items.Add(GameRowContextMenuBuilder.CreateMenuItem(
                    this,
                    "LOCPlayAch_Menu_ClearData",
                    () => ClearUnownedGame(unownedGame)));
            }

            return menu;
        }

        private static bool IsClearableUnownedGame(object data, out FriendGameSummaryItem game)
        {
            game = data as FriendGameSummaryItem;
            return game != null
                   && !game.PlayniteGameId.HasValue
                   && !string.IsNullOrWhiteSpace(game.ProviderKey)
                   && (game.AppId > 0 || !string.IsNullOrWhiteSpace(game.ProviderGameKey));
        }

        private static bool IsMappableExophaseFriendGame(object data, out FriendGameSummaryItem game)
        {
            game = data as FriendGameSummaryItem;
            return game != null &&
                   string.Equals(game.ProviderKey, "Exophase", StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(game.ProviderGameKey);
        }

        private static bool HasManualExophaseFriendGameMapping(FriendGameSummaryItem game)
        {
            var key = ExophaseSettings.NormalizeFriendGameMappingKey(game?.ProviderGameKey);
            return !string.IsNullOrWhiteSpace(key) &&
                   ProviderRegistry.Settings<ExophaseSettings>().FriendGameMappings?.ContainsKey(key) == true;
        }

        private MenuItem CreateTextMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private void EditExophaseFriendGameMapping(FriendGameSummaryItem game)
        {
            if (!IsMappableExophaseFriendGame(game, out game))
            {
                return;
            }

            try
            {
                var selected = PlayniteGamePickerDialog.Pick(
                    Window.GetWindow(this),
                    _playniteApi?.Database?.Games,
                    GetText("LOCPlayAch_Menu_MapToPlayniteGame", "Map to Playnite Game"),
                    game.GameName);
                if (selected == null)
                {
                    return;
                }

                var friendPlatform = ExophaseFriendPlatformMatcher.ExtractPlatformSlugFromFriendGameKey(game.ProviderGameKey);
                if (!ExophaseFriendPlatformMatcher.IsSameProviderPlatform(selected, friendPlatform))
                {
                    _playniteApi?.Dialogs?.ShowMessage(
                        string.Format(
                            GetText(
                                "LOCPlayAch_Menu_MapToPlayniteGame_PlatformMismatch",
                                "The selected Playnite game is not on the same platform as the Exophase friend game ({0})."),
                            string.IsNullOrWhiteSpace(friendPlatform) ? GetText("LOCPlayAch_Common_Unknown", "Unknown") : friendPlatform),
                        GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                SaveExophaseFriendGameMapping(game, selected);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to edit Exophase friend game mapping for {game.ProviderGameKey}.");
                _playniteApi?.Dialogs?.ShowErrorMessage(
                    string.Format(GetText("LOCPlayAch_Status_Failed", "Failed: {0}"), ex.Message),
                    GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"));
            }
        }

        private void SaveExophaseFriendGameMapping(FriendGameSummaryItem game, Game playniteGame)
        {
            if (game == null || playniteGame == null || playniteGame.Id == Guid.Empty)
            {
                return;
            }

            var key = ExophaseSettings.NormalizeFriendGameMappingKey(game.ProviderGameKey);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            var settings = ProviderRegistry.Settings<ExophaseSettings>();
            var mappings = new Dictionary<string, Guid>(
                settings.FriendGameMappings ?? new Dictionary<string, Guid>(),
                StringComparer.OrdinalIgnoreCase)
            {
                [key] = playniteGame.Id
            };
            settings.FriendGameMappings = mappings;
            ProviderRegistry.Write(settings, persistToDisk: true);
            PlayniteAchievementsPlugin.NotifySettingsSaved();

            if (!game.PlayniteGameId.HasValue)
            {
                var clearResult = _friendCache?.ClearUnownedFriendGame(game.ProviderKey, game.AppId, game.ProviderGameKey);
                if (clearResult != null && !clearResult.Success)
                {
                    _logger?.Warn($"Failed to clear provider-only Exophase row after mapping {game.ProviderGameKey}: {clearResult.ErrorMessage}");
                }
            }

            RefreshExophaseProviderGame(game);
            _ = _viewModel?.LoadAsync();
        }

        private void ClearExophaseFriendGameMapping(FriendGameSummaryItem game)
        {
            if (!IsMappableExophaseFriendGame(game, out game))
            {
                return;
            }

            try
            {
                var key = ExophaseSettings.NormalizeFriendGameMappingKey(game.ProviderGameKey);
                var settings = ProviderRegistry.Settings<ExophaseSettings>();
                var mappings = new Dictionary<string, Guid>(
                    settings.FriendGameMappings ?? new Dictionary<string, Guid>(),
                    StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    mappings.Remove(key);
                }

                settings.FriendGameMappings = mappings;
                ProviderRegistry.Write(settings, persistToDisk: true);
                PlayniteAchievementsPlugin.NotifySettingsSaved();
                RefreshExophaseProviderGame(game);
                _ = _viewModel?.LoadAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear Exophase friend game mapping for {game.ProviderGameKey}.");
            }
        }

        private void RefreshExophaseProviderGame(FriendGameSummaryItem game)
        {
            if (game == null)
            {
                return;
            }

            var refreshTarget = new FriendGameSummaryItem
            {
                ProviderKey = game.ProviderKey,
                Provider = game.Provider,
                AppId = game.AppId,
                ProviderGameKey = game.ProviderGameKey,
                GameName = game.GameName
            };
            GameRowContextMenuBuilder.ExecuteCommand(_viewModel?.RefreshFriendSelectedGameCommand, refreshTarget);
        }

        private void ClearUnownedGame(FriendGameSummaryItem game)
        {
            if (game == null)
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(game.GameName) ? game.ProviderGameKey : game.GameName;
            var message = string.Format(
                GetText("LOCPlayAch_Menu_ClearData_ConfirmSingle", "Clear cached data for {0}?"),
                name);

            var result = _playniteApi?.Dialogs?.ShowMessage(
                message,
                GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var clearResult = _friendCache?.ClearUnownedFriendGame(game.ProviderKey, game.AppId, game.ProviderGameKey);
                if (clearResult != null && !clearResult.Success)
                {
                    _logger?.Warn($"Failed to clear unowned friend game data for {game.ProviderKey}/{game.AppId}/{game.ProviderGameKey}: {clearResult.ErrorMessage}");
                }

                _viewModel?.ClearGameSelection();
                _ = _viewModel?.LoadAsync();

                _playniteApi?.Dialogs?.ShowMessage(
                    GetText("LOCPlayAch_Status_Succeeded", "Succeeded"),
                    GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear unowned friend game {game.ProviderKey}/{game.AppId}/{game.ProviderGameKey}.");
            }
        }

        private ContextMenu BuildFriendMenu(FriendSummaryItem friend)
        {
            var menu = new ContextMenu();
            var refreshCommand = _viewModel?.RefreshFriendSelectedGameCommand;
            var refreshItem = new MenuItem
            {
                Header = GetText("LOCPlayAch_Menu_RefreshFriend", "Refresh Friend"),
                IsEnabled = refreshCommand?.CanExecute(friend) == true
            };
            refreshItem.Click += (_, __) => GameRowContextMenuBuilder.ExecuteCommand(refreshCommand, friend);
            menu.Items.Add(refreshItem);
            menu.Items.Add(new Separator());

            var clearItem = new MenuItem
            {
                Header = GetText("LOCPlayAch_Menu_ClearFriend", "Clear Friend"),
                IsEnabled = IsClearableFriend(friend)
            };
            clearItem.Click += (_, __) => ClearFriend(friend);
            menu.Items.Add(clearItem);

            var ignoreItem = new MenuItem
            {
                Header = GetText("LOCPlayAch_Menu_IgnoreFriend", "Ignore Friend"),
                IsEnabled = IsConfigurableFriend(friend)
            };
            ignoreItem.Click += (_, __) => IgnoreFriend(friend);
            menu.Items.Add(ignoreItem);
            return menu;
        }

        private void ClearFriend(FriendSummaryItem friend)
        {
            if (!IsClearableFriend(friend))
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(friend.DisplayName)
                ? friend.ExternalUserId
                : friend.DisplayName;
            var message = string.Format(
                GetText(
                    "LOCPlayAch_Menu_ClearFriend_Confirm",
                    "Clear cached achievement data for {0}? It will be re-fetched on the next friend refresh."),
                name);

            var result = _playniteApi?.Dialogs?.ShowMessage(
                message,
                GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                // Clear only cached achievement/game data; keep the friend record so they stay
                // registered and re-populate on the next friend refresh.
                foreach (var account in GetConfigurableFriendAccounts(friend))
                {
                    var deleteResult = _friendCache?.DeleteFriendData(account.ProviderKey, account.ExternalUserId, preserveFriendRecord: true);
                    if (deleteResult != null && !deleteResult.Success)
                    {
                        _logger?.Warn($"Failed to clear friend data for {account.ProviderKey}/{account.ExternalUserId}: {deleteResult.ErrorMessage}");
                    }
                }

                _viewModel?.ClearFriendSelection();
                _ = _viewModel?.LoadAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear friend {friend.ProviderKey}/{friend.ExternalUserId}.");
                _playniteApi?.Dialogs?.ShowErrorMessage(
                    string.Format(GetText("LOCPlayAch_Status_Failed", "Failed: {0}"), ex.Message),
                    GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"));
            }
        }

        private static bool IsClearableFriend(FriendSummaryItem friend)
        {
            return GetConfigurableFriendAccounts(friend).Any();
        }

        private void IgnoreFriend(FriendSummaryItem friend)
        {
            if (!IsConfigurableFriend(friend))
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(friend.DisplayName)
                ? friend.ExternalUserId
                : friend.DisplayName;
            var message = string.Format(
                GetText(
                    "LOCPlayAch_Menu_IgnoreFriend_Confirm",
                    "Ignore {0}? Their cached friend achievement data will be deleted and they will be skipped during friend refreshes."),
                name);

            var result = _playniteApi?.Dialogs?.ShowMessage(
                message,
                GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBox.Show(
                message,
                GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var plugin = PlayniteAchievementsPlugin.Instance;
                var persisted = plugin?.Settings?.Persisted;
                if (plugin == null || persisted == null)
                {
                    return;
                }

                foreach (var account in GetConfigurableFriendAccounts(friend))
                {
                    var entry = persisted.AddOrUpdateFriend(
                        account.ProviderKey,
                        account.ExternalUserId,
                        friend.DisplayName,
                        friend.AvatarPath,
                        null,
                        FriendSettingsSource.AutoDiscovered);
                    if (entry != null)
                    {
                        entry.IsIgnored = true;
                    }

                    var deleteResult = _friendCache?.DeleteFriendData(account.ProviderKey, account.ExternalUserId);
                    if (deleteResult != null && !deleteResult.Success)
                    {
                        _logger?.Warn($"Failed to delete ignored friend data for {account.ProviderKey}/{account.ExternalUserId}: {deleteResult.ErrorMessage}");
                    }
                }

                FriendSettingsSyncService.SyncConfiguredFriendsToCache(persisted, _friendCache, _logger);
                plugin.PersistSettingsForUi();
                plugin.ThemeIntegrationService?.RequestUpdate(null, forceRefresh: true);
                _viewModel?.ClearFriendSelection();
                _ = _viewModel?.LoadAsync();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to ignore friend {friend.ProviderKey}/{friend.ExternalUserId}.");
                _playniteApi?.Dialogs?.ShowErrorMessage(
                    string.Format(GetText("LOCPlayAch_Status_Failed", "Failed: {0}"), ex.Message),
                    GetText("LOCPlayAch_Title_PluginName", "Playnite Achievements"));
            }
        }

        private static bool IsConfigurableFriend(FriendSummaryItem friend)
        {
            return GetConfigurableFriendAccounts(friend).Any();
        }

        private static IEnumerable<FriendAccountRef> GetConfigurableFriendAccounts(FriendSummaryItem friend)
        {
            if (friend == null)
            {
                yield break;
            }

            if (friend.IsMergedFriend)
            {
                foreach (var account in friend.MemberAccounts ?? new List<FriendAccountRef>())
                {
                    if (!string.IsNullOrWhiteSpace(account?.ProviderKey) &&
                        !string.IsNullOrWhiteSpace(account.ExternalUserId))
                    {
                        yield return account;
                    }
                }

                yield break;
            }

            if (!string.IsNullOrWhiteSpace(friend.ProviderKey) &&
                !string.IsNullOrWhiteSpace(friend.ExternalUserId))
            {
                yield return FriendAccountRef.From(friend.ProviderKey, friend.ExternalUserId);
            }
        }

        private string GetText(string resourceKey, string fallback)
        {
            return TryFindResource(resourceKey) as string
                   ?? ResourceProvider.GetString(resourceKey)
                   ?? fallback
                   ?? resourceKey;
        }

        private void QueueScrollAfterSummarySelection(object dataContext)
        {
            var action = new Action(() =>
            {
                if (dataContext is FriendSummaryItem)
                {
                    ScrollDataGridToTop(FriendGameSummariesGridControl?.InternalDataGrid);
                    ScrollDataGridToTop(SelectedFriendGameSummariesGridControl?.InternalDataGrid);
                    ScrollDataGridToTop(FriendsAchievementsGrid?.InternalDataGrid);
                }
                else if (dataContext is FriendGameSummaryItem)
                {
                    ScrollDataGridToTop(FriendSummariesGridControl?.InternalDataGrid);
                    ScrollDataGridToTop(FriendsAchievementsGrid?.InternalDataGrid);
                }
            });

            if (Dispatcher == null)
            {
                action();
                return;
            }

            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }

        private static void ScrollDataGridToTop(DataGrid grid)
        {
            if (grid == null)
            {
                return;
            }

            try
            {
                if (grid.Items.Count > 0)
                {
                    grid.ScrollIntoView(grid.Items[0]);
                }

                VisualTreeHelpers.FindVisualChild<ScrollViewer>(grid)?.ScrollToTop();
            }
            catch
            {
                // Best effort; scrolling should not interfere with row selection.
            }
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

        private static DataGrid FindParentDataGrid(DependencyObject source)
        {
            var current = source;
            while (current != null)
            {
                if (current is DataGrid grid)
                {
                    return grid;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
