using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Shows a font-family <see cref="ComboBox"/>'s picked font in that font, and searches its
    /// options by family name.
    /// </summary>
    /// <remarks>
    /// The search box itself is the one every long drop-down has (<see cref="SelectorSearch"/>); this
    /// only points it at the family name, which the item template does not expose as text.
    /// </remarks>
    internal static class FontPickerBehavior
    {
        public static readonly DependencyProperty PreviewSelectedFontProperty =
            DependencyProperty.RegisterAttached(
                "PreviewSelectedFont",
                typeof(bool),
                typeof(FontPickerBehavior),
                new PropertyMetadata(false, OnPreviewSelectedFontChanged));

        public static void SetPreviewSelectedFont(DependencyObject element, bool value)
        {
            element.SetValue(PreviewSelectedFontProperty, value);
        }

        public static bool GetPreviewSelectedFont(DependencyObject element)
        {
            return (bool)element.GetValue(PreviewSelectedFontProperty);
        }

        private static void OnPreviewSelectedFontChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ComboBox combo))
            {
                return;
            }

            combo.SelectionChanged -= OnSelectionChanged;
            combo.Loaded -= OnLoaded;
            if (!(e.NewValue is bool enabled) || !enabled)
            {
                combo.ClearValue(Control.FontFamilyProperty);
                return;
            }

            TextSearch.SetTextPath(combo, nameof(FontFamilyOption.DisplayName));
            combo.SelectionChanged += OnSelectionChanged;
            combo.Loaded += OnLoaded;
            ApplySelectedPreviewFont(combo);
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            ApplySelectedPreviewFont((ComboBox)sender);
        }

        private static void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplySelectedPreviewFont((ComboBox)sender);
        }

        private static void ApplySelectedPreviewFont(ComboBox combo)
        {
            var preview = (combo.SelectedItem as FontFamilyOption)?.PreviewFamily;
            if (preview == null)
            {
                combo.ClearValue(Control.FontFamilyProperty);
                return;
            }

            combo.FontFamily = preview;
        }
    }
}
