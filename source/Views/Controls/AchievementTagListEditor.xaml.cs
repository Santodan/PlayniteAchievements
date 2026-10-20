using System.Linq;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.ViewModels.Tags;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>Edits a tag list; see <see cref="AchievementTagListViewModel"/>.</summary>
    public partial class AchievementTagListEditor : UserControl
    {
        private const string DragDataFormat = "PlayniteAchievements.AchievementTagOrder";

        public AchievementTagListEditor()
        {
            InitializeComponent();

            DataGridRowReorderBehavior.SetOptions(TagGrid, new DataGridRowReorderOptions
            {
                DragDataFormat = DragDataFormat,
                DropIndicator = DropInsertLine,
                DragCountPopup = DragCountPopup,
                DragCountText = DragCountText,
                IsReorderableItem = item => item is AchievementTagEditorRow,
                ExtractDragKeys = items => items
                    .OfType<AchievementTagEditorRow>()
                    .Select(row => row.Key)
                    .ToList(),
                MoveItemsRelativeToTarget = (keys, target, insertAfter) =>
                    target is AchievementTagEditorRow targetRow &&
                    ViewModel?.MoveRows(keys, targetRow.Key, insertAfter) == true,
                MoveItemsToEnd = keys => ViewModel?.MoveRowsToEnd(keys) == true
            });
        }

        private AchievementTagListViewModel ViewModel => DataContext as AchievementTagListViewModel;

        // Enter commits a text box like leaving it does.
        private void CommitOnEnter_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }
    }
}
