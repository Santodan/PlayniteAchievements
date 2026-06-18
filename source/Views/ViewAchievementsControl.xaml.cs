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
        private DataGridRow _pendingRightClickRow;

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
            PlayniteAchievementsSettings settings)
        {
            InitializeComponent();

            _settings = settings;
            _logger = logger;
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
            var focusedButton = FullscreenControllerNavigationService.FindAncestor<Button>(
                                    Keyboard.FocusedElement as DependencyObject)
                                ?? Keyboard.FocusedElement as Button;
            if (focusedButton == null)
            {
                return false;
            }

            if (ReferenceEquals(focusedButton, CategoryTypeFilterSelectionButton))
            {
                CategoryTypeFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return CategoryTypeFilterSelectionContextMenu?.IsOpen == true;
            }

            if (ReferenceEquals(focusedButton, CategoryLabelFilterSelectionButton))
            {
                CategoryLabelFilterSelectionButton_Click(focusedButton, new RoutedEventArgs());
                return CategoryLabelFilterSelectionContextMenu?.IsOpen == true;
            }

            return false;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearSearch();
        }

        private void OpenLocalEditor_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            PlayniteAchievementsPlugin.Instance?.OpenLocalAchievementsEditorView(ViewModel.GameId);
        }

        private void CategoryTypeFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CategoryTypeFilterSelectionButton,
                ViewModel.CategoryTypeFilterOptions,
                option => ViewModel.IsCategoryTypeFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryTypeFilterSelected(option, isSelected),
                AchievementCategoryTypeHelper.ToCategoryTypeDisplayText);
        }

        private void CategoryLabelFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CategoryLabelFilterSelectionButton,
                ViewModel.CategoryLabelFilterOptions,
                option => ViewModel.IsCategoryLabelFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryLabelFilterSelected(option, isSelected),
                AchievementCategoryTypeHelper.ToCategoryLabelDisplayText);
        }

        private void OpenMultiSelectFilterContextMenu(
            Button button,
            System.Collections.Generic.IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection,
            Func<string, string> displayText = null)
        {
            if (button == null || isSelected == null || setSelection == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            if (options == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

                var item = new MenuItem
                {
                    Header = displayText?.Invoke(option) ?? option,
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = isSelected(option)
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }
                item.Click += (_, __) => setSelection(option, item.IsChecked);
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu)
        {
            if (button == null || menu == null)
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
            if (button.IsKeyboardFocusWithin)
            {
                FullscreenControllerNavigationService.OpenContextMenu(button, menu);
            }
            else
            {
                menu.IsOpen = true;
            }
        }
    }
}


