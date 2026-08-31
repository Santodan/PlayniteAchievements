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
// WinForms dialog: the WPF Microsoft.Win32 picker renders legacy-style on .NET Framework.
using DialogResult = System.Windows.Forms.DialogResult;
using OpenFileDialog = System.Windows.Forms.OpenFileDialog;
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

        private DataGridRow _pendingRightClickRow;
        private DataGridRow _pendingManagerRightClickRow;

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
                // An insert-line drop means the gap it points at, level included: the dragged
                // categories become siblings of the row below the gap. The VM falls back to a
                // flat reorder when the gap is at their current level or the reparent is invalid.
                MoveItemsRelativeToTarget = (labels, target, insertAfter) =>
                    target is ManageAchievementsCategoryMetadataItem targetItem &&
                    ViewModel?.NestCategoryRowsIntoGap(
                        labels,
                        ResolveGapRowBelow(targetItem, insertAfter)?.CategoryLabel) == true,
                MoveItemsToEnd = labels => ViewModel?.NestCategoryRowsIntoGap(labels, null) == true,
                RestoreSelection = RestoreManagerSelectionAfterReorder,
                ResolveDropIndicatorInset = ResolveDropLineInset,
                // Dropping onto a row's middle makes the dragged categories subcategories of that
                // row. The guards (Default, cycles, depth, no-ops) all live in the planner, so the
                // drag and the context menu can never disagree about what is allowed.
                NestHighlight = CategoryNestHighlight,
                CanNestOnTarget = (labels, target) =>
                    target is ManageAchievementsCategoryMetadataItem nestTarget &&
                    ViewModel?.CanNestCategoryRowsUnder(labels, nestTarget.CategoryLabel) == true,
                NestItemsOnTarget = (labels, target) =>
                    target is ManageAchievementsCategoryMetadataItem nestTarget &&
                    ViewModel?.NestCategoryRowsUnder(labels, nestTarget.CategoryLabel) == true
            });

            viewModel.CategoryRowsMoved += (_, labels) => RestoreCategoryManagerSelectionByLabels(labels);

            // Enter in the bulk picker applies, matching the plain text box it replaced. The
            // control raises this only when its drop-down is closed, so Enter still picks the
            // highlighted row while the list is open.
            CategoryInputPicker.Committed += (_, __) => ApplyBulk();
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

            OpenCategoryFilterContextMenu(
                CategoryLabelFilterSelectionButton,
                CategoryLabelFilterSelectionContextMenu,
                ViewModel.CategoryLabelFilterOptions,
                option => ViewModel.IsCategoryLabelFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryLabelFilterSelected(option, isSelected));
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

            var applied = ViewModel.ApplyBulkToSelection(selectedRows, CategoryInputPicker.ResolveSelection());
            if (applied)
            {
                CategoryInputPicker.SetInitialCategory(null);
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
                CategoryInputPicker.SetInitialCategory(null);
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
                Filter = ImageFormats.BuildOpenFileDialogFilter(includeAllFiles: true),
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() == DialogResult.OK)
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

        private void CategoryManagerRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                _pendingManagerRightClickRow = row;
            }
        }

        private void CategoryManagerRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row)
            {
                e.Handled = true;
                var targetRow = _pendingManagerRightClickRow ?? row;
                _pendingManagerRightClickRow = null;
                OpenManagerContextMenuForRow(targetRow);
            }
        }

        private bool OpenManagerContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (!(row?.DataContext is ManageAchievementsCategoryMetadataItem item))
            {
                return false;
            }

            var menu = BuildManagerRowContextMenu(item);
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

        private ContextMenu BuildManagerRowContextMenu(ManageAchievementsCategoryMetadataItem contextItem)
        {
            if (ViewModel == null)
            {
                return null;
            }

            var rows = ResolveManagerActionRows(contextItem);
            var actionLabels = rows
                .Where(row => row != null && !row.IsDefaultCategory && !string.IsNullOrWhiteSpace(row.CategoryLabel))
                .Select(row => row.CategoryLabel)
                .ToList();
            if (actionLabels.Count == 0)
            {
                // The Default bucket has no row actions: it cannot move, merge, duplicate or delete.
                return null;
            }

            var menu = new ContextMenu();

            var subcategoryMenu = new MenuItem
            {
                Header = L("LOCPlayAch_ManageAchievements_Category_Context_MakeSubcategoryOf")
            };
            var validTargets = ViewModel.CategoryRows
                .Where(candidate => candidate != null &&
                    !candidate.IsDefaultCategory &&
                    !string.IsNullOrWhiteSpace(candidate.CategoryLabel) &&
                    ViewModel.CanNestCategoryRowsUnder(actionLabels, candidate.CategoryLabel))
                .Select(candidate => candidate.CategoryLabel)
                .ToList();
            var targetRows = CategoryFilterMenuBuilder.BuildRows(validTargets);
            var itemStyle = CategoryFilterMenuBuilder.ResolveItemStyle(this, targetRows);
            foreach (var targetRow in targetRows)
            {
                subcategoryMenu.Items.Add(CategoryFilterMenuBuilder.CreateActionItem(
                    targetRow,
                    itemStyle,
                    target => ViewModel?.NestCategoryRowsUnder(actionLabels, target)));
            }

            subcategoryMenu.IsEnabled = targetRows.Count > 0;
            menu.Items.Add(subcategoryMenu);

            var makeTopLevelItem = CreateMenuItem(
                L("LOCPlayAch_ManageAchievements_Category_Context_MakeTopLevel"),
                () => ViewModel?.NestCategoryRowsUnder(actionLabels, null));
            makeTopLevelItem.IsEnabled = actionLabels.Any(label => CategoryPathHelper.GetDepth(label) > 1);
            menu.Items.Add(makeTopLevelItem);

            menu.Items.Add(new Separator());

            // Merge is one source into one target, so it stays single-row. The target list is the
            // same connector tree the subcategory submenu draws; the source's own subtree is
            // excluded because the merge folds that whole subtree away.
            var mergeRow = rows.Count == 1 ? rows[0] : null;
            var mergeMenu = new MenuItem
            {
                Header = L("LOCPlayAch_ManageAchievements_Category_MergeDialog_Target"),
                ToolTip = L("LOCPlayAch_ManageAchievements_Category_MergeTooltip")
            };
            if (mergeRow != null && !mergeRow.IsDefaultCategory && ViewModel.CanMergeCategories)
            {
                var sourceLabel = mergeRow.CategoryLabel;
                var mergeTargets = ViewModel.CategoryRows
                    .Where(candidate => candidate != null &&
                        !string.IsNullOrWhiteSpace(candidate.CategoryLabel) &&
                        !CategoryPathHelper.IsSelfOrDescendantOf(candidate.CategoryLabel, sourceLabel))
                    .Select(candidate => candidate.CategoryLabel)
                    .ToList();
                var mergeTargetRows = CategoryFilterMenuBuilder.BuildRows(mergeTargets);
                var mergeStyle = CategoryFilterMenuBuilder.ResolveItemStyle(this, mergeTargetRows);
                foreach (var mergeTargetRow in mergeTargetRows)
                {
                    mergeMenu.Items.Add(CategoryFilterMenuBuilder.CreateActionItem(
                        mergeTargetRow,
                        mergeStyle,
                        target =>
                        {
                            if (ViewModel?.MergeCategoryInto(sourceLabel, target) == true)
                            {
                                DataGridRowReorderBehavior.CancelPendingDrag(CategoryManagerDataGrid);
                            }
                        }));
                }
            }

            mergeMenu.IsEnabled = mergeMenu.Items.Count > 0;
            menu.Items.Add(mergeMenu);

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Common_Duplicate"),
                () => DuplicateCategoriesFromContext(actionLabels)));

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Button_Delete"),
                () => DeleteCategoriesFromContext(actionLabels)));

            return menu;
        }

        private List<ManageAchievementsCategoryMetadataItem> ResolveManagerActionRows(
            ManageAchievementsCategoryMetadataItem contextItem)
        {
            if (contextItem == null)
            {
                return new List<ManageAchievementsCategoryMetadataItem>();
            }

            var selected = CategoryManagerDataGrid?.SelectedItems
                ?.OfType<ManageAchievementsCategoryMetadataItem>()
                .ToList() ?? new List<ManageAchievementsCategoryMetadataItem>();
            if (selected.Count > 1 && selected.Contains(contextItem) && ViewModel != null)
            {
                // Visual order, so batched moves land the way the grid reads.
                return selected
                    .OrderBy(item => ViewModel.CategoryRows.IndexOf(item))
                    .ToList();
            }

            return new List<ManageAchievementsCategoryMetadataItem> { contextItem };
        }

        private void DuplicateCategoriesFromContext(IReadOnlyList<string> labels)
        {
            var created = ViewModel?.DuplicateCategories(labels);
            if (created == null || created.Count == 0)
            {
                return;
            }

            DataGridRowReorderBehavior.CancelPendingDrag(CategoryManagerDataGrid);
            if (created.Count == 1)
            {
                FocusCategoryRenameBox(created[0]);
            }
            else
            {
                RestoreCategoryManagerSelectionByLabels(created);
            }
        }

        private void DeleteCategoriesFromContext(IReadOnlyList<string> labels)
        {
            if (ViewModel == null || labels == null || labels.Count == 0)
            {
                return;
            }

            // Deleting an empty branch is quiet; deleting one that holds achievements asks first,
            // because those achievements fall back to the Default bucket.
            var affected = ViewModel.CountAchievementsInCategories(labels);
            if (affected > 0)
            {
                var confirmText = labels.Count == 1
                    ? string.Format(
                        L("LOCPlayAch_ManageAchievements_Category_DeleteConfirmSingle"),
                        CategoryPathHelper.ToDisplayPath(labels[0]),
                        affected)
                    : string.Format(
                        L("LOCPlayAch_ManageAchievements_Category_DeleteConfirmSelected"),
                        labels.Count,
                        affected);
                var result = API.Instance?.Dialogs?.ShowMessage(
                    confirmText,
                    L("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;
                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            if (ViewModel.DeleteCategories(labels))
            {
                DataGridRowReorderBehavior.CancelPendingDrag(CategoryManagerDataGrid);
            }
        }

        private void AddCategoryButton_Click(object sender, RoutedEventArgs e)
        {
            var label = ViewModel?.AddNewCategory();
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            DataGridRowReorderBehavior.CancelPendingDrag(CategoryManagerDataGrid);
            FocusCategoryRenameBox(label);
            e.Handled = true;
        }

        /// <summary>
        /// Scrolls the row for <paramref name="categoryLabel"/> into view, selects it, and focuses
        /// its inline rename box with the text selected. Deferred to Loaded priority so the
        /// collection Reset and container generation settle first.
        /// </summary>
        private void FocusCategoryRenameBox(string categoryLabel)
        {
            if (ViewModel == null || string.IsNullOrWhiteSpace(categoryLabel))
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() =>
            {
                var item = ViewModel?.CategoryRows.FirstOrDefault(row =>
                    row != null && CategoryPathHelper.IsSame(row.CategoryLabel, categoryLabel));
                if (item == null || CategoryManagerDataGrid == null)
                {
                    return;
                }

                CategoryManagerDataGrid.SelectedItems.Clear();
                CategoryManagerDataGrid.SelectedItems.Add(item);
                CategoryManagerDataGrid.ScrollIntoView(item);
                CategoryManagerDataGrid.UpdateLayout();
                var row = CategoryManagerDataGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
                if (row == null)
                {
                    return;
                }

                // The category cell (column 2) holds exactly one TextBox, the rename box; probing
                // the whole row would find the art-URL box first.
                var categoryColumn = CategoryManagerDataGrid.Columns.Count > 2
                    ? CategoryManagerDataGrid.Columns[2]
                    : null;
                var cellContent = categoryColumn?.GetCellContent(row);
                var textBox = cellContent as TextBox
                    ?? (cellContent == null ? null : VisualTreeHelpers.FindVisualChild<TextBox>(cellContent));
                if (textBox == null)
                {
                    return;
                }

                textBox.Focus();
                textBox.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private bool TryOpenSelectedManagerRowContextMenu()
        {
            var row = GetManagerControllerTargetRow();
            if (row == null)
            {
                return false;
            }

            return OpenManagerContextMenuForRow(row, useControllerPlacement: true);
        }

        private DataGridRow GetManagerControllerTargetRow()
        {
            var focusedRow = VisualTreeHelpers.FindVisualParent<DataGridRow>(
                Keyboard.FocusedElement as DependencyObject);
            if (focusedRow != null &&
                ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(focusedRow), CategoryManagerDataGrid))
            {
                return focusedRow;
            }

            var index = CategoryManagerDataGrid?.SelectedIndex ?? -1;
            if (index < 0 && CategoryManagerDataGrid?.Items.Count > 0)
            {
                index = 0;
                CategoryManagerDataGrid.SelectedIndex = index;
            }

            if (CategoryManagerDataGrid == null || index < 0)
            {
                return null;
            }

            CategoryManagerDataGrid.UpdateLayout();
            var row = CategoryManagerDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            if (row == null)
            {
                CategoryManagerDataGrid.ScrollIntoView(CategoryManagerDataGrid.Items[index]);
                CategoryManagerDataGrid.UpdateLayout();
                row = CategoryManagerDataGrid.ItemContainerGenerator.ContainerFromIndex(index) as DataGridRow;
            }

            return row;
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

                if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
                {
                    return TryOpenSelectedManagerRowContextMenu();
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
                elements.Add(CategoryInputPicker);
                elements.Add(ApplyBulkButton);
            }

            if (CategorySubTabs?.SelectedIndex == 1)
            {
                elements.Add(AddCategoryButton);
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

            var inputDialog = new CategoryPickerDialog(
                L("LOCPlayAch_ManageAchievements_Category_Context_SetLabelHint"),
                ViewModel.AssignableCategoryOptions,
                contextItem?.Category);
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

            ViewModel.SetCategoryLabelForSelection(rows, (inputDialog.SelectedCategory ?? string.Empty).Trim());
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
                CategoryInputPicker.SetInitialCategory(null);
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
                CategoryInputPicker.SetInitialCategory(null);
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

        /// <summary>
        /// The row directly below the gap an insert-line drop points at: the target itself for
        /// insert-before, the next rendered row for insert-after, null past the last row. The
        /// dropped categories become siblings of this row.
        /// </summary>
        private ManageAchievementsCategoryMetadataItem ResolveGapRowBelow(
            ManageAchievementsCategoryMetadataItem target,
            bool insertAfter)
        {
            if (target == null || ViewModel == null)
            {
                return null;
            }

            if (!insertAfter)
            {
                return target;
            }

            var rows = ViewModel.CategoryRows;
            var index = rows.IndexOf(target);
            return index >= 0 && index + 1 < rows.Count ? rows[index + 1] : null;
        }

        /// <summary>
        /// Indents the insert line to the level the gap would give the dropped rows, so a line
        /// inside a subtree visibly differs from one at the root.
        /// </summary>
        private double ResolveDropLineInset(object target, bool insertAfter)
        {
            var gapRow = ResolveGapRowBelow(target as ManageAchievementsCategoryMetadataItem, insertAfter);
            var parentDepth = gapRow == null ? 0 : CategoryPathHelper.GetDepth(gapRow.CategoryLabel) - 1;
            if (parentDepth <= 0 || CategoryManagerDataGrid == null || CategoryManagerDataGrid.Columns.Count < 3)
            {
                return 0;
            }

            // Columns left of the name column (drag handle, indent buttons), then the parent's
            // guide lane - where the new row's stem would hang.
            return CategoryManagerDataGrid.Columns[0].ActualWidth +
                   CategoryManagerDataGrid.Columns[1].ActualWidth +
                   CategoryTreeGuideMetrics.GetLaneCentre(parentDepth);
        }

        /// <summary>
        /// A gap drop can reparent, which rewrites the dragged keys; the VM's CategoryRowsMoved
        /// has then already restored selection on the new labels, and restoring the stale keys
        /// here would clear it. Restore only when every dragged key still resolves - a pure
        /// reorder.
        /// </summary>
        private void RestoreManagerSelectionAfterReorder(IReadOnlyList<string> labels)
        {
            if (ViewModel == null || labels == null || labels.Count == 0)
            {
                return;
            }

            foreach (var label in labels)
            {
                if (!ViewModel.CategoryRows.Any(row =>
                        row != null && CategoryPathHelper.IsSame(row.CategoryLabel, label)))
                {
                    return;
                }
            }

            RestoreCategoryManagerSelectionByLabels(labels);
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
            return ImageDropHelper.IsSupportedImageFile(path);
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

        /// <summary>
        /// Opens the category filter dropdown: one row per category, leaf names with the full path
        /// on hover, and the connectors that place each row in the tree.
        /// </summary>
        private static void OpenCategoryFilterContextMenu(
            Button button,
            ContextMenu menu,
            IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection)
        {
            if (button == null ||
                !CategoryFilterMenuBuilder.Populate(button, menu, options, isSelected, setSelection))
            {
                return;
            }

            OpenSelectorContextMenu(button, menu);
        }
    }
}
