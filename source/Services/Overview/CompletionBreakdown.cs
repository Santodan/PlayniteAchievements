using System;
using System.Collections.Generic;
using System.Linq;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Overview
{
    /// <summary>
    /// A snapshot's completions split the way the Completed games pie draws them. Each game's
    /// finishes count under the badge its rows show (<see cref="GameSummaryItem.CompletionBadgeFor"/>),
    /// so the detail priority decides between a platinum and a mythic. The finishes still open
    /// split by whether the game has any unlock, as the In Progress and No Progress filters do.
    /// </summary>
    public sealed class CompletionBreakdown
    {
        private static readonly IReadOnlyList<CompletionBadgeKind> CompletedOnly = new[] { CompletionBadgeKind.Completed };

        private readonly Dictionary<CompletionBadgeKind, int> _finishes = new Dictionary<CompletionBadgeKind, int>();

        private CompletionBreakdown()
        {
        }

        /// <summary>
        /// The finish badges in priority order: the platinum and mythic as the Achievement styles
        /// order them, then the completed badge. The pie sorts its slices by count and falls back
        /// to this order on ties.
        /// </summary>
        public IReadOnlyList<CompletionBadgeKind> FinishOrder { get; private set; } = CompletedOnly;

        /// <summary>Finishes still open in games with at least one unlock.</summary>
        public int InProgress { get; private set; }

        /// <summary>Finishes still open in games with no unlock.</summary>
        public int NoProgress { get; private set; }

        public int Incomplete => InProgress + NoProgress;

        public int Finishes(CompletionBadgeKind kind) => _finishes.TryGetValue(kind, out var count) ? count : 0;

        public static CompletionBreakdown From(OverviewDataSnapshot snapshot, IReadOnlyList<ProgressDetailStyle> detailOrder)
        {
            var breakdown = new CompletionBreakdown();
            var completions = Math.Max(0, snapshot?.Completions ?? 0);
            var open = Math.Max(0, (snapshot?.PossibleCompletions ?? 0) - completions);
            if (snapshot == null || completions + open <= 0)
            {
                return breakdown;
            }

            breakdown.FinishOrder = FinishOrderFor(detailOrder);
            var byGame = snapshot.CompletionsByGame;
            var counted = 0;
            var inProgress = 0;
            var noProgress = 0;
            foreach (var game in snapshot.GameSummaries ?? new List<GameSummaryItem>())
            {
                if (game == null)
                {
                    continue;
                }

                var done = byGame == null
                    ? game.Completions
                    : game.PlayniteGameId is Guid id && byGame.TryGetValue(id, out var inSpan) ? inSpan : 0;
                if (done > 0)
                {
                    var kind = game.CompletionBadgeFor(detailOrder);
                    breakdown._finishes[kind] = breakdown.Finishes(kind) + done;
                    counted += done;
                }

                // A cut to a span keeps no open finishes.
                var remaining = byGame == null ? Math.Max(0, game.PossibleCompletions - game.Completions) : 0;
                if (remaining > 0)
                {
                    if (!game.IsCompleted && game.UnlockedAchievements <= 0)
                    {
                        noProgress += remaining;
                    }
                    else
                    {
                        inProgress += remaining;
                    }
                }
            }

            // A snapshot whose totals were not summed from these games keeps its totals, unsplit.
            if (counted != completions)
            {
                breakdown._finishes.Clear();
                breakdown._finishes[CompletionBadgeKind.Completed] = completions;
            }

            if (inProgress + noProgress == open)
            {
                breakdown.InProgress = inProgress;
                breakdown.NoProgress = noProgress;
            }
            else
            {
                breakdown.InProgress = open;
            }

            return breakdown;
        }

        /// <summary>
        /// The finish badges <paramref name="detailOrder"/> allows, in its order: the platinum
        /// for Trophy, the mythic for Points, then the completed badge.
        /// </summary>
        public static IReadOnlyList<CompletionBadgeKind> FinishOrderFor(IReadOnlyList<ProgressDetailStyle> detailOrder)
        {
            var order = new List<CompletionBadgeKind>();
            foreach (var style in detailOrder ?? Array.Empty<ProgressDetailStyle>())
            {
                if (style == ProgressDetailStyle.Trophy)
                {
                    order.Add(CompletionBadgeKind.Platinum);
                }
                else if (style == ProgressDetailStyle.Points)
                {
                    order.Add(CompletionBadgeKind.Mythic);
                }
            }

            order.Add(CompletionBadgeKind.Completed);
            return order.Distinct().ToList();
        }
    }
}
