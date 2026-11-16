using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    public partial class GridControlBarControl : UserControl
    {
        public static readonly DependencyProperty ControlBarProperty =
            DependencyProperty.Register(
                nameof(ControlBar),
                typeof(GridControlBarViewModel),
                typeof(GridControlBarControl),
                new PropertyMetadata(null, OnControlBarChanged));

        /// <summary>
        /// The grid options whose hidden items this bar leaves out; null shows every item. Set by
        /// the grid that hosts the bar, from the surface its Display Settings edit.
        /// </summary>
        public static readonly DependencyProperty GridOptionsProperty =
            DependencyProperty.Register(
                nameof(GridOptions),
                typeof(PlayniteAchievements.Models.Settings.GridCommonOptions),
                typeof(GridControlBarControl),
                new PropertyMetadata(null, OnGridOptionsChanged));

        public PlayniteAchievements.Models.Settings.GridCommonOptions GridOptions
        {
            get => (PlayniteAchievements.Models.Settings.GridCommonOptions)GetValue(GridOptionsProperty);
            set => SetValue(GridOptionsProperty, value);
        }

        private static readonly DependencyPropertyKey ShownItemsPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(ShownItems),
                typeof(System.ComponentModel.ICollectionView),
                typeof(GridControlBarControl),
                new PropertyMetadata(null));

        /// <summary>
        /// What the bar itself shows, less what its grid options hide: by default the buttons and
        /// toggles, then Sort By, then the Filters button standing in for the dropdown filters,
        /// rearranged by the saved item order.
        /// </summary>
        public System.ComponentModel.ICollectionView ShownItems => (System.ComponentModel.ICollectionView)GetValue(ShownItemsPropertyKey.DependencyProperty);

        private readonly System.Collections.ObjectModel.ObservableCollection<GridControlBarItem> _barItems =
            new System.Collections.ObjectModel.ObservableCollection<GridControlBarItem>();

        private readonly System.Collections.ObjectModel.ObservableCollection<GridControlBarItem> _panelItems =
            new System.Collections.ObjectModel.ObservableCollection<GridControlBarItem>();

        private readonly List<GridControlBarItem> _watchedPanelItems = new List<GridControlBarItem>();
        private Window _panelHostWindow;

        /// <summary>The dropdown filters the Filters panel lists, less what the grid options hide.</summary>
        public System.Collections.ObjectModel.ObservableCollection<GridControlBarItem> ShownFilterItems => _panelItems;

        /// <summary>The bar's Filters button, which counts the filters that narrow the rows.</summary>
        public GridFiltersButton FiltersButton { get; } = new GridFiltersButton();

        /// <summary>
        /// Whether the dropdown filters fold into the Filters panel. Off shows every item on the bar
        /// itself, for a bar holding one item, such as a row of a widget's settings.
        /// </summary>
        public static readonly DependencyProperty CollapsesFiltersProperty =
            DependencyProperty.Register(
                nameof(CollapsesFilters),
                typeof(bool),
                typeof(GridControlBarControl),
                new PropertyMetadata(true, (d, e) => ((GridControlBarControl)d).ApplyHiddenItems()));

        public bool CollapsesFilters
        {
            get => (bool)GetValue(CollapsesFiltersProperty);
            set => SetValue(CollapsesFiltersProperty, value);
        }

        /// <summary>
        /// The width every filter takes inside the Filters panel, so its rows line up; NaN leaves
        /// each item its own width. Inherited, so the panel sets it once for every row.
        /// </summary>
        public static readonly DependencyProperty PanelItemWidthProperty =
            DependencyProperty.RegisterAttached(
                "PanelItemWidth",
                typeof(double),
                typeof(GridControlBarControl),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.Inherits));

        public static double GetPanelItemWidth(DependencyObject element) =>
            (double)element.GetValue(PanelItemWidthProperty);

        public static void SetPanelItemWidth(DependencyObject element, double value) =>
            element.SetValue(PanelItemWidthProperty, value);

        /// <summary>Whether <paramref name="item"/> sits in the Filters panel rather than on the bar.</summary>
        public bool IsInFilterPanel(GridControlBarItem item) => UsesFilterPanel && item?.IsPanelFilter == true;

        // Hiding the Filters button shows the dropdown filters on the bar itself.
        private bool UsesFilterPanel =>
            CollapsesFilters &&
            ControlBar?.FiltersInline != true &&
            GridOptions?.IsControlBarItemHidden(FiltersButton.Id) != true;

        private IReadOnlyList<string> ItemOrder => GridOptions?.ControlBarItemOrder ?? ControlBar?.ItemOrder;

        private static readonly DependencyPropertyKey ShownSearchPropertyKey =
            DependencyProperty.RegisterReadOnly(
                nameof(ShownSearch),
                typeof(GridSearchControl),
                typeof(GridControlBarControl),
                new PropertyMetadata(null));

        /// <summary>The bar's search box, or null while its grid options hide it.</summary>
        public GridSearchControl ShownSearch => (GridSearchControl)GetValue(ShownSearchPropertyKey.DependencyProperty);

        /// <summary>Raised when the grid options change which items show.</summary>
        public event EventHandler ShownItemsChanged;

        /// <summary>
        /// The search box the bar holds outside the category list, which swaps its own in; null
        /// while the bar's search is that one.
        /// </summary>
        public GridSearchControl BaseSearch { get; set; }

        /// <summary>
        /// The items a grid's Display Settings list under Show Control Bar: the search box, then
        /// every item with an id in the order the bar and then its Filters panel show them. The
        /// Filters button is listed whenever the bar has a dropdown filter, shown or not.
        /// </summary>
        public IReadOnlyList<IGridControlBarEntry> GetSettingsEntries()
        {
            var entries = new List<IGridControlBarEntry>();
            var bar = ControlBar;
            if (bar == null)
            {
                return entries;
            }

            var search = BaseSearch ?? bar.Search;
            if (!string.IsNullOrWhiteSpace(search?.Id))
            {
                entries.Add(search);
            }

            GridControlBarOrder.Arrange(bar.Items, FiltersButton, UsesFilterPanel, ItemOrder, out var onBar, out var inPanel);
            var ordered = onBar.Concat(inPanel).ToList();
            if (CollapsesFilters && !ordered.Contains(FiltersButton) && bar.Items.Any(item => item?.IsPanelFilter == true))
            {
                ordered.Add(FiltersButton);
                ordered = GridControlBarOrder.Apply(ordered, item => item.Id, ItemOrder);
            }

            foreach (var item in bar.LeadingItems.Concat(ordered))
            {
                if (!string.IsNullOrWhiteSpace(item?.Id) && !entries.Contains(item))
                {
                    entries.Add(item);
                }
            }

            return entries;
        }

        /// <summary>Whether the grid options hide <paramref name="entry"/>.</summary>
        public bool IsItemHidden(IGridControlBarEntry entry) =>
            entry != null && GridOptions?.IsControlBarItemHidden(entry.Id) == true;

        public static readonly DependencyProperty ShowControlBarProperty =
            DependencyProperty.Register(
                nameof(ShowControlBar),
                typeof(bool),
                typeof(GridControlBarControl),
                new PropertyMetadata(true, OnVisibilityPropertyChanged));

        /// <summary>
        /// Whether the filters may drop below the search box and wrap across lines when the bar
        /// is too narrow for one line. Off keeps the single-line bar. Inherited, so a host such as
        /// a showcase widget sets it once for every control bar inside it.
        /// </summary>
        public static readonly DependencyProperty WrapItemsProperty =
            DependencyProperty.RegisterAttached(
                "WrapItems",
                typeof(bool),
                typeof(GridControlBarControl),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetWrapItems(DependencyObject element) =>
            (bool)element.GetValue(WrapItemsProperty);

        public static void SetWrapItems(DependencyObject element, bool value) =>
            element.SetValue(WrapItemsProperty, value);

        public bool WrapItems
        {
            get => (bool)GetValue(WrapItemsProperty);
            set => SetValue(WrapItemsProperty, value);
        }

        /// <summary>
        /// Whether each item spans the bar's width instead of keeping its own, for a bar holding
        /// one item, such as a row of a widget's settings.
        /// </summary>
        public static readonly DependencyProperty FillsWidthProperty =
            DependencyProperty.Register(
                nameof(FillsWidth),
                typeof(bool),
                typeof(GridControlBarControl),
                new PropertyMetadata(false));

        public bool FillsWidth
        {
            get => (bool)GetValue(FillsWidthProperty);
            set => SetValue(FillsWidthProperty, value);
        }

        public GridControlBarControl()
        {
            InitializeComponent();
            SetValue(ShownItemsPropertyKey, new ListCollectionView(_barItems));
            Unloaded += (_, __) => FiltersPopup.IsOpen = false;
            UpdateVisibility();
        }

        public GridControlBarViewModel ControlBar
        {
            get => (GridControlBarViewModel)GetValue(ControlBarProperty);
            set => SetValue(ControlBarProperty, value);
        }

        public bool ShowControlBar
        {
            get => (bool)GetValue(ShowControlBarProperty);
            set => SetValue(ShowControlBarProperty, value);
        }

        public bool OpenFocusedSelectorForController()
        {
            var focusedButton = FullscreenControllerNavigationService.FindAncestor<Button>(
                                    Keyboard.FocusedElement as DependencyObject)
                                ?? Keyboard.FocusedElement as Button;
            if (focusedButton == null || !IsKeyboardFocusWithin)
            {
                return false;
            }

            if (focusedButton.DataContext is GridMultiSelectFilter)
            {
                MultiSelectFilter_Click(focusedButton, new RoutedEventArgs());
                return focusedButton.ContextMenu?.IsOpen == true;
            }

            if (focusedButton.DataContext is GridProviderPlatformFilter)
            {
                ProviderFilter_Click(focusedButton, new RoutedEventArgs());
                return focusedButton.ContextMenu?.IsOpen == true;
            }

            if (focusedButton.DataContext is GridFiltersButton)
            {
                FiltersButton_Click(focusedButton, new RoutedEventArgs());
                return FiltersPopup.IsOpen;
            }

            // The direction half has no menu; only the dropdown half opens one.
            if (focusedButton.DataContext is GridSortSelector && focusedButton.ContextMenu != null)
            {
                SortSelector_Click(focusedButton, new RoutedEventArgs());
                return focusedButton.ContextMenu.IsOpen;
            }

            return false;
        }

        public IList<UIElement> GetControllerElements()
        {
            var elements = new List<UIElement>();
            CollectControllerElements(this, elements);
            return elements
                .Where(IsControllerElementAvailable)
                .ToList();
        }

        private static void OnVisibilityPropertyChanged(
            DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is GridControlBarControl control)
            {
                control.UpdateVisibility();
            }
        }

        private static void OnControlBarChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is GridControlBarControl control))
            {
                return;
            }

            if (e.OldValue is GridControlBarViewModel oldBar)
            {
                oldBar.PropertyChanged -= control.ControlBar_PropertyChanged;
                oldBar.Items.CollectionChanged -= control.ControlBarItems_CollectionChanged;
            }

            if (e.NewValue is GridControlBarViewModel newBar)
            {
                newBar.PropertyChanged += control.ControlBar_PropertyChanged;
                newBar.Items.CollectionChanged += control.ControlBarItems_CollectionChanged;
            }

            control.RebuildShownItems();
            control.UpdateShownSearch();
            control.UpdateVisibility();
        }

        private static void OnGridOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is GridControlBarControl control))
            {
                return;
            }

            if (e.OldValue is System.ComponentModel.INotifyPropertyChanged oldOptions)
            {
                oldOptions.PropertyChanged -= control.GridOptions_PropertyChanged;
            }

            if (e.NewValue is System.ComponentModel.INotifyPropertyChanged newOptions)
            {
                newOptions.PropertyChanged += control.GridOptions_PropertyChanged;
            }

            control.ApplyHiddenItems();
        }

        private void GridOptions_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(PlayniteAchievements.Models.Settings.GridCommonOptions.HiddenControlBarItems) ||
                e.PropertyName == nameof(PlayniteAchievements.Models.Settings.GridCommonOptions.ControlBarItemOrder))
            {
                ApplyHiddenItems();
            }
        }

        // The category list swaps the bar's search box, so the shown one follows it.
        private void ControlBar_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(GridControlBarViewModel.Search))
            {
                UpdateShownSearch();
            }

            if (e.PropertyName == nameof(GridControlBarViewModel.ItemOrder) ||
                e.PropertyName == nameof(GridControlBarViewModel.FiltersInline))
            {
                ApplyHiddenItems();
            }
        }

        // Items added later (the category mode toggle) can make a fully hidden bar show again.
        private void ControlBarItems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            RebuildShownItems();
            UpdateVisibility();
        }

        private void ApplyHiddenItems()
        {
            RebuildShownItems();
            UpdateShownSearch();
            UpdateVisibility();
            ShownItemsChanged?.Invoke(this, EventArgs.Empty);
        }

        // Splits the shown items between the bar and the Filters panel. Rebuilt only when the
        // split changes, so open dropdowns keep their buttons.
        private void RebuildShownItems()
        {
            var shown = (ControlBar?.Items ?? Enumerable.Empty<GridControlBarItem>())
                .Where(item => item != null && IsShown(item))
                .ToList();
            GridControlBarOrder.Arrange(shown, FiltersButton, UsesFilterPanel, ItemOrder, out var bar, out var panel);
            Replace(_barItems, bar);
            Replace(_panelItems, panel);

            foreach (var item in _watchedPanelItems)
            {
                item.PropertyChanged -= PanelItem_PropertyChanged;
            }

            _watchedPanelItems.Clear();
            foreach (var item in panel)
            {
                item.PropertyChanged += PanelItem_PropertyChanged;
                _watchedPanelItems.Add(item);
            }

            UpdateFiltersButton();
            if (panel.Count == 0)
            {
                FiltersPopup.IsOpen = false;
            }
        }

        private static void Replace(
            System.Collections.ObjectModel.ObservableCollection<GridControlBarItem> target,
            List<GridControlBarItem> items)
        {
            if (target.SequenceEqual(items))
            {
                return;
            }

            target.Clear();
            foreach (var item in items)
            {
                target.Add(item);
            }
        }

        private void PanelItem_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            UpdateFiltersButton();

        private void UpdateFiltersButton()
        {
            FiltersButton.Count = _panelItems.Count(item => item.HasSelection);
            FiltersButton.Available = _panelItems.Any(item => item.EffectiveIsVisible);
        }

        private bool IsShown(object item) => !(item is IGridControlBarEntry entry) || !IsItemHidden(entry);

        private void UpdateShownSearch()
        {
            var search = ControlBar?.Search;
            SetValue(ShownSearchPropertyKey, IsItemHidden(search) ? null : search);
        }

        private void UpdateVisibility()
        {
            // A bar whose grid options hide everything it holds shows nothing at all.
            var bar = ControlBar;
            var anyShown = bar != null &&
                (bar.LeadingItems.Count > 0 || ShownSearch != null || bar.Items.Any(item => IsShown(item)));
            Visibility = ShowControlBar && anyShown
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GridSearchControl search)
            {
                search.Clear();
            }
        }

        // Enter submits the search: the delayed binding applies now, and focus leaves the box.
        private void SearchTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || !(sender is TextBox textBox))
            {
                return;
            }

            BindingOperations.GetBindingExpression(textBox, TextBox.TextProperty)?.UpdateSource();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(textBox), null);
            Keyboard.ClearFocus();
            e.Handled = true;
        }

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GridActionButton item)
            {
                item.Invoke();
            }
        }

        // The popup closes on the press outside it, so the press on the button that closed it must
        // not reopen it on the release that completes the click.
        private void DateRange_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.Tag is System.Windows.Controls.Primitives.Popup popup))
            {
                return;
            }

            if (popup.Tag is DateTime closedAt && (DateTime.UtcNow - closedAt).TotalMilliseconds < 300)
            {
                return;
            }

            popup.IsOpen = true;
        }

        private void DateRangePopup_Closed(object sender, EventArgs e)
        {
            if (sender is System.Windows.Controls.Primitives.Popup popup)
            {
                popup.Tag = DateTime.UtcNow;
            }
        }

        private void DateRangeClearFrom_Click(object sender, RoutedEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as GridDateRangeFilter)?.ClearFrom();
        }

        private void DateRangeClearTo_Click(object sender, RoutedEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as GridDateRangeFilter)?.ClearTo();
        }

        private void DateRangeClear_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GridDateRangeFilter item)
            {
                item.Clear();
            }
        }

        private void MultiSelectFilter_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            MultiSelectFilterMenu.Open(button, button?.DataContext as GridMultiSelectFilter);
        }

        // One row per sortable column; the checked row is the current sort, and clicking it again
        // removes the sort.
        private void SortSelector_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var selector = button?.DataContext as GridSortSelector;
            var menu = button?.ContextMenu;
            if (selector == null || menu == null)
            {
                return;
            }

            selector.Opening?.Invoke();
            menu.Items.Clear();
            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in selector.Options)
            {
                menu.Items.Add(CreateSortMenuItem(menu, selector, option.Key, option.Label, itemStyle));
            }

            MultiSelectFilterMenu.OpenSelectorContextMenu(button, menu);
        }

        // Not checkable: a checkable row flips its own mark on click, which would show two marked
        // rows (or none) while the menu closes. The marks follow the selector instead.
        private static MenuItem CreateSortMenuItem(ContextMenu menu, GridSortSelector selector, string key, string label, Style style)
        {
            var item = new MenuItem
            {
                Header = label,
                Tag = key,
                IsChecked = string.Equals(selector.SelectedKey, key, StringComparison.Ordinal)
            };
            if (style != null)
            {
                item.Style = style;
            }

            item.Click += (_, __) =>
            {
                selector.Select(key);
                foreach (var row in menu.Items.OfType<MenuItem>())
                {
                    row.IsChecked = string.Equals(selector.SelectedKey, row.Tag as string, StringComparison.Ordinal);
                }
            };
            return item;
        }

        private void SortDirection_Click(object sender, RoutedEventArgs e)
        {
            ((sender as FrameworkElement)?.DataContext as GridSortSelector)?.ToggleDirection();
        }

        // The press that closed the panel (outside it, on this button) must not reopen it on the
        // release that completes the click.
        private void FiltersButton_Click(object sender, RoutedEventArgs e)
        {
            if (FiltersPopup.IsOpen)
            {
                FiltersPopup.IsOpen = false;
                return;
            }

            if (FiltersPopup.Tag is DateTime closedAt && (DateTime.UtcNow - closedAt).TotalMilliseconds < 300)
            {
                return;
            }

            FiltersPopup.PlacementTarget = sender as UIElement;
            FiltersPopup.IsOpen = true;
            _panelHostWindow = Window.GetWindow(this);
            if (_panelHostWindow != null)
            {
                _panelHostWindow.PreviewMouseDown += PanelHostWindow_PreviewMouseDown;
                _panelHostWindow.Deactivated += PanelHostWindow_Closing;
                _panelHostWindow.LocationChanged += PanelHostWindow_Closing;
            }
        }

        private void FiltersPopup_Closed(object sender, EventArgs e)
        {
            FiltersPopup.Tag = DateTime.UtcNow;
            if (_panelHostWindow != null)
            {
                _panelHostWindow.PreviewMouseDown -= PanelHostWindow_PreviewMouseDown;
                _panelHostWindow.Deactivated -= PanelHostWindow_Closing;
                _panelHostWindow.LocationChanged -= PanelHostWindow_Closing;
                _panelHostWindow = null;
            }
        }

        private void PanelHostWindow_Closing(object sender, EventArgs e) => FiltersPopup.IsOpen = false;

        // Presses inside the panel, or inside a dropdown a filter in it opened, keep it open; the
        // Filters button closes it through its own click.
        private void PanelHostWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            if (IsWithin(source, FiltersPopup.Child) || IsWithin(source, FiltersPopup.PlacementTarget))
            {
                return;
            }

            FiltersPopup.IsOpen = false;
        }

        // Walks up through visual, logical and templated parents, and from a context menu to the
        // element it opened from.
        private static bool IsWithin(DependencyObject element, DependencyObject ancestor)
        {
            if (ancestor == null)
            {
                return false;
            }

            for (var current = element; current != null;)
            {
                if (ReferenceEquals(current, ancestor))
                {
                    return true;
                }

                if (current is ContextMenu menu)
                {
                    current = menu.PlacementTarget;
                    continue;
                }

                DependencyObject next = null;
                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    next = VisualTreeHelper.GetParent(current);
                }

                next = next ?? LogicalTreeHelper.GetParent(current) ?? (current as FrameworkElement)?.TemplatedParent;
                current = next;
            }

            return false;
        }

        private void FiltersPanel_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                FiltersPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        private void FiltersClear_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _panelItems.ToList())
            {
                item.ClearSelection();
            }

            UpdateFiltersButton();
        }

        private void ProviderFilter_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var filter = button?.DataContext as GridProviderPlatformFilter;
            var menu = button?.ContextMenu;
            if (button == null || filter == null || menu == null)
            {
                return;
            }

            menu.ItemsSource = filter.Groups;
            menu.Tag = filter;
            MultiSelectFilterMenu.OpenSelectorContextMenu(button, menu, allowEmptyItems: true);
        }

        private void ProviderFilterContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            if ((sender as ContextMenu)?.Tag is GridProviderPlatformFilter filter)
            {
                filter.OnClosed();
            }
        }

        private static void CollectControllerElements(DependencyObject parent, IList<UIElement> elements)
        {
            if (parent == null || elements == null)
            {
                return;
            }

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBox || child is Button || child is CheckBox)
                {
                    if (child is UIElement element)
                    {
                        elements.Add(element);
                    }
                }

                CollectControllerElements(child, elements);
            }
        }

        private static bool IsControllerElementAvailable(UIElement element)
        {
            return element != null &&
                   element.IsVisible &&
                   element.IsEnabled &&
                   element.Focusable;
        }
    }

}
