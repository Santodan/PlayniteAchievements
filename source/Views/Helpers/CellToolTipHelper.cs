using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attaches a tooltip showing the full header/body text of a cell, with an optional
    /// formatted note section appended. Like
    /// <see cref="InlineMarkdownFormatter.NoteToolTipProperty"/>, the tooltip content is
    /// built lazily on open to keep per-cell cost low in virtualized grids.
    /// </summary>
    public static class CellToolTipHelper
    {
        private const double ToolTipMaxWidth = 560;

        public static readonly DependencyProperty HeaderTextProperty =
            DependencyProperty.RegisterAttached(
                "HeaderText",
                typeof(string),
                typeof(CellToolTipHelper),
                new PropertyMetadata(null, OnTextChanged));

        public static string GetHeaderText(DependencyObject element)
        {
            return (string)element.GetValue(HeaderTextProperty);
        }

        public static void SetHeaderText(DependencyObject element, string value)
        {
            element.SetValue(HeaderTextProperty, value);
        }

        public static readonly DependencyProperty BodyTextProperty =
            DependencyProperty.RegisterAttached(
                "BodyText",
                typeof(string),
                typeof(CellToolTipHelper),
                new PropertyMetadata(null, OnTextChanged));

        public static string GetBodyText(DependencyObject element)
        {
            return (string)element.GetValue(BodyTextProperty);
        }

        public static void SetBodyText(DependencyObject element, string value)
        {
            element.SetValue(BodyTextProperty, value);
        }

        public static readonly DependencyProperty NoteTextProperty =
            DependencyProperty.RegisterAttached(
                "NoteText",
                typeof(string),
                typeof(CellToolTipHelper),
                new PropertyMetadata(null, OnTextChanged));

        public static string GetNoteText(DependencyObject element)
        {
            return (string)element.GetValue(NoteTextProperty);
        }

        public static void SetNoteText(DependencyObject element, string value)
        {
            element.SetValue(NoteTextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            element.ToolTipOpening -= OnToolTipOpening;

            if (string.IsNullOrWhiteSpace(GetHeaderText(element)) &&
                string.IsNullOrWhiteSpace(GetBodyText(element)) &&
                string.IsNullOrWhiteSpace(GetNoteText(element)))
            {
                element.ClearValue(FrameworkElement.ToolTipProperty);
                return;
            }

            element.ToolTipOpening += OnToolTipOpening;
            if (!(element.ToolTip is ToolTip))
            {
                // Lightweight placeholder; its content is built on open.
                element.ToolTip = new ToolTip();
            }
        }

        private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.ToolTip is ToolTip toolTip))
            {
                return;
            }

            // Rebuilt on each open; the bound values change on hidden-reveal toggles.
            var panel = new StackPanel();
            AddTextBlock(panel, GetHeaderText(element), FontWeights.SemiBold, topMargin: 0);
            AddTextBlock(panel, GetBodyText(element), FontWeights.Normal, topMargin: panel.Children.Count > 0 ? 3 : 0);

            var note = GetNoteText(element);
            if (!string.IsNullOrWhiteSpace(note))
            {
                var noteBlock = CreateTextBlock(FontWeights.Normal, topMargin: panel.Children.Count > 0 ? 6 : 0);
                InlineMarkdownFormatter.ApplyFormattedText(noteBlock, note);
                panel.Children.Add(noteBlock);
            }

            if (panel.Children.Count == 0)
            {
                e.Handled = true;
                return;
            }

            toolTip.Content = panel;
        }

        private static void AddTextBlock(StackPanel panel, string text, FontWeight weight, double topMargin)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var block = CreateTextBlock(weight, topMargin);
            block.Text = text;
            panel.Children.Add(block);
        }

        private static TextBlock CreateTextBlock(FontWeight weight, double topMargin)
        {
            var block = new TextBlock
            {
                MaxWidth = ToolTipMaxWidth,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = weight,
                Margin = new Thickness(0, topMargin, 0, 0)
            };
            block.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            return block;
        }
    }
}
