using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Shared surface of the friend and game summary grid controls: the row sizing, header,
    /// control bar and column-settings dependency properties, row mouse event forwarding,
    /// sorting, the column visibility menu and the fullscreen controller hooks. Derived XAML
    /// supplies the grid and the control bar host.
    /// </summary>
    public abstract class SummaryGridControlBase : UserControl
    {
        private protected DataGridColumnLayoutService _columnPersistence;

        public static readonly DependencyProperty ColumnSettingsKeyProperty =
            DependencyProperty.Register(
                nameof(ColumnSettingsKey),
                typeof(string),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(null, (d, e) => ((SummaryGridControlBase)d).OnColumnSettingsKeyChanged(e)));

        public string ColumnSettingsKey
        {
            get => (string)GetValue(ColumnSettingsKeyProperty);
            set => SetValue(ColumnSettingsKeyProperty, value);
        }

        /// <summary>
        /// When false, the grid offers no "Display settings…" entry.
        /// </summary>
        public static readonly DependencyProperty AllowDisplaySettingsMenuProperty =
            DependencyProperty.Register(
                nameof(AllowDisplaySettingsMenu),
                typeof(bool),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(true));

        public bool AllowDisplaySettingsMenu
        {
            get => (bool)GetValue(AllowDisplaySettingsMenuProperty);
            set => SetValue(AllowDisplaySettingsMenuProperty, value);
        }

        public static readonly DependencyProperty FixedRowHeightProperty =
            DependencyProperty.Register(
                nameof(FixedRowHeight),
                typeof(double?),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(null, OnRowSizingChanged));

        public double? FixedRowHeight
        {
            get => (double?)GetValue(FixedRowHeightProperty);
            set => SetValue(FixedRowHeightProperty, value);
        }

        public static readonly DependencyProperty ShowColumnHeadersProperty =
            DependencyProperty.Register(
                nameof(ShowColumnHeaders),
                typeof(bool),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(true, OnShowColumnHeadersChanged));

        public bool ShowColumnHeaders
        {
            get => (bool)GetValue(ShowColumnHeadersProperty);
            set => SetValue(ShowColumnHeadersProperty, value);
        }

        public static readonly DependencyProperty DelayInitialRenderUntilNormalizedProperty =
            DependencyProperty.Register(
                nameof(DelayInitialRenderUntilNormalized),
                typeof(bool),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(false, OnDelayInitialRenderUntilNormalizedChanged));

        public bool DelayInitialRenderUntilNormalized
        {
            get => (bool)GetValue(DelayInitialRenderUntilNormalizedProperty);
            set => SetValue(DelayInitialRenderUntilNormalizedProperty, value);
        }

        public static readonly DependencyProperty ControlBarProperty =
            DependencyProperty.Register(
                nameof(ControlBar),
                typeof(GridControlBarViewModel),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(null, (d, e) => ((SummaryGridControlBase)d)._sortBinder?.SetBar(e.NewValue as GridControlBarViewModel)));

        public GridControlBarViewModel ControlBar
        {
            get => (GridControlBarViewModel)GetValue(ControlBarProperty);
            set => SetValue(ControlBarProperty, value);
        }

        /// <summary>
        /// Whether a control bar without a Sort By item gets one. A showcase widget turns this off:
        /// its item belongs to the widget's adapter, and the widget's settings show or hide it.
        /// </summary>
        public static readonly DependencyProperty ProvidesSortSelectorProperty =
            DependencyProperty.Register(
                nameof(ProvidesSortSelector),
                typeof(bool),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(true, (d, e) => ((SummaryGridControlBase)d)._sortBinder?.Ensure()));

        public bool ProvidesSortSelector
        {
            get => (bool)GetValue(ProvidesSortSelectorProperty);
            set => SetValue(ProvidesSortSelectorProperty, value);
        }

        public static readonly DependencyProperty ShowControlBarProperty =
            DependencyProperty.Register(
                nameof(ShowControlBar),
                typeof(bool),
                typeof(SummaryGridControlBase),
                new PropertyMetadata(true));

        public bool ShowControlBar
        {
            get => (bool)GetValue(ShowControlBarProperty);
            set => SetValue(ShowControlBarProperty, value);
        }

        public event EventHandler<DataGridSortingEventArgs> Sorting;

        public static readonly RoutedEvent RowPreviewMouseLeftButtonDownEvent =
            EventManager.RegisterRoutedEvent(
                nameof(RowPreviewMouseLeftButtonDown),
                RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler),
                typeof(SummaryGridControlBase));

        public event MouseButtonEventHandler RowPreviewMouseLeftButtonDown
        {
            add => AddHandler(RowPreviewMouseLeftButtonDownEvent, value);
            remove => RemoveHandler(RowPreviewMouseLeftButtonDownEvent, value);
        }

        public static readonly RoutedEvent RowPreviewMouseRightButtonDownEvent =
            EventManager.RegisterRoutedEvent(
                nameof(RowPreviewMouseRightButtonDown),
                RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler),
                typeof(SummaryGridControlBase));

        public event MouseButtonEventHandler RowPreviewMouseRightButtonDown
        {
            add => AddHandler(RowPreviewMouseRightButtonDownEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonDownEvent, value);
        }

        public static readonly RoutedEvent RowPreviewMouseRightButtonUpEvent =
            EventManager.RegisterRoutedEvent(
                nameof(RowPreviewMouseRightButtonUp),
                RoutingStrategy.Bubble,
                typeof(MouseButtonEventHandler),
                typeof(SummaryGridControlBase));

        public event MouseButtonEventHandler RowPreviewMouseRightButtonUp
        {
            add => AddHandler(RowPreviewMouseRightButtonUpEvent, value);
            remove => RemoveHandler(RowPreviewMouseRightButtonUpEvent, value);
        }

        private GridSortSelectorBinder _sortBinder;

        private protected SummaryGridControlBase()
        {
        }

        // The derived XAML has built the grid by now, so Sort By can follow it from here on.
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            if (_sortBinder == null && SummaryGrid != null)
            {
                _sortBinder = new GridSortSelectorBinder(
                    SummaryGrid,
                    request => DataGrid_Sorting(SummaryGrid, request),
                    () => ProvidesSortSelector,
                    column => _columnPersistence?.IsListedInColumnMenu(column) ?? true);
                _sortBinder.SetBar(ControlBar);
            }
        }

        /// <summary>The data grid declared by the derived control's XAML.</summary>
        private protected abstract DataGrid SummaryGrid { get; }

        /// <summary>The control bar host declared by the derived control's XAML.</summary>
        private protected abstract GridControlBarControl ControlBarElement { get; }

        public DataGrid InternalDataGrid => SummaryGrid;

        /// <summary>Runs after <see cref="ColumnSettingsKey"/> changes.</summary>
        private protected virtual void OnColumnSettingsKeyChanged(DependencyPropertyChangedEventArgs e)
        {
        }

        /// <summary>
        /// Applies row sizing to a realized row when the fixed row height changes.
        /// </summary>
        private protected virtual void ApplyRealizedRowSizing(DataGridRow row)
        {
            ApplyFixedRowHeight(row);
        }

        /// <summary>Adjusts the column visibility menu before it opens below its header.</summary>
        private protected virtual void PrepareColumnVisibilityMenuPlacement(ContextMenu menu)
        {
        }

        private static void OnShowColumnHeadersChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SummaryGridControlBase control)
            {
                control.UpdateColumnHeadersVisibility();
            }
        }

        private static void OnDelayInitialRenderUntilNormalizedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SummaryGridControlBase control && control._columnPersistence != null)
            {
                control._columnPersistence.DelayInitialRenderUntilNormalized = e.NewValue is bool value && value;
            }
        }

        private protected static void OnRowSizingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SummaryGridControlBase control)
            {
                control.UpdateRealizedRowHeights();
            }
        }

        private protected void UpdateColumnHeadersVisibility()
        {
            var grid = SummaryGrid;
            if (grid != null)
            {
                grid.HeadersVisibility = ShowColumnHeaders
                    ? DataGridHeadersVisibility.Column
                    : DataGridHeadersVisibility.None;
            }
        }

        private protected void UpdateRealizedRowHeights()
        {
            var grid = SummaryGrid;
            if (grid == null)
            {
                return;
            }

            foreach (var item in grid.Items)
            {
                if (grid.ItemContainerGenerator.ContainerFromItem(item) is DataGridRow row)
                {
                    ApplyRealizedRowSizing(row);
                }
            }
        }

        private protected void ApplyFixedRowHeight(DataGridRow row)
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

        private protected double? ResolveFixedRowHeight()
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

        private protected void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            Sorting?.Invoke(sender, e);
            if (e.Handled)
            {
                return;
            }

            DataGridSortingHelper.ApplyCollectionViewSorting(sender, e, SummaryGrid);
        }

        private protected void DataGridRow_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            ForwardRowMouseEvent(e, RowPreviewMouseRightButtonDownEvent, sender);
        }

        private protected void DataGridRow_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            ForwardRowMouseEvent(e, RowPreviewMouseRightButtonUpEvent, sender);
            if (e.Handled)
            {
                return;
            }

            // Hosts that build a row menu append the display settings item themselves. Where a
            // host offers no row menu, offer the display settings on their own.
            GridDisplaySettingsMenuBuilder.TryOpenRowFallbackMenu(this, sender as DataGridRow, e);
        }

        private protected void ForwardRowMouseEvent(MouseButtonEventArgs sourceEvent, RoutedEvent routedEvent, object source)
        {
            var forwardedEvent = new MouseButtonEventArgs(
                sourceEvent.MouseDevice,
                sourceEvent.Timestamp,
                sourceEvent.ChangedButton)
            {
                RoutedEvent = routedEvent,
                Source = source
            };
            RaiseEvent(forwardedEvent);
            if (forwardedEvent.Handled)
            {
                sourceEvent.Handled = true;
            }
        }

        /// <summary>
        /// Tunnels ahead of the row handlers, so it must decline a row hit and leave the row menu
        /// to run. Handles a column header hit, then falls back to the display settings menu for a
        /// click on the grid itself.
        /// </summary>
        private protected void DataGrid_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is DataGrid grid))
            {
                return;
            }

            var header = VisualTreeHelpers.FindVisualParent<DataGridColumnHeader>(
                e.OriginalSource as DependencyObject);
            if (header?.Column != null)
            {
                e.Handled = true;
                OpenColumnVisibilityMenu(grid, header, useControllerPlacement: false);
                return;
            }

            GridDisplaySettingsMenuBuilder.TryOpenFallbackMenu(this, grid, e);
        }

        public bool OpenColumnVisibilityMenuForController()
        {
            var header = FullscreenControllerNavigationService.GetFocusedDataGridColumnHeader(SummaryGrid);
            if (header == null)
            {
                return false;
            }

            return OpenColumnVisibilityMenu(SummaryGrid, header, useControllerPlacement: true);
        }

        public bool IsColumnHeaderFocusedForController()
        {
            return FullscreenControllerNavigationService.IsFocusWithinDataGridColumnHeader(SummaryGrid);
        }

        public bool ActivateFocusedColumnHeaderForController()
        {
            return FullscreenControllerNavigationService.ActivateFocusedDataGridColumnHeader(SummaryGrid);
        }

        public bool OpenFocusedControlBarMenuForController()
        {
            return ControlBarElement?.OpenFocusedSelectorForController() == true;
        }

        public bool IsControlBarFocusedForController()
        {
            return ControlBarElement?.IsKeyboardFocusWithin == true;
        }

        public IList<UIElement> GetControlBarControllerElements()
        {
            return ControlBarElement?.GetControllerElements() ?? new List<UIElement>();
        }

        private bool OpenColumnVisibilityMenu(DataGrid grid, FrameworkElement owner, bool useControllerPlacement)
        {
            if (grid == null || owner == null)
            {
                return false;
            }

            var menu = _columnPersistence?.BuildColumnVisibilityMenu((owner as DataGridColumnHeader)?.Column);
            if (menu == null || menu.Items.Count == 0)
            {
                return false;
            }

            ContextMenuStyleHelper.ApplyAchievementContextMenuStyle(owner, menu);
            if (useControllerPlacement)
            {
                return FullscreenControllerNavigationService.OpenContextMenu(owner, menu);
            }

            menu.Placement = PlacementMode.Bottom;
            menu.PlacementTarget = owner;
            PrepareColumnVisibilityMenuPlacement(menu);
            menu.IsOpen = true;
            return true;
        }

        public void SetSortIndicator(string sortMemberPath, ListSortDirection? direction)
        {
            DataGridSortingHelper.SetSortIndicator(SummaryGrid, sortMemberPath, direction);
        }
    }
}
