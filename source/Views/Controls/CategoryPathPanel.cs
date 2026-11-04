using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// Lays out a drilled category path in the width it is given, keeping the leaf and as many of
    /// its nearest ancestors as fit. The first child is the elided marker ("…"): it is shown only
    /// when an ancestor had to be dropped, so a path reads "A > B > Leaf" while it fits, then
    /// "… > B > Leaf", then "… > Leaf". A leaf alone that still does not fit is measured at the
    /// available width so its own text trimming takes over.
    /// </summary>
    public sealed class CategoryPathPanel : Panel
    {
        private readonly List<KeyValuePair<UIElement, double>> _layout = new List<KeyValuePair<UIElement, double>>();

        protected override Size MeasureOverride(Size availableSize)
        {
            _layout.Clear();
            var infinite = new Size(double.PositiveInfinity, availableSize.Height);
            UIElement marker = null;
            var segments = new List<UIElement>();
            foreach (UIElement child in InternalChildren)
            {
                if (child == null)
                {
                    continue;
                }

                child.Measure(infinite);
                if (IsMarker(child))
                {
                    marker = child;
                }
                else
                {
                    segments.Add(child);
                }
            }

            if (segments.Count == 0)
            {
                return default(Size);
            }

            var available = availableSize.Width;
            var leaf = segments[segments.Count - 1];
            var ancestorCount = segments.Count - 1;
            var markerWidth = marker?.DesiredSize.Width ?? 0;
            var leafWidth = leaf.DesiredSize.Width;

            // The largest suffix of ancestors that fits beside the leaf, with the marker's own
            // width reserved whenever the suffix is partial.
            var shown = ancestorCount;
            if (!double.IsInfinity(available))
            {
                for (; shown >= 0; shown--)
                {
                    var width = leafWidth + (shown < ancestorCount ? markerWidth : 0);
                    for (var i = ancestorCount - shown; i < ancestorCount; i++)
                    {
                        width += segments[i].DesiredSize.Width;
                    }

                    if (width <= available)
                    {
                        break;
                    }
                }
            }

            var showMarker = marker != null && ancestorCount > 0 && shown < ancestorCount;
            if (shown < 0)
            {
                // Not even the leaf fits: it takes the whole width and trims its text; the marker
                // goes before it only when there is room left for both.
                shown = 0;
                showMarker = marker != null && ancestorCount > 0 && markerWidth < available / 2;
                var leafConstraint = Math.Max(0, available - (showMarker ? markerWidth : 0));
                leaf.Measure(new Size(leafConstraint, availableSize.Height));
                leafWidth = leaf.DesiredSize.Width;
            }

            if (showMarker)
            {
                _layout.Add(new KeyValuePair<UIElement, double>(marker, markerWidth));
            }

            for (var i = ancestorCount - shown; i < ancestorCount; i++)
            {
                _layout.Add(new KeyValuePair<UIElement, double>(segments[i], segments[i].DesiredSize.Width));
            }

            _layout.Add(new KeyValuePair<UIElement, double>(leaf, leafWidth));

            var totalWidth = 0d;
            var height = 0d;
            foreach (var entry in _layout)
            {
                totalWidth += entry.Value;
                height = Math.Max(height, entry.Key.DesiredSize.Height);
            }

            return new Size(totalWidth, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var visible = new HashSet<UIElement>();
            var x = 0d;
            foreach (var entry in _layout)
            {
                entry.Key.Arrange(new Rect(x, 0, entry.Value, finalSize.Height));
                visible.Add(entry.Key);
                x += entry.Value;
            }

            foreach (UIElement child in InternalChildren)
            {
                if (child != null && !visible.Contains(child))
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                }
            }

            return finalSize;
        }

        private static bool IsMarker(UIElement child)
        {
            return (child as FrameworkElement)?.DataContext is CategoryPathSegment segment && segment.Depth == 0;
        }
    }
}
