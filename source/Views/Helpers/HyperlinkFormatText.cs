using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Composes a TextBlock from a localized format string containing a {0} placeholder
    /// and an inline hyperlink inserted at the placeholder position. Keeps whole sentences
    /// in one localization key so translators control word order around the link.
    /// </summary>
    public static class HyperlinkFormatText
    {
        public static readonly DependencyProperty FormatProperty = DependencyProperty.RegisterAttached(
            "Format", typeof(string), typeof(HyperlinkFormatText), new PropertyMetadata(null, OnPartChanged));

        public static readonly DependencyProperty LinkTextProperty = DependencyProperty.RegisterAttached(
            "LinkText", typeof(string), typeof(HyperlinkFormatText), new PropertyMetadata(null, OnPartChanged));

        public static readonly DependencyProperty NavigateUriProperty = DependencyProperty.RegisterAttached(
            "NavigateUri", typeof(string), typeof(HyperlinkFormatText), new PropertyMetadata(null, OnPartChanged));

        public static string GetFormat(DependencyObject obj) => (string)obj.GetValue(FormatProperty);

        public static void SetFormat(DependencyObject obj, string value) => obj.SetValue(FormatProperty, value);

        public static string GetLinkText(DependencyObject obj) => (string)obj.GetValue(LinkTextProperty);

        public static void SetLinkText(DependencyObject obj, string value) => obj.SetValue(LinkTextProperty, value);

        public static string GetNavigateUri(DependencyObject obj) => (string)obj.GetValue(NavigateUriProperty);

        public static void SetNavigateUri(DependencyObject obj, string value) => obj.SetValue(NavigateUriProperty, value);

        private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                Rebuild(textBlock);
            }
        }

        private static void Rebuild(TextBlock textBlock)
        {
            var format = GetFormat(textBlock);
            if (string.IsNullOrEmpty(format))
            {
                return;
            }

            var linkText = GetLinkText(textBlock) ?? string.Empty;
            var uriText = GetNavigateUri(textBlock);

            textBlock.Inlines.Clear();

            var placeholderIndex = format.IndexOf("{0}", StringComparison.Ordinal);
            if (placeholderIndex < 0 ||
                string.IsNullOrWhiteSpace(uriText) ||
                !Uri.TryCreate(uriText, UriKind.Absolute, out var uri))
            {
                textBlock.Inlines.Add(new Run(format.Replace("{0}", linkText)));
                return;
            }

            var prefix = format.Substring(0, placeholderIndex);
            var suffix = format.Substring(placeholderIndex + 3);

            if (prefix.Length > 0)
            {
                textBlock.Inlines.Add(new Run(prefix));
            }

            var hyperlink = new Hyperlink(new Run(linkText))
            {
                NavigateUri = uri,
                TextDecorations = System.Windows.TextDecorations.Underline,
                FontWeight = FontWeights.SemiBold
            };
            hyperlink.SetResourceReference(TextElement.ForegroundProperty, "PlayAch.Brush.ActionLink");
            hyperlink.RequestNavigate += (sender, args) =>
            {
                Process.Start(args.Uri.AbsoluteUri);
                args.Handled = true;
            };
            textBlock.Inlines.Add(hyperlink);

            if (suffix.Length > 0)
            {
                textBlock.Inlines.Add(new Run(suffix));
            }
        }
    }
}
