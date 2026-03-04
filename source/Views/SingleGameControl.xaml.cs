using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using Playnite.SDK;

namespace PlayniteAchievements.Views
{
    public partial class SingleGameControl : UserControl
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private ColumnWidthPersistenceService _columnPersistence;

        private static readonly IReadOnlyDictionary<string, double> DefaultColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Achievement"] = 460,
                ["UnlockDate"] = 240,
                ["Rarity"] = 170,
                ["Points"] = 100
            };

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
            _columnPersistence?.Refresh();
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
            _columnPersistence?.Dispose();
            _columnPersistence = null;
            ViewModel?.Dispose();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_columnPersistence != null)
            {
                return;
            }

            _columnPersistence = new ColumnWidthPersistenceService(
                AchievementsDataGrid,
                _logger,
                () => GetMergedWidths(),
                map => _settings.Persisted.SingleGameColumnWidths = map,
                () => _settings.Persisted.DataGridColumnVisibility,
                map => _settings.Persisted.DataGridColumnVisibility = map,
                SavePluginSettings,
                DefaultColumnWidthSeeds);

            _columnPersistence.Attach();
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
            if (AchievementsDataGrid == null || AchievementsDataGrid.Columns == null)
            {
                return;
            }

            foreach (var column in AchievementsDataGrid.Columns)
            {
                column.SortDirection = null;
            }

            if (ViewModel?.HasCustomAchievementOrder == true)
            {
                return;
            }

            var unlockColumn = AchievementsDataGrid.Columns
                .FirstOrDefault(c => string.Equals(c.SortMemberPath, "UnlockTime", StringComparison.Ordinal));
            if (unlockColumn != null)
            {
                unlockColumn.SortDirection = ListSortDirection.Descending;
            }
        }

        private Dictionary<string, double> GetMergedWidths()
        {
            var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Legacy fallback
            var legacyMap = _settings?.Persisted?.DataGridColumnWidths;
            if (legacyMap != null)
            {
                foreach (var pair in legacyMap)
                {
                    if (IsValidWidth(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            var map = _settings?.Persisted?.SingleGameColumnWidths;
            if (map != null)
            {
                foreach (var pair in map)
                {
                    if (IsValidWidth(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            return merged;
        }

        private static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        private void SavePluginSettings()
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin == null || _settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(_settings);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist single-game column layout settings.");
            }
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is AchievementDisplayItem item)
            {
                if (item.CanReveal)
                {
                    ViewModel?.RevealAchievementCommand.Execute(item);
                    e.Handled = true;
                }
            }
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (ViewModel == null) return;

            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null) return;

            ViewModel.SortDataGrid(e.Column.SortMemberPath, sortDirection.Value);
        }

        private void DataGridColumnMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid grid))
            {
                return;
            }

            var row = ItemsControl.ContainerFromElement(grid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row != null)
            {
                return;
            }

            e.Handled = true;

            var menu = _columnPersistence?.BuildColumnVisibilityMenu();
            if (menu == null || menu.Items.Count == 0)
            {
                return;
            }

            menu.Placement = PlacementMode.RelativePoint;
            menu.PlacementTarget = grid;
            menu.HorizontalOffset = e.GetPosition(grid).X;
            menu.VerticalOffset = e.GetPosition(grid).Y;
            menu.IsOpen = true;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            ViewModel?.ClearSearch();
        }
    }
}


