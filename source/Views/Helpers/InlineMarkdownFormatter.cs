using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Navigation;

namespace PlayniteAchievements.Views.Helpers
{
    public static class InlineMarkdownFormatter
    {
        private const int MaxCachedFormattedTexts = 256;
        private static readonly object FormattedTextCacheSync = new object();
        private static readonly Dictionary<string, IReadOnlyList<InlineToken>> FormattedTextCache =
            new Dictionary<string, IReadOnlyList<InlineToken>>(StringComparer.Ordinal);
        private static readonly Queue<string> FormattedTextCacheKeys = new Queue<string>();

        public static readonly DependencyProperty FormattedTextProperty =
            DependencyProperty.RegisterAttached(
                "FormattedText",
                typeof(string),
                typeof(InlineMarkdownFormatter),
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
            ApplyInlines(textBlock, value, parseLinks: true);
        }

        private static void OnFormattedTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                ApplyFormattedText(textBlock, e.NewValue as string);
            }
        }

        /// <summary>
        /// Renders inline markdown emphasis (**bold**, *italic*, __underline__,
        /// ~~strikethrough~~) without link detection, for surfaces where hyperlinks are not
        /// interactive — the unlock notification and screenshot-frame text lines.
        /// </summary>
        public static readonly DependencyProperty MarkupTextProperty =
            DependencyProperty.RegisterAttached(
                "MarkupText",
                typeof(string),
                typeof(InlineMarkdownFormatter),
                new PropertyMetadata(string.Empty, OnMarkupTextChanged));

        public static string GetMarkupText(DependencyObject element)
        {
            return (string)element.GetValue(MarkupTextProperty);
        }

        public static void SetMarkupText(DependencyObject element, string value)
        {
            element.SetValue(MarkupTextProperty, value ?? string.Empty);
        }

        private static void OnMarkupTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is TextBlock textBlock)
            {
                ApplyInlines(textBlock, e.NewValue as string, parseLinks: false);
            }
        }

        private static void ApplyInlines(TextBlock textBlock, string value, bool parseLinks)
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

            var tokens = GetFormattedTokens(text, parseLinks);
            for (var i = 0; i < tokens.Count; i++)
            {
                AddInline(textBlock.Inlines, tokens[i]);
            }
        }

        /// <summary>
        /// Attaches a formatted note tooltip that is built lazily on hover. Unlike an inline
        /// &lt;ToolTip&gt; in a cell template (which is instantiated for every realized cell, including
        /// empty ones), this creates no tooltip object when the note is empty and defers building
        /// the formatted content until the tooltip actually opens. This keeps the per-cell cost of a
        /// visible note column low when many rows are realized.
        /// </summary>
        public static readonly DependencyProperty NoteToolTipProperty =
            DependencyProperty.RegisterAttached(
                "NoteToolTip",
                typeof(string),
                typeof(InlineMarkdownFormatter),
                new PropertyMetadata(null, OnNoteToolTipChanged));

        public static string GetNoteToolTip(DependencyObject element)
        {
            return (string)element.GetValue(NoteToolTipProperty);
        }

        public static void SetNoteToolTip(DependencyObject element, string value)
        {
            element.SetValue(NoteToolTipProperty, value);
        }

        private static void OnNoteToolTipChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is FrameworkElement element))
            {
                return;
            }

            element.ToolTipOpening -= OnNoteToolTipOpening;

            if (string.IsNullOrWhiteSpace(e.NewValue as string))
            {
                element.ClearValue(FrameworkElement.ToolTipProperty);
                return;
            }

            element.ToolTipOpening += OnNoteToolTipOpening;
            if (!(element.ToolTip is ToolTip))
            {
                // Lightweight placeholder; its content is built on first open.
                element.ToolTip = new ToolTip();
            }
        }

        private static void OnNoteToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (!(sender is FrameworkElement element) || !(element.ToolTip is ToolTip toolTip))
            {
                return;
            }

            var note = GetNoteToolTip(element);
            if (string.IsNullOrWhiteSpace(note))
            {
                e.Handled = true;
                return;
            }

            if (!(toolTip.Content is TextBlock content))
            {
                content = new TextBlock
                {
                    MaxWidth = 560,
                    TextWrapping = System.Windows.TextWrapping.Wrap
                };
                content.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
                toolTip.Content = content;
            }

            ApplyFormattedText(content, note);
        }

        private static IReadOnlyList<InlineToken> GetFormattedTokens(string text, bool parseLinks)
        {
            // Link-free parses share the cache under a key that cannot collide with note text
            // (notes are normalized to '\n' newlines, so a leading '\0' never occurs naturally).
            var cacheKey = parseLinks ? text : "\0" + text;
            lock (FormattedTextCacheSync)
            {
                if (FormattedTextCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }
            }

            var tokens = ParseFormattedTokens(text, parseLinks);
            lock (FormattedTextCacheSync)
            {
                if (FormattedTextCache.TryGetValue(cacheKey, out var cached))
                {
                    return cached;
                }

                FormattedTextCache[cacheKey] = tokens;
                FormattedTextCacheKeys.Enqueue(cacheKey);
                while (FormattedTextCache.Count > MaxCachedFormattedTexts &&
                       FormattedTextCacheKeys.Count > 0)
                {
                    var oldestKey = FormattedTextCacheKeys.Dequeue();
                    FormattedTextCache.Remove(oldestKey);
                }
            }

            return tokens;
        }

        private static IReadOnlyList<InlineToken> ParseFormattedTokens(string text, bool parseLinks)
        {
            var tokens = new List<InlineToken>();
            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0)
                {
                    tokens.Add(InlineToken.CreateLineBreak());
                }

                AppendFormattedTokens(
                    tokens, lines[i],
                    bold: false, italic: false, underline: false, strike: false,
                    parseLinks: parseLinks);
            }

            return tokens;
        }

        private static void AppendFormattedTokens(
            IList<InlineToken> tokens,
            string text,
            bool bold,
            bool italic,
            bool underline,
            bool strike,
            bool parseLinks)
        {
            if (tokens == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var index = 0;
            while (index < text.Length)
            {
                var marker = FindNextMarker(text, index);
                if (marker.Start < 0)
                {
                    AddRunTokens(tokens, text.Substring(index), bold, italic, underline, strike, parseLinks);
                    return;
                }

                if (marker.Start > index)
                {
                    AddRunTokens(tokens, text.Substring(index, marker.Start - index), bold, italic, underline, strike, parseLinks);
                }

                var contentStart = marker.Start + marker.Token.Length;
                var end = text.IndexOf(marker.Token, contentStart, StringComparison.Ordinal);
                if (end < 0)
                {
                    AddRunTokens(tokens, text.Substring(marker.Start), bold, italic, underline, strike, parseLinks);
                    return;
                }

                AppendFormattedTokens(
                    tokens,
                    text.Substring(contentStart, end - contentStart),
                    bold || marker.Kind == MarkerKind.Bold,
                    italic || marker.Kind == MarkerKind.Italic,
                    underline || marker.Kind == MarkerKind.Underline,
                    strike || marker.Kind == MarkerKind.Strikethrough,
                    parseLinks);

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

                if (i + 1 < text.Length &&
                    text[i] == '~' &&
                    text[i + 1] == '~')
                {
                    return new Marker(i, "~~", MarkerKind.Strikethrough);
                }

                if (text[i] == '*')
                {
                    return new Marker(i, "*", MarkerKind.Italic);
                }
            }

            return new Marker(-1, string.Empty, MarkerKind.Italic);
        }

        private static void AddRunTokens(
            IList<InlineToken> tokens,
            string text,
            bool bold,
            bool italic,
            bool underline,
            bool strike,
            bool parseLinks)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!parseLinks)
            {
                AddTextToken(tokens, text, bold, italic, underline, strike);
                return;
            }

            var index = 0;
            while (index < text.Length)
            {
                var link = FindNextLink(text, index);
                if (link.Start < 0)
                {
                    AddTextToken(tokens, text.Substring(index), bold, italic, underline, strike);
                    return;
                }

                if (link.Start > index)
                {
                    AddTextToken(tokens, text.Substring(index, link.Start - index), bold, italic, underline, strike);
                }

                AddHyperlinkToken(tokens, link.DisplayText, link.NavigateUri, bold, italic, underline, strike);
                index = link.End;
            }
        }

        private static void AddTextToken(
            IList<InlineToken> tokens,
            string text,
            bool bold,
            bool italic,
            bool underline,
            bool strike)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            tokens.Add(InlineToken.CreateText(text, bold, italic, underline, strike));
        }

        private static void AddHyperlinkToken(
            IList<InlineToken> tokens,
            string displayText,
            Uri navigateUri,
            bool bold,
            bool italic,
            bool underline,
            bool strike)
        {
            if (string.IsNullOrWhiteSpace(displayText) || navigateUri == null)
            {
                return;
            }

            tokens.Add(InlineToken.CreateLink(displayText, navigateUri, bold, italic, underline, strike));
        }

        private static void AddInline(InlineCollection inlines, InlineToken token)
        {
            if (token.IsLineBreak)
            {
                inlines.Add(new LineBreak());
                return;
            }

            var run = new Run(token.Text);
            ApplyTextStyle(run, token.Bold, token.Italic, token.Underline, token.Strikethrough);

            if (token.NavigateUri == null)
            {
                inlines.Add(run);
                return;
            }

            var hyperlink = new Hyperlink(run)
            {
                NavigateUri = token.NavigateUri,
                ToolTip = token.NavigateUri.AbsoluteUri
            };
            hyperlink.RequestNavigate += Hyperlink_RequestNavigate;
            inlines.Add(hyperlink);
        }

        private static void ApplyTextStyle(Run run, bool bold, bool italic, bool underline, bool strike)
        {
            if (run == null)
            {
                return;
            }

            if (bold)
            {
                run.FontWeight = FontWeights.Bold;
            }

            if (italic)
            {
                run.FontStyle = FontStyles.Italic;
            }

            if (underline || strike)
            {
                var decorations = new TextDecorationCollection();
                if (underline)
                {
                    decorations.Add(TextDecorations.Underline);
                }

                if (strike)
                {
                    decorations.Add(TextDecorations.Strikethrough);
                }

                run.TextDecorations = decorations;
            }
        }

        private static void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            if (e?.Uri == null)
            {
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
                e.Handled = true;
            }
            catch
            {
                // Navigation failures should not close or corrupt the note dialog.
            }
        }

        private static LinkMatch FindNextLink(string text, int startIndex)
        {
            for (var i = startIndex; i < text.Length; i++)
            {
                if (text[i] == '[' && TryParseMarkdownLink(text, i, out var markdownLink))
                {
                    return markdownLink;
                }

                if (IsRawUrlStart(text, i) && TryParseRawUrl(text, i, out var rawLink))
                {
                    return rawLink;
                }
            }

            return new LinkMatch(-1, -1, string.Empty, null);
        }

        private static bool TryParseMarkdownLink(string text, int startIndex, out LinkMatch match)
        {
            match = default;

            var labelEnd = text.IndexOf(']', startIndex + 1);
            if (labelEnd <= startIndex + 1 ||
                labelEnd + 1 >= text.Length ||
                text[labelEnd + 1] != '(')
            {
                return false;
            }

            var urlStart = labelEnd + 2;
            var urlEnd = text.IndexOf(')', urlStart);
            if (urlEnd <= urlStart)
            {
                return false;
            }

            var label = text.Substring(startIndex + 1, labelEnd - startIndex - 1);
            var urlText = text.Substring(urlStart, urlEnd - urlStart);
            if (!TryCreateNavigateUri(urlText, out var uri))
            {
                return false;
            }

            match = new LinkMatch(startIndex, urlEnd + 1, label, uri);
            return true;
        }

        private static bool TryParseRawUrl(string text, int startIndex, out LinkMatch match)
        {
            match = default;

            var end = startIndex;
            while (end < text.Length && !char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            var urlText = text.Substring(startIndex, end - startIndex);
            while (urlText.Length > 0 && IsTrailingUrlPunctuation(urlText[urlText.Length - 1]))
            {
                urlText = urlText.Substring(0, urlText.Length - 1);
                end--;
            }

            if (!TryCreateNavigateUri(urlText, out var uri))
            {
                return false;
            }

            match = new LinkMatch(startIndex, end, urlText, uri);
            return true;
        }

        private static bool IsRawUrlStart(string text, int index)
        {
            return text.IndexOf("http://", index, StringComparison.OrdinalIgnoreCase) == index ||
                   text.IndexOf("https://", index, StringComparison.OrdinalIgnoreCase) == index ||
                   text.IndexOf("www.", index, StringComparison.OrdinalIgnoreCase) == index;
        }

        private static bool TryCreateNavigateUri(string value, out Uri uri)
        {
            uri = null;
            var candidate = (value ?? string.Empty).Trim();
            if (candidate.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate;
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
            {
                return false;
            }

            if (!string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        private static bool IsTrailingUrlPunctuation(char value)
        {
            return value == '.' ||
                   value == ',' ||
                   value == ';' ||
                   value == ':' ||
                   value == '!' ||
                   value == '?' ||
                   value == ')' ||
                   value == ']' ||
                   value == '}';
        }

        private enum MarkerKind
        {
            Bold,
            Italic,
            Underline,
            Strikethrough
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

        private readonly struct LinkMatch
        {
            public LinkMatch(int start, int end, string displayText, Uri navigateUri)
            {
                Start = start;
                End = end;
                DisplayText = displayText;
                NavigateUri = navigateUri;
            }

            public int Start { get; }

            public int End { get; }

            public string DisplayText { get; }

            public Uri NavigateUri { get; }
        }

        private readonly struct InlineToken
        {
            private InlineToken(
                string text,
                Uri navigateUri,
                bool bold,
                bool italic,
                bool underline,
                bool strikethrough,
                bool isLineBreak)
            {
                Text = text;
                NavigateUri = navigateUri;
                Bold = bold;
                Italic = italic;
                Underline = underline;
                Strikethrough = strikethrough;
                IsLineBreak = isLineBreak;
            }

            public string Text { get; }

            public Uri NavigateUri { get; }

            public bool Bold { get; }

            public bool Italic { get; }

            public bool Underline { get; }

            public bool Strikethrough { get; }

            public bool IsLineBreak { get; }

            public static InlineToken CreateText(
                string text,
                bool bold,
                bool italic,
                bool underline,
                bool strike)
            {
                return new InlineToken(text, null, bold, italic, underline, strike, isLineBreak: false);
            }

            public static InlineToken CreateLink(
                string text,
                Uri navigateUri,
                bool bold,
                bool italic,
                bool underline,
                bool strike)
            {
                return new InlineToken(text, navigateUri, bold, italic, underline, strike, isLineBreak: false);
            }

            public static InlineToken CreateLineBreak()
            {
                return new InlineToken(string.Empty, null, bold: false, italic: false, underline: false, strikethrough: false, isLineBreak: true);
            }
        }
    }
}
