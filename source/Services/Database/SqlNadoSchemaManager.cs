using Playnite.SDK;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using PlayniteAchievements.Services.Database.Rows;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class SqlNadoSchemaManager
    {
        public const int SchemaVersion = 17;
        private const string LegacyGamesProviderGameIdIndexName = "UX_Games_Provider_GameId";
        private const string GamesProviderGameIdNonRaIndexName = "UX_Games_Provider_GameId_NonRA";
        private const string GamesProviderGameIdLookupIndexName = "IX_Games_Provider_GameId";
        private readonly ILogger _logger;
        private readonly string _databasePath;
        private readonly string _pluginDataDir;

        public SqlNadoSchemaManager(ILogger logger, string databasePath, string pluginDataDir)
        {
            _logger = logger;
            _databasePath = databasePath ?? string.Empty;
            _pluginDataDir = pluginDataDir ?? string.Empty;
        }

        public void EnsureSchema(SQLiteDatabase db)
        {
            db.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
            db.ExecuteNonQuery("PRAGMA synchronous = NORMAL;");
            db.ExecuteNonQuery("PRAGMA foreign_keys = ON;");
            db.ExecuteNonQuery("PRAGMA temp_store = MEMORY;");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS CacheMetadata (
                Key TEXT PRIMARY KEY NOT NULL,
                Value TEXT NOT NULL
            );");

            // Create tables with IF NOT EXISTS - these will silently skip if tables exist
            // Note: We create indexes AFTER migration check to avoid referencing non-existent columns
            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProviderKey TEXT NOT NULL COLLATE NOCASE,
                ExternalUserId TEXT NOT NULL COLLATE NOCASE,
                DisplayName TEXT NULL,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0,
                FriendSource TEXT NULL,
                AvatarUrl TEXT NULL,
                AvatarPath TEXT NULL,
                LastRefreshedUtc TEXT NULL,
                IsActiveFriend INTEGER NOT NULL DEFAULT 1,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (ProviderKey, ExternalUserId)
            );");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS Games (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProviderKey TEXT NOT NULL COLLATE NOCASE,
                ProviderPlatformKey TEXT NULL,
                ProviderGameId INTEGER NULL,
                ProviderGameKey TEXT NULL COLLATE NOCASE,
                PlayniteGameId TEXT NULL,
                GameName TEXT NULL,
                LibrarySourceName TEXT NULL,
                IconPath TEXT NULL,
                CoverPath TEXT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastUpdatedUtc TEXT NOT NULL
            );");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS AchievementDefinitions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GameId INTEGER NOT NULL,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                DisplayName TEXT NULL,
                Description TEXT NULL,
                UnlockedIconPath TEXT NULL,
                LockedIconPath TEXT NULL,
                Points INTEGER NULL,
                ScaledPoints INTEGER NULL,
                Category TEXT NOT NULL DEFAULT 'Default',
                CategoryType TEXT NOT NULL DEFAULT 'Default',
                TrophyType TEXT NULL,
                Hidden INTEGER NOT NULL DEFAULT 0,
                IsCapstone INTEGER NOT NULL DEFAULT 0,
                GlobalPercentUnlocked REAL NULL,
                Rarity TEXT NOT NULL DEFAULT 'Common',
                ProgressMax INTEGER NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                UNIQUE (GameId, ApiName)
            );");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_AchievementDefinitions_GameId
                ON AchievementDefinitions (GameId);");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS UserGameProgress (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                GameId INTEGER NOT NULL,
                CacheKey TEXT NOT NULL COLLATE NOCASE,
                HasAchievements INTEGER NOT NULL DEFAULT 0,
                AchievementsUnlocked INTEGER NOT NULL DEFAULT 0,
                TotalAchievements INTEGER NOT NULL DEFAULT 0,
                LastUpdatedUtc TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                UNIQUE (UserId, GameId),
                UNIQUE (UserId, CacheKey)
            );");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserGameProgress_CacheKey
                ON UserGameProgress (CacheKey);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserGameProgress_LastUpdatedUtc
                ON UserGameProgress (LastUpdatedUtc);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserGameProgress_User_LastUpdated
                ON UserGameProgress (UserId, LastUpdatedUtc);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserGameProgress_GameId
                ON UserGameProgress (GameId);");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS UserAchievements (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserGameProgressId INTEGER NOT NULL,
                AchievementDefinitionId INTEGER NOT NULL,
                Unlocked INTEGER NOT NULL DEFAULT 0,
                UnlockTimeUtc TEXT NULL,
                ProgressNum INTEGER NULL,
                ProgressDenom INTEGER NULL,
                LastUpdatedUtc TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                FOREIGN KEY (UserGameProgressId) REFERENCES UserGameProgress(Id) ON DELETE CASCADE,
                FOREIGN KEY (AchievementDefinitionId) REFERENCES AchievementDefinitions(Id) ON DELETE CASCADE,
                UNIQUE (UserGameProgressId, AchievementDefinitionId)
            );");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserAchievements_UnlockTimeUtc
                ON UserAchievements (UnlockTimeUtc);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserAchievements_Definition
                ON UserAchievements (AchievementDefinitionId);");

            EnsureFriendOwnershipTable(db);
            EnsureProviderGameDefinitionStateTable(db);
            EnsureAchievementFiltersTable(db);

            var storedVersion = GetStoredSchemaVersion(db);
            var verification = VerifyRequiredColumns(db);

            if (verification.Success)
            {
                // Create ProviderKey-dependent indexes (only after verifying schema is correct)
                CreateProviderKeyIndexes(db);

                BackfillRequiredAchievementCategoryValues(db);

                // Schema is correct - ensure version is set if needed
                if (storedVersion < SchemaVersion)
                {
                    RunVersionedDataCleanups(db, storedVersion);
                    db.ExecuteNonQuery(
                        "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                        "schema_version",
                        SchemaVersion.ToString(CultureInfo.InvariantCulture));
                    _logger?.Info($"[Schema] Schema verified and version set to {SchemaVersion}.");
                }
                else
                {
                    _logger?.Info($"[Schema] Schema up-to-date (version {storedVersion}), skipping reconciliation.");
                }
                return;
            }

            // Verification failed - only valid for existing databases that need migration
            if (storedVersion == 0)
            {
                throw new InvalidOperationException(
                    $"Fresh database verification failed. This indicates a bug in CREATE TABLE statements. {verification.Message}");
            }

            _logger?.Warn($"[Schema] Schema version {storedVersion} but verification failed, running reconciliation.");
            var backupPath = ReconcileSchema(db);

            var verification2 = VerifyRequiredColumns(db);
            _logger?.Info(
                $"[Schema] Verification Success={verification2.Success} " +
                $"StoredVersion={storedVersion} TargetVersion={SchemaVersion} " +
                $"BackupPath={(string.IsNullOrWhiteSpace(backupPath) ? "(none)" : backupPath)}");

            if (!verification2.Success)
            {
                throw new InvalidOperationException(
                    $"Schema verification failed after reconciliation. {verification2.Message}");
            }

            // Create ProviderKey-dependent indexes after successful migration
            CreateProviderKeyIndexes(db);

            RunVersionedDataCleanups(db, storedVersion);
            db.ExecuteNonQuery(
                "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                "schema_version",
                SchemaVersion.ToString(CultureInfo.InvariantCulture));

            BackfillRequiredAchievementCategoryValues(db);
        }

        // One-time data cleanups tied to schema version upgrades. Runs before the stored version is
        // advanced; fresh databases (storedVersion 0) have nothing to clean.
        private void RunVersionedDataCleanups(SQLiteDatabase db, int storedVersion)
        {
            if (storedVersion <= 0)
            {
                return;
            }

            if (storedVersion < 16)
            {
                CleanupZeroUnlockProviderOnlyFriendOwnership(db);
            }
        }

        // v16: friend ownership rows for provider-only games (no PlayniteGameId) are only written once
        // a probe has confirmed the friend has unlocked achievements. Older caches persisted them
        // unconditionally; delete the rows whose friend has no unlocked achievement for that game so
        // the ownership-driven friends overview matches the refresh-side invariant without display
        // filtering. Mirrors the unlocks predicate of LoadFriendGameSummaryRows (UserGameProgress ->
        // UserAchievements with Unlocked = 1).
        private void CleanupZeroUnlockProviderOnlyFriendOwnership(SQLiteDatabase db)
        {
            try
            {
                db.ExecuteNonQuery(@"DELETE FROM FriendOwnership
                    WHERE Id IN (
                        SELECT fo.Id
                        FROM FriendOwnership fo
                        INNER JOIN Users u ON u.Id = fo.UserId
                        INNER JOIN Games g ON g.Id = fo.GameId
                        WHERE g.PlayniteGameId IS NULL
                          AND u.IsCurrentUser = 0
                          AND NOT EXISTS (
                              SELECT 1
                              FROM UserGameProgress ugp
                              INNER JOIN UserAchievements ua
                                  ON ua.UserGameProgressId = ugp.Id AND ua.Unlocked = 1
                              WHERE ugp.UserId = fo.UserId AND ugp.GameId = fo.GameId
                          )
                    );");
                var deleted = db.ExecuteScalar<long>("SELECT changes();");
                if (deleted > 0)
                {
                    _logger?.Info($"[Schema] v16 cleanup removed {deleted} zero-unlock provider-only friend ownership row(s).");
                }
            }
            catch (System.Exception ex)
            {
                _logger?.Error(ex, "[Schema] v16 zero-unlock provider-only friend ownership cleanup failed.");
            }
        }

        private void ExecuteSafe(SQLiteDatabase db, string sql)
        {
            try
            {
                db.ExecuteNonQuery(sql);
            }
            catch (System.Exception ex)
            {
                _logger?.Error(ex, $"Failed schema SQL: {sql}");
                throw;
            }
        }

        /// <summary>
        /// Creates indexes that reference ProviderKey column.
        /// Called only after schema verification passes or migration completes.
        /// </summary>
        private void CreateProviderKeyIndexes(SQLiteDatabase db)
        {
            DropProviderGameIdUniqueIndexes(db);

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_CurrentPerProvider
                ON Users (ProviderKey)
                WHERE IsCurrentUser = 1;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Users_CurrentUser_Id
                ON Users (IsCurrentUser, Id);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserGameProgress_GameId
                ON UserGameProgress (GameId);");

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Games_Provider_Playnite
                ON Games (ProviderKey, PlayniteGameId)
                WHERE PlayniteGameId IS NOT NULL;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameId
                ON Games (ProviderKey, ProviderGameId)
                WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameKey
                ON Games (ProviderKey, ProviderGameKey)
                WHERE ProviderGameKey IS NOT NULL AND TRIM(ProviderGameKey) <> '';");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_PlayniteGameId
                ON Games (PlayniteGameId);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_LastUpdatedUtc
                ON Games (LastUpdatedUtc);");

            // Covering indexes for the friends-overview summary aggregates (schema v14): the
            // friend-user set is filtered by (IsActiveFriend, IsCurrentUser) in every friend
            // CTE, and the unlocked-achievement joins probe UserAchievements by
            // (UserGameProgressId, Unlocked).
            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Users_ActiveFriend
                ON Users (IsActiveFriend, IsCurrentUser)
                WHERE IsActiveFriend = 1 AND IsCurrentUser = 0;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_UserAchievements_Progress_Unlocked
                ON UserAchievements (UserGameProgressId, Unlocked);");

            EnsureFriendOwnershipTable(db);
            EnsureFriendOwnershipIndexes(db);
            EnsureProviderGameDefinitionStateTable(db);
            EnsureProviderGameDefinitionStateIndexes(db);
            EnsureAchievementFiltersTable(db);
        }

        private void EnsureFriendOwnershipTable(SQLiteDatabase db)
        {
            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS FriendOwnership (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId INTEGER NOT NULL,
                GameId INTEGER NOT NULL,
                PlaytimeForeverMinutes INTEGER NOT NULL DEFAULT 0,
                Playtime2WeeksMinutes INTEGER NULL,
                LastPlayedUtc TEXT NULL,
                LastOwnershipRefreshUtc TEXT NULL,
                LastScrapedUtc TEXT NULL,
                LastScrapeStatus TEXT NULL,
                LastScrapeDetail TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                FOREIGN KEY (UserId) REFERENCES Users(Id) ON DELETE CASCADE,
                FOREIGN KEY (GameId) REFERENCES Games(Id) ON DELETE CASCADE,
                UNIQUE (UserId, GameId)
            );");
        }

        private void EnsureFriendOwnershipIndexes(SQLiteDatabase db)
        {
            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_FriendOwnership_Game_User
                ON FriendOwnership (GameId, UserId);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_FriendOwnership_User
                ON FriendOwnership (UserId);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_FriendOwnership_LastScraped
                ON FriendOwnership (LastScrapedUtc, LastScrapeStatus);");
        }

        // Derived mirror of the per-game achievement filter lists stored in game_custom_data.db
        // (a separate SQLite file the summary queries cannot join to). Fully resynced from
        // custom data at startup and on every CustomDataChanged; refresh saves never touch it.
        // The UNIQUE index prefix-covers the summary queries' (PlayniteGameId, ApiName)
        // anti-join probe.
        private void EnsureAchievementFiltersTable(SQLiteDatabase db)
        {
            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS AchievementFilters (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PlayniteGameId TEXT NOT NULL COLLATE NOCASE,
                ApiName TEXT NOT NULL COLLATE NOCASE,
                Kind TEXT NOT NULL COLLATE NOCASE,
                CreatedUtc TEXT NOT NULL,
                UNIQUE (PlayniteGameId, ApiName, Kind)
            );");
        }

        private void EnsureProviderGameDefinitionStateTable(SQLiteDatabase db)
        {
            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS ProviderGameDefinitionState (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProviderKey TEXT NOT NULL COLLATE NOCASE,
                ProviderGameId INTEGER NULL,
                ProviderGameKey TEXT NULL COLLATE NOCASE,
                GameName TEXT NULL,
                IconUrl TEXT NULL,
                Status TEXT NOT NULL,
                LastCheckedUtc TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL
            );");
        }

        private void EnsureProviderGameDefinitionStateIndexes(SQLiteDatabase db)
        {
            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_ProviderGameDefinitionState_GameId
                ON ProviderGameDefinitionState (ProviderKey, ProviderGameId)
                WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;");

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_ProviderGameDefinitionState_GameKey
                ON ProviderGameDefinitionState (ProviderKey, ProviderGameKey)
                WHERE ProviderGameKey IS NOT NULL AND TRIM(ProviderGameKey) <> '';");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_ProviderGameDefinitionState_Status_Checked
                ON ProviderGameDefinitionState (Status, LastCheckedUtc);");
        }

        private void BackfillRequiredAchievementCategoryValues(SQLiteDatabase db)
        {
            ExecuteSafe(
                db,
                "UPDATE AchievementDefinitions " +
                "SET Category = 'Default' " +
                "WHERE Category IS NULL OR TRIM(Category) = '';");

            ExecuteSafe(
                db,
                "UPDATE AchievementDefinitions " +
                "SET CategoryType = 'Default' " +
                "WHERE CategoryType IS NULL OR TRIM(CategoryType) = '';");
        }

        private string ReconcileSchema(SQLiteDatabase db)
        {
            var backupPath = default(string);
            var definitionColumns = GetColumnNames(db, "AchievementDefinitions");

            if (!definitionColumns.Contains("unlockediconpath"))
            {
                if (definitionColumns.Contains("iconunlockedpath"))
                {
                    ExecuteSchemaChangeWithBackup(
                        db,
                        "ALTER TABLE AchievementDefinitions RENAME COLUMN IconUnlockedPath TO UnlockedIconPath;",
                        ref backupPath,
                        "Renamed IconUnlockedPath to UnlockedIconPath.");
                }
                else if (definitionColumns.Contains("iconpath"))
                {
                    ExecuteSchemaChangeWithBackup(
                        db,
                        "ALTER TABLE AchievementDefinitions RENAME COLUMN IconPath TO UnlockedIconPath;",
                        ref backupPath,
                        "Renamed IconPath to UnlockedIconPath.");
                }
            }

            definitionColumns = GetColumnNames(db, "AchievementDefinitions");
            EnsureColumn(db, "AchievementDefinitions", "LockedIconPath", "TEXT NULL", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "Points", "INTEGER NULL", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "Category", "TEXT NOT NULL DEFAULT 'Default'", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "CategoryType", "TEXT NOT NULL DEFAULT 'Default'", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "TrophyType", "TEXT NULL", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "IsCapstone", "INTEGER NOT NULL DEFAULT 0", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "ScaledPoints", "INTEGER NULL", definitionColumns, ref backupPath);
            EnsureColumn(db, "AchievementDefinitions", "Rarity", "TEXT NOT NULL DEFAULT 'Common'", definitionColumns, ref backupPath);

            // Migrate UserGameProgress: NoAchievements -> HasAchievements (inverted) + add ExcludedByUser
            var progressColumns = GetColumnNames(db, "UserGameProgress");

            // Add HasAchievements column if it doesn't exist
            if (!progressColumns.Contains("hasachievements"))
            {
                ExecuteSchemaChangeWithBackup(
                    db,
                    "ALTER TABLE UserGameProgress ADD COLUMN HasAchievements INTEGER NOT NULL DEFAULT 0;",
                    ref backupPath,
                    "Added HasAchievements column to UserGameProgress.");
            }

            // Migrate data from NoAchievements to HasAchievements (inverted) if NoAchievements exists
            if (progressColumns.Contains("noachievements"))
            {
                ExecuteSchemaChangeWithBackup(
                    db,
                    "UPDATE UserGameProgress SET HasAchievements = CASE WHEN NoAchievements = 1 THEN 0 ELSE 1 END WHERE HasAchievements = 0;",
                    ref backupPath,
                    "Migrated NoAchievements to HasAchievements (inverted) in UserGameProgress.");
            }

            // Check if migration needed for Games: ProviderName -> ProviderKey
            var gamesColumns = GetColumnNames(db, "Games");
            var usersColumns = GetColumnNames(db, "Users");
            var needsGamesMigration = gamesColumns.Contains("providername") && !gamesColumns.Contains("providerkey");
            var needsUsersMigration = usersColumns.Contains("providername") && !usersColumns.Contains("providerkey");

            if (needsGamesMigration || needsUsersMigration)
            {
                // Create backup BEFORE any schema changes
                if (string.IsNullOrWhiteSpace(backupPath))
                {
                    backupPath = CreateMigrationBackup();
                }

                // Disable foreign keys to prevent cascade deletion during table recreation
                var fkEnabled = db.ExecuteScalar<string>("PRAGMA foreign_keys;");
                if (fkEnabled == "1")
                {
                    ExecuteSafe(db, "PRAGMA foreign_keys = OFF;");
                    _logger?.Info("[Schema] Disabled foreign keys for migration");
                }

                try
                {
                    if (needsGamesMigration)
                    {
                        _logger?.Info("[Schema] Migrating Games table from ProviderName to ProviderKey");

                        // Drop old indexes
                        ExecuteSafe(db, "DROP INDEX IF EXISTS UX_Games_Provider_Playnite;");
                        ExecuteSafe(db, "DROP INDEX IF EXISTS IX_Games_Provider_GameId;");
                        ExecuteSafe(db, "DROP INDEX IF EXISTS UX_Games_Provider_GameId_NonRA;");
                        _logger?.Info("[Schema] Dropped old Games indexes");

                        // Clean up any leftover from failed migration
                        ExecuteSafe(db, "DROP TABLE IF EXISTS Games_New;");

                        // Create new table
                        ExecuteSafe(db,
                            @"CREATE TABLE Games_New (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProviderKey TEXT NOT NULL COLLATE NOCASE,
                                ProviderPlatformKey TEXT NULL,
                                ProviderGameId INTEGER NULL,
                                ProviderGameKey TEXT NULL COLLATE NOCASE,
                                PlayniteGameId TEXT NULL,
                                GameName TEXT NULL,
                                LibrarySourceName TEXT NULL,
                                FirstSeenUtc TEXT NOT NULL,
                                LastUpdatedUtc TEXT NOT NULL
                            );");
                        _logger?.Info("[Schema] Created Games_New table");

                        // Migrate data with value transformation (INSERT OR IGNORE to handle duplicates)
                        ExecuteSafe(db,
                                                        @"INSERT OR IGNORE INTO Games_New (Id, ProviderKey, ProviderPlatformKey, ProviderGameId, ProviderGameKey, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc)
                              SELECT
                                Id,
                                CASE
                                    WHEN ProviderName IS NULL THEN 'Unmapped'
                                    WHEN LOWER(ProviderName) = 'steam' THEN 'Steam'
                                    WHEN LOWER(ProviderName) = 'epic' THEN 'Epic'
                                    WHEN LOWER(ProviderName) = 'epic games' THEN 'Epic'
                                    WHEN LOWER(ProviderName) = 'gog' THEN 'GOG'
                                    WHEN LOWER(ProviderName) = 'xbox' THEN 'Xbox'
                                    WHEN LOWER(ProviderName) = 'psn' THEN 'PSN'
                                    WHEN LOWER(ProviderName) = 'playstation' THEN 'PSN'
                                    WHEN LOWER(ProviderName) LIKE '%playstation%' THEN 'PSN'
                                    WHEN LOWER(ProviderName) = 'retroachievements' THEN 'RetroAchievements'
                                    WHEN LOWER(ProviderName) LIKE '%retro%' THEN 'RetroAchievements'
                                    WHEN LOWER(ProviderName) = 'rpcs3' THEN 'RPCS3'
                                    WHEN LOWER(ProviderName) = 'shadps4' THEN 'ShadPS4'
                                    WHEN LOWER(ProviderName) = 'manual' THEN 'Manual'
                                    WHEN LOWER(ProviderName) = 'manuel' THEN 'Manual'
                                    WHEN LOWER(ProviderName) = 'unmapped' THEN 'Unmapped'
                                    ELSE 'Unmapped'
                                END,
                                                                NULL,
                                ProviderGameId, NULL, PlayniteGameId, GameName, LibrarySourceName, FirstSeenUtc, LastUpdatedUtc
                              FROM Games;");
                        _logger?.Info("[Schema] Migrated Games data to ProviderKey");

                        // Drop old and rename
                        ExecuteSafe(db, "DROP TABLE Games;");
                        ExecuteSafe(db, "ALTER TABLE Games_New RENAME TO Games;");
                        _logger?.Info("[Schema] Renamed Games_New to Games");

                        // Recreate indexes
                        ExecuteSafe(db,
                            @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Games_Provider_Playnite
                                ON Games (ProviderKey, PlayniteGameId)
                                WHERE PlayniteGameId IS NOT NULL;");
                        ExecuteSafe(db,
                            @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameId
                                ON Games (ProviderKey, ProviderGameId)
                                WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;");
                        ExecuteSafe(db, "CREATE INDEX IF NOT EXISTS IX_Games_PlayniteGameId ON Games (PlayniteGameId);");
                        ExecuteSafe(db, "CREATE INDEX IF NOT EXISTS IX_Games_LastUpdatedUtc ON Games (LastUpdatedUtc);");
                        _logger?.Info("[Schema] Recreated Games indexes");

                        _logger?.Info("[Schema] Games table migration completed");
                    }

                    if (needsUsersMigration)
                    {
                        _logger?.Info("[Schema] Migrating Users table from ProviderName to ProviderKey");

                        // Drop old indexes
                        ExecuteSafe(db, "DROP INDEX IF EXISTS UX_Users_CurrentPerProvider;");
                        ExecuteSafe(db, "DROP INDEX IF EXISTS IX_Users_CurrentUser_Id;");
                        _logger?.Info("[Schema] Dropped old Users indexes");

                        // Clean up any leftover from failed migration
                        ExecuteSafe(db, "DROP TABLE IF EXISTS Users_New;");

                        // Create new table
                        ExecuteSafe(db,
                            @"CREATE TABLE Users_New (
                                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                                ProviderKey TEXT NOT NULL COLLATE NOCASE,
                                ExternalUserId TEXT NOT NULL COLLATE NOCASE,
                                DisplayName TEXT NULL,
                                IsCurrentUser INTEGER NOT NULL DEFAULT 0,
                                FriendSource TEXT NULL,
                                AvatarUrl TEXT NULL,
                                LastRefreshedUtc TEXT NULL,
                                IsActiveFriend INTEGER NOT NULL DEFAULT 1,
                                CreatedUtc TEXT NOT NULL,
                                UpdatedUtc TEXT NOT NULL,
                                UNIQUE (ProviderKey, ExternalUserId)
                            );");
                        _logger?.Info("[Schema] Created Users_New table");

                        // Migrate data with value transformation (INSERT OR IGNORE to handle duplicates)
                        ExecuteSafe(db,
                            @"INSERT OR IGNORE INTO Users_New (Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, FriendSource, AvatarUrl, LastRefreshedUtc, IsActiveFriend, CreatedUtc, UpdatedUtc)
                              SELECT
                                Id,
                                CASE
                                    WHEN ProviderName IS NULL THEN 'Unmapped'
                                    WHEN LOWER(ProviderName) = 'steam' THEN 'Steam'
                                    WHEN LOWER(ProviderName) = 'epic' THEN 'Epic'
                                    WHEN LOWER(ProviderName) = 'epic games' THEN 'Epic'
                                    WHEN LOWER(ProviderName) = 'gog' THEN 'GOG'
                                    WHEN LOWER(ProviderName) = 'xbox' THEN 'Xbox'
                                    WHEN LOWER(ProviderName) = 'psn' THEN 'PSN'
                                    WHEN LOWER(ProviderName) = 'playstation' THEN 'PSN'
                                    WHEN LOWER(ProviderName) LIKE '%playstation%' THEN 'PSN'
                                    WHEN LOWER(ProviderName) = 'retroachievements' THEN 'RetroAchievements'
                                    WHEN LOWER(ProviderName) LIKE '%retro%' THEN 'RetroAchievements'
                                    WHEN LOWER(ProviderName) = 'rpcs3' THEN 'RPCS3'
                                    WHEN LOWER(ProviderName) = 'shadps4' THEN 'ShadPS4'
                                    WHEN LOWER(ProviderName) = 'manual' THEN 'Manual'
                                    WHEN LOWER(ProviderName) = 'manuel' THEN 'Manual'
                                    WHEN LOWER(ProviderName) = 'unmapped' THEN 'Unmapped'
                                    ELSE 'Unmapped'
                                END,
                                ExternalUserId, DisplayName, IsCurrentUser, FriendSource, NULL, NULL, 1, CreatedUtc, UpdatedUtc
                              FROM Users;");
                        _logger?.Info("[Schema] Migrated Users data to ProviderKey");

                        // Drop old and rename
                        ExecuteSafe(db, "DROP TABLE Users;");
                        ExecuteSafe(db, "ALTER TABLE Users_New RENAME TO Users;");
                        _logger?.Info("[Schema] Renamed Users_New to Users");

                        // Recreate indexes
                        ExecuteSafe(db,
                            @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_CurrentPerProvider
                                ON Users (ProviderKey)
                                WHERE IsCurrentUser = 1;");
                        ExecuteSafe(db, "CREATE INDEX IF NOT EXISTS IX_Users_CurrentUser_Id ON Users (IsCurrentUser, Id);");
                        _logger?.Info("[Schema] Recreated Users indexes");

                        _logger?.Info("[Schema] Users table migration completed");
                    }
                }
                finally
                {
                    // Re-enable foreign keys if they were enabled before
                    if (fkEnabled == "1")
                    {
                        ExecuteSafe(db, "PRAGMA foreign_keys = ON;");
                        _logger?.Info("[Schema] Re-enabled foreign keys after migration");
                    }
                }
            }

            gamesColumns = GetColumnNames(db, "Games");
            EnsureColumn(db, "Games", "ProviderPlatformKey", "TEXT NULL", gamesColumns, ref backupPath);
            EnsureColumn(db, "Games", "ProviderGameKey", "TEXT NULL COLLATE NOCASE", gamesColumns, ref backupPath);
            EnsureColumn(db, "Games", "IconPath", "TEXT NULL", gamesColumns, ref backupPath);
            EnsureColumn(db, "Games", "CoverPath", "TEXT NULL", gamesColumns, ref backupPath);

            usersColumns = GetColumnNames(db, "Users");
            EnsureColumn(db, "Users", "AvatarUrl", "TEXT NULL", usersColumns, ref backupPath);
            EnsureColumn(db, "Users", "AvatarPath", "TEXT NULL", usersColumns, ref backupPath);
            EnsureColumn(db, "Users", "LastRefreshedUtc", "TEXT NULL", usersColumns, ref backupPath);
            EnsureColumn(db, "Users", "IsActiveFriend", "INTEGER NOT NULL DEFAULT 1", usersColumns, ref backupPath);

            EnsureFriendOwnershipTable(db);
            ReconcileFriendOwnershipColumns(db, ref backupPath);
            ReconcileProviderGameDefinitionStateTable(db, ref backupPath);
            EnsureProviderGameDefinitionStateTable(db);
            EnsureProviderGameDefinitionStateIndexes(db);
            EnsureAchievementFiltersTable(db);

            return backupPath;
        }

        private void ReconcileProviderGameDefinitionStateTable(SQLiteDatabase db, ref string backupPath)
        {
            var columns = GetColumnNames(db, "ProviderGameDefinitionState");
            if (columns.Count == 0)
            {
                return;
            }

            if (!columns.Contains("providergamekey"))
            {
                ExecuteSchemaChangeWithBackup(
                    db,
                    "DROP TABLE IF EXISTS ProviderGameDefinitionState;",
                    ref backupPath,
                    "Recreated ProviderGameDefinitionState for string provider game keys.");
            }
        }

        private void ReconcileFriendOwnershipColumns(SQLiteDatabase db, ref string backupPath)
        {
            var columns = GetColumnNames(db, "FriendOwnership");
            EnsureColumn(db, "FriendOwnership", "Playtime2WeeksMinutes", "INTEGER NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "LastPlayedUtc", "TEXT NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "LastOwnershipRefreshUtc", "TEXT NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "LastScrapedUtc", "TEXT NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "LastScrapeStatus", "TEXT NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "LastScrapeDetail", "TEXT NULL", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "CreatedUtc", "TEXT NOT NULL DEFAULT ''", columns, ref backupPath);
            EnsureColumn(db, "FriendOwnership", "UpdatedUtc", "TEXT NOT NULL DEFAULT ''", columns, ref backupPath);
        }

        private void ReconcileGamesProviderGameIdIndexes(SQLiteDatabase db, ref string backupPath)
        {
            if (IndexExists(db, LegacyGamesProviderGameIdIndexName))
            {
                ExecuteSchemaChangeWithBackup(
                    db,
                    $"DROP INDEX IF EXISTS {LegacyGamesProviderGameIdIndexName};",
                    ref backupPath,
                    $"Dropped legacy index {LegacyGamesProviderGameIdIndexName}.");
            }

            if (IndexExists(db, GamesProviderGameIdNonRaIndexName))
            {
                ExecuteSchemaChangeWithBackup(
                    db,
                    $"DROP INDEX IF EXISTS {GamesProviderGameIdNonRaIndexName};",
                    ref backupPath,
                    $"Dropped index {GamesProviderGameIdNonRaIndexName}.");
            }

            EnsureIndex(
                db,
                GamesProviderGameIdLookupIndexName,
                @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameId
                    ON Games (ProviderKey, ProviderGameId)
                    WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;",
                ref backupPath,
                $"Ensured index {GamesProviderGameIdLookupIndexName}.");
        }

        private void DropProviderGameIdUniqueIndexes(SQLiteDatabase db)
        {
            if (IndexExists(db, LegacyGamesProviderGameIdIndexName))
            {
                ExecuteSafe(db, $"DROP INDEX IF EXISTS {LegacyGamesProviderGameIdIndexName};");
                _logger?.Info($"[Schema] Dropped legacy index {LegacyGamesProviderGameIdIndexName}.");
            }

            if (IndexExists(db, GamesProviderGameIdNonRaIndexName))
            {
                ExecuteSafe(db, $"DROP INDEX IF EXISTS {GamesProviderGameIdNonRaIndexName};");
                _logger?.Info($"[Schema] Dropped index {GamesProviderGameIdNonRaIndexName}.");
            }
        }

        private int GetStoredSchemaVersion(SQLiteDatabase db)
        {
            try
            {
                var rows = db.Load<CacheMetadataRow>(
                    "SELECT Key, Value FROM CacheMetadata WHERE Key = ?;",
                    "schema_version").ToList();
                if (rows.Count > 0 && int.TryParse(rows[0]?.Value, out var version))
                {
                    return version;
                }
            }
            catch (System.Exception ex)
            {
                _logger?.Debug(ex, "[Schema] Could not read schema version, assuming 0");
            }
            return 0;
        }

        private HashSet<string> GetColumnNames(SQLiteDatabase db, string tableName)
        {
            var sql = $"PRAGMA table_info({tableName});";
            var rows = db.Load<ColumnInfoRow>(sql).ToList();

            return new HashSet<string>(
                rows
                    .Select(a => a?.Name?.Trim().ToLowerInvariant())
                    .Where(a => !string.IsNullOrWhiteSpace(a)),
                StringComparer.OrdinalIgnoreCase);
        }

        private bool IndexExists(SQLiteDatabase db, string indexName)
        {
            if (string.IsNullOrWhiteSpace(indexName))
            {
                return false;
            }

            var exists = db.ExecuteScalar<long>(
                @"SELECT EXISTS(
                    SELECT 1
                    FROM sqlite_master
                    WHERE type = 'index'
                      AND name = ?
                    LIMIT 1
                  );",
                indexName.Trim());

            return exists != 0;
        }

        private void EnsureIndex(
            SQLiteDatabase db,
            string indexName,
            string createIndexSql,
            ref string backupPath,
            string successLog)
        {
            if (IndexExists(db, indexName))
            {
                return;
            }

            ExecuteSchemaChangeWithBackup(db, createIndexSql, ref backupPath, successLog);
        }

        private void EnsureColumn(
            SQLiteDatabase db,
            string tableName,
            string columnName,
            string columnDefinition,
            HashSet<string> knownColumns,
            ref string backupPath)
        {
            if (knownColumns.Contains(columnName))
            {
                return;
            }

            ExecuteSchemaChangeWithBackup(
                db,
                $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};",
                ref backupPath,
                $"Added {tableName}.{columnName}.");

            knownColumns.Add(columnName);
        }

        private void ExecuteSchemaChangeWithBackup(
            SQLiteDatabase db,
            string sql,
            ref string backupPath,
            string successLog)
        {
            if (string.IsNullOrWhiteSpace(backupPath))
            {
                backupPath = CreateMigrationBackup();
            }

            ExecuteSafe(db, sql);
            if (!string.IsNullOrWhiteSpace(successLog))
            {
                _logger?.Info($"[Schema] {successLog}");
            }
        }

        private string CreateMigrationBackup()
        {
            var root = string.IsNullOrWhiteSpace(_pluginDataDir)
                ? Path.GetDirectoryName(_databasePath)
                : _pluginDataDir;
            var backupPath = BackupHelper.CreateBackup(
                root,
                "db-schema",
                _databasePath,
                _databasePath + "-wal",
                _databasePath + "-shm");

            _logger?.Info($"[Schema] Migration backup created: {backupPath}");
            return backupPath;
        }

        private (bool Success, string Message) VerifyRequiredColumns(SQLiteDatabase db)
        {
            var missing = new List<string>();

            var definitionColumns = GetColumnNames(db, "AchievementDefinitions");
            EnsureRequiredColumn(definitionColumns, "UnlockedIconPath", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "LockedIconPath", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "Points", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "ScaledPoints", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "Category", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "CategoryType", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "TrophyType", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "IsCapstone", "AchievementDefinitions", missing);
            EnsureRequiredColumn(definitionColumns, "Rarity", "AchievementDefinitions", missing);

            // Verify ProviderKey column exists in Games and Users tables (migration from ProviderName)
            var gamesColumns = GetColumnNames(db, "Games");
            EnsureRequiredColumn(gamesColumns, "ProviderKey", "Games", missing);
            EnsureRequiredColumn(gamesColumns, "ProviderPlatformKey", "Games", missing);
            EnsureRequiredColumn(gamesColumns, "ProviderGameKey", "Games", missing);
            EnsureRequiredColumn(gamesColumns, "IconPath", "Games", missing);
            EnsureRequiredColumn(gamesColumns, "CoverPath", "Games", missing);

            var usersColumns = GetColumnNames(db, "Users");
            EnsureRequiredColumn(usersColumns, "ProviderKey", "Users", missing);
            EnsureRequiredColumn(usersColumns, "AvatarUrl", "Users", missing);
            EnsureRequiredColumn(usersColumns, "AvatarPath", "Users", missing);
            EnsureRequiredColumn(usersColumns, "LastRefreshedUtc", "Users", missing);
            EnsureRequiredColumn(usersColumns, "IsActiveFriend", "Users", missing);

            var friendOwnershipColumns = GetColumnNames(db, "FriendOwnership");
            EnsureRequiredColumn(friendOwnershipColumns, "UserId", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "GameId", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "PlaytimeForeverMinutes", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "Playtime2WeeksMinutes", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "LastPlayedUtc", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "LastOwnershipRefreshUtc", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "LastScrapedUtc", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "LastScrapeStatus", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "LastScrapeDetail", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "CreatedUtc", "FriendOwnership", missing);
            EnsureRequiredColumn(friendOwnershipColumns, "UpdatedUtc", "FriendOwnership", missing);

            var achievementFilterColumns = GetColumnNames(db, "AchievementFilters");
            EnsureRequiredColumn(achievementFilterColumns, "PlayniteGameId", "AchievementFilters", missing);
            EnsureRequiredColumn(achievementFilterColumns, "ApiName", "AchievementFilters", missing);
            EnsureRequiredColumn(achievementFilterColumns, "Kind", "AchievementFilters", missing);
            EnsureRequiredColumn(achievementFilterColumns, "CreatedUtc", "AchievementFilters", missing);

            var providerGameDefinitionStateColumns = GetColumnNames(db, "ProviderGameDefinitionState");
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "ProviderKey", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "ProviderGameId", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "ProviderGameKey", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "GameName", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "IconUrl", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "Status", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "LastCheckedUtc", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "CreatedUtc", "ProviderGameDefinitionState", missing);
            EnsureRequiredColumn(providerGameDefinitionStateColumns, "UpdatedUtc", "ProviderGameDefinitionState", missing);

            // Note: Index verification is intentionally NOT done here because indexes are
            // created in CreateProviderKeyIndexes which runs AFTER this verification passes.
            // For fresh databases, the indexes don't exist yet at this point.

            if (missing.Count > 0)
            {
                return (false, "Schema verification failures: " + string.Join(", ", missing));
            }

            return (true, "OK");
        }

        private static void EnsureRequiredColumn(
            HashSet<string> columns,
            string columnName,
            string tableName,
            List<string> missing)
        {
            if (!columns.Contains(columnName))
            {
                missing.Add($"{tableName}.{columnName}");
            }
        }

        // Row class for PRAGMA table_info results
        private sealed class ColumnInfoRow
        {
            public int Cid { get; set; }
            public string Name { get; set; }
            public string Type { get; set; }
            public int NotNull { get; set; }
            public object Default { get; set; }
            public int PrimaryKey { get; set; }
        }
    }
}

