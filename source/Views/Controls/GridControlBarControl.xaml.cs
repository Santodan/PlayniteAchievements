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

        /// <summary>The bar's items less the ones its grid options hide.</summary>
        public System.ComponentModel.ICollectionView ShownItems => (System.ComponentModel.ICollectionView)GetValue(ShownItemsPropertyKey.DependencyProperty);

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
        /// The items a grid's Display Settings list under Show Control Bar: the search box and
        /// every item with an id, in bar order.
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

            foreach (var item in bar.LeadingItems.Concat(bar.Items))
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

            var view = e.NewValue is GridControlBarViewModel bar
                ? new ListCollectionView(bar.Items) { Filter = control.IsShown }
                : null;
            control.SetValue(ShownItemsPropertyKey, view);
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
                e.PropertyName == nameof(PlayniteAchievements.Models.Settings.GridCommonOptions.HiddenControlBarItems))
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
        }

        // Items added later (the category mode toggle) can make a fully hidden bar show again.
        private void ControlBarItems_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) =>
            UpdateVisibility();

        private void ApplyHiddenItems()
        {
            ShownItems?.Refresh();
            UpdateShownSearch();
            UpdateVisibility();
            ShownItemsChanged?.Invoke(this, EventArgs.Empty);
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
