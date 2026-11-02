using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows the image a bound key names: an image resource, as a dynamic resource reference so a
    /// badge recolor reaches it, or a tag icon ("TagIcon:<codepoint>|<color>", see
    /// TagControlBarFilter.IconKey) drawn from the icon font, in the text color when the tag has none.
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

            if (e.NewValue is string key && key.StartsWith(Converters.ProviderIconConverter.TagIconPrefix, System.StringComparison.Ordinal))
            {
                var parts = key.Split('|');
                var color = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
                    ? parts[1]
                    : ((image.TryFindResource("PlayAch.Brush.Text") as System.Windows.Media.SolidColorBrush)?.Color ?? System.Windows.Media.Colors.White).ToString();
                image.Source = Converters.ProviderIconConverter.BuildIcon(parts[0], color);
            }
            else if (e.NewValue is string key2 && !string.IsNullOrWhiteSpace(key2))
            {
                image.SetResourceReference(Image.SourceProperty, key2);
            }
            else
            {
                image.ClearValue(Image.SourceProperty);
            }
        }
    }
}