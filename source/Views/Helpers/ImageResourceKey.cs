using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows the image resource a bound key names, as a dynamic resource reference, so a badge
    /// recolor reaches it.
    /// </summary>
    public static class ImageResourceKey
    {
        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(ImageResourceKey),
            new PropertyMetadata(null, OnKeyChanged));

        public static string GetKey(DependencyObject element) => (string)element.GetValue(KeyProperty);

        public static void SetKey(DependencyObject element, string value) => element.SetValue(KeyProperty, value);

        private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is Image image))
            {
                return;
            }

            if (e.NewValue is string key && !string.IsNullOrWhiteSpace(key))
            {
                image.SetResourceReference(Image.SourceProperty, key);
            }
            else
            {
                image.ClearValue(Image.SourceProperty);
            }
        }
    }
}