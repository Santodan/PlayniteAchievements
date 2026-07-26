using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Dialogs;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsCategoryTab : UserControl, IFullscreenControllerNavigable
    {
        private const string CategoryDragDataFormat = "PlayniteAchievements.ManageAchievementsCategoryRows";
        private static readonly Regex HttpUrlRegex = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] SupportedImageExtensions =
        {
            ".png",
            ".jpg",
            ".jpeg",
            ".bmp",
            ".gif",
            ".tif",
            ".tiff"
        };

        private DataGridRow _pendingRightClickRow;

        public ManageAchievementsCategoryTab(ManageAchievementsCategoryViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // Live sorting repositions only the rows whose sorted-column value changed (via a
            // Move, not a Reset), and does nothing while no column sort is active. This keeps
            // in-place category edits flicker-free yet still honors an active sort.
            EnableLiveSorting(viewModel.AchievementRows);
            DataGridRowReorderBehavior.SetOptions(CategoryManagerDataGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = CategoryDragDataFormat,
                DropIndicator = CategoryDropInsertLine,
                DragCountPopup = CategoryDragCountPopup,
                DragCountText = CategoryDragCountText,
                IsReorderableItem = item => item is ManageAchievementsCategoryMetadataItem,
                ExtractDragKeys = items => items
                    .OfType<ManageAchievementsCategoryMetadataItem>()
                    .Select(item => item.CategoryLabel)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .ToList(),
                MoveItemsRelativeToTarget = (labels, target, insertAfter) =>
                    target is ManageAchievementsCategoryMetadataItem targetItem &&
                    ViewModel?.MoveCategoryRowsByLabel(labels, targetItem.CategoryLabel, insertAfter) == true,
                MoveItemsToEnd = labels => ViewModel?.MoveCategoryRowsToEndByLabel(labels) == true,
                RestoreSelection = RestoreCategoryManagerSelectionByLabels
            });
        }

        private ManageAchievementsCategoryViewModel ViewModel => DataContext as ManageAchievementsCategoryViewModel;

        internal void SelectManageCategoriesSubTab()
        {
            if (CategorySubTabs != null)
            {
                CategorySubTabs.SelectedIndex = 1;
            }
        }

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
                (option, isSelected) => ViewModel.SetCategoryLabelFilterSelected(option, isSelected),
                AchievementCategoryTypeHelper.ToCategoryLabelDisplayText);
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

        private void CategoryRenameTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyCategoryRenameOverride(sender as TextBox);
        }

        private void CategoryRenameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            var textBox = sender as TextBox;
            ApplyCategoryRenameOverride(textBox);
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void ApplyCategoryRenameOverride(TextBox textBox)
        {
            if (ViewModel == null || !(textBox?.DataContext is ManageAchievementsCategoryMetadataItem row))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            ViewModel.ApplyCategoryRenameOverride(row);
        }

        private async void BrowseCategoryImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == true)
            {
                await ViewModel.ApplyCategoryLocalFileOverrideAsync(row, dialog.FileName);
            }
        }

        private void CategoryImageTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void ClearCategoryImageButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            row.ClearOverride();
            e.Handled = true;
        }

        // The radio is bound OneWay with VM-enforced exclusivity, so the toggle here is the
        // single writer; handling the tunneling event also allows click-again-to-clear, which
        // a RadioButton's own click cannot do.
        private void SummaryCategoryRadioButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            row.IsSummarySelected = !row.IsSummarySelected;
            e.Handled = true;
        }

        private void SummaryCategoryRadioButton_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space ||
                !TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            row.IsSummarySelected = !row.IsSummarySelected;
            e.Handled = true;
        }

        private void CategoryImageTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = TryGetFirstImageFilePath(e.Data, out _) || TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private async void CategoryImageTextBox_Drop(object sender, DragEventArgs e)
        {
            if (!TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            try
            {
                if (TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    e.Handled = true;
                    await ViewModel.ApplyCategoryLocalFileOverrideAsync(row, imagePath);
                    return;
                }

                if (TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    e.Handled = true;
                    row.SetOverrideValue(url);
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private static bool TryResolveCategoryImageRow(
            FrameworkElement element,
            out ManageAchievementsCategoryMetadataItem row)
        {
            row = element?.DataContext as ManageAchievementsCategoryMetadataItem;
            return row != null;
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
                // Toggle bulk-selection ourselves and consume the event so the DataGrid never runs
                // native cell selection / BringIntoView, which scrolls (and oscillates) when the
                // clicked row is only partially visible at the viewport edge.
                if (VisualTreeHelpers.FindVisualParent<DataGridRow>(source)?.DataContext
                        is ManageAchievementsCategoryItem checkItem)
                {
                    checkItem.IsSelected = !checkItem.IsSelected;
                }

                e.Handled = true;
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(source);
            if (!(row?.DataContext is ManageAchievementsCategoryItem item))
            {
                return;
            }

            ViewModel?.ToggleReveal(item);

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
            if (CategoryManagerDataGrid?.IsKeyboardFocusWithin == true)
            {
                if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(CategoryManagerDataGrid))
                {
                    if (FullscreenControllerNavigationService.IsAcceptInput(input))
                    {
                        return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(CategoryManagerDataGrid);
                    }

                    return false;
                }
            }

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
                CategorySubTabs,
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
            }

            if (CategorySubTabs?.SelectedIndex == 1)
            {
                elements.Add(ResetCategoryMetadataButton);
                elements.Add(OpenCategoryImagesFolderButton);
                elements.Add(CategoryManagerDataGrid);
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

            var item = CategoryDataGrid?.SelectedItem as ManageAchievementsCategoryItem
                       ?? CategoryDataGrid?.CurrentItem as ManageAchievementsCategoryItem;
            if (item == null || !item.CanReveal)
            {
                return false;
            }

            ViewModel?.ToggleReveal(item);
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
            if (!(row?.DataContext is ManageAchievementsCategoryItem item))
            {
                return false;
            }

            var menu = BuildRowContextMenu(item);
            if (menu == null || menu.Items.Count == 0)
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

        private ContextMenu BuildRowContextMenu(ManageAchievementsCategoryItem contextItem)
        {
            var menu = new ContextMenu();

            var rows = ResolveActionRows(contextItem);
            var typesMenu = new MenuItem
            {
                Header = L("LOCPlayAch_Common_Label_Type")
            };
            foreach (var categoryType in AchievementCategoryTypeHelper.AssignableCategoryTypes)
            {
                var capturedType = categoryType;
                var typeItem = new MenuItem
                {
                    Header = ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(capturedType),
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = IsCategoryTypeOnAllRows(rows, capturedType)
                };
                typeItem.Click += (_, __) =>
                    ViewModel?.SetCategoryTypeForSelection(rows, capturedType, typeItem.IsChecked);
                typesMenu.Items.Add(typeItem);
            }
            menu.Items.Add(typesMenu);

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Common_SetLabelEllipsis"),
                () => SetLabelFromContext(contextItem)));

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Button_Clear"),
                () => ClearRowsFromContext(contextItem)));

            return menu;
        }

        private MenuItem CreateMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private static void EnableLiveSorting(IEnumerable source)
        {
            if (source != null &&
                CollectionViewSource.GetDefaultView(source) is ICollectionViewLiveShaping live &&
                live.CanChangeLiveSorting)
            {
                live.IsLiveSorting = true;
            }
        }

        private static bool IsCategoryTypeOnAllRows(
            IReadOnlyList<ManageAchievementsCategoryItem> rows,
            string categoryType)
        {
            if (rows == null || rows.Count == 0)
            {
                return false;
            }

            return rows.All(row => row != null &&
                AchievementCategoryTypeHelper.ParseValues(row.CategoryType)
                    .Any(value => string.Equals(value, categoryType, StringComparison.OrdinalIgnoreCase)));
        }

        private void SetLabelFromContext(ManageAchievementsCategoryItem contextItem)
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
                L("LOCPlayAch_ManageAchievements_Category_Context_SetLabelHint"),
                inputText);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_ManageAchievements_Category_Context_SetLabelTitle"),
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

        private void MergeCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || !TryResolveCategoryImageRow(sender as FrameworkElement, out var row))
            {
                return;
            }

            var sourceLabel = row.CategoryLabel;
            var targetOptions = ViewModel.CategoryRows
                .Where(candidate => candidate != null && !string.IsNullOrWhiteSpace(candidate.CategoryLabel))
                .Select(candidate => candidate.CategoryLabel)
                .ToList();

            if (targetOptions.Count(label => !string.Equals(label, sourceLabel, StringComparison.OrdinalIgnoreCase)) == 0)
            {
                return;
            }

            var dialog = new MergeCategoryDialog(sourceLabel, targetOptions);
            var window = PlayniteUiProvider.CreateExtensionWindow(
                L("LOCPlayAch_ManageAchievements_Category_MergeDialog_Title"),
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = false,
                    Width = 500,
                    Height = 220
                });

            WindowPlacementPersistenceService.Attach(
                window,
                ViewModel.PlacementSettings,
                () => PlayniteAchievementsPlugin.Instance?.PersistSettingsForUi(),
                "ManageAchievementsMergeCategoryDialog",
                ViewModel.PlacementLogger);

            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult != true)
            {
                return;
            }

            ViewModel.MergeCategoryInto(sourceLabel, dialog.SelectedTarget);
        }

        private void ClearRowsFromContext(ManageAchievementsCategoryItem contextItem)
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

        private List<ManageAchievementsCategoryItem> ResolveActionRows(ManageAchievementsCategoryItem contextItem)
        {
            if (ViewModel == null)
            {
                return new List<ManageAchievementsCategoryItem>();
            }

            var selectedRows = ViewModel.GetAllSelectedRows();
            if (selectedRows.Count == 0)
            {
                return contextItem == null
                    ? new List<ManageAchievementsCategoryItem>()
                    : new List<ManageAchievementsCategoryItem> { contextItem };
            }

            if (contextItem == null || contextItem.IsSelected)
            {
                return selectedRows;
            }

            return new List<ManageAchievementsCategoryItem> { contextItem };
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

        private void ResetCategoryMetadataButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            var menu = new ContextMenu();
            menu.Items.Add(CreateResetMenuItem(
                L("LOCPlayAch_ManageAchievements_Tab_AchievementOrder"),
                ViewModel.HasCustomCategoryOrder,
                () => ResetCategoryMetadataAspect(ViewModel.ResetCategoryOrder)));
            menu.Items.Add(CreateResetMenuItem(
                L("LOCPlayAch_Column_Name"),
                ViewModel.HasCustomCategoryNames,
                () => ResetCategoryMetadataAspect(ViewModel.ResetCategoryNames)));
            menu.Items.Add(CreateResetMenuItem(
                L("LOCPlayAch_Column_CategoryArt"),
                ViewModel.HasCustomCategoryArt || ViewModel.HasCustomSummaryCategory,
                () => ResetCategoryMetadataAspect(ViewModel.ResetCategoryArt)));

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            OpenSelectorContextMenu(ResetCategoryMetadataButton, menu);
            e.Handled = true;
        }

        private MenuItem CreateResetMenuItem(string header, bool isEnabled, Action onClick)
        {
            var item = CreateMenuItem(header, onClick);
            item.IsEnabled = isEnabled;
            return item;
        }

        private void ResetCategoryMetadataAspect(Func<bool> reset)
        {
            if (reset?.Invoke() == true)
            {
                DataGridRowReorderBehavior.CancelPendingDrag(CategoryManagerDataGrid);
            }
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

        private void RestoreCategoryManagerSelectionByLabels(IEnumerable<string> labels)
        {
            if (CategoryManagerDataGrid == null)
            {
                return;
            }

            var selectedLabels = new HashSet<string>(
                (labels ?? Enumerable.Empty<string>())
                    .Select(AchievementCategoryTypeHelper.NormalizeCategoryOrDefault)
                    .Where(label => !string.IsNullOrWhiteSpace(label)),
                StringComparer.OrdinalIgnoreCase);
            CategoryManagerDataGrid.SelectedItems.Clear();
            if (selectedLabels.Count == 0 || ViewModel?.CategoryRows == null)
            {
                return;
            }

            foreach (var row in ViewModel.CategoryRows)
            {
                if (!string.IsNullOrWhiteSpace(row?.CategoryLabel) && selectedLabels.Contains(row.CategoryLabel))
                {
                    CategoryManagerDataGrid.SelectedItems.Add(row);
                }
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static bool TryGetFirstImageFilePath(IDataObject data, out string imagePath)
        {
            imagePath = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                if (!data.GetDataPresent(DataFormats.FileDrop))
                {
                    return false;
                }

                var files = data.GetData(DataFormats.FileDrop) as string[];
                imagePath = files?.FirstOrDefault(IsSupportedImageFile);
                return !string.IsNullOrWhiteSpace(imagePath);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetFirstBrowserUrl(IDataObject data, out string url)
        {
            url = null;
            if (data == null)
            {
                return false;
            }

            try
            {
                var text = ReadDroppedText(data, DataFormats.UnicodeText) ??
                           ReadDroppedText(data, DataFormats.Text) ??
                           ReadDroppedText(data, DataFormats.Html);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                var match = HttpUrlRegex.Match(text);
                if (!match.Success)
                {
                    return false;
                }

                url = TrimTrailingUrlPunctuation(match.Value);
                return !string.IsNullOrWhiteSpace(url);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadDroppedText(IDataObject data, string format)
        {
            if (data == null || string.IsNullOrWhiteSpace(format))
            {
                return null;
            }

            try
            {
                return data.GetDataPresent(format)
                    ? data.GetData(format) as string
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsSupportedImageFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path) ?? string.Empty;
            if (!SupportedImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string TrimTrailingUrlPunctuation(string value)
        {
            return (value ?? string.Empty).Trim().TrimEnd('.', ',', ';', ')', ']', '}');
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
            Action<string, bool> setSelection,
            Func<string, string> displayText = null)
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
    }
}
