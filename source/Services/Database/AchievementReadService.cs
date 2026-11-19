using PlayniteAchievements.Services.Achievements;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>One resolved achievement row, as <see cref="AchievementReadService.Rows"/> reads it.</summary>
    /// <remarks>
    /// Values as stored: flags are 0 or 1, times are the stored UTC text, paths are as the database
    /// holds them. The friend columns are set for friend rows only.
    /// </remarks>
    internal sealed class AchievementRowRecord
    {
        public long? DefinitionId { get; set; }
        public long? GameId { get; set; }
        public string ApiName { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public int? Points { get; set; }
        public int? ScaledPoints { get; set; }
        public string Category { get; set; }
        public string ProviderCategory { get; set; }
        public string Tags { get; set; }
        public string CategoryKind { get; set; }
        public string TrophyType { get; set; }
        public long Hidden { get; set; }
        public long IsCapstone { get; set; }
        public double? GlobalPercentUnlocked { get; set; }
        public string Rarity { get; set; }
        public int? ProgressMax { get; set; }
        public long IsFiltered { get; set; }
        public long IsSummaryFiltered { get; set; }
        public string Note { get; set; }
        public long IsGoal { get; set; }
        public long? GoalOrder { get; set; }
        public long? CustomOrder { get; set; }
        public long? CategoryOrder { get; set; }
        public string CategoryArtPath { get; set; }
        public string PlayniteGameId { get; set; }
        public string GameName { get; set; }
        public string ProviderKey { get; set; }
        public string ProviderPlatformKey { get; set; }
        public long? ProviderGameId { get; set; }
        public string ProviderGameKey { get; set; }
        public string GameIconPath { get; set; }
        public string GameCoverPath { get; set; }
        public long Unlocked { get; set; }
        public string UnlockTimeUtc { get; set; }
        public int? ProgressNum { get; set; }
        public int? ProgressDenom { get; set; }
        public long ExcludedFromSummaries { get; set; }
        public long IsAuthored { get; set; }
        public string CleanCapturePath { get; set; }
        public string NotificationCapturePath { get; set; }
        public string FramedCapturePath { get; set; }
        public string VideoCapturePath { get; set; }
        public long? UserId { get; set; }
        public long IsCurrentUser { get; set; }
        public string UserProviderKey { get; set; }
        public string ExternalUserId { get; set; }
        public string UserDisplayName { get; set; }
        public string AvatarUrl { get; set; }
        public string AvatarPath { get; set; }
        public long OwnershipConfirmed { get; set; }

        /// <summary>Whether the current user unlocked the same achievement of the same Playnite game.</summary>
        public long SelfUnlocked { get; set; }

        /// <summary>The current user's latest resolved unlock time of it, across their accounts.</summary>
        public string SelfUnlockTimeUtc { get; set; }

        /// <summary>Whether the current user holds progress for the Playnite game.</summary>
        public long SelfHasGameData { get; set; }
    }

    /// <summary>
    /// Counts and sums over one group of rows, as <see cref="AchievementReadService.Totals"/> reads
    /// them. Scores and points add positive values only and stop at <see cref="int.MaxValue"/>, as
    /// <c>AchievementGameStats.AddClamped</c> does; unlocked measures count
    /// unlocked rows only, and platform score points leave out softcore unlocks.
    /// </summary>
    internal sealed class AchievementTotalsRecord
    {
        /// <summary>The group's key, as <see cref="AchievementGroup"/> describes it.</summary>
        public string Key { get; set; }
        public long Count { get; set; }
        public long UnlockedCount { get; set; }
        public long CommonCount { get; set; }
        public long CommonUnlocked { get; set; }
        public long UncommonCount { get; set; }
        public long UncommonUnlocked { get; set; }
        public long RareCount { get; set; }
        public long RareUnlocked { get; set; }
        public long UltraRareCount { get; set; }
        public long UltraRareUnlocked { get; set; }
        public long PlatinumCount { get; set; }
        public long PlatinumUnlocked { get; set; }
        public long GoldCount { get; set; }
        public long GoldUnlocked { get; set; }
        public long SilverCount { get; set; }
        public long SilverUnlocked { get; set; }
        public long BronzeCount { get; set; }
        public long BronzeUnlocked { get; set; }
        public long CapstoneCount { get; set; }
        public long CapstoneUnlocked { get; set; }
        public long CollectionScore { get; set; }
        public long PrestigeScore { get; set; }
        public long CollectionScoreTotal { get; set; }
        public long PrestigeScoreTotal { get; set; }
        public long Points { get; set; }
        public long PlatformScorePoints { get; set; }
        public long PointsTotal { get; set; }
        public string FirstUnlockUtc { get; set; }
        public string LastUnlockUtc { get; set; }
        public string LastCapstoneUnlockUtc { get; set; }
        public string FirstPlatinumUnlockUtc { get; set; }

        /// <summary>The sum and count of the rows' global percents, for the average.</summary>
        public double PercentSum { get; set; }
        public long PercentCount { get; set; }
        public double? MinPercent { get; set; }
    }

    /// <summary>
    /// Runs <see cref="AchievementQuery"/> reads on the plugin's read connection and returns plain
    /// records: a page of rows, totals per group, or the distinct keys of the matching rows.
    /// </summary>
    internal sealed class AchievementReadService
    {
        private readonly Func<Func<SQLiteDatabase, object>, object> _withReadDb;

        /// <param name="withReadDb">Runs a read on the read connection, as <c>SqlNadoCacheStore.WithReadDb</c> does.</param>
        public AchievementReadService(Func<Func<SQLiteDatabase, object>, object> withReadDb)
        {
            _withReadDb = withReadDb ?? throw new ArgumentNullException(nameof(withReadDb));
        }

        public IReadOnlyList<AchievementRowRecord> Rows(AchievementQuery query) =>
            (IReadOnlyList<AchievementRowRecord>)_withReadDb(db => Rows(db, query));

        public IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group) =>
            (IReadOnlyList<AchievementTotalsRecord>)_withReadDb(db => Totals(db, query, group));

        public IReadOnlyList<string> Keys(AchievementQuery query, AchievementGroup group) =>
            (IReadOnlyList<string>)_withReadDb(db => Keys(db, query, group));

        internal static IReadOnlyList<AchievementRowRecord> Rows(SQLiteDatabase db, AchievementQuery query)
        {
            var command = AchievementQuerySql.Rows(query);
            return db.Load<AchievementRowRecord>(command.Sql, command.Args).ToList();
        }

        internal static IReadOnlyList<AchievementTotalsRecord> Totals(SQLiteDatabase db, AchievementQuery query, AchievementGroup group)
        {
            var command = AchievementQuerySql.Totals(query, group);
            var rows = db.Load<AchievementTotalsRecord>(command.Sql, command.Args).ToList();
            return group == AchievementGroup.Tag ? SplitTags(rows) : rows;
        }

        internal static IReadOnlyList<string> Keys(SQLiteDatabase db, AchievementQuery query, AchievementGroup group)
        {
            var command = AchievementQuerySql.Keys(query, group);
            var keys = db.Load<KeyRow>(command.Sql, command.Args).Select(row => row.Key);
            return group == AchievementGroup.Tag
                ? keys.SelectMany(TagsOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                : keys.ToList();
        }

        private sealed class KeyRow
        {
            public string Key { get; set; }
        }

        // An untagged row counts under the null tag, as the tallies count it under the empty set.
        private static IEnumerable<string> TagsOf(string tags)
        {
            var parsed = AchievementTagHelper.GetComponents(tags);
            return parsed.Count == 0 ? new string[] { null } : parsed;
        }

        /// <summary>Totals per stored tag text, added up per tag.</summary>
        private static IReadOnlyList<AchievementTotalsRecord> SplitTags(IEnumerable<AchievementTotalsRecord> rows)
        {
            var byTag = new Dictionary<string, AchievementTotalsRecord>(StringComparer.OrdinalIgnoreCase);
            AchievementTotalsRecord untagged = null;
            foreach (var row in rows)
            {
                foreach (var tag in TagsOf(row.Key))
                {
                    AchievementTotalsRecord total;
                    if (tag == null)
                    {
                        total = untagged ??= new AchievementTotalsRecord();
                    }
                    else if (!byTag.TryGetValue(tag, out total))
                    {
                        total = new AchievementTotalsRecord { Key = tag };
                        byTag[tag] = total;
                    }

                    Add(total, row);
                }
            }

            return byTag.Values.Concat(untagged == null ? Enumerable.Empty<AchievementTotalsRecord>() : new[] { untagged }).ToList();
        }

        private static void Add(AchievementTotalsRecord total, AchievementTotalsRecord row)
        {
            long Clamp(long a, long b) => Math.Min(a + b, int.MaxValue);
            string Min(string a, string b) => a == null || (b != null && string.CompareOrdinal(b, a) < 0) ? b : a;
            string Max(string a, string b) => a == null || (b != null && string.CompareOrdinal(b, a) > 0) ? b : a;

            total.Count += row.Count;
            total.UnlockedCount += row.UnlockedCount;
            total.CommonCount += row.CommonCount;
            total.CommonUnlocked += row.CommonUnlocked;
            total.UncommonCount += row.UncommonCount;
            total.UncommonUnlocked += row.UncommonUnlocked;
            total.RareCount += row.RareCount;
            total.RareUnlocked += row.RareUnlocked;
            total.UltraRareCount += row.UltraRareCount;
            total.UltraRareUnlocked += row.UltraRareUnlocked;
            total.PlatinumCount += row.PlatinumCount;
            total.PlatinumUnlocked += row.PlatinumUnlocked;
            total.GoldCount += row.GoldCount;
            total.GoldUnlocked += row.GoldUnlocked;
            total.SilverCount += row.SilverCount;
            total.SilverUnlocked += row.SilverUnlocked;
            total.BronzeCount += row.BronzeCount;
            total.BronzeUnlocked += row.BronzeUnlocked;
            total.CapstoneCount += row.CapstoneCount;
            total.CapstoneUnlocked += row.CapstoneUnlocked;
            total.CollectionScore = Clamp(total.CollectionScore, row.CollectionScore);
            total.PrestigeScore = Clamp(total.PrestigeScore, row.PrestigeScore);
            total.CollectionScoreTotal = Clamp(total.CollectionScoreTotal, row.CollectionScoreTotal);
            total.PrestigeScoreTotal = Clamp(total.PrestigeScoreTotal, row.PrestigeScoreTotal);
            total.Points = Clamp(total.Points, row.Points);
            total.PlatformScorePoints = Clamp(total.PlatformScorePoints, row.PlatformScorePoints);
            total.PointsTotal = Clamp(total.PointsTotal, row.PointsTotal);
            total.FirstUnlockUtc = Min(total.FirstUnlockUtc, row.FirstUnlockUtc);
            total.LastUnlockUtc = Max(total.LastUnlockUtc, row.LastUnlockUtc);
            total.LastCapstoneUnlockUtc = Max(total.LastCapstoneUnlockUtc, row.LastCapstoneUnlockUtc);
            total.FirstPlatinumUnlockUtc = Min(total.FirstPlatinumUnlockUtc, row.FirstPlatinumUnlockUtc);
            total.PercentSum += row.PercentSum;
            total.PercentCount += row.PercentCount;
            if (row.MinPercent.HasValue && (!total.MinPercent.HasValue || row.MinPercent < total.MinPercent))
            {
                total.MinPercent = row.MinPercent;
            }
        }
    }
}
