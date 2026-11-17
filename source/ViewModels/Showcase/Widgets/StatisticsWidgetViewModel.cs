using System.Linq;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// Backs the Statistics widget: the shared stat tiles, every statistic or the ones chosen in
    /// its settings, over the games its collection and control bar keep. A Tall viewport
    /// arranges them in a single column.
    /// </summary>
    public sealed class StatisticsWidgetViewModel : ShowcaseWidgetViewModelBase
    {
        private readonly ShowcaseStatisticsScope _scope;
        private GridControlBarViewModel _controlBar;

        public StatisticsWidgetViewModel()
        {
            _scope = new ShowcaseStatisticsScope(Refresh);
            // The widget's own height is shared out across the rows, so each tile can follow
            // the vertical alignment within its cell.
            Stats.StretchRows = true;
        }

        /// <summary>The platform, game, tag and progress filters its settings show; null when none.</summary>
        public GridControlBarViewModel ControlBar
        {
            get => _controlBar;
            private set
            {
                if (SetValueAndReturn(ref _controlBar, value))
                {
                    OnPropertyChanged(nameof(ShowControlBar));
                }
            }
        }

        public bool ShowControlBar => ControlBar != null;

        public StatBlockViewModel Stats { get; } = new StatBlockViewModel();

        protected override void Refresh()
        {
            _scope.Apply(Projection);
            Stats.TextAlignment = ImageWidgetViewModel.ToTextAlignment(
                ShowcaseWidgetOptions.GetHorizontalAlignment(Projection?.Instance, ShowcaseHorizontalAlignment.Left));
            Stats.ItemVerticalAlignment = ImageWidgetViewModel.ToVertical(
                ShowcaseWidgetOptions.GetVerticalAlignment(Projection?.Instance, ShowcaseVerticalAlignment.Top));
            ControlBar = _scope.ShownBar;
            // The widget's chosen statistics, in the chosen order, without the game counts while
            // it counts one game.
            var list = ShowcaseWidgetProjectionService.SelectStatistics(
                _scope.Statistics,
                ShowcaseWidgetOptions.GetStatisticsKeys(Projection?.Instance),
                _scope.Snapshot?.GameSummaries?.Count(game => game != null) ?? 0);
            Stats.SetStatistics(
                list,
                Orientation == WidgetViewportOrientation.Tall ? 1 : StatBlockViewModel.MaxColumns);
        }
    }
}
