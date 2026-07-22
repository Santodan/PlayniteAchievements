using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Reusable DataGrid control for displaying achievements with sorting,
    /// column visibility, and width persistence.
    /// </summary>
    public partial class AchievementDataGridControl : UserControl, IDisposable
    {
        private static readonly ILogger Logger = LogManager.GetLogger();
        private DataGridColumnLayoutService _columnPersistence;
        private bool _isAttached;
        private const double CompactColumnMinWidth = 22;
        private const double DefaultStatusColumnWidth = 40;
        private const double DefaultIconColumnWidth = 72;
        private const double DefaultGameImageColumnWidth = 96;
        private const double DefaultTrophyIconColumnWidth = 72;
        private const double MinimumStatusColumnWidth = 28;
        private const double MinimumGameImageColumnWidth = 56;
        private const double MaximumStatusColumnWidth = 96;
        private const double MaximumGameImageColumnWidth = 240;
        private const double LegacyTrophyColumnWidth = 100;
        private const double DefaultTrophyColumnWidth = 44;
        private const double LegacyRarityTierColumnWidth = 90;
        private const double DefaultRarityTierColumnWidth = 44;

        private static readonly IReadOnlyDictionary<string, double> DefaultImageColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = DefaultStatusColumnWidth,
                ["Icon"] = DefaultIconColumnWidth,
                ["Game"] = DefaultGameImageColumnWidth,
                ["Trophy"] = DefaultTrophyIconColumnWidth,
                ["RarityTier"] = DefaultTrophyIconColumnWidth
            };

        private static readonly IReadOnlyDictionary<string, double> LegacyImageColumnRuntimeDefaults =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = 36,
                ["Icon"] = 64,
                ["Game"] = 64
            };

        // Defaults are applied only when a saved layout is missing a key.
        private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, bool>> DefaultVisibilityByColumnSettingsKey =
            new Dictionary<string, IReadOnlyDictionary<string, bool>>(StringComparer.OrdinalIgnoreCase)
            {
                ["Default"] = CreateAchievementVisibility(),
                ["SingleGame"] = CreateAchievementVisibility(),
                ["DesktopTheme"] = CreateAchievementVisibility(),
                ["OverviewSelectedGameAchievements"] = CreateAchievementVisibility(),
                ["OverviewGame"] = CreateAchievementVisibility(),
                ["OverviewRecentAchievements"] = CreateAchievementVisibility(status: false, game: true),
                ["Overview"] = CreateAchievementVisibility(status: false, game: true),
                ["StartPageAchievements"] = CreateAchievementVisibility(
                    status: false,
                    game: false,
                    unlockDate: false,
                    categoryType: false,
                    categoryLabel: false,
                    trophy: false,
                    rarity: false,
                    rarityTier: true,
                    collectionScore: false,
                    prestigeScore: false,
                    points: false)
            };

        private static readonly IReadOnlyDictionary<string, double> DefaultColumnWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = DefaultStatusColumnWidth,
                ["Icon"] = 84,
                ["Game"] = 64,
                ["Achievement"] = 460,
                ["Title"] = 260,
                ["UnlockDate"] = 240,
                ["CategoryType"] = 210,
                ["CategoryLabel"] = 210,
                ["Trophy"] = DefaultTrophyColumnWidth,
                ["Rarity"] = 170,
                ["RarityTier"] = DefaultRarityTierColumnWidth,
                ["RarityPercent"] = 120,
                ["CollectionScore"] = 110,
                ["PrestigeScore"] = 110,
                ["Points"] = 100
            };

        private static readonly IReadOnlyDictionary<string, double> LegacyOverviewAchievementWidthSeeds =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["Achievement"] = 520,
                ["UnlockDate"] = 230,
                ["CategoryType"] = 200,
                ["CategoryLabel"] = 200,
                ["Rarity"] = 170,
                ["Points"] = 120
            };

        private static IReadOnlyDictionary<string, bool> CreateAchievementVisibility(
            bool status = true,
            bool icon = true,
            bool achievement = true,
            bool title = false,
            bool note = false,
            bool game = false,
            bool unlockDate = true,
            bool categoryType = false,
            bool categoryLabel = false,
            bool trophy = false,
            bool rarity = true,
            bool rarityTier = false,
            bool rarityPercent = false,
            bool collectionScore = false,
            bool prestigeScore = false,
            bool points = false)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                ["Status"] = status,
                ["Icon"] = icon,
                ["Achievement"] = achievement,
                ["Title"] = title,
                ["Note"] = note,
                ["Game"] = game,
                ["UnlockDate"] = unlockDate,
                ["CategoryType"] = categoryType,
                ["CategoryLabel"] = categoryLabel,
                ["Trophy"] = trophy,
                ["Rarity"] = rarity,
                ["RarityTier"] = rarityTier,
                ["RarityPercent"] = rarityPercent,
                ["CollectionScore"] = collectionScore,
                ["PrestigeScore"] = prestigeScore,
                ["Points"] = points
            };
        }

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

        public static readonly DependencyProperty SortScopeProperty =
            DependencyProperty.Register(nameof(SortScope), typeof(AchievementSortScope),
                typeof(AchievementDataGridControl), new PropertyMetadata(AchievementSortScope.GameAchievements));

        public AchievementSortScope SortScope
        {
            get => (AchievementSortScope)GetValue(SortScopeProperty);
            set => SetValue(SortScopeProperty, value);
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
        /// Identifies the DataGridMaxHeight dependency property.
        /// When set, limits the maximum height of the internal DataGrid.
        /// </summary>
        public static readonly DependencyProperty DataGridMaxHeightProperty =
            DependencyProperty.Register(nameof(DataGridMaxHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(PersistedSettings.DefaultAchievementDataGridMaxHeight));

        /// <summary>
        /// Gets or sets the maximum height of the internal DataGrid.
        /// Default is PersistedSettings.DefaultAchievementDataGridMaxHeight.
        /// </summary>
        public double DataGridMaxHeight
        {
            get => (double)GetValue(DataGridMaxHeightProperty);
            set => SetValue(DataGridMaxHeightProperty, value);
        }

        /// <summary>
        /// Identifies the FixedRowHeight dependency property.
        /// Null or NaN keeps the existing automatic row sizing.
        /// </summary>
        public static readonly DependencyProperty FixedRowHeightProperty =
            DependencyProperty.Register(nameof(FixedRowHeight), typeof(double?),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnRowSizingChanged));

        /// <summary>
        /// Gets or sets a fixed DataGrid row height. Null keeps automatic sizing.
        /// </summary>
        public double? FixedRowHeight
        {
            get => (double?)GetValue(FixedRowHeightProperty);
            set => SetValue(FixedRowHeightProperty, value);
        }
        public static readonly DependencyProperty IsTextWrapEnabledProperty =
            DependencyProperty.Register(nameof(IsTextWrapEnabled), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        public bool IsTextWrapEnabled
        {
            get => (bool)GetValue(IsTextWrapEnabledProperty);
            set => SetValue(IsTextWrapEnabledProperty, value);
        }

        /// <summary>
        /// Identifies the AllowLayoutPersistence dependency property.
        /// When false, the control reads persisted layout state but never writes changes back.
        /// </summary>
        public static readonly DependencyProperty AllowLayoutPersistenceProperty =
            DependencyProperty.Register(nameof(AllowLayoutPersistence), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether column widths and visibility changes can be persisted.
        /// </summary>
        public bool AllowLayoutPersistence
        {
            get => (bool)GetValue(AllowLayoutPersistenceProperty);
            set => SetValue(AllowLayoutPersistenceProperty, value);
        }

        /// <summary>
        /// Identifies the AllowColumnVisibilityMenu dependency property.
        /// When false, the right-click column visibility menu is suppressed.
        /// </summary>
        public static readonly DependencyProperty AllowColumnVisibilityMenuProperty =
            DependencyProperty.Register(nameof(AllowColumnVisibilityMenu), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true));

        /// <summary>
        /// Gets or sets whether the right-click column visibility menu is enabled.
        /// </summary>
        public bool AllowColumnVisibilityMenu
        {
            get => (bool)GetValue(AllowColumnVisibilityMenuProperty);
            set => SetValue(AllowColumnVisibilityMenuProperty, value);
        }

        public static readonly DependencyProperty DelayInitialRenderUntilNormalizedProperty =
            DependencyProperty.Register(nameof(DelayInitialRenderUntilNormalized), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false, OnDelayInitialRenderUntilNormalizedChanged));

        public bool DelayInitialRenderUntilNormalized
        {
            get => (bool)GetValue(DelayInitialRenderUntilNormalizedProperty);
            set => SetValue(DelayInitialRenderUntilNormalizedProperty, value);
        }

        public static readonly DependencyProperty ShowColumnHeadersProperty =
            DependencyProperty.Register(nameof(ShowColumnHeaders), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(true, OnShowColumnHeadersChanged));

        public bool ShowColumnHeaders
        {
            get => (bool)GetValue(ShowColumnHeadersProperty);
            set => SetValue(ShowColumnHeadersProperty, value);
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
            DataContextChanged += OnDataContextChanged;
            Unloaded += OnUnloaded;
            UpdateColumnHeadersVisibility();
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
                SetResizableColumnVisibility(
                    statusColumn,
                    !HideStatusColumn,
                    DefaultStatusColumnWidth,
                    MinimumStatusColumnWidth,
                    MaximumStatusColumnWidth);
            }

            // Update Game column visibility - force collapsed when ShowGameColumn is false
            var gameColumn = AchievementsDataGrid.Columns.FirstOrDefault(c => c.GetValue(FrameworkElement.NameProperty) as string == "GameColumn") as DataGridTemplateColumn;
            if (gameColumn != null)
            {
                SetResizableColumnVisibility(
                    gameColumn,
                    ShowGameColumn,
                    DefaultGameImageColumnWidth,
                    MinimumGameImageColumnWidth,
                    MaximumGameImageColumnWidth);
            }
        }

        private static void OnShowColumnHeadersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateColumnHeadersVisibility();
            }
        }

        private static void OnDelayInitialRenderUntilNormalizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control && control._columnPersistence != null)
            {
                control._columnPersistence.DelayInitialRenderUntilNormalized = e.NewValue is bool value && value;
            }
        }

        private void UpdateColumnHeadersVisibility()
        {
            if (AchievementsDataGrid != null)
            {
                AchievementsDataGrid.HeadersVisibility = ShowColumnHeaders
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            }
        }

        private static void SetResizableColumnVisibility(
            DataGridColumn column,
            bool isVisible,
            double defaultWidth,
            double minWidth,
            double maxWidth)
        {
            if (column == null)
            {
                return;
            }

            if (isVisible)
            {
                column.Visibility = Visibility.Visible;
                column.MinWidth = minWidth;
                column.MaxWidth = maxWidth;
                if (column.Width.IsAbsolute && column.Width.Value <= 0)
                {
                    column.Width = new DataGridLength(defaultWidth, DataGridLengthUnitType.Pixel);
                }

                return;
            }

            column.Visibility = Visibility.Collapsed;
            column.MinWidth = 0;
            column.MaxWidth = 0;
            column.Width = new DataGridLength(0, DataGridLengthUnitType.Pixel);
        }

        private static void OnRowSizingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.UpdateRealizedRowHeights();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateColumnVisibility();
            UpdateColumnHeadersVisibility();
            UpdateRealizedRowHeights();

            if (_isAttached)
            {
                return;
            }

            AttachColumnPersistence();
            // Column visibility is now handled by ForcedCollapsedKeys during Attach()
            _isAttached = true;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            UpdateRealizedRowHeights();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
        }

        private void AchievementsDataGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            ApplyFixedRowHeight(e.Row);
        }

        private void UpdateRealizedRowHeights()
        {
            if (AchievementsDataGrid == null)
            {
                return;
            }

            foreach (var item in AchievementsDataGrid.Items)
            {
                if (AchievementsDataGrid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                {
                    ApplyFixedRowHeight(row);
                }
            }
        }

        private void ApplyFixedRowHeight(DataGridRow row)
        {
            if (row == null)
            {
                return;
            }

            var fixedHeight = ResolveFixedRowHeight();
            if (fixedHeight.HasValue)
            {
                row.Height = fixedHeight.Value;
                row.MinHeight = fixedHeight.Value;
                return;
            }

            row.ClearValue(FrameworkElement.HeightProperty);
            row.ClearValue(FrameworkElement.MinHeightProperty);
        }

        private double? ResolveFixedRowHeight()
        {
            var height = FixedRowHeight;
            if (!height.HasValue ||
                double.IsNaN(height.Value) ||
                double.IsInfinity(height.Value) ||
                height.Value <= 0)
            {
                return null;
            }

            return Math.Max(PersistedSettings.MinimumGridRowHeight, height.Value);
        }

        private void AttachColumnPersistence()
        {
            var settings = PlayniteAchievementsPlugin.Instance?.Settings;
            if (settings == null)
            {
                return;
            }

            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetAlignmentsByKey(settings));
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetCellVerticalAlignmentsByKey(settings));
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(
                AchievementsDataGrid,
                () => GetHeaderAlignmentsByKey(settings));

            _columnPersistence = new DataGridColumnLayoutService(
                AchievementsDataGrid,
                Logger,
                () => GetMergedWidths(settings),
                map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetWidthsByKey(settings, map);
                    }
                },
                () => GetVisibilityMap(settings),
                map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetVisibilityByKey(settings, map);
                    }
                },
                () =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SavePluginSettings(settings);
                    }
                },
                defaultWidthSeeds: DefaultImageColumnWidthSeeds,
                getOrder: () => GetOrderMap(settings),
                setOrder: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetOrderByKey(settings, map);
                    }
                },
                getCellAlignments: () => GetAlignmentMap(settings),
                setCellAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetAlignmentsByKey(settings, map);
                    }
                },
                getDefaultCellAlignment: () => settings.Persisted?.GridCellAlignment ?? GridAlignment.Left,
                getCellVerticalAlignments: () => GetCellVerticalAlignmentMap(settings),
                setCellVerticalAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetCellVerticalAlignmentsByKey(settings, map);
                    }
                },
                getDefaultCellVerticalAlignment: () => settings.Persisted?.GridCellVerticalAlignment ?? GridVerticalAlignment.Center,
                getHeaderHorizontalAlignments: () => GetHeaderAlignmentMap(settings),
                setHeaderHorizontalAlignments: map =>
                {
                    if (AllowLayoutPersistence)
                    {
                        SetHeaderAlignmentsByKey(settings, map);
                    }
                },
                getDefaultHeaderHorizontalAlignment: () => settings.Persisted?.GridColumnHeaderAlignment ?? GridAlignment.Center,
                applyCellAlignments: () => DataGridAlignmentBehavior.Refresh(AchievementsDataGrid),
                isRuntimeDefaultWidth: IsLegacyImageColumnRuntimeDefaultWidth);
            _columnPersistence.DelayInitialRenderUntilNormalized = DelayInitialRenderUntilNormalized;

            // Force collapse Game column when not shown (prevents flicker by applying during persistence)
            // Also exclude from visibility toggle menu
            if (!ShowGameColumn)
            {
                _columnPersistence.ForcedCollapsedKeys.Add("Game");
                _columnPersistence.ExcludedVisibilityKeys.Add("Game");
            }
            // Force collapse Status column when hidden (prevents flicker by applying during persistence)
            // Also exclude from visibility toggle menu
            if (HideStatusColumn)
            {
                _columnPersistence.ForcedCollapsedKeys.Add("Status");
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

            if (ShouldUseSingleGameWidthFallback())
            {
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
            }

            return merged;
        }

        private bool ShouldUseSingleGameWidthFallback()
        {
            switch (ColumnSettingsKey)
            {
                case "OverviewRecentAchievements":
                case "Overview":
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                case "StartPageAchievements":
                    return false;
                default:
                    return true;
            }
        }

        private Dictionary<string, bool> GetVisibilityMap(PlayniteAchievementsSettings settings)
        {
            var map = GetVisibilityByKey(settings);
            map = ApplyContextDefaultVisibility(settings, map);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, bool>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, bool> ApplyContextDefaultVisibility(
            PlayniteAchievementsSettings settings,
            Dictionary<string, bool> map)
        {
            var defaults = GetDefaultVisibility(ColumnSettingsKey);
            return defaults != null
                ? ApplyVisibilityDefaults(settings, map, defaults)
                : map;
        }

        private static IReadOnlyDictionary<string, bool> GetDefaultVisibility(string columnSettingsKey)
        {
            if (!string.IsNullOrWhiteSpace(columnSettingsKey) &&
                DefaultVisibilityByColumnSettingsKey.TryGetValue(columnSettingsKey, out var defaults))
            {
                return defaults;
            }

            return DefaultVisibilityByColumnSettingsKey.TryGetValue("Default", out var fallback)
                ? fallback
                : null;
        }

        private Dictionary<string, bool> ApplyVisibilityDefaults(
            PlayniteAchievementsSettings settings,
            Dictionary<string, bool> map,
            IReadOnlyDictionary<string, bool> defaults)
        {
            if (defaults == null || defaults.Count == 0)
            {
                return map;
            }

            if (map == null)
            {
                map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                if (AllowLayoutPersistence)
                {
                    SetVisibilityByKey(settings, map);
                }
            }

            foreach (var pair in defaults)
            {
                if (!map.ContainsKey(pair.Key))
                {
                    map[pair.Key] = pair.Value;
                }
            }

            return map;
        }

        private Dictionary<string, int> GetOrderMap(PlayniteAchievementsSettings settings)
        {
            var map = GetOrderByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, GridAlignment> GetAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, GridVerticalAlignment> GetCellVerticalAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetCellVerticalAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridVerticalAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, GridAlignment> GetHeaderAlignmentMap(PlayniteAchievementsSettings settings)
        {
            var map = GetHeaderAlignmentsByKey(settings);
            if (AllowLayoutPersistence || map == null)
            {
                return map;
            }

            return new Dictionary<string, GridAlignment>(map, StringComparer.OrdinalIgnoreCase);
        }

        private Dictionary<string, bool> GetVisibilityByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            switch (ColumnSettingsKey)
            {
                case "StartPageAchievements":
                    return settings.Persisted.StartPageAchievementColumnVisibility;
                case "SingleGame":
                    return settings.Persisted.SingleGameColumnVisibility;
                case "OverviewRecentAchievements":
                case "Overview":
                    return settings.Persisted.OverviewRecentAchievementColumnVisibility;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    return settings.Persisted.OverviewSelectedGameAchievementColumnVisibility;
                default:
                    return settings.Persisted.DataGridColumnVisibility;
            }
        }

        private void SetVisibilityByKey(PlayniteAchievementsSettings settings, Dictionary<string, bool> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnVisibility = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnVisibility = map;
                    break;
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnVisibility = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnVisibility = map;
                    break;
                default:
                    settings.Persisted.DataGridColumnVisibility = map;
                    break;
            }
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
                "OverviewRecentAchievements" => settings.Persisted.OverviewRecentAchievementColumnWidths,
                "Overview" => settings.Persisted.OverviewRecentAchievementColumnWidths,
                "OverviewSelectedGameAchievements" => settings.Persisted.OverviewSelectedGameAchievementColumnWidths,
                "OverviewGame" => settings.Persisted.OverviewSelectedGameAchievementColumnWidths,
                "StartPageAchievements" => settings.Persisted.StartPageAchievementColumnWidths,
                _ => settings.Persisted.SingleGameColumnWidths
            };
        }

        private Dictionary<string, int> GetOrderByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            return ColumnSettingsKey switch
            {
                "DesktopTheme" => settings.Persisted.DesktopThemeColumnOrder,
                "SingleGame" => settings.Persisted.SingleGameColumnOrder,
                "OverviewRecentAchievements" => settings.Persisted.OverviewRecentAchievementColumnOrder,
                "Overview" => settings.Persisted.OverviewRecentAchievementColumnOrder,
                "OverviewSelectedGameAchievements" => settings.Persisted.OverviewSelectedGameAchievementColumnOrder,
                "OverviewGame" => settings.Persisted.OverviewSelectedGameAchievementColumnOrder,
                "StartPageAchievements" => settings.Persisted.StartPageAchievementColumnOrder,
                _ => settings.Persisted.SingleGameColumnOrder
            };
        }

        private Dictionary<string, GridAlignment> GetAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            return ColumnSettingsKey switch
            {
                "DesktopTheme" => settings.Persisted.DesktopThemeColumnAlignments,
                "SingleGame" => settings.Persisted.SingleGameColumnAlignments,
                "OverviewRecentAchievements" => settings.Persisted.OverviewRecentAchievementColumnAlignments,
                "Overview" => settings.Persisted.OverviewRecentAchievementColumnAlignments,
                "OverviewSelectedGameAchievements" => settings.Persisted.OverviewSelectedGameAchievementColumnAlignments,
                "OverviewGame" => settings.Persisted.OverviewSelectedGameAchievementColumnAlignments,
                "StartPageAchievements" => settings.Persisted.StartPageAchievementColumnAlignments,
                _ => settings.Persisted.SingleGameColumnAlignments
            };
        }

        private Dictionary<string, GridVerticalAlignment> GetCellVerticalAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            return ColumnSettingsKey switch
            {
                "DesktopTheme" => settings.Persisted.DesktopThemeColumnVerticalAlignments,
                "SingleGame" => settings.Persisted.SingleGameColumnVerticalAlignments,
                "OverviewRecentAchievements" => settings.Persisted.OverviewRecentAchievementColumnVerticalAlignments,
                "Overview" => settings.Persisted.OverviewRecentAchievementColumnVerticalAlignments,
                "OverviewSelectedGameAchievements" => settings.Persisted.OverviewSelectedGameAchievementColumnVerticalAlignments,
                "OverviewGame" => settings.Persisted.OverviewSelectedGameAchievementColumnVerticalAlignments,
                "StartPageAchievements" => settings.Persisted.StartPageAchievementColumnVerticalAlignments,
                _ => settings.Persisted.SingleGameColumnVerticalAlignments
            };
        }

        private Dictionary<string, GridAlignment> GetHeaderAlignmentsByKey(PlayniteAchievementsSettings settings)
        {
            if (settings?.Persisted == null)
            {
                return null;
            }

            return ColumnSettingsKey switch
            {
                "DesktopTheme" => settings.Persisted.DesktopThemeColumnHeaderAlignments,
                "SingleGame" => settings.Persisted.SingleGameColumnHeaderAlignments,
                "OverviewRecentAchievements" => settings.Persisted.OverviewRecentAchievementColumnHeaderAlignments,
                "Overview" => settings.Persisted.OverviewRecentAchievementColumnHeaderAlignments,
                "OverviewSelectedGameAchievements" => settings.Persisted.OverviewSelectedGameAchievementColumnHeaderAlignments,
                "OverviewGame" => settings.Persisted.OverviewSelectedGameAchievementColumnHeaderAlignments,
                "StartPageAchievements" => settings.Persisted.StartPageAchievementColumnHeaderAlignments,
                _ => settings.Persisted.SingleGameColumnHeaderAlignments
            };
        }

        private void SetOrderByKey(PlayniteAchievementsSettings settings, Dictionary<string, int> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "DesktopTheme":
                    settings.Persisted.DesktopThemeColumnOrder = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnOrder = map;
                    break;
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnOrder = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnOrder = map;
                    break;
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnOrder = map;
                    break;
                default:
                    settings.Persisted.SingleGameColumnOrder = map;
                    break;
            }
        }

        private void SetAlignmentsByKey(PlayniteAchievementsSettings settings, Dictionary<string, GridAlignment> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "DesktopTheme":
                    settings.Persisted.DesktopThemeColumnAlignments = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnAlignments = map;
                    break;
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnAlignments = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnAlignments = map;
                    break;
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnAlignments = map;
                    break;
                default:
                    settings.Persisted.SingleGameColumnAlignments = map;
                    break;
            }
        }

        private void SetCellVerticalAlignmentsByKey(
            PlayniteAchievementsSettings settings,
            Dictionary<string, GridVerticalAlignment> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "DesktopTheme":
                    settings.Persisted.DesktopThemeColumnVerticalAlignments = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnVerticalAlignments = map;
                    break;
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnVerticalAlignments = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnVerticalAlignments = map;
                    break;
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnVerticalAlignments = map;
                    break;
                default:
                    settings.Persisted.SingleGameColumnVerticalAlignments = map;
                    break;
            }
        }

        private void SetHeaderAlignmentsByKey(PlayniteAchievementsSettings settings, Dictionary<string, GridAlignment> map)
        {
            if (settings?.Persisted == null)
            {
                return;
            }

            switch (ColumnSettingsKey)
            {
                case "DesktopTheme":
                    settings.Persisted.DesktopThemeColumnHeaderAlignments = map;
                    break;
                case "SingleGame":
                    settings.Persisted.SingleGameColumnHeaderAlignments = map;
                    break;
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnHeaderAlignments = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnHeaderAlignments = map;
                    break;
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnHeaderAlignments = map;
                    break;
                default:
                    settings.Persisted.SingleGameColumnHeaderAlignments = map;
                    break;
            }
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
                case "OverviewRecentAchievements":
                case "Overview":
                    settings.Persisted.OverviewRecentAchievementColumnWidths = map;
                    break;
                case "OverviewSelectedGameAchievements":
                case "OverviewGame":
                    settings.Persisted.OverviewSelectedGameAchievementColumnWidths = map;
                    break;
                case "StartPageAchievements":
                    settings.Persisted.StartPageAchievementColumnWidths = map;
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

        private static bool IsLegacyImageColumnRuntimeDefaultWidth(string key, double width)
        {
            return !string.IsNullOrWhiteSpace(key) &&
                   LegacyImageColumnRuntimeDefaults.TryGetValue(key, out var legacyWidth) &&
                   Math.Abs(ColumnWidthNormalization.RoundPixelWidth(width) -
                            ColumnWidthNormalization.RoundPixelWidth(legacyWidth)) <= 0.2;
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

            var currentSortPath = string.Empty;
            ListSortDirection? currentSortDirection = null;
            if (!AchievementSortHelper.TrySortItems(
                    items,
                    e.Column.SortMemberPath,
                    sortDirection.Value,
                    SortScope,
                    ref currentSortPath,
                    ref currentSortDirection))
            {
                return;
            }

            if (ItemsSource is BulkObservableCollection<AchievementDisplayItem> bulkItems)
            {
                bulkItems.ReplaceAll(items);
            }
            else if (ItemsSource is IList<AchievementDisplayItem> listItems && !listItems.IsReadOnly)
            {
                CollectionHelper.SynchronizeReferenceCollectionByPosition(
                    listItems,
                    items,
                    updateExisting: null);
            }
            else
            {
                ItemsSource = items;
            }
        }

        private void AchievementRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsHyperlinkClick(e?.OriginalSource))
            {
                return;
            }

            if (sender is DataGridRow row && row.DataContext is AchievementDisplayItem item)
            {
                if (TryActivateAchievementItem(item, consumeWhenNoAction: false))
                {
                    e.Handled = true;
                }
            }
        }

        private static bool IsHyperlinkClick(object source)
        {
            return source is DependencyObject dependencyObject &&
                   VisualTreeHelpers.FindVisualParent<Hyperlink>(dependencyObject) != null;
        }

        private bool TryActivateAchievementItem(AchievementDisplayItem item, bool consumeWhenNoAction)
        {
            if (item == null || !item.CanReveal)
            {
                return consumeWhenNoAction && item != null;
            }

            var command = RevealCommand;
            if (command != null && command.CanExecute(item))
            {
                command.Execute(item);
            }
            else
            {
                item.ToggleReveal();
            }

            return true;
        }

        private void AchievementRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var row = sender as DataGridRow
                      ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
            {
                return;
            }

            var forwardedEvent = new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, e.ChangedButton)
            {
                RoutedEvent = RowPreviewMouseRightButtonDownEvent,
                Source = row
            };
            RaiseEvent(forwardedEvent);
            if (forwardedEvent.Handled)
            {
                e.Handled = true;
            }
        }

        private void AchievementRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var row = sender as DataGridRow
                      ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row == null)
            {
                return;
            }

            var forwardedEvent = new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, e.ChangedButton)
            {
                RoutedEvent = RowPreviewMouseRightButtonUpEvent,
                Source = row
            };
            RaiseEvent(forwardedEvent);
            if (forwardedEvent.Handled)
            {
                e.Handled = true;
            }
        }

        private void DataGridColumnMenu_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!AllowColumnVisibilityMenu)
            {
                e.Handled = true;
                return;
            }

            if (!(sender is DataGrid grid))
            {
                return;
            }

            var row = VisualTreeHelpers.FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (row != null)
            {
                e.Handled = true;
                AchievementRow_PreviewMouseRightButtonUp(row, e);
                return;
            }

            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header?.Column == null)
            {
                return;
            }

            e.Handled = true;

            OpenColumnVisibilityMenu(grid, header, useControllerPlacement: false);
        }

        public bool OpenColumnVisibilityMenuForController()
        {
            var header = Services.UI.FullscreenControllerNavigationService.GetFocusedDataGridColumnHeader(AchievementsDataGrid);
            if (header == null)
            {
                return false;
            }

            return OpenColumnVisibilityMenu(
                AchievementsDataGrid,
                header,
                useControllerPlacement: true);
        }


        public bool IsColumnHeaderFocusedForController()
        {
            return Services.UI.FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(AchievementsDataGrid);
        }

        public bool ActivateFocusedColumnHeaderForController()
        {
            return Services.UI.FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(AchievementsDataGrid);
        }

        private bool OpenColumnVisibilityMenu(DataGrid grid, FrameworkElement owner, bool useControllerPlacement)
        {
            if (!AllowColumnVisibilityMenu || grid == null || owner == null)
            {
                return false;
            }

            var menu = _columnPersistence?.BuildColumnVisibilityMenu((owner as DataGridColumnHeader)?.Column);
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            if (useControllerPlacement)
            {
                return Services.UI.FullscreenControllerNavigationService.OpenContextMenu(owner, menu);
            }

            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = owner;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            menu.IsOpen = true;
            return true;
        }

        /// <summary>
        /// Gets the internal DataGrid for direct access when needed.
        /// Used for scroll reset and other operations that require direct DataGrid access.
        /// </summary>
        public DataGrid InternalDataGrid => AchievementsDataGrid;

        public bool ActivateSelectedItem()
        {
            var item = AchievementsDataGrid?.SelectedItem as AchievementDisplayItem
                       ?? AchievementsDataGrid?.CurrentItem as AchievementDisplayItem;
            return TryActivateAchievementItem(item, consumeWhenNoAction: true);
        }

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
            DataGridAlignmentBehavior.SetColumnCellAlignmentOverridesProvider(AchievementsDataGrid, null);
            DataGridAlignmentBehavior.SetColumnCellVerticalAlignmentOverridesProvider(AchievementsDataGrid, null);
            DataGridAlignmentBehavior.SetColumnHeaderHorizontalAlignmentOverridesProvider(AchievementsDataGrid, null);
            _isAttached = false;
        }
    }
}

