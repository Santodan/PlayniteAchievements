using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Activity Calendar control bar: platform, game, tag and progress (Complete / In
    /// Progress) filters, and rarity and trophy filters. Tags, rarity and trophy narrow the
    /// unlocks that fill the days.
    /// </summary>
    public sealed class ActivityCalendarControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public ActivityCalendarControlBarAdapter()
            : base(chartFilters: true, includeTags: true)
        {
            TierFilters = new RarityTrophyControlBarFilters(RaiseFilterChanged);
            ControlBar.Items.Add(TierFilters.RarityItem);
            ControlBar.Items.Add(TierFilters.TrophyItem);
        }

        public RarityTrophyControlBarFilters TierFilters { get; }

        /// <summary>The games the platform, game, tag and trophy options come from.</summary>
        public void UpdateGames(IReadOnlyList<GameSummaryItem> games)
        {
            UpdateOptions(games);
            if (TierFilters.UpdateGames(games))
            {
                ControlBar.Refresh();
            }
        }

        public override ControlBarFilterState CaptureState()
        {
            var state = base.CaptureState();
            TierFilters.Capture(state);
            return state;
        }

        public override void RestoreState(ControlBarFilterState state)
        {
            base.RestoreState(state);
            TierFilters.Restore(state);
        }
    }
}