using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Attaches a tooltip showing the full header/body text of a cell, shown only when the
    /// cell's text is actually clipped (trimmed, capped by MaxHeight, or cut by the row height).
    /// An optional note section is appended whenever a note exists, independent of clipping.
    /// Like <see cref="AchievementNoteInlineFormatter.NoteToolTipProperty"/>, the tooltip
    /// content is built lazily on open to keep per-cell cost low in virtualized grids.
    /// </summary>
    public static class ClippedTextToolTipHelper
    {
        private const double ClipTolerance = 1.0;
        private const double ToolTipMaxWidth = 560;

        public static readonly DependencyProperty HeaderTextProperty =
            DependencyProperty.RegisterAttached(
                "HeaderText",
                typeof(string),
                typeof(ClippedTextToolTipHelper),
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
                typeof(ClippedTextToolTipHelper),
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
                typeof(ClippedTextToolTipHelper),
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
            element.MouseEnter -= OnMouseEnter;
            element.ClearValue(ToolTipService.IsEnabledProperty);

            if (string.IsNullOrWhiteSpace(GetHeaderText(element)) &&
                string.IsNullOrWhiteSpace(GetBodyText(element)) &&
                string.IsNullOrWhiteSpace(GetNoteText(element)))
            {
                element.ClearValue(FrameworkElement.ToolTipProperty);
                return;
            }

            element.ToolTipOpening += OnToolTipOpening;
            element.MouseEnter += OnMouseEnter;
            if (!(element.ToolTip is ToolTip))
            {
                // Lightweight placeholder; its content is built on open.
                element.ToolTip = new ToolTip();
            }
        }

        /// <summary>
        /// Decides on hover-enter whether this cell should act as a tooltip owner at all.
        /// Cancelling later via ToolTipOpening (e.Handled) makes the tooltip service treat the
        /// cell as an owner that showed nothing: it consumes the quick-show window that lets
        /// tooltips chain instantly between adjacent cells, so sweeping across a row stops
        /// updating the tooltip. Disabling via ToolTipService.IsEnabled instead makes an
        /// unclipped cell equivalent to a cell with no tooltip. MouseEnter is raised during
        /// hit-test processing, before the tooltip service inspects the element.
        /// </summary>
        private static void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!(sender is FrameworkElement element))
            {
                return;
            }

            var active = !string.IsNullOrWhiteSpace(GetNoteText(element)) || IsAnyTextClipped(element);
            ToolTipService.SetIsEnabled(element, active);
        }

        private static void OnToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.ToolTip is ToolTip toolTip))
            {
                return;
            }

            var note = GetNoteText(element);
            var hasNote = !string.IsNullOrWhiteSpace(note);
            var clipped = IsAnyTextClipped(element);
            if (!clipped && !hasNote)
            {
                e.Handled = true;
                return;
            }

            // Rebuilt on each open; the bound values change on hidden-reveal toggles.
            var panel = new StackPanel();
            if (clipped)
            {
                AddTextBlock(panel, GetHeaderText(element), FontWeights.SemiBold, topMargin: 0);
                AddTextBlock(panel, GetBodyText(element), FontWeights.Normal, topMargin: panel.Children.Count > 0 ? 3 : 0);
            }

            if (hasNote)
            {
                var noteBlock = CreateTextBlock(FontWeights.Normal, topMargin: panel.Children.Count > 0 ? 6 : 0);
                AchievementNoteInlineFormatter.ApplyFormattedText(noteBlock, note);
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

        private static bool IsAnyTextClipped(FrameworkElement element)
        {
            if (element is TextBlock single)
            {
                return IsTextBlockSelfClipped(single) || IsClippedByCell(single);
            }

            return IsAnyDescendantTextClipped(element, depth: 0) || IsClippedByCell(element);
        }

        private static bool IsAnyDescendantTextClipped(DependencyObject parent, int depth)
        {
            if (parent == null || depth > 4)
            {
                return false;
            }

            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock textBlock)
                {
                    if (IsTextBlockSelfClipped(textBlock))
                    {
                        return true;
                    }
                }
                else if (IsAnyDescendantTextClipped(child, depth + 1))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Detects overflow hidden by TextTrimming or a MaxHeight cap: measures the element
        /// unconstrained in height at its current width and compares against the arranged height.
        /// The target TextBlocks all wrap, so overflow is height-only.
        /// </summary>
        private static bool IsTextBlockSelfClipped(TextBlock textBlock)
        {
            if (textBlock == null || !textBlock.IsVisible || textBlock.ActualWidth <= 0)
            {
                return false;
            }

            var hasMaxHeightCap = !double.IsPositiveInfinity(textBlock.MaxHeight);
            var localMaxHeight = textBlock.ReadLocalValue(FrameworkElement.MaxHeightProperty);
            try
            {
                if (hasMaxHeightCap)
                {
                    // MaxHeight clamps DesiredSize, hiding the overflow being measured.
                    textBlock.SetValue(FrameworkElement.MaxHeightProperty, double.PositiveInfinity);
                }

                textBlock.Measure(new Size(
                    textBlock.ActualWidth + textBlock.Margin.Left + textBlock.Margin.Right,
                    double.PositiveInfinity));
                var fullHeight = textBlock.DesiredSize.Height - textBlock.Margin.Top - textBlock.Margin.Bottom;
                return fullHeight > textBlock.ActualHeight + ClipTolerance;
            }
            finally
            {
                if (hasMaxHeightCap)
                {
                    if (localMaxHeight == DependencyProperty.UnsetValue)
                    {
                        // Restores a style-provided MaxHeight setter.
                        textBlock.ClearValue(FrameworkElement.MaxHeightProperty);
                    }
                    else
                    {
                        textBlock.SetValue(FrameworkElement.MaxHeightProperty, localMaxHeight);
                    }
                }

                textBlock.InvalidateMeasure();
            }
        }

        /// <summary>
        /// Detects text cut off by a fixed row height: panels give children unconstrained
        /// height during measure, so that clip never shows up in the self check.
        /// </summary>
        private static bool IsClippedByCell(FrameworkElement element)
        {
            var cell = FindVisualAncestor<DataGridCell>(element);
            if (cell == null)
            {
                return false;
            }

            var bounds = element.TransformToAncestor(cell).TransformBounds(new Rect(element.RenderSize));
            return bounds.Bottom > cell.ActualHeight + ClipTolerance;
        }

        private static T FindVisualAncestor<T>(DependencyObject element) where T : DependencyObject
        {
            var current = element;
            while (current != null)
            {
                current = VisualTreeHelper.GetParent(current);
                if (current is T match)
                {
                    return match;
                }
            }

            return null;
        }
    }
}
