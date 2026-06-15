using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace PlayniteAchievements.Views.Helpers
{
    public static class AchievementNoteInlineFormatter
    {
        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.RegisterAttached(
                "FormattedText",
                typeof(string),
                typeof(AchievementNoteInlineFormatter),
                new PropertyMetadata(string.Empty, OnFormattedTextChanged));

        public static string GetFormattedText(DependencyObject element)
        {
            return (string)element.GetValue(FormattedTextProperty);
        }

        public static void SetFormattedText(DependencyObject element, string value)
        {
            element.SetValue(FormattedTextProperty, value ?? string.Empty);
        }

        public static void ApplyFormattedText(TextBlock textBlock, string value)
        {
            if (textBlock == null)
            {
                return;
            }

            textBlock.Inlines.Clear();

            var text = (value ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    textBlock.Inlines.Add(new LineBreak());
                }

                AppendFormattedText(textBlock.Inlines, lines[i], bold: false, italic: false, underline: false);
            }
        }

        private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                ApplyFormattedText(textBlock, e.NewValue as string);
            }
        }

        private static void AppendFormattedText(
            InlineCollection inlines,
            string text,
            bool bold,
            bool italic,
            bool underline)
        {
            if (inlines == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var index = 0;
            while (index < text.Length)
            {
                var marker = FindNextMarker(text, index);
                if (marker.Start < 0)
                {
                    AddRun(inlines, text.Substring(index), bold, italic, underline);
                    return;
                }

                if (marker.Start > index)
                {
                    AddRun(inlines, text.Substring(index, marker.Start - index), bold, italic, underline);
                }

                var contentStart = marker.Start + marker.Token.Length;
                var end = text.IndexOf(marker.Token, contentStart, StringComparison.Ordinal);
                if (end < 0)
                {
                    AddRun(inlines, text.Substring(marker.Start), bold, italic, underline);
                    return;
                }

                AppendFormattedText(
                    inlines,
                    text.Substring(contentStart, end - contentStart),
                    bold || marker.Kind == MarkerKind.Bold,
                    italic || marker.Kind == MarkerKind.Italic,
                    underline || marker.Kind == MarkerKind.Underline);

                index = end + marker.Token.Length;
            }
        }

        private static Marker FindNextMarker(string text, int startIndex)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                if (i + 1 < text.Length &&
                    text[i] == '*' &&
                    text[i + 1] == '*')
                {
                    return new Marker(i, "**", MarkerKind.Bold);
                }

                if (i + 1 < text.Length &&
                    text[i] == '_' &&
                    text[i + 1] == '_')
                {
                    return new Marker(i, "__", MarkerKind.Underline);
                }

                if (text[i] == '*')
                {
                    return new Marker(i, "*", MarkerKind.Italic);
                }
            }

            return new Marker(-1, string.Empty, MarkerKind.Italic);
        }

        private static void AddRun(
            InlineCollection inlines,
            string text,
            bool bold,
            bool italic,
            bool underline)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            var run = new Run(text);
            if (bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            if (underline)
            {
                run.TextDecorations = TextDecorations.Underline;
            }

            inlines.Add(run);
        }

        private enum MarkerKind
        {
            Bold,
            Italic,
            Underline
        }

        private readonly struct Marker
        {
            public Marker(int start, string token, MarkerKind kind)
            {
                Start = start;
                Token = token;
                Kind = kind;
            }

            public int Start { get; }

            public string Token { get; }

            public MarkerKind Kind { get; }
        }
    }
}
