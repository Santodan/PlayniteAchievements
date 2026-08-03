using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PlayniteAchievements.Services.UI;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

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

        private void ActionButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is GridActionButton item)
            {
                item.Invoke();
            }
        }

        private void MultiSelectFilter_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var filter = button?.DataContext as GridMultiSelectFilter;
            var menu = button?.ContextMenu;
            if (button == null || filter == null || menu == null)
            {
                return;
            }

            menu.Items.Clear();
            var itemStyle = button.TryFindResource("AchievementMultiSelectMenuItemStyle") as Style;
            foreach (var option in filter.Options?.Where(value => !string.IsNullOrWhiteSpace(value)) ?? Enumerable.Empty<string>())
            {
                var value = option;
                var item = new MenuItem
                {
                    Header = BuildMultiSelectHeader(filter, value),
                    IsCheckable = true,
                    StaysOpenOnClick = true,
                    IsChecked = filter.IsSelected(value),
                    Tag = value
                };
                if (itemStyle != null)
                {
                    item.Style = itemStyle;
                }

                item.Click += (_, __) =>
                {
                    filter.SetSelected(value, item.IsChecked);

                    // Re-sync every sibling checkmark from the filter while the menu stays
                    // open: single-select-style filters (e.g. Compare) uncheck the previous
                    // option when a new one is selected. No-op for plain multi-selects.
                    foreach (var sibling in menu.Items.OfType<MenuItem>())
                    {
                        if (sibling.Tag is string optionValue)
                        {
                            sibling.IsChecked = filter.IsSelected(optionValue);
                        }
                    }
                };
                menu.Items.Add(item);
            }

            OpenSelectorContextMenu(button, menu);
        }

        // Plain string header for ordinary dropdowns; for a marker-aware filter (e.g. the friend
        // Compare dropdown) render a fixed-width star gutter before the label so marked options show
        // a star while every label stays aligned (unmarked options keep a transparent star).
        private static object BuildMultiSelectHeader(GridMultiSelectFilter filter, string value)
        {
            var label = filter.GetDisplayLabel(value);
            if (!filter.HasMarker)
            {
                return label;
            }

            var panel = new DockPanel { LastChildFill = true };
            var star = new TextBlock
            {
                Text = "★",
                Width = 14,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = filter.IsMarked(value) ? 1d : 0d
            };
            DockPanel.SetDock(star, Dock.Left);
            panel.Children.Add(star);
            panel.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center
            });
            return panel;
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
            OpenSelectorContextMenu(button, menu, allowEmptyItems: true);
        }

        private void ProviderFilterContextMenu_Closed(object sender, RoutedEventArgs e)
        {
            if ((sender as ContextMenu)?.Tag is GridProviderPlatformFilter filter)
            {
                filter.OnClosed();
            }
        }

        private static void OpenSelectorContextMenu(Button button, ContextMenu menu, bool allowEmptyItems = false)
        {
            if (button == null || menu == null || (!allowEmptyItems && menu.Items.Count == 0))
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
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 0;
            if (button.IsKeyboardFocusWithin)
            {
                FullscreenControllerNavigationService.OpenContextMenu(button, menu);
            }
            else
            {
                menu.IsOpen = true;
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
