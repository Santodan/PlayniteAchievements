using System.Collections.Generic;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the GameSummaries widget: the shared game-summaries grid over the snapshot's
    /// game summaries, sorted/searched/capped by the per-instance options (the sort itself
    /// lives on the game grid widget base).
    /// </summary>
    public sealed class GameSummariesWidgetViewModel
        : ShowcaseGameGridWidgetViewModelBase
    {
        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.GameSummaries;

        protected override IEnumerable<GameSummaryItem> SelectItems(
            ShowcaseWidgetProjection projection) => projection?.Games;
    }
}
