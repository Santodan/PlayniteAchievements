using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.ManageAchievements
{
    /// <summary>
    /// The merged Manage Achievements editor. One list of every achievement, with drag-reorder and
    /// a right-click menu that applies a facet to the whole selection.
    /// </summary>
    public partial class ManageAchievementsEditorTab : UserControl
    {
        private const string DragDataFormat = "PlayniteAchievements.ManageAchievementsEditorRows";

        internal ManageAchievementsEditorTab(ManageAchievementsEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // Reuses the behavior that drives the old Order and Goals grids, including its
            // ctrl/shift-preserving press handling, so a multi-row drag behaves identically here.
            DataGridRowReorderBehavior.SetOptions(AchievementsGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is AchievementEditorRow,
                ExtractDragKeys = items => AchievementOrderHelper.NormalizeApiNames(
                    items.OfType<AchievementEditorRow>().Select(item => item.ApiName)),
                MoveItemsRelativeToTarget = (apiNames, target, insertAfter) =>
                    target is AchievementEditorRow targetRow &&
                    ViewModel?.MoveItemsByApiName(apiNames, targetRow.ApiName, insertAfter) == true,
                MoveItemsToEnd = apiNames => ViewModel?.MoveItemsToEndByApiName(apiNames) == true,
                RestoreSelection = RestoreSelectionByApiNames
            });
        }

        private ManageAchievementsEditorViewModel ViewModel => DataContext as ManageAchievementsEditorViewModel;

        public void RefreshData() => ViewModel?.ReloadData();

        /// <summary>
        /// The elements fullscreen controller navigation steps through on this tab.
        /// </summary>
        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement> { SearchBox, AchievementsGrid };
            return elements
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        /// <summary>
        /// Opens the bulk menu for the focused row on a controller secondary press, matching how
        /// the Order and Goals grids surface their row menus.
        /// </summary>
        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (AchievementsGrid?.IsKeyboardFocusWithin != true ||
                !FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return false;
            }

            var row = FullscreenControllerNavigationService.GetTargetDataGridRow(AchievementsGrid);
            return row != null && OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        /// <summary>
        /// Commits an in-progress cell edit on Enter. The text boxes bind on LostFocus so a partial
        /// value is never persisted, which leaves Enter as the explicit commit.
        /// </summary>
        private void EditorTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }

        // Right-click must not collapse an existing multi-row selection: the menu acts on the
        // selection, so pressing down on an already-selected row is swallowed and the menu opens
        // on release.
        private void Row_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.IsSelected && AchievementsGrid.SelectedItems.Count > 1)
            {
                e.Handled = true;
            }
        }

        private void Row_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGridRow row))
            {
                return;
            }

            if (OpenContextMenuForRow(row))
            {
                e.Handled = true;
            }
        }

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (!(row?.DataContext is AchievementEditorRow contextRow) || ViewModel == null)
            {
                return false;
            }

            var menu = BuildRowContextMenu(contextRow);
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

        /// <summary>
        /// Builds the bulk menu for the current selection, falling back to the right-clicked row
        /// when nothing is selected.
        /// </summary>
        /// <remarks>
        /// Capstone is absent on purpose: a game has at most one, so it stays an inline single-row
        /// checkbox rather than a selection action.
        /// </remarks>
        private ContextMenu BuildRowContextMenu(AchievementEditorRow contextRow)
        {
            var selection = ResolveActionRows(contextRow);
            var menu = new ContextMenu();

            var typesMenu = new MenuItem { Header = L("LOCPlayAch_Common_Label_Type") };
            foreach (var categoryType in AchievementCategoryTypeHelper.AssignableCategoryTypes)
            {
                var captured = categoryType;
                typesMenu.Items.Add(CreateMenuItem(
                    captured,
                    () => ViewModel?.ApplyBulkCategoryType(selection, captured)));
            }

            menu.Items.Add(typesMenu);

            var trophyMenu = new MenuItem { Header = L("LOCPlayAch_Column_Trophy") };
            foreach (var trophyType in ViewModel.TrophyTypeOptions)
            {
                var captured = trophyType;
                trophyMenu.Items.Add(CreateMenuItem(
                    string.IsNullOrEmpty(captured) ? L("LOCPlayAch_Common_None") : captured,
                    () => ViewModel?.ApplyBulkTrophyType(selection, captured)));
            }

            menu.Items.Add(trophyMenu);
            menu.Items.Add(new Separator());

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_ManageAchievements_Tab_Goals"),
                () => ViewModel?.ApplyBulkGoal(selection, true)));
            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Button_Remove"),
                () => ViewModel?.ApplyBulkGoal(selection, false)));
            menu.Items.Add(new Separator());

            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_ManageAchievements_Filters_FilterOut"),
                () => ViewModel?.ApplyBulkFiltered(selection, true)));
            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_ManageAchievements_Filters_FilterOutOfSummaries"),
                () => ViewModel?.ApplyBulkSummaryFiltered(selection, true)));
            menu.Items.Add(CreateMenuItem(
                L("LOCPlayAch_Common_ResetSelected"),
                () =>
                {
                    ViewModel?.ApplyBulkFiltered(selection, false);
                    ViewModel?.ApplyBulkSummaryFiltered(selection, false);
                }));

            return menu;
        }

        /// <summary>
        /// The rows an action applies to: the grid selection when the right-clicked row is part of
        /// it, otherwise just that row. Mirrors the convention the Category tab established.
        /// </summary>
        private List<AchievementEditorRow> ResolveActionRows(AchievementEditorRow contextRow)
        {
            var selected = AchievementsGrid.SelectedItems
                .OfType<AchievementEditorRow>()
                .ToList();
            if (selected.Count > 1 && selected.Contains(contextRow))
            {
                return selected;
            }

            return new List<AchievementEditorRow> { contextRow };
        }

        private static MenuItem CreateMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, __) => action();
            return item;
        }

        /// <summary>
        /// Reselects rows after a reorder rebuilds the collection, so a multi-row drag does not
        /// clear the user's selection.
        /// </summary>
        private void RestoreSelectionByApiNames(IReadOnlyList<string> apiNames)
        {
            if (apiNames == null || apiNames.Count == 0)
            {
                return;
            }

            var wanted = new HashSet<string>(apiNames, StringComparer.OrdinalIgnoreCase);
            AchievementsGrid.SelectedItems.Clear();
            foreach (var row in AchievementsGrid.Items.OfType<AchievementEditorRow>())
            {
                if (!string.IsNullOrWhiteSpace(row.ApiName) && wanted.Contains(row.ApiName))
                {
                    AchievementsGrid.SelectedItems.Add(row);
                }
            }
        }

        private static string L(string key) => ResourceProvider.GetString(key);
    }
}
