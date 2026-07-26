using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Models.Friends;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Exophase;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Providers.Steam;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database.Rows;
using PlayniteAchievements.Services.Friends;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using Playnite.SDK;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class SqlNadoCacheStore : IDisposable
    {
        private const string PercentNormalizationMetadataKey = "achievement_percent_normalization_v1";

        /// <summary>
        /// Shared predicate selecting active friend rows; requires the Users table to be aliased as "u".
        /// </summary>
        private const string ActiveFriendPredicateSql =
            "u.IsCurrentUser = 0 AND u.IsActiveFriend = 1 AND u.FriendSource IS NOT NULL";

        /// <summary>
        /// Shared column list for achievement-detail join queries; requires AchievementDefinitions
        /// aliased as "ad" and UserAchievements aliased as "ua".
        /// </summary>
        private const string AchievementDetailColumnsSql =
            @"ad.ApiName AS ApiName,
                        ad.DisplayName AS DisplayName,
                        ad.Description AS Description,
                        ad.UnlockedIconPath AS UnlockedIconPath,
                        ad.LockedIconPath AS LockedIconPath,
                        ad.Points AS Points,
                        ad.ScaledPoints AS ScaledPoints,
                        ad.Category AS Category,
                        ad.CategoryType AS CategoryType,
                        ad.TrophyType AS TrophyType,
                        ad.Hidden AS Hidden,
                        ad.IsCapstone AS IsCapstone,
                        ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                        ad.Rarity AS Rarity,
                        ua.Unlocked AS Unlocked,
                        ua.UnlockTimeUtc AS UnlockTimeUtc,
                        ua.ProgressNum AS ProgressNum,
                        ua.ProgressDenom AS ProgressDenom";

        /// <summary>
        /// Shared SELECT/FROM/WHERE prefix for the friend-refresh-candidate loaders that drive from
        /// FriendOwnership. Binds two parameters: u.ProviderKey and g.ProviderKey.
        /// </summary>
        private const string FriendRefreshCandidateSelectFromSql =
            @"SELECT
                        u.ProviderKey AS ProviderKey,
                        u.ExternalUserId AS ExternalUserId,
                        u.DisplayName AS DisplayName,
                        u.AvatarUrl AS AvatarUrl,
                        u.LastRefreshedUtc AS LastRefreshedUtc,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        fo.PlaytimeForeverMinutes AS PlaytimeForeverMinutes,
                        fo.LastPlayedUtc AS LastPlayedUtc,
                        fo.LastOwnershipRefreshUtc AS LastOwnershipRefreshUtc,
                        fo.LastScrapedUtc AS LastScrapedUtc,
                        fo.LastScrapeStatus AS LastScrapeStatus
                      FROM FriendOwnership fo
                      INNER JOIN Users u ON u.Id = fo.UserId
                      INNER JOIN Games g ON g.Id = fo.GameId
                      WHERE u.ProviderKey = ?
                        AND u.IsCurrentUser = 0
                        AND u.IsActiveFriend = 1
                        AND g.ProviderKey = ?";

        private const string FriendRefreshCandidateOrderBySql =
            " ORDER BY COALESCE(fo.LastPlayedUtc, '') DESC, fo.PlaytimeForeverMinutes DESC, u.DisplayName, g.GameName;";

        private sealed class CacheKeyRow
        {
            public string CacheKey { get; set; }
        }

        private sealed class ProgressGameJoinRow
        {
            public long UserGameProgressId { get; set; }
            public long GameId { get; set; }
            public string CacheKey { get; set; }
            public long HasAchievements { get; set; }
            public string LastUpdatedUtc { get; set; }
            public string ProviderKey { get; set; }
            public string ProviderPlatformKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public string LibrarySourceName { get; set; }
        }

        /// <summary>
        /// Common shape of achievement join rows (AchievementDefinitions + UserAchievements columns)
        /// consumed by <see cref="MapAchievementDetail"/>.
        /// </summary>
        private interface IAchievementDetailJoinRow
        {
            string ApiName { get; }
            string DisplayName { get; }
            string Description { get; }
            string UnlockedIconPath { get; }
            string LockedIconPath { get; }
            int? Points { get; }
            int? ScaledPoints { get; }
            string Category { get; }
            string CategoryType { get; }
            string TrophyType { get; }
            long Hidden { get; }
            long IsCapstone { get; }
            double? GlobalPercentUnlocked { get; }
            string Rarity { get; }
            long? Unlocked { get; }
            string UnlockTimeUtc { get; }
            int? ProgressNum { get; }
            int? ProgressDenom { get; }
        }

        private sealed class ProgressAchievementJoinRow : IAchievementDetailJoinRow
        {
            public long UserGameProgressId { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public int? ScaledPoints { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public long Hidden { get; set; }
            public long IsCapstone { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public long? Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
        }

        private sealed class AchievementJoinRow : IAchievementDetailJoinRow
        {
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public int? ScaledPoints { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public long Hidden { get; set; }
            public long IsCapstone { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public long? Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
        }

        private sealed class AchievementPercentNormalizationRow
        {
            public long Id { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
        }

        private sealed class ResolvedUser
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public string FriendSource { get; set; }
        }

        private sealed class FriendRefreshCandidateRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public string AvatarUrl { get; set; }
            public string LastRefreshedUtc { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public int PlaytimeForeverMinutes { get; set; }
            public string LastPlayedUtc { get; set; }
            public string LastOwnershipRefreshUtc { get; set; }
            public string LastScrapedUtc { get; set; }
            public string LastScrapeStatus { get; set; }
        }

        private sealed class FriendOwnershipRecencyRow
        {
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public int PlaytimeForeverMinutes { get; set; }
            public string LastPlayedUtc { get; set; }
            public string LastScrapedUtc { get; set; }
            public string LastScrapeStatus { get; set; }
        }

        private sealed class ProviderGameMappingRow
        {
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
        }

        private sealed class FriendSummaryRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public string AvatarUrl { get; set; }
            public string AvatarPath { get; set; }
            public long SharedGamesCount { get; set; }
            public long GamesWithUnlocksCount { get; set; }
            public long CompletedGamesCount { get; set; }
            public long UnlockedAchievementsCount { get; set; }
            public long RecentUnlockCount { get; set; }
            public string LastUnlockUtc { get; set; }
            public string LastRefreshedUtc { get; set; }
            public long TotalPlaytimeMinutes { get; set; }
        }

        private sealed class FriendGameSummaryRow
        {
            public string ProviderKey { get; set; }
            public string ProviderPlatformKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public long FriendCount { get; set; }
            public long FriendsWithUnlocksCount { get; set; }
            public long UnlockedAchievementsCount { get; set; }
            public long UniqueUnlockedAchievementsCount { get; set; }
            public long TotalAchievements { get; set; }
            public string LastUnlockUtc { get; set; }
            public long TotalPlaytimeMinutes { get; set; }
            public long AveragePlaytimeMinutes { get; set; }
            public string LastPlayedUtc { get; set; }
            public string LastScrapedUtc { get; set; }
            public string LastScrapeStatus { get; set; }
            public string IconPath { get; set; }
            public string CoverPath { get; set; }
        }

        private sealed class FriendGameLinkRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public long PlaytimeForeverMinutes { get; set; }
            public string LastPlayedUtc { get; set; }
        }

        private sealed class ProviderGameDefinitionStateRow
        {
            public string ProviderKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string GameName { get; set; }
            public string IconUrl { get; set; }
            public string Status { get; set; }
            public string LastCheckedUtc { get; set; }
            public long DefinitionCount { get; set; }
        }

        private sealed class FriendRecentUnlockRow
        {
            public string ProviderKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string ProviderGameKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public string FriendExternalUserId { get; set; }
            public string FriendName { get; set; }
            public string FriendAvatarUrl { get; set; }
            public string FriendAvatarPath { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public int? ScaledPoints { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public long Hidden { get; set; }
            public long IsCapstone { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public string UnlockTimeUtc { get; set; }
            public long? Unlocked { get; set; }
            public long? MyUnlocked { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
            public string IconPath { get; set; }
            public string CoverPath { get; set; }
        }

        private sealed class GamePresentation
        {
            public string DisplayName { get; set; }
            public string SortingName { get; set; }
            public string IconPath { get; set; }
            public string CoverPath { get; set; }
            public DateTime? LastPlayed { get; set; }
            public string PlatformText { get; set; }
            public IReadOnlyList<string> Platforms { get; set; } = Array.Empty<string>();
            public string RegionText { get; set; }
            public ulong PlaytimeSeconds { get; set; }
        }

        private sealed class FriendDefinitionMatch
        {
            public long DefinitionId { get; set; }
            public FriendAchievementRow Row { get; set; }
        }

        private sealed class CachedCurrentUserState
        {
            public string ExternalUserId { get; set; }
            public long UserId { get; set; }
        }

        private sealed class CurrentUserScopeRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
        }

        internal readonly object _sync = new object();
        internal readonly ILogger _logger;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly SqlNadoSchemaManager _schemaManager;
        private readonly Dictionary<string, CachedCurrentUserState> _cachedCurrentUsersByProvider =
            new Dictionary<string, CachedCurrentUserState>(StringComparer.OrdinalIgnoreCase);
        private readonly string _pluginUserDataPath;
        private readonly CacheCsvExporter _csvExporter;
        private readonly SummaryCacheReader _summaryReader;
        internal SQLiteDatabase _db;
        private bool _initialized;

        // Second, read-only connection used for long read-only queries (friends overview,
        // whole-library projection, summaries). WAL mode allows a reader to run concurrently
        // with the read-write connection, so these queries no longer serialize behind _sync and
        // stall short per-game grid reads. Guarded by its own lock; never held together with
        // _sync (WithReadDb runs EnsureInitialized before taking _readSync, and the disposal
        // paths take the two locks separately).
        private readonly object _readSync = new object();
        private SQLiteDatabase _readDb;
        private bool _readInitialized;
        private const int ReadConnectionBusyTimeoutMs = 5000;

        public string DatabasePath { get; }

        public SqlNadoCacheStore(PlayniteAchievementsPlugin plugin, ILogger logger, string baseDir)
        {
            _plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
            _logger = logger;
            _pluginUserDataPath = baseDir ?? string.Empty;
            DatabasePath = Path.Combine(_pluginUserDataPath, "achievement_cache.db");
            _schemaManager = new SqlNadoSchemaManager(logger, DatabasePath, baseDir);
            _csvExporter = new CacheCsvExporter(this);
            _summaryReader = new SummaryCacheReader(this);
        }

        public void EnsureInitialized()
        {
            lock (_sync)
            {
                EnsureInitializedLocked();
            }
        }

        public string GetMetadata(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            return WithDb(db =>
            {
                var row = db.Load<CacheMetadataRow>(
                    "SELECT Key, Value FROM CacheMetadata WHERE Key = ? LIMIT 1;",
                    key.Trim()).FirstOrDefault();
                return row?.Value;
            });
        }

        public void SetMetadata(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            WithDb(db =>
            {
                db.ExecuteNonQuery(
                    "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                    key.Trim(),
                    value ?? string.Empty);
            });
        }

        public bool HasAnyCurrentUserCacheRows()
        {
            return WithDb(db =>
            {
                var exists = db.ExecuteScalar<long>(
                    @"SELECT EXISTS(
                        SELECT 1
                        FROM UserGameProgress ugp
                        INNER JOIN Users u ON u.Id = ugp.UserId
                        WHERE u.IsCurrentUser = 1
                        LIMIT 1
                      );");
                return exists != 0;
            });
        }

        public DateTime? GetMostRecentLastUpdatedUtc()
        {
            return WithDb(db =>
            {
                var value = db.ExecuteScalar<string>(
                    @"SELECT MAX(ugp.LastUpdatedUtc)
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      WHERE u.IsCurrentUser = 1;");
                return ParseUtc(value);
            });
        }

        // Cheap freshness probe for the per-game grid read: the latest progress row's
        // LastUpdatedUtc for the cache key, or null when no row exists or the value is
        // unparsable — callers must fall back to the full read on null rather than treating it
        // as a deleted row.
        public DateTime? GetCurrentUserGameLastUpdatedUtc(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var cacheKey = key.Trim();
            return WithDb(db =>
            {
                var value = db.ExecuteScalar<string>(
                    @"SELECT ugp.LastUpdatedUtc
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      WHERE u.IsCurrentUser = 1
                        AND ugp.CacheKey = ?
                      ORDER BY ugp.LastUpdatedUtc DESC
                      LIMIT 1;",
                    cacheKey);
                return ParseUtc(value);
            });
        }

        public DateTime? GetMostRecentFriendLastRefreshedUtc()
        {
            return WithDb(db =>
            {
                var value = db.ExecuteScalar<string>(
                    @"SELECT MAX(u.LastRefreshedUtc)
                      FROM Users u
                      WHERE " + ActiveFriendPredicateSql + ";");
                return ParseUtc(value);
            });
        }

        public string GetCurrentUserScopeToken()
        {
            return WithDb(db =>
            {
                var rows = db.Load<CurrentUserScopeRow>(
                    @"SELECT ProviderKey, ExternalUserId
                      FROM Users
                      WHERE IsCurrentUser = 1
                      ORDER BY ProviderKey, ExternalUserId;").ToList();

                if (rows.Count == 0)
                {
                    return "none";
                }

                var parts = rows
                    .Select(a =>
                    {
                        var provider = a?.ProviderKey?.Trim().ToLowerInvariant();
                        var user = a?.ExternalUserId?.Trim().ToLowerInvariant();
                        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(user))
                        {
                            return null;
                        }

                        return $"{provider}:{user}";
                    })
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return parts.Count == 0 ? "none" : string.Join("|", parts);
            });
        }

        public List<string> GetCachedGameIdsForCurrentUsers()
        {
            return WithDb(db =>
            {
                var rows = db.Load<CacheKeyRow>(
                    @"SELECT DISTINCT ugp.CacheKey AS CacheKey
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      WHERE u.IsCurrentUser = 1
                      ORDER BY ugp.CacheKey;").ToList();

                return rows
                    .Select(a => a.CacheKey)
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        public GameAchievementData LoadCurrentUserGameData(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var cacheKey = key.Trim();
            return WithDb(db =>
            {
                var progress = db.Load<ProgressGameJoinRow>(
                    @"SELECT
                        ugp.Id AS UserGameProgressId,
                        ugp.GameId AS GameId,
                        ugp.CacheKey AS CacheKey,
                        ugp.HasAchievements AS HasAchievements,
                        ugp.LastUpdatedUtc AS LastUpdatedUtc,
                        g.ProviderKey AS ProviderKey,
                        g.ProviderPlatformKey AS ProviderPlatformKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        g.LibrarySourceName AS LibrarySourceName
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      INNER JOIN Games g ON g.Id = ugp.GameId
                      WHERE u.IsCurrentUser = 1
                        AND ugp.CacheKey = ?
                      ORDER BY ugp.LastUpdatedUtc DESC
                      LIMIT 1;",
                    cacheKey).FirstOrDefault();

                if (progress == null)
                {
                    return null;
                }

                var model = CreateModel(progress);
                var details = db.Load<AchievementJoinRow>(
                    @"SELECT
                        " + AchievementDetailColumnsSql + @"
                      FROM AchievementDefinitions ad
                      LEFT JOIN UserAchievements ua
                        ON ua.AchievementDefinitionId = ad.Id
                       AND ua.UserGameProgressId = ?
                      WHERE ad.GameId = ?
                      ORDER BY ad.Id;",
                    progress.UserGameProgressId,
                    progress.GameId).ToList();

                foreach (var row in details)
                {
                    if (string.IsNullOrWhiteSpace(row?.ApiName))
                    {
                        continue;
                    }

                    model.Achievements.Add(MapAchievementDetail(row));
                }

                BackfillPlayniteGameIdFromCacheKey(model, cacheKey);

                return model;
            });
        }

        public List<KeyValuePair<string, GameAchievementData>> LoadAllCurrentUserGameDataByCacheKey()
        {
            return WithReadDb(db =>
            {
                var progressRows = db.Load<ProgressGameJoinRow>(
                    @"WITH LatestProgress AS (
                        SELECT
                            ugp.Id AS UserGameProgressId,
                            ugp.GameId AS GameId,
                            TRIM(ugp.CacheKey) AS CacheKey,
                            ugp.HasAchievements AS HasAchievements,
                            ugp.LastUpdatedUtc AS LastUpdatedUtc,
                            g.ProviderKey AS ProviderKey,
                            g.ProviderPlatformKey AS ProviderPlatformKey,
                            g.ProviderGameId AS ProviderGameId,
                            g.ProviderGameKey AS ProviderGameKey,
                            g.PlayniteGameId AS PlayniteGameId,
                            g.GameName AS GameName,
                            g.LibrarySourceName AS LibrarySourceName,
                            ROW_NUMBER() OVER (
                                PARTITION BY ugp.CacheKey
                                ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                            ) AS RowNum
                        FROM UserGameProgress ugp
                        INNER JOIN Users u ON u.Id = ugp.UserId
                        INNER JOIN Games g ON g.Id = ugp.GameId
                        WHERE u.IsCurrentUser = 1
                          AND ugp.CacheKey IS NOT NULL
                          AND TRIM(ugp.CacheKey) <> ''
                    )
                    SELECT
                        UserGameProgressId,
                        GameId,
                        CacheKey,
                        HasAchievements,
                        LastUpdatedUtc,
                        ProviderKey,
                        ProviderPlatformKey,
                        ProviderGameId,
                        ProviderGameKey,
                        PlayniteGameId,
                        GameName,
                        LibrarySourceName
                    FROM LatestProgress
                    WHERE RowNum = 1
                    ORDER BY LastUpdatedUtc DESC, UserGameProgressId DESC;").ToList();

                if (progressRows.Count == 0)
                {
                    return new List<KeyValuePair<string, GameAchievementData>>();
                }

                var selectedByProgressId = new Dictionary<long, ProgressGameJoinRow>(progressRows.Count);
                var selectedByCacheKey = new Dictionary<string, ProgressGameJoinRow>(StringComparer.OrdinalIgnoreCase);
                var modelsByProgressId = new Dictionary<long, GameAchievementData>(progressRows.Count);
                for (int i = 0; i < progressRows.Count; i++)
                {
                    var row = progressRows[i];
                    var cacheKey = row?.CacheKey?.Trim();
                    if (string.IsNullOrWhiteSpace(cacheKey) || row == null)
                    {
                        continue;
                    }

                    row.CacheKey = cacheKey;
                    selectedByCacheKey[cacheKey] = row;
                    selectedByProgressId[row.UserGameProgressId] = row;

                    var model = CreateModel(row);
                    BackfillPlayniteGameIdFromCacheKey(model, row.CacheKey);
                    modelsByProgressId[row.UserGameProgressId] = model;
                }

                var detailRows = db.Load<ProgressAchievementJoinRow>(
                    @"WITH LatestProgress AS (
                        SELECT
                            ugp.Id AS UserGameProgressId,
                            ugp.GameId AS GameId,
                            ROW_NUMBER() OVER (
                                PARTITION BY ugp.CacheKey
                                ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                            ) AS RowNum
                        FROM UserGameProgress ugp
                        INNER JOIN Users u ON u.Id = ugp.UserId
                        WHERE u.IsCurrentUser = 1
                          AND ugp.CacheKey IS NOT NULL
                          AND TRIM(ugp.CacheKey) <> ''
                    )
                    SELECT
                        lp.UserGameProgressId AS UserGameProgressId,
                        " + AchievementDetailColumnsSql + @"
                      FROM LatestProgress lp
                      INNER JOIN AchievementDefinitions ad ON ad.GameId = lp.GameId
                      LEFT JOIN UserAchievements ua
                        ON ua.AchievementDefinitionId = ad.Id
                       AND ua.UserGameProgressId = lp.UserGameProgressId
                      WHERE lp.RowNum = 1
                      ORDER BY lp.UserGameProgressId, ad.Id;").ToList();

                for (int i = 0; i < detailRows.Count; i++)
                {
                    var row = detailRows[i];
                    if (row == null ||
                        string.IsNullOrWhiteSpace(row.ApiName) ||
                        !modelsByProgressId.TryGetValue(row.UserGameProgressId, out var model))
                    {
                        continue;
                    }

                    model.Achievements.Add(MapAchievementDetail(row));
                }

                return selectedByCacheKey
                    .OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(a =>
                    {
                        return modelsByProgressId.TryGetValue(a.Value.UserGameProgressId, out var model)
                            ? new KeyValuePair<string, GameAchievementData>(a.Key, model)
                            : default(KeyValuePair<string, GameAchievementData>);
                    })
                    .Where(a => !string.IsNullOrWhiteSpace(a.Key) && a.Value != null)
                    .ToList();
            });
        }

        private AchievementDetail MapAchievementDetail(IAchievementDetailJoinRow row)
        {
            var detail = new AchievementDetail
            {
                ApiName = row.ApiName,
                DisplayName = row.DisplayName,
                Description = row.Description,
                UnlockedIconPath = MakeAbsolutePath(row.UnlockedIconPath),
                LockedIconPath = MakeAbsolutePath(row.LockedIconPath),
                Points = row.Points,
                ScaledPoints = row.ScaledPoints,
                Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(row.CategoryType),
                TrophyType = row.TrophyType,
                Hidden = row.Hidden != 0,
                IsCapstone = row.IsCapstone != 0,
                GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                Rarity = ParseStoredRarity(row.Rarity),
                UnlockTimeUtc = ParseUtc(row.UnlockTimeUtc),
                ProgressNum = row.ProgressNum,
                ProgressDenom = row.ProgressDenom
            };

            if (row.Unlocked.HasValue)
            {
                detail.Unlocked = row.Unlocked.Value != 0;
            }

            return detail;
        }

        public CachedSummaryData LoadCachedSummaryData(int recentAchievementDetailLimit = 0)
        {
            return _summaryReader.LoadCachedSummaryData(recentAchievementDetailLimit);
        }

        internal static Guid? ResolveCachedPlayniteGameId(string cacheKey, string playniteGameId)
        {
            var resolved = ParseGuid(playniteGameId);
            if (resolved.HasValue)
            {
                return resolved;
            }

            if (Guid.TryParse((cacheKey ?? string.Empty).Trim(), out var parsedGameId))
            {
                return parsedGameId;
            }

            return null;
        }

        private GamePresentation ResolveGamePresentation(Guid? playniteGameId)
        {
            if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
            {
                return new GamePresentation();
            }

            var playniteGame = _plugin?.PlayniteApi?.Database?.Games?.Get(playniteGameId.Value);
            return CreateGamePresentation(playniteGame);
        }

        // Memoized variant: many friend rows (games and, especially, individual achievements)
        // point at the same Playnite game, so resolving a game's presentation once per load
        // avoids repeated metadata-formatter work for every achievement of that game.
        private GamePresentation ResolveGamePresentation(
            Guid? playniteGameId,
            Dictionary<Guid, GamePresentation> cache)
        {
            if (cache == null)
            {
                return ResolveGamePresentation(playniteGameId);
            }

            var key = playniteGameId ?? Guid.Empty;
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var presentation = ResolveGamePresentation(playniteGameId);
            cache[key] = presentation;
            return presentation;
        }

        private GamePresentation CreateGamePresentation(Playnite.SDK.Models.Game playniteGame)
        {
            return new GamePresentation
            {
                DisplayName = playniteGame?.Name,
                SortingName = playniteGame?.SortingName,
                IconPath = !string.IsNullOrWhiteSpace(playniteGame?.Icon)
                    ? ResolvePlayniteAssetPath(playniteGame.Icon)
                    : null,
                CoverPath = !string.IsNullOrWhiteSpace(playniteGame?.CoverImage)
                    ? ResolvePlayniteAssetPath(playniteGame.CoverImage)
                    : null,
                LastPlayed = playniteGame?.LastActivity,
                PlatformText = PlayniteGameMetadataFormatter.GetPlatformText(playniteGame),
                Platforms = PlayniteGameMetadataFormatter.GetPlatformNames(playniteGame),
                RegionText = PlayniteGameMetadataFormatter.GetRegionText(playniteGame),
                PlaytimeSeconds = playniteGame?.Playtime ?? 0
            };
        }

        private string ResolvePlayniteAssetPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return _plugin?.PlayniteApi?.Database?.GetFullFilePath(path) ?? path;
            }
            catch
            {
                return path;
            }
        }


        public FriendCacheWriteResult SaveFriendList(string providerKey, IReadOnlyList<FriendIdentity> friends)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var friendSource = GetFriendSource(providerKey);
            var now = DateTime.UtcNow;
            var nowIso = ToIso(now);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var incomingCount = friends?.Count(friend => !string.IsNullOrWhiteSpace(friend?.ExternalUserId)) ?? 0;

            try
            {
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        foreach (var friend in friends ?? Array.Empty<FriendIdentity>())
                        {
                            if (string.IsNullOrWhiteSpace(friend?.ExternalUserId))
                            {
                                continue;
                            }

                            friend.ProviderKey = providerKey;
                            UpsertFriendUser(db, providerKey, friendSource, friend, nowIso);
                            seen.Add(friend.ExternalUserId.Trim());
                        }

                        MarkMissingFriendsInactive(db, providerKey, friendSource, seen, nowIso);
                    });
                });

                return FriendCacheWriteResult.Ok(incomingCount, seen.Count, Math.Max(0, incomingCount - seen.Count));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to save friend list for provider={providerKey}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public FriendCacheWriteResult SaveFriendOwnership(
            string providerKey,
            string externalUserId,
            IReadOnlyList<FriendGameOwnership> ownership,
            FriendOwnershipSaveOptions options = null)
        {
            providerKey = NormalizeProviderKey(providerKey);
            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                return FriendCacheWriteResult.Failed("Friend id is missing.");
            }

            var nowIso = ToIso(DateTime.UtcNow);
            var validOwnership = (ownership ?? Array.Empty<FriendGameOwnership>())
                .Where(item => HasProviderGameIdentity(item?.AppId ?? 0, item?.ProviderGameKey))
                .Select(item =>
                {
                    item.ProviderGameKey = NormalizeProviderGameKey(item.ProviderGameKey);
                    return item;
                })
                .ToList();
            var incomingCount = validOwnership.Count;
            var writtenCount = 0;
            var skippedCount = 0;
            var seenSharedGameIds = new HashSet<long>();

            try
            {
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        var user = LoadFriendUser(db, providerKey, externalUserId);
                        if (user == null)
                        {
                            skippedCount = incomingCount;
                            return;
                        }

                        foreach (var item in validOwnership)
                        {
                            var game = LoadFriendGameByAppId(
                                db,
                                providerKey,
                                item.AppId,
                                item.ProviderGameKey,
                                item.ProviderPlatformKey,
                                item.PlayniteGameId,
                                options?.IncludeProviderOnlyGames == true,
                                item.GameName,
                                item.IconUrl,
                                nowIso);
                            if (game == null)
                            {
                                skippedCount++;
                                continue;
                            }

                            if (!UpsertFriendOwnership(db, user.Id, game.Id, item, nowIso))
                            {
                                throw new InvalidOperationException(
                                    $"Failed to upsert FriendOwnership for userId={user.Id}, gameId={game.Id}.");
                            }

                            writtenCount++;
                            seenSharedGameIds.Add(game.Id);
                        }

                        // Ownership-safety invariant: only a complete, non-empty shared snapshot may
                        // remove stale rows. Soft-empty or scoped saves can upsert their rows, but must
                        // not downgrade a friend's existing shared ownership cache.
                        if (options?.IncludeProviderOnlyGames != true &&
                            options?.PruneStaleShared == true &&
                            seenSharedGameIds.Count > 0)
                        {
                            DeleteStaleSharedFriendOwnership(db, user.Id, seenSharedGameIds);
                        }
                    });
                });

                return FriendCacheWriteResult.Ok(incomingCount, writtenCount, skippedCount);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to save friend ownership for provider={providerKey}, friend={externalUserId}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public FriendCacheWriteResult SaveFriendGameDefinition(
            string providerKey,
            FriendGameDefinition definition)
        {
            providerKey = NormalizeProviderKey(providerKey);
            if (definition != null)
            {
                definition.ProviderGameKey = NormalizeProviderGameKey(definition.ProviderGameKey);
            }

            if (definition == null || !HasProviderGameIdentity(definition.AppId, definition.ProviderGameKey))
            {
                return FriendCacheWriteResult.Failed("Friend game definition is missing a provider game id.");
            }

            var now = DateTime.UtcNow;
            var nowIso = ToIso(now);
            var checkedIso = ToIso(definition.LastCheckedUtc == default(DateTime)
                ? now
                : definition.LastCheckedUtc);
            var status = ToDefinitionStatusStorage(definition.Status);

            try
            {
                var writtenCount = 0;
                var renamedApiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Guid? renamedPlayniteGameId = null;
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        if (definition.Status == FriendGameDefinitionStatus.Ok &&
                            definition.Achievements != null &&
                            definition.Achievements.Count > 0)
                        {
                            // Exophase keys resolve through the single-row invariant (healing any
                            // duplicates) without touching the mapping flag; other providers keep
                            // the existing lookup.
                            var game = IsExophaseProvider(providerKey) && !string.IsNullOrWhiteSpace(definition.ProviderGameKey)
                                ? ResolveSingleExophaseFriendGameRow(
                                    db,
                                    providerKey,
                                    definition.AppId,
                                    NormalizeProviderGameKey(definition.ProviderGameKey),
                                    definition.ProviderPlatformKey,
                                    null,
                                    allowCreate: true,
                                    applyOwnershipMapping: false,
                                    definition.GameName,
                                    nowIso)
                                : LoadAnyGameByAppId(db, providerKey, definition.AppId, definition.ProviderGameKey);
                            var gameId = game?.Id ?? EnsureProviderOnlyGame(
                                db,
                                providerKey,
                                definition.AppId,
                                definition.ProviderGameKey,
                                definition.ProviderPlatformKey,
                                definition.GameName,
                                nowIso);
                            if (gameId > 0)
                            {
                                // A complete Ok definition fetch is authoritative for provider-only
                                // rows. Mapped proxy rows mirror the current user's schema, which
                                // stays canonical: Exophase definitions use aggregator api-name
                                // families that the proxy refresh prunes, so they are never written
                                // here. Native-provider definitions share the user's key family, so
                                // when the mapped row has no schema yet the category-bearing fetch
                                // seeds it (non-authoritative: it must never prune a user scan), and
                                // otherwise only Default category placeholders are backfilled.
                                var isProviderOnlyRow = !ParseGuid(game?.PlayniteGameId).HasValue;
                                if (isProviderOnlyRow)
                                {
                                    UpsertAchievementDefinitions(
                                        db,
                                        gameId,
                                        definition.Achievements,
                                        nowIso,
                                        checkedIso,
                                        renamedApiNames,
                                        authoritativeSource: true);
                                    writtenCount = definition.Achievements.Count;
                                }
                                else if (IsExophaseProvider(providerKey))
                                {
                                    _logger?.Debug($"Skipping definition write for mapped friend game {providerKey}/{definition.ProviderGameKey}; the mapped current-user schema is canonical for its proxy row.");
                                }
                                else if (db.ExecuteScalar<long>(
                                    "SELECT COUNT(1) FROM AchievementDefinitions WHERE GameId = ?;",
                                    gameId) == 0)
                                {
                                    UpsertAchievementDefinitions(
                                        db,
                                        gameId,
                                        definition.Achievements,
                                        nowIso,
                                        checkedIso,
                                        renamedApiNames,
                                        authoritativeSource: false);
                                    writtenCount = definition.Achievements.Count;
                                }
                                else
                                {
                                    writtenCount = BackfillDefinitionCategories(
                                        db,
                                        gameId,
                                        definition.Achievements,
                                        checkedIso);
                                }

                                renamedPlayniteGameId = ParseGuid(game?.PlayniteGameId);
                            }
                        }

                        // Image source URLs are not persisted; the header banner is downloaded to
                        // local IconPath/CoverPath during refresh instead.
                        UpsertProviderGameDefinitionState(
                            db,
                            providerKey,
                            definition.AppId,
                            definition.ProviderGameKey,
                            definition.GameName,
                            null,
                            status,
                            checkedIso,
                            nowIso);
                    });
                });

                var result = FriendCacheWriteResult.Ok(
                    incomingCount: definition.Achievements?.Count ?? 0,
                    writtenCount: writtenCount,
                    skippedCount: Math.Max(0, (definition.Achievements?.Count ?? 0) - writtenCount));
                if (renamedApiNames.Count > 0)
                {
                    result.RenamedApiNames = renamedApiNames;
                    result.RenamedPlayniteGameId = renamedPlayniteGameId;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to save friend game definition for provider={providerKey}, game={ToProviderGameLogKey(definition.AppId, definition.ProviderGameKey)}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public Dictionary<string, FriendGameDefinitionState> LoadFriendGameDefinitionStates(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var keys = (providerGameKeys ?? Array.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count == 0)
            {
                return new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);
            }

            return WithDb(db =>
            {
                var numericIds = keys
                    .Select(key => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                var slugKeys = keys
                    .Where(key => !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .ToList();

                var result = new Dictionary<string, FriendGameDefinitionState>(StringComparer.OrdinalIgnoreCase);
                var clauses = new List<string>();
                var args = new List<object> { providerKey };

                if (numericIds.Count > 0)
                {
                    clauses.Add("ProviderGameId IN (" + string.Join(",", numericIds.Select(_ => "?")) + ")");
                    args.AddRange(numericIds.Cast<object>());
                }

                if (slugKeys.Count > 0)
                {
                    clauses.Add("ProviderGameKey IN (" + string.Join(",", slugKeys.Select(_ => "?")) + ")");
                    args.AddRange(slugKeys.Cast<object>());
                }

                if (clauses.Count == 0)
                {
                    return result;
                }

                var sql =
                    @"SELECT ProviderKey, ProviderGameId, ProviderGameKey, GameName, IconUrl, Status, LastCheckedUtc
                      FROM ProviderGameDefinitionState
                      WHERE ProviderKey = ?
                        AND (" + string.Join(" OR ", clauses) + ");";

                result = db.Load<ProviderGameDefinitionStateRow>(sql, args.ToArray())
                    .Where(row => row != null && HasProviderGameIdentity((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey))
                    .GroupBy(row => ToProviderGameCacheKey((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group =>
                        {
                            var row = group.First();
                            return new FriendGameDefinitionState
                            {
                                ProviderKey = row.ProviderKey,
                                AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                                ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                                GameName = row.GameName,
                                IconUrl = row.IconUrl,
                                Status = ParseDefinitionStatus(row.Status),
                                LastCheckedUtc = ParseUtc(row.LastCheckedUtc)
                            };
                        });

                var missingKeys = keys
                    .Where(key => !result.ContainsKey(key))
                    .ToList();
                if (missingKeys.Count == 0)
                {
                    return result;
                }

                var missingNumericIds = missingKeys
                    .Select(key => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                var missingSlugKeys = missingKeys
                    .Where(key => !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .ToList();
                var ownedClauses = new List<string>();
                var ownedArgs = new List<object> { providerKey };
                if (missingNumericIds.Count > 0)
                {
                    ownedClauses.Add("g.ProviderGameId IN (" + string.Join(",", missingNumericIds.Select(_ => "?")) + ")");
                    ownedArgs.AddRange(missingNumericIds.Cast<object>());
                }

                if (missingSlugKeys.Count > 0)
                {
                    ownedClauses.Add("g.ProviderGameKey IN (" + string.Join(",", missingSlugKeys.Select(_ => "?")) + ")");
                    ownedArgs.AddRange(missingSlugKeys.Cast<object>());
                }

                if (ownedClauses.Count == 0)
                {
                    return result;
                }

                var ownedSql =
                    @"SELECT
                        g.ProviderKey AS ProviderKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.GameName AS GameName,
                        COUNT(ad.Id) AS DefinitionCount,
                        g.LastUpdatedUtc AS LastCheckedUtc
                      FROM Games g
                      LEFT JOIN AchievementDefinitions ad ON ad.GameId = g.Id
                      WHERE g.ProviderKey = ?
                        AND (" + string.Join(" OR ", ownedClauses) + @")
                        AND g.PlayniteGameId IS NOT NULL
                        AND TRIM(g.PlayniteGameId) <> ''
                      GROUP BY g.Id, g.ProviderKey, g.ProviderGameId, g.ProviderGameKey, g.GameName, g.LastUpdatedUtc;";

                foreach (var row in db.Load<ProviderGameDefinitionStateRow>(ownedSql, ownedArgs.ToArray())
                             .Where(row => row != null && HasProviderGameIdentity((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey)))
                {
                    result[ToProviderGameCacheKey((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey)] = new FriendGameDefinitionState
                    {
                        ProviderKey = row.ProviderKey,
                        AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                        ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                        GameName = row.GameName,
                        Status = row.DefinitionCount > 0
                            ? FriendGameDefinitionStatus.Ok
                            : FriendGameDefinitionStatus.Unavailable,
                        LastCheckedUtc = DateTime.UtcNow
                    };
                }

                return result;
            });
        }

        // Returns the subset of the given provider game cache keys whose cached achievement
        // definitions still carry legacy display-derived Exophase keys ("exophase_..."). Such games
        // need a definition re-fetch so the rename-aware upsert can migrate them to stable ids
        // before locale-independent unlock rows can match.
        public List<string> LoadLegacyKeyedDefinitionGameKeys(
            string providerKey,
            IReadOnlyCollection<string> providerGameKeys)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var keys = (providerGameKeys ?? Array.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (keys.Count == 0)
            {
                return new List<string>();
            }

            return WithDb(db =>
            {
                var numericIds = keys
                    .Select(key => int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();
                var slugKeys = keys
                    .Where(key => !int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                    .ToList();

                var clauses = new List<string>();
                var args = new List<object> { providerKey };
                if (numericIds.Count > 0)
                {
                    clauses.Add("g.ProviderGameId IN (" + string.Join(",", numericIds.Select(_ => "?")) + ")");
                    args.AddRange(numericIds.Cast<object>());
                }

                if (slugKeys.Count > 0)
                {
                    // COLLATE NOCASE mirrors the OrdinalIgnoreCase key comparisons used everywhere
                    // else in this file; SQLite IN is case-sensitive by default.
                    clauses.Add("g.ProviderGameKey COLLATE NOCASE IN (" + string.Join(",", slugKeys.Select(_ => "?")) + ")");
                    args.AddRange(slugKeys.Cast<object>());
                }

                if (clauses.Count == 0)
                {
                    return new List<string>();
                }

                var sql =
                    @"SELECT DISTINCT
                        g.Id AS Id,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey
                      FROM Games g
                      INNER JOIN AchievementDefinitions d ON d.GameId = g.Id
                      WHERE g.ProviderKey = ?
                        AND d.ApiName LIKE 'exophase\_%' ESCAPE '\'
                        AND (" + string.Join(" OR ", clauses) + ");";

                var requestedKeySet = new HashSet<string>(keys, StringComparer.OrdinalIgnoreCase);
                return db.Load<GameRow>(sql, args.ToArray())
                    .Where(row => row != null)
                    .Select(row => ToProviderGameCacheKey((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey))
                    .Where(key => !string.IsNullOrWhiteSpace(key) && requestedKeySet.Contains(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        public FriendCacheWriteResult SaveProviderGameImagePaths(
            string providerKey,
            string providerGameKey,
            int appId,
            string iconAbsolutePath,
            string coverAbsolutePath)
        {
            providerKey = NormalizeProviderKey(providerKey);
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!HasProviderGameIdentity(appId, providerGameKey))
            {
                return FriendCacheWriteResult.Failed("Provider game id is missing.");
            }

            var iconPath = MakeRelativePath(iconAbsolutePath);
            var coverPath = MakeRelativePath(coverAbsolutePath);
            if (string.IsNullOrWhiteSpace(iconPath) && string.IsNullOrWhiteSpace(coverPath))
            {
                return FriendCacheWriteResult.Ok(incomingCount: 0, writtenCount: 0, skippedCount: 0);
            }

            try
            {
                var nowIso = ToIso(DateTime.UtcNow);
                WithDb(db =>
                {
                    if (!string.IsNullOrWhiteSpace(providerGameKey) && appId > 0)
                    {
                        db.ExecuteNonQuery(
                            @"UPDATE Games
                              SET IconPath = COALESCE(?, IconPath),
                                  CoverPath = COALESCE(?, CoverPath),
                                  LastUpdatedUtc = ?
                              WHERE ProviderKey = ?
                                AND (ProviderGameKey = ? OR ProviderGameId = ?)
                                AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '');",
                            DbValue(iconPath),
                            DbValue(coverPath),
                            nowIso,
                            providerKey,
                            providerGameKey,
                            appId);
                    }
                    else if (!string.IsNullOrWhiteSpace(providerGameKey))
                    {
                        db.ExecuteNonQuery(
                            @"UPDATE Games
                              SET IconPath = COALESCE(?, IconPath),
                                  CoverPath = COALESCE(?, CoverPath),
                                  LastUpdatedUtc = ?
                              WHERE ProviderKey = ?
                                AND ProviderGameKey = ?
                                AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '');",
                            DbValue(iconPath),
                            DbValue(coverPath),
                            nowIso,
                            providerKey,
                            providerGameKey);
                    }
                    else
                    {
                        db.ExecuteNonQuery(
                            @"UPDATE Games
                              SET IconPath = COALESCE(?, IconPath),
                                  CoverPath = COALESCE(?, CoverPath),
                                  LastUpdatedUtc = ?
                              WHERE ProviderKey = ?
                                AND ProviderGameId = ?
                                AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '');",
                            DbValue(iconPath),
                            DbValue(coverPath),
                            nowIso,
                            providerKey,
                            appId);
                    }
                });

                return FriendCacheWriteResult.Ok(incomingCount: 1, writtenCount: 1, skippedCount: 0);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to save provider game image paths for {providerKey}/{ToProviderGameLogKey(appId, providerGameKey)}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public FriendUnownedCacheStats GetUnownedFriendGameCacheStats()
        {
            return WithDb(db => LoadUnownedFriendGameCacheStats(db));
        }

        public FriendUnownedCacheClearResult ClearUnownedFriendGameData()
        {
            try
            {
                FriendUnownedCacheClearResult result = null;
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        var before = LoadUnownedFriendGameCacheStats(db);
                        db.ExecuteNonQuery(
                            @"DELETE FROM UserAchievements
                              WHERE UserGameProgressId IN (
                                  SELECT ugp.Id
                                  FROM UserGameProgress ugp
                                  INNER JOIN Games g ON g.Id = ugp.GameId
                                  INNER JOIN Users u ON u.Id = ugp.UserId
                                  WHERE u.IsCurrentUser = 0
                                    AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                              );");
                        db.ExecuteNonQuery(
                            @"DELETE FROM UserGameProgress
                              WHERE Id IN (
                                  SELECT ugp.Id
                                  FROM UserGameProgress ugp
                                  INNER JOIN Games g ON g.Id = ugp.GameId
                                  INNER JOIN Users u ON u.Id = ugp.UserId
                                  WHERE u.IsCurrentUser = 0
                                    AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                              );");
                        db.ExecuteNonQuery(
                            @"DELETE FROM FriendOwnership
                              WHERE GameId IN (
                                  SELECT Id
                                  FROM Games
                                  WHERE PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = ''
                              );");
                        db.ExecuteNonQuery(
                            @"DELETE FROM AchievementDefinitions
                              WHERE GameId IN (
                                  SELECT g.Id
                                  FROM Games g
                                  WHERE (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM UserGameProgress ugp
                                        INNER JOIN Users u ON u.Id = ugp.UserId
                                        WHERE ugp.GameId = g.Id
                                          AND u.IsCurrentUser = 1
                                    )
                              );");
                        db.ExecuteNonQuery(
                            @"DELETE FROM Games
                              WHERE (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '')
                                AND NOT EXISTS (
                                    SELECT 1
                                    FROM UserGameProgress ugp
                                    INNER JOIN Users u ON u.Id = ugp.UserId
                                    WHERE ugp.GameId = Games.Id
                                      AND u.IsCurrentUser = 1
                                );");
                        db.ExecuteNonQuery("DELETE FROM ProviderGameDefinitionState;");

                        result = new FriendUnownedCacheClearResult
                        {
                            Success = true,
                            Games = before.Games,
                            Definitions = before.Definitions,
                            OwnershipRows = before.OwnershipRows,
                            ProgressRows = before.ProgressRows,
                            AchievementRows = before.AchievementRows,
                            DefinitionStates = before.DefinitionStates
                        };
                    });
                });

                return result ?? new FriendUnownedCacheClearResult { Success = true };
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to clear unowned friend game data.");
                return FriendUnownedCacheClearResult.Failed(ex.Message);
            }
        }

        public FriendCacheWriteResult ClearUnownedFriendGame(string providerKey, int appId, string providerGameKey)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var normalizedGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!HasProviderGameIdentity(appId, normalizedGameKey))
            {
                return FriendCacheWriteResult.Failed("Provider game identity is missing.");
            }

            // Shared predicate selecting the single unowned (provider-only) Games row(s) for this
            // provider game. Matching either ProviderGameId or ProviderGameKey mirrors the provider
            // identity semantics used elsewhere; the OR is safe because DbValue(null) binds DBNull,
            // which never equals a stored key (and appId <= 0 never matches a real ProviderGameId).
            const string gameFilter =
                "g.ProviderKey = ? AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '') " +
                "AND (g.ProviderGameId = ? OR g.ProviderGameKey = ?)";

            try
            {
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        db.ExecuteNonQuery(
                            @"DELETE FROM UserAchievements
                              WHERE UserGameProgressId IN (
                                  SELECT ugp.Id
                                  FROM UserGameProgress ugp
                                  INNER JOIN Games g ON g.Id = ugp.GameId
                                  INNER JOIN Users u ON u.Id = ugp.UserId
                                  WHERE u.IsCurrentUser = 0 AND " + gameFilter + ");",
                            providerKey, appId, DbValue(normalizedGameKey));
                        db.ExecuteNonQuery(
                            @"DELETE FROM UserGameProgress
                              WHERE Id IN (
                                  SELECT ugp.Id
                                  FROM UserGameProgress ugp
                                  INNER JOIN Games g ON g.Id = ugp.GameId
                                  INNER JOIN Users u ON u.Id = ugp.UserId
                                  WHERE u.IsCurrentUser = 0 AND " + gameFilter + ");",
                            providerKey, appId, DbValue(normalizedGameKey));
                        db.ExecuteNonQuery(
                            @"DELETE FROM FriendOwnership
                              WHERE GameId IN (
                                  SELECT g.Id FROM Games g WHERE " + gameFilter + ");",
                            providerKey, appId, DbValue(normalizedGameKey));
                        db.ExecuteNonQuery(
                            @"DELETE FROM AchievementDefinitions
                              WHERE GameId IN (
                                  SELECT g.Id
                                  FROM Games g
                                  WHERE " + gameFilter + @"
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM UserGameProgress ugp
                                        INNER JOIN Users u ON u.Id = ugp.UserId
                                        WHERE ugp.GameId = g.Id
                                          AND u.IsCurrentUser = 1
                                    ));",
                            providerKey, appId, DbValue(normalizedGameKey));
                        db.ExecuteNonQuery(
                            @"DELETE FROM Games
                              WHERE Id IN (
                                  SELECT g.Id
                                  FROM Games g
                                  WHERE " + gameFilter + @"
                                    AND NOT EXISTS (
                                        SELECT 1
                                        FROM UserGameProgress ugp
                                        INNER JOIN Users u ON u.Id = ugp.UserId
                                        WHERE ugp.GameId = g.Id
                                          AND u.IsCurrentUser = 1
                                    ));",
                            providerKey, appId, DbValue(normalizedGameKey));
                        db.ExecuteNonQuery(
                            @"DELETE FROM ProviderGameDefinitionState
                              WHERE ProviderKey = ?
                                AND (ProviderGameId = ? OR ProviderGameKey = ?);",
                            providerKey, appId, DbValue(normalizedGameKey));
                    });
                });

                return FriendCacheWriteResult.Ok(incomingCount: 1, writtenCount: 1, skippedCount: 0);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to clear unowned friend game data for {providerKey}/{ToProviderGameLogKey(appId, normalizedGameKey)}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public bool IsProviderGameMappedToPlayniteLibrary(string providerKey, int appId, string providerGameKey)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var normalizedGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!HasProviderGameIdentity(appId, normalizedGameKey))
            {
                return false;
            }

            return WithDb(db =>
            {
                var clauses = new List<string>();
                var args = new List<object> { providerKey };
                if (appId > 0)
                {
                    clauses.Add("ProviderGameId = ?");
                    args.Add(appId);
                }

                if (!string.IsNullOrWhiteSpace(normalizedGameKey))
                {
                    clauses.Add("ProviderGameKey = ?");
                    args.Add(normalizedGameKey);
                }

                if (clauses.Count == 0)
                {
                    return false;
                }

                var count = db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM Games
                      WHERE ProviderKey = ?
                        AND PlayniteGameId IS NOT NULL
                        AND TRIM(PlayniteGameId) <> ''
                        AND (" + string.Join(" OR ", clauses) + ");",
                    args.ToArray());
                return count > 0;
            });
        }

        public IReadOnlyList<FriendGameMapping> LoadFriendGameMappings(string providerKey)
        {
            providerKey = NormalizeProviderKey(providerKey);
            return WithDb(db =>
            {
                var rows = db.Load<ProviderGameMappingRow>(
                    @"SELECT
                        ProviderGameId AS ProviderGameId,
                        ProviderGameKey AS ProviderGameKey,
                        PlayniteGameId AS PlayniteGameId
                      FROM Games
                      WHERE ProviderKey = ?
                        AND PlayniteGameId IS NOT NULL
                        AND TRIM(PlayniteGameId) <> ''
                        AND (
                            (ProviderGameId IS NOT NULL AND ProviderGameId > 0)
                            OR (ProviderGameKey IS NOT NULL AND TRIM(ProviderGameKey) <> '')
                        );",
                    providerKey).ToList();

                var mappings = new List<FriendGameMapping>(rows.Count);
                foreach (var row in rows)
                {
                    var playniteGameId = ParseGuid(row.PlayniteGameId);
                    if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
                    {
                        continue;
                    }

                    mappings.Add(new FriendGameMapping
                    {
                        AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                        ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                        PlayniteGameId = playniteGameId.Value
                    });
                }

                return (IReadOnlyList<FriendGameMapping>)mappings;
            });
        }

        public FriendCacheWriteResult PromoteProviderOnlyGameToPlayniteBacked(
            string providerKey,
            int appId,
            string providerGameKey,
            Guid playniteGameId)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var normalizedGameKey = NormalizeProviderGameKey(providerGameKey);
            if (playniteGameId == Guid.Empty || !HasProviderGameIdentity(appId, normalizedGameKey))
            {
                return FriendCacheWriteResult.Failed("Playnite or provider game identity is missing.");
            }

            try
            {
                var promotedRows = 0;
                var sourceRows = 0;
                var playniteGameIdText = playniteGameId.ToString();
                var nowIso = ToIso(DateTime.UtcNow);

                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        var identity = BuildProviderGameIdentityFilter(appId, normalizedGameKey, out var identityArgs);
                        if (string.IsNullOrWhiteSpace(identity))
                        {
                            return;
                        }

                        var targetArgs = new List<object> { providerKey, playniteGameIdText };
                        targetArgs.AddRange(identityArgs);
                        var target = db.Load<GameRow>(
                            @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                              FROM Games
                              WHERE ProviderKey = ?
                                AND PlayniteGameId = ?
                                AND (" + identity + @")
                              ORDER BY LastUpdatedUtc DESC, Id DESC
                              LIMIT 1;",
                            targetArgs.ToArray()).FirstOrDefault();
                        if (target == null)
                        {
                            return;
                        }

                        var sourceArgs = new List<object> { providerKey };
                        sourceArgs.AddRange(identityArgs);
                        sourceArgs.Add(target.Id);
                        var sources = db.Load<GameRow>(
                            @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                              FROM Games
                              WHERE ProviderKey = ?
                                AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '')
                                AND (" + identity + @")
                                AND Id <> ?
                              ORDER BY LastUpdatedUtc DESC, Id DESC;",
                            sourceArgs.ToArray()).ToList();

                        sourceRows = sources.Count;
                        foreach (var source in sources)
                        {
                            if (source == null || source.Id <= 0)
                            {
                                continue;
                            }

                            MergeProviderOnlyDefinitionsIntoTarget(db, source.Id, target.Id, nowIso);
                            MoveFriendOwnershipToPromotedGame(db, source.Id, target.Id, nowIso);
                            MoveFriendProgressToPromotedGame(db, source.Id, target.Id, nowIso);
                            DeleteProviderOnlyGameIfOrphaned(db, source.Id);
                            promotedRows++;
                        }
                    });
                });

                return FriendCacheWriteResult.Ok(
                    incomingCount: sourceRows,
                    writtenCount: promotedRows,
                    skippedCount: Math.Max(0, sourceRows - promotedRows));
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to promote provider-only friend game for {providerKey}/{ToProviderGameLogKey(appId, normalizedGameKey)}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        private static string BuildProviderGameIdentityFilter(
            int appId,
            string providerGameKey,
            out List<object> args)
        {
            args = new List<object>();
            var clauses = new List<string>();
            if (appId > 0)
            {
                clauses.Add("ProviderGameId = ?");
                args.Add(appId);
            }

            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                clauses.Add("ProviderGameKey = ?");
                args.Add(providerGameKey);
            }

            return clauses.Count == 0
                ? null
                : string.Join(" OR ", clauses);
        }

        private static void MergeProviderOnlyDefinitionsIntoTarget(
            SQLiteDatabase db,
            long sourceGameId,
            long targetGameId,
            string nowIso)
        {
            db.ExecuteNonQuery(
                @"UPDATE AchievementDefinitions
                  SET GameId = ?,
                      UpdatedUtc = ?
                  WHERE GameId = ?
                    AND NOT EXISTS (
                        SELECT 1
                        FROM AchievementDefinitions target
                        WHERE target.GameId = ?
                          AND target.ApiName = AchievementDefinitions.ApiName
                    );",
                targetGameId,
                nowIso,
                sourceGameId,
                targetGameId);
        }

        private static void MoveFriendOwnershipToPromotedGame(
            SQLiteDatabase db,
            long sourceGameId,
            long targetGameId,
            string nowIso)
        {
            db.ExecuteNonQuery(
                @"DELETE FROM FriendOwnership
                  WHERE GameId = ?
                    AND EXISTS (
                        SELECT 1
                        FROM FriendOwnership target
                        WHERE target.UserId = FriendOwnership.UserId
                          AND target.GameId = ?
                    );",
                sourceGameId,
                targetGameId);

            db.ExecuteNonQuery(
                @"UPDATE FriendOwnership
                  SET GameId = ?,
                      UpdatedUtc = ?
                  WHERE GameId = ?;",
                targetGameId,
                nowIso,
                sourceGameId);
        }

        private static void MoveFriendProgressToPromotedGame(
            SQLiteDatabase db,
            long sourceGameId,
            long targetGameId,
            string nowIso)
        {
            var sourceProgressRows = db.Load<UserGameProgressRow>(
                @"SELECT Id, UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc
                  FROM UserGameProgress
                  WHERE GameId = ?;",
                sourceGameId).ToList();

            foreach (var sourceProgress in sourceProgressRows)
            {
                var targetProgress = db.Load<UserGameProgressRow>(
                    @"SELECT Id, UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc
                      FROM UserGameProgress
                      WHERE UserId = ?
                        AND GameId = ?
                      LIMIT 1;",
                    sourceProgress.UserId,
                    targetGameId).FirstOrDefault();

                if (targetProgress == null)
                {
                    RemapFriendAchievementRowsToTargetDefinitions(db, sourceProgress.Id, targetGameId);
                    db.ExecuteNonQuery(
                        @"UPDATE UserGameProgress
                          SET GameId = ?,
                              UpdatedUtc = ?
                          WHERE Id = ?;",
                        targetGameId,
                        nowIso,
                        sourceProgress.Id);
                    continue;
                }

                CopyMatchingFriendAchievements(db, sourceProgress.Id, targetProgress.Id, targetGameId, nowIso);
                db.ExecuteNonQuery(
                    "DELETE FROM UserAchievements WHERE UserGameProgressId = ?;",
                    sourceProgress.Id);
                db.ExecuteNonQuery(
                    "DELETE FROM UserGameProgress WHERE Id = ?;",
                    sourceProgress.Id);
            }
        }

        private static void RemapFriendAchievementRowsToTargetDefinitions(
            SQLiteDatabase db,
            long progressId,
            long targetGameId)
        {
            db.ExecuteNonQuery(
                @"UPDATE UserAchievements
                  SET AchievementDefinitionId = (
                      SELECT target.Id
                      FROM AchievementDefinitions source
                      INNER JOIN AchievementDefinitions target
                        ON target.GameId = ?
                       AND target.ApiName = source.ApiName
                      WHERE source.Id = UserAchievements.AchievementDefinitionId
                      LIMIT 1
                  )
                  WHERE UserGameProgressId = ?
                    AND EXISTS (
                        SELECT 1
                        FROM AchievementDefinitions source
                        INNER JOIN AchievementDefinitions target
                          ON target.GameId = ?
                         AND target.ApiName = source.ApiName
                        WHERE source.Id = UserAchievements.AchievementDefinitionId
                    );",
                targetGameId,
                progressId,
                targetGameId);
        }

        private static void CopyMatchingFriendAchievements(
            SQLiteDatabase db,
            long sourceProgressId,
            long targetProgressId,
            long targetGameId,
            string nowIso)
        {
            db.ExecuteNonQuery(
                @"INSERT OR IGNORE INTO UserAchievements
                    (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                  SELECT
                    ?,
                    target.Id,
                    ua.Unlocked,
                    ua.UnlockTimeUtc,
                    ua.ProgressNum,
                    ua.ProgressDenom,
                    ua.LastUpdatedUtc,
                    ?
                  FROM UserAchievements ua
                  INNER JOIN AchievementDefinitions source
                    ON source.Id = ua.AchievementDefinitionId
                  INNER JOIN AchievementDefinitions target
                    ON target.GameId = ?
                   AND target.ApiName = source.ApiName
                  WHERE ua.UserGameProgressId = ?;",
                targetProgressId,
                nowIso,
                targetGameId,
                sourceProgressId);
        }

        private static void DeleteProviderOnlyGameIfOrphaned(
            SQLiteDatabase db,
            long sourceGameId)
        {
            db.ExecuteNonQuery(
                @"DELETE FROM AchievementDefinitions
                  WHERE GameId = ?
                    AND NOT EXISTS (
                        SELECT 1
                        FROM UserAchievements ua
                        WHERE ua.AchievementDefinitionId = AchievementDefinitions.Id
                    );",
                sourceGameId);

            db.ExecuteNonQuery(
                @"DELETE FROM Games
                  WHERE Id = ?
                    AND NOT EXISTS (
                        SELECT 1
                        FROM FriendOwnership fo
                        WHERE fo.GameId = Games.Id
                    )
                    AND NOT EXISTS (
                        SELECT 1
                        FROM UserGameProgress ugp
                        WHERE ugp.GameId = Games.Id
                    );",
                sourceGameId);
        }

        public FriendCacheWriteResult SaveFriendGameAchievements(
            string providerKey,
            string externalUserId,
            string providerGameKey,
            int appId,
            FriendGameAchievements achievements)
        {
            providerKey = NormalizeProviderKey(providerKey);
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (string.IsNullOrWhiteSpace(providerGameKey) && !string.IsNullOrWhiteSpace(achievements?.ProviderGameKey))
            {
                providerGameKey = NormalizeProviderGameKey(achievements.ProviderGameKey);
            }

            if (string.IsNullOrWhiteSpace(externalUserId) || !HasProviderGameIdentity(appId, providerGameKey))
            {
                return FriendCacheWriteResult.Failed("Friend id or app id is missing.");
            }

            var nowIso = ToIso(DateTime.UtcNow);
            var updatedIso = ToIso(achievements?.LastUpdatedUtc == default(DateTime)
                ? DateTime.UtcNow
                : achievements.LastUpdatedUtc);

            try
            {
                var renamedApiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Guid? renamedPlayniteGameId = null;
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        var user = LoadFriendUser(db, providerKey, externalUserId);
                        // Exophase keys resolve through the single-row invariant (healing any
                        // duplicates) without touching the mapping flag; other providers keep the
                        // existing lookup.
                        var game = IsExophaseProvider(providerKey) && !string.IsNullOrWhiteSpace(providerGameKey)
                            ? ResolveSingleExophaseFriendGameRow(
                                db,
                                providerKey,
                                appId,
                                providerGameKey,
                                null,
                                null,
                                allowCreate: false,
                                applyOwnershipMapping: false,
                                null,
                                nowIso)
                            : LoadAnyGameByAppId(db, providerKey, appId, providerGameKey);
                        if (user == null || game == null)
                        {
                            return;
                        }

                        renamedPlayniteGameId = ParseGuid(game.PlayniteGameId);

                        var status = ResolveFriendScrapeStatus(achievements);
                        var detail = achievements?.DetailCode.ToString();

                        UpdateFriendOwnershipScrapeState(db, user.Id, game.Id, status, detail, updatedIso, nowIso);

                        if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        var incomingRows = achievements?.Rows ?? new List<FriendAchievementRow>();

                        var definitions = LoadAchievementDefinitionsForGame(db, game.Id);
                        var refreshedFromMappedCurrentUser = TryRefreshMappedCurrentUserDefinitionsForProxyGame(
                            db,
                            game,
                            nowIso,
                            updatedIso,
                            renamedApiNames);
                        if (refreshedFromMappedCurrentUser)
                        {
                            definitions = LoadAchievementDefinitionsForGame(db, game.Id);
                        }

                        var seededDefinitions = SqlNadoCacheBehavior.BuildDefinitionsFromFriendRows(incomingRows);
                        if (!refreshedFromMappedCurrentUser &&
                            ShouldRefreshDefinitionsFromFriendRows(definitions, seededDefinitions))
                        {
                            // No cached definitions for this game (e.g. a shared-library friend's game that
                            // was never scraped for definitions and that the current user does not own under
                            // this provider), or an older friend-seeded definition set with the same keys.
                            // Seed/refresh from this scrape only when no mapped current-user schema won;
                            // native definitions stay canonical for shared/mapped games.
                            UpsertAchievementDefinitions(db, game.Id, seededDefinitions, nowIso, updatedIso, renamedApiNames);
                            definitions = LoadAchievementDefinitionsForGame(db, game.Id);
                        }

                        // Stable-keyed unlock rows carry no display text and can only match definitions by
                        // key. Defer only while definitions are missing or still carry legacy display-derived
                        // Exophase keys (migration-pending); definitions keyed by another provider's scheme
                        // (mapped games serviced by Steam/PSN/...) proceed and match via the native-key bridge.
                        if (SqlNadoCacheBehavior.RowsRequireStableKeyedDefinitions(incomingRows) &&
                            (definitions.Count == 0 ||
                             SqlNadoCacheBehavior.HasLegacyExophaseKeyedDefinitions(definitions.Select(def => def?.ApiName))))
                        {
                            _logger?.Info($"Friend achievements for provider={providerKey}, game={ToProviderGameLogKey(appId, providerGameKey)} " +
                                "deferred: definitions are missing or still legacy-keyed; awaiting definition migration.");
                            UpdateFriendOwnershipScrapeState(db, user.Id, game.Id, "transient", "awaiting-definition-migration", updatedIso, nowIso);
                            return;
                        }

                        var totalAchievements = definitions.Count;
                        var matchedRows = MapFriendRowsToDefinitions(definitions, incomingRows);

                        // A nonzero unlock list that matches no definition at all is a matching failure
                        // (mismatched key schemes, stale definitions), never a real state; persisting it
                        // would write the collapsed unlock counts this pipeline exists to prevent. Record
                        // a transient state so the candidate retries after definitions heal.
                        if (matchedRows.Count == 0 && incomingRows.Count > 0 && definitions.Count > 0)
                        {
                            _logger?.Warn($"Friend achievements for provider={providerKey}, game={ToProviderGameLogKey(appId, providerGameKey)} " +
                                $"deferred: none of {incomingRows.Count} unlock row(s) matched {definitions.Count} cached definition(s).");
                            UpdateFriendOwnershipScrapeState(db, user.Id, game.Id, "transient", "no-definition-match", updatedIso, nowIso);
                            return;
                        }

                        var cacheKey = BuildFriendCacheKey(providerKey, externalUserId, game);
                        var existingProgress = LoadUserGameProgress(db, user.Id, game.Id, cacheKey);
                        var userProgressId = UpsertUserGameProgress(
                            db,
                            existingProgress,
                            user.Id,
                            game.Id,
                            cacheKey,
                            totalAchievements > 0,
                            Math.Max(0, matchedRows.Count(match => match?.Row?.Unlocked == true)),
                            totalAchievements,
                            updatedIso,
                            nowIso);

                        UpsertFriendAchievementRows(db, userProgressId, matchedRows, updatedIso, nowIso);

                        var unlockedCount = db.ExecuteScalar<long>(
                            @"SELECT COUNT(*)
                              FROM UserAchievements
                              WHERE UserGameProgressId = ?
                                AND Unlocked = 1;",
                            new object[] { userProgressId });

                        db.ExecuteNonQuery(
                            @"UPDATE UserGameProgress
                              SET AchievementsUnlocked = ?,
                                  TotalAchievements = ?,
                                  HasAchievements = ?,
                                  LastUpdatedUtc = ?,
                                  UpdatedUtc = ?
                              WHERE Id = ?;",
                            Math.Max(0, unlockedCount),
                            totalAchievements,
                            totalAchievements > 0 ? 1 : 0,
                            updatedIso,
                            nowIso,
                            userProgressId);
                    });
                });

                var result = FriendCacheWriteResult.Ok();
                if (renamedApiNames.Count > 0)
                {
                    result.RenamedApiNames = renamedApiNames;
                    result.RenamedPlayniteGameId = renamedPlayniteGameId;
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to save friend achievements for provider={providerKey}, friend={externalUserId}, game={ToProviderGameLogKey(appId, providerGameKey)}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        public List<FriendAchievementRow> LoadFriendGameAchievements(
            string providerKey,
            string externalUserId,
            int appId,
            string providerGameKey)
        {
            providerKey = NormalizeProviderKey(providerKey);
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (string.IsNullOrWhiteSpace(externalUserId) || !HasProviderGameIdentity(appId, providerGameKey))
            {
                return new List<FriendAchievementRow>();
            }

            var clauses = new List<string>();
            var args = new List<object>
            {
                providerKey,
                externalUserId.Trim(),
                providerKey
            };
            if (appId > 0)
            {
                clauses.Add("g.ProviderGameId = ?");
                args.Add(appId);
            }

            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                clauses.Add("g.ProviderGameKey = ?");
                args.Add(providerGameKey);
            }

            if (clauses.Count == 0)
            {
                return new List<FriendAchievementRow>();
            }

            return WithDb(db =>
            {
                var rows = db.Load<FriendRecentUnlockRow>(
                    @"SELECT
                        g.ProviderKey AS ProviderKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        u.ExternalUserId AS FriendExternalUserId,
                        u.DisplayName AS FriendName,
                        u.AvatarUrl AS FriendAvatarUrl,
                        u.AvatarPath AS FriendAvatarPath,
                        ad.ApiName AS ApiName,
                        ad.DisplayName AS DisplayName,
                        ad.Description AS Description,
                        ad.UnlockedIconPath AS UnlockedIconPath,
                        ad.LockedIconPath AS LockedIconPath,
                        ad.Points AS Points,
                        ad.ScaledPoints AS ScaledPoints,
                        ad.Category AS Category,
                        ad.CategoryType AS CategoryType,
                        ad.TrophyType AS TrophyType,
                        ad.Hidden AS Hidden,
                        ad.IsCapstone AS IsCapstone,
                        ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                        ad.Rarity AS Rarity,
                        ua.UnlockTimeUtc AS UnlockTimeUtc,
                        ua.Unlocked AS Unlocked,
                        ua.ProgressNum AS ProgressNum,
                        ua.ProgressDenom AS ProgressDenom,
                        g.IconPath AS IconPath,
                        g.CoverPath AS CoverPath
                      FROM Users u
                      INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id
                      INNER JOIN Games g ON g.Id = ugp.GameId
                      INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id
                      INNER JOIN AchievementDefinitions ad ON ad.Id = ua.AchievementDefinitionId
                      WHERE u.ProviderKey = ?
                        AND u.IsCurrentUser = 0
                        AND u.ExternalUserId = ?
                        AND g.ProviderKey = ?
                        AND (" + string.Join(" OR ", clauses) + @")
                      ORDER BY ad.Id;",
                    args.ToArray()).ToList();

                return rows
                    .Where(row => row != null)
                    .Select(row => new FriendAchievementRow
                    {
                        ApiName = row.ApiName,
                        DisplayName = row.DisplayName,
                        Description = row.Description,
                        IconUrl = MakeAbsolutePath(row.UnlockedIconPath),
                        UnlockedIconUrl = MakeAbsolutePath(row.UnlockedIconPath),
                        LockedIconUrl = MakeAbsolutePath(row.LockedIconPath),
                        Points = row.Points,
                        ScaledPoints = row.ScaledPoints,
                        Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                        CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(row.CategoryType),
                        TrophyType = row.TrophyType,
                        Hidden = row.Hidden != 0,
                        IsCapstone = row.IsCapstone != 0,
                        GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                        Rarity = ParseStoredRarity(row.Rarity),
                        Unlocked = row.Unlocked.GetValueOrDefault() != 0,
                        UnlockTimeUtc = ParseUtc(row.UnlockTimeUtc),
                        ProgressNum = row.ProgressNum,
                        ProgressDenom = row.ProgressDenom
                    })
                    .ToList();
            });
        }

        public List<FriendRefreshCandidate> LoadFriendRefreshCandidates(
            string providerKey,
            FriendRefreshOptions options)
        {
            providerKey = NormalizeProviderKey(providerKey);
            options = options?.Clone() ?? new FriendRefreshOptions();

            return WithDb(db =>
            {
                if (options.ProviderGameKeys?.Count > 0)
                {
                    return LoadProviderGameKeyFriendRefreshCandidates(db, providerKey, options);
                }

                if (options.ProviderAppIds?.Count > 0)
                {
                    return LoadProviderAppFriendRefreshCandidates(db, providerKey, options);
                }

                // Selected-game / explicit game-id targets refresh a specific current-user library
                // game across friends, so they source from the current user's library rows
                // (cross-joined with friends) rather than the friend's own ownership.
                if (options.Scope == FriendRefreshScope.SelectedGame ||
                    (options.Scope == FriendRefreshScope.Custom && options.PlayniteGameIds?.Count > 0))
                {
                    var currentUserCandidates = LoadCurrentUserFriendRefreshCandidates(db, providerKey, options);
                    if (ShouldUseMappedFriendOwnershipCandidates(providerKey, options))
                    {
                        currentUserCandidates.AddRange(LoadSharedFriendRefreshCandidates(
                            db,
                            providerKey,
                            includeProviderOnly: false,
                            providerOnlyOnly: false,
                            options));
                    }

                    return DeduplicateFriendRefreshCandidates(currentUserCandidates);
                }

                // Full / Shared / Installed / Recent all source from the friend's actual ownership.
                // They differ only by the my-library intersection: Full includes provider-only games
                // (everything the friend owns), while Shared / Installed / Recent require a
                // Playnite-mapped game. Installed additionally filters to the installed-library ids
                // carried on the options; Recent restricts to recent friend playtime.
                return LoadSharedFriendRefreshCandidates(
                    db,
                    providerKey,
                    includeProviderOnly: FriendRefreshPolicy.DiscoversProviderOnlyGames(options.Scope),
                    providerOnlyOnly: false,
                    options);
            });
        }

        public IReadOnlyDictionary<string, FriendOwnershipRecency> LoadFriendOwnershipRecency(
            string providerKey,
            string externalUserId)
        {
            providerKey = NormalizeProviderKey(providerKey);
            var result = new Dictionary<string, FriendOwnershipRecency>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                return result;
            }

            var normalizedExternalUserId = externalUserId.Trim();
            return WithDb(db =>
            {
                var rows = db.Load<FriendOwnershipRecencyRow>(
                    @"SELECT
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        fo.PlaytimeForeverMinutes AS PlaytimeForeverMinutes,
                        fo.LastPlayedUtc AS LastPlayedUtc,
                        fo.LastScrapedUtc AS LastScrapedUtc,
                        fo.LastScrapeStatus AS LastScrapeStatus
                      FROM FriendOwnership fo
                      INNER JOIN Users u ON u.Id = fo.UserId
                      INNER JOIN Games g ON g.Id = fo.GameId
                      WHERE u.ProviderKey = ?
                        AND u.IsCurrentUser = 0
                        AND u.ExternalUserId = ?
                        AND g.ProviderKey = ?;",
                    providerKey,
                    normalizedExternalUserId,
                    providerKey);

                foreach (var row in rows)
                {
                    var key = BuildProviderGameRecencyKey((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey);
                    if (string.IsNullOrEmpty(key))
                    {
                        continue;
                    }

                    result[key] = new FriendOwnershipRecency
                    {
                        PlaytimeForeverMinutes = Math.Max(0, row.PlaytimeForeverMinutes),
                        LastPlayedUtc = ParseUtc(row.LastPlayedUtc),
                        LastScrapedUtc = ParseUtc(row.LastScrapedUtc),
                        LastScrapeStatus = row.LastScrapeStatus
                    };
                }

                return (IReadOnlyDictionary<string, FriendOwnershipRecency>)result;
            });
        }

        // Matches RefreshRuntime.GetProviderGameCacheKey so the recency snapshot dictionary keys align
        // with the keys the runtime builds from freshly-fetched ownership items and refresh candidates.
        private static string BuildProviderGameRecencyKey(int appId, string providerGameKey)
        {
            return !string.IsNullOrWhiteSpace(providerGameKey)
                ? providerGameKey.Trim()
                : (appId > 0 ? appId.ToString(CultureInfo.InvariantCulture) : null);
        }

        public FriendCacheWriteResult DeleteFriendData(string providerKey, string externalUserId, bool preserveFriendRecord = false)
        {
            providerKey = NormalizeProviderKey(providerKey);
            if (string.IsNullOrWhiteSpace(externalUserId))
            {
                return FriendCacheWriteResult.Failed("Friend id is missing.");
            }

            var normalizedExternalUserId = externalUserId.Trim();

            try
            {
                var deletedUsers = 0;
                WithDb(db =>
                {
                    db.RunTransaction(() =>
                    {
                        var users = db.Load<UserRow>(
                            @"SELECT Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, AvatarUrl, LastRefreshedUtc, IsActiveFriend, CreatedUtc, UpdatedUtc
                              FROM Users
                              WHERE ProviderKey = ?
                                AND ExternalUserId = ?
                                AND IsCurrentUser = 0;",
                            providerKey,
                            normalizedExternalUserId).ToList();

                        foreach (var user in users)
                        {
                            db.ExecuteNonQuery(
                                @"DELETE FROM UserAchievements
                                  WHERE UserGameProgressId IN (
                                      SELECT Id FROM UserGameProgress WHERE UserId = ?
                                  );",
                                user.Id);
                            db.ExecuteNonQuery("DELETE FROM UserGameProgress WHERE UserId = ?;", user.Id);
                            db.ExecuteNonQuery("DELETE FROM FriendOwnership WHERE UserId = ?;", user.Id);
                            if (!preserveFriendRecord)
                            {
                                // Removing the friend reference entirely (settings-side Remove / Ignore).
                                // "Clear Friend" preserves this row so the friend stays registered.
                                db.ExecuteNonQuery("DELETE FROM Users WHERE Id = ? AND IsCurrentUser = 0;", user.Id);
                            }
                            deletedUsers++;
                        }
                    });
                });

                return FriendCacheWriteResult.Ok(
                    incomingCount: 1,
                    writtenCount: deletedUsers,
                    skippedCount: deletedUsers > 0 ? 0 : 1);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to delete friend data for provider={providerKey}, friend={normalizedExternalUserId}.");
                return FriendCacheWriteResult.Failed(ex.Message);
            }
        }

        private static List<FriendRefreshCandidate> LoadSharedFriendRefreshCandidates(
            SQLiteDatabase db,
            string providerKey,
            bool includeProviderOnly,
            bool providerOnlyOnly,
            FriendRefreshOptions options)
        {
            var sql = new StringBuilder(FriendRefreshCandidateSelectFromSql);
            sql.Append(
                    @"
                        AND (
                            (g.ProviderGameId IS NOT NULL AND g.ProviderGameId > 0)
                            OR (g.ProviderGameKey IS NOT NULL AND TRIM(g.ProviderGameKey) <> '')
                        )");

            var args = new List<object> { providerKey, providerKey };

            if (providerOnlyOnly)
            {
                sql.Append(" AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')");
            }
            else if (!includeProviderOnly)
            {
                sql.Append(" AND g.PlayniteGameId IS NOT NULL AND TRIM(g.PlayniteGameId) <> ''");
            }

            AppendInClause(sql, args, "u.ExternalUserId", NormalizeFriendFilterIds(options?.FriendExternalUserIds));
            AppendInClause(sql, args, "g.PlayniteGameId", NormalizeGameFilterIds(options?.PlayniteGameIds));

            if (options?.Scope == FriendRefreshScope.Recent)
            {
                sql.Append(
                    @" AND (
                        fo.LastScrapedUtc IS NULL
                        OR TRIM(fo.LastScrapedUtc) = ''
                        OR fo.LastScrapeStatus IS NULL
                        OR LOWER(fo.LastScrapeStatus) <> 'ok'
                        OR (
                            fo.LastOwnershipRefreshUtc IS NOT NULL
                            AND TRIM(fo.LastOwnershipRefreshUtc) <> ''
                            AND fo.LastOwnershipRefreshUtc > fo.LastScrapedUtc
                        )
                    )");
            }

            sql.Append(FriendRefreshCandidateOrderBySql);

            return MapFriendRefreshCandidates(
                db.Load<FriendRefreshCandidateRow>(sql.ToString(), args.ToArray()).ToList());
        }

        private static List<FriendRefreshCandidate> LoadProviderAppFriendRefreshCandidates(
            SQLiteDatabase db,
            string providerKey,
            FriendRefreshOptions options)
        {
            var appIds = options?.ProviderAppIds?
                .Where(id => id > 0)
                .Distinct()
                .ToList() ?? new List<int>();
            if (appIds.Count == 0)
            {
                return new List<FriendRefreshCandidate>();
            }

            var sql = new StringBuilder(FriendRefreshCandidateSelectFromSql);
            sql.Append(
                    @"
                        AND g.ProviderGameId IS NOT NULL
                        AND g.ProviderGameId > 0");

            var args = new List<object> { providerKey, providerKey };

            AppendInClause(sql, args, "g.ProviderGameId", appIds);
            AppendInClause(sql, args, "u.ExternalUserId", NormalizeFriendFilterIds(options?.FriendExternalUserIds));

            sql.Append(FriendRefreshCandidateOrderBySql);

            return MapFriendRefreshCandidates(
                db.Load<FriendRefreshCandidateRow>(sql.ToString(), args.ToArray()).ToList());
        }

        private static List<FriendRefreshCandidate> LoadProviderGameKeyFriendRefreshCandidates(
            SQLiteDatabase db,
            string providerKey,
            FriendRefreshOptions options)
        {
            var providerGameKeys = options?.ProviderGameKeys?
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => key.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
            if (providerGameKeys.Count == 0)
            {
                return new List<FriendRefreshCandidate>();
            }

            var sql = new StringBuilder(FriendRefreshCandidateSelectFromSql);

            var args = new List<object> { providerKey, providerKey };

            AppendInClause(sql, args, "g.ProviderGameKey", providerGameKeys);
            AppendInClause(sql, args, "u.ExternalUserId", NormalizeFriendFilterIds(options?.FriendExternalUserIds));

            sql.Append(FriendRefreshCandidateOrderBySql);

            return MapFriendRefreshCandidates(
                db.Load<FriendRefreshCandidateRow>(sql.ToString(), args.ToArray()).ToList());
        }

        private static bool ShouldUseMappedFriendOwnershipCandidates(string providerKey, FriendRefreshOptions options)
        {
            // Exophase maps friend games to the library by name, so supplement the current-user
            // cross-join (used by the selected-game / explicit game-id paths) with the friend's
            // actual mapped ownership rows.
            return string.Equals(providerKey, "Exophase", StringComparison.OrdinalIgnoreCase) &&
                   (options?.Scope == FriendRefreshScope.SelectedGame ||
                    (options?.Scope == FriendRefreshScope.Custom && options.PlayniteGameIds?.Count > 0));
        }

        private static List<FriendRefreshCandidate> DeduplicateFriendRefreshCandidates(
            IEnumerable<FriendRefreshCandidate> candidates)
        {
            return (candidates ?? Enumerable.Empty<FriendRefreshCandidate>())
                .Where(candidate => candidate?.Friend != null)
                .GroupBy(BuildFriendRefreshCandidateKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        private static string BuildFriendRefreshCandidateKey(FriendRefreshCandidate candidate)
        {
            var friendId = candidate?.Friend?.ExternalUserId?.Trim() ?? string.Empty;
            var gameKey = !string.IsNullOrWhiteSpace(candidate?.ProviderGameKey)
                ? "key:" + candidate.ProviderGameKey.Trim()
                : (candidate?.AppId > 0
                    ? "app:" + candidate.AppId.ToString(CultureInfo.InvariantCulture)
                    : "playnite:" + (candidate?.PlayniteGameId?.ToString() ?? string.Empty));
            return friendId + "\u001f" + gameKey;
        }

        private static List<string> NormalizeFriendFilterIds(IReadOnlyCollection<string> friendExternalUserIds)
        {
            return friendExternalUserIds?
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        private static List<string> NormalizeGameFilterIds(IReadOnlyCollection<Guid> playniteGameIds)
        {
            return playniteGameIds?
                .Where(id => id != Guid.Empty)
                .Select(id => id.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();
        }

        /// <summary>
        /// Appends " AND column IN (?, ...)" with one positional placeholder per value and adds the
        /// values to <paramref name="args"/> in the same order. No-op for a null or empty collection.
        /// </summary>
        private static void AppendInClause<T>(
            StringBuilder sql,
            List<object> args,
            string column,
            IReadOnlyCollection<T> values)
        {
            if (values == null || values.Count == 0)
            {
                return;
            }

            sql.Append(" AND ").Append(column).Append(" IN (");
            sql.Append(string.Join(",", values.Select(_ => "?")));
            sql.Append(')');
            args.AddRange(values.Cast<object>());
        }

        private static List<FriendRefreshCandidate> LoadCurrentUserFriendRefreshCandidates(
            SQLiteDatabase db,
            string providerKey,
            FriendRefreshOptions options)
        {
            var ids = options.PlayniteGameIds?
                .Where(id => id != Guid.Empty)
                .Select(id => id.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            var sql = new StringBuilder(
                @"WITH CurrentGames AS (
                    SELECT DISTINCT
                        g.Id AS Id,
                        g.ProviderKey AS ProviderKey,
                        g.ProviderPlatformKey AS ProviderPlatformKey,
                        g.ProviderGameId AS ProviderGameId,
                        g.ProviderGameKey AS ProviderGameKey,
                        g.PlayniteGameId AS PlayniteGameId,
                        g.GameName AS GameName,
                        g.LibrarySourceName AS LibrarySourceName,
                        g.FirstSeenUtc AS FirstSeenUtc,
                        g.LastUpdatedUtc AS LastUpdatedUtc
                    FROM Games g
                    INNER JOIN UserGameProgress ugp ON ugp.GameId = g.Id
                    INNER JOIN Users cu ON cu.Id = ugp.UserId
                    WHERE cu.ProviderKey = ?
                      AND cu.IsCurrentUser = 1
                      AND g.ProviderKey = ?
                      AND (
                          (g.ProviderGameId IS NOT NULL AND g.ProviderGameId > 0)
                          OR (g.ProviderGameKey IS NOT NULL AND TRIM(g.ProviderGameKey) <> '')
                      )
                      AND ugp.HasAchievements != 0
                      AND g.PlayniteGameId IS NOT NULL
                      AND TRIM(g.PlayniteGameId) <> ''");

            var args = new List<object> { providerKey, providerKey };
            AppendInClause(sql, args, "g.PlayniteGameId", ids);

            sql.Append(
                @")
                  SELECT
                    u.ProviderKey AS ProviderKey,
                    u.ExternalUserId AS ExternalUserId,
                    u.DisplayName AS DisplayName,
                    u.AvatarUrl AS AvatarUrl,
                    u.LastRefreshedUtc AS LastRefreshedUtc,
                    g.ProviderGameId AS ProviderGameId,
                    g.ProviderGameKey AS ProviderGameKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    COALESCE(fo.PlaytimeForeverMinutes, 0) AS PlaytimeForeverMinutes,
                    fo.LastPlayedUtc AS LastPlayedUtc,
                    fo.LastOwnershipRefreshUtc AS LastOwnershipRefreshUtc,
                    fo.LastScrapedUtc AS LastScrapedUtc,
                    fo.LastScrapeStatus AS LastScrapeStatus
                  FROM Users u
                  CROSS JOIN CurrentGames g
                  LEFT JOIN FriendOwnership fo ON fo.UserId = u.Id AND fo.GameId = g.Id
                  WHERE u.ProviderKey = ?
                    AND " + ActiveFriendPredicateSql);

            args.Add(providerKey);

            AppendInClause(sql, args, "u.ExternalUserId", NormalizeFriendFilterIds(options.FriendExternalUserIds));

            sql.Append(" ORDER BY COALESCE(fo.LastPlayedUtc, '') DESC, COALESCE(fo.PlaytimeForeverMinutes, 0) DESC, u.DisplayName, g.GameName;");

            return MapFriendRefreshCandidates(db.Load<FriendRefreshCandidateRow>(sql.ToString(), args.ToArray()).ToList());
        }

        private static List<FriendRefreshCandidate> MapFriendRefreshCandidates(IEnumerable<FriendRefreshCandidateRow> rows)
        {
            return (rows ?? Enumerable.Empty<FriendRefreshCandidateRow>())
                .Where(row => row != null && HasProviderGameIdentity((int)Math.Max(0, row.ProviderGameId ?? 0), row.ProviderGameKey))
                .Select(row => new FriendRefreshCandidate
                {
                    Friend = new FriendIdentity
                    {
                        ProviderKey = row.ProviderKey,
                        ExternalUserId = row.ExternalUserId,
                        DisplayName = row.DisplayName,
                        AvatarUrl = row.AvatarUrl,
                        LastRefreshedUtc = ParseUtc(row.LastRefreshedUtc)
                    },
                    AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                    ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                    PlayniteGameId = ParseGuid(row.PlayniteGameId),
                    GameName = row.GameName,
                    PlaytimeForeverMinutes = Math.Max(0, row.PlaytimeForeverMinutes),
                    LastPlayedUtc = ParseUtc(row.LastPlayedUtc),
                    LastOwnershipRefreshUtc = ParseUtc(row.LastOwnershipRefreshUtc),
                    LastScrapedUtc = ParseUtc(row.LastScrapedUtc),
                    LastScrapeStatus = row.LastScrapeStatus
                })
                .ToList();
        }

        public FriendsOverviewData LoadFriendsOverviewData(int recentLimit)
        {
            return WithReadDb(db =>
            {
                var data = new FriendsOverviewData();

                // Shared across the games list and both achievement lists so each game's
                // presentation is resolved at most once per load.
                var presentationCache = new Dictionary<Guid, GamePresentation>();

                using (PerfScope.Start(_logger, "Friends.LoadSummaryRows", thresholdMs: 15))
                data.Friends = MapFriendSummaryRows(LoadFriendSummaryRows(db));

                using (PerfScope.Start(_logger, "Friends.LoadGameSummaryRows", thresholdMs: 15))
                data.Games = MapFriendGameSummaryRows(LoadFriendGameSummaryRows(db), presentationCache);

                using (PerfScope.Start(_logger, "Friends.LoadGameLinkRows", thresholdMs: 15))
                data.FriendGameLinks = MapFriendGameLinkRows(LoadFriendGameLinkRows(db));

                using (PerfScope.Start(_logger, "Friends.LoadAchievementRows", thresholdMs: 15))
                data.AllAchievements = MapFriendAchievementRows(LoadFriendAllAchievementRows(db), presentationCache);

                using (PerfScope.Start(_logger, "Friends.ApplySummaryScores", thresholdMs: 15))
                FriendsOverviewDerivations.Apply(data, recentLimit);
                return data;
            });
        }

        public FriendsOverviewData LoadFriendGameAchievementData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return new FriendsOverviewData();
            }

            return WithReadDb(db =>
            {
                var data = new FriendsOverviewData();
                var presentationCache = new Dictionary<Guid, GamePresentation>();

                using (PerfScope.Start(_logger, "Friends.LoadTargetGameSummaryRows", thresholdMs: 15))
                data.Games = MapFriendGameSummaryRows(
                    LoadFriendGameSummaryRows(db, playniteGameId),
                    presentationCache);

                using (PerfScope.Start(_logger, "Friends.LoadTargetGameSummaryFriends", thresholdMs: 15))
                data.Friends = MapFriendSummaryRows(LoadFriendSummaryRows(db, playniteGameId));

                using (PerfScope.Start(_logger, "Friends.LoadTargetGameLinkRows", thresholdMs: 15))
                data.FriendGameLinks = MapFriendGameLinkRows(LoadFriendGameLinkRows(db, playniteGameId));

                using (PerfScope.Start(_logger, "Friends.LoadTargetGameAchievementRows", thresholdMs: 15))
                data.AllAchievements = MapFriendAchievementRows(
                    LoadFriendAchievementRows(
                        db,
                        0,
                        requireUnlockTime: false,
                        unlockedOnly: false,
                        playniteGameId: playniteGameId),
                    presentationCache);
                using (PerfScope.Start(_logger, "Friends.ApplyTargetGameSummaryScores", thresholdMs: 15))
                FriendsOverviewDerivations.Apply(data, recentLimit: 0);
                return data;
            });
        }

        /// <summary>
        /// Loads every friend's achievement rows — LOCKED INCLUDED — for one game identified by
        /// provider scope (ProviderKey + AppId/ProviderGameKey), so provider-only friend games
        /// without a PlayniteGameId are supported. Used by the friend+game pair comparison
        /// surfaces to materialize locked rows on demand, since the overview snapshot carries
        /// unlocked rows only.
        /// </summary>
        public FriendsOverviewData LoadFriendGameAchievementData(FriendCacheChange gameScope)
        {
            if (gameScope == null ||
                string.IsNullOrWhiteSpace(gameScope.ProviderKey) ||
                (gameScope.AppId <= 0 && string.IsNullOrWhiteSpace(gameScope.ProviderGameKey)))
            {
                return new FriendsOverviewData();
            }

            return WithReadDb(db =>
            {
                var data = new FriendsOverviewData();
                var presentationCache = new Dictionary<Guid, GamePresentation>();

                using (PerfScope.Start(_logger, "Friends.LoadPairAchievementRows", thresholdMs: 15))
                data.AllAchievements = MapFriendAchievementRows(
                    LoadFriendAchievementRows(
                        db,
                        0,
                        requireUnlockTime: false,
                        unlockedOnly: false,
                        reloadScopes: new[] { gameScope }),
                    presentationCache);

                FriendsOverviewDerivations.Apply(data, recentLimit: 0);
                return data;
            });
        }

        /// <summary>
        /// Loads the data needed to patch a retained friends-overview data set: the three cheap
        /// lists reloaded in full (friend summaries, game summaries, ownership links — keeping
        /// all cross-friend aggregates authoritative from SQL) plus achievement display rows for
        /// ONLY the given scopes. The caller splices the scoped rows into its retained
        /// AllAchievements and re-runs <see cref="FriendsOverviewDerivations"/>; the derived
        /// lists are left empty here.
        /// </summary>
        public FriendsOverviewData LoadFriendsOverviewPatchData(IReadOnlyList<FriendCacheChange> reloadScopes)
        {
            return WithReadDb(db =>
            {
                var data = new FriendsOverviewData();
                var presentationCache = new Dictionary<Guid, GamePresentation>();

                using (PerfScope.Start(_logger, "Friends.LoadSummaryRows", thresholdMs: 15))
                data.Friends = MapFriendSummaryRows(LoadFriendSummaryRows(db));

                using (PerfScope.Start(_logger, "Friends.LoadGameSummaryRows", thresholdMs: 15))
                data.Games = MapFriendGameSummaryRows(LoadFriendGameSummaryRows(db), presentationCache);

                using (PerfScope.Start(_logger, "Friends.LoadGameLinkRows", thresholdMs: 15))
                data.FriendGameLinks = MapFriendGameLinkRows(LoadFriendGameLinkRows(db));

                if (reloadScopes != null && reloadScopes.Count > 0)
                {
                    // Unlocked-only to match the retained splice base (see
                    // LoadFriendAllAchievementRows); a locked patch row would otherwise
                    // reintroduce rows the base never carries.
                    using (PerfScope.Start(_logger, "Friends.LoadScopedAchievementRows", thresholdMs: 15))
                    data.AllAchievements = MapFriendAchievementRows(
                        LoadFriendAchievementRows(
                            db,
                            0,
                            requireUnlockTime: false,
                            unlockedOnly: true,
                            reloadScopes: reloadScopes),
                        presentationCache);
                }

                return data;
            });
        }

        public FriendsOverviewData LoadFriendRecentUnlocksData(int recentLimit)
        {
            return WithReadDb(db =>
            {
                var data = new FriendsOverviewData();
                var presentationCache = new Dictionary<Guid, GamePresentation>();
                var effectiveLimit = Math.Max(0, recentLimit);

                using (PerfScope.Start(_logger, "Friends.LoadRecentUnlockRows", thresholdMs: 15))
                data.RecentUnlocks = MapFriendAchievementRows(
                    LoadFriendAchievementRows(
                        db,
                        effectiveLimit,
                        requireUnlockTime: true,
                        unlockedOnly: true),
                    presentationCache);

                data.AllAchievements = data.RecentUnlocks;
                data.AllUnlockedAchievements = data.RecentUnlocks;
                data.Friends = BuildFriendSummariesFromAchievements(data.RecentUnlocks);
                FriendsOverviewDerivations.ApplyFriendSummaryScores(data.Friends, data.AllUnlockedAchievements);
                return data;
            });
        }

        private static string GetFriendSource(string providerKey) =>
            string.IsNullOrWhiteSpace(providerKey) ? "Friends" : providerKey.Trim() + "Friends";

        // Prefer the underlying platform key (e.g. Steam) over the aggregator key (e.g. Exophase) so
        // friend game summaries render the display-provider icon, mirroring
        // OverviewDataBuilder.ResolveEffectiveProviderKey.
        private static string ResolveDisplayProviderKey(string providerKey, string providerPlatformKey)
        {
            var resolved = !string.IsNullOrWhiteSpace(providerPlatformKey)
                ? providerPlatformKey
                : providerKey;
            return string.IsNullOrWhiteSpace(resolved) ? "Unknown" : resolved.Trim();
        }

        private string ResolveFriendGameIconPath(GamePresentation presentation, string cachedIconPath)
        {
            return presentation?.IconPath ?? MakeAbsolutePath(cachedIconPath);
        }

        private string ResolveFriendGameCoverPath(GamePresentation presentation, string cachedCoverPath)
        {
            return presentation?.CoverPath ?? MakeAbsolutePath(cachedCoverPath);
        }

        private static string BuildFriendCacheKey(string providerKey, string externalUserId, GameRow game)
        {
            var gamePart = !string.IsNullOrWhiteSpace(game?.PlayniteGameId)
                ? game.PlayniteGameId.Trim()
                : (!string.IsNullOrWhiteSpace(game?.ProviderGameKey)
                    ? game.ProviderGameKey.Trim()
                    : (game?.ProviderGameId?.ToString(CultureInfo.InvariantCulture) ?? "unknown"));
            return $"friend:{providerKey}:{externalUserId}:{gamePart}";
        }

        internal static string NormalizeProviderGameKey(string providerGameKey)
        {
            return string.IsNullOrWhiteSpace(providerGameKey)
                ? null
                : providerGameKey.Trim();
        }

        private static bool IsExophaseProvider(string providerKey)
        {
            return string.Equals(providerKey?.Trim(), "Exophase", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasProviderGameIdentity(int appId, string providerGameKey)
        {
            return appId > 0 || !string.IsNullOrWhiteSpace(providerGameKey);
        }

        private static string ToProviderGameCacheKey(int appId, string providerGameKey)
        {
            var normalized = NormalizeProviderGameKey(providerGameKey);
            return !string.IsNullOrWhiteSpace(normalized)
                ? normalized
                : (appId > 0 ? appId.ToString(CultureInfo.InvariantCulture) : null);
        }

        private static string ToProviderGameLogKey(int appId, string providerGameKey)
        {
            return ToProviderGameCacheKey(appId, providerGameKey) ?? "unknown";
        }

        private long UpsertFriendUser(
            SQLiteDatabase db,
            string providerKey,
            string friendSource,
            FriendIdentity friend,
            string nowIso)
        {
            var externalUserId = friend.ExternalUserId.Trim();
            var displayName = string.IsNullOrWhiteSpace(friend.DisplayName)
                ? externalUserId
                : friend.DisplayName.Trim();
            var lastRefreshedIso = ToIso(friend.LastRefreshedUtc ?? DateTime.UtcNow);

            var avatarPath = MakeRelativePath(friend.AvatarPath);

            db.ExecuteNonQuery(
                @"INSERT OR IGNORE INTO Users
                    (ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, AvatarUrl, AvatarPath, LastRefreshedUtc, IsActiveFriend, CreatedUtc, UpdatedUtc)
                  VALUES
                    (?, ?, ?, 0, ?, ?, ?, ?, 1, ?, ?);",
                providerKey,
                externalUserId,
                DbValue(displayName),
                DbValue(friendSource),
                DbValue(friend.AvatarUrl),
                DbValue(avatarPath),
                lastRefreshedIso,
                nowIso,
                nowIso);

            var userId = db.ExecuteScalar<long>(
                @"SELECT Id
                  FROM Users
                  WHERE ProviderKey = ?
                    AND ExternalUserId = ?
                  LIMIT 1;",
                providerKey,
                externalUserId);

            if (userId <= 0)
            {
                return 0;
            }

            db.ExecuteNonQuery(
                @"UPDATE Users
                  SET DisplayName = ?,
                      FriendSource = ?,
                      AvatarUrl = ?,
                      AvatarPath = COALESCE(?, AvatarPath),
                      LastRefreshedUtc = ?,
                      IsActiveFriend = CASE WHEN IsCurrentUser = 1 THEN IsActiveFriend ELSE 1 END,
                      UpdatedUtc = ?
                  WHERE Id = ?;",
                DbValue(displayName),
                DbValue(friendSource),
                DbValue(friend.AvatarUrl),
                DbValue(avatarPath),
                lastRefreshedIso,
                nowIso,
                userId);

            return userId;
        }

        private static void MarkMissingFriendsInactive(
            SQLiteDatabase db,
            string providerKey,
            string friendSource,
            HashSet<string> seenExternalUserIds,
            string nowIso)
        {
            if (seenExternalUserIds == null || seenExternalUserIds.Count == 0)
            {
                // An empty roster may be a soft-empty/transient provider result. Keep the prior roster
                // until a non-empty authoritative snapshot can prove which friends are gone.
                return;
            }

            var placeholders = string.Join(",", seenExternalUserIds.Select(_ => "?"));
            var args = new List<object> { nowIso, providerKey, friendSource };
            args.AddRange(seenExternalUserIds.Cast<object>());
            db.ExecuteNonQuery(
                @"UPDATE Users
                  SET IsActiveFriend = 0,
                      UpdatedUtc = ?
                  WHERE ProviderKey = ?
                    AND IsCurrentUser = 0
                    AND FriendSource = ?
                    AND ExternalUserId NOT IN (" + placeholders + ");",
                args.ToArray());
        }

        private static UserRow LoadFriendUser(SQLiteDatabase db, string providerKey, string externalUserId)
        {
            return db.Load<UserRow>(
                @"SELECT Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, AvatarUrl, LastRefreshedUtc, IsActiveFriend, CreatedUtc, UpdatedUtc
                  FROM Users
                  WHERE ProviderKey = ?
                    AND ExternalUserId = ?
                    AND IsCurrentUser = 0
                    AND IsActiveFriend = 1
                  LIMIT 1;",
                providerKey,
                externalUserId.Trim()).FirstOrDefault();
        }

        private static GameRow LoadSharedGameByAppId(SQLiteDatabase db, string providerKey, int appId, string providerGameKey)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                return db.Load<GameRow>(
                    @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                      FROM Games
                      WHERE ProviderKey = ?
                        AND ProviderGameKey = ?
                        AND PlayniteGameId IS NOT NULL
                        AND TRIM(PlayniteGameId) <> ''
                      ORDER BY LastUpdatedUtc DESC, Id DESC
                      LIMIT 1;",
                    providerKey,
                    providerGameKey).FirstOrDefault();
            }

            return db.Load<GameRow>(
                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                  FROM Games
                  WHERE ProviderKey = ?
                    AND ProviderGameId = ?
                    AND PlayniteGameId IS NOT NULL
                    AND TRIM(PlayniteGameId) <> ''
                  ORDER BY LastUpdatedUtc DESC, Id DESC
                  LIMIT 1;",
                providerKey,
                appId).FirstOrDefault();
        }

        private static GameRow LoadAnyGameByAppId(SQLiteDatabase db, string providerKey, int appId, string providerGameKey = null)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                return db.Load<GameRow>(
                    @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                      FROM Games
                      WHERE ProviderKey = ?
                        AND ProviderGameKey = ?
                      ORDER BY CASE WHEN PlayniteGameId IS NOT NULL AND TRIM(PlayniteGameId) <> '' THEN 0 ELSE 1 END,
                               LastUpdatedUtc DESC,
                               Id DESC
                      LIMIT 1;",
                    providerKey,
                    providerGameKey).FirstOrDefault();
            }

            return db.Load<GameRow>(
                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                  FROM Games
                  WHERE ProviderKey = ?
                    AND ProviderGameId = ?
                  ORDER BY CASE WHEN PlayniteGameId IS NOT NULL AND TRIM(PlayniteGameId) <> '' THEN 0 ELSE 1 END,
                           LastUpdatedUtc DESC,
                           Id DESC
                  LIMIT 1;",
                providerKey,
                appId).FirstOrDefault();
        }

        private static GameRow LoadGameByPlayniteId(SQLiteDatabase db, string providerKey, Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return null;
            }

            return db.Load<GameRow>(
                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                  FROM Games
                  WHERE ProviderKey = ?
                    AND PlayniteGameId = ?
                  ORDER BY LastUpdatedUtc DESC, Id DESC
                  LIMIT 1;",
                providerKey,
                playniteGameId.ToString()).FirstOrDefault();
        }

        private static GameRow LoadProviderOnlyGameByAppId(SQLiteDatabase db, string providerKey, int appId, string providerGameKey = null)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                return db.Load<GameRow>(
                    @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                      FROM Games
                      WHERE ProviderKey = ?
                        AND ProviderGameKey = ?
                        AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '')
                      ORDER BY LastUpdatedUtc DESC, Id DESC
                      LIMIT 1;",
                    providerKey,
                    providerGameKey).FirstOrDefault();
            }

            return db.Load<GameRow>(
                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                  FROM Games
                  WHERE ProviderKey = ?
                    AND ProviderGameId = ?
                    AND (PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = '')
                  ORDER BY LastUpdatedUtc DESC, Id DESC
                  LIMIT 1;",
                providerKey,
                appId).FirstOrDefault();
        }

        private static GameRow LoadFriendGameByAppId(
            SQLiteDatabase db,
            string providerKey,
            int appId,
            string providerGameKey,
            string providerPlatformKey,
            Guid? playniteGameId,
            bool allowProviderOnly,
            string gameName,
            string iconUrl,
            string nowIso)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);

            // Aggregator (Exophase) friend games hold the invariant: exactly ONE Games row per
            // (provider, providerGameKey). The library mapping is a flag set/cleared on that single
            // row by the ownership save (which is authoritative for the mapping each run) — it is
            // never expressed by creating a second row for the same key, and a mapped item never
            // rewrites another key's row. Every Exophase key row is friend-pipeline-owned (the
            // current user's own rows use un-prefixed slug keys), so flag changes here cannot touch
            // current-user data. Other providers keep the shared-row semantics below, where a friend
            // item may legitimately resolve to the current user's own Games row.
            if (IsExophaseProvider(providerKey) && !string.IsNullOrWhiteSpace(providerGameKey))
            {
                return ResolveSingleExophaseFriendGameRow(
                    db,
                    providerKey,
                    appId,
                    providerGameKey,
                    providerPlatformKey,
                    playniteGameId,
                    allowProviderOnly,
                    applyOwnershipMapping: true,
                    gameName,
                    nowIso);
            }

            if (playniteGameId.HasValue && playniteGameId.Value != Guid.Empty)
            {
                var mapped = LoadGameByPlayniteId(db, providerKey, playniteGameId.Value);
                if (mapped != null)
                {
                    UpdateMappedGameIdentity(db, mapped.Id, appId, providerGameKey, providerPlatformKey, gameName, nowIso);
                    mapped.ProviderGameId = appId > 0 ? appId : mapped.ProviderGameId;
                    mapped.ProviderGameKey = providerGameKey ?? mapped.ProviderGameKey;
                    return mapped;
                }

                var mappedId = EnsureMappedProviderGame(
                    db,
                    providerKey,
                    appId,
                    providerGameKey,
                    providerPlatformKey,
                    playniteGameId.Value,
                    gameName,
                    nowIso);
                if (mappedId > 0)
                {
                    return LoadGameByPlayniteId(db, providerKey, playniteGameId.Value);
                }
            }

            var shared = ShouldUseSharedFriendGameFallback(providerKey, providerGameKey, playniteGameId)
                ? LoadSharedGameByAppId(db, providerKey, appId, providerGameKey)
                : null;
            if (shared != null || !allowProviderOnly)
            {
                return shared;
            }

            var providerOnly = LoadProviderOnlyGameByAppId(db, providerKey, appId, providerGameKey);
            if (providerOnly != null)
            {
                if (!string.IsNullOrWhiteSpace(gameName) &&
                    !string.Equals(providerOnly.GameName, gameName.Trim(), StringComparison.Ordinal))
                {
                    db.ExecuteNonQuery(
                        @"UPDATE Games
                          SET GameName = ?,
                              LastUpdatedUtc = ?
                          WHERE Id = ?;",
                        DbValue(gameName.Trim()),
                        nowIso,
                        providerOnly.Id);
                    providerOnly.GameName = gameName.Trim();
                }

                return providerOnly;
            }

            var gameId = EnsureProviderOnlyGame(db, providerKey, appId, providerGameKey, providerPlatformKey, gameName, nowIso);
            return gameId > 0
                ? LoadProviderOnlyGameByAppId(db, providerKey, appId, providerGameKey)
                : null;
        }

        // Resolves THE single Games row for an Exophase friend game key, healing any duplicate rows
        // for the key first, and optionally syncing the library-mapping flag from the ownership row.
        // Returns null only when the row does not exist and creation is not allowed.
        private static GameRow ResolveSingleExophaseFriendGameRow(
            SQLiteDatabase db,
            string providerKey,
            int appId,
            string providerGameKey,
            string providerPlatformKey,
            Guid? playniteGameId,
            bool allowCreate,
            bool applyOwnershipMapping,
            string gameName,
            string nowIso)
        {
            var row = MergeDuplicateFriendGameRows(db, providerKey, providerGameKey, nowIso);
            var mappedId = playniteGameId.HasValue && playniteGameId.Value != Guid.Empty
                ? playniteGameId.Value
                : Guid.Empty;

            if (applyOwnershipMapping && mappedId != Guid.Empty)
            {
                // One mapping claim per (provider, library game): release any OTHER key's row still
                // claiming this PlayniteGameId (the previous winner among duplicate trophy lists).
                // This must run BEFORE this key claims the mapping below — the partial unique index
                // on (ProviderKey, PlayniteGameId) rejects a second claim, so a claim-first order
                // aborts the whole ownership transaction whenever the mapping winner changes.
                // Scoped to friend-format keys ("platform|slug"): the current user's own
                // Exophase-provider row uses a bare slug key and must never lose its mapping here.
                db.ExecuteNonQuery(
                    @"UPDATE Games
                      SET PlayniteGameId = NULL,
                          LastUpdatedUtc = ?
                      WHERE ProviderKey = ?
                        AND PlayniteGameId = ?
                        AND ProviderGameKey LIKE '%|%'
                        AND Id <> ?;",
                    nowIso,
                    providerKey,
                    mappedId.ToString(),
                    row?.Id ?? -1);
            }

            if (row == null)
            {
                if (!allowCreate && mappedId == Guid.Empty)
                {
                    return null;
                }

                var name = string.IsNullOrWhiteSpace(gameName)
                    ? $"{providerKey} Game {ToProviderGameLogKey(appId, providerGameKey)}"
                    : gameName.Trim();
                db.ExecuteNonQuery(
                    @"INSERT INTO Games
                        (ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc)
                      VALUES
                        (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    providerKey,
                    DbValue(string.IsNullOrWhiteSpace(providerPlatformKey) ? providerKey : providerPlatformKey.Trim()),
                    appId > 0 ? (object)appId : DBNull.Value,
                    DbValue(providerGameKey),
                    mappedId != Guid.Empty ? (object)mappedId.ToString() : DBNull.Value,
                    DbValue(name),
                    DbValue(providerKey),
                    nowIso,
                    nowIso);
                row = LoadAnyGameByAppId(db, providerKey, appId, providerGameKey);
            }
            else if (applyOwnershipMapping)
            {
                var rowMappedId = ParseGuid(row.PlayniteGameId) ?? Guid.Empty;
                if (mappedId != Guid.Empty && rowMappedId != mappedId)
                {
                    db.ExecuteNonQuery(
                        @"UPDATE Games
                          SET PlayniteGameId = ?,
                              LastUpdatedUtc = ?
                          WHERE Id = ?;",
                        mappedId.ToString(),
                        nowIso,
                        row.Id);
                    row.PlayniteGameId = mappedId.ToString();
                }
                else if (mappedId == Guid.Empty && rowMappedId != Guid.Empty)
                {
                    // Ownership is authoritative for the mapping: this key did not resolve to (or
                    // lost) the library mapping this run, so the stale flag is cleared in place.
                    db.ExecuteNonQuery(
                        @"UPDATE Games
                          SET PlayniteGameId = NULL,
                              LastUpdatedUtc = ?
                          WHERE Id = ?;",
                        nowIso,
                        row.Id);
                    row.PlayniteGameId = null;
                }
            }

            if (row == null)
            {
                return null;
            }

            // Identity refresh: name/platform/appid only. The key equals the row's key by
            // construction (key-first resolution), so the COALESCE write cannot re-key the row.
            UpdateMappedGameIdentity(db, row.Id, appId, providerGameKey, providerPlatformKey, gameName, nowIso);
            return LoadAnyGameByAppId(db, providerKey, appId, providerGameKey);
        }

        // Restores the one-row-per-(provider, key) invariant for a key whose rows were duplicated by
        // earlier mapped/provider-only split resolution: keeps the row holding the most progress data
        // (tie: most recently updated), absorbs the losers' missing definitions, ownership, and
        // progress via the promotion movers, and deletes the loser rows. Returns the surviving row,
        // or null when the key has no rows.
        private static GameRow MergeDuplicateFriendGameRows(
            SQLiteDatabase db,
            string providerKey,
            string providerGameKey,
            string nowIso)
        {
            var rows = db.Load<GameRow>(
                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                  FROM Games
                  WHERE ProviderKey = ?
                    AND ProviderGameKey = ?
                  ORDER BY Id;",
                providerKey,
                providerGameKey).ToList();
            if (rows.Count <= 1)
            {
                return rows.FirstOrDefault();
            }

            GameRow keeper = null;
            var keeperProgressCount = -1L;
            foreach (var candidate in rows)
            {
                var progressCount = db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM UserGameProgress WHERE GameId = ?;",
                    candidate.Id);
                if (progressCount > keeperProgressCount ||
                    (progressCount == keeperProgressCount &&
                     string.CompareOrdinal(candidate.LastUpdatedUtc, keeper?.LastUpdatedUtc) > 0))
                {
                    keeper = candidate;
                    keeperProgressCount = progressCount;
                }
            }

            foreach (var loser in rows)
            {
                if (loser.Id == keeper.Id)
                {
                    continue;
                }

                // Same movers the provider-only promotion uses: definitions the keeper lacks move
                // over (so unlock rows keep resolving), ownership and progress rows migrate with
                // ApiName-based achievement remapping, and conflicts keep the keeper's data.
                MergeProviderOnlyDefinitionsIntoTarget(db, loser.Id, keeper.Id, nowIso);
                MoveFriendOwnershipToPromotedGame(db, loser.Id, keeper.Id, nowIso);
                MoveFriendProgressToPromotedGame(db, loser.Id, keeper.Id, nowIso);

                // Definitions still on the loser are duplicates of keys the keeper already holds
                // (everything else moved above), and any unlock rows still referencing them were
                // copied/remapped onto the keeper's definitions by the progress mover — delete both
                // explicitly rather than relying on FK cascade, which is per-connection.
                db.ExecuteNonQuery(
                    @"DELETE FROM UserAchievements
                      WHERE AchievementDefinitionId IN (
                          SELECT Id FROM AchievementDefinitions WHERE GameId = ?
                      );",
                    loser.Id);
                db.ExecuteNonQuery(
                    @"DELETE FROM AchievementDefinitions
                      WHERE GameId = ?;",
                    loser.Id);
                db.ExecuteNonQuery(
                    "DELETE FROM ProviderGameDefinitionState WHERE ProviderKey = ? AND ProviderGameKey = ? AND Id NOT IN (SELECT MIN(Id) FROM ProviderGameDefinitionState WHERE ProviderKey = ? AND ProviderGameKey = ?);",
                    providerKey,
                    providerGameKey,
                    providerKey,
                    providerGameKey);
                db.ExecuteNonQuery(
                    "DELETE FROM Games WHERE Id = ?;",
                    loser.Id);
            }

            // The keeper carries the union; a stale mapping flag on it is reconciled by the caller.
            return keeper;
        }

        private static bool ShouldUseSharedFriendGameFallback(string providerKey, string providerGameKey, Guid? playniteGameId)
        {
            if (playniteGameId.HasValue && playniteGameId.Value != Guid.Empty)
            {
                return true;
            }

            // Steam/native integer app ids intentionally fall back to the current user's matching
            // shared library row. Exophase string keys are already platform-harmonized by the provider;
            // when it declines to emit a Playnite id, reusing an old mapped row would reintroduce
            // cross-platform merges (for example an Origin friend game landing on a Steam library row).
            return !IsExophaseProvider(providerKey) || string.IsNullOrWhiteSpace(providerGameKey);
        }

        private static long EnsureProviderOnlyGame(
            SQLiteDatabase db,
            string providerKey,
            int appId,
            string providerGameKey,
            string providerPlatformKey,
            string gameName,
            string nowIso)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            var existing = LoadAnyGameByAppId(db, providerKey, appId, providerGameKey);
            if (existing != null)
            {
                return existing.Id;
            }

            var name = string.IsNullOrWhiteSpace(gameName)
                ? $"{providerKey} Game {ToProviderGameLogKey(appId, providerGameKey)}"
                : gameName.Trim();
            db.ExecuteNonQuery(
                @"INSERT INTO Games
                    (ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc)
                  VALUES
                    (?, ?, ?, ?, NULL, ?, ?, ?, ?);",
                providerKey,
                DbValue(string.IsNullOrWhiteSpace(providerPlatformKey) ? providerKey : providerPlatformKey.Trim()),
                appId > 0 ? (object)appId : DBNull.Value,
                DbValue(providerGameKey),
                DbValue(name),
                DbValue(providerKey),
                nowIso,
                nowIso);
            return db.ExecuteScalar<long>("SELECT last_insert_rowid();");
        }

        private static long EnsureMappedProviderGame(
            SQLiteDatabase db,
            string providerKey,
            int appId,
            string providerGameKey,
            string providerPlatformKey,
            Guid playniteGameId,
            string gameName,
            string nowIso)
        {
            if (playniteGameId == Guid.Empty)
            {
                return 0;
            }

            providerGameKey = NormalizeProviderGameKey(providerGameKey);

            // Dev-parity behavior for non-Exophase providers (Exophase keys never reach this
            // method; they resolve through ResolveSingleExophaseFriendGameRow, which handles
            // duplicate trophy lists without hijacking row identity).
            var existing = LoadGameByPlayniteId(db, providerKey, playniteGameId);
            if (existing != null)
            {
                UpdateMappedGameIdentity(db, existing.Id, appId, providerGameKey, providerPlatformKey, gameName, nowIso);
                return existing.Id;
            }

            // Upgrade an existing provider-only row for this game in place (attach the PlayniteGameId)
            // rather than inserting a second row. A cross-provider aggregator friend game (ProviderKey
            // "Exophase" mapped to, say, a "PSN" library game) already has a provider-only row carrying
            // its achievement definitions; creating a separate mapped row would split those definitions
            // away from the ownership/progress/achievements that then attach to the mapped row, dropping
            // every scraped unlock. Reusing the row keeps them on one Games.Id. No unique-index conflict:
            // LoadGameByPlayniteId above already confirmed no mapped row exists for this PlayniteGameId.
            var providerOnly = LoadProviderOnlyGameByAppId(db, providerKey, appId, providerGameKey);
            if (providerOnly != null)
            {
                db.ExecuteNonQuery(
                    @"UPDATE Games
                      SET PlayniteGameId = ?,
                          LastUpdatedUtc = ?
                      WHERE Id = ?;",
                    playniteGameId.ToString(),
                    nowIso,
                    providerOnly.Id);
                UpdateMappedGameIdentity(db, providerOnly.Id, appId, providerGameKey, providerPlatformKey, gameName, nowIso);
                return providerOnly.Id;
            }

            var name = string.IsNullOrWhiteSpace(gameName)
                ? $"{providerKey} Game {ToProviderGameLogKey(appId, providerGameKey)}"
                : gameName.Trim();
            db.ExecuteNonQuery(
                @"INSERT INTO Games
                    (ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc)
                  VALUES
                    (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                providerKey,
                DbValue(string.IsNullOrWhiteSpace(providerPlatformKey) ? providerKey : providerPlatformKey.Trim()),
                appId > 0 ? (object)appId : DBNull.Value,
                DbValue(providerGameKey),
                playniteGameId.ToString(),
                DbValue(name),
                DbValue(providerKey),
                nowIso,
                nowIso);
            return db.ExecuteScalar<long>("SELECT last_insert_rowid();");
        }

        private static void UpdateMappedGameIdentity(
            SQLiteDatabase db,
            long gameId,
            int appId,
            string providerGameKey,
            string providerPlatformKey,
            string gameName,
            string nowIso)
        {
            if (gameId <= 0)
            {
                return;
            }

            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            db.ExecuteNonQuery(
                @"UPDATE Games
                  SET ProviderPlatformKey = COALESCE(?, ProviderPlatformKey),
                      ProviderGameId = COALESCE(?, ProviderGameId),
                      ProviderGameKey = COALESCE(?, ProviderGameKey),
                      GameName = COALESCE(?, GameName),
                      LastUpdatedUtc = ?
                  WHERE Id = ?;",
                DbValue(string.IsNullOrWhiteSpace(providerPlatformKey) ? null : providerPlatformKey.Trim()),
                appId > 0 ? (object)appId : DBNull.Value,
                DbValue(providerGameKey),
                DbValue(string.IsNullOrWhiteSpace(gameName) ? null : gameName.Trim()),
                nowIso,
                gameId);
        }

        private static void UpsertProviderGameDefinitionState(
            SQLiteDatabase db,
            string providerKey,
            int appId,
            string providerGameKey,
            string gameName,
            string iconUrl,
            string status,
            string checkedIso,
            string nowIso)
        {
            providerGameKey = NormalizeProviderGameKey(providerGameKey);
            db.ExecuteNonQuery(
                @"INSERT OR IGNORE INTO ProviderGameDefinitionState
                    (ProviderKey, ProviderGameId, ProviderGameKey, GameName, IconUrl, Status, LastCheckedUtc, CreatedUtc, UpdatedUtc)
                  VALUES
                    (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                providerKey,
                appId > 0 ? (object)appId : DBNull.Value,
                DbValue(providerGameKey),
                DbValue(gameName),
                DbValue(iconUrl),
                DbValue(status),
                checkedIso,
                nowIso,
                nowIso);
            // Schema-safety invariant: a cached 'ok' definition state is only ever replaced by
            // another 'ok'. The "AND (Status <> 'ok' OR ? = 'ok')" guard blocks an 'ok' -> non-'ok'
            // downgrade from a soft-empty/transient/unavailable fetch. Because a blocked update also
            // leaves LastCheckedUtc untouched, a previously-good game past its TTL stays eligible for
            // a real retry rather than thrashing on the bad result. anything -> 'ok' and
            // non-'ok' -> non-'ok' transitions are still allowed.
            if (!string.IsNullOrWhiteSpace(providerGameKey))
            {
                db.ExecuteNonQuery(
                    @"UPDATE ProviderGameDefinitionState
                  SET GameName = ?,
                      IconUrl = ?,
                      Status = ?,
                      LastCheckedUtc = ?,
                      UpdatedUtc = ?
                  WHERE ProviderKey = ?
                    AND ProviderGameKey = ?
                    AND (Status <> 'ok' OR ? = 'ok');",
                    DbValue(gameName),
                    DbValue(iconUrl),
                    DbValue(status),
                    checkedIso,
                    nowIso,
                    providerKey,
                    providerGameKey,
                    status);
            }
            else
            {
                db.ExecuteNonQuery(
                    @"UPDATE ProviderGameDefinitionState
                      SET GameName = ?,
                          IconUrl = ?,
                          Status = ?,
                          LastCheckedUtc = ?,
                          UpdatedUtc = ?
                      WHERE ProviderKey = ?
                        AND ProviderGameId = ?
                        AND (Status <> 'ok' OR ? = 'ok');",
                    DbValue(gameName),
                    DbValue(iconUrl),
                    DbValue(status),
                    checkedIso,
                    nowIso,
                    providerKey,
                    appId,
                    status);
            }
        }

        private static FriendUnownedCacheStats LoadUnownedFriendGameCacheStats(SQLiteDatabase db)
        {
            return new FriendUnownedCacheStats
            {
                Games = (int)Math.Max(0, db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM Games g
                      WHERE (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                        AND NOT EXISTS (
                            SELECT 1
                            FROM UserGameProgress ugp
                            INNER JOIN Users u ON u.Id = ugp.UserId
                            WHERE ugp.GameId = g.Id
                              AND u.IsCurrentUser = 1
                        );")),
                Definitions = (int)Math.Max(0, db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM AchievementDefinitions
                      WHERE GameId IN (
                          SELECT g.Id
                          FROM Games g
                          WHERE (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                            AND NOT EXISTS (
                                SELECT 1
                                FROM UserGameProgress ugp
                                INNER JOIN Users u ON u.Id = ugp.UserId
                                WHERE ugp.GameId = g.Id
                                  AND u.IsCurrentUser = 1
                            )
                      );")),
                OwnershipRows = (int)Math.Max(0, db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM FriendOwnership
                      WHERE GameId IN (
                          SELECT Id FROM Games WHERE PlayniteGameId IS NULL OR TRIM(PlayniteGameId) = ''
                      );")),
                ProgressRows = (int)Math.Max(0, db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM UserGameProgress ugp
                      INNER JOIN Games g ON g.Id = ugp.GameId
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      WHERE u.IsCurrentUser = 0
                        AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '');")),
                AchievementRows = (int)Math.Max(0, db.ExecuteScalar<long>(
                    @"SELECT COUNT(*)
                      FROM UserAchievements
                      WHERE UserGameProgressId IN (
                          SELECT ugp.Id
                          FROM UserGameProgress ugp
                          INNER JOIN Games g ON g.Id = ugp.GameId
                          INNER JOIN Users u ON u.Id = ugp.UserId
                          WHERE u.IsCurrentUser = 0
                            AND (g.PlayniteGameId IS NULL OR TRIM(g.PlayniteGameId) = '')
                      );")),
                DefinitionStates = (int)Math.Max(0, db.ExecuteScalar<long>(
                    "SELECT COUNT(*) FROM ProviderGameDefinitionState;"))
            };
        }

        private static string ToDefinitionStatusStorage(FriendGameDefinitionStatus status)
        {
            switch (status)
            {
                case FriendGameDefinitionStatus.Ok:
                    return "ok";
                case FriendGameDefinitionStatus.NoAchievements:
                    return "no_achievements";
                case FriendGameDefinitionStatus.Transient:
                    return "transient";
                default:
                    return "unavailable";
            }
        }

        private static FriendGameDefinitionStatus ParseDefinitionStatus(string status)
        {
            if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                return FriendGameDefinitionStatus.Ok;
            }

            if (string.Equals(status, "no_achievements", StringComparison.OrdinalIgnoreCase))
            {
                return FriendGameDefinitionStatus.NoAchievements;
            }

            if (string.Equals(status, "transient", StringComparison.OrdinalIgnoreCase))
            {
                return FriendGameDefinitionStatus.Transient;
            }

            return FriendGameDefinitionStatus.Unavailable;
        }

        private static bool UpsertFriendOwnership(
            SQLiteDatabase db,
            long userId,
            long gameId,
            FriendGameOwnership item,
            string nowIso)
        {
            var lastPlayedIso = item.LastPlayedUtc.HasValue ? ToIso(item.LastPlayedUtc.Value) : null;
            var playtime2Weeks = item.Playtime2WeeksMinutes.HasValue
                ? (int?)Math.Max(0, item.Playtime2WeeksMinutes.Value)
                : null;
            var existingId = db.ExecuteScalar<long>(
                @"SELECT Id
                  FROM FriendOwnership
                  WHERE UserId = ?
                    AND GameId = ?
                  LIMIT 1;",
                new object[] { userId, gameId });

            if (existingId <= 0)
            {
                db.ExecuteNonQuery(
                    @"INSERT INTO FriendOwnership
                        (UserId, GameId, PlaytimeForeverMinutes, Playtime2WeeksMinutes, LastPlayedUtc, LastOwnershipRefreshUtc, CreatedUtc, UpdatedUtc)
                      VALUES
                        (?, ?, ?, ?, ?, ?, ?, ?);",
                    userId,
                    gameId,
                    Math.Max(0, item.PlaytimeForeverMinutes),
                    DbParam(playtime2Weeks),
                    DbValue(lastPlayedIso),
                    nowIso,
                    nowIso,
                    nowIso);
                return FriendOwnershipExists(db, userId, gameId);
            }

            db.ExecuteNonQuery(
                @"UPDATE FriendOwnership
                  SET PlaytimeForeverMinutes = ?,
                      Playtime2WeeksMinutes = ?,
                      LastPlayedUtc = ?,
                      LastOwnershipRefreshUtc = ?,
                      UpdatedUtc = ?
                  WHERE Id = ?;",
                Math.Max(0, item.PlaytimeForeverMinutes),
                DbParam(playtime2Weeks),
                DbValue(lastPlayedIso),
                nowIso,
                nowIso,
                existingId);

            return FriendOwnershipExists(db, userId, gameId);
        }

        private static void DeleteStaleSharedFriendOwnership(SQLiteDatabase db, long userId, HashSet<long> seenGameIds)
        {
            if (seenGameIds == null || seenGameIds.Count == 0)
            {
                // Empty ownership is not authoritative; preserve existing shared ownership rows.
                return;
            }

            var placeholders = string.Join(",", seenGameIds.Select(_ => "?"));
            var args = new List<object> { userId };
            args.AddRange(seenGameIds.Cast<object>());
            db.ExecuteNonQuery(
                @"DELETE FROM FriendOwnership
                  WHERE UserId = ?
                    AND GameId NOT IN (" + placeholders + @")
                    AND EXISTS (
                        SELECT 1
                        FROM Games g
                        WHERE g.Id = FriendOwnership.GameId
                          AND g.PlayniteGameId IS NOT NULL
                          AND TRIM(g.PlayniteGameId) <> ''
                    );",
                args.ToArray());
        }

        private static bool FriendOwnershipExists(SQLiteDatabase db, long userId, long gameId)
        {
            if (db == null || userId <= 0 || gameId <= 0)
            {
                return false;
            }

            return db.ExecuteScalar<long>(
                @"SELECT EXISTS(
                    SELECT 1
                    FROM FriendOwnership
                    WHERE UserId = ?
                      AND GameId = ?
                    LIMIT 1
                  );",
                new object[] { userId, gameId }) != 0;
        }

        private static string ResolveFriendScrapeStatus(FriendGameAchievements achievements)
        {
            if (achievements == null || achievements.TransientFailure)
            {
                return "transient";
            }

            return achievements.StatsUnavailable ? "unavailable" : "ok";
        }

        private static void UpdateFriendOwnershipScrapeState(
            SQLiteDatabase db,
            long userId,
            long gameId,
            string status,
            string detail,
            string updatedIso,
            string nowIso)
        {
            var existingId = db.ExecuteScalar<long>(
                @"SELECT Id
                  FROM FriendOwnership
                  WHERE UserId = ?
                    AND GameId = ?
                  LIMIT 1;",
                new object[] { userId, gameId });

            if (existingId <= 0)
            {
                db.ExecuteNonQuery(
                    @"INSERT INTO FriendOwnership
                        (UserId, GameId, PlaytimeForeverMinutes, LastScrapedUtc, LastScrapeStatus, LastScrapeDetail, CreatedUtc, UpdatedUtc)
                      VALUES
                        (?, ?, 0, ?, ?, ?, ?, ?);",
                    userId,
                    gameId,
                    updatedIso,
                    DbValue(status),
                    DbValue(detail),
                    nowIso,
                    nowIso);
                return;
            }

            db.ExecuteNonQuery(
                @"UPDATE FriendOwnership
                  SET LastScrapedUtc = ?,
                      LastScrapeStatus = ?,
                      LastScrapeDetail = ?,
                      UpdatedUtc = ?
                  WHERE Id = ?;",
                updatedIso,
                DbValue(status),
                DbValue(detail),
                nowIso,
                existingId);
        }

        private static List<AchievementDefinitionRow> LoadAchievementDefinitionsForGame(SQLiteDatabase db, long gameId)
        {
            return db.Load<AchievementDefinitionRow>(
                @"SELECT Id, GameId, ApiName, DisplayName, Description, UnlockedIconPath, LockedIconPath,
                         Points, ScaledPoints, Category, CategoryType, TrophyType, Hidden, IsCapstone,
                         GlobalPercentUnlocked, Rarity, ProgressMax, CreatedUtc, UpdatedUtc
                  FROM AchievementDefinitions
                  WHERE GameId = ?
                  ORDER BY Id;",
                gameId).ToList();
        }

        private static bool ShouldRefreshDefinitionsFromFriendRows(
            List<AchievementDefinitionRow> definitions,
            List<AchievementDetail> incomingDefinitions)
        {
            if (incomingDefinitions == null || incomingDefinitions.Count == 0)
            {
                return false;
            }

            // Schema-safety invariant: scraped friend rows only *seed* definitions when the game has
            // no cached schema at all. Any existing schema — a game the current user owns
            // (authoritative), a friend definition fetched from the provider's schema API, or a prior
            // complete friend seed — stays canonical and is never overwritten with scrape-quality
            // fields. (Previously this also reseeded when the cached keys were a subset of the scrape,
            // which clobbered good descriptions/icons/rarity with scrape values.)
            return definitions == null || definitions.Count == 0;
        }

        private bool TryRefreshMappedCurrentUserDefinitionsForProxyGame(
            SQLiteDatabase db,
            GameRow game,
            string nowIso,
            string updatedIso,
            IDictionary<string, string> renameCollector = null)
        {
            if (db == null ||
                game == null ||
                game.Id <= 0 ||
                !IsExophaseProvider(game.ProviderKey) ||
                !ParseGuid(game.PlayniteGameId).HasValue)
            {
                return false;
            }

            var sourceDefinitions = LoadMappedCurrentUserDefinitionsForProxyGame(db, game);
            if (sourceDefinitions.Count == 0)
            {
                return false;
            }

            var achievements = sourceDefinitions
                .Select(CreateAchievementDetailFromDefinitionRow)
                .Where(achievement => !string.IsNullOrWhiteSpace(achievement?.ApiName))
                .ToList();
            if (achievements.Count == 0)
            {
                return false;
            }

            // The mapped current-user schema is complete by construction; the proxy row must mirror
            // it exactly, so stale accumulated keys are pruned regardless of relative counts.
            UpsertAchievementDefinitions(db, game.Id, achievements, nowIso, updatedIso, renameCollector, authoritativeSource: true);
            return true;
        }

        private static List<AchievementDefinitionRow> LoadMappedCurrentUserDefinitionsForProxyGame(
            SQLiteDatabase db,
            GameRow proxyGame)
        {
            var playniteGameId = ParseGuid(proxyGame?.PlayniteGameId);
            if (db == null || proxyGame == null || !playniteGameId.HasValue)
            {
                return new List<AchievementDefinitionRow>();
            }

            var candidates = db.Load<GameRow>(
                @"SELECT DISTINCT g.Id, g.ProviderKey, g.ProviderPlatformKey, g.ProviderGameId, g.ProviderGameKey, g.PlayniteGameId, g.GameName, g.LibrarySourceName, g.FirstSeenUtc, g.LastUpdatedUtc
                  FROM Games g
                  INNER JOIN UserGameProgress ugp ON ugp.GameId = g.Id
                  INNER JOIN Users u ON u.Id = ugp.UserId
                  WHERE u.IsCurrentUser = 1
                    AND g.PlayniteGameId = ?
                    AND g.Id <> ?
                  ORDER BY g.LastUpdatedUtc DESC, g.Id DESC;",
                playniteGameId.Value.ToString(),
                proxyGame.Id).ToList();

            var bestRank = int.MaxValue;
            List<AchievementDefinitionRow> bestDefinitions = null;
            foreach (var candidate in candidates)
            {
                var definitions = LoadAchievementDefinitionsForGame(db, candidate.Id);
                if (definitions.Count == 0)
                {
                    continue;
                }

                var rank = ComputeMappedCurrentUserDefinitionRank(proxyGame, candidate);
                if (bestDefinitions == null ||
                    rank < bestRank ||
                    (rank == bestRank && definitions.Count > bestDefinitions.Count))
                {
                    bestRank = rank;
                    bestDefinitions = definitions;
                }
            }

            return bestDefinitions ?? new List<AchievementDefinitionRow>();
        }

        private static int ComputeMappedCurrentUserDefinitionRank(GameRow proxyGame, GameRow candidate)
        {
            var targetFamily = ExophaseFriendPlatformMatcher.ResolveStoredGameFamilyKey(
                proxyGame?.ProviderKey,
                proxyGame?.ProviderPlatformKey);
            var candidateFamily = ExophaseFriendPlatformMatcher.ResolveStoredGameFamilyKey(
                candidate?.ProviderKey,
                candidate?.ProviderPlatformKey);

            if (!string.IsNullOrWhiteSpace(targetFamily) &&
                !string.IsNullOrWhiteSpace(candidateFamily) &&
                string.Equals(targetFamily, candidateFamily, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            return string.IsNullOrWhiteSpace(targetFamily) ? 1 : 2;
        }

        private static AchievementDetail CreateAchievementDetailFromDefinitionRow(AchievementDefinitionRow row)
        {
            return new AchievementDetail
            {
                ApiName = row?.ApiName,
                DisplayName = row?.DisplayName,
                Description = row?.Description,
                UnlockedIconPath = row?.UnlockedIconPath,
                LockedIconPath = row?.LockedIconPath,
                Points = row?.Points,
                ScaledPoints = row?.ScaledPoints,
                Category = row?.Category,
                CategoryType = row?.CategoryType,
                TrophyType = row?.TrophyType,
                Hidden = row != null && row.Hidden != 0,
                IsCapstone = row != null && row.IsCapstone != 0,
                GlobalPercentUnlocked = row?.GlobalPercentUnlocked,
                Rarity = ParseStoredRarity(row?.Rarity),
                ProgressDenom = row?.ProgressMax
            };
        }

        private static List<FriendDefinitionMatch> MapFriendRowsToDefinitions(
            List<AchievementDefinitionRow> definitions,
            IEnumerable<FriendAchievementRow> rows)
        {
            var result = new List<FriendDefinitionMatch>();
            if (definitions == null || definitions.Count == 0 || rows == null)
            {
                return result;
            }

            var usedDefinitionIds = new HashSet<long>();
            foreach (var row in rows.Where(row => row != null))
            {
                var definition = ResolveFriendDefinition(definitions, row, usedDefinitionIds);
                if (definition == null)
                {
                    continue;
                }

                usedDefinitionIds.Add(definition.Id);
                result.Add(new FriendDefinitionMatch
                {
                    DefinitionId = definition.Id,
                    Row = row
                });
            }

            return result;
        }

        private static AchievementDefinitionRow ResolveFriendDefinition(
            List<AchievementDefinitionRow> definitions,
            FriendAchievementRow row,
            HashSet<long> usedDefinitionIds)
        {
            // Prefer a stable, language-independent key (e.g. Steam api name) when the friend row
            // carries one. This matches correctly even when the friend's display text is in a
            // different language than the stored definitions. Falls through to display-name/icon
            // matching for providers that expose no stable key.
            var rowApiName = NormalizeMatchText(row.ApiName);
            if (!string.IsNullOrEmpty(rowApiName))
            {
                var byApiName = definitions
                    .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                                  string.Equals(NormalizeMatchText(def.ApiName), rowApiName, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (byApiName.Count == 1)
                {
                    return byApiName[0];
                }
            }

            // Native-key bridge: mapped games serviced by the platform's own provider key their
            // definitions with the platform-native scheme (Steam apinames, PSN "{group}:{trophyId}").
            // Aggregator rows carry that native key in ProviderNativeKey — match it verbatim first,
            // then in the group-qualified PSN form. Unique matches only.
            if (!string.IsNullOrWhiteSpace(row.ProviderNativeKey))
            {
                var byNativeKey = definitions
                    .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                                  SqlNadoCacheBehavior.MatchesNativeKey(def.ApiName, row.ProviderNativeKey))
                    .ToList();
                if (byNativeKey.Count == 1)
                {
                    return byNativeKey[0];
                }

                var byGroupQualifiedKey = definitions
                    .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                                  SqlNadoCacheBehavior.MatchesGroupQualifiedNativeKey(def.ApiName, row.ProviderNativeKey))
                    .ToList();
                if (byGroupQualifiedKey.Count == 1)
                {
                    return byGroupQualifiedKey[0];
                }
            }

            var rowDisplay = NormalizeMatchText(row.DisplayName);
            var rowDescription = NormalizeMatchText(row.Description);

            var exact = definitions
                .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                              string.Equals(NormalizeMatchText(def.DisplayName), rowDisplay, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(NormalizeMatchText(def.Description), rowDescription, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
            {
                return exact[0];
            }

            var byName = definitions
                .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                              string.Equals(NormalizeMatchText(def.DisplayName), rowDisplay, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (byName.Count == 1)
            {
                return byName[0];
            }

            var iconFile = ExtractIconFilename(row.IconUrl);
            if (!string.IsNullOrWhiteSpace(iconFile))
            {
                var byIcon = definitions
                    .Where(def => !usedDefinitionIds.Contains(def.Id) &&
                                  (string.Equals(ExtractIconFilename(def.UnlockedIconPath), iconFile, StringComparison.OrdinalIgnoreCase) ||
                                   string.Equals(ExtractIconFilename(def.LockedIconPath), iconFile, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (byIcon.Count == 1)
                {
                    return byIcon[0];
                }
            }

            return null;
        }

        private static void UpsertFriendAchievementRows(
            SQLiteDatabase db,
            long userProgressId,
            List<FriendDefinitionMatch> matchedRows,
            string updatedIso,
            string nowIso)
        {
            if (matchedRows == null || matchedRows.Count == 0)
            {
                return;
            }

            var existingRows = db.Load<UserAchievementRow>(
                @"SELECT Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc
                  FROM UserAchievements
                  WHERE UserGameProgressId = ?;",
                userProgressId)
                .ToDictionary(row => row.AchievementDefinitionId);

            foreach (var match in matchedRows)
            {
                var incoming = match.Row;
                var incomingUnlocked = incoming?.Unlocked == true ? 1L : 0L;
                var incomingTime = incomingUnlocked != 0 ? NormalizeUnlockTime(incoming.UnlockTimeUtc) : null;
                var incomingIso = incomingTime.HasValue ? ToIso(incomingTime.Value) : null;
                var progressNum = incoming?.ProgressNum;
                var progressDenom = incoming?.ProgressDenom;

                if (!existingRows.TryGetValue(match.DefinitionId, out var existing))
                {
                    db.ExecuteNonQuery(
                        @"INSERT INTO UserAchievements
                            (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                          VALUES
                            (?, ?, ?, ?, ?, ?, ?, ?);",
                        userProgressId,
                        match.DefinitionId,
                        incomingUnlocked,
                        DbValue(incomingIso),
                        DbParam(progressNum),
                        DbParam(progressDenom),
                        updatedIso,
                        nowIso);
                    continue;
                }

                var existingIso = NormalizeStoredIso(existing.UnlockTimeUtc);
                var resolvedIso = incomingUnlocked != 0
                    ? incomingIso ?? existingIso
                    : null;
                var resolvedProgressNum = progressNum ?? existing.ProgressNum;
                var resolvedProgressDenom = progressDenom ?? existing.ProgressDenom;

                var changed = existing.Unlocked != incomingUnlocked ||
                              !NullableEquals(existingIso, resolvedIso) ||
                              existing.ProgressNum != resolvedProgressNum ||
                              existing.ProgressDenom != resolvedProgressDenom;
                if (!changed)
                {
                    continue;
                }

                db.ExecuteNonQuery(
                    @"UPDATE UserAchievements
                      SET Unlocked = ?,
                          UnlockTimeUtc = ?,
                          ProgressNum = ?,
                          ProgressDenom = ?,
                          LastUpdatedUtc = ?
                      WHERE Id = ?;",
                    incomingUnlocked,
                    DbValue(resolvedIso),
                    DbParam(resolvedProgressNum),
                    DbParam(resolvedProgressDenom),
                    updatedIso,
                    existing.Id);
            }
        }

        /// <summary>
        /// Loads the active (non-ignored) friend roster for a provider as lightweight identities, for
        /// display in provider settings. Ignored friends are tracked separately in provider settings.
        /// </summary>
        public List<FriendIdentity> LoadFriendIdentities(string providerKey)
        {
            providerKey = NormalizeProviderKey(providerKey);
            try
            {
                return WithDb(db =>
                    db.Load<FriendIdentityRow>(
                        @"SELECT
                            u.ProviderKey AS ProviderKey,
                            u.ExternalUserId AS ExternalUserId,
                            u.DisplayName AS DisplayName,
                            u.AvatarUrl AS AvatarUrl,
                            u.AvatarPath AS AvatarPath
                          FROM Users u
                          WHERE u.ProviderKey = ?
                            AND " + ActiveFriendPredicateSql + @"
                          ORDER BY u.DisplayName;",
                        providerKey)
                    .Select(row => new FriendIdentity
                    {
                        ProviderKey = row.ProviderKey,
                        ExternalUserId = row.ExternalUserId,
                        DisplayName = row.DisplayName,
                        AvatarUrl = row.AvatarUrl,
                        AvatarPath = !string.IsNullOrWhiteSpace(row.AvatarPath)
                            ? MakeAbsolutePath(row.AvatarPath)
                            : row.AvatarUrl
                    })
                    .ToList());
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to load friend identities for provider={providerKey}.");
                return new List<FriendIdentity>();
            }
        }

        private sealed class FriendIdentityRow
        {
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public string AvatarUrl { get; set; }
            public string AvatarPath { get; set; }
        }

        private List<FriendSummaryItem> MapFriendSummaryRows(IEnumerable<FriendSummaryRow> rows)
        {
            return (rows ?? Enumerable.Empty<FriendSummaryRow>())
                .Where(row => row != null)
                .Select(row => new FriendSummaryItem
                {
                    ProviderKey = row.ProviderKey,
                    ExternalUserId = row.ExternalUserId,
                    DisplayName = row.DisplayName,
                    AvatarPath = !string.IsNullOrWhiteSpace(row.AvatarPath)
                        ? MakeAbsolutePath(row.AvatarPath)
                        : row.AvatarUrl,
                    SharedGamesCount = (int)Math.Max(0, row.SharedGamesCount),
                    GamesWithUnlocksCount = (int)Math.Max(0, row.GamesWithUnlocksCount),
                    CompletedGamesCount = (int)Math.Max(0, row.CompletedGamesCount),
                    UnlockedAchievementsCount = (int)Math.Max(0, row.UnlockedAchievementsCount),
                    RecentUnlockCount = (int)Math.Max(0, row.RecentUnlockCount),
                    LastUnlockUtc = ParseUtc(row.LastUnlockUtc),
                    LastRefreshedUtc = ParseUtc(row.LastRefreshedUtc),
                    TotalPlaytimeMinutes = Math.Max(0, row.TotalPlaytimeMinutes)
                })
                .ToList();
        }

        private List<FriendGameSummaryItem> MapFriendGameSummaryRows(
            IEnumerable<FriendGameSummaryRow> rows,
            Dictionary<Guid, GamePresentation> presentationCache)
        {
            return (rows ?? Enumerable.Empty<FriendGameSummaryRow>())
                .Where(row => row != null)
                .Select(row =>
                {
                    var playniteGameId = ResolveCachedPlayniteGameId(null, row.PlayniteGameId);
                    var presentation = ResolveGamePresentation(playniteGameId, presentationCache);
                    // Display the underlying provider (e.g. EA) via visual fields, but keep the raw
                    // aggregator ProviderKey for identity comparisons and refresh targeting.
                    var displayProviderKey = ResolveDisplayProviderKey(row.ProviderKey, row.ProviderPlatformKey);
                    var providerName = ProviderRegistry.GetLocalizedName(displayProviderKey);
                    if (!ProviderRegistry.TryResolveProviderVisuals(displayProviderKey, out var providerIconKey, out var providerColorHex))
                    {
                        providerIconKey = string.IsNullOrWhiteSpace(displayProviderKey) ? null : "ProviderIcon" + displayProviderKey;
                        providerColorHex = "#888888";
                    }

                    return new FriendGameSummaryItem
                    {
                        ProviderKey = string.IsNullOrWhiteSpace(row.ProviderKey) ? displayProviderKey : row.ProviderKey.Trim(),
                        Provider = string.IsNullOrWhiteSpace(providerName) ? displayProviderKey : providerName,
                        ProviderIconKey = providerIconKey,
                        ProviderColorHex = providerColorHex,
                        AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                        ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                        PlayniteGameId = playniteGameId,
                        GameName = presentation.DisplayName ?? row.GameName,
                        SortingName = presentation.SortingName ?? presentation.DisplayName ?? row.GameName,
                        GameLogo = ResolveFriendGameIconPath(presentation, row.IconPath),
                        GameCoverPath = ResolveFriendGameCoverPath(presentation, row.CoverPath),
                        PlatformText = presentation.PlatformText,
                        Platforms = presentation.Platforms,
                        RegionText = presentation.RegionText,
                        PlaytimeSeconds = presentation.PlaytimeSeconds,
                        LastPlayed = presentation.LastPlayed,
                        FriendCount = (int)Math.Max(0, row.FriendCount),
                        FriendsWithUnlocksCount = (int)Math.Max(0, row.FriendsWithUnlocksCount),
                        FriendUnlockedAchievementsCount = (int)Math.Max(0, row.UnlockedAchievementsCount),
                        UniqueFriendUnlockedAchievementsCount = (int)Math.Max(0, row.UniqueUnlockedAchievementsCount),
                        TotalAchievements = (int)Math.Max(0, row.TotalAchievements),
                        LastFriendUnlockUtc = ParseUtc(row.LastUnlockUtc),
                        TotalFriendPlaytimeMinutes = Math.Max(0, row.TotalPlaytimeMinutes),
                        AverageFriendPlaytimeMinutes = Math.Max(0, row.AveragePlaytimeMinutes),
                        LastFriendPlayedUtc = ParseUtc(row.LastPlayedUtc),
                        LastFriendScrapedUtc = ParseUtc(row.LastScrapedUtc),
                        LastFriendScrapeStatus = row.LastScrapeStatus
                    };
                })
                .ToList();
        }

        private List<FriendGameLinkItem> MapFriendGameLinkRows(IEnumerable<FriendGameLinkRow> rows)
        {
            return (rows ?? Enumerable.Empty<FriendGameLinkRow>())
                .Where(row => row != null)
                .Select(row => new FriendGameLinkItem
                {
                    ProviderKey = row.ProviderKey,
                    ExternalUserId = row.ExternalUserId,
                    AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                    ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                    PlayniteGameId = ResolveCachedPlayniteGameId(null, row.PlayniteGameId),
                    PlaytimeForeverMinutes = Math.Max(0, row.PlaytimeForeverMinutes),
                    LastPlayedUtc = ParseUtc(row.LastPlayedUtc)
                })
                .ToList();
        }

        private static List<FriendSummaryItem> BuildFriendSummariesFromAchievements(
            IEnumerable<FriendAchievementDisplayItem> achievements)
        {
            return (achievements ?? Enumerable.Empty<FriendAchievementDisplayItem>())
                .Where(achievement => achievement != null &&
                                      !string.IsNullOrWhiteSpace(achievement.ProviderKey) &&
                                      !string.IsNullOrWhiteSpace(achievement.FriendExternalUserId))
                .GroupBy(
                    achievement => FriendsOverviewDerivations.BuildFriendScoreKey(achievement.ProviderKey, achievement.FriendExternalUserId),
                    StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var first = group.First();
                    var unlocked = group.Where(achievement => achievement.Unlocked).ToList();
                    return new FriendSummaryItem
                    {
                        ProviderKey = first.ProviderKey,
                        ExternalUserId = first.FriendExternalUserId,
                        DisplayName = first.FriendName,
                        AvatarPath = first.FriendAvatarPath,
                        SharedGamesCount = group
                            .Select(achievement => FriendOverviewProjection.BuildGameUnlockKey(
                                achievement.ProviderKey,
                                achievement.ProviderGameKey,
                                achievement.AppId,
                                achievement.PlayniteGameId))
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        GamesWithUnlocksCount = unlocked
                            .Select(achievement => FriendOverviewProjection.BuildGameUnlockKey(
                                achievement.ProviderKey,
                                achievement.ProviderGameKey,
                                achievement.AppId,
                                achievement.PlayniteGameId))
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Count(),
                        UnlockedAchievementsCount = unlocked.Count,
                        RecentUnlockCount = unlocked.Count(achievement => achievement.UnlockTimeUtc.HasValue),
                        LastUnlockUtc = unlocked
                            .Select(achievement => achievement.UnlockTimeUtc)
                            .Where(value => value.HasValue)
                            .DefaultIfEmpty()
                            .Max()
                    };
                })
                .OrderByDescending(friend => friend.LastUnlockUtc ?? DateTime.MinValue)
                .ThenBy(friend => friend.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        private static List<FriendSummaryRow> LoadFriendSummaryRows(SQLiteDatabase db, Guid? playniteGameId = null)
        {
            var recentCutoffIso = ToIso(DateTime.UtcNow.AddDays(-30));
            var filterByGame = playniteGameId.HasValue;
            var args = new List<object>();

            // Per-friend stats are pre-aggregated once (grouped by UserId) and joined back to
            // the friend rows, replacing the previous six correlated subqueries per friend.
            // When a target game is supplied, a target_games CTE narrows both aggregates and
            // only friends related to that game are returned.
            var sql = new StringBuilder("WITH ");

            if (filterByGame)
            {
                sql.Append(
                    @"target_games AS (
                    SELECT Id
                    FROM Games
                    WHERE PlayniteGameId IS NOT NULL
                      AND TRIM(PlayniteGameId) <> ''
                      AND LOWER(PlayniteGameId) = LOWER(?)
                ),
                ");
                args.Add(playniteGameId.Value.ToString("D"));
            }

            sql.Append(
                @"ownership AS (
                    SELECT fo.UserId AS UserId,
                           COUNT(DISTINCT fo.GameId) AS SharedGamesCount,
                           COALESCE(SUM(fo.PlaytimeForeverMinutes), 0) AS TotalPlaytimeMinutes
                    FROM FriendOwnership fo");
            if (filterByGame)
            {
                sql.Append(@"
                    INNER JOIN target_games tg ON tg.Id = fo.GameId");
            }

            sql.Append(
                @"
                    INNER JOIN Users u ON u.Id = fo.UserId
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY fo.UserId
                ),
                unlocks AS (
                    SELECT ugp.UserId AS UserId,
                           COUNT(DISTINCT ugp.GameId) AS GamesWithUnlocksCount,
                           COUNT(DISTINCT CASE WHEN ugp.TotalAchievements > 0 AND ugp.AchievementsUnlocked >= ugp.TotalAchievements THEN ugp.GameId END) AS CompletedGamesCount,
                           COUNT(ua.Id) AS UnlockedAchievementsCount,
                           COUNT(CASE WHEN ua.UnlockTimeUtc IS NOT NULL AND ua.UnlockTimeUtc >= ? THEN ua.Id END) AS RecentUnlockCount,
                           MAX(ua.UnlockTimeUtc) AS LastUnlockUtc
                    FROM UserGameProgress ugp");
            args.Add(recentCutoffIso);
            if (filterByGame)
            {
                sql.Append(@"
                    INNER JOIN target_games tg ON tg.Id = ugp.GameId");
            }

            sql.Append(
                @"
                    INNER JOIN Users u ON u.Id = ugp.UserId
                    INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY ugp.UserId
                )
                SELECT
                    u.ProviderKey AS ProviderKey,
                    u.ExternalUserId AS ExternalUserId,
                    u.DisplayName AS DisplayName,
                    u.AvatarUrl AS AvatarUrl,
                    u.AvatarPath AS AvatarPath,
                    COALESCE(o.SharedGamesCount, 0) AS SharedGamesCount,
                    COALESCE(un.GamesWithUnlocksCount, 0) AS GamesWithUnlocksCount,
                    COALESCE(un.CompletedGamesCount, 0) AS CompletedGamesCount,
                    COALESCE(un.UnlockedAchievementsCount, 0) AS UnlockedAchievementsCount,
                    COALESCE(un.RecentUnlockCount, 0) AS RecentUnlockCount,
                    un.LastUnlockUtc AS LastUnlockUtc,
                    u.LastRefreshedUtc AS LastRefreshedUtc,
                    COALESCE(o.TotalPlaytimeMinutes, 0) AS TotalPlaytimeMinutes
                  FROM Users u
                  LEFT JOIN ownership o ON o.UserId = u.Id
                  LEFT JOIN unlocks un ON un.UserId = u.Id
                  WHERE " + ActiveFriendPredicateSql);
            if (filterByGame)
            {
                sql.Append(@"
                    AND (o.UserId IS NOT NULL OR un.UserId IS NOT NULL)");
            }

            sql.Append(@"
                  ORDER BY un.LastUnlockUtc DESC, u.DisplayName;");

            return db.Load<FriendSummaryRow>(sql.ToString(), args.ToArray()).ToList();
        }

        private static List<FriendGameSummaryRow> LoadFriendGameSummaryRows(SQLiteDatabase db)
        {
            // Per-game friend stats are pre-aggregated once (grouped by GameId) across three
            // source CTEs and joined back to the game rows, replacing the previous eleven
            // correlated subqueries per game. Ownership is the driving set, so only games a
            // friend owns appear (matching the previous EXISTS filter).
            return db.Load<FriendGameSummaryRow>(
                @"WITH ownership AS (
                    SELECT fo.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendCount,
                           COALESCE(SUM(fo.PlaytimeForeverMinutes), 0) AS TotalPlaytimeMinutes,
                           COALESCE(AVG(fo.PlaytimeForeverMinutes), 0) AS AveragePlaytimeMinutes,
                           MAX(fo.LastPlayedUtc) AS LastPlayedUtc,
                           MAX(fo.LastScrapedUtc) AS LastScrapedUtc
                    FROM FriendOwnership fo
                    INNER JOIN Users u ON u.Id = fo.UserId
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY fo.GameId
                ),
                unlocks AS (
                    SELECT ugp.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendsWithUnlocksCount,
                           COUNT(ua.Id) AS UnlockedAchievementsCount,
                           COUNT(DISTINCT ua.AchievementDefinitionId) AS UniqueUnlockedAchievementsCount,
                           MAX(ua.UnlockTimeUtc) AS LastUnlockUtc
                    FROM Users u
                    INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id
                    INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY ugp.GameId
                ),
                totals AS (
                    SELECT ad.GameId AS GameId, COUNT(*) AS TotalAchievements
                    FROM AchievementDefinitions ad
                    GROUP BY ad.GameId
                ),
                scrapeStatus AS (
                    SELECT GameId, LastScrapeStatus
                    FROM (
                        SELECT fo.GameId AS GameId,
                               fo.LastScrapeStatus AS LastScrapeStatus,
                               ROW_NUMBER() OVER (PARTITION BY fo.GameId ORDER BY fo.LastScrapedUtc DESC) AS rn
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE " + ActiveFriendPredicateSql + @"
                          AND fo.LastScrapeStatus IS NOT NULL
                    )
                    WHERE rn = 1
                )
                SELECT
                    g.ProviderKey AS ProviderKey,
                    g.ProviderPlatformKey AS ProviderPlatformKey,
                    g.ProviderGameId AS ProviderGameId,
                    g.ProviderGameKey AS ProviderGameKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    o.FriendCount AS FriendCount,
                    COALESCE(un.FriendsWithUnlocksCount, 0) AS FriendsWithUnlocksCount,
                    COALESCE(un.UnlockedAchievementsCount, 0) AS UnlockedAchievementsCount,
                    COALESCE(un.UniqueUnlockedAchievementsCount, 0) AS UniqueUnlockedAchievementsCount,
                    COALESCE(t.TotalAchievements, 0) AS TotalAchievements,
                    un.LastUnlockUtc AS LastUnlockUtc,
                    o.TotalPlaytimeMinutes AS TotalPlaytimeMinutes,
                    o.AveragePlaytimeMinutes AS AveragePlaytimeMinutes,
                    o.LastPlayedUtc AS LastPlayedUtc,
                    o.LastScrapedUtc AS LastScrapedUtc,
                    ss.LastScrapeStatus AS LastScrapeStatus,
                    g.IconPath AS IconPath,
                    g.CoverPath AS CoverPath
                  FROM Games g
                  INNER JOIN ownership o ON o.GameId = g.Id
                  LEFT JOIN unlocks un ON un.GameId = g.Id
                  LEFT JOIN totals t ON t.GameId = g.Id
                  LEFT JOIN scrapeStatus ss ON ss.GameId = g.Id
                  ORDER BY un.LastUnlockUtc DESC, g.GameName;").ToList();
        }

        private static List<FriendGameSummaryRow> LoadFriendGameSummaryRows(SQLiteDatabase db, Guid playniteGameId)
        {
            var playniteGameIdText = playniteGameId.ToString("D");

            return db.Load<FriendGameSummaryRow>(
                @"WITH target_games AS (
                    SELECT Id
                    FROM Games
                    WHERE PlayniteGameId IS NOT NULL
                      AND TRIM(PlayniteGameId) <> ''
                      AND LOWER(PlayniteGameId) = LOWER(?)
                ),
                ownership AS (
                    SELECT fo.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendCount,
                           COALESCE(SUM(fo.PlaytimeForeverMinutes), 0) AS TotalPlaytimeMinutes,
                           COALESCE(AVG(fo.PlaytimeForeverMinutes), 0) AS AveragePlaytimeMinutes,
                           MAX(fo.LastPlayedUtc) AS LastPlayedUtc,
                           MAX(fo.LastScrapedUtc) AS LastScrapedUtc
                    FROM FriendOwnership fo
                    INNER JOIN target_games tg ON tg.Id = fo.GameId
                    INNER JOIN Users u ON u.Id = fo.UserId
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY fo.GameId
                ),
                unlocks AS (
                    SELECT ugp.GameId AS GameId,
                           COUNT(DISTINCT u.Id) AS FriendsWithUnlocksCount,
                           COUNT(ua.Id) AS UnlockedAchievementsCount,
                           COUNT(DISTINCT ua.AchievementDefinitionId) AS UniqueUnlockedAchievementsCount,
                           MAX(ua.UnlockTimeUtc) AS LastUnlockUtc
                    FROM Users u
                    INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id
                    INNER JOIN target_games tg ON tg.Id = ugp.GameId
                    INNER JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                    WHERE " + ActiveFriendPredicateSql + @"
                    GROUP BY ugp.GameId
                ),
                totals AS (
                    SELECT ad.GameId AS GameId, COUNT(*) AS TotalAchievements
                    FROM AchievementDefinitions ad
                    INNER JOIN target_games tg ON tg.Id = ad.GameId
                    GROUP BY ad.GameId
                ),
                scrapeStatus AS (
                    SELECT GameId, LastScrapeStatus
                    FROM (
                        SELECT fo.GameId AS GameId,
                               fo.LastScrapeStatus AS LastScrapeStatus,
                               ROW_NUMBER() OVER (PARTITION BY fo.GameId ORDER BY fo.LastScrapedUtc DESC) AS rn
                        FROM FriendOwnership fo
                        INNER JOIN target_games tg ON tg.Id = fo.GameId
                        INNER JOIN Users u ON u.Id = fo.UserId
                        WHERE " + ActiveFriendPredicateSql + @"
                          AND fo.LastScrapeStatus IS NOT NULL
                    )
                    WHERE rn = 1
                )
                SELECT
                    g.ProviderKey AS ProviderKey,
                    g.ProviderPlatformKey AS ProviderPlatformKey,
                    g.ProviderGameId AS ProviderGameId,
                    g.ProviderGameKey AS ProviderGameKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    COALESCE(o.FriendCount, 0) AS FriendCount,
                    COALESCE(un.FriendsWithUnlocksCount, 0) AS FriendsWithUnlocksCount,
                    COALESCE(un.UnlockedAchievementsCount, 0) AS UnlockedAchievementsCount,
                    COALESCE(un.UniqueUnlockedAchievementsCount, 0) AS UniqueUnlockedAchievementsCount,
                    COALESCE(t.TotalAchievements, 0) AS TotalAchievements,
                    un.LastUnlockUtc AS LastUnlockUtc,
                    COALESCE(o.TotalPlaytimeMinutes, 0) AS TotalPlaytimeMinutes,
                    COALESCE(o.AveragePlaytimeMinutes, 0) AS AveragePlaytimeMinutes,
                    o.LastPlayedUtc AS LastPlayedUtc,
                    o.LastScrapedUtc AS LastScrapedUtc,
                    ss.LastScrapeStatus AS LastScrapeStatus,
                    g.IconPath AS IconPath,
                    g.CoverPath AS CoverPath
                  FROM Games g
                  INNER JOIN target_games tg ON tg.Id = g.Id
                  LEFT JOIN ownership o ON o.GameId = g.Id
                  LEFT JOIN unlocks un ON un.GameId = g.Id
                  LEFT JOIN totals t ON t.GameId = g.Id
                  LEFT JOIN scrapeStatus ss ON ss.GameId = g.Id
                  WHERE o.GameId IS NOT NULL OR un.GameId IS NOT NULL OR t.GameId IS NOT NULL
                  ORDER BY un.LastUnlockUtc DESC, g.GameName;",
                playniteGameIdText).ToList();
        }

        private static List<FriendGameLinkRow> LoadFriendGameLinkRows(SQLiteDatabase db, Guid? playniteGameId = null)
        {
            var args = new List<object>();
            var sql = new StringBuilder(
                @"SELECT DISTINCT
                    g.ProviderKey AS ProviderKey,
                    u.ExternalUserId AS ExternalUserId,
                    g.ProviderGameId AS ProviderGameId,
                    g.ProviderGameKey AS ProviderGameKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    fo.PlaytimeForeverMinutes AS PlaytimeForeverMinutes,
                    fo.LastPlayedUtc AS LastPlayedUtc
                  FROM Users u
                  INNER JOIN FriendOwnership fo ON fo.UserId = u.Id
                  INNER JOIN Games g ON g.Id = fo.GameId
                  WHERE " + ActiveFriendPredicateSql);

            if (playniteGameId.HasValue)
            {
                sql.Append(@"
                    AND g.PlayniteGameId IS NOT NULL
                    AND TRIM(g.PlayniteGameId) <> ''
                    AND LOWER(g.PlayniteGameId) = LOWER(?)");
                args.Add(playniteGameId.Value.ToString("D"));
            }

            sql.Append(@"
                  ORDER BY u.ExternalUserId, g.GameName;");

            return args.Count > 0
                ? db.Load<FriendGameLinkRow>(sql.ToString(), args.ToArray()).ToList()
                : db.Load<FriendGameLinkRow>(sql.ToString()).ToList();
        }

        // Unlocked-only: the overview snapshot's consumers (summaries, recents, projection
        // derivations, single-selection grids) never read locked rows, and materializing the
        // full definition-driven set was the dominant load cost (~6x more rows). The friend+game
        // pair comparison view loads its one game's locked rows on demand via
        // LoadFriendGameAchievementData instead.
        private static List<FriendRecentUnlockRow> LoadFriendAllAchievementRows(SQLiteDatabase db)
        {
            return LoadFriendAchievementRows(db, 0, requireUnlockTime: false, unlockedOnly: true);
        }

        // Rows are driven from the game's full AchievementDefinitions schema with the friend's
        // stored unlock rows LEFT JOINed on the stable definition key, so the friend+game pair
        // view always shows every achievement of the game with the friend's unlock state per
        // row, even when the friend's stored rows cover only a subset of the schema.
        private static List<FriendRecentUnlockRow> LoadFriendAchievementRows(
            SQLiteDatabase db,
            int recentLimit,
            bool requireUnlockTime,
            bool unlockedOnly,
            Guid? playniteGameId = null,
            IReadOnlyList<FriendCacheChange> reloadScopes = null)
        {
            var args = new List<object>();
            var sql = new StringBuilder(
                @"WITH CurrentUnlocks AS (
                    SELECT
                        cg.PlayniteGameId AS PlayniteGameId,
                        cad.ApiName AS ApiName,
                        MAX(cua.Unlocked) AS Unlocked
                    FROM Users cu
                    INNER JOIN UserGameProgress cugp ON cugp.UserId = cu.Id
                    INNER JOIN Games cg ON cg.Id = cugp.GameId
                    INNER JOIN UserAchievements cua ON cua.UserGameProgressId = cugp.Id AND cua.Unlocked = 1
                    INNER JOIN AchievementDefinitions cad ON cad.Id = cua.AchievementDefinitionId
                    WHERE cu.IsCurrentUser = 1
                      AND cg.PlayniteGameId IS NOT NULL
                      AND TRIM(cg.PlayniteGameId) <> ''
                      AND cad.ApiName IS NOT NULL
                      AND TRIM(cad.ApiName) <> ''
                    GROUP BY cg.PlayniteGameId, cad.ApiName
                )
                SELECT
                    g.ProviderKey AS ProviderKey,
                    g.ProviderGameId AS ProviderGameId,
                    g.ProviderGameKey AS ProviderGameKey,
                    g.PlayniteGameId AS PlayniteGameId,
                    g.GameName AS GameName,
                    u.ExternalUserId AS FriendExternalUserId,
                    u.DisplayName AS FriendName,
                    u.AvatarUrl AS FriendAvatarUrl,
                    u.AvatarPath AS FriendAvatarPath,
                    ad.ApiName AS ApiName,
                    ad.DisplayName AS DisplayName,
                    ad.Description AS Description,
                    ad.UnlockedIconPath AS UnlockedIconPath,
                    ad.LockedIconPath AS LockedIconPath,
                    ad.Points AS Points,
                    ad.ScaledPoints AS ScaledPoints,
                    ad.Category AS Category,
                    ad.CategoryType AS CategoryType,
                    ad.TrophyType AS TrophyType,
                    ad.Hidden AS Hidden,
                    ad.IsCapstone AS IsCapstone,
                    ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                    ad.Rarity AS Rarity,
                    ua.UnlockTimeUtc AS UnlockTimeUtc,
                    COALESCE(ua.Unlocked, 0) AS Unlocked,
                    COALESCE(cu.Unlocked, 0) AS MyUnlocked,
                    ua.ProgressNum AS ProgressNum,
                    ua.ProgressDenom AS ProgressDenom,
                    g.IconPath AS IconPath,
                    g.CoverPath AS CoverPath
                  FROM Users u
                  INNER JOIN UserGameProgress ugp ON ugp.UserId = u.Id
                  INNER JOIN Games g ON g.Id = ugp.GameId
                  INNER JOIN FriendOwnership fo ON fo.UserId = u.Id AND fo.GameId = g.Id
                  INNER JOIN AchievementDefinitions ad ON ad.GameId = g.Id
                  LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id
                      AND ua.AchievementDefinitionId = ad.Id
                  LEFT JOIN CurrentUnlocks cu ON cu.PlayniteGameId = g.PlayniteGameId
                      AND cu.ApiName = ad.ApiName
                  WHERE " + ActiveFriendPredicateSql);

            if (unlockedOnly)
            {
                sql.Append(" AND ua.Unlocked = 1");
            }

            if (requireUnlockTime)
            {
                sql.Append(" AND ua.UnlockTimeUtc IS NOT NULL");
            }

            if (playniteGameId.HasValue && playniteGameId.Value != Guid.Empty)
            {
                sql.Append(" AND g.PlayniteGameId IS NOT NULL AND TRIM(g.PlayniteGameId) <> '' AND LOWER(g.PlayniteGameId) = LOWER(?)");
                args.Add(playniteGameId.Value.ToString("D"));
            }

            if (reloadScopes != null && reloadScopes.Count > 0)
            {
                // Restrict rows to the changed (provider [, friend], game) scopes. A scope
                // without a friend covers every friend of that game (definition-level changes).
                var scopeSql = new List<string>();
                foreach (var scope in reloadScopes)
                {
                    if (scope == null)
                    {
                        continue;
                    }

                    var gameConditions = new List<string>();
                    if (!string.IsNullOrWhiteSpace(scope.ProviderGameKey))
                    {
                        gameConditions.Add("LOWER(g.ProviderGameKey) = LOWER(?)");
                    }

                    if (scope.AppId > 0)
                    {
                        gameConditions.Add("g.ProviderGameId = ?");
                    }

                    if (gameConditions.Count == 0)
                    {
                        // Not translatable to a game filter; callers exclude such kinds before
                        // requesting a patch load.
                        continue;
                    }

                    var conditions = new List<string> { "LOWER(g.ProviderKey) = LOWER(?)" };
                    args.Add(scope.ProviderKey ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(scope.ExternalUserId))
                    {
                        conditions.Add("LOWER(u.ExternalUserId) = LOWER(?)");
                        args.Add(scope.ExternalUserId);
                    }

                    if (!string.IsNullOrWhiteSpace(scope.ProviderGameKey))
                    {
                        args.Add(scope.ProviderGameKey);
                    }

                    if (scope.AppId > 0)
                    {
                        args.Add(scope.AppId);
                    }

                    conditions.Add("(" + string.Join(" OR ", gameConditions) + ")");
                    scopeSql.Add("(" + string.Join(" AND ", conditions) + ")");
                }

                sql.Append(scopeSql.Count > 0
                    // No translatable scope must return nothing, never everything.
                    ? " AND (" + string.Join(" OR ", scopeSql) + ")"
                    : " AND 1 = 0");
            }

            // Recent-unlock queries stay unlock-time ordered so LIMIT takes the newest rows.
            // Full comparison loads order by AchievementDefinitions id so each friend+game pair's
            // rows arrive in the same canonical definition order the non-friend surfaces use,
            // keeping category-order fallbacks independent of unlock recency and grid sorts.
            sql.Append(requireUnlockTime
                ? " ORDER BY ua.UnlockTimeUtc DESC, u.DisplayName, g.GameName, ad.Id"
                : " ORDER BY u.DisplayName, g.GameName, ad.Id");
            if (recentLimit > 0)
            {
                sql.Append(" LIMIT ?");
                args.Add(recentLimit);
            }

            return args.Count > 0
                ? db.Load<FriendRecentUnlockRow>(sql.ToString(), args.ToArray()).ToList()
                : db.Load<FriendRecentUnlockRow>(sql.ToString()).ToList();
        }

        private List<FriendAchievementDisplayItem> MapFriendAchievementRows(
            IEnumerable<FriendRecentUnlockRow> rows,
            Dictionary<Guid, GamePresentation> presentationCache = null)
        {
            var result = new List<FriendAchievementDisplayItem>();
            var customDataByGameId = new Dictionary<Guid, ResolvedGameCustomData>();
            foreach (var row in rows ?? Enumerable.Empty<FriendRecentUnlockRow>())
            {
                if (row == null || string.IsNullOrWhiteSpace(row.ApiName))
                {
                    continue;
                }

                var friendUnlockTimeUtc = ParseUtc(row.UnlockTimeUtc);
                var isUnlockedByFriend = row.Unlocked.GetValueOrDefault() != 0;
                var isUnlockedByMe = row.MyUnlocked.GetValueOrDefault() != 0;
                var detail = new AchievementDetail
                {
                    ApiName = row.ApiName,
                    DisplayName = row.DisplayName,
                    Description = row.Description,
                    UnlockedIconPath = MakeAbsolutePath(row.UnlockedIconPath),
                    LockedIconPath = MakeAbsolutePath(row.LockedIconPath),
                    Points = row.Points,
                    ScaledPoints = row.ScaledPoints,
                    Category = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(row.Category),
                    CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(row.CategoryType),
                    TrophyType = row.TrophyType,
                    Hidden = row.Hidden != 0,
                    IsCapstone = row.IsCapstone != 0,
                    GlobalPercentUnlocked = row.GlobalPercentUnlocked,
                    Rarity = ParseStoredRarity(row.Rarity),
                    UnlockTimeUtc = friendUnlockTimeUtc,
                    Unlocked = isUnlockedByMe,
                    ProgressNum = row.ProgressNum,
                    ProgressDenom = row.ProgressDenom
                };

                var playniteGameId = ResolveCachedPlayniteGameId(null, row.PlayniteGameId);
                var presentation = ResolveGamePresentation(playniteGameId, presentationCache);
                var item = new FriendAchievementDisplayItem
                {
                    ApiName = detail.ApiName,
                    DisplayName = detail.DisplayName,
                    Description = detail.Description,
                    UnlockedIconPath = detail.UnlockedIconPath,
                    LockedIconPath = detail.LockedIconPath,
                    PointsValue = detail.ScaledPoints ?? detail.Points,
                    CategoryLabel = detail.Category,
                    CategoryType = detail.CategoryType,
                    TrophyType = detail.TrophyType,
                    Hidden = detail.Hidden,
                    IsCapstone = detail.IsCapstone,
                    GlobalPercentUnlocked = detail.GlobalPercentUnlocked,
                    Rarity = detail.Rarity,
                    UnlockTimeUtc = friendUnlockTimeUtc,
                    Unlocked = isUnlockedByFriend,
                    UnlockedBySelf = isUnlockedByMe,
                    ProgressNum = detail.ProgressNum,
                    ProgressDenom = detail.ProgressDenom,
                    ProviderKey = row.ProviderKey,
                    AppId = (int)Math.Max(0, row.ProviderGameId ?? 0),
                    ProviderGameKey = NormalizeProviderGameKey(row.ProviderGameKey),
                    GameName = presentation.DisplayName ?? row.GameName,
                    SortingName = presentation.SortingName ?? presentation.DisplayName ?? row.GameName,
                    PlayniteGameId = playniteGameId,
                    GameIconPath = ResolveFriendGameIconPath(presentation, row.IconPath),
                    GameCoverPath = ResolveFriendGameCoverPath(presentation, row.CoverPath),
                    FriendName = row.FriendName,
                    FriendExternalUserId = row.FriendExternalUserId,
                    FriendAvatarPath = !string.IsNullOrWhiteSpace(row.FriendAvatarPath)
                        ? MakeAbsolutePath(row.FriendAvatarPath)
                        : row.FriendAvatarUrl
                };

                var customData = ResolveFriendAchievementCustomData(playniteGameId, customDataByGameId);
                ApplyCustomDataToFriendAchievement(item, customData);
                // Category rollups render the shared CategoryArtPath; the display-path
                // properties fall back to the game images when no category art exists.
                AchievementDisplayItem.ApplyCategoryPresentation(
                    item,
                    customData?.AchievementCategoryOrder,
                    customData?.AchievementCategoryImageOverrides,
                    item.CategoryLabel,
                    detail.Category,
                    playniteGameId);
                item.ApplyAppearanceSettings(AchievementDisplayItem.CreateAppearanceSettingsSnapshot(
                    _plugin?.Settings,
                    playniteGameId,
                    customData?.UseSeparateLockedIcons));

                result.Add(item);
            }

            return result;
        }

        private ResolvedGameCustomData ResolveFriendAchievementCustomData(
            Guid? playniteGameId,
            Dictionary<Guid, ResolvedGameCustomData> customDataByGameId)
        {
            if (!playniteGameId.HasValue || playniteGameId.Value == Guid.Empty)
            {
                return null;
            }

            if (customDataByGameId == null)
            {
                return GameCustomDataLookup.ResolveGameCustomData(
                    playniteGameId.Value,
                    _plugin?.Settings?.Persisted,
                    _plugin?.GameCustomDataStore);
            }

            if (!customDataByGameId.TryGetValue(playniteGameId.Value, out var customData))
            {
                customData = GameCustomDataLookup.ResolveGameCustomData(
                    playniteGameId.Value,
                    _plugin?.Settings?.Persisted,
                    _plugin?.GameCustomDataStore);
                customDataByGameId[playniteGameId.Value] = customData;
            }

            return customData;
        }

        private static void ApplyCustomDataToFriendAchievement(
            FriendAchievementDisplayItem item,
            ResolvedGameCustomData customData)
        {
            if (item == null || customData == null)
            {
                return;
            }

            var apiName = NormalizeDbText(item.ApiName);
            if (string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            var manualCapstoneApiName = NormalizeDbText(customData.ManualCapstoneApiName);
            if (!string.IsNullOrWhiteSpace(manualCapstoneApiName))
            {
                item.IsCapstone = string.Equals(apiName, manualCapstoneApiName, StringComparison.OrdinalIgnoreCase);
            }

            if (customData.AchievementCategoryOverrides != null &&
                customData.AchievementCategoryOverrides.TryGetValue(apiName, out var categoryOverride) &&
                !string.IsNullOrWhiteSpace(categoryOverride))
            {
                item.CategoryLabel = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(categoryOverride);
            }

            if (customData.AchievementCategoryTypeOverrides != null &&
                customData.AchievementCategoryTypeOverrides.TryGetValue(apiName, out var categoryTypeOverride) &&
                !string.IsNullOrWhiteSpace(categoryTypeOverride))
            {
                item.CategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(categoryTypeOverride);
            }

            item.AchievementNote = customData.AchievementNotes != null &&
                                   customData.AchievementNotes.TryGetValue(apiName, out var note)
                ? note
                : null;
        }

        private static string NormalizeMatchText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            // Fold diacritics (NFD decompose + drop combining marks) and collapse internal
            // whitespace so accent-only differences (e.g. "Épée" vs "Epee") compare equal. This is
            // language neutral: it removes accents, it does not translate. Both the friend row and
            // the definition text pass through here, so matching stays symmetric.
            var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            var lastWasWhitespace = false;
            foreach (var ch in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsWhiteSpace(ch))
                {
                    if (builder.Length > 0)
                    {
                        lastWasWhitespace = true;
                    }
                    continue;
                }

                if (lastWasWhitespace)
                {
                    builder.Append(' ');
                    lastWasWhitespace = false;
                }

                builder.Append(ch);
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static string ExtractIconFilename(string iconUrl)
        {
            if (string.IsNullOrWhiteSpace(iconUrl))
            {
                return null;
            }

            var queryIndex = iconUrl.IndexOf('?');
            if (queryIndex > 0)
            {
                iconUrl = iconUrl.Substring(0, queryIndex);
            }

            var lastSlash = iconUrl.LastIndexOf('/');
            if (lastSlash < 0 || lastSlash >= iconUrl.Length - 1)
            {
                lastSlash = iconUrl.LastIndexOf('\\');
            }

            if (lastSlash < 0 || lastSlash >= iconUrl.Length - 1)
            {
                return null;
            }

            return iconUrl.Substring(lastSlash + 1);
        }

        // Returns the ApiName renames applied while upserting definitions (old -> new), so the
        // caller can rewrite ApiName-keyed per-game custom data (notes, order, filters, overrides).
        public Dictionary<string, string> SaveCurrentUserGameData(string key, GameAchievementData data)
        {
            var renamedApiNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(key))
            {
                return renamedApiNames;
            }

            var cacheKey = key.Trim();
            var payload = data ?? new GameAchievementData();

            if (payload.LastUpdatedUtc == default(DateTime))
            {
                payload.LastUpdatedUtc = DateTime.UtcNow;
            }
            payload.LastUpdatedUtc = DateTimeUtilities.AsUtcKind(payload.LastUpdatedUtc);

            if (payload.PlayniteGameId == null && Guid.TryParse(cacheKey, out var parsedId))
            {
                payload.PlayniteGameId = parsedId;
            }

            var providerKey = NormalizeProviderKey(payload.ProviderKey);
            var resolvedUser = ResolveCurrentUser(providerKey);
            var nowIso = ToIso(DateTime.UtcNow);
            var updatedIso = ToIso(payload.LastUpdatedUtc);

            var achievements = payload.Achievements ?? new List<AchievementDetail>();
            NormalizeIncomingAchievements(achievements);
            var unlockedCount = achievements.Count(IsUnlocked);
            var totalCount = achievements.Count;

            WithDb(db =>
            {
                db.RunTransaction(() =>
                {
                    // If creating an Unmapped stub, check for existing real provider data and use that instead
                    string effectiveProviderKey = providerKey;
                    ResolvedUser effectiveUser = resolvedUser;
                    if (string.Equals(providerKey, "Unmapped", StringComparison.OrdinalIgnoreCase))
                    {
                        var existingRealProvider = FindExistingRealProviderGame(db, cacheKey);
                        if (existingRealProvider != null)
                        {
                            effectiveProviderKey = existingRealProvider.ProviderKey;
                            effectiveUser = ResolveCurrentUser(effectiveProviderKey);
                        }
                    }

                    var userId = UpsertCurrentUser(db, effectiveUser, nowIso);
                    var gameId = UpsertGame(db, effectiveProviderKey, payload, nowIso, updatedIso);
                    var existingProgress = LoadUserGameProgress(db, userId, gameId, cacheKey);
                    if (existingProgress == null)
                    {
                        // Reuse stale Exophase proxy progress when a native provider reclaims the same game.
                        existingProgress = LoadReclaimableExophaseProgress(db, cacheKey, effectiveProviderKey);
                    }
                    var previousGameId = existingProgress?.GameId;

                    // Use payload.HasAchievements directly - callers are responsible for setting it correctly
                    // Default is true; only false when a scan explicitly finds no achievements
                    var hasAchievements = payload.HasAchievements;

                    var userProgressId = UpsertUserGameProgress(
                        db,
                        existingProgress,
                        userId,
                        gameId,
                        cacheKey,
                        hasAchievements,
                        unlockedCount,
                        totalCount,
                        updatedIso,
                        nowIso);

                    if (previousGameId.HasValue && previousGameId.Value != gameId)
                    {
                        db.ExecuteNonQuery(
                            @"DELETE FROM Games
                              WHERE Id = ?
                                AND NOT EXISTS (SELECT 1 FROM UserGameProgress WHERE GameId = Games.Id);",
                            previousGameId.Value);
                    }

                    var definitionIds = UpsertAchievementDefinitions(
                        db,
                        gameId,
                        achievements,
                        nowIso,
                        updatedIso,
                        renamedApiNames);

                    var existingRows = db.Load<UserAchievementRow>(
                        @"SELECT Id, UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc
                          FROM UserAchievements
                          WHERE UserGameProgressId = ?;",
                        userProgressId)
                        .ToDictionary(a => a.AchievementDefinitionId);

                    var desiredRows = new Dictionary<long, AchievementDetail>();
                    foreach (var achievement in achievements)
                    {
                        if (achievement == null || string.IsNullOrWhiteSpace(achievement.ApiName))
                        {
                            continue;
                        }

                        if (!definitionIds.TryGetValue(achievement.ApiName.Trim(), out var definitionId))
                        {
                            continue;
                        }

                        desiredRows[definitionId] = achievement;
                    }

                    foreach (var desired in desiredRows)
                    {
                        var definitionId = desired.Key;
                        var achievement = desired.Value;

                        var unlockTime = NormalizeUnlockTime(achievement.UnlockTimeUtc);
                        var unlocked = IsUnlocked(achievement) ? 1L : 0L;
                        var unlockIso = unlocked != 0 && unlockTime.HasValue ? ToIso(unlockTime.Value) : null;
                        var progressNum = achievement.ProgressNum;
                        var progressDenom = achievement.ProgressDenom;

                        if (!existingRows.TryGetValue(definitionId, out var existing))
                        {
                            db.ExecuteNonQuery(
                                @"INSERT INTO UserAchievements
                                    (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                                  VALUES
                                    (?, ?, ?, ?, ?, ?, ?, ?);",
                                userProgressId,
                                definitionId,
                                unlocked,
                                DbValue(unlockIso),
                                DbParam(progressNum),
                                DbParam(progressDenom),
                                updatedIso,
                                nowIso);
                            continue;
                        }

                        existingRows.Remove(definitionId);

                        var existingUnlockIso = NormalizeStoredIso(existing.UnlockTimeUtc);
                        var changed = existing.Unlocked != unlocked ||
                                      !NullableEquals(existingUnlockIso, unlockIso) ||
                                      existing.ProgressNum != progressNum ||
                                      existing.ProgressDenom != progressDenom;

                        if (!changed)
                        {
                            continue;
                        }

                        db.ExecuteNonQuery(
                            @"UPDATE UserAchievements
                              SET Unlocked = ?,
                                  UnlockTimeUtc = ?,
                                  ProgressNum = ?,
                                  ProgressDenom = ?,
                                  LastUpdatedUtc = ?
                              WHERE Id = ?;",
                            unlocked,
                            DbValue(unlockIso),
                            DbParam(progressNum),
                            DbParam(progressDenom),
                            updatedIso,
                            existing.Id);
                    }

                    foreach (var stale in existingRows.Values)
                    {
                        db.ExecuteNonQuery(
                            @"DELETE FROM UserAchievements
                              WHERE Id = ?;",
                            stale.Id);
                    }

                    // Deduplication: When saving real provider data, remove Unmapped stubs for the same game
                    if (!string.Equals(effectiveProviderKey, "Unmapped", StringComparison.OrdinalIgnoreCase))
                    {
                        RemoveUnmappedStubsForGame(db, cacheKey, userProgressId);
                    }
                });
            });

            return renamedApiNames;
        }

        private GameRow FindExistingRealProviderGame(SQLiteDatabase db, string cacheKey)
        {
            // Find a Game entry for this CacheKey from a non-Unmapped provider
            return db.Load<GameRow>(
                                @"SELECT g.Id, g.ProviderKey, g.ProviderPlatformKey, g.ProviderGameId, g.ProviderGameKey, g.PlayniteGameId, g.GameName, g.LibrarySourceName, g.FirstSeenUtc, g.LastUpdatedUtc
                  FROM Games g
                  WHERE g.PlayniteGameId = ?
                    AND g.ProviderKey <> 'Unmapped'
                    AND g.ProviderKey IS NOT NULL
                  ORDER BY g.LastUpdatedUtc DESC
                  LIMIT 1;",
                cacheKey).FirstOrDefault();
        }

        private void RemoveUnmappedStubsForGame(
            SQLiteDatabase db,
            string cacheKey,
            long realProgressId)
        {
            // Find Unmapped UserGameProgress entries for the same CacheKey
            var unmappedProgress = db.Load<UserGameProgressRow>(
                @"SELECT ugp.Id, ugp.UserId, ugp.GameId, ugp.CacheKey, ugp.HasAchievements,
                         ugp.AchievementsUnlocked, ugp.TotalAchievements,
                         ugp.LastUpdatedUtc, ugp.CreatedUtc, ugp.UpdatedUtc
                  FROM UserGameProgress ugp
                  INNER JOIN Users u ON ugp.UserId = u.Id
                  WHERE ugp.CacheKey = ?
                    AND u.ProviderKey = 'Unmapped'
                    AND ugp.Id <> ?;",
                cacheKey,
                realProgressId).ToList();

            if (unmappedProgress.Count == 0)
            {
                return;
            }

            foreach (var stub in unmappedProgress)
            {
                // Delete UserAchievements for the stub
                db.ExecuteNonQuery(
                    @"DELETE FROM UserAchievements
                      WHERE UserGameProgressId = ?;",
                    stub.Id);

                // Delete the stub's UserGameProgress
                db.ExecuteNonQuery(
                    @"DELETE FROM UserGameProgress
                      WHERE Id = ?;",
                    stub.Id);

                // Delete the stub's Game entry if no other progress references it
                db.ExecuteNonQuery(
                    @"DELETE FROM Games
                      WHERE Id = ?
                        AND NOT EXISTS (SELECT 1 FROM UserGameProgress WHERE GameId = Games.Id);",
                    stub.GameId);

                // Delete AchievementDefinitions for the stub's game if no other progress references them
                db.ExecuteNonQuery(
                    @"DELETE FROM AchievementDefinitions
                      WHERE GameId = ?
                        AND NOT EXISTS (
                            SELECT 1 FROM UserAchievements ua
                            INNER JOIN UserGameProgress ugp ON ua.UserGameProgressId = ugp.Id
                            WHERE ua.AchievementDefinitionId = AchievementDefinitions.Id
                        );",
                    stub.GameId);

                _logger?.Debug($"Removed Unmapped stub for CacheKey={cacheKey}, stubProgressId={stub.Id}, " +
                              $"replaced by realProgressId={realProgressId}");
            }
        }

        public void ClearCacheData()
        {
            // Close the read-only connection first so it does not hold a handle to the file
            // being deleted below. Done before the _sync block to avoid nesting the two locks.
            DisposeReadConnection();

            lock (_sync)
            {
                // Close the database connection
                if (_db is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch { }
                }
                _db = null;
                _initialized = false;
                _cachedCurrentUsersByProvider.Clear();

                // Delete the database file and WAL-related files
                try
                {
                    var dbPath = DatabasePath;
                    var filesToDelete = new[]
                    {
                        dbPath,
                        dbPath + "-wal",
                        dbPath + "-shm"
                    };

                    foreach (var file in filesToDelete)
                    {
                        if (File.Exists(file))
                        {
                            File.Delete(file);
                            _logger?.Info($"Deleted database file: {file}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed to delete database files: {DatabasePath}");
                }
            }
        }

        public void RemoveGameData(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            var playniteGameIdText = playniteGameId.ToString();
            WithDb(db =>
            {
                db.RunTransaction(() =>
                {
                    db.ExecuteNonQuery(
                        @"DELETE FROM UserGameProgress
                          WHERE CacheKey = ?;",
                        playniteGameIdText);

                    db.ExecuteNonQuery(
                        @"DELETE FROM Games
                          WHERE PlayniteGameId = ?;",
                        playniteGameIdText);
                });
            });
        }

        internal static class AchievementFilterKinds
        {
            public const string Filtered = "Filtered";
            public const string SummaryFiltered = "SummaryFiltered";
        }

        private sealed class AchievementFilterRow
        {
            public string PlayniteGameId { get; set; }
            public string ApiName { get; set; }
            public string Kind { get; set; }
        }

        private static string AchievementFilterEntryKey(string apiName, string kind)
        {
            return (apiName ?? string.Empty).Trim() + "\n" + (kind ?? string.Empty).Trim();
        }

        private static HashSet<string> BuildAchievementFilterEntryKeys(
            IReadOnlyList<(string ApiName, string Kind)> entries)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries ?? Array.Empty<(string, string)>())
            {
                if (string.IsNullOrWhiteSpace(entry.ApiName) || string.IsNullOrWhiteSpace(entry.Kind))
                {
                    continue;
                }

                keys.Add(AchievementFilterEntryKey(entry.ApiName, entry.Kind));
            }

            return keys;
        }

        /// <summary>
        /// Replaces one game's rows in the AchievementFilters mirror (see the schema comment:
        /// derived from per-game custom data, which the summary queries cannot join to
        /// directly). Compares before writing so unchanged saves stay WAL-silent. Returns true
        /// when rows changed.
        /// </summary>
        public bool ReplaceAchievementFilters(
            Guid playniteGameId,
            IReadOnlyList<(string ApiName, string Kind)> entries)
        {
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            var gameIdText = playniteGameId.ToString();
            var desired = BuildAchievementFilterEntryKeys(entries);
            return WithDb(db =>
            {
                var existing = db.Load<AchievementFilterRow>(
                        @"SELECT PlayniteGameId, ApiName, Kind
                          FROM AchievementFilters
                          WHERE PlayniteGameId = ?;",
                        gameIdText)
                    .Select(row => AchievementFilterEntryKey(row.ApiName, row.Kind))
                    .ToList();

                if (existing.Count == desired.Count && desired.SetEquals(existing))
                {
                    return false;
                }

                var nowIso = ToIso(DateTime.UtcNow);
                db.RunTransaction(() =>
                {
                    db.ExecuteNonQuery(
                        "DELETE FROM AchievementFilters WHERE PlayniteGameId = ?;",
                        gameIdText);
                    InsertAchievementFilterKeys(db, gameIdText, desired, nowIso);
                });

                return true;
            });
        }

        /// <summary>
        /// Reconciles the whole AchievementFilters mirror against the desired per-game entry
        /// sets (games absent from the map lose their rows). Diffs first and returns the number
        /// of changed games; 0 means no write at all.
        /// </summary>
        public int ResyncAllAchievementFilters(
            IReadOnlyDictionary<Guid, IReadOnlyList<(string ApiName, string Kind)>> entriesByGameId)
        {
            var desiredByGame = new Dictionary<Guid, HashSet<string>>();
            foreach (var pair in entriesByGameId ?? new Dictionary<Guid, IReadOnlyList<(string, string)>>())
            {
                if (pair.Key == Guid.Empty)
                {
                    continue;
                }

                var keys = BuildAchievementFilterEntryKeys(pair.Value);
                if (keys.Count > 0)
                {
                    desiredByGame[pair.Key] = keys;
                }
            }

            return WithDb(db =>
            {
                var existingByGameText = db.Load<AchievementFilterRow>(
                        "SELECT PlayniteGameId, ApiName, Kind FROM AchievementFilters;")
                    .GroupBy(row => row.PlayniteGameId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => new HashSet<string>(
                            group.Select(row => AchievementFilterEntryKey(row.ApiName, row.Kind)),
                            StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase);

                // Stale rows: stored games no longer carrying filters (or unparsable ids).
                var textsToDelete = new List<string>();
                foreach (var pair in existingByGameText)
                {
                    if (!Guid.TryParse(pair.Key, out var gameId) ||
                        !desiredByGame.TryGetValue(gameId, out var desired) ||
                        !desired.SetEquals(pair.Value))
                    {
                        textsToDelete.Add(pair.Key);
                    }
                }

                // New or changed games to (re)write.
                var gamesToWrite = new List<Guid>();
                foreach (var pair in desiredByGame)
                {
                    if (!existingByGameText.TryGetValue(pair.Key.ToString(), out var existing) ||
                        !pair.Value.SetEquals(existing))
                    {
                        gamesToWrite.Add(pair.Key);
                    }
                }

                if (textsToDelete.Count == 0 && gamesToWrite.Count == 0)
                {
                    return 0;
                }

                var nowIso = ToIso(DateTime.UtcNow);
                db.RunTransaction(() =>
                {
                    foreach (var gameIdText in textsToDelete)
                    {
                        db.ExecuteNonQuery(
                            "DELETE FROM AchievementFilters WHERE PlayniteGameId = ?;",
                            gameIdText);
                    }

                    foreach (var gameId in gamesToWrite)
                    {
                        var gameIdText = gameId.ToString();
                        db.ExecuteNonQuery(
                            "DELETE FROM AchievementFilters WHERE PlayniteGameId = ?;",
                            gameIdText);
                        InsertAchievementFilterKeys(db, gameIdText, desiredByGame[gameId], nowIso);
                    }
                });

                var changedGames = new HashSet<string>(textsToDelete, StringComparer.OrdinalIgnoreCase);
                foreach (var gameId in gamesToWrite)
                {
                    changedGames.Add(gameId.ToString());
                }

                return changedGames.Count;
            });
        }

        private static void InsertAchievementFilterKeys(
            SQLiteDatabase db,
            string gameIdText,
            IEnumerable<string> entryKeys,
            string nowIso)
        {
            foreach (var key in entryKeys)
            {
                var separator = key.IndexOf('\n');
                if (separator <= 0 || separator >= key.Length - 1)
                {
                    continue;
                }

                db.ExecuteNonQuery(
                    @"INSERT OR IGNORE INTO AchievementFilters (PlayniteGameId, ApiName, Kind, CreatedUtc)
                      VALUES (?, ?, ?, ?);",
                    gameIdText,
                    key.Substring(0, separator),
                    key.Substring(separator + 1),
                    nowIso);
            }
        }

        private void EnsureInitializedLocked()
        {
            if (_initialized)
            {
                return;
            }

            var parent = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }

            try
            {
                _db = new SQLiteDatabase(
                    DatabasePath,
                    SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                    SQLiteOpenOptions.SQLITE_OPEN_CREATE |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX);

                _db.EnableStatementsCache = true;
                _schemaManager.EnsureSchema(_db);
                EnsureLegacyAchievementPercentNormalization(_db);
                _initialized = true;
            }
            catch
            {
                if (_db is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                    }
                }

                _db = null;
                _initialized = false;
                throw;
            }
        }

        private void EnsureLegacyAchievementPercentNormalization(SQLiteDatabase db)
        {
            var status = GetMetadataValue(db, PercentNormalizationMetadataKey);
            if (string.Equals(status, "done", StringComparison.Ordinal))
            {
                return;
            }

            db.RunTransaction(() =>
            {
                var rows = db.Load<AchievementPercentNormalizationRow>(
                    @"SELECT
                        ad.Id AS Id,
                        ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                        ad.Rarity AS Rarity
                      FROM AchievementDefinitions ad;").ToList();

                for (var i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    if (row == null)
                    {
                        continue;
                    }

                    var normalizedPercent = NormalizeStoredPercent(
                        row.GlobalPercentUnlocked,
                        convertLegacyRatio: true);
                    var storedRarity = ParseStoredRarity(row.Rarity);
                    var resolvedRarity = normalizedPercent.HasValue
                        ? PercentRarityHelper.GetRarityTier(normalizedPercent.Value)
                        : storedRarity;

                    if (row.GlobalPercentUnlocked == normalizedPercent &&
                        storedRarity == resolvedRarity)
                    {
                        continue;
                    }

                    db.ExecuteNonQuery(
                        @"UPDATE AchievementDefinitions
                          SET GlobalPercentUnlocked = ?,
                              Rarity = ?
                          WHERE Id = ?;",
                        DbParam(normalizedPercent),
                        resolvedRarity.ToString(),
                        row.Id);
                }

                SetMetadataValue(db, PercentNormalizationMetadataKey, "done");
            });
        }

        internal T WithDb<T>(Func<SQLiteDatabase, T> action)
        {
            lock (_sync)
            {
                EnsureInitializedLocked();
                return action(_db);
            }
        }

        private void WithDb(Action<SQLiteDatabase> action)
        {
            lock (_sync)
            {
                EnsureInitializedLocked();
                action(_db);
            }
        }

        // Runs a read-only query on the dedicated read connection, off the _sync lock. Falls
        // back to default(T) if the connection cannot be opened or the query fails (e.g. the
        // database file is being cleared concurrently) so read callers degrade to empty results
        // rather than throwing.
        internal T WithReadDb<T>(Func<SQLiteDatabase, T> action)
        {
            if (action == null)
            {
                return default;
            }

            // Ensure the read-write connection has created the database file, WAL and shared
            // memory before the read-only connection opens against them. Done outside _readSync
            // so the two locks are never held simultaneously.
            EnsureInitialized();

            lock (_readSync)
            {
                try
                {
                    EnsureReadConnectionLocked();
                    return action(_readDb);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Read-only cache query failed; returning empty result.");
                    return default;
                }
            }
        }

        private void EnsureReadConnectionLocked()
        {
            if (_readInitialized && _readDb != null)
            {
                return;
            }

            _readDb = new SQLiteDatabase(
                DatabasePath,
                SQLiteOpenOptions.SQLITE_OPEN_READONLY |
                SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX);
            _readDb.EnableStatementsCache = true;
            _readDb.BusyTimeout = ReadConnectionBusyTimeoutMs;
            _readInitialized = true;
        }

        private void DisposeReadConnection()
        {
            lock (_readSync)
            {
                if (_readDb is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                    }
                }

                _readDb = null;
                _readInitialized = false;
            }
        }

        private static string GetMetadataValue(SQLiteDatabase db, string key)
        {
            if (db == null || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            var row = db.Load<CacheMetadataRow>(
                "SELECT Key, Value FROM CacheMetadata WHERE Key = ? LIMIT 1;",
                key.Trim()).FirstOrDefault();
            return row?.Value;
        }

        private static void SetMetadataValue(SQLiteDatabase db, string key, string value)
        {
            if (db == null || string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            db.ExecuteNonQuery(
                "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                key.Trim(),
                value ?? string.Empty);
        }

        private long UpsertCurrentUser(SQLiteDatabase db, ResolvedUser user, string nowIso)
        {
            if (_cachedCurrentUsersByProvider.TryGetValue(user.ProviderKey, out var cachedUser) &&
                string.Equals(cachedUser.ExternalUserId, user.ExternalUserId, StringComparison.OrdinalIgnoreCase))
            {
                var cachedIdExists = db.ExecuteScalar<long>(
                    @"SELECT Id
                      FROM Users
                      WHERE Id = ?
                      LIMIT 1;",
                    new object[] { cachedUser.UserId });
                if (cachedIdExists <= 0)
                {
                    _cachedCurrentUsersByProvider.Remove(user.ProviderKey);
                }
                else
                {
                    db.ExecuteNonQuery(
                        @"UPDATE Users
                          SET DisplayName = ?,
                              FriendSource = ?,
                              IsCurrentUser = 1,
                              UpdatedUtc = ?
                          WHERE Id = ?;",
                        DbValue(user.DisplayName),
                        DbValue(user.FriendSource),
                        nowIso,
                        cachedUser.UserId);
                    return cachedUser.UserId;
                }
            }

            db.ExecuteNonQuery(
                @"UPDATE Users
                  SET IsCurrentUser = 0,
                      UpdatedUtc = ?
                  WHERE ProviderKey = ?
                    AND IsCurrentUser = 1;",
                nowIso,
                user.ProviderKey);

            db.ExecuteNonQuery(
                @"INSERT OR IGNORE INTO Users
                    (ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, CreatedUtc, UpdatedUtc)
                  VALUES
                    (?, ?, ?, 0, ?, ?, ?);",
                user.ProviderKey,
                user.ExternalUserId,
                DbValue(user.DisplayName),
                DbValue(user.FriendSource),
                nowIso,
                nowIso);

            var userId = db.ExecuteScalar<long>(
                @"SELECT Id
                  FROM Users
                  WHERE ProviderKey = ?
                    AND ExternalUserId = ?
                  LIMIT 1;",
                user.ProviderKey,
                user.ExternalUserId);

            if (userId <= 0)
            {
                db.ExecuteNonQuery(
                    @"INSERT INTO Users
                        (ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, CreatedUtc, UpdatedUtc)
                      VALUES
                        (?, ?, ?, 1, ?, ?, ?);",
                    user.ProviderKey,
                    user.ExternalUserId,
                    DbValue(user.DisplayName),
                    DbValue(user.FriendSource),
                    nowIso,
                    nowIso);
                userId = db.ExecuteScalar<long>("SELECT last_insert_rowid();");
            }

            db.ExecuteNonQuery(
                @"UPDATE Users
                  SET DisplayName = ?,
                      FriendSource = ?,
                      IsCurrentUser = 1,
                      UpdatedUtc = ?
                  WHERE Id = ?;",
                DbValue(user.DisplayName),
                DbValue(user.FriendSource),
                nowIso,
                userId);

            _cachedCurrentUsersByProvider[user.ProviderKey] = new CachedCurrentUserState
            {
                ExternalUserId = user.ExternalUserId,
                UserId = userId
            };

            return userId;
        }

        private long UpsertGame(SQLiteDatabase db, string providerKey, GameAchievementData data, string nowIso, string updatedIso)
        {
            var playniteGameId = data.PlayniteGameId?.ToString();
            long? providerGameId = data.AppId > 0 ? data.AppId : (long?)null;
            var providerGameKey = NormalizeProviderGameKey(data.ProviderGameKey);
            var isRetroAchievements = SqlNadoCacheBehavior.IsRetroAchievementsProvider(providerKey);

            GameRow game = null;
            if (!string.IsNullOrWhiteSpace(playniteGameId))
            {
                game = db.Load<GameRow>(
                                        @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                      FROM Games
                      WHERE ProviderKey = ? AND PlayniteGameId = ?
                      LIMIT 1;",
                    providerKey,
                    playniteGameId).FirstOrDefault();
            }

            if (game == null &&
                SqlNadoCacheBehavior.ShouldFallbackToProviderGameIdLookup(providerKey, playniteGameId, providerGameId))
            {
                game = db.Load<GameRow>(
                                        @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                      FROM Games
                      WHERE ProviderKey = ? AND ProviderGameId = ?
                      LIMIT 1;",
                    providerKey,
                    providerGameId.Value).FirstOrDefault();
            }

            if (game == null &&
                !string.IsNullOrWhiteSpace(providerGameKey) &&
                !string.IsNullOrWhiteSpace(playniteGameId))
            {
                game = LoadProviderOnlyGameByAppId(db, providerKey, 0, providerGameKey);
            }

            if (game == null &&
                providerGameId.HasValue &&
                providerGameId.Value > 0 &&
                !string.IsNullOrWhiteSpace(playniteGameId))
            {
                game = LoadProviderOnlyGameByAppId(db, providerKey, (int)providerGameId.Value);
            }

            if (game == null)
            {
                if (isRetroAchievements && providerGameId.HasValue && !string.IsNullOrWhiteSpace(playniteGameId))
                {
                    var mirroredRows = db.Load<GameRow>(
                                                @"SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                          FROM Games
                          WHERE ProviderKey = ?
                            AND ProviderGameId = ?
                            AND (PlayniteGameId IS NULL OR PlayniteGameId <> ?)
                          ORDER BY LastUpdatedUtc DESC, Id DESC
                          LIMIT 5;",
                        providerKey,
                        providerGameId.Value,
                        playniteGameId).ToList();

                    if (mirroredRows.Count > 0)
                    {
                        var existingIds = string.Join(
                            ",",
                            mirroredRows.Select(a => string.IsNullOrWhiteSpace(a?.PlayniteGameId) ? "(null)" : a.PlayniteGameId));
                        _logger?.Debug(
                            $"[Cache][RA] Mirroring providerGameId={providerGameId.Value} to playniteGameId={playniteGameId}; " +
                            $"existingPlayniteGameIds={existingIds}");
                    }
                }

                db.ExecuteNonQuery(
                    @"INSERT INTO Games
                        (ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc)
                      VALUES
                        (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    providerKey,
                    DbValue(data.ProviderPlatformKey),
                    DbParam(providerGameId),
                    DbValue(providerGameKey),
                    DbValue(playniteGameId),
                    DbValue(data.GameName),
                    DbValue(data.LibrarySourceName),
                    nowIso,
                    updatedIso);
                return db.ExecuteScalar<long>("SELECT last_insert_rowid();");
            }

            db.ExecuteNonQuery(
                @"UPDATE Games
                  SET ProviderPlatformKey = ?,
                      ProviderGameId = ?,
                      ProviderGameKey = ?,
                      PlayniteGameId = ?,
                      GameName = ?,
                      LibrarySourceName = ?,
                      LastUpdatedUtc = ?
                  WHERE Id = ?;",
                DbValue(data.ProviderPlatformKey),
                DbParam(providerGameId),
                DbValue(providerGameKey),
                DbValue(playniteGameId),
                DbValue(data.GameName),
                DbValue(data.LibrarySourceName),
                updatedIso,
                game.Id);

            return game.Id;
        }

        private UserGameProgressRow LoadUserGameProgress(
            SQLiteDatabase db,
            long userId,
            long gameId,
            string cacheKey)
        {
            var existing = db.Load<UserGameProgressRow>(
                @"SELECT Id, UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc
                  FROM UserGameProgress
                  WHERE UserId = ? AND CacheKey = ?
                  LIMIT 1;",
                userId,
                cacheKey).FirstOrDefault();

            if (existing != null)
            {
                return existing;
            }

            return db.Load<UserGameProgressRow>(
                @"SELECT Id, UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc
                  FROM UserGameProgress
                  WHERE UserId = ? AND GameId = ?
                  LIMIT 1;",
                userId,
                gameId).FirstOrDefault();
        }

        private UserGameProgressRow LoadReclaimableExophaseProgress(
            SQLiteDatabase db,
            string cacheKey,
            string incomingProviderKey)
        {
            if (string.IsNullOrWhiteSpace(cacheKey) ||
                !SqlNadoCacheBehavior.CanReclaimExophaseProxy(incomingProviderKey))
            {
                return null;
            }

            return db.Load<UserGameProgressRow>(
                @"SELECT ugp.Id, ugp.UserId, ugp.GameId, ugp.CacheKey, ugp.HasAchievements,
                         ugp.AchievementsUnlocked, ugp.TotalAchievements,
                         ugp.LastUpdatedUtc, ugp.CreatedUtc, ugp.UpdatedUtc
                  FROM UserGameProgress ugp
                  INNER JOIN Users u ON ugp.UserId = u.Id
                  INNER JOIN Games g ON ugp.GameId = g.Id
                  WHERE ugp.CacheKey = ?
                    AND u.IsCurrentUser = 1
                    AND g.ProviderKey = 'Exophase'
                    AND (
                        g.ProviderPlatformKey IS NULL
                        OR TRIM(g.ProviderPlatformKey) = ''
                        OR g.ProviderPlatformKey = 'Unknown'
                        OR g.ProviderPlatformKey = ?
                    )
                  ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                  LIMIT 1;",
                cacheKey,
                incomingProviderKey.Trim()).FirstOrDefault();
        }

        private long UpsertUserGameProgress(
            SQLiteDatabase db,
            UserGameProgressRow existing,
            long userId,
            long gameId,
            string cacheKey,
            bool hasAchievements,
            int achievementsUnlocked,
            int totalAchievements,
            string updatedIso,
            string nowIso)
        {
            if (existing == null)
            {
                db.ExecuteNonQuery(
                    @"INSERT INTO UserGameProgress
                        (UserId, GameId, CacheKey, HasAchievements, AchievementsUnlocked, TotalAchievements, LastUpdatedUtc, CreatedUtc, UpdatedUtc)
                      VALUES
                        (?, ?, ?, ?, ?, ?, ?, ?, ?);",
                    userId,
                    gameId,
                    cacheKey,
                    hasAchievements ? 1 : 0,
                    achievementsUnlocked,
                    totalAchievements,
                    updatedIso,
                    nowIso,
                    nowIso);
                return db.ExecuteScalar<long>("SELECT last_insert_rowid();");
            }

            db.ExecuteNonQuery(
                @"UPDATE UserGameProgress
                  SET GameId = ?,
                      CacheKey = ?,
                      HasAchievements = ?,
                      AchievementsUnlocked = ?,
                      TotalAchievements = ?,
                      LastUpdatedUtc = ?,
                      UpdatedUtc = ?
                  WHERE Id = ?;",
                gameId,
                cacheKey,
                hasAchievements ? 1 : 0,
                achievementsUnlocked,
                totalAchievements,
                updatedIso,
                nowIso,
                existing.Id);

            return existing.Id;
        }

        // Fills Category/CategoryType on existing definition rows that still hold the Default
        // placeholder, matched by ApiName. Used for mapped friend proxy rows: the current user's
        // schema stays canonical, so a friend definition fetch only supplies category data that
        // is missing and never rewrites rows a user scan already categorized.
        private int BackfillDefinitionCategories(
            SQLiteDatabase db,
            long gameId,
            IReadOnlyList<AchievementDetail> achievements,
            string updatedIso)
        {
            var existingDefinitions = db.Load<AchievementDefinitionRow>(
                    @"SELECT Id, GameId, ApiName, Category, CategoryType
                      FROM AchievementDefinitions
                      WHERE GameId = ?;",
                    gameId)
                .Where(a => a != null)
                .Select(a => (a.Id, a.ApiName, a.Category, a.CategoryType))
                .ToList();

            var backfills = SqlNadoCacheBehavior.ComputeDefinitionCategoryBackfills(
                existingDefinitions,
                achievements);
            foreach (var backfill in backfills)
            {
                db.ExecuteNonQuery(
                    @"UPDATE AchievementDefinitions
                      SET Category = ?,
                          CategoryType = ?,
                          UpdatedUtc = ?
                      WHERE Id = ?;",
                    DbValue(backfill.Category),
                    DbValue(backfill.CategoryType),
                    updatedIso,
                    backfill.DefinitionId);
            }

            return backfills.Count;
        }

        private Dictionary<string, long> UpsertAchievementDefinitions(
            SQLiteDatabase db,
            long gameId,
            IEnumerable<AchievementDetail> achievements,
            string nowIso,
            string updatedIso,
            IDictionary<string, string> renameCollector = null,
            bool authoritativeSource = false)
        {
            var idsByApiName = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (achievements == null)
            {
                db.ExecuteNonQuery(
                    @"DELETE FROM UserAchievements
                      WHERE AchievementDefinitionId IN (
                          SELECT Id FROM AchievementDefinitions WHERE GameId = ?
                      );",
                    gameId);
                db.ExecuteNonQuery(
                    @"DELETE FROM AchievementDefinitions
                      WHERE GameId = ?;",
                    gameId);
                return idsByApiName;
            }

            var incomingAchievements = achievements
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.ApiName))
                .ToList();

            var desiredApiNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var existingByApiName = db.Load<AchievementDefinitionRow>(
                    @"SELECT Id, GameId, ApiName, DisplayName, Description, UnlockedIconPath, LockedIconPath, Points, ScaledPoints, Category, CategoryType, TrophyType, Hidden, IsCapstone, GlobalPercentUnlocked, Rarity, ProgressMax, CreatedUtc, UpdatedUtc
                      FROM AchievementDefinitions
                      WHERE GameId = ?;",
                    gameId)
                .Where(a => !string.IsNullOrWhiteSpace(a?.ApiName))
                .ToDictionary(a => a.ApiName.Trim(), StringComparer.OrdinalIgnoreCase);

            // An incoming achievement whose ApiName is unknown may be an existing achievement whose
            // key changed (provider key-format migration or a renamed achievement) rather than a new
            // one. Pair such incoming rows with otherwise-stale existing rows by content and rename
            // the existing row in place, preserving its Id and therefore every user's unlock rows
            // (UserAchievements cascades on definition delete). Unpaired rows keep the existing
            // insert + prune semantics.
            var renamesByOldApiName = ComputeDefinitionRenames(incomingAchievements, existingByApiName);
            foreach (var rename in renamesByOldApiName)
            {
                if (!existingByApiName.TryGetValue(rename.Key, out var renamedRow) || renamedRow == null)
                {
                    continue;
                }

                db.ExecuteNonQuery(
                    @"UPDATE AchievementDefinitions
                      SET ApiName = ?,
                          UpdatedUtc = ?
                      WHERE Id = ?;",
                    rename.Value,
                    updatedIso,
                    renamedRow.Id);

                existingByApiName.Remove(rename.Key);
                renamedRow.ApiName = rename.Value;
                existingByApiName[rename.Value] = renamedRow;

                if (renameCollector != null)
                {
                    renameCollector[rename.Key] = rename.Value;
                }
                _logger?.Debug($"Renamed achievement definition {renamedRow.Id} '{rename.Key}' -> '{rename.Value}' (gameId={gameId})");
            }

            if (renamesByOldApiName.Count > 0)
            {
                _logger?.Info($"Renamed {renamesByOldApiName.Count} achievement definitions in place for gameId={gameId}, preserving unlock history.");
            }

            foreach (var achievement in incomingAchievements)
            {
                if (achievement == null || string.IsNullOrWhiteSpace(achievement.ApiName))
                {
                    continue;
                }

                var apiName = achievement.ApiName.Trim();
                desiredApiNames.Add(apiName);
                var incomingCategory = AchievementCategoryTypeHelper.NormalizeCategoryOrDefault(achievement.Category);
                var incomingCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(achievement.CategoryType);
                var incomingGlobalPercent = NormalizeStoredPercent(achievement.GlobalPercentUnlocked);
                var incomingRarity = achievement.Rarity.ToString();

                // Compute IsCapstone: provider-set value or auto-detect platinum trophies.
                // Manual capstones from settings are applied on top at load time.
                var isCapstone = achievement.IsCapstone ||
                    string.Equals(achievement.TrophyType?.Trim(), "platinum", StringComparison.OrdinalIgnoreCase);

                if (!existingByApiName.TryGetValue(apiName, out var existing))
                {
                    db.ExecuteNonQuery(
                        @"INSERT INTO AchievementDefinitions
                            (GameId, ApiName, DisplayName, Description, UnlockedIconPath, LockedIconPath, Points, ScaledPoints, Category, CategoryType, TrophyType, Hidden, IsCapstone, GlobalPercentUnlocked, Rarity, ProgressMax, CreatedUtc, UpdatedUtc)
                          VALUES
                            (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        gameId,
                        apiName,
                        DbValue(achievement.DisplayName),
                        DbValue(achievement.Description),
                        DbValue(MakeRelativePath(achievement.UnlockedIconPath)),
                        DbValue(MakeRelativePath(achievement.LockedIconPath)),
                        DbParam(achievement.Points),
                        DbParam(achievement.ScaledPoints),
                        DbValue(incomingCategory),
                        DbValue(incomingCategoryType),
                        DbValue(achievement.TrophyType),
                        achievement.Hidden ? 1 : 0,
                        isCapstone ? 1 : 0,
                        DbParam(incomingGlobalPercent),
                        incomingRarity,
                        DbParam(achievement.ProgressDenom),
                        nowIso,
                        updatedIso);

                    var definitionId = db.ExecuteScalar<long>("SELECT last_insert_rowid();");
                    existingByApiName[apiName] = new AchievementDefinitionRow
                    {
                        Id = definitionId,
                        GameId = gameId,
                        ApiName = apiName
                    };
                    idsByApiName[apiName] = definitionId;
                    continue;
                }

                var incomingDisplayName = NormalizeDbText(achievement.DisplayName);
                var incomingDescription = NormalizeDbText(achievement.Description);
                var incomingUnlockedIconPath = MakeRelativePath(NormalizeDbText(achievement.UnlockedIconPath));
                var incomingLockedIconPath = MakeRelativePath(NormalizeDbText(achievement.LockedIconPath));
                var incomingPoints = achievement.Points;
                var incomingScaledPoints = achievement.ScaledPoints;
                var incomingTrophyType = NormalizeDbText(achievement.TrophyType);
                var incomingHidden = achievement.Hidden ? 1L : 0L;
                var incomingIsCapstone = isCapstone ? 1L : 0L;
                var incomingStoredRarity = incomingRarity;
                var incomingProgressMax = achievement.ProgressDenom;

                var changed = !NullableEquals(NormalizeDbText(existing.DisplayName), incomingDisplayName) ||
                              !NullableEquals(NormalizeDbText(existing.Description), incomingDescription) ||
                              !NullableEquals(NormalizeDbText(existing.UnlockedIconPath), incomingUnlockedIconPath) ||
                              !NullableEquals(NormalizeDbText(existing.LockedIconPath), incomingLockedIconPath) ||
                              existing.Points != incomingPoints ||
                              existing.ScaledPoints != incomingScaledPoints ||
                              !NullableEquals(NormalizeDbText(existing.Category), incomingCategory) ||
                              !NullableEquals(NormalizeDbText(existing.CategoryType), incomingCategoryType) ||
                              !NullableEquals(NormalizeDbText(existing.TrophyType), incomingTrophyType) ||
                              existing.Hidden != incomingHidden ||
                              existing.IsCapstone != incomingIsCapstone ||
                              existing.GlobalPercentUnlocked != incomingGlobalPercent ||
                              !NullableEquals(NormalizeDbText(existing.Rarity), incomingStoredRarity) ||
                              existing.ProgressMax != incomingProgressMax;

                if (!changed)
                {
                    idsByApiName[apiName] = existing.Id;
                    continue;
                }

                db.ExecuteNonQuery(
                    @"UPDATE AchievementDefinitions
                      SET DisplayName = ?,
                          Description = ?,
                          UnlockedIconPath = ?,
                          LockedIconPath = ?,
                          Points = ?,
                          ScaledPoints = ?,
                          Category = ?,
                          CategoryType = ?,
                          TrophyType = ?,
                          Hidden = ?,
                          IsCapstone = ?,
                          GlobalPercentUnlocked = ?,
                          Rarity = ?,
                          ProgressMax = ?,
                          UpdatedUtc = ?
                      WHERE Id = ?;",
                                        DbValue(incomingDisplayName),
                                        DbValue(incomingDescription),
                                        DbValue(incomingUnlockedIconPath),
                                        DbValue(incomingLockedIconPath),
                                        DbParam(incomingPoints),
                                        DbParam(incomingScaledPoints),
                                        DbValue(incomingCategory),
                                        DbValue(incomingCategoryType),
                                        DbValue(incomingTrophyType),
                                        incomingHidden,
                                        incomingIsCapstone,
                                        DbParam(incomingGlobalPercent),
                                        incomingStoredRarity,
                                        DbParam(incomingProgressMax),
                    updatedIso,
                    existing.Id);

                idsByApiName[apiName] = existing.Id;
            }

            var existingIdsByApiName = existingByApiName.ToDictionary(
                a => a.Key,
                a => a.Value?.Id ?? 0,
                StringComparer.OrdinalIgnoreCase);

            var staleDefinitionIds = SqlNadoCacheBehavior.ComputeStaleDefinitionIds(
                existingIdsByApiName,
                desiredApiNames);

            // Schema-safety invariant: only prune stale definitions when the incoming set is at
            // least as complete as what is cached. This protects against an API failure returning
            // an empty list (count 0) AND against a partial/truncated "ok" schema (fewer rows than
            // cached) that would otherwise delete good achievement data. An equal-or-larger incoming
            // set still prunes, so genuine renames/replacements/growth are handled. Legitimate
            // shrinkage (a developer removing achievements) is left un-pruned rather than risk data loss.
            //
            // An authoritative source (a mapped current-user schema copied onto its friend proxy row,
            // or a complete Ok definition fetch) is exempt from the count comparison: it is complete
            // by construction, and holding stale keys instead deadlocks proxy rows where several key
            // schemes have accumulated — every DisplayName is then duplicated, so rename pairing is
            // ambiguous, the count invariant never allows the prune, and legacy keys defer friend
            // saves forever. Friend unlock rows attached to pruned rows are re-derived from the next
            // earned fetch; current-user saves never pass authoritativeSource.
            if ((authoritativeSource || desiredApiNames.Count >= existingByApiName.Count) &&
                desiredApiNames.Count > 0 &&
                staleDefinitionIds.Count > 0)
            {
                for (int i = 0; i < staleDefinitionIds.Count; i++)
                {
                    // Explicitly delete dependent unlock rows: the declared ON DELETE CASCADE only
                    // applies when the connection has PRAGMA foreign_keys enabled, which is not
                    // guaranteed on the save connections. A stale definition surviving with unlock
                    // rows (or unlock rows orphaned and later re-joined by rowid reuse) is how
                    // duplicate per-family unlock sets arise.
                    db.ExecuteNonQuery(
                        @"DELETE FROM UserAchievements
                          WHERE AchievementDefinitionId = ?;",
                        staleDefinitionIds[i]);
                    db.ExecuteNonQuery(
                        @"DELETE FROM AchievementDefinitions
                          WHERE Id = ?;",
                        staleDefinitionIds[i]);
                }
            }

            return idsByApiName;
        }

        // Pairs incoming achievements that have no ApiName match (would-be inserts) with existing
        // definitions absent from the incoming set (would-be prune candidates), by content:
        // DisplayName+Description, then DisplayName, then icon filename. A pair forms only when the
        // content key is unique on BOTH sides within its tier, so ambiguity always falls through to
        // the pre-existing insert + prune behavior rather than a speculative rename. Rename targets
        // are by construction absent from the existing key set, so in-place ApiName updates cannot
        // collide with other rows for the game.
        private static Dictionary<string, string> ComputeDefinitionRenames(
            List<AchievementDetail> incomingAchievements,
            Dictionary<string, AchievementDefinitionRow> existingByApiName)
        {
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (incomingAchievements == null || incomingAchievements.Count == 0 ||
                existingByApiName == null || existingByApiName.Count == 0)
            {
                return renames;
            }

            var incomingApiNames = new HashSet<string>(
                incomingAchievements.Select(a => a.ApiName.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var unmatchedIncoming = incomingAchievements
                .Where(a => !existingByApiName.ContainsKey(a.ApiName.Trim()))
                .ToList();
            var unmatchedExisting = existingByApiName.Values
                .Where(row => row != null && !incomingApiNames.Contains(row.ApiName.Trim()))
                .ToList();

            if (unmatchedIncoming.Count == 0 || unmatchedExisting.Count == 0)
            {
                return renames;
            }

            var tiers = new Func<string, string, string>[]
            {
                (displayName, description) => CombineRenameMatchKey(
                    NormalizeMatchText(displayName),
                    NormalizeMatchText(description)),
                (displayName, description) => NormalizeMatchText(displayName),
                null // icon tier handled separately below
            };

            for (var tierIndex = 0; tierIndex < tiers.Length; tierIndex++)
            {
                if (unmatchedIncoming.Count == 0 || unmatchedExisting.Count == 0)
                {
                    break;
                }

                Dictionary<string, AchievementDetail> incomingByKey;
                Dictionary<string, AchievementDefinitionRow> existingByKey;
                if (tiers[tierIndex] != null)
                {
                    var tier = tiers[tierIndex];
                    incomingByKey = GroupUniqueByKey(
                        unmatchedIncoming,
                        a => tier(a.DisplayName, a.Description));
                    existingByKey = GroupUniqueByKey(
                        unmatchedExisting,
                        row => tier(row.DisplayName, row.Description));
                }
                else
                {
                    incomingByKey = GroupUniqueByKey(
                        unmatchedIncoming,
                        a => FirstIconFilename(a.UnlockedIconPath, a.LockedIconPath));
                    existingByKey = GroupUniqueByKey(
                        unmatchedExisting,
                        row => FirstIconFilename(row.UnlockedIconPath, row.LockedIconPath));
                }

                foreach (var pair in incomingByKey)
                {
                    if (pair.Value == null ||
                        !existingByKey.TryGetValue(pair.Key, out var existingRow) ||
                        existingRow == null)
                    {
                        continue;
                    }

                    var oldApiName = existingRow.ApiName.Trim();
                    var newApiName = pair.Value.ApiName.Trim();
                    if (renames.ContainsKey(oldApiName))
                    {
                        continue;
                    }

                    // A degenerate incoming set with duplicate ApiNames could target the same new
                    // key from two pairs; the second in-place UPDATE would violate
                    // UNIQUE (GameId, ApiName) and abort the save. First pairing wins.
                    if (renames.ContainsValue(newApiName))
                    {
                        continue;
                    }

                    renames[oldApiName] = newApiName;
                    unmatchedIncoming.Remove(pair.Value);
                    unmatchedExisting.Remove(existingRow);
                }
            }

            return renames;
        }

        private static string CombineRenameMatchKey(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return null;
            }

            return left + "\n" + right;
        }

        private static string FirstIconFilename(string primaryIconPath, string secondaryIconPath)
        {
            var fileName = ExtractIconFilename(primaryIconPath);
            return string.IsNullOrWhiteSpace(fileName)
                ? ExtractIconFilename(secondaryIconPath)
                : fileName;
        }

        // Groups items by a content key; a key that maps to more than one item is marked ambiguous
        // by storing null, so callers can require uniqueness. Null/empty keys are skipped.
        private static Dictionary<string, TItem> GroupUniqueByKey<TItem>(
            List<TItem> items,
            Func<TItem, string> keySelector)
            where TItem : class
        {
            var byKey = new Dictionary<string, TItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var key = keySelector(item);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                byKey[key] = byKey.ContainsKey(key) ? null : item;
            }

            return byKey;
        }

        public void Dispose()
        {
            DisposeReadConnection();

            lock (_sync)
            {
                _cachedCurrentUsersByProvider.Clear();
                if (_db is IDisposable disposable)
                {
                    try
                    {
                        disposable.Dispose();
                    }
                    catch
                    {
                    }
                }

                _db = null;
                _initialized = false;
            }
        }

        private static GameAchievementData CreateModel(ProgressGameJoinRow progress)
        {
            return new GameAchievementData
            {
                LastUpdatedUtc = ParseUtc(progress?.LastUpdatedUtc) ?? DateTime.UtcNow,
                ProviderKey = progress?.ProviderKey,
                ProviderPlatformKey = progress?.ProviderPlatformKey,
                LibrarySourceName = progress?.LibrarySourceName,
                HasAchievements = progress != null && progress.HasAchievements != 0,
                // ExcludedByUser is populated by callers from settings
                GameName = progress?.GameName,
                AppId = (int)Math.Max(0, progress?.ProviderGameId ?? 0),
                ProviderGameKey = NormalizeProviderGameKey(progress?.ProviderGameKey),
                PlayniteGameId = ParseGuid(progress?.PlayniteGameId),
                Achievements = new List<AchievementDetail>()
            };
        }

        private static void BackfillPlayniteGameIdFromCacheKey(GameAchievementData model, string cacheKey)
        {
            if (model?.PlayniteGameId != null || string.IsNullOrWhiteSpace(cacheKey))
            {
                return;
            }

            if (Guid.TryParse(cacheKey.Trim(), out var parsed))
            {
                model.PlayniteGameId = parsed;
            }
        }

        private ResolvedUser ResolveCurrentUser(string providerKey)
        {
            var settings = _plugin?.Settings?.Persisted;
            string externalId = null;
            string displayName = null;

            if (string.Equals(providerKey, "Steam", StringComparison.OrdinalIgnoreCase))
            {
                externalId = ProviderRegistry.Settings<SteamSettings>().SteamUserId;
            }
            else if (string.Equals(providerKey, "RetroAchievements", StringComparison.OrdinalIgnoreCase))
            {
                externalId = ProviderRegistry.Settings<RetroAchievementsSettings>().RaUsername;
            }

            if (string.IsNullOrWhiteSpace(externalId))
            {
                externalId = "unmapped";
            }

            displayName = externalId;
            return new ResolvedUser
            {
                ProviderKey = providerKey,
                ExternalUserId = externalId.Trim(),
                DisplayName = displayName,
                FriendSource = null
            };
        }

        private static string NormalizeProviderKey(string providerKey)
        {
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                return "Unmapped";
            }

            var normalized = providerKey.Trim();

            // Normalize to standard casing to match database values
            switch (normalized.ToLowerInvariant())
            {
                case "steam": return "Steam";
                case "epic": return "Epic";
                case "epic games": return "Epic";
                case "gog": return "GOG";
                case "battle.net": return "BattleNet";
                case "battlenet": return "BattleNet";
                case "ea": return "EA";
                case "origin": return "EA";
                case "xbox": return "Xbox";
                case "psn": return "PSN";
                case "playstation": return "PSN";
                case "retroachievements": return "RetroAchievements";
                case "rpcs3": return "RPCS3";
                case "shadps4": return "ShadPS4";
                case "manual": return "Manual";
                case "manuel": return "Manual";
                case "unmapped": return "Unmapped";
                default: return normalized;
            }
        }

        private static object DbValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }

        private static object DbParam<T>(T? value) where T : struct
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static string NormalizeDbText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static void NormalizeIncomingAchievements(IEnumerable<AchievementDetail> achievements)
        {
            if (achievements == null)
            {
                return;
            }

            foreach (var achievement in achievements)
            {
                if (achievement == null)
                {
                    continue;
                }

                achievement.GlobalPercentUnlocked = NormalizeStoredPercent(
                    achievement.GlobalPercentUnlocked);
            }
        }

        private static double? NormalizeStoredPercent(double? rawPercent, bool convertLegacyRatio = false)
        {
            if (!rawPercent.HasValue)
            {
                return null;
            }

            var value = rawPercent.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return null;
            }

            if (convertLegacyRatio && value > 0 && value <= 1)
            {
                value *= 100.0;
            }

            if (value < 0)
            {
                return 0;
            }

            if (value > 100)
            {
                return 100;
            }

            return value;
        }

        internal static RarityTier ParseStoredRarity(string value)
        {
            return RarityTierExtensions.TryParse(value, out var rarity)
                ? rarity
                : RarityTier.Common;
        }

        private static string NormalizeMarkerApiName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static string NormalizeStoredIso(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var parsed = ParseUtc(value);
            return parsed.HasValue ? ToIso(parsed.Value) : value.Trim();
        }

        private static bool NullableEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }

        private static long ClampPlaytime(ulong seconds)
        {
            if (seconds > long.MaxValue)
            {
                return long.MaxValue;
            }

            return (long)seconds;
        }

        private static bool IsUnlocked(AchievementDetail achievement)
        {
            if (achievement == null)
            {
                return false;
            }

            return achievement.Unlocked;
        }

        private static DateTime? NormalizeUnlockTime(DateTime? unlockTimeUtc)
        {
            if (!unlockTimeUtc.HasValue)
            {
                return null;
            }

            var value = unlockTimeUtc.Value;
            if (value == DateTime.MinValue)
            {
                return null;
            }

            return DateTimeUtilities.AsUtcKind(value);
        }

        private static Guid? ParseGuid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (Guid.TryParse(value, out var guid))
            {
                return guid;
            }

            return null;
        }

        internal static DateTime? ParseUtc(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return DateTimeUtilities.AsUtcKind(parsed);
            }

            return null;
        }

        private static string ToIso(DateTime dateTime)
        {
            return DateTimeUtilities.AsUtcKind(dateTime).ToString("O", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Convert an absolute cache path to a relative path for database storage.
        /// Returns the original path if it's already relative, a URL, or not under the plugin data path.
        /// </summary>
        private string MakeRelativePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return absolutePath;
            }

            // Already relative or URL - pass through unchanged
            if (!Path.IsPathRooted(absolutePath))
            {
                return absolutePath;
            }

            if (absolutePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return absolutePath;
            }

            if (string.IsNullOrWhiteSpace(_pluginUserDataPath))
            {
                return absolutePath;
            }

            try
            {
                var fullBasePath = Path.GetFullPath(_pluginUserDataPath).TrimEnd(Path.DirectorySeparatorChar);
                var fullPath = Path.GetFullPath(absolutePath);

                if (fullPath.StartsWith(fullBasePath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return fullPath.Substring(fullBasePath.Length + 1);
                }
            }
            catch
            {
                // Path operations can fail with invalid characters - return original
            }

            return absolutePath;
        }

        /// <summary>
        /// Convert a relative path to an absolute path for runtime use.
        /// Returns the original path if it's already absolute or a URL.
        /// </summary>
        internal string MakeAbsolutePath(string relativeOrAbsolutePath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrAbsolutePath))
            {
                return relativeOrAbsolutePath;
            }

            // Already absolute - pass through unchanged
            if (Path.IsPathRooted(relativeOrAbsolutePath))
            {
                return relativeOrAbsolutePath;
            }

            // URL - pass through unchanged
            if (relativeOrAbsolutePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return relativeOrAbsolutePath;
            }

            if (string.IsNullOrWhiteSpace(_pluginUserDataPath))
            {
                return relativeOrAbsolutePath;
            }

            try
            {
                return Path.Combine(_pluginUserDataPath, relativeOrAbsolutePath);
            }
            catch
            {
                // Path operations can fail with invalid characters - return original
                return relativeOrAbsolutePath;
            }
        }

        /// <summary>
        /// Exports all database tables to CSV files in the specified directory.
        /// Returns the path to the directory containing the CSV files.
        /// </summary>
        public string ExportToCsv(string exportDirectory)
        {
            return _csvExporter.ExportToCsv(exportDirectory);
        }
    }
}




