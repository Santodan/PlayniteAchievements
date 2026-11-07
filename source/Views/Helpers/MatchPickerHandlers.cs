using PlayniteAchievements.ViewModels.ManageAchievements;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// The event handlers behind a pairing dropdown (a popup holding a search box over a list),
    /// whose data context is a <see cref="MatchPickerItem"/>. The windows that host one forward
    /// their handlers here.
    /// </summary>
    internal static class MatchPickerHandlers
    {
        /// <summary>Types straight into the search box when a dropdown opens.</summary>
        public static void PopupOpened(object sender)
        {
            var search = FindDescendant<TextBox>((sender as Popup)?.Child);
            if (search != null)
            {
                search.Dispatcher.BeginInvoke(new Action(() =>
                {
                    search.Focus();
                    Keyboard.Focus(search);
                }), DispatcherPriority.Input);
            }
        }

        /// <summary>Enter picks the first row the search left; Escape closes the dropdown.</summary>
        public static void SearchPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is MatchPickerItem item))
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                var first = item.Options.FirstOrDefault(option => !option.IsSpecial);
                if (first != null)
                {
                    item.Pick(first);
                }

                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                item.IsPickerOpen = false;
                e.Handled = true;
            }
        }

        /// <summary>
        /// A click on a row picks it. Taken on mouse-up from the row the pointer is over, so a
        /// click on the scrollbar or between rows picks nothing.
        /// </summary>
        public static void ListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var list = sender as ListBox;
            var container = list == null ? null : ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (container?.DataContext is MatchOption option && list.DataContext is MatchPickerItem item)
            {
                item.Pick(option);
                e.Handled = true;
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match)
                {
                    return match;
                }

                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }
    }
}
