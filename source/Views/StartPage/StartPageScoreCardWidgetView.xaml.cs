using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.Views;
using PlayniteAchievements.Views.Dialogs;

namespace PlayniteAchievements.Views.StartPage
{
    public partial class StartPageScoreCardWidgetView : UserControl
    {
        public StartPageScoreCardWidgetView()
        {
            InitializeComponent();
        }

        private void ScoreInfoButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ScoreInfoDialogPresenter.Show();
        }
    }
}
