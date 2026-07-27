using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Views
{
    public partial class LocalAchievementEditorControl : UserControl
    {
        public LocalAchievementEditorControl()
        {
            InitializeComponent();
        }

        public LocalAchievementEditorControl(LocalAchievementEditorViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        }

        private LocalAchievementEditorViewModel ViewModel => DataContext as LocalAchievementEditorViewModel;

        public string WindowTitle => ViewModel?.WindowTitle ?? "Edit Local Achievements";

        public void Cleanup()
        {
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel == null)
            {
                return;
            }

            if (await ViewModel.SaveAsync().ConfigureAwait(true))
            {
                var window = Window.GetWindow(this);
                window?.Close();
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Window.GetWindow(this)?.Close();
        }
    }
}