using System;
using System.Windows;
using WpfToolkit.Controls;

namespace PlayniteAchievements.Views.Showcase
{
    /// <summary>
    /// The mosaics' virtualizing wrap panel, with the tiles centered in the widget. The base
    /// panel's Uniform spacing sizes its gaps against a full row, so a mosaic with fewer tiles
    /// than fit on one row sat against the left edge, and its None spacing packed every row
    /// left. Here the occupied columns are packed (the tile margin is the spacing option's
    /// gap) and the unused width splits evenly on both sides. A shorter last row stays on the
    /// column grid, aligned under the rows above it.
    /// </summary>
    public sealed class ShowcaseMosaicWrapPanel : VirtualizingWrapPanel
    {
        public ShowcaseMosaicWrapPanel()
        {
            // None keeps the base extent to the packed columns, matching the arrange below.
            SpacingMode = SpacingMode.None;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var offset = Offset;
            var columns = Math.Max(1, Math.Min(itemsPerRowCount, Items.Count));
            var left = Math.Max(0, (finalSize.Width - (childSize.Width * columns)) / 2);
            for (var i = 0; i < InternalChildren.Count; i++)
            {
                var child = InternalChildren[i];
                if (finalSize.Height == 0)
                {
                    child.Arrange(new Rect(0, 0, 0, 0));
                    continue;
                }

                var index = GetItemIndexFromChildIndex(i);
                var column = index % Math.Max(1, itemsPerRowCount);
                var row = index / Math.Max(1, itemsPerRowCount);
                child.Arrange(new Rect(
                    left + (column * childSize.Width) - offset.X,
                    (row * childSize.Height) - offset.Y,
                    childSize.Width,
                    childSize.Height));
            }

            return finalSize;
        }
    }
}
