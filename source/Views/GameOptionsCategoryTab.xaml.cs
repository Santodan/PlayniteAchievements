using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class GameOptionsCategoryTab : UserControl, IFullscreenControllerNavigable
    {
        private DataGridRow _pendingRightClickRow;

        public GameOptionsCategoryTab(GameOptionsCategoryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private GameOptionsCategoryViewModel ViewModel => DataContext as GameOptionsCategoryViewModel;

        private void ApplyBulkButton_Click(object sender, RoutedEventArgs e)
        {
            ApplyBulk();
        }

        private void ClearSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            ClearSelected();
        }

        private void TypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || TypeSelectionContextMenu == null || TypeSelectionButton == null)
            {
                return;
            }

            OpenCategoryTypeContextMenu(
                TypeSelectionButton,
                TypeSelectionContextMenu,
                ViewModel.TypeSelectionOptions);
        }

        private void FilterTypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || FilterTypeSelectionContextMenu == null || FilterTypeSelectionButton == null)
            {
                return;
            }

            OpenCategoryTypeContextMenu(
                FilterTypeSelectionButton,
                FilterTypeSelectionContextMenu,
                ViewModel.TypeFilterOptions);
        }

        private void CategoryLabelFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null ||
                CategoryLabelFilterSelectionButton == null ||
                CategoryLabelFilterSelectionContextMenu == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CategoryLabelFilterSelectionButton,
                CategoryLabelFilterSelectionContextMenu,
                ViewModel.CategoryLabelFilterOptions,
                option => ViewModel.IsCategoryLabelFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryLabelFilterSelected(option, isSelected));
        }

        private void RenameCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            OpenRenameCategoryDialog(null);
        }

        private void CategoryInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ApplyBulk();
                e.Handled = true;
            }
        }

        private void ApplyBulk()
        {
            if (ViewModel == null)
            {
                return;
            }

            var selectedRows = ViewModel.GetAllSelectedRows();
            if (selectedRows.Count == 0)
            {
                return;
            }

            var applied = ViewModel.ApplyBulkToSelection(selectedRows, CategoryInputTextBox.Text);
            if (applied)
            {
                CategoryInputTextBox.Text = string.Empty;
                ViewModel.ResetBulkEditorInputs();
                ViewModel.ClearAllSelections();
            }
        }

        private void ClearSelected()
        {
            if (ViewModel == null)
            {
                return;
            }

            var selectedRows = ViewModel.GetAllSelectedRows();
            if (selectedRows.Count == 0)
            {
                return;
            }

            var cleared = ViewModel.ClearSelectionOverrides(selectedRows);
            if (cleared)
            {
                CategoryInputTextBox.Text = string.Empty;
                ViewModel.ResetBulkEditorInputs();
            }
        }

        private void OpenRenameCategoryDialog(GameOptionsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return;
            }

            var labels = ViewModel.CategoryLabelOptions
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .OrderBy(label => label, System.StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (labels.Count == 0)
            {
                API.Instance.Dialogs.ShowMessage(
                    L(
                        "LOCPlayAch_GameOptions_Category_RenameDialog_NoLabels",
                        "No category labels are available to rename."),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var preferredSource = contextItem?.CategoryDisplay;
            var dialog = new RenameCategoryLabelDialog(labels, preferredSource);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L(
                    "LOCPlayAch_GameOptions_Category_RenameDialog_Title",
                    "Rename Category Label"),
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 520,
                    Height = 260
                });

            dialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult != true)
            {
                return;
            }

            ViewModel.RenameCategoryLabel(
                dialog.SelectedSourceLabel,
                dialog.TargetLabel);
        }

        private void CategoryDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (source == null)
            {
                return;
            }

            if (source is CheckBox || VisualTreeHelpers.FindVisualParent<CheckBox>(source) != null)
            {
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(source);
            if (!(row?.DataContext is GameOptionsCategoryItem item))
            {
                return;
            }

            if (item.CanReveal)
            {
                item.ToggleReveal();
            }

            // Selection is controlled by the checkbox column only.
            e.Handled = true;
        }

        private void CategoryDataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void CategoryDataGridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                OpenContextMenuForRow(targetRow);
            }
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (CategoryDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(CategoryDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(CategoryDataGrid);
                }

                return false;
            }

            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return TryOpenSelectedRowContextMenu();
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                ResetCategoryButton,
                SearchTextBox,
                ClearSearchButton,
                FilterTypeSelectionButton,
                CategoryLabelFilterSelectionButton,
                SelectAllButton,
                DeselectAllButton,
                CategoryDataGrid,
                BulkEditExpander
            };

            if (BulkEditExpander?.IsExpanded == true)
            {
                elements.Add(TypeSelectionButton);
                elements.Add(ClearSelectedButton);
                elements.Add(CategoryInputTextBox);
                elements.Add(ApplyBulkButton);
                elements.Add(RenameCategoryButton);
            }

            return elements
                .Where(IsControllerElementAvailable)
                .ToList();
        }

        private static bool IsControllerElementAvailable(UIElement element)
        {
            if (element == null || !element.IsVisible || !element.IsEnabled)
            {
                return false;
            }

            if (element is Button button &&
                ReferenceEquals(button.Style, button.TryFindResource("ClearSearchButtonStyle")))
            {
                return !string.IsNullOrEmpty(button.Tag as string);
            }

            return true;
        }

        private bool TryActivateSelectedRow()
        {
            if (FullscreenControllerNavigationService.FindAncestor<ButtonBase>(
                    Keyboard.FocusedElement as DependencyObject) != null)
            {
                return false;
            }

            var item = CategoryDataGrid?.SelectedItem as GameOptionsCategoryItem
                       ?? CategoryDataGrid?.CurrentItem as GameOptionsCategoryItem;
            if (item == null || !item.CanReveal)
            {
                return false;
            }

            item.ToggleReveal();
            return true;
        }

        private bool TryOpenSelectedRowContextMenu()
        {
            var row = GetControllerTargetRow();
            if (row == null)
            {
                return false;
            }

            return OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        private DataGridRow GetControllerTargetRow()
        {
            var focusedRow = VisualTreeHelpers.FindVisualParent<DataGridRow>(
                Keyboard.FocusedElement as DependencyObject);
            if (focusedRow != null &&
                ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedRow), CategoryDataGrid))
            {
                return focusedRow;
            }

            var index = CategoryDataGrid?.SelectedIndex ?? -1;
            if (index < 0 && CategoryDataGrid?.Items.Count > 0)
            {
                index = 0;
                CategoryDataGrid.SelectedIndex = index;
            }

            if (CategoryDataGrid == null || index < 0)
            {
                return null;
            }

            CategoryDataGrid.UpdateLayout();
            var row = CategoryDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            if (row == null)
            {
                CategoryDataGrid.ScrollIntoView(CategoryDataGrid.Items[index]);
                CategoryDataGrid.UpdateLayout();
                row = CategoryDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            }

            return row;
        }

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (!(row?.DataContext is GameOptionsCategoryItem item))
            {
                return false;
            }

            var menu = BuildRowContextMenu(item);
            if (menu == null || menu.Items.Count == 0)
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

        private ContextMenu BuildRowContextMenu(GameOptionsCategoryItem contextItem)
        {
            var menu = new ContextMenu();

            var addTypeMenu = new MenuItem
            {
                Header = L("LOCPlayAch_Common_AddType", "Add Type")
            };
            foreach (var categoryType in AchievementCategoryTypeHelper.AllowedCategoryTypes.Where(type =>
                         !string.Equals(type, AchievementCategoryTypeHelper.DefaultCategoryType, StringComparison.OrdinalIgnoreCase)))
            {
                var capturedType = categoryType;
                addTypeMenu.Items.Add(CreateMenuItem(
                    GameOptionsCategoryViewModel.GetCategoryTypeDisplayName(capturedType),
                    () => AddTypeFromContext(contextItem, capturedType)));
            }
            menu.Items.Add(addTypeMenu);

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Common_SetLabelEllipsis", "Set Label..."),
                () => SetLabelFromContext(contextItem)));

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Button_Clear", "Clear"),
                () => ClearRowsFromContext(contextItem)));

            return menu;
        }

        private MenuItem CreateMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private void AddTypeFromContext(GameOptionsCategoryItem contextItem, string categoryType)
        {
            if (ViewModel == null)
            {
                return;
            }

            var rows = ResolveActionRows(contextItem);
            if (rows.Count == 0)
            {
                return;
            }

            ViewModel.AddCategoryTypesToSelection(rows, new[] { categoryType });
        }

        private void SetLabelFromContext(GameOptionsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return;
            }

            var rows = ResolveActionRows(contextItem);
            if (rows.Count == 0)
            {
                return;
            }

            var inputText = contextItem?.CategoryDisplay ?? string.Empty;
            var inputDialog = new TextInputDialog(
                L(
                    "LOCPlayAch_GameOptions_Category_Context_SetLabelHint",
                    "Enter a category label for the selected achievements."),
                inputText);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L(
                    "LOCPlayAch_GameOptions_Category_Context_SetLabelTitle",
                    "Set Category Label"),
                inputDialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 500,
                    Height = 200
                });

            inputDialog.RequestClose += (s, e) => window.Close();
            window.ShowDialog();

            if (inputDialog.DialogResult != true)
            {
                return;
            }

            inputText = (inputDialog.InputText ?? string.Empty).Trim();
            ViewModel.SetCategoryLabelForSelection(rows, inputText);
        }

        private void ClearRowsFromContext(GameOptionsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return;
            }

            var rows = ResolveActionRows(contextItem);
            if (rows.Count == 0)
            {
                return;
            }

            var cleared = ViewModel.ClearSelectionOverrides(rows);
            if (cleared)
            {
                CategoryInputTextBox.Text = string.Empty;
                ViewModel.ResetBulkEditorInputs();
            }
        }

        private List<GameOptionsCategoryItem> ResolveActionRows(GameOptionsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return new List<GameOptionsCategoryItem>();
            }

            var selectedRows = ViewModel.GetAllSelectedRows();
            if (selectedRows.Count == 0)
            {
                return contextItem == null
                    ? new List<GameOptionsCategoryItem>()
                    : new List<GameOptionsCategoryItem> { contextItem };
            }

            if (contextItem == null || contextItem.IsSelected)
            {
                return selectedRows;
            }

            return new List<GameOptionsCategoryItem> { contextItem };
        }

        private void RowSelectionCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is GameOptionsCategoryItem item)
            {
                item.IsSelected = checkBox.IsChecked == true;
            }

            e.Handled = true;
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllSelectableRows(selected: true);
            e.Handled = true;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            SetAllSelectableRows(selected: false);
            e.Handled = true;
        }

        private void ResetCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            var reset = ViewModel.ResetCategoryOverrides();
            if (reset)
            {
                CategoryInputTextBox.Text = string.Empty;
                ViewModel.ResetBulkEditorInputs();
            }

            e.Handled = true;
        }

        private void SetAllSelectableRows(bool selected)
        {
            if (ViewModel == null)
            {
                return;
            }

            foreach (var item in ViewModel.AchievementRows.Where(i => i != null))
            {
                item.IsSelected = selected;
            }
        }

        private static string L(string key, string fallback)
        {
            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
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

        private static void OpenCategoryTypeContextMenu(
            Button button,
            ContextMenu menu,
            IEnumerable<CategoryTypeSelectionOption> options)
        {
            if (button == null || menu == null)
            {
                return;
            }

            menu.Items.Clear();

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options ?? Enumerable.Empty<CategoryTypeSelectionOption>())
            {
                if (option == null)
                {
                    continue;
                }

                var item = new MenuItem
                {
                    Header = option.DisplayName,
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = option.IsSelected
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                item.Click += (_, __) => option.IsSelected = item.IsChecked;
                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
        }

        private static void OpenMultiSelectFilterContextMenu(
            Button button,
            ContextMenu menu,
            IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection)
        {
            if (button == null || menu == null || isSelected == null || setSelection == null)
            {
                return;
            }

            menu.Items.Clear();
            if (options == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                var item = new MenuItem
                {
                    Header = option,
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
    }
}
