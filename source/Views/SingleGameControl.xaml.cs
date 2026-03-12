using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Controls;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    public partial class SingleGameControl : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;

        public SingleGameControl()
        {
            InitializeComponent();
        }

        public SingleGameControl(
            Guid gameId,
            AchievementService achievementService,
            IPlayniteAPI playniteApi,
            ILogger logger,
            PlayniteAchievementsSettings settings)
        {
            InitializeComponent();

            _settings = settings;
            _logger = logger;
            DataContext = new SingleGameControlModel(gameId, achievementService, playniteApi, logger, settings);
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            }

            // Subscribe to settings saved event to refresh when credentials change
            PlayniteAchievementsPlugin.SettingsSaved += Plugin_SettingsSaved;
        }

        private void Plugin_SettingsSaved(object sender, EventArgs e)
        {
            RefreshView();
            AchievementsDataGridControl?.Refresh();
            UpdateDefaultSortIndicator();
        }

        private SingleGameControlModel ViewModel => DataContext as SingleGameControlModel;

        public string WindowTitle => ViewModel?.GameName != null
            ? $"{ViewModel.GameName} - Achievements"
            : "Achievements";

        public void RefreshView()
        {
            ViewModel?.RefreshView();
        }

        public void Cleanup()
        {
            PlayniteAchievementsPlugin.SettingsSaved -= Plugin_SettingsSaved;
            if (ViewModel != null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }
            ViewModel?.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateDefaultSortIndicator();
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == nameof(SingleGameControlModel.HasCustomAchievementOrder))
            {
                Dispatcher.BeginInvoke(new Action(UpdateDefaultSortIndicator));
            }
        }

        private void UpdateDefaultSortIndicator()
        {
            if (ViewModel?.HasCustomAchievementOrder == true)
            {
                AchievementsDataGridControl?.SetSortIndicator(null, null);
                return;
            }

            AchievementsDataGridControl?.SetSortIndicator("UnlockTime", ListSortDirection.Descending);
        }

        private void OnGridSorting(object sender, DataGridSortingEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null)
            {
                return;
            }

            ViewModel.SortDataGrid(e.Column.SortMemberPath, sortDirection.Value);
            e.Handled = true;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearSearch();
        }

        private void CategoryTypeFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CategoryTypeFilterSelectionButton,
                ViewModel.CategoryTypeFilterOptions,
                option => ViewModel.IsCategoryTypeFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryTypeFilterSelected(option, isSelected));
        }

        private void CategoryLabelFilterSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            OpenMultiSelectFilterContextMenu(
                CategoryLabelFilterSelectionButton,
                ViewModel.CategoryLabelFilterOptions,
                option => ViewModel.IsCategoryLabelFilterSelected(option),
                (option, isSelected) => ViewModel.SetCategoryLabelFilterSelected(option, isSelected));
        }

        private void OpenMultiSelectFilterContextMenu(
            Button button,
            System.Collections.Generic.IEnumerable<string> options,
            Func<string, bool> isSelected,
            Action<string, bool> setSelection)
        {
            if (button == null || isSelected == null || setSelection == null)
            {
                return;
            }

            var menu = button.ContextMenu;
            if (menu == null)
            {
                return;
            }

            menu.Items.Clear();
            if (options == null)
            {
                return;
            }

            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in options)
            {
                if (string.IsNullOrWhiteSpace(option))
                {
                    continue;
                }

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
            menu.IsOpen = true;
        }
    }
}
