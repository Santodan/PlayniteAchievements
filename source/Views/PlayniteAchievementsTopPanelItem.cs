using System;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using PlayniteAchievements.Views.Helpers;

namespace PlayniteAchievements.Views
{
    public class PlayniteAchievementsTopPanelItem : TopPanelItem
    {
        public PlayniteAchievementsTopPanelItem(Action openOverviewWindow)
        {
            Icon = BrandIconFactory.CreateTrophyIcon(22);
            Title = ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
            Activated = () => openOverviewWindow?.Invoke();
        }
    }
}
