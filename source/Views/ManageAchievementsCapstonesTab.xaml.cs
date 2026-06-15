using Playnite.SDK;
using Playnite.SDK.Events;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PlayniteAchievements.Views
{
    public partial class ManageAchievementsCapstonesTab : UserControl, IFullscreenControllerNavigable
    {
        private readonly CapstoneViewModel _viewModel;

        public event EventHandler CapstoneChanged;

        public ManageAchievementsCapstonesTab(
            Guid gameId,
            AchievementOverridesService achievementOverridesService,
            ManageAchievementsDataSnapshotProvider gameDataSnapshotProvider,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            _viewModel = new CapstoneViewModel(gameId, achievementOverridesService, gameDataSnapshotProvider, playniteApi, logger, settings);
            DataContext = _viewModel;
            InitializeComponent();
            _viewModel.CapstoneChanged += ViewModel_CapstoneChanged;
        }

        public void RefreshData()
        {
            _viewModel?.ReloadData();
        }

        public void Cleanup()
        {
            if (_viewModel != null)
            {
                _viewModel.CapstoneChanged -= ViewModel_CapstoneChanged;
            }
        }

        private void ViewModel_CapstoneChanged(object sender, EventArgs e)
        {
            CapstoneChanged?.Invoke(this, EventArgs.Empty);
        }

        private void MarkerCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is CapstoneOptionItem item)
            {
                if (item.IsCurrentMarker)
                {
                    _viewModel?.ClearMarker();
                }
                else
                {
                    _viewModel?.SetMarker(item);
                }
            }

            // Marker interactions should not trigger row-level behaviors.
            e.Handled = true;
        }

        private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;

            if (source is CheckBox || VisualTreeHelpers.FindVisualParent<CheckBox>(source) != null)
            {
                return;
            }

            var cell = VisualTreeHelpers.FindVisualParent<DataGridCell>(source);
            if (cell?.Column == null)
            {
                return;
            }

            if (cell.Column.DisplayIndex <= 2)
            {
                // Marker/badge/status column interactions should not reveal hidden achievements.
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(source);
            if (row == null)
            {
                return;
            }

            if (row.DataContext is CapstoneOptionItem item)
            {
                _viewModel?.ToggleReveal(item);
            }
        }

        public bool HandleFullscreenControllerInput(ControllerInput input)
        {
            if (AchievementsDataGrid?.IsKeyboardFocusWithin != true)
            {
                return false;
            }

            if (FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(AchievementsDataGrid))
            {
                if (FullscreenControllerNavigationService.IsAcceptInput(input))
                {
                    return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(AchievementsDataGrid);
                }

                return false;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            return GetVisibleControllerElements(
                SearchTextBox,
                ClearSearchButton,
                AchievementsDataGrid);
        }

        private static IList<UIElement> GetVisibleControllerElements(params UIElement[] elements)
        {
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

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null)
            {
                return;
            }

            var column = e.Column;
            var items = _viewModel.AchievementOptions.ToList();
            items.Sort((a, b) => column.SortMemberPath switch
            {
                "GlobalPercent" => sortDirection.Value == ListSortDirection.Ascending
                    ? a.RaritySortValue.CompareTo(b.RaritySortValue)
                    : b.RaritySortValue.CompareTo(a.RaritySortValue),
                "RaritySortValue" => sortDirection.Value == ListSortDirection.Ascending
                    ? a.RaritySortValue.CompareTo(b.RaritySortValue)
                    : b.RaritySortValue.CompareTo(a.RaritySortValue),
                "Points" => sortDirection.Value == ListSortDirection.Ascending
                    ? a.Points.CompareTo(b.Points)
                    : b.Points.CompareTo(a.Points),
                _ => 0
            });

            _viewModel.AchievementOptions.Clear();
            foreach (var item in items)
            {
                _viewModel.AchievementOptions.Add(item);
            }
        }
    }
}
