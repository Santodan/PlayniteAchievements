using System.Windows.Controls;
using System.Windows;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Settings;

namespace PlayniteAchievements.Views.Settings.General
{
    public partial class ForkPerformanceSettingsSection : UserControl
    {
        public ForkPerformanceSettingsSection()
        {
            InitializeComponent();
            DataContext = ProviderRegistry.Instance?.GetSettingsForEdit("Local");
        }

        private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
        {
            ForkPerformanceDefaults.Restore(DataContext as LocalSettings);
        }
    }
}
