using System.Windows;
using System.Windows.Controls;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views.Controls
{
    public partial class ScoreCardControl : UserControl
    {
        public static readonly DependencyProperty ScoreCardProperty =
            DependencyProperty.Register(
                nameof(ScoreCard),
                typeof(ScoreCardViewModel),
                typeof(ScoreCardControl),
                new PropertyMetadata(null));

        public ScoreCardControl()
        {
            InitializeComponent();
        }

        public event RoutedEventHandler InfoRequested;

        public ScoreCardViewModel ScoreCard
        {
            get => (ScoreCardViewModel)GetValue(ScoreCardProperty);
            set => SetValue(ScoreCardProperty, value);
        }

        private void ScoreInfoButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            InfoRequested?.Invoke(this, e);
        }
    }
}
