using PlayniteAchievements.Providers.Steam.Models;
using System.Collections.Generic;

namespace PlayniteAchievements.Providers.Steam
{
    /// <summary>
    /// Turns scraped community-page rows into in-game progress observations.
    ///
    /// Scraped rows carry no api name: <see cref="ScrapedAchievement.Key"/> is a display-text
    /// composite of title and description, so it varies with the page language and never equals a
    /// stored api name. The stable identity is reconstructed from the row's icon hash by
    /// <see cref="SteamAchievementApiNameResolver"/>, the same way the scan path does it.
    ///
    /// Rows whose api name cannot be reconstructed are dropped rather than emitted under their
    /// composite key. The progress writer matches observations against stored api names, so a
    /// composite key can only land in its unmatched bucket, which the in-game monitor reads as a
    /// recovery signal and answers with a full refresh.
    /// </summary>
    internal static class SteamRemoteObservationBuilder
    {
        public static SteamRemoteObservations Build(
            SchemaAndPercentages schema,
            IReadOnlyCollection<ScrapedAchievement> rows)
        {
            var observations = new List<AchievementProgressObservation>();
            if (rows == null || rows.Count == 0)
            {
                return new SteamRemoteObservations(observations, 0);
            }

            var apiNamesByRow = SteamAchievementApiNameResolver.Resolve(schema, rows);
            var unresolved = 0;

            foreach (var row in rows)
            {
                if (row == null || !row.IsUnlocked)
                {
                    continue;
                }

                if (!apiNamesByRow.TryGetValue(row, out var apiName) ||
                    string.IsNullOrWhiteSpace(apiName))
                {
                    unresolved++;
                    continue;
                }

                observations.Add(new AchievementProgressObservation
                {
                    ApiName = apiName,
                    Unlocked = true,
                    UnlockTimeUtc = row.UnlockTimeUtc
                });
            }

            return new SteamRemoteObservations(observations, unresolved);
        }
    }

    internal sealed class SteamRemoteObservations
    {
        public SteamRemoteObservations(
            IReadOnlyList<AchievementProgressObservation> observations,
            int unresolvedUnlockedRows)
        {
            Observations = observations;
            UnresolvedUnlockedRows = unresolvedUnlockedRows;
        }

        public IReadOnlyList<AchievementProgressObservation> Observations { get; }

        /// <summary>
        /// Unlocked rows whose api name could not be reconstructed, so they were dropped. A non-zero
        /// count means the icon hashes on the page and in the schema disagree.
        /// </summary>
        public int UnresolvedUnlockedRows { get; }
    }
}
