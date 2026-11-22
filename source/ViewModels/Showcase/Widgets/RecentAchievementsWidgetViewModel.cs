using PlayniteAchievements.Models;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the RecentAchievements widget: the shared achievement grid over the unlocks it
    /// reads, searched and capped by the per-instance grid options.
    /// </summary>
    public sealed class RecentAchievementsWidgetViewModel
        : ShowcaseAchievementGridWidgetViewModelBase
    {
        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.RecentAchievements;
    }
}
