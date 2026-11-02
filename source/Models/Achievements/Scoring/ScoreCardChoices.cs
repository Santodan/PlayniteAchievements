using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Models.Achievements.Scoring
{
    /// <summary>
    /// The cards a score card picker offers: Collection, Prestige, and the points families the
    /// library last had points for. The library's families are published by whichever build last
    /// summed the points totals.
    /// </summary>
    public static class ScoreCardChoices
    {
        private static readonly object Sync = new object();
        private static IReadOnlyList<string> _familiesWithPoints = Array.Empty<string>();

        /// <summary>Raised after <see cref="PublishFamiliesWithPoints"/> changes the list.</summary>
        public static event EventHandler FamiliesChanged;

        /// <summary>Records the families with points from a fresh library total.</summary>
        public static void PublishFamiliesWithPoints(PlatformScoreTotals totals)
        {
            var families = (totals?.Families ?? Array.Empty<string>()).ToList();

            lock (Sync)
            {
                if (families.SequenceEqual(_familiesWithPoints, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                _familiesWithPoints = families;
            }

            FamiliesChanged?.Invoke(null, EventArgs.Empty);
        }

        /// <summary>
        /// Every card a picker lists, with <paramref name="current"/> kept in the list even when
        /// its family has no points any more, so a saved choice stays selected.
        /// </summary>
        public static IReadOnlyList<ScoreCardType> Build(ScoreCardType? current = null)
        {
            IReadOnlyList<string> withPoints;
            lock (Sync)
            {
                withPoints = _familiesWithPoints;
            }

            var cards = new List<ScoreCardType> { ScoreCardType.Collection, ScoreCardType.Prestige };
            foreach (var family in withPoints)
            {
                AddDistinct(cards, ScoreCardType.Points(family));
            }

            if (current.HasValue)
            {
                AddDistinct(cards, current.Value);
            }

            return cards;
        }

        private static void AddDistinct(List<ScoreCardType> cards, ScoreCardType card)
        {
            if (!cards.Contains(card))
            {
                cards.Add(card);
            }
        }
    }
}
