using PlayniteAchievements.Models.Achievements;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// One cached tier row, reduced to what a live progress read needs: its stable key and the
    /// running total it is earned at.
    /// </summary>
    internal sealed class Gw2CachedTier
    {
        public string ApiName { get; set; }

        public int Threshold { get; set; }
    }

    /// <summary>
    /// Maps account progress onto already-cached tier rows for the in-game path.
    ///
    /// This deliberately reads nothing but the cached schema and the account endpoint: no
    /// definitions, no icons, no rarity. The thresholds it needs were already persisted as each
    /// row's progress denominator by the full refresh, so a live poll never has to hold the
    /// catalog.
    /// </summary>
    internal static class Gw2InGameProgressMapper
    {
        /// <summary>
        /// Groups the cached rows by achievement id so a changed account entry can find its tiers.
        /// Rows whose key is not a tier key are ignored rather than guessed at.
        /// </summary>
        public static Dictionary<int, List<Gw2CachedTier>> BuildTierIndex(GameAchievementData cachedSchema)
        {
            var index = new Dictionary<int, List<Gw2CachedTier>>();
            if (cachedSchema?.Achievements == null)
            {
                return index;
            }

            foreach (var achievement in cachedSchema.Achievements)
            {
                if (achievement?.ApiName == null ||
                    !Gw2AchievementMapper.TryParseTierApiName(achievement.ApiName, out var achievementId))
                {
                    continue;
                }

                if (!index.TryGetValue(achievementId, out var tiers))
                {
                    tiers = new List<Gw2CachedTier>();
                    index[achievementId] = tiers;
                }

                tiers.Add(new Gw2CachedTier
                {
                    ApiName = achievement.ApiName,

                    // The full refresh wrote each tier's own count here. A row with no denominator
                    // is an all-or-nothing tier, which is earned the moment anything is recorded.
                    Threshold = achievement.ProgressDenom ?? 1
                });
            }

            return index;
        }

        /// <summary>
        /// Observations for the achievements whose progress moved, expanded to one per tier. The
        /// unlock rules match the full refresh exactly, so a live update and a later refresh cannot
        /// disagree about whether a tier is earned.
        ///
        /// Tiers that were already settled at <paramref name="previous"/> are left out. Progress in
        /// this game never goes backwards, so a tier at or below the previous running total is
        /// already unlocked with its bar already full: it cannot change, and reporting it again
        /// would only make the monitor re-examine a row that is finished with.
        /// </summary>
        public static List<AchievementProgressObservation> BuildObservations(
            Dictionary<int, List<Gw2CachedTier>> tierIndex,
            IReadOnlyList<int> changedIds,
            Dictionary<int, Gw2ProgressSignature> snapshot,
            Dictionary<int, Gw2ProgressSignature> previous = null)
        {
            var observations = new List<AchievementProgressObservation>();
            if (tierIndex == null || changedIds == null || snapshot == null)
            {
                return observations;
            }

            foreach (var achievementId in changedIds)
            {
                if (!tierIndex.TryGetValue(achievementId, out var tiers) ||
                    !snapshot.TryGetValue(achievementId, out var progress))
                {
                    continue;
                }

                var before = default(Gw2ProgressSignature);
                var hadPrevious = previous != null &&
                    previous.TryGetValue(achievementId, out before);

                // A ladder that was already finished has no tier left to move. Only its repeat
                // count can still climb, and that changes nothing about any tier.
                if (hadPrevious && (before.Done || before.Repeated > 0))
                {
                    continue;
                }

                var settledAt = hadPrevious && before.Current > 0 ? before.Current : 0;
                var completed = progress.Done || progress.Repeated > 0;
                var current = progress.Current < 0 ? 0 : progress.Current;

                foreach (var tier in tiers)
                {
                    if (tier == null)
                    {
                        continue;
                    }

                    var threshold = tier.Threshold > 0 ? tier.Threshold : 1;
                    if (threshold <= settledAt)
                    {
                        continue;
                    }

                    observations.Add(new AchievementProgressObservation
                    {
                        ApiName = tier.ApiName,
                        Unlocked = completed || current >= threshold,

                        // The API records no unlock time, so the monitor's own observation of the
                        // transition is the only honest anchor. The registration declares that.
                        UnlockTimeUtc = null,

                        ProgressNum = current > threshold ? threshold : current,
                        ProgressDenom = threshold
                    });
                }
            }

            return observations;
        }
    }
}
