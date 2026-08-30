using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>
    /// A menu row that knows where it sits in a category tree, so its template can hand the shape
    /// to a <see cref="CategoryTreeGuide"/> and draw the same connectors the category grid does.
    ///
    /// A dependency property rather than <see cref="FrameworkElement.Tag"/>: the template reaches
    /// it through TemplatedParent, and a typed property says what it holds.
    /// </summary>
    public sealed class CategoryTreeMenuItem : MenuItem
    {
        public static readonly DependencyProperty TreeShapeProperty =
            DependencyProperty.Register(
                nameof(TreeShape),
                typeof(CategoryTreeShape),
                typeof(CategoryTreeMenuItem),
                new PropertyMetadata(null));

        /// <summary>
        /// Connector geometry for this row, or null when the list has no nesting to show - which
        /// collapses the guide to zero width and leaves the row looking as it always did.
        /// </summary>
        public CategoryTreeShape TreeShape
        {
            get => (CategoryTreeShape)GetValue(TreeShapeProperty);
            set => SetValue(TreeShapeProperty, value);
        }
    }
}
