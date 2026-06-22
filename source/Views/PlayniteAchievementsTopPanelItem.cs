using System;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Plugins;

namespace PlayniteAchievements.Views
{
    public class PlayniteAchievementsTopPanelItem : TopPanelItem
    {
        public PlayniteAchievementsTopPanelItem(Action openOverviewWindow)
        {
            Icon = GetTrophyIcon();
            Title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
            Activated = () => openOverviewWindow?.Invoke();
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

