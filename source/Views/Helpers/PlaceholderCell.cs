using System.Windows;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>What a column's cell shows while its row has not loaded.</summary>
    public enum PlaceholderShape
    {
        /// <summary>A bar where a line of text goes.</summary>
        Bar,

        /// <summary>A short bar over two longer ones, where a name and a description go.</summary>
        Lines,

        /// <summary>A rounded square where an image goes.</summary>
        Block,

        /// <summary>A small circle where a badge or a glyph goes.</summary>
        Dot,

        /// <summary>Nothing.</summary>
        None
    }

    /// <summary>
    /// The shape a column's cells draw for a placeholder row; set on the column, and copied onto
    /// each cell by the cell style so the placeholder template can test it.
    /// </summary>
    public static class PlaceholderCell
    {
        public static readonly DependencyProperty ShapeProperty =
            DependencyProperty.RegisterAttached(
                "Shape",
                typeof(PlaceholderShape),
                typeof(PlaceholderCell),
                new PropertyMetadata(PlaceholderShape.Bar));

        public static void SetShape(DependencyObject element, PlaceholderShape value) => element.SetValue(ShapeProperty, value);

        public static PlaceholderShape GetShape(DependencyObject element) => (PlaceholderShape)element.GetValue(ShapeProperty);
    }
}
