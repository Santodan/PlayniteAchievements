using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers;
using PlayniteAchievements.ViewModels;
using System.Windows.Controls;

namespace PlayniteAchievements.Views
{
    public partial class FriendsSettingsTab : UserControl
    {
        public FriendsSettingsTab()
        {
            InitializeComponent();
        }

        internal FriendsSettingsTab(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry,
            ILogger logger)
            : this()
        {
            DataContext = new FriendsSettingsViewModel(settings, plugin, providerRegistry, logger);
        }
    }
}
