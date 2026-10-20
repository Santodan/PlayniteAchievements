using System.Windows.Controls;
using System.Windows.Input;

namespace PlayniteAchievements.Views.Controls
{
    /// <summary>Edits a tag list; see <see cref="ViewModels.Tags.AchievementTagListViewModel"/>.</summary>
    public partial class AchievementTagListEditor : UserControl
    {
        public AchievementTagListEditor()
        {
            InitializeComponent();
        }

        // Enter commits the name like leaving the box does.
        private void NameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox box)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }
    }
}
