using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Keyboard-focus inspection shared by hotkey handlers so bare-letter shortcuts
    /// never fire while the user is typing in a text input.
    /// </summary>
    internal static class KeyboardFocusScope
    {
        public static bool IsTextInputFocused()
        {
            var element = Keyboard.FocusedElement as DependencyObject;
            while (element != null)
            {
                if (element is TextBoxBase ||
                    element is PasswordBox ||
                    element is RichTextBox)
                {
                    return true;
                }

                if (element is ComboBox comboBox && comboBox.IsEditable)
                {
                    return true;
                }

                element = GetParent(element);
            }

            return false;
        }

        private static DependencyObject GetParent(DependencyObject element)
        {
            if (element == null)
            {
                return null;
            }

            try
            {
                if (element is Visual || element is Visual3D)
                {
                    var parent = VisualTreeHelper.GetParent(element);
                    if (parent != null)
                    {
                        return parent;
                    }
                }
            }
            catch
            {
            }

            return LogicalTreeHelper.GetParent(element);
        }
    }
}
