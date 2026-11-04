using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.ViewModels.Showcase.Widgets
{
    /// <summary>
    /// One score card's filters: a points card's snapshot narrowed to the games its control bar
    /// keeps, and the rarity badges and game art those games give it. Shared by the Scores widget
    /// and the Overview header's cards, which store their options as Scores widget instances.
    /// </summary>
    public sealed class ScoreCardFilter
    {
        // The widget instance's shared adapter, so the filters survive view model swaps. Only a
        // points card uses it: one provider's points narrowed to games.
        private readonly ShowcaseControlBarSlot<ChartControlBarAdapter> _slot;

        // The last narrowed view, reused while its source and games hold, so a refresh that keeps
        // them does not rebuild it. The source is held weakly: a strong field would pin a whole
        // snapshot generation per card.
        private WeakReference<OverviewDataSnapshot> _narrowedFrom;
        private HashSet<GameSummaryItem> _narrowedGames;
        private OverviewDataSnapshot _narrowed;

        /// <param name="onFilterChanged">Called when the card's filters change.</param>
        public ScoreCardFilter(Action onFilterChanged)
        {
            _slot = new ShowcaseControlBarSlot<ChartControlBarAdapter>(onFilterChanged);
        }

        /// <summary>The instance's control bar slot, for a host that shows the bar.</summary>
        public ShowcaseControlBarSlot<ChartControlBarAdapter> Slot => _slot;

        /// <summary>
        /// A points card's snapshot narrowed to the games its control bar keeps; the snapshot
        /// itself for the other cards, or while the bar keeps every game.
        /// </summary>
        /// <param name="keptGames">The platform's games the bar keeps; empty for the other cards.</param>
        /// <param name="rebuilt">True when the narrowed view was rebuilt rather than reused.</param>
        public OverviewDataSnapshot Apply(
            OverviewDataSnapshot source,
            ScoreCardType type,
            ShowcaseWidgetInstanceSettings instance,
            out IReadOnlyList<GameSummaryItem> keptGames,
            out bool rebuilt)
        {
            _slot.Bind(instance?.InstanceId);
            keptGames = Array.Empty<GameSummaryItem>();
            rebuilt = false;
            if (!type.IsPoints)
            {
                return source;
            }

            // Only the card's platform earns its points, so the bar lists and filters that
            // platform's games alone.
            var adapter = _slot.Adapter;
            var family = PointsFamilies.Normalize(type.PointsFamily);
            var games = (source.GameSummaries ?? new List<GameSummaryItem>())
                .Where(game => game != null &&
                               string.Equals(PointsFamilies.Normalize(game.PointsFamily), family, StringComparison.OrdinalIgnoreCase))
                .ToList();
            adapter.UpdateOptions(games);
            var kept = adapter.Apply(games);
            keptGames = kept;
            adapter.ShownGameCount = kept.Count;
            if (kept.Count == games.Count)
            {
                return source;
            }

            OverviewDataSnapshot narrowedFrom = null;
            _narrowedFrom?.TryGetTarget(out narrowedFrom);
            if (!ReferenceEquals(narrowedFrom, source) || _narrowedGames == null || !_narrowedGames.SetEquals(kept))
            {
                _narrowedFrom = new WeakReference<OverviewDataSnapshot>(source);
                _narrowedGames = new HashSet<GameSummaryItem>(kept);
                _narrowed = OverviewLinkedSnapshots.NarrowToGames(source, kept);
                rebuilt = true;
            }

            return _narrowed;
        }

        /// <summary>The rarity badges the card's options ask for, or null when they are off.</summary>
        public static IReadOnlyList<BadgeCountItem> RarityBadgesFor(
            ShowcaseWidgetInstanceSettings instance,
            IReadOnlyList<GameSummaryItem> keptGames) =>
            ShowcaseWidgetOptions.GetScoreCardShowRarityBadges(instance)
                ? BuildRarityBadges(keptGames)
                : null;

        // The kept games' unlocks per rarity, summed: only the card's platform, as its points are.
        private static IReadOnlyList<BadgeCountItem> BuildRarityBadges(IReadOnlyList<GameSummaryItem> games) =>
            BadgeCountItem.BuildRarity(
                games.Sum(game => game.UltraRareCount),
                games.Sum(game => game.RareCount),
                games.Sum(game => game.UncommonCount),
                games.Sum(game => game.CommonCount));

        /// <summary>
        /// A card narrowed to one game shows that game's art when the option asks for it; with
        /// several games, or art the game lacks, the platform icon stays (null).
        /// </summary>
        public static string GameArtFor(
            ShowcaseWidgetInstanceSettings instance,
            IReadOnlyList<GameSummaryItem> games)
        {
            if (games.Count != 1)
            {
                return null;
            }

            switch (ShowcaseWidgetOptions.GetScoreCardArt(instance))
            {
                case ShowcaseScoreCardArt.GameCover:
                    return games[0].GameCoverPath;
                case ShowcaseScoreCardArt.GameIcon:
                    return games[0].GameLogo;
                default:
                    return null;
            }
        }
    }
}
