using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// The reads the Friends Overview window issues: its cheap lists, and achievement rows, counts
    /// and totals through <see cref="AchievementReadService"/>. Every method runs off the UI thread.
    /// </summary>
    internal interface IFriendsOverviewReads
    {
        /// <summary>The change feed the window's queries rerun on; null for none.</summary>
        AchievementDataFeed Feed { get; }

        /// <summary>Friend summaries, game summaries and ownership links, without achievement rows.</summary>
        FriendsOverviewData LoadLists();

        IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group);

        int Count(AchievementQuery query);

        /// <summary>
        /// The rows as display items. With <see cref="AchievementQuery.CompareAccounts"/>, each
        /// carries the compare accounts' state as an unnamed comparison.
        /// </summary>
        IReadOnlyList<FriendAchievementDisplayItem> Rows(AchievementQuery query);
    }

    /// <summary>The window's reads over a read service, a lists loader and a row mapper.</summary>
    internal sealed class FriendsOverviewReads : IFriendsOverviewReads
    {
        private readonly Func<FriendsOverviewData> _loadLists;
        private readonly AchievementReadService _reads;
        private readonly Func<IReadOnlyList<AchievementRowRecord>, bool, IReadOnlyList<FriendAchievementDisplayItem>> _mapRows;

        /// <param name="mapRows">Maps rows to display items; true when the rows carry compare columns.</param>
        public FriendsOverviewReads(
            AchievementDataFeed feed,
            Func<FriendsOverviewData> loadLists,
            AchievementReadService reads,
            Func<IReadOnlyList<AchievementRowRecord>, bool, IReadOnlyList<FriendAchievementDisplayItem>> mapRows)
        {
            Feed = feed;
            _loadLists = loadLists ?? throw new ArgumentNullException(nameof(loadLists));
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _mapRows = mapRows ?? throw new ArgumentNullException(nameof(mapRows));
        }

        public AchievementDataFeed Feed { get; }

        public FriendsOverviewData LoadLists() => _loadLists() ?? new FriendsOverviewData();

        public IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group) =>
            _reads.Totals(query, group) ?? Array.Empty<AchievementTotalsRecord>();

        public int Count(AchievementQuery query) => _reads.Count(query);

        public IReadOnlyList<FriendAchievementDisplayItem> Rows(AchievementQuery query) =>
            _mapRows(_reads.Rows(query) ?? Array.Empty<AchievementRowRecord>(), query.CompareAccounts != null);
    }
}
