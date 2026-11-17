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

        public static readonly DependencyProperty ShowsCheckGutterProperty =
            DependencyProperty.Register(
                nameof(ShowsCheckGutter),
                typeof(bool),
                typeof(CategoryTreeMenuItem),
                new PropertyMetadata(true));

        /// <summary>
        /// Connector geometry for this row, or null when the list has no nesting to show - which
        /// collapses the guide to zero width and leaves the row looking as it always did.
        /// </summary>
        public CategoryTreeShape TreeShape
        {
            get => (CategoryTreeShape)GetValue(TreeShapeProperty);
            set => SetValue(TreeShapeProperty, value);
        }

        /// <summary>
        /// Whether the row keeps the check column. A filter list keeps it on every row so the
        /// labels of checkable and structural rows line up; a target chooser, where no row is
        /// checkable, turns it off and the labels start at the left edge.
        /// </summary>
        public bool ShowsCheckGutter
        {
            get => (bool)GetValue(ShowsCheckGutterProperty);
            set => SetValue(ShowsCheckGutterProperty, value);
        }
    }
}
