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

        private void ScoreCard_InfoRequested(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            ScoreInfoDialogPresenter.Show();
        }
    }
}
