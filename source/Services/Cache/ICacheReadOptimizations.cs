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
        /// <summary>
        /// Every cached game with its identity (name, provider, ids) and no achievements, for
        /// lookups that would otherwise read every achievement in the library.
        /// </summary>
        List<GameAchievementData> LoadAllGameHeadersFast();

        /// <inheritdoc cref="Database.SummaryCacheReader.Load"/>
        CachedSummaryData LoadCachedSummaryDataFast(
            int limit,
            IEnumerable<Guid> excludedGameIds,
            Func<Guid, CachedGameSummaryData> authoredGame,
            bool days = true);

        /// <summary>Reads resolved achievement rows, totals and keys.</summary>
        Database.AchievementReadService Reads { get; }

        /// <summary>One change event per write batch, for read models.</summary>
        AchievementDataFeed Feed { get; }

        /// <summary>The current user's rows of <paramref name="keys"/> in their order, or of every row the query reads (null keys), as the summary maps them.</summary>
        List<CachedRecentUnlockData> LoadRows(Database.AchievementQuery query, IReadOnlyList<Database.AchievementQuerySql.RowKey> keys);

        /// <summary>
        /// The rows of <paramref name="keys"/> in their order with the provider values of their
        /// platform rows, for the achievement editor, with every stored icon path made absolute.
        /// </summary>
        Database.EditorRowsRead LoadEditorRows(Database.AchievementQuery query, IReadOnlyList<Database.AchievementQuerySql.RowKey> keys);

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

        /// <summary>Games whose cached achievements carry the tag <paramref name="tagKey"/>; empty on failure.</summary>
        HashSet<Guid> GetGameIdsWithTag(string tagKey);
    }
}
