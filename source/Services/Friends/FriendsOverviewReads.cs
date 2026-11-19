using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// Every friend read: the cheap lists, and achievement rows, keys and totals through
    /// <see cref="AchievementReadService"/>. Every method runs off the UI thread.
    /// </summary>
    internal interface IFriendsOverviewReads
    {
        /// <summary>The change feed friend reads rerun on; null for none.</summary>
        AchievementDataFeed Feed { get; }

        /// <summary>
        /// Friend summaries, game summaries and ownership links, without achievement rows; with a
        /// Playnite game, the friends and links of that game and their counts in it.
        /// </summary>
        FriendsOverviewData LoadLists(Guid? playniteGameId = null);

        IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group);

        /// <summary>The keys of the matching rows in the query's order.</summary>
        IReadOnlyList<AchievementQuerySql.RowKey> SortedKeys(AchievementQuery query);

        /// <summary>
        /// The rows as display items: of <paramref name="keys"/> in their order, or every matching
        /// row when null. With <see cref="AchievementQuery.CompareAccounts"/>, each carries the
        /// compare accounts' state as an unnamed comparison.
        /// </summary>
        IReadOnlyList<FriendAchievementDisplayItem> Rows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys = null);
    }

    /// <summary>The friend reads over a read service, a lists loader and a row mapper.</summary>
    internal sealed class FriendsOverviewReads : IFriendsOverviewReads
    {
        private readonly Func<Guid?, FriendsOverviewData> _loadLists;
        private readonly AchievementReadService _reads;
        private readonly Func<IReadOnlyList<AchievementRowRecord>, bool, IReadOnlyList<FriendAchievementDisplayItem>> _mapRows;

        /// <param name="mapRows">Maps rows to display items; true when the rows carry compare columns.</param>
        public FriendsOverviewReads(
            AchievementDataFeed feed,
            Func<Guid?, FriendsOverviewData> loadLists,
            AchievementReadService reads,
            Func<IReadOnlyList<AchievementRowRecord>, bool, IReadOnlyList<FriendAchievementDisplayItem>> mapRows)
        {
            Feed = feed;
            _loadLists = loadLists ?? throw new ArgumentNullException(nameof(loadLists));
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _mapRows = mapRows ?? throw new ArgumentNullException(nameof(mapRows));
        }

        public AchievementDataFeed Feed { get; }

        public FriendsOverviewData LoadLists(Guid? playniteGameId = null) => _loadLists(playniteGameId) ?? new FriendsOverviewData();

        public IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group) => _reads.Totals(query, group);

        public IReadOnlyList<AchievementQuerySql.RowKey> SortedKeys(AchievementQuery query) => _reads.SortedKeys(query);

        public IReadOnlyList<FriendAchievementDisplayItem> Rows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys = null) =>
            _mapRows(keys == null ? _reads.Rows(query) : _reads.Rows(query, keys), query.CompareAccounts != null);
    }
}
