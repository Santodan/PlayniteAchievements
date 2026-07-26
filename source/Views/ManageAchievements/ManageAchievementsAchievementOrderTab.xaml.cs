using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsAchievementOrderTab : UserControl, IFullscreenControllerNavigable
    {
        private const string DragDataFormat = "PlayniteAchievements.ManageAchievementsAchievementOrderRows";

        private bool _pendingRefreshRequested;

        public ManageAchievementsAchievementOrderTab(ManageAchievementsAchievementOrderViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            DataGridRowReorderBehavior.SetOptions(AchievementOrderDataGrid, new DataGridRowReorderOptions
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

        private ManageAchievementsAchievementOrderViewModel ViewModel => DataContext as ManageAchievementsAchievementOrderViewModel;

        public void RefreshData()
        {
            if (ViewModel == null)
            {
                return;
            }

            if (DataGridRowReorderBehavior.GetIsDragging(AchievementOrderDataGrid))
            {
                _pendingRefreshRequested = true;
                return;
            }

            RefreshDataCore(CaptureSelectedApiNames());
        }

        private void ResetOrderButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.ResetCustomOrder() != true)
            {
                return;
            }

            DataGridRowReorderBehavior.CancelPendingDrag(AchievementOrderDataGrid);
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (AchievementOrderDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(AchievementOrderDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(AchievementOrderDataGrid);
                }

                return false;
            }

            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return OpenControllerOrderMenu();
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            return new UIElement[]
                {
                    ResetOrderButton,
                    AchievementOrderDataGrid
                }
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        private bool OpenControllerOrderMenu()
        {
            var row = FullscreenControllerNavigationService.GetTargetDataGridRow(AchievementOrderDataGrid);
            if (!(row?.DataContext is AchievementDisplayItem item))
            {
                return false;
            }

            var menu = new ContextMenu();
            menu.Items.Add(CreateOrderMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveUp"), () => MoveControllerSelection(item, -1)));
            menu.Items.Add(CreateOrderMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveDown"), () => MoveControllerSelection(item, 1)));
            menu.Items.Add(CreateOrderMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveToTop"), () => MoveControllerSelectionToEdge(item, toTop: true)));
            menu.Items.Add(CreateOrderMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_MoveToBottom"), () => MoveControllerSelectionToEdge(item, toTop: false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateOrderMenuItem(ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Order_Reset"), () =>
            {
                ViewModel?.ResetCustomOrder();
                FocusRowByApiName(item.ApiName);
            }));

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            return FullscreenControllerNavigationService.OpenContextMenu(row, menu);
        }

        private MenuItem CreateOrderMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => action?.Invoke();
            return item;
        }

        private void MoveControllerSelection(AchievementDisplayItem focusedItem, int delta)
        {
            var apiNames = CaptureControllerSelectedApiNames(focusedItem);
            if (apiNames.Count == 0 || ViewModel?.AchievementRows == null)
            {
                return;
            }

            var indexes = ResolveIndexes(apiNames);
            if (indexes.Count == 0)
            {
                return;
            }

            var targetIndex = delta < 0 ? indexes.Min() - 1 : indexes.Max() + 1;
            if (targetIndex < 0 || targetIndex >= ViewModel.AchievementRows.Count)
            {
                return;
            }

            var target = ViewModel.AchievementRows[targetIndex];
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
            if (apiNames.Count == 0 || ViewModel?.AchievementRows == null || ViewModel.AchievementRows.Count == 0)
            {
                return;
            }

            bool moved;
            if (toTop)
            {
                var first = ViewModel.AchievementRows.FirstOrDefault();
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
            if (apiNames == null || ViewModel?.AchievementRows == null)
            {
                return new List<int>();
            }

            var normalized = new HashSet<string>(
                AchievementOrderHelper.NormalizeApiNames(apiNames),
                StringComparer.OrdinalIgnoreCase);
            var indexes = new List<int>();
            for (var i = 0; i < ViewModel.AchievementRows.Count; i++)
            {
                var apiName = (ViewModel.AchievementRows[i]?.ApiName ?? string.Empty).Trim();
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
                ViewModel?.AchievementRows == null ||
                AchievementOrderDataGrid == null)
            {
                return;
            }

            var index = ViewModel.AchievementRows.ToList().FindIndex(item =>
                string.Equals((item?.ApiName ?? string.Empty).Trim(), apiName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                AchievementOrderDataGrid.SelectedIndex = index;
                AchievementOrderDataGrid.ScrollIntoView(AchievementOrderDataGrid.Items[index]);
                var row = AchievementOrderDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as UIElement;
                if (row != null)
                {
                    FullscreenControllerNavigationService.FocusElement(row);
                }
                else
                {
                    FullscreenControllerNavigationService.FocusElement(AchievementOrderDataGrid);
                }
            }
        }

        private void RefreshDataCore(IReadOnlyList<string> selectedApiNames)
        {
            DataGridRowReorderBehavior.CancelPendingDrag(AchievementOrderDataGrid);
            ViewModel?.ReloadData();
            RestoreSelectionByApiNames(selectedApiNames);
        }

        private List<string> CaptureSelectedApiNames()
        {
            var selectedItems = AchievementOrderDataGrid?.SelectedItems;
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
            if (AchievementOrderDataGrid == null)
            {
                return;
            }

            var selectedApiNames = new HashSet<string>(
                AchievementOrderHelper.NormalizeApiNames(apiNames),
                StringComparer.OrdinalIgnoreCase);

            AchievementOrderDataGrid.SelectedItems.Clear();
            if (selectedApiNames.Count == 0 || ViewModel?.AchievementRows == null)
            {
                return;
            }

            foreach (var row in ViewModel.AchievementRows)
            {
                var apiName = (row?.ApiName ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(apiName) && selectedApiNames.Contains(apiName))
                {
                    AchievementOrderDataGrid.SelectedItems.Add(row);
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
