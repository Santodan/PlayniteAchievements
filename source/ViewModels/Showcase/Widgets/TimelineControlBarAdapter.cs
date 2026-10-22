using System.Collections.Generic;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// The Timeline control bar: platform and game filters over the games whose unlocks or
    /// completions the chart counts, and tag, rarity and trophy filters over the unlocks
    /// themselves.
    /// </summary>
    public sealed class TimelineControlBarAdapter : GameSummaryGridControlBarAdapter
    {
        public TimelineControlBarAdapter()
            : base(chartFilters: true, includeProgress: false, includeTags: true)
        {
            TierFilters = new RarityTrophyControlBarFilters(RaiseFilterChanged);
            ControlBar.Items.Add(TierFilters.RarityItem);
            ControlBar.Items.Add(TierFilters.TrophyItem);
        }

        public RarityTrophyControlBarFilters TierFilters { get; }

        /// <summary>The games the platform, game and trophy options come from.</summary>
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