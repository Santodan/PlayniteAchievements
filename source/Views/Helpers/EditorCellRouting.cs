using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels.ManageAchievements;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Sends an edit made in a grid cell to the rows the details pane would have edited.
    ///
    /// The pane edits the whole selection, because its DataContext is the view model's edit
    /// target - the selected row, or a proxy standing in for several. A cell's DataContext is its
    /// own row, so a plain two-way binding there would write that row alone and the same control
    /// would mean two different things depending on where it was clicked. Every interactive cell
    /// therefore displays from its own row one way and writes through <see cref="ResolveTarget"/>.
    /// </summary>
    public static class EditorCellRouting
    {
        /// <summary>
        /// Set on a column whose cells edit the selection rather than just their own row. Read
        /// when a press is being routed, so the columns that predate this keep their own
        /// behaviour and no cell is captured by accident.
        /// </summary>
        public static readonly DependencyProperty RoutesToSelectionProperty =
            DependencyProperty.RegisterAttached(
                "RoutesToSelection",
                typeof(bool),
                typeof(EditorCellRouting),
                new PropertyMetadata(false));

        public static void SetRoutesToSelection(DependencyObject element, bool value)
        {
            element?.SetValue(RoutesToSelectionProperty, value);
        }

        public static bool GetRoutesToSelection(DependencyObject element)
        {
            return element?.GetValue(RoutesToSelectionProperty) is bool value && value;
        }

        /// <summary>
        /// The row property a cell's control commits to, for the handlers shared by several
        /// columns. Separate from Tag, which those controls already use for their input mode.
        /// </summary>
        public static readonly DependencyProperty FieldProperty =
            DependencyProperty.RegisterAttached(
                "Field",
                typeof(string),
                typeof(EditorCellRouting),
                new PropertyMetadata(null));

        public static void SetField(DependencyObject element, string value)
        {
            element?.SetValue(FieldProperty, value);
        }

        public static string GetField(DependencyObject element)
        {
            return element?.GetValue(FieldProperty) as string;
        }

        /// <summary>
        /// The row a cell's control belongs to.
        /// </summary>
        public static AchievementEditorRow ResolveRow(object sender)
        {
            return (sender as FrameworkElement)?.DataContext as AchievementEditorRow;
        }

        /// <summary>
        /// What to write an edit to: the view model's edit target while this row is part of the
        /// selection, and the row itself otherwise.
        ///
        /// The fallback is not a formality. Arrow keys move the achievement selection window-wide
        /// while focus stays in a cell's text box, so a box can still be focused after its row has
        /// left the selection; committing to the edit target then would write rows the user is no
        /// longer looking at.
        /// </summary>
        public static AchievementEditorRow ResolveTarget(
            ManageAchievementsEditorViewModel viewModel,
            AchievementEditorRow row)
        {
            if (viewModel == null || row == null)
            {
                return row;
            }

            return viewModel.IsRowInSelection(row)
                ? viewModel.EditTarget ?? row
                : row;
        }

        /// <summary>
        /// Whether a press landed in a cell of a column whose edits route to the selection.
        /// </summary>
        public static bool IsRoutedCellHit(DependencyObject source)
        {
            var cell = VisualTreeHelpers.FindVisualParent<DataGridCell>(source);
            return cell?.Column != null && GetRoutesToSelection(cell.Column);
        }
    }
}
