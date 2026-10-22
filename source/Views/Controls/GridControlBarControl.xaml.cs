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
                new PropertyMetadata(null, OnVisibilityPropertyChanged));

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

        private void UpdateVisibility()
        {
            Visibility = ShowControlBar && ControlBar != null
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
