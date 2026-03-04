using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class GameOptionsCategoryTab : UserControl
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
            if (TypeSelectionContextMenu == null || TypeSelectionButton == null)
            {
                return;
            }

            TypeSelectionContextMenu.PlacementTarget = TypeSelectionButton;
            TypeSelectionContextMenu.IsOpen = true;
        }

        private void FilterTypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (FilterTypeSelectionContextMenu == null || FilterTypeSelectionButton == null)
            {
                return;
            }

            FilterTypeSelectionContextMenu.PlacementTarget = FilterTypeSelectionButton;
            FilterTypeSelectionContextMenu.IsOpen = true;
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

            var selectedRows = ViewModel.AchievementRows
                .Where(item => item != null && item.IsSelected)
                .ToList();
            if (selectedRows.Count == 0)
            {
                return;
            }

            var applied = ViewModel.ApplyBulkToSelection(selectedRows, CategoryInputTextBox.Text);
            if (applied)
            {
                CategoryInputTextBox.Text = string.Empty;
                ViewModel.ResetBulkEditorInputs();
            }
            else
            {
                ShowStatusMessageIfAny();
            }
        }

        private void ClearSelected()
        {
            if (ViewModel == null)
            {
                return;
            }

            var selectedRows = ViewModel.AchievementRows
                .Where(item => item != null && item.IsSelected)
                .ToList();
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
            else
            {
                ShowStatusMessageIfAny();
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

            var renamed = ViewModel.RenameCategoryLabel(
                dialog.SelectedSourceLabel,
                dialog.TargetLabel);
            if (!renamed)
            {
                ShowStatusMessageIfAny();
            }
        }

        private void ShowStatusMessageIfAny()
        {
            if (ViewModel == null || string.IsNullOrWhiteSpace(ViewModel.StatusMessage))
            {
                return;
            }

            API.Instance.Dialogs.ShowMessage(
                ViewModel.StatusMessage,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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

        private void OpenContextMenuForRow(DataGridRow row)
        {
            if (!(row?.DataContext is GameOptionsCategoryItem item))
            {
                return;
            }

            var menu = BuildRowContextMenu(item);
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
        }

        private ContextMenu BuildRowContextMenu(GameOptionsCategoryItem contextItem)
        {
            var menu = new ContextMenu();

            var addTypeMenu = new MenuItem
            {
                Header = L("LOCPlayAch_GameOptions_Category_Context_AddType", "Add Type")
            };
            addTypeMenu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_GameOptions_Category_Type_Base", "Base"),
                () => AddTypeFromContext(contextItem, "Base")));
            addTypeMenu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_GameOptions_Category_Type_DLC", "DLC"),
                () => AddTypeFromContext(contextItem, "DLC")));
            addTypeMenu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_GameOptions_Category_Type_Singleplayer", "Singleplayer"),
                () => AddTypeFromContext(contextItem, "Singleplayer")));
            addTypeMenu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_GameOptions_Category_Type_Multiplayer", "Multiplayer"),
                () => AddTypeFromContext(contextItem, "Multiplayer")));
            menu.Items.Add(addTypeMenu);

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_GameOptions_Category_Context_SetLabel", "Set Label..."),
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

            var applied = ViewModel.AddCategoryTypesToSelection(rows, new[] { categoryType });
            if (!applied)
            {
                ShowStatusMessageIfAny();
            }
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
            while (true)
            {
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
                if (string.IsNullOrWhiteSpace(inputText))
                {
                    API.Instance.Dialogs.ShowMessage(
                        L("LOCPlayAch_GameOptions_Category_Bulk_Status_NoLabelInput", "Enter a category label."),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    continue;
                }

                var applied = ViewModel.SetCategoryLabelForSelection(rows, inputText);
                if (!applied)
                {
                    ShowStatusMessageIfAny();
                }

                return;
            }
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
            else
            {
                ShowStatusMessageIfAny();
            }
        }

        private List<GameOptionsCategoryItem> ResolveActionRows(GameOptionsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return new List<GameOptionsCategoryItem>();
            }

            var selectedRows = ViewModel.AchievementRows
                .Where(item => item != null && item.IsSelected)
                .ToList();
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
    }
}
