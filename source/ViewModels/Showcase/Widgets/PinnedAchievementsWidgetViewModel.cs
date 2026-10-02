using System.Collections.Generic;
using System.Linq;
using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Achievements Grid widget's pinned source: the selected pin collection with
    /// reorder support. Missing pins arrive as placeholder rows, so the grid row menu can still
    /// unpin and reorder them; blank placeholder names are substituted with the localized
    /// "unavailable" strings here.
    /// </summary>
    public sealed class PinnedAchievementsWidgetViewModel
        : ShowcaseAchievementGridWidgetViewModelBase
    {
        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.RecentAchievements;

        protected override IEnumerable<AchievementDisplayItem> SelectItems(
            ShowcaseWidgetProjection projection)
        {
            var rows = projection?.AchievementRows;
            if (rows == null)
            {
                return null;
            }

            foreach (var row in rows.Where(row => row != null))
            {
                if (string.IsNullOrWhiteSpace(row.DisplayName))
                {
                    row.DisplayName = ResourceProvider.GetString("LOCPlayAch_Showcase_UnavailableAchievement");
                }

                if (string.IsNullOrWhiteSpace(row.GameName))
                {
                    row.GameName = ResourceProvider.GetString("LOCPlayAch_Showcase_UnavailableGame");
                }
            }

            return rows;
        }
    }
}
