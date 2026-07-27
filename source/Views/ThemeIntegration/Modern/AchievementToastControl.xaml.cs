using System.Windows.Controls;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Views.ThemeIntegration.Modern
{
    public partial class AchievementToastControl : UserControl
    {
        public AchievementToastControl()
        {
            InitializeComponent();
            ToastContent.ContentTemplate = new AchievementToastTemplateResolver(
                PlayniteAchievementsPlugin.Instance?.PlayniteApi,
                PluginLogger.GetLogger(nameof(AchievementToastControl))).ResolveTemplate();
        }
    }
}
