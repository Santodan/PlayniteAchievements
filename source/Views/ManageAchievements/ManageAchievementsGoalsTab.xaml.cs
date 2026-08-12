using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsGoalsTab : UserControl, IFullscreenControllerNavigable
    {
        private const string DragDataFormat = "PlayniteAchievements.ManageAchievementsGoalRows";

        private bool _pendingRefreshRequested;

        public ManageAchievementsGoalsTab(ManageAchievementsGoalsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataGridRowReorderBehavior.SetOptions(GoalsDataGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is AchievementDisplayItem,
                ExtractDragKeys = items => AchievementOrderHelper.NormalizeApiNames(
                    items.OfType<AchievementDisplayItem>().Select(item => item.ApiName)),
                MoveItemsRelativeToTarget = (apiNames, target, insertAfter) =>
                    target is AchievementDisplayItem targetItem &&
                    ViewModel?.MoveItemsByApiName(apiNames, targetItem.ApiName, insertAfter) == true,
                MoveItemsToEnd = apiNames => ViewModel?.MoveItemsToEndByApiName(apiNames) == true,
                RestoreSelection = RestoreSelectionByApiNames,
                RowPressOutsideDragHandle = (item, e) =>
                {
                    if (item is AchievementDisplayItem displayItem && displayItem.CanReveal)
                    {
                        displayItem.ToggleReveal();
                        e.Handled = true;
                    }
                },
                DragCompleted = ApplyPendingRefreshIfNeeded
            });
        }

        private ManageAchievementsGoalsViewModel ViewModel => DataContext as ManageAchievementsGoalsViewModel;

        public void RefreshData()
        {
            if (ViewModel == null)
            {
                return;
            }

            if (DataGridRowReorderBehavior.GetIsDragging(GoalsDataGrid))
            {
                _pendingRefreshRequested = true;
                return;
            }

            RefreshDataCore(CaptureSelectedApiNames());
        }

        private void ClearGoalsButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.ClearGoals() != true)
            {
                return;
            }

            DataGridRowReorderBehavior.CancelPendingDrag(GoalsDataGrid);
        }

        private void RemoveGoalButton_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is AchievementDisplayItem item))
            {
                return;
            }

            if (ViewModel?.RemoveGoal(item.ApiName) == true)
            {
                DataGridRowReorderBehavior.CancelPendingDrag(GoalsDataGrid);
            }
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (GoalsDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(GoalsDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(GoalsDataGrid);
                }

                return false;
            }

            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return OpenControllerGoalMenu();
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            return new UIElement[]
                {
                    ClearGoalsButton,
                    GoalsDataGrid
                }
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        private bool OpenControllerGoalMenu()
        {
            var row = FullscreenControllerNavigationService.GetTargetDataGridRow(GoalsDataGrid);
            if (!(row?.DataContext is AchievementDisplayItem item))
            {
                return false;
            }

            var menu = new ContextMenu();
            menu.Items.Add(CreateGoalMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveUp"), () => MoveControllerSelection(item, -1)));
            menu.Items.Add(CreateGoalMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveDown"), () => MoveControllerSelection(item, 1)));
            menu.Items.Add(CreateGoalMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveToTop"), () => MoveControllerSelectionToEdge(item, toTop: true)));
            menu.Items.Add(CreateGoalMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveToBottom"), () => MoveControllerSelectionToEdge(item, toTop: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateGoalMenuItem(ResourceProvider.GetString("LOCPlayAch_Button_Remove"), () => ViewModel?.RemoveGoal(item.ApiName)));

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            return FullscreenControllerNavigationService.OpenContextMenu(row, menu);
        }

        private MenuItem CreateGoalMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => action?.Invoke();
            return item;
        }

        private void MoveControllerSelection(AchievementDisplayItem focusedItem, int delta)
        {
            var apiNames = CaptureControllerSelectedApiNames(focusedItem);
            if (apiNames.Count == 0 || ViewModel?.GoalRows == null)
            {
                return;
            }

            var indexes = ResolveIndexes(apiNames);
            if (indexes.Count == 0)
            {
                return;
            }

            var targetIndex = delta < 0 ? indexes.Min() - 1 : indexes.Max() + 1;
            if (targetIndex < 0 || targetIndex >= ViewModel.GoalRows.Count)
            {
                return;
            }

            var target = ViewModel.GoalRows[targetIndex];
            if (target == null || string.IsNullOrWhiteSpace(target.ApiName))
            {
                return;
            }

            var moved = ViewModel.MoveItemsByApiName(apiNames, target.ApiName, insertAfterTarget: delta > 0);
            if (moved)
            {
                RestoreSelectionByApiNames(apiNames);
                FocusRowByApiName(focusedItem?.ApiName ?? apiNames.FirstOrDefault());
            }
        }

        private void MoveControllerSelectionToEdge(AchievementDisplayItem focusedItem, bool toTop)
        {
            var apiNames = CaptureControllerSelectedApiNames(focusedItem);
            if (apiNames.Count == 0 || ViewModel?.GoalRows == null || ViewModel.GoalRows.Count == 0)
            {
                return;
            }

            bool moved;
            if (toTop)
            {
                var first = ViewModel.GoalRows.FirstOrDefault();
                moved = first != null &&
                        ViewModel.MoveItemsByApiName(apiNames, first.ApiName, insertAfterTarget: false);
            }
            else
            {
                moved = ViewModel.MoveItemsToEndByApiName(apiNames);
            }

            if (moved)
            {
                RestoreSelectionByApiNames(apiNames);
                FocusRowByApiName(focusedItem?.ApiName ?? apiNames.FirstOrDefault());
            }
        }

        private List<string> CaptureControllerSelectedApiNames(AchievementDisplayItem focusedItem)
        {
            var selected = CaptureSelectedApiNames();
            if (selected.Count > 0)
            {
                return selected;
            }

            return string.IsNullOrWhiteSpace(focusedItem?.ApiName)
                ? new List<string>()
                : new List<string> { focusedItem.ApiName };
        }

        private List<int> ResolveIndexes(IReadOnlyList<string> apiNames)
        {
            if (apiNames == null || ViewModel?.GoalRows == null)
            {
                return new List<int>();
            }

            var normalized = new HashSet<string>(
                AchievementOrderHelper.NormalizeApiNames(apiNames),
                StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();
            for (var i = 0; i < ViewModel.GoalRows.Count; i++)
            {
                var apiName = (ViewModel.GoalRows[i]?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && normalized.Contains(apiName))
                {
                    indexes.Add(i);
                }
            }

            return indexes;
        }

        private void FocusRowByApiName(string apiName)
        {
            if (string.IsNullOrWhiteSpace(apiName) ||
                ViewModel?.GoalRows == null ||
                GoalsDataGrid == null)
            {
                return;
            }

            var index = ViewModel.GoalRows.ToList().FindIndex(item =>
                string.Equals((item?.ApiName ?? string.Empty).Trim(), apiName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                GoalsDataGrid.SelectedIndex = index;
                GoalsDataGrid.ScrollIntoView(GoalsDataGrid.Items[index]);
                var row = GoalsDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as UIElement;
                if (row != null)
                {
                    FullscreenControllerNavigationService.FocusElement(row);
                }
                else
                {
                    FullscreenControllerNavigationService.FocusElement(GoalsDataGrid);
                }
            }
        }

        private void RefreshDataCore(IReadOnlyList<string> selectedApiNames)
        {
            DataGridRowReorderBehavior.CancelPendingDrag(GoalsDataGrid);
            ViewModel?.ReloadData();
            RestoreSelectionByApiNames(selectedApiNames);
        }

        private List<string> CaptureSelectedApiNames()
        {
            var selectedItems = GoalsDataGrid?.SelectedItems;
            if (selectedItems == null)
            {
                return new List<string>();
            }

            return AchievementOrderHelper.NormalizeApiNames(
                selectedItems
                    .OfType<AchievementDisplayItem>()
                    .Select(item => item.ApiName));
        }

        private void RestoreSelectionByApiNames(IEnumerable<string> apiNames)
        {
            if (GoalsDataGrid == null)
            {
                return;
            }

            var selectedApiNames = new HashSet<string>(
                AchievementOrderHelper.NormalizeApiNames(apiNames),
                StringComparer.OrdinalIgnoreCase);

            GoalsDataGrid.SelectedItems.Clear();
            if (selectedApiNames.Count == 0 || ViewModel?.GoalRows == null)
            {
                return;
            }

            foreach (var row in ViewModel.GoalRows)
            {
                var apiName = (row?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && selectedApiNames.Contains(apiName))
                {
                    GoalsDataGrid.SelectedItems.Add(row);
                }
            }
        }

        private void ApplyPendingRefreshIfNeeded()
        {
            if (!_pendingRefreshRequested)
            {
                return;
            }

            _pendingRefreshRequested = false;
            RefreshDataCore(CaptureSelectedApiNames());
        }
    }
}
