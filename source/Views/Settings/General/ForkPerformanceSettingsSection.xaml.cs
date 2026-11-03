using System.Windows.Controls;
using PlayniteAchievements.Providers;

namespace PlayniteAchievements.Views.Settings.General
{
    public partial class ForkPerformanceSettingsSection : UserControl
    {
        public ForkPerformanceSettingsSection()
        {
            InitializeComponent();
            DataContext = ProviderRegistry.Instance?.GetSettingsForEdit("Local");
        }
    }
}
