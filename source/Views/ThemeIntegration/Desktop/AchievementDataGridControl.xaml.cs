using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.ThemeIntegration.Base;

namespace PlayniteAchievements.Views.ThemeIntegration.Desktop
{
    /// <summary>
    /// Desktop PlayniteAchievements list control for theme integration.
    /// Displays achievements in a DataGrid with sorting and virtualization.
    /// Clones items to maintain independent reveal state per control instance.
    /// </summary>
    public partial class AchievementDataGridControl : ThemeControlBase
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        // Cache the source reference to avoid unnecessary cloning when data hasn't changed
        private List<AchievementDisplayItem> _lastSourceItems;

        // Sort state tracking
        private string _currentSortPath;
        private ListSortDirection? _currentSortDirection;

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
            InitializeComponent();
            Loaded += OnLoaded;
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

        private void LoadData()
        {
            var theme = EffectiveTheme;
            var sourceItems = theme?.AllAchievementDisplayItems;
            if (sourceItems == null)
            {
                _lastSourceItems = null;
                if (DisplayItems == null)
                {
                    DisplayItems = new ObservableCollection<AchievementDisplayItem>();
                }
                else
                {
                    DisplayItems.Clear();
                }
                return;
            }

            // Skip cloning if the source reference hasn't changed
            if (ReferenceEquals(sourceItems, _lastSourceItems))
            {
                return;
            }

            _lastSourceItems = sourceItems;
            var revealedKeys = GetRevealedKeys(DisplayItems);
            var clonedItems = sourceItems.Select(item => item.Clone()).ToList();
            RestoreRevealedState(clonedItems, revealedKeys);

            // Reapply current sort if active
            if (!string.IsNullOrWhiteSpace(_currentSortPath) && _currentSortDirection.HasValue)
            {
                AchievementGridSortHelper.TrySortItems(
                    clonedItems,
                    _currentSortPath,
                    _currentSortDirection.Value,
                    AchievementGridSortScope.GameAchievements,
                    ref _currentSortPath,
                    ref _currentSortDirection);
            }

            // Initialize or synchronize collection
            if (DisplayItems == null)
            {
                DisplayItems = new ObservableCollection<AchievementDisplayItem>(clonedItems);
            }
            else
            {
                CollectionHelper.SynchronizeCollection(DisplayItems, clonedItems);
            }
        }

        /// <summary>
        /// Determines whether a change raised from modern theme bindings should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleThemeDataChange(string propertyName)
        {
            return propertyName == nameof(ModernThemeBindings.AllAchievementDisplayItems);
        }

        /// <summary>
        /// Determines whether a settings change should trigger a refresh.
        /// </summary>
        protected override bool ShouldHandleSettingsDataChange(string propertyName)
        {
            return propertyName == nameof(PersistedSettings.AchievementDataGridMaxHeight);
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
            if (IsLoaded)
            {
                LoadData();
            }
        }

        private void AchievementsGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            // Handle sort indicators
            var sortDirection = DataGridSortingHelper.HandleSorting(sender, e, AchievementsGrid?.InternalDataGrid);
            if (sortDirection == null) return;

            // Perform the actual sorting
            ApplySorting(e.Column.SortMemberPath, sortDirection.Value);

            // Mark as handled to prevent DataGrid's default sorting
            e.Handled = true;
        }

        private void ApplySorting(string sortMemberPath, ListSortDirection direction)
        {
            if (DisplayItems == null || DisplayItems.Count == 0) return;

            // Sort to a new list
            var items = DisplayItems.ToList();
            AchievementGridSortHelper.TrySortItems(
                items,
                sortMemberPath,
                direction,
                AchievementGridSortScope.GameAchievements,
                ref _currentSortPath,
                ref _currentSortDirection);

            // Synchronize in place to trigger efficient UI updates
            CollectionHelper.SynchronizeCollection(DisplayItems, items);
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

