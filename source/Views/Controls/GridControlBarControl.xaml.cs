using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
