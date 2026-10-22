using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Lays out the grid control bar's three zones: leading items, the search box, and the filter
    /// items. On one line the leading and filter zones take their natural widths and the search
    /// box fills the rest (at least <see cref="SearchMinWidth"/>). With <see cref="Wrap"/> set and
    /// too little width for one line, the filter zone moves to a second line at the full width,
    /// where its own wrap panel breaks it into further, centered lines.
    /// </summary>
    public sealed class ControlBarLayoutPanel : Panel
    {
        public static readonly DependencyProperty WrapProperty =
            DependencyProperty.Register(
                nameof(Wrap),
                typeof(bool),
                typeof(ControlBarLayoutPanel),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty SearchMinWidthProperty =
            DependencyProperty.Register(
                nameof(SearchMinWidth),
                typeof(double),
                typeof(ControlBarLayoutPanel),
                new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        public static readonly DependencyProperty LineSpacingProperty =
            DependencyProperty.Register(
                nameof(LineSpacing),
                typeof(double),
                typeof(ControlBarLayoutPanel),
                new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsMeasure));

        private bool _wrapped;

        public bool Wrap
        {
            get => (bool)GetValue(WrapProperty);
            set => SetValue(WrapProperty, value);
        }

        public double SearchMinWidth
        {
            get => (double)GetValue(SearchMinWidthProperty);
            set => SetValue(SearchMinWidthProperty, value);
        }

        public double LineSpacing
        {
            get => (double)GetValue(LineSpacingProperty);
            set => SetValue(LineSpacingProperty, value);
        }

        private UIElement Leading => InternalChildren.Count > 0 ? InternalChildren[0] : null;
        private UIElement Search => InternalChildren.Count > 1 ? InternalChildren[1] : null;
        private UIElement Items => InternalChildren.Count > 2 ? InternalChildren[2] : null;

        protected override Size MeasureOverride(Size availableSize)
        {
            var infinite = new Size(double.PositiveInfinity, availableSize.Height);
            var leading = MeasureChild(Leading, infinite);
            var items = MeasureChild(Items, infinite);
            var hasSearch = HasContent(Search);
            var searchMin = hasSearch ? Math.Max(SearchMinWidth, MeasureChild(Search, infinite).Width) : 0;

            var available = availableSize.Width;
            var oneLine = leading.Width + searchMin + items.Width;
            // A wrapping bar without a search box always takes the full-width filter layout, so
            // its filters are centered even when they fit on one line.
            _wrapped = Wrap && !double.IsInfinity(available) && (!hasSearch || oneLine > available);

            if (!_wrapped)
            {
                // Desired width is the content's natural width, as a grid with a star column
                // reports; the search box takes the rest of the width at arrange.
                var searchWidth = hasSearch && !double.IsInfinity(available)
                    ? Math.Max(searchMin, available - leading.Width - items.Width)
                    : searchMin;
                var search = MeasureChild(Search, new Size(searchWidth, availableSize.Height));
                return new Size(
                    oneLine,
                    Math.Max(leading.Height, Math.Max(search.Height, items.Height)));
            }

            // First line: leading items and the search box; second: the filters at full width.
            var firstSearch = MeasureChild(
                Search,
                new Size(hasSearch ? Math.Max(0, available - leading.Width) : 0, availableSize.Height));
            var firstHeight = Math.Max(leading.Height, firstSearch.Height);
            var wrappedItems = MeasureChild(Items, new Size(available, double.PositiveInfinity));
            var spacing = firstHeight > 0 && wrappedItems.Height > 0 ? LineSpacing : 0;
            return new Size(
                Math.Min(available, Math.Max(leading.Width + searchMin, wrappedItems.Width)),
                firstHeight + spacing + wrappedItems.Height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var leading = Leading?.DesiredSize ?? default(Size);
            var items = Items?.DesiredSize ?? default(Size);
            var hasSearch = HasContent(Search);

            if (!_wrapped)
            {
                var height = finalSize.Height;
                var searchWidth = hasSearch ? Math.Max(0, finalSize.Width - leading.Width - items.Width) : 0;
                Leading?.Arrange(new Rect(0, 0, leading.Width, height));
                Search?.Arrange(new Rect(leading.Width, 0, searchWidth, height));
                // Right-aligned beside the search box, as the auto column of a grid would place it;
                // with no search box (a grid's Display Settings can hide it) they follow the
                // leading items instead of leaving its space empty.
                var itemsLeft = hasSearch ? Math.Max(0, finalSize.Width - items.Width) : leading.Width;
                Items?.Arrange(new Rect(itemsLeft, 0, items.Width, height));
                return finalSize;
            }

            var firstHeight = Math.Max(leading.Height, Search?.DesiredSize.Height ?? 0);
            Leading?.Arrange(new Rect(0, 0, leading.Width, firstHeight));
            Search?.Arrange(new Rect(
                leading.Width,
                0,
                hasSearch ? Math.Max(0, finalSize.Width - leading.Width) : 0,
                firstHeight));
            var top = firstHeight > 0 && items.Height > 0 ? firstHeight + LineSpacing : firstHeight;
            Items?.Arrange(new Rect(0, top, finalSize.Width, items.Height));
            return finalSize;
        }

        private static Size MeasureChild(UIElement child, Size constraint)
        {
            if (child == null)
            {
                return default(Size);
            }

            child.Measure(constraint);
            return child.DesiredSize;
        }

        private static bool HasContent(UIElement child) =>
            child is ContentControl control && control.Content != null;
    }
}
