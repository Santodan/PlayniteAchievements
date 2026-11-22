using Playnite.SDK;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Friends;
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

        /// <summary>With <see cref="AchievementQuery.CompareAccounts"/>: whether they unlocked the same achievement of the same provider game.</summary>
        public long CompareUnlocked { get; set; }

        /// <summary>Their latest unlock time of it.</summary>
        public string CompareUnlockTimeUtc { get; set; }

        /// <summary>Whether they have the provider game: an unlock in it, or ownership from their game list or with playtime.</summary>
        public long CompareHasGame { get; set; }
    }

    /// <summary>
    /// The provider's values of one platform achievement for one user, as
    /// <see cref="AchievementReadService.EditorBaselines"/> reads them; stored values, as
    /// <see cref="AchievementRowRecord"/> holds them.
    /// </summary>
    internal sealed class EditorBaselineRecord
    {
        public long DefinitionId { get; set; }
        public long UserId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public string UnlockedIconPath { get; set; }
        public string LockedIconPath { get; set; }
        public int? Points { get; set; }
        public string TrophyType { get; set; }
        public string Tags { get; set; }
        public long Hidden { get; set; }
        public long IsCapstone { get; set; }

        /// <summary>The unlock time the platform reported, before any override; null when locked.</summary>
        public string UnlockTimeUtc { get; set; }
    }

    /// <summary>The achievement editor's read of some rows: the rows and the provider values of their platform rows.</summary>
    internal sealed class EditorRowsRead
    {
        public EditorRowsRead(
            IReadOnlyList<AchievementRowRecord> rows,
            IReadOnlyDictionary<(long UserId, long DefinitionId), EditorBaselineRecord> baselines)
        {
            Rows = rows ?? Array.Empty<AchievementRowRecord>();
            Baselines = baselines ?? new Dictionary<(long UserId, long DefinitionId), EditorBaselineRecord>();
        }

        public IReadOnlyList<AchievementRowRecord> Rows { get; }

        public IReadOnlyDictionary<(long UserId, long DefinitionId), EditorBaselineRecord> Baselines { get; }
    }

    /// <summary>Reads the keys of the <see cref="AchievementGroup.ProviderGame"/> and <see cref="AchievementGroup.FriendGame"/> groups.</summary>
    internal static class AchievementGroupKeys
    {
        /// <summary>One provider game: its provider, provider game key, provider game id and Playnite game id.</summary>
        internal sealed class ProviderGameIdentity
        {
            public string ProviderKey { get; set; }
            public string ProviderGameKey { get; set; }
            public int AppId { get; set; }
            public Guid? PlayniteGameId { get; set; }
        }

        public static ProviderGameIdentity ProviderGame(string key)
        {
            var parts = (key ?? string.Empty).Split('\u001f');
            if (parts.Length != 4)
            {
                return null;
            }

            return new ProviderGameIdentity
            {
                ProviderKey = string.IsNullOrWhiteSpace(parts[0]) ? null : parts[0],
                ProviderGameKey = string.IsNullOrWhiteSpace(parts[1]) ? null : parts[1].Trim(),
                AppId = int.TryParse(parts[2], out var appId) && appId > 0 ? appId : 0,
                PlayniteGameId = Guid.TryParse(parts[3], out var gameId) && gameId != Guid.Empty ? gameId : (Guid?)null
            };
        }

        /// <summary>The friend key, as <see cref="AchievementGroup.Friend"/> spells it, and the provider game.</summary>
        public static KeyValuePair<string, ProviderGameIdentity> FriendGame(string key)
        {
            var split = (key ?? string.Empty).IndexOf('\u001e');
            return split < 0
                ? new KeyValuePair<string, ProviderGameIdentity>(null, null)
                : new KeyValuePair<string, ProviderGameIdentity>(key.Substring(0, split), ProviderGame(key.Substring(split + 1)));
        }
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

        /// <summary>The lowest positive global percent among the unlocked rows, and among the unlocked base-game capstones.</summary>
        public double? MinPercent { get; set; }
        public double? MinBasePercent { get; set; }

        /// <summary>Hidden achievements still locked.</summary>
        public long HiddenLockedCount { get; set; }

        /// <summary>Achievements the user authored.</summary>
        public long AuthoredCount { get; set; }

        /// <summary>The lowest custom category order index of the group's rows; null when none has one.</summary>
        public long? MinCategoryOrder { get; set; }

        /// <summary>Category groups: a text that sorts the groups by their first row in definition order.</summary>
        public string FirstDisplayOrder { get; set; }
    }

    /// <summary>
    /// Runs <see cref="AchievementQuery"/> reads on the plugin's read connection and returns plain
    /// records: a page of rows, totals per group, or the distinct keys of the matching rows.
    /// </summary>
    internal sealed class AchievementReadService
    {
        private readonly Func<Func<SQLiteDatabase, object>, object> _withReadDb;
        private readonly ILogger _logger;
        private readonly HashSet<string> _loggedFailures = new HashSet<string>(StringComparer.Ordinal);

        /// <param name="withReadDb">Runs a read on the read connection and throws its failure, as <c>SqlNadoCacheStore.ReadOrThrow</c> does.</param>
        public AchievementReadService(Func<Func<SQLiteDatabase, object>, object> withReadDb, ILogger logger = null)
        {
            _withReadDb = withReadDb ?? throw new ArgumentNullException(nameof(withReadDb));
            _logger = logger;
        }

        /// <summary>
        /// Runs <paramref name="read"/> on the read connection. A failure is thrown, so a
        /// <see cref="LiveQuery{T}"/> or <see cref="PagedRows{T}"/> keeps its last result, and is
        /// logged as a warning the first time its kind is seen.
        /// </summary>
        public T Read<T>(Func<SQLiteDatabase, T> read)
        {
            try
            {
                return (T)_withReadDb(db => read(db));
            }
            catch (Exception ex)
            {
                lock (_loggedFailures)
                {
                    if (_loggedFailures.Add(ex.GetType().FullName + ": " + ex.Message))
                    {
                        _logger?.Warn(ex, "An achievement read failed; what it shows keeps its last result.");
                    }
                }

                throw;
            }
        }

        public IReadOnlyList<AchievementRowRecord> Rows(AchievementQuery query) => Read(db => Rows(db, query));

        public IReadOnlyList<AchievementTotalsRecord> Totals(AchievementQuery query, AchievementGroup group) => Read(db => Totals(db, query, group));

        public IReadOnlyList<string> Keys(AchievementQuery query, AchievementGroup group) => Read(db => Keys(db, query, group));

        /// <summary>The identities of the rows <paramref name="query"/> matches, in its order.</summary>
        public IReadOnlyList<AchievementQuerySql.RowKey> SortedKeys(AchievementQuery query) => Read(db => SortedKeys(db, query));

        /// <inheritdoc cref="AchievementQuerySql.SlicedKeys"/>
        public IReadOnlyList<AchievementQuerySql.RowKey> SlicedKeys(AchievementQuery query, int slice) => Read(db => SlicedKeys(db, query, slice));

        /// <summary>The rows of <paramref name="keys"/>, in their order; a key whose row no longer matches is left out.</summary>
        public IReadOnlyList<AchievementRowRecord> Rows(AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys) =>
            Read(db => Rows(db, query, keys));

        /// <summary>The provider's values of the platform rows of <paramref name="keys"/>, by user and definition id.</summary>
        public IReadOnlyDictionary<(long UserId, long DefinitionId), EditorBaselineRecord> EditorBaselines(IReadOnlyList<AchievementQuerySql.RowKey> keys) =>
            Read(db => EditorBaselines(db, keys));

        internal static IReadOnlyDictionary<(long UserId, long DefinitionId), EditorBaselineRecord> EditorBaselines(
            SQLiteDatabase db,
            IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            var result = new Dictionary<(long, long), EditorBaselineRecord>();
            if (keys == null || !keys.Any(key => key.PlayniteGameId == null && key.DefinitionId.HasValue))
            {
                return result;
            }

            var command = AchievementQuerySql.EditorBaselines(keys);
            foreach (var row in db.Load<EditorBaselineRecord>(command.Sql, command.Args))
            {
                result[(row.UserId, row.DefinitionId)] = row;
            }

            return result;
        }

        /// <summary>The library games of the provider games <paramref name="changes"/> name.</summary>
        public IReadOnlyList<Guid> LibraryGamesOf(IEnumerable<FriendCacheChange> changes) => Read(db => LibraryGamesOf(db, changes));

        internal static IReadOnlyList<Guid> LibraryGamesOf(SQLiteDatabase db, IEnumerable<FriendCacheChange> changes) =>
            changes
                .SelectMany(change => db.Load<KeyRow>(
                    "SELECT DISTINCT PlayniteGameId AS Key FROM Games WHERE ProviderKey = ? AND PlayniteGameId IS NOT NULL " +
                    "AND ((? > 0 AND ProviderGameId = ?) OR ProviderGameKey = ?);",
                    change.ProviderKey, change.AppId, change.AppId, change.ProviderGameKey))
                .Select(row => Guid.TryParse(row.Key, out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .Distinct()
                .ToList();

        internal static IReadOnlyList<AchievementRowRecord> Rows(SQLiteDatabase db, AchievementQuery query)
        {
            var command = AchievementQuerySql.Rows(query);
            return db.Load<AchievementRowRecord>(command.Sql, command.Args).ToList();
        }

        internal static IReadOnlyList<AchievementQuerySql.RowKey> SortedKeys(SQLiteDatabase db, AchievementQuery query) =>
            Load(db, AchievementQuerySql.SortedKeys(query));

        /// <summary>The keys <see cref="AchievementQuerySql.SlicedKeys"/> reads.</summary>
        internal static IReadOnlyList<AchievementQuerySql.RowKey> SlicedKeys(SQLiteDatabase db, AchievementQuery query, int slice) =>
            Load(db, AchievementQuerySql.SlicedKeys(query, slice));

        private static IReadOnlyList<AchievementQuerySql.RowKey> Load(SQLiteDatabase db, AchievementQuerySql.Command command) =>
            db.Load<AchievementQuerySql.RowKey>(command.Sql, command.Args).ToList();

        // The columns of the keyed rows only, which skips building them for every row the sort
        // passed over.
        internal static IReadOnlyList<AchievementRowRecord> Rows(SQLiteDatabase db, AchievementQuery query, IReadOnlyList<AchievementQuerySql.RowKey> keys)
        {
            if (keys == null || keys.Count == 0)
            {
                return Array.Empty<AchievementRowRecord>();
            }

            var command = AchievementQuerySql.Rows(query, keys);
            var byKey = new Dictionary<(long, long, long, string, string), AchievementRowRecord>();
            foreach (var row in db.Load<AchievementRowRecord>(command.Sql, command.Args))
            {
                byKey[Identity(KeyOf(row))] = row;
            }

            return keys.Select(key => byKey.TryGetValue(Identity(key), out var row) ? row : null)
                .Where(row => row != null)
                .ToList();
        }

        /// <summary>The key of <paramref name="row"/>, as <see cref="SortedKeys"/> reads it.</summary>
        internal static AchievementQuerySql.RowKey KeyOf(AchievementRowRecord row) =>
            row.IsAuthored != 0
                ? new AchievementQuerySql.RowKey { PlayniteGameId = row.PlayniteGameId, ApiName = row.ApiName }
                : new AchievementQuerySql.RowKey { UserId = row.UserId, GameId = row.GameId, DefinitionId = row.DefinitionId };

        private static (long, long, long, string, string) Identity(AchievementQuerySql.RowKey key) =>
            (key.UserId ?? 0, key.GameId ?? 0, key.DefinitionId ?? 0, key.PlayniteGameId, key.ApiName);

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

                    AddTotals(total, row);
                }
            }

            return byTag.Values.Concat(untagged == null ? Enumerable.Empty<AchievementTotalsRecord>() : new[] { untagged }).ToList();
        }

        /// <summary>Adds <paramref name="row"/> into <paramref name="total"/>, as totals of one group add up.</summary>
        internal static void AddTotals(AchievementTotalsRecord total, AchievementTotalsRecord row)
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
            total.FirstDisplayOrder = Min(total.FirstDisplayOrder, row.FirstDisplayOrder);
            total.PercentSum += row.PercentSum;
            total.PercentCount += row.PercentCount;
            total.HiddenLockedCount += row.HiddenLockedCount;
            total.AuthoredCount += row.AuthoredCount;
            if (row.MinCategoryOrder.HasValue && (!total.MinCategoryOrder.HasValue || row.MinCategoryOrder < total.MinCategoryOrder))
            {
                total.MinCategoryOrder = row.MinCategoryOrder;
            }

            total.MinPercent = MinOf(total.MinPercent, row.MinPercent);
            total.MinBasePercent = MinOf(total.MinBasePercent, row.MinBasePercent);
        }

        internal static double? MinOf(double? a, double? b) => !a.HasValue || (b.HasValue && b < a) ? b : a;
    }
}
