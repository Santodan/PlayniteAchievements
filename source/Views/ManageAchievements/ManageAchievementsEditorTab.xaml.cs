using Playnite.SDK;
using PlayniteAchievements.Views.Dialogs;
using Microsoft.Win32;
using Playnite.SDK.Events;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace PlayniteAchievements.Views.ManageAchievements
{
    public partial class ManageAchievementsEditorTab : UserControl, IFullscreenControllerNavigable
    {
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

        private const string DragDataFormat = "PlayniteAchievements.ManageAchievementsEditorRows";

        private AchievementEditorRow _categoryPickerRow;

        private string _categoryPickerLabel;

        private DataGridRow _pendingRightClickRow;

        public ManageAchievementsEditorTab(ManageAchievementsEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            // The picker applies on pick, like the rest of this window, and is seeded for the row
            // that was selected when editing began, so it always commits to the right one.
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
            viewModel.FilterChanged += ViewModel_FilterChanged;
            AttachFilter();
            viewModel.AssignmentsChanged += (_, __) => SeedCategoryPicker();
            viewModel.ScrollRowIntoViewRequested += (_, row) => ScrollRowIntoView(row);

            // Posted at Background priority on purpose: the grid is still working through the
            // collection reset and the SelectedRow push-back when this is raised, and reselecting
            // inline would be undone by them.
            viewModel.RestoreSelectionRequested += (_, apiNames) =>
                Dispatcher.BeginInvoke(
                    new Action(() => RestoreSelectionByApiNames(apiNames)),
                    System.Windows.Threading.DispatcherPriority.Background);
            CategoryPicker.SelectionCommitted += (_, __) => ApplyCategoryFromPicker();
            CategoryPicker.CreateRequested += (_, __) => PromptAndCreateCategory();
            SeedCategoryPicker();

            // Same behavior that drove the Order and Goals grids, including its ctrl/shift-
            // preserving press handling, so a multi-row drag behaves the way it did there.
            DataGridRowReorderBehavior.SetOptions(CustomAchievementsGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is AchievementEditorRow,
                ExtractDragKeys = items => AchievementOrderHelper.NormalizeApiNames(
                    items.OfType<AchievementEditorRow>().Select(item => item.OriginalApiName)),
                MoveItemsRelativeToTarget = (apiNames, target, insertAfter) =>
                    target is AchievementEditorRow targetRow &&
                    ViewModel?.MoveItemsByApiName(apiNames, targetRow.OriginalApiName, insertAfter) == true,
                MoveItemsToEnd = apiNames => ViewModel?.MoveItemsToEndByApiName(apiNames) == true,
                RestoreSelection = RestoreSelectionByApiNames
            });

            // Confirms the behavior attached at all: if no reorder line ever appears in the log,
            // this says whether the wiring ran or the drop is being lost before it reaches us.
            LogManager.GetLogger().Debug(
                $"[Editor] Row reorder behavior attached to the achievements grid. " +
                $"columns={CustomAchievementsGrid.Columns.Count}.");
        }

        /// <summary>
        /// Reselects rows after a reorder rebuilds the collection, so a multi-row drag does not
        /// clear the user's selection.
        /// </summary>
        /// <summary>
        /// Points the grid's collection view at the view model's filter predicate, so narrowing the
        /// list never touches the rows behind it.
        /// </summary>
        /// <remarks>
        /// The filter lives on the view rather than on a second, filtered collection: every persist
        /// path in the view model walks <c>AchievementRows</c> as the game's complete, ordered list,
        /// and would write a truncated one if the filter removed rows from it.
        /// </remarks>
        private void AttachFilter()
        {
            var view = CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows);
            if (view == null)
            {
                return;
            }

            view.Filter = candidate => ViewModel?.MatchesFilter(candidate as AchievementEditorRow) != false;
        }

        private void ViewModel_FilterChanged(object sender, EventArgs e)
        {
            CollectionViewSource.GetDefaultView(ViewModel?.AchievementRows)?.Refresh();
        }

        private void ToggleDetailsPaneButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel != null)
            {
                ViewModel.IsDetailsPaneExpanded = !ViewModel.IsDetailsPaneExpanded;
            }
        }

        private void ClearFilterButton_Click(object sender, RoutedEventArgs e)
        {
            FilterTextBox.Clear();
            FilterTextBox.Focus();
        }

        /// <summary>
        /// Selects every achievement the grid is currently showing. With a filter active that is the
        /// matching subset, which is what makes a bulk edit over a search possible.
        /// </summary>
        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            CustomAchievementsGrid.SelectAll();
            CustomAchievementsGrid.Focus();
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            CustomAchievementsGrid.UnselectAll();
        }

        private void RestoreSelectionByApiNames(IReadOnlyList<string> apiNames)
        {
            if (apiNames == null || apiNames.Count == 0)
            {
                return;
            }

            var wanted = new HashSet<string>(apiNames, StringComparer.OrdinalIgnoreCase);
            CustomAchievementsGrid.SelectedItems.Clear();
            foreach (var row in CustomAchievementsGrid.Items.OfType<AchievementEditorRow>())
            {
                if (!string.IsNullOrWhiteSpace(row.OriginalApiName) && wanted.Contains(row.OriginalApiName))
                {
                    CustomAchievementsGrid.SelectedItems.Add(row);
                }
            }
        }

        private ManageAchievementsEditorViewModel ViewModel => DataContext as ManageAchievementsEditorViewModel;

        /// <summary>
        /// Hands the grid's selection to the view model so the details pane can edit several rows
        /// at once. WPF exposes SelectedItems only on the control, so it cannot be bound.
        /// </summary>
        private void AchievementsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ViewModel?.SetSelectedRows(CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>());
        }

        /// <summary>
        /// Brings a row the editor picked into view, after the grid has had a chance to realize it:
        /// a row added a moment ago has no container yet, and scrolling to one that does not exist
        /// does nothing.
        /// </summary>
        private void ScrollRowIntoView(AchievementEditorRow row)
        {
            if (row == null)
            {
                return;
            }

            Dispatcher.BeginInvoke(
                new Action(() =>
                {
                    try
                    {
                        CustomAchievementsGrid.ScrollIntoView(row);
                    }
                    catch (Exception)
                    {
                        // A row the filter is hiding has nowhere to scroll to.
                    }
                }),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ViewModel_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e?.PropertyName == nameof(ManageAchievementsEditorViewModel.SelectedRow) ||
                e?.PropertyName == nameof(ManageAchievementsEditorViewModel.EditTarget))
            {
                SeedCategoryPicker();
            }
        }

        /// <summary>
        /// Seeds the picker from whatever the pane is editing: the selected row, or the bulk proxy
        /// carrying the label the selection agrees on.
        /// </summary>
        private void SeedCategoryPicker()
        {
            var target = ViewModel?.EditTarget;
            // The effective label, so the picker opens showing the category the achievement is
            // actually in rather than only a category the user had overridden it to.
            var label = target?.EffectiveCategoryLabel;

            // The box's state is a function of exactly these two, and one selection change raises
            // EditTarget many times over - once for the grid's own reset, once for each row it
            // re-reports, and once more for the assignment notification.
            if (ReferenceEquals(target, _categoryPickerRow) &&
                string.Equals(label, _categoryPickerLabel, StringComparison.Ordinal))
            {
                return;
            }

            _categoryPickerRow = target;
            _categoryPickerLabel = label;
            CategoryPicker.SetInitialCategory(label);
        }

        /// <summary>
        /// Commits the picker to every selected achievement. The captured row is only the guard
        /// that editing had actually begun; the label lands on the selection, which is what the
        /// pane says it is editing.
        /// </summary>
        private void ApplyCategoryFromPicker()
        {
            var row = _categoryPickerRow;
            if (row == null || ViewModel == null || !row.CanEditAssignments)
            {
                return;
            }

            // The box is select-only, so it has no way to express "no category": an empty selection
            // means the list was rebuilt under it, and committing that would clear the assignment.
            // Clearing is the row menu's job.
            var picked = CategoryPicker.ResolveSelection();
            if (string.IsNullOrWhiteSpace(picked))
            {
                return;
            }

            ViewModel.SetCategoryForSelection(picked);
        }

        /// <summary>
        /// Names and creates a category, filing the selection in it. Reached from the create row at
        /// the top of the picker and from the row menu, so both gestures share one set of rules.
        /// </summary>
        private void PromptAndCreateCategory()
        {
            if (ViewModel == null || !CategoryCreationPrompt.TryPrompt(out var leafName))
            {
                return;
            }

            var created = ViewModel.CreateAndAssignCategory(leafName);
            if (!string.IsNullOrWhiteSpace(created))
            {
                SeedCategoryPicker();
            }
        }

        /// <summary>
        /// Applies a filter scope picked in the details pane to every selected achievement.
        /// </summary>
        /// <remarks>
        /// The combo only reports what the user chose; re-seeding it as the selection changes
        /// raises this too, which the comparison against the edit target's current scope filters
        /// out. A proxy standing in for rows that disagree has no matching item at all, so the
        /// first real pick always reads as a change.
        /// </remarks>
        private void FilterScopeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(e?.AddedItems?.Count > 0) ||
                !(e.AddedItems[0] is AchievementFilterScopeOption option) ||
                ViewModel == null)
            {
                return;
            }

            var target = ViewModel.EditTarget;
            if (target == null || !target.CanEditAssignments || option.Value == target.FilterScope)
            {
                return;
            }

            ViewModel.SetFilterScopeForSelection(option.Value);
        }

        /// <summary>
        /// Opens the same note editor the Notes tab uses, so a note written here is written the
        /// same way and gets the markdown-capable editor rather than a bare cell.
        /// </summary>
        private void EditNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                EditNote(row);
            }
        }

        private void EditNoteForSelectedRow()
        {
            var row = CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>().FirstOrDefault();
            if (row != null)
            {
                EditNote(row);
            }
        }

        private void EditNote(AchievementEditorRow row)
        {
            var dialog = new AchievementNoteDialog(
                row.DisplayName,
                row.OriginalApiName,
                row.AchievementNote,
                isReadOnly: false,
                achievementIconSource: row.DisplayIcon);

            var window = PlayniteUiProvider.CreateExtensionWindow(
                ResourceProvider.GetString("LOCPlayAch_NotesDialog_EditTitle"),
                dialog,
                new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = false,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 640,
                    Height = 560
                });

            WindowPlacementPersistenceService.Attach(window, "AchievementNoteEdit");
            dialog.RequestClose += (s, args) => window.Close();
            window.ShowDialog();

            if (dialog.DialogResult == true)
            {
                // Assigning the row's note raises the change that persists it, the same path a
                // checkbox or committed text box takes.
                row.AchievementNote = dialog.SavedNote;
            }
        }

        /// <summary>
        /// Text editors bind on focus loss so a half-typed value is not persisted; Enter commits
        /// the same way the other Manage tabs do.
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

        /// <summary>
        /// Adds, replaces or drops the capstone for the edited achievement. The button says which,
        /// so a replacement is never silent.
        /// </summary>
        private void CapstoneActionButton_Click(object sender, RoutedEventArgs e)
        {
            var row = ViewModel?.EditTarget;
            if (row == null)
            {
                return;
            }

            ViewModel.SetCapstoneForSelection(!row.IsCapstone);
        }

        private void TypeSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null || TypeSelectionContextMenu == null || TypeSelectionButton == null)
            {
                return;
            }

            SelectorContextMenuHelper.OpenCategoryTypeMenu(
                TypeSelectionButton,
                TypeSelectionContextMenu,
                ViewModel.TypeSelectionOptions);
        }

        public void RefreshData()
        {
            ViewModel?.RefreshData();
        }

        private void ContextMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || button.ContextMenu == null)
            {
                return;
            }

            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
        }

        /// <summary>
        /// Reveals a masked name by clicking it. The placeholder is only on screen while the name
        /// is masked, so the click has one meaning and does not need to re-mask; the toggle beside
        /// it is what puts the mask back.
        /// </summary>
        private void MaskedTitle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealTitle();
                e.Handled = true;
            }
        }

        /// <summary>Reveals a masked description by clicking it.</summary>
        private void MaskedDescription_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RevealDescription();
                e.Handled = true;
            }
        }

        /// <summary>
        /// Reveals or re-masks one row's name. Separate from the description's toggle: each is
        /// spoiled on its own.
        /// </summary>
        private void ToggleTitleRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.ToggleTitleReveal();
            }
        }

        /// <summary>Reveals or re-masks one row's description.</summary>
        private void ToggleDescriptionRevealButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.ToggleDescriptionReveal();
            }
        }

        private void IconImage_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is AchievementEditorRow row) || !row.CanReveal)
            {
                return;
            }

            row.AdvanceIconStage();
            e.Handled = true;
        }

        private void RarityMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!(e.OriginalSource is MenuItem menuItem) ||
                !(menuItem.DataContext is CustomAchievementSelectionOption option))
            {
                return;
            }

            var menu = ItemsControl.ItemsControlFromItemContainer(menuItem) as ContextMenu;
            if ((menu?.PlacementTarget as FrameworkElement)?.DataContext is AchievementEditorRow row)
            {
                row.RarityInput = option.DisplayName;
            }
        }

        private void BrowseIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Image Files (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All Files (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            SetIconPath(row, variant, dialog.FileName);
        }

        private void ClearIconButton_Click(object sender, RoutedEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            SetIconPath(row, variant, null);
            e.Handled = true;
        }

        private void IconTextBox_PreviewDragOver(object sender, DragEventArgs e)
        {
            var hasDropPayload = TryGetFirstImageFilePath(e.Data, out _) || TryGetFirstBrowserUrl(e.Data, out _);
            e.Effects = hasDropPayload ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void IconTextBox_Drop(object sender, DragEventArgs e)
        {
            if (!TryResolveRowAndVariant(sender as FrameworkElement, out var row, out var variant))
            {
                return;
            }

            try
            {
                if (TryGetFirstImageFilePath(e.Data, out var imagePath))
                {
                    SetIconPath(row, variant, imagePath);
                    e.Handled = true;
                    return;
                }

                if (TryGetFirstBrowserUrl(e.Data, out var url))
                {
                    SetIconPath(row, variant, url);
                    e.Handled = true;
                }
            }
            catch
            {
                e.Handled = true;
            }
        }

        private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var textBox = sender as TextBox;
            var candidate = BuildCandidateText(textBox, e?.Text);
            e.Handled = !IsValidNumericCandidate(candidate, textBox?.Tag as string);
        }

        private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            var textBox = sender as TextBox;
            var pastedText = e.DataObject?.GetData(typeof(string)) as string;
            if (!IsValidNumericCandidate(BuildCandidateText(textBox, pastedText), textBox?.Tag as string))
            {
                e.CancelCommand();
            }
        }

        /// <summary>
        /// A right-click that lands outside the current selection moves the selection to that row
        /// first, so the menu always acts on what the user sees highlighted.
        /// </summary>
        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGridRow row))
            {
                return;
            }

            e.Handled = true;
            _pendingRightClickRow = row;
            if (!CustomAchievementsGrid.SelectedItems.Contains(row.Item))
            {
                CustomAchievementsGrid.SelectedItems.Clear();
                CustomAchievementsGrid.SelectedItem = row.Item;
            }
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGridRow row))
            {
                return;
            }

            e.Handled = true;
            var targetRow = _pendingRightClickRow ?? row;
            _pendingRightClickRow = null;
            OpenContextMenuForRow(targetRow);
        }

        private bool OpenContextMenuForRow(DataGridRow row, bool useControllerPlacement = false)
        {
            if (!(row?.DataContext is AchievementEditorRow))
            {
                return false;
            }

            var menu = BuildRowContextMenu();
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
        /// Builds the row menu from the current selection, so every entry applies to all selected
        /// achievements rather than to the row that was clicked.
        /// </summary>
        /// <remarks>
        /// A check mark means every selected row already carries that value; a mixed selection
        /// shows none, and picking the entry applies it to all of them.
        /// </remarks>
        private ContextMenu BuildRowContextMenu()
        {
            var viewModel = ViewModel;
            if (viewModel == null)
            {
                return null;
            }

            var selection = CustomAchievementsGrid.SelectedItems.OfType<AchievementEditorRow>().ToList();
            if (selection.Count == 0)
            {
                return null;
            }

            var menu = new ContextMenu();

            // Capstone first, matching the Capstones tab's single-per-game rule: it is a property of
            // the game, not of a selection, so it is offered only for one row.
            if (viewModel.IsSingleCapstoneSelection(out var isCapstone))
            {
                var capstoneItem = new MenuItem
                {
                    Header = ResourceProvider.GetString("LOCPlayAch_Dynamic_Capstone"),
                    IsCheckable = true,
                    IsChecked = isCapstone
                };
                capstoneItem.Click += (_, __) => viewModel.SetCapstoneForSelection(capstoneItem.IsChecked);
                menu.Items.Add(capstoneItem);
            }

            var goalItem = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_Goal"),
                IsCheckable = true,
                IsChecked = selection.All(row => row.IsGoal),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };
            goalItem.Click += (_, __) => viewModel.SetGoalForSelection(goalItem.IsChecked);
            menu.Items.Add(goalItem);

            var filterMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Menu_Filters"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };
            foreach (var option in viewModel.FilterScopeOptions)
            {
                var scope = option.Value;
                var scopeItem = new MenuItem
                {
                    Header = option.DisplayName,
                    IsCheckable = true,
                    IsChecked = selection.All(row => row.FilterScope == scope)
                };
                scopeItem.Click += (_, __) => viewModel.SetFilterScopeForSelection(scope);
                filterMenu.Items.Add(scopeItem);
            }

            menu.Items.Add(filterMenu);

            // Category and type, the same two the Category tab's row menu offers.
            var categoryMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Common_Label_Category"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };

            // Creating one sits above the categories that exist, the same place the picker offers
            // it, so the gesture is in reach without going to the Categories tab.
            categoryMenu.Items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Category_NewCategoryEllipsis"),
                PromptAndCreateCategory));
            categoryMenu.Items.Add(new Separator());

            foreach (var option in viewModel.CategoryFilterOptions.Where(option => option.IsSelectable))
            {
                var label = option.Label;
                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                var categoryItem = new MenuItem
                {
                    Header = option.LeafDisplay,
                    ToolTip = option.PathDisplay,
                    IsCheckable = true,
                    IsChecked = selection.All(row =>
                        string.Equals(row.EffectiveCategoryLabel, label, StringComparison.OrdinalIgnoreCase))
                };
                categoryItem.Click += (_, __) => viewModel.SetCategoryForSelection(label);
                categoryMenu.Items.Add(categoryItem);
            }

            if (!(categoryMenu.Items[categoryMenu.Items.Count - 1] is Separator))
            {
                categoryMenu.Items.Add(new Separator());
            }

            categoryMenu.Items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Button_Clear"),
                () => viewModel.SetCategoryForSelection(null)));
            menu.Items.Add(categoryMenu);

            var typeMenu = new MenuItem
            {
                Header = ResourceProvider.GetString("LOCPlayAch_Common_Label_Type"),
                IsEnabled = selection.All(row => row.CanEditAssignments)
            };
            // Kept so a click can read every tick, not just its own: the menu stays open, and the
            // set the user leaves it in is what the whole selection takes.
            var typeItems = new List<MenuItem>();
            foreach (var categoryType in AchievementCategoryTypeHelper.AssignableCategoryTypes)
            {
                var captured = categoryType;
                var typeItem = new MenuItem
                {
                    Header = ManageAchievementsCategoryViewModel.GetCategoryTypeDisplayName(captured),
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    Tag = captured,
                    // The effective type, like the Category item above and the ticks in the details
                    // pane: reading the override alone left a provider-typed row showing the type
                    // unticked here and ticked there.
                    IsChecked = selection.All(row =>
                        AchievementCategoryTypeHelper.ParseValues(row.EffectiveCategoryTypeValue)
                            .Any(value => string.Equals(value, captured, StringComparison.OrdinalIgnoreCase)))
                };
                typeItem.Click += (_, __) => viewModel.SetCategoryTypesForSelection(
                    typeItems.Where(item => item.IsChecked).Select(item => item.Tag as string));
                typeItems.Add(typeItem);
                typeMenu.Items.Add(typeItem);
            }

            menu.Items.Add(typeMenu);

            menu.Items.Add(CreateMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Notes_Note"),
                () => EditNoteForSelectedRow(),
                selection.Count == 1 && selection[0].CanEditAssignments));

            menu.Items.Add(new Separator());

            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Common_Duplicate"),
                viewModel.DuplicateCommand));
            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_ManageAchievements_Editor_Revert"),
                viewModel.RevertCommand));
            menu.Items.Add(CreateCommandMenuItem(
                ResourceProvider.GetString("LOCPlayAch_Button_Delete"),
                viewModel.DeleteCommand));

            return menu;
        }

        private static MenuItem CreateMenuItem(string header, Action onClick, bool isEnabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = isEnabled };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        // Command is not bound: the menu is rebuilt per right-click, so the enabled state is read
        // once here rather than tracked.
        private static MenuItem CreateCommandMenuItem(string header, Common.RelayCommand command)
        {
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = command?.CanExecute(null) == true
            };
            item.Click += (_, __) => command?.Execute(null);
            return item;
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (CustomAchievementsGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(CustomAchievementsGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(CustomAchievementsGrid);
                }

                return false;
            }

            // The same row menu the mouse opens, so a controller is not left without the actions.
            if (FullscreenControllerNavigationService.IsSecondaryClickInput(input))
            {
                return TryOpenSelectedRowContextMenu();
            }

            return false;
        }

        private bool TryOpenSelectedRowContextMenu()
        {
            var item = CustomAchievementsGrid?.SelectedItem ?? CustomAchievementsGrid?.CurrentItem;
            if (item == null)
            {
                return false;
            }

            var row = CustomAchievementsGrid.ItemContainerGenerator.ContainerFromItem(item) as DataGridRow;
            return row != null && OpenContextMenuForRow(row, useControllerPlacement: true);
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                // Header provider row first (top-left); the IsVisible filter below drops it for
                // games that have real provider data.
                CustomProviderComboBox,
                AddCustomProviderButton,
                EditCustomProviderButton,
                AddButton,
                DuplicateButton,
                DeleteButton,
                ImportFileButton,
                ExportButton,
                ResetButton,
                CustomAchievementsGrid,
                AddRowFooterButton,
                CapstoneActionButton,
                CategoryPicker,
                TypeSelectionButton
            };

            return elements
                .Where(element => element != null && element.IsVisible && element.IsEnabled)
                .ToList();
        }

        private static bool TryResolveRowAndVariant(
            FrameworkElement element,
            out AchievementEditorRow row,
            out AchievementIconVariant variant)
        {
            row = element?.DataContext as AchievementEditorRow;
            variant = AchievementIconVariant.Unlocked;
            if (row == null)
            {
                return false;
            }

            var variantToken = (element as ButtonBase)?.CommandParameter as string;
            if (string.IsNullOrWhiteSpace(variantToken))
            {
                variantToken = element?.Tag as string;
            }

            if (string.Equals((variantToken ?? string.Empty).Trim(), "Locked", StringComparison.OrdinalIgnoreCase))
            {
                variant = AchievementIconVariant.Locked;
            }

            return true;
        }

        private static void SetIconPath(
            AchievementEditorRow row,
            AchievementIconVariant variant,
            string value)
        {
            if (row == null)
            {
                return;
            }

            if (variant == AchievementIconVariant.Locked)
            {
                row.LockedIconPath = value;
            }
            else
            {
                row.UnlockedIconPath = value;
            }
        }

        private static string BuildCandidateText(TextBox textBox, string input)
        {
            var current = textBox?.Text ?? string.Empty;
            input ??= string.Empty;
            var start = Math.Max(0, textBox?.SelectionStart ?? current.Length);
            var length = Math.Max(0, textBox?.SelectionLength ?? 0);
            if (start > current.Length)
            {
                start = current.Length;
            }

            if (start + length > current.Length)
            {
                length = current.Length - start;
            }

            return current.Remove(start, length).Insert(start, input);
        }

        private static bool IsValidNumericCandidate(string value, string mode)
        {
            var normalized = (value ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return true;
            }

            if (string.Equals(mode, "Percent", StringComparison.OrdinalIgnoreCase))
            {
                if (normalized.Count(c => c == '.') > 1 ||
                    !double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                {
                    return false;
                }

                return percent >= 0 && percent <= 100;
            }

            return normalized.All(char.IsDigit);
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
                if (!data.GetDataPresent(format))
                {
                    return null;
                }

                return data.GetData(format) as string;
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
    }
}
