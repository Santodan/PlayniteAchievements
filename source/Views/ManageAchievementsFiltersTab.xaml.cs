using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK.Events;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public partial class ManageAchievementsFiltersTab : UserControl, IFullscreenControllerNavigable
    {
        public ManageAchievementsFiltersTab(ManageAchievementsFiltersViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private ManageAchievementsFiltersViewModel ViewModel => DataContext as ManageAchievementsFiltersViewModel;

        public void RefreshData()
        {
            ViewModel?.ReloadData();
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (FiltersDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(FiltersDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(FiltersDataGrid);
                }

                return false;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>
            {
                ResetFiltersButton,
                SearchTextBox,
                ClearSearchButton,
                FilterTypeSelectionButton,
                CategoryLabelFilterSelectionButton,
                StateFilterComboBox,
                SelectAllButton,
                DeselectAllButton,
                FiltersDataGrid
            };

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

        private void FiltersDataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
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
            if (!(row?.DataContext is ManageAchievementsFilterItem item))
            {
                return;
            }

            if (item.CanReveal)
            {
                item.ToggleReveal();
            }

            e.Handled = true;
        }

        private void ResetFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ResetFilters();
            e.Handled = true;
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetVisibleRowsFiltered(true);
            e.Handled = true;
        }

        private void DeselectAllButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.SetVisibleRowsFiltered(false);
            e.Handled = true;
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
