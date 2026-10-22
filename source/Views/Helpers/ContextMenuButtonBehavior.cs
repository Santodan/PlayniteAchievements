using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Makes a button open its own ContextMenu below itself on a left click, Enter or Space,
    /// instead of clicking. A ToggleButton keeps its IsChecked binding, so it can show whether
    /// the menu's choice is on.
    /// </summary>
    public static class ContextMenuButtonBehavior
    {
        public static readonly DependencyProperty OpensMenuProperty = DependencyProperty.RegisterAttached(
            "OpensMenu",
            typeof(bool),
            typeof(ContextMenuButtonBehavior),
            new PropertyMetadata(false, OnOpensMenuChanged));

        public static bool GetOpensMenu(DependencyObject element) => (bool)element.GetValue(OpensMenuProperty);

        public static void SetOpensMenu(DependencyObject element, bool value) => element.SetValue(OpensMenuProperty, value);

        private static void OnOpensMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ButtonBase button))
            {
                return;
            }

            button.PreviewMouseLeftButtonDown -= Button_PreviewMouseLeftButtonDown;
            button.PreviewKeyDown -= Button_PreviewKeyDown;
            if ((bool)e.NewValue)
            {
                button.PreviewMouseLeftButtonDown += Button_PreviewMouseLeftButtonDown;
                button.PreviewKeyDown += Button_PreviewKeyDown;
            }
        }

        private static void Button_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = Open(sender as ButtonBase);
        }

        private static void Button_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter || e.Key == Key.Space)
            {
                e.Handled = Open(sender as ButtonBase);
            }
        }

        private static bool Open(ButtonBase button)
        {
            var menu = button?.ContextMenu;
            if (menu == null)
            {
                return false;
            }

            SelectorContextMenuHelper.Open(button, menu);
            return true;
        }
    }
}