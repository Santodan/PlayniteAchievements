using System;
using System.Collections.Generic;
using PlayniteAchievements.Models.Achievements;

namespace PlayniteAchievements.Services.Cache
{
    /// <summary>
    /// Batched read paths over the cache, for callers that would otherwise ask the same
    /// question once per game. Implemented only by <see cref="CacheManager"/>; callers reach it
    /// with an <c>as</c> cast and fall back to the per-game read when it is unavailable.
    /// </summary>
    /// <remarks>
    /// Kept in its own file rather than beside <see cref="CacheManager"/> so the test project,
    /// which links individual source files, can compile the sources that consume this seam.
    /// </remarks>
    internal interface ICacheReadOptimizations
    {
        List<GameAchievementData> LoadAllGameDataFast();

        /// <summary>
        /// Every cached game with its identity (name, provider, ids) and no achievements, for
        /// lookups that would otherwise read every achievement in the library.
        /// </summary>
        List<GameAchievementData> LoadAllGameHeadersFast();

        CachedSummaryData LoadCachedSummaryDataFast(int recentAchievementDetailLimit = 0);

        /// <summary>
        /// One game's contribution, for patching a cached whole-library summary rather than
        /// re-reading every row. Always unbounded, matching the overview's own request.
        /// </summary>
        CachedSummaryData LoadCachedSummaryDataForGameFast(Guid playniteGameId);

        /// <summary>
        /// Games the current user's cache records as having no achievements at all.
        /// </summary>
        /// <remarks>
        /// One query for the whole set. Bulk refresh used to ask this per candidate game
        /// through <c>LoadGameData</c>, which takes the cache lock, issues a SQL round trip and
        /// deep-copies the entire achievement payload -- all to read one boolean, once per game
        /// in the library, before a single provider call.
        /// </remarks>
        HashSet<Guid> GetNoAchievementGameIds();

        /// <summary>
        /// Which of the asked-for ApiNames the current user has unlocked, per game.
        /// </summary>
        /// <remarks>
        /// For the stored-capstone correction on a bounded summary read, which holds only the most
        /// recent unlocks and so cannot say whether an older capstone was earned.
        /// </remarks>
        Dictionary<Guid, HashSet<string>> LoadUnlockedApiNamesFast(
            IReadOnlyDictionary<Guid, HashSet<string>> wanted);

        /// <summary>
        /// The asked-for achievements as the tallies counted them, per game and ApiName, plus the
        /// provider capstones of <paramref name="providerCapstoneGames"/>, for applying the user's
        /// tag and capstone edits to the summary's tallies. Null on failure.
        /// </summary>
        Dictionary<Guid, Dictionary<string, Models.Achievements.AchievementTallyState>> LoadAchievementTallyStatesFast(
            IReadOnlyDictionary<Guid, HashSet<string>> wanted,
            ISet<Guid> providerCapstoneGames = null);

        /// <summary>One game's provider tallies, read on its own; null on failure.</summary>
        Models.Achievements.AchievementTally[] LoadTalliesForGameFast(Guid playniteGameId);

        /// <summary>Games whose cached achievements carry the tag <paramref name="tagKey"/>; empty on failure.</summary>
        HashSet<Guid> GetGameIdsWithTag(string tagKey);
    }
}
