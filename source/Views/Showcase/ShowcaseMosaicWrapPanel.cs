using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using WpfToolkit.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The mosaics' virtualizing wrap panel, with every line of tiles centered in the widget.
    /// The default Uniform spacing sizes its gaps against a full row, so a mosaic with fewer
    /// tiles than fit on one row sat against the left edge. Here the tiles pack at the spacing
    /// option's gap (the tile margin) and each line splits its unused width evenly on both sides,
    /// except the last line of a multi-line mosaic, which starts at the line above's left edge.
    /// </summary>
    /// <remarks>
    /// Only public properties are set: the plugin compiles against VirtualizingWrapPanel 1.5.4,
    /// but Playnite loads its own 2.x build, whose protected members differ (overriding the
    /// arrange against 1.5.4 internals threw MissingMethodException at runtime). The 2.x
    /// IsGridLayoutEnabled property, which makes each line center on its own tile count rather
    /// than a full row's, is absent from 1.5.4, so it is set by name when present.
    /// <para>
    /// The last-line alignment runs after the base arrange and re-arranges only that line's
    /// containers from their layout slots, using FrameworkElement and ItemsControl members alone.
    /// ArrangeOverride is declared on VirtualizingWrapPanel in both 1.5.4 and 2.x, so the base
    /// call reaches the real arrange.
    /// </para>
    /// </remarks>
    public sealed class ShowcaseMosaicWrapPanel : VirtualizingWrapPanel
    {
        public ShowcaseMosaicWrapPanel()
        {
            SpacingMode = SpacingMode.StartAndEndOnly;
            DependencyPropertyDescriptor
                .FromName("IsGridLayoutEnabled", typeof(VirtualizingWrapPanel), typeof(VirtualizingWrapPanel))
                ?.SetValue(this, false);
        }

        /// <summary>
        /// Lays out each tile at its own measured size instead of the first tile's. Set by name for
        /// the same reason as IsGridLayoutEnabled: AllowDifferentSizedItems is 2.x-only.
        /// </summary>
        public bool AllowVariableItemSizes
        {
            get => DependencyPropertyDescriptor
                .FromName("AllowDifferentSizedItems", typeof(VirtualizingWrapPanel), typeof(VirtualizingWrapPanel))
                ?.GetValue(this) as bool? == true;
            set => DependencyPropertyDescriptor
                .FromName("AllowDifferentSizedItems", typeof(VirtualizingWrapPanel), typeof(VirtualizingWrapPanel))
                ?.SetValue(this, value);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var arranged = base.ArrangeOverride(finalSize);
            AlignLastLine(finalSize.Width);
            return arranged;
        }

        /// <summary>
        /// Moves the last line left to the line above's left edge, so a short final row lines up
        /// with the tiles over it instead of centering on its own. A single-line mosaic, or a last
        /// line whose line above is not realized, keeps its centering.
        /// </summary>
        private void AlignLastLine(double width)
        {
            var owner = ItemsControl.GetItemsOwner(this);
            var count = owner?.Items.Count ?? 0;
            if (count < 2 ||
                !(owner.ItemContainerGenerator.ContainerFromIndex(count - 1) is UIElement last) ||
                !InternalChildren.Contains(last))
            {
                return;
            }

            // Only containers laid out inside the viewport; the panel parks a kept-focused
            // container outside it.
            var slots = new List<KeyValuePair<UIElement, Rect>>();
            foreach (UIElement child in InternalChildren)
            {
                var slot = LayoutInformation.GetLayoutSlot(child as FrameworkElement);
                if (child.Visibility == Visibility.Visible && slot.X >= 0 && slot.X < width)
                {
                    slots.Add(new KeyValuePair<UIElement, Rect>(child, slot));
                }
            }

            var lastTop = LayoutInformation.GetLayoutSlot(last as FrameworkElement).Y;
            var above = slots.Where(pair => pair.Value.Y < lastTop - 0.5).ToList();
            if (above.Count == 0)
            {
                return;
            }

            var aboveTop = above.Max(pair => pair.Value.Y);
            var targetLeft = above
                .Where(pair => Math.Abs(pair.Value.Y - aboveTop) < 0.5)
                .Min(pair => pair.Value.X);
            var lastLine = slots.Where(pair => Math.Abs(pair.Value.Y - lastTop) < 0.5).ToList();
            var shift = targetLeft - lastLine.Min(pair => pair.Value.X);
            // A last line at least as wide as the one above is left where it is.
            if (shift >= -0.5)
            {
                return;
            }

            foreach (var pair in lastLine)
            {
                var slot = pair.Value;
                pair.Key.Arrange(new Rect(slot.X + shift, slot.Y, slot.Width, slot.Height));
            }
        }
    }
}
