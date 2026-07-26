using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    /// <summary>
    /// Modern PlayniteAchievements list control for theme integration.
    /// Displays achievements in a DataGrid with sorting and virtualization.
    /// Clones items to maintain independent reveal state per control instance.
    /// </summary>
    public partial class AchievementDataGridControl : ThemeControlBase
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Cache the source reference to avoid unnecessary cloning when data hasn't changed
        private List<AchievementDisplayItem> _lastSourceItems;
        private List<AchievementDetail> _lastOrderedAchievements;
        private int? _lastMaxRows;
        private readonly AchievementGridControlBarAdapter _controlBarAdapter;

        // Sort state tracking
        private string _currentSortPath;
        private ListSortDirection? _currentSortDirection;
        private DataGridRow _pendingRightClickRow;

        /// <summary>
        /// Identifies the DisplayItems dependency property.
        /// </summary>
        public static readonly DependencyProperty DisplayItemsProperty =
            DependencyProperty.Register(nameof(DisplayItems), typeof(ObservableCollection<AchievementDisplayItem>),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        /// <summary>
        /// Gets the display items for the list.
        /// </summary>
        public ObservableCollection<AchievementDisplayItem> DisplayItems
        {
            get => (ObservableCollection<AchievementDisplayItem>)GetValue(DisplayItemsProperty);
            private set => SetValue(DisplayItemsProperty, value);
        }

        /// <summary>
        /// Identifies the PreviewMaxHeightOverride dependency property.
        /// When set, preview controls use this value instead of the persisted max height.
        /// </summary>
        public static readonly DependencyProperty PreviewMaxHeightOverrideProperty =
            DependencyProperty.Register(nameof(PreviewMaxHeightOverride), typeof(double?),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnPreviewSizingChanged));

        /// <summary>
        /// Gets or sets a preview-only max height override.
        /// </summary>
        public double? PreviewMaxHeightOverride
        {
            get => (double?)GetValue(PreviewMaxHeightOverrideProperty);
            set => SetValue(PreviewMaxHeightOverrideProperty, value);
        }

        /// <summary>
        /// Identifies the PreviewMinimumMaxHeight dependency property.
        /// When set, preview controls clamp persisted max height up to this minimum.
        /// </summary>
        public static readonly DependencyProperty PreviewMinimumMaxHeightProperty =
            DependencyProperty.Register(nameof(PreviewMinimumMaxHeight), typeof(double),
                typeof(AchievementDataGridControl), new PropertyMetadata(0d, OnPreviewSizingChanged));

        /// <summary>
        /// Gets or sets the minimum visible max height for preview controls.
        /// </summary>
        public double PreviewMinimumMaxHeight
        {
            get => (double)GetValue(PreviewMinimumMaxHeightProperty);
            set => SetValue(PreviewMinimumMaxHeightProperty, value);
        }

        public AchievementDataGridControl()
        {
            _controlBarAdapter = new AchievementGridControlBarAdapter();
            _controlBarAdapter.FilterChanged += (_, __) => LoadData(forceReload: true);
            InitializeComponent();
            Loaded += OnLoaded;
        }

        public GridControlBarViewModel ControlBar => _controlBarAdapter.ControlBar;

        /// <summary>
        /// Identifies the SelectedCategoryName dependency property.
        /// </summary>
        public static readonly DependencyProperty SelectedCategoryNameProperty =
            DependencyProperty.Register(nameof(SelectedCategoryName), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata(null, OnSelectedCategoryNameChanged));

        /// <summary>
        /// Category the achievement grid is currently drilled into (null when not drilled), pushed
        /// up from AchievementsGrid so the compact breadcrumb can be shown above it.
        /// </summary>
        public string SelectedCategoryName
        {
            get => (string)GetValue(SelectedCategoryNameProperty);
            set => SetValue(SelectedCategoryNameProperty, value);
        }

        private static readonly DependencyPropertyKey IsCategorySelectedPropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(IsCategorySelected), typeof(bool),
                typeof(AchievementDataGridControl), new PropertyMetadata(false));

        public static readonly DependencyProperty IsCategorySelectedProperty =
            IsCategorySelectedPropertyKey.DependencyProperty;

        public bool IsCategorySelected => (bool)GetValue(IsCategorySelectedProperty);

        private static void OnSelectedCategoryNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control)
            {
                control.SetValue(IsCategorySelectedPropertyKey, !string.IsNullOrEmpty(control.SelectedCategoryName));
            }
        }

        private static readonly DependencyPropertyKey GameNamePropertyKey =
            DependencyProperty.RegisterReadOnly(nameof(GameName), typeof(string),
                typeof(AchievementDataGridControl), new PropertyMetadata(null));

        public static readonly DependencyProperty GameNameProperty =
            GameNamePropertyKey.DependencyProperty;

        // Name of the currently loaded game, so the breadcrumb can read "GameName > Category" with
        // the game name acting as the back link, matching the other surfaces' breadcrumb headers.
        public string GameName => (string)GetValue(GameNameProperty);

        private void GameNameBreadcrumb_Click(object sender, MouseButtonEventArgs e)
        {
            if (IsCategorySelected)
            {
                AchievementsGrid?.ExitDrilledCategory();
            }
        }

        private static void OnPreviewSizingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is AchievementDataGridControl control && control.IsLoaded)
            {
                control.UpdateMaxHeight();
            }
        }

        /// <summary>
        /// Gets a value indicating whether this control should subscribe to theme data change notifications.
        /// </summary>
        protected override bool EnableAutomaticThemeDataUpdates => true;
        protected override bool UsesThemeBindings => true;

        /// <summary>
        /// Called when ThemeDataOverride changes. Clears cache to force data refresh.
        /// </summary>
        protected override void OnThemeDataOverrideChangedInternal()
        {
            _lastSourceItems = null;
            _lastOrderedAchievements = null;
            _lastMaxRows = null;
            ResetSortState();
            UpdatePreviewBehavior();
            base.OnThemeDataOverrideChangedInternal();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdatePreviewBehavior();
            UpdateMaxHeight();
            LoadData();
        }

        private void UpdateMaxHeight()
        {
            var settings = EffectiveSettings?.Persisted;
            if (settings == null || AchievementsGrid == null)
            {
                return;
            }

            var isPreview = ThemeDataOverride != null;
            var persistedMaxHeight = settings.AchievementDataGridMaxHeight;
            var resolvedMaxHeight = AchievementDataGridPreviewHeightResolver.Resolve(
                persistedMaxHeight,
                isPreview,
                PreviewMaxHeightOverride,
                PreviewMinimumMaxHeight);

            AchievementsGrid.DataGridMaxHeight = resolvedMaxHeight;

            if (isPreview)
            {
                var persistedText = persistedMaxHeight?.ToString("0.##") ?? "unlimited";
                var appliedText = double.IsNaN(resolvedMaxHeight) ? "unlimited" : resolvedMaxHeight.ToString("0.##");
                var overrideText = PreviewMaxHeightOverride?.ToString("0.##") ?? "none";
                Logger.Debug(
                    $"[SettingsPreviewGrid] UpdateMaxHeight kind={GetPreviewKind()}, persisted={persistedText}, " +
                    $"override={overrideText}, min={PreviewMinimumMaxHeight:0.##}, applied={appliedText}");

                if (!double.IsNaN(resolvedMaxHeight) && !double.IsInfinity(resolvedMaxHeight) && resolvedMaxHeight < 88)
                {
                    Logger.Warn($"[SettingsPreviewGrid] Applied preview max height is below one-row height: {resolvedMaxHeight:0.##}");
                }
            }
        }

        private void LoadData(bool useSourceOrder = false, bool forceReload = false)
        {
            var theme = EffectiveTheme;
            if (!IsEffectiveModernThemeCurrentForContext())
            {
                return;
            }

            if (theme == null || !theme.HasAchievements)
            {
                ClearDisplayItems(resetSortState: false);
                return;
            }

            var sourceItems = theme?.AllAchievementDisplayItems;
            SetValue(GameNamePropertyKey, sourceItems?.FirstOrDefault()?.GameName);
            var settings = EffectiveSettings?.Persisted;
            var maxRows = settings?.DesktopThemeAchievementGridMaxRows;
            var orderedAchievements = useSourceOrder
                ? theme?.AchievementDefaultOrder ?? new List<AchievementDetail>()
                : AchievementSortHelper.ResolveSelectedGameAchievements(
                    theme,
                    settings,
                    AchievementSortSurface.AchievementDataGrid);
            if (sourceItems == null)
            {
                ClearDisplayItems(resetSortState: true);
                return;
            }

            var needsReload =
                forceReload ||
                !ReferenceEquals(sourceItems, _lastSourceItems) ||
                !ReferenceEquals(orderedAchievements, _lastOrderedAchievements) ||
                _lastMaxRows != maxRows;

            if (!needsReload)
            {
                ApplyCurrentSortIndicator(theme);
                return;
            }

            _lastSourceItems = sourceItems;
            _lastOrderedAchievements = orderedAchievements;
            _lastMaxRows = maxRows;

            var revealedKeys = GetRevealedKeys(DisplayItems);
            var clonedItems = sourceItems.Select(item => item.Clone()).ToList();
            RestoreRevealedState(clonedItems, revealedKeys);

            // Category rollups and dropdown options use the canonical definition order,
            // independent of the configured theme sort or a user-applied column sort.
            var categorySummaryItems = new List<AchievementDisplayItem>(clonedItems);
            AchievementSortHelper.ApplyExplicitOrder(
                categorySummaryItems,
                AchievementSortHelper.CreateExplicitOrderKeys(theme?.AchievementDefaultOrder ?? new List<AchievementDetail>()));
            if (AchievementsGrid != null)
            {
                AchievementsGrid.CategorySummarySource = categorySummaryItems;
            }

            _controlBarAdapter.UpdateOptions(categorySummaryItems);

            if (!string.IsNullOrWhiteSpace(_currentSortPath) && _currentSortDirection.HasValue)
            {
                AchievementSortHelper.TrySortItems(
                    clonedItems,
                    _currentSortPath,
                    _currentSortDirection.Value,
                    AchievementSortScope.GameAchievements,
                    ref _currentSortPath,
                    ref _currentSortDirection);
            }
            else
            {
                AchievementSortHelper.ApplyExplicitOrder(
                    clonedItems,
                    AchievementSortHelper.CreateExplicitOrderKeys(orderedAchievements));
            }

            var filteredItems = _controlBarAdapter.Apply(clonedItems);
            var displayItems = DisplayGridRowLimitHelper.Limit(filteredItems, maxRows);

            if (DisplayItems == null)
            {
                DisplayItems = new ObservableCollection<AchievementDisplayItem>(displayItems);
            }
            else
            {
                CollectionHelper.SynchronizeReferenceCollectionByPosition(
                    DisplayItems,
                    displayItems,
                    (target, source) => target.UpdateFrom(source));
            }

            if (useSourceOrder)
            {
                AchievementsGrid?.SetSortIndicator(null, null);
            }
            else
            {
                ApplyCurrentSortIndicator(theme);
            }
        }

        /// <summary>
        /// Determines whether a change raised from modern theme bindings should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(ModernThemeBindings.SelectedGameId) ||
                   propertyName == nameof(ModernThemeBindings.HasAchievements) ||
                   propertyName == nameof(ModernThemeBindings.AllAchievementDisplayItems) ||
                   AchievementSortHelper.IsSelectedGameAchievementsPropertyName(propertyName) ||
                   propertyName == nameof(ModernThemeBindings.HasCustomAchievementOrder);
        }

        /// <summary>
        /// Determines whether a settings change should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.AchievementDataGridMaxHeight) ||
                   propertyName == nameof(PersistedSettings.DesktopThemeAchievementGridMaxRows) ||
                   AchievementSortHelper.IsConfiguredDefaultSortPropertyName(
                       propertyName,
                       AchievementSortSurface.AchievementDataGrid);
        }

        /// <summary>
        /// Called when theme data changes and the list should be refreshed.
        /// </summary>
        protected override void OnThemeDataUpdated()
        {
            UpdateMaxHeight();
            LoadData();
        }

        /// <summary>
        /// Called when the game context changes for this control.
        /// </summary>
        public override void GameContextChanged(Game oldContext, Game newContext)
        {
            UpdateCurrentGameContext(newContext);

            if ((oldContext?.Id ?? Guid.Empty) != (newContext?.Id ?? Guid.Empty))
            {
                ResetSortState();
            }
        }

        private void AchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;

            var sortAction = AchievementSortHelper.ResolveGridSortAction(
                e.Column?.SortMemberPath,
                _currentSortPath,
                _currentSortDirection,
                EffectiveSettings?.Persisted,
                AchievementSortSurface.AchievementDataGrid,
                e.Column?.SortDirection);
            if (sortAction.Kind == AchievementGridSortActionKind.None)
            {
                return;
            }

            if (sortAction.Kind == AchievementGridSortActionKind.ResetToDefault)
            {
                ResetToDefaultSort();
            }
            else if (sortAction.Direction.HasValue)
            {
                ApplySorting(sortAction.SortMemberPath, sortAction.Direction.Value);
            }
        }

        private void AchievementsGrid_RowPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                _pendingRightClickRow = row;
            }
        }

        private void AchievementsGrid_RowPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TryResolveContextMenuRow(sender, e, out var row))
            {
                e.Handled = true;
                var targetRow = _pendingRightClickRow ?? row;
                _pendingRightClickRow = null;
                OpenContextMenuForRow(targetRow);
            }
        }

        private static bool TryResolveContextMenuRow(object sender, MouseButtonEventArgs e, out DataGridRow row)
        {
            row = sender as DataGridRow
                  ?? e?.Source as DataGridRow
                  ?? VisualTreeHelpers.FindVisualParent<DataGridRow>(e?.OriginalSource as DependencyObject);
            return row != null;
        }

        private bool OpenContextMenuForRow(DataGridRow row)
        {
            if (row == null || !row.IsLoaded || row.DataContext == null)
            {
                return false;
            }

            var menu = new ContextMenu();
            AchievementRowOptionsMenuBuilder.AppendAchievementOptions(
                menu,
                row.DataContext,
                this,
                RefreshAfterRowOptionsChanged);
            if (menu.Items.Count == 0)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(this, menu);
            row.ContextMenu = menu;
            menu.PlacementTarget = row;
            menu.IsOpen = true;
            return true;
        }

        private void RefreshAfterRowOptionsChanged()
        {
            _lastSourceItems = null;
            _lastOrderedAchievements = null;
            LoadData();
        }

        private void ApplySorting(string sortMemberPath, ListSortDirection direction)
        {
            if (DisplayItems == null || DisplayItems.Count == 0) return;

            var sourceItems = EffectiveTheme?.AllAchievementDisplayItems;
            var revealedKeys = GetRevealedKeys(DisplayItems);
            var items = sourceItems != null
                ? sourceItems.Select(item => item.Clone()).ToList()
                : DisplayItems.ToList();
            RestoreRevealedState(items, revealedKeys);
            AchievementSortHelper.TrySortItems(
                items,
                sortMemberPath,
                direction,
                AchievementSortScope.GameAchievements,
                ref _currentSortPath,
                ref _currentSortDirection);

            // Keep dropdown options in canonical definition order rather than the new column sort.
            _controlBarAdapter.UpdateOptions(AchievementsGrid?.CategorySummarySource ?? items);
            var filteredItems = _controlBarAdapter.Apply(items);
            var displayItems = DisplayGridRowLimitHelper.Limit(
                filteredItems,
                EffectiveSettings?.Persisted?.DesktopThemeAchievementGridMaxRows);

            // Synchronize in place to trigger efficient UI updates
            CollectionHelper.SynchronizeCollection(DisplayItems, displayItems);
            ApplyCurrentSortIndicator(EffectiveTheme);
        }

        private void ResetSortState()
        {
            _currentSortPath = null;
            _currentSortDirection = null;
        }

        private void ResetToDefaultSort()
        {
            ResetSortState();
            _lastOrderedAchievements = null;
            LoadData(useSourceOrder: true);
        }

        private void ClearDisplayItems(bool resetSortState)
        {
            _lastSourceItems = null;
            _lastOrderedAchievements = null;
            _lastMaxRows = null;
            SetValue(GameNamePropertyKey, null);
            if (resetSortState)
            {
                ResetSortState();
            }

            if (DisplayItems == null)
            {
                DisplayItems = new ObservableCollection<AchievementDisplayItem>();
            }
            else
            {
                DisplayItems.Clear();
            }

            _controlBarAdapter.Clear();
            if (AchievementsGrid != null)
            {
                AchievementsGrid.CategorySummarySource = null;
            }

            AchievementsGrid?.SetSortIndicator(null, null);
        }

        private void ApplyCurrentSortIndicator(ModernThemeBindings theme)
        {
            if (AchievementsGrid == null)
            {
                return;
            }

            AchievementSortHelper.ApplySortIndicator(
                _currentSortPath,
                _currentSortDirection,
                EffectiveSettings?.Persisted,
                AchievementSortSurface.AchievementDataGrid,
                (sortPath, sortDirection) => AchievementsGrid.SetSortIndicator(sortPath, sortDirection));
        }

        private void UpdatePreviewBehavior()
        {
            if (AchievementsGrid == null)
            {
                return;
            }

            var isPreview = ThemeDataOverride != null;
            AchievementsGrid.AllowLayoutPersistence = !isPreview;
            AchievementsGrid.AllowColumnVisibilityMenu = !isPreview;
        }

        private string GetPreviewKind()
        {
            if (PreviewMaxHeightOverride.HasValue)
            {
                return "settings-inline";
            }

            if (ThemeDataOverride != null && PreviewMinimumMaxHeight > 0)
            {
                return "settings-main";
            }

            return ThemeDataOverride != null ? "settings-generic" : "standard";
        }

        private static HashSet<string> GetRevealedKeys(IEnumerable<AchievementDisplayItem> items)
        {
            if (items == null)
            {
                return null;
            }

            var revealedKeys = new HashSet<string>(
                items
                .Where(item => item?.IsRevealed == true)
                .Select(GetRevealKey)
                .Where(key => !string.IsNullOrWhiteSpace(key)),
                StringComparer.OrdinalIgnoreCase);

            return revealedKeys.Count > 0 ? revealedKeys : null;
        }

        private static void RestoreRevealedState(IEnumerable<AchievementDisplayItem> items, HashSet<string> revealedKeys)
        {
            if (items == null || revealedKeys == null || revealedKeys.Count == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                var key = GetRevealKey(item);
                if (!string.IsNullOrWhiteSpace(key) && revealedKeys.Contains(key))
                {
                    item.IsRevealed = true;
                }
            }
        }

        private static string GetRevealKey(AchievementDisplayItem item)
        {
            if (item == null)
            {
                return null;
            }

            return $"{item.PlayniteGameId:N}|{item.ApiName}|{item.DisplayName}|{item.GameName}";
        }
    }
}



