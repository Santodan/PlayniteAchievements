using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public class PlayniteAchievementsTopPanelItem : TopPanelItem
    {
        public PlayniteAchievementsTopPanelItem(
            IPlayniteAPI api,
            ILogger logger,
            AchievementService achievementService,
            RefreshCoordinator refreshCoordinator,
            PlayniteAchievementsSettings settings)
        {
            Icon = GetTrophyIcon();
            Title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
            Activated = () =>
            {
                var view = new SidebarControl(api, logger, achievementService, refreshCoordinator, settings);

                var windowOptions = new WindowOptions
                {
                    ShowMinimizeButton = false,
                    ShowMaximizeButton = true,
                    ShowCloseButton = true,
                    CanBeResizable = true,
                    Width = 1280,
                    Height = 800
                };

                var window = PlayniteUiProvider.CreateExtensionWindow(Title, view, windowOptions);
                window.ShowDialog();
            };
        }

        private TextBlock GetTrophyIcon()
        {
            var tb = new TextBlock
            {
                Text = char.ConvertFromUtf32(0xedd7), // ico-font: trophy
                FontSize = 22
            };

            var font = ResourceProvider.GetResource("FontIcoFont") as FontFamily;
            tb.FontFamily = font ?? new FontFamily("Segoe UI Symbol");

            return tb;
        }
    }
}
