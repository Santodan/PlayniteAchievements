using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reusable DataGrid control for displaying achievements with sorting,
    /// column visibility, and width persistence.
    /// </summary>
    public partial class AchievementDataGridControl : UserControl, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private ColumnWidthPersistenceService _columnPersistence;
        private bool _isAttached;

        private static readonly IReadOnlyDictionary<string, double> DefaultColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Game"] = 64,
                ["Achievement"] = 460,
                ["UnlockDate"] = 240,
                ["CategoryType"] = 210,
                ["CategoryLabel"] = 210,
                ["Rarity"] = 170,
                ["Points"] = 100
            };

        /// <summary>
        /// Identifies the ItemsSource dependency property.
        /// </summary>
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable<AchievementDisplayItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the achievement items to display.
        /// </summary>
        public IEnumerable<AchievementDisplayItem> ItemsSource
        {
            get => (IEnumerable<AchievementDisplayItem>)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        /// <summary>
        /// Identifies the RevealCommand dependency property.
        /// </summary>
        public static readonly DependencyProperty RevealCommandProperty =
            DependencyProperty.Register(nameof(RevealCommand), typeof(System.Windows.Input.ICommand),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        /// <summary>
        /// Gets or sets the command to execute when revealing a hidden achievement.
        /// The command parameter will be the AchievementDisplayItem.
        /// </summary>
        public System.Windows.Input.ICommand RevealCommand
        {
            get => (System.Windows.Input.ICommand)GetValue(RevealCommandProperty);
            set => SetValue(RevealCommandProperty, value);
        }

        /// <summary>
        /// Identifies the ColumnSettingsKey dependency property.
        /// Used to separate persisted column settings for different contexts.
        /// </summary>
        public static readonly DependencyProperty ColumnSettingsKeyProperty =
            DependencyProperty.Register(nameof(ColumnSettingsKey), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata("Default"));

        /// <summary>
        /// Gets or sets the key used to persist column settings separately per control instance.
        /// </summary>
        public string ColumnSettingsKey
        {
            get => (string)GetValue(ColumnSettingsKeyProperty);
            set => SetValue(ColumnSettingsKeyProperty, value);
        }

        /// <summary>
        /// Identifies the UseExternalSorting dependency property.
        /// When true, the control raises the Sorting event but does not perform in-memory sorting.
        /// </summary>
        public static readonly DependencyProperty UseExternalSortingProperty =
            DependencyProperty.Register(nameof(UseExternalSorting), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether sorting should be handled externally.
        /// When true, the Sorting event is raised but in-memory sorting is skipped.
        /// </summary>
        public bool UseExternalSorting
        {
            get => (bool)GetValue(UseExternalSortingProperty);
            set => SetValue(UseExternalSortingProperty, value);
        }

        /// <summary>
        /// Identifies the ShowGameColumn dependency property.
        /// When true, displays the Game column showing the associated game's icon/cover.
        /// </summary>
        public static readonly DependencyProperty ShowGameColumnProperty =
            DependencyProperty.Register(nameof(ShowGameColumn), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnColumnVisibilityChanged));

        /// <summary>
        /// Gets or sets whether to show the Game column.
        /// </summary>
        public bool ShowGameColumn
        {
            get => (bool)GetValue(ShowGameColumnProperty);
            set => SetValue(ShowGameColumnProperty, value);
        }

        /// <summary>
        /// Identifies the HideStatusColumn dependency property.
        /// When true, hides the Status column (checkmark/padlock).
        /// </summary>
        public static readonly DependencyProperty HideStatusColumnProperty =
            DependencyProperty.Register(nameof(HideStatusColumn), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnColumnVisibilityChanged));

        /// <summary>
        /// Gets or sets whether to hide the Status column.
        /// </summary>
        public bool HideStatusColumn
        {
            get => (bool)GetValue(HideStatusColumnProperty);
            set => SetValue(HideStatusColumnProperty, value);
        }

        /// <summary>
        /// Identifies the UseCoverImages dependency property.
        /// When true and ShowGameColumn is true, displays cover images instead of icons in the Game column.
        /// </summary>
        public static readonly DependencyProperty UseCoverImagesProperty =
            DependencyProperty.Register(nameof(UseCoverImages), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        /// <summary>
        /// Gets or sets whether to use cover images (instead of icons) in the Game column.
        /// </summary>
        public bool UseCoverImages
        {
            get => (bool)GetValue(UseCoverImagesProperty);
            set => SetValue(UseCoverImagesProperty, value);
        }

        /// <summary>
        /// Occurs when a column header is clicked for sorting.
        /// Subscribe to handle sorting externally when UseExternalSorting is true.
        /// </summary>
        public event EventHandler<DataGridSortingEventArgs> Sorting;

        /// <summary>
        /// Routed event raised when a row receives a right mouse button down.
        /// </summary>
        public static readonly RoutedEvent RowPreviewMouseRightButtonDownEvent =
            EventManager.RegisterRoutedEvent("RowPreviewMouseRightButtonDown", RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler), typeof(AchievementDataGridControl));

        /// <summary>
        /// Occurs when the right mouse button is pressed on a row.
        /// </summary>
        public event MouseButtonEventHandler RowPreviewMouseRightButtonDown
        {
            add => AddHandler(RowPreviewMouseRightButtonDownEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonDownEvent, value);
        }

        /// <summary>
        /// Routed event raised when a row receives a right mouse button up.
        /// </summary>
        public static readonly RoutedEvent RowPreviewMouseRightButtonUpEvent =
            EventManager.RegisterRoutedEvent("RowPreviewMouseRightButtonUp", RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler), typeof(AchievementDataGridControl));

        /// <summary>
        /// Occurs when the right mouse button is released on a row.
        /// </summary>
        public event MouseButtonEventHandler RowPreviewMouseRightButtonUp
        {
            add => AddHandler(RowPreviewMouseRightButtonUpEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonUpEvent, value);
        }

        public AchievementDataGridControl()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // When control becomes visible, ensure column visibility is correct
            // This handles the case where persisted visibility was applied while control was hidden
            if ((bool)e.NewValue && _isAttached)
            {
                UpdateColumnVisibility();
            }
        }

        private static void OnColumnVisibilityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateColumnVisibility();
            }
        }

        private void UpdateColumnVisibility()
        {
            if (AchievementsDataGrid == null || AchievementsDataGrid.Columns == null)
            {
                return;
            }

            // Update Status column visibility - force collapsed when HideStatusColumn is true
            var statusColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "StatusColumn") as DataGridTemplateColumn;
            if (statusColumn != null)
            {
                statusColumn.Visibility = HideStatusColumn ? Visibility.Collapsed : Visibility.Visible;
            }

            // Update Game column visibility - force collapsed when ShowGameColumn is false
            var gameColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "GameColumn") as DataGridTemplateColumn;
            if (gameColumn != null)
            {
                gameColumn.Visibility = ShowGameColumn ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_isAttached)
            {
                return;
            }

            AttachColumnPersistence();
            // Must come AFTER AttachColumnPersistence so dependency property settings
            // override the persisted visibility settings
            UpdateColumnVisibility();
            _isAttached = true;
        }

        private void AttachColumnPersistence()
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings == null)
            {
                return;
            }

            _columnPersistence = new ColumnWidthPersistenceService(
                AchievementsDataGrid,
                Logger,
                () => GetMergedWidths(settings),
                map => SetWidthsByKey(settings, map),
                () => settings.Persisted.DataGridColumnVisibility,
                map => settings.Persisted.DataGridColumnVisibility = map,
                () => SavePluginSettings(settings),
                DefaultColumnWidthSeeds);

            // Exclude Game column from visibility toggle when not shown
            // Exclude Status column from visibility toggle when hidden
            if (!ShowGameColumn)
            {
                _columnPersistence.ExcludedVisibilityKeys.Add("Game");
            }
            if (HideStatusColumn)
            {
                _columnPersistence.ExcludedVisibilityKeys.Add("Status");
            }

            _columnPersistence.Attach();
        }

        private Dictionary<string, double> GetMergedWidths(PlayniteAchievementsSettings settings)
        {
            var merged = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            // Use key-specific widths if available
            var keyMap = GetWidthsByKey(settings);
            if (keyMap != null)
            {
                foreach (var pair in keyMap)
                {
                    if (IsValidWidth(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            // Fall back to single game widths for any missing keys
            var singleGameMap = settings?.Persisted?.SingleGameColumnWidths;
            if (singleGameMap != null)
            {
                foreach (var pair in singleGameMap)
                {
                    if (!merged.ContainsKey(pair.Key) && IsValidWidth(pair.Value))
                    {
                        merged[pair.Key] = pair.Value;
                    }
                }
            }

            return merged;
        }

        private Dictionary<string, double> GetWidthsByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            return ColumnSettingsKey switch
            {
                "DesktopTheme" => settings.Persisted.DesktopThemeColumnWidths,
                "SingleGame" => settings.Persisted.SingleGameColumnWidths,
                "Sidebar" => settings.Persisted.SidebarAchievementColumnWidths,
                _ => settings.Persisted.SingleGameColumnWidths
            };
        }

        private void SetWidthsByKey(PlayniteAchievementsSettings settings, Dictionary<string, double> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "DesktopTheme":
                    settings.Persisted.DesktopThemeColumnWidths = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnWidths = map;
                    break;
                case "Sidebar":
                    settings.Persisted.SidebarAchievementColumnWidths = map;
                    break;
                default:
                    settings.Persisted.SingleGameColumnWidths = map;
                    break;
            }
        }

        private static bool IsValidWidth(double width)
        {
            return !double.IsNaN(width) && !double.IsInfinity(width) && width > 0;
        }

        private static void SavePluginSettings(PlayniteAchievementsSettings settings)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (plugin == null || settings == null)
            {
                return;
            }

            try
            {
                plugin.SavePluginSettings(settings);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Failed to persist column layout settings.");
            }
        }

        private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // Raise the Sorting event to allow external handling
            Sorting?.Invoke(this, e);

            if (e.Handled || UseExternalSorting)
            {
                return;
            }

            // Default: in-memory sorting
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e);
            if (sortDirection == null)
            {
                return;
            }

            // Sort in-memory by reordering ItemsSource
            var items = ItemsSource?.ToList();
            if (items == null || items.Count == 0)
            {
                return;
            }

            var sortPath = e.Column.SortMemberPath;
            var sorted = sortDirection.Value == ListSortDirection.Ascending
                ? items.OrderBy(x => GetSortValue(x, sortPath))
                : items.OrderByDescending(x => GetSortValue(x, sortPath));

            ItemsSource = sorted.ToList();
        }

        private static object GetSortValue(AchievementDisplayItem item, string sortPath)
        {
            if (item == null || string.IsNullOrWhiteSpace(sortPath))
            {
                return null;
            }

            return sortPath switch
            {
                "DisplayName" => item.DisplayName ?? string.Empty,
                "UnlockTime" => item.UnlockTime,
                "GlobalPercent" => item.GlobalPercent,
                "CategoryType" => item.CategoryType ?? string.Empty,
                "CategoryLabel" => item.CategoryLabel ?? string.Empty,
                "TrophyType" => item.TrophyType ?? string.Empty,
                "Points" => item.Points,
                _ => null
            };
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is DataGridRow row && row.DataContext is AchievementDisplayItem item)
            {
                if (item.CanReveal)
                {
                    var command = RevealCommand;
                    if (command != null && command.CanExecute(item))
                    {
                        command.Execute(item);
                    }
                    else
                    {
                        item.ToggleReveal();
                    }
                    e.Handled = true;
                }
            }
        }

        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, e.ChangedButton)
            {
                RoutedEvent = RowPreviewMouseRightButtonDownEvent,
                Source = sender
            });
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, e.ChangedButton)
            {
                RoutedEvent = RowPreviewMouseRightButtonUpEvent,
                Source = sender
            });
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

        /// <summary>
        /// Gets the internal DataGrid for direct access when needed.
        /// Used for scroll reset and other operations that require direct DataGrid access.
        /// </summary>
        public DataGrid InternalDataGrid => AchievementsDataGrid;

        /// <summary>
        /// Sets the sort indicator on a specific column, clearing others.
        /// Used for external sorting mode where the parent controls sort order.
        /// </summary>
        /// <param name="sortMemberPath">The SortMemberPath of the column to set the indicator on.</param>
        /// <param name="direction">The sort direction, or null to clear all indicators.</param>
        public void SetSortIndicator(string sortMemberPath, ListSortDirection? direction)
        {
            if (AchievementsDataGrid == null || AchievementsDataGrid.Columns == null)
            {
                return;
            }

            foreach (var column in AchievementsDataGrid.Columns)
            {
                column.SortDirection = null;
            }

            if (direction == null || string.IsNullOrEmpty(sortMemberPath))
            {
                return;
            }

            var targetColumn = AchievementsDataGrid.Columns
                .FirstOrDefault(c => string.Equals(c.SortMemberPath, sortMemberPath, StringComparison.Ordinal));
            if (targetColumn != null)
            {
                targetColumn.SortDirection = direction;
            }
        }

        /// <summary>
        /// Refreshes column persistence settings from storage.
        /// </summary>
        public void Refresh()
        {
            _columnPersistence?.Refresh();
        }

        public void Dispose()
        {
            if (!_isAttached)
            {
                return;
            }

            _columnPersistence?.Dispose();
            _columnPersistence = null;
            _isAttached = false;
        }
    }
}
