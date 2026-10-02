using System.Collections.Generic;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Game Summaries Grid widget's pinned and Playnite-favorites sources. Pin
    /// reordering is only offered for the pinned source, whose order is user-controlled.
    /// </summary>
    public sealed class FavoriteGamesWidgetViewModel
        : ShowcaseGameGridWidgetViewModelBase
    {
        private bool _pinReorderEnabled;

        public bool PinReorderEnabled
        {
            get => _pinReorderEnabled;
            private set => SetValue(ref _pinReorderEnabled, value);
        }

        protected override string BaseSurfaceKey => ShowcaseGridSurfaces.GameSummaries;

        protected override GameSummariesSortMode DefaultSortMode => GameSummariesSortMode.PinOrder;

        protected override IEnumerable<GameSummaryItem> SelectItems(
            ShowcaseWidgetProjection projection)
        {
            PinReorderEnabled = ShowcaseWidgetOptions.GetGameGridSource(projection?.Instance) ==
                ShowcaseGameGridSource.Pinned;
            return projection?.Games;
        }
    }
}
