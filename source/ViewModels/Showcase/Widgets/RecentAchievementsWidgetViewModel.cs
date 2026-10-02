using System.Collections.Generic;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the RecentAchievements widget: the shared achievement grid over the
    /// snapshot's recent unlocks, searched and capped by the per-instance grid options.
    /// </summary>
    public sealed class RecentAchievementsWidgetViewModel
        : ShowcaseAchievementGridWidgetViewModelBase
    {
        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.RecentAchievements;

        protected override IEnumerable<AchievementDisplayItem> SelectItems(
            ShowcaseWidgetProjection projection) => projection?.AchievementRows;
    }
}
