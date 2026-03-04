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
        public const int SchemaVersion = 6;
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

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS Users (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProviderName TEXT NOT NULL COLLATE NOCASE,
                ExternalUserId TEXT NOT NULL COLLATE NOCASE,
                DisplayName TEXT NULL,
                IsCurrentUser INTEGER NOT NULL DEFAULT 0,
                FriendSource TEXT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE (ProviderName, ExternalUserId)
            );");

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Users_CurrentPerProvider
                ON Users (ProviderName)
                WHERE IsCurrentUser = 1;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Users_CurrentUser_Id
                ON Users (IsCurrentUser, Id);");

            ExecuteSafe(db, @"CREATE TABLE IF NOT EXISTS Games (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProviderName TEXT NOT NULL COLLATE NOCASE,
                ProviderGameId INTEGER NULL,
                PlayniteGameId TEXT NULL,
                GameName TEXT NULL,
                LibrarySourceName TEXT NULL,
                FirstSeenUtc TEXT NOT NULL,
                LastUpdatedUtc TEXT NOT NULL
            );");

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Games_Provider_Playnite
                ON Games (ProviderName, PlayniteGameId)
                WHERE PlayniteGameId IS NOT NULL;");

            ExecuteSafe(db, @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Games_Provider_GameId_NonRA
                ON Games (ProviderName, ProviderGameId)
                WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0
                  AND ProviderName <> 'RetroAchievements';");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameId
                ON Games (ProviderName, ProviderGameId)
                WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_PlayniteGameId
                ON Games (PlayniteGameId);");

            ExecuteSafe(db, @"CREATE INDEX IF NOT EXISTS IX_Games_LastUpdatedUtc
                ON Games (LastUpdatedUtc);");

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

            var storedVersion = GetStoredSchemaVersion(db);
            var verification = VerifyRequiredColumns(db);

            if (verification.Success)
            {
                BackfillRequiredAchievementCategoryValues(db);

                // Schema is correct - ensure version is set if needed
                if (storedVersion < SchemaVersion)
                {
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

            db.ExecuteNonQuery(
                "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                "schema_version",
                SchemaVersion.ToString(CultureInfo.InvariantCulture));

            BackfillRequiredAchievementCategoryValues(db);
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

            ReconcileGamesProviderGameIdIndexes(db, ref backupPath);

            return backupPath;
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

            EnsureIndex(
                db,
                GamesProviderGameIdNonRaIndexName,
                @"CREATE UNIQUE INDEX IF NOT EXISTS UX_Games_Provider_GameId_NonRA
                    ON Games (ProviderName, ProviderGameId)
                    WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0
                      AND ProviderName <> 'RetroAchievements';",
                ref backupPath,
                $"Ensured index {GamesProviderGameIdNonRaIndexName}.");

            EnsureIndex(
                db,
                GamesProviderGameIdLookupIndexName,
                @"CREATE INDEX IF NOT EXISTS IX_Games_Provider_GameId
                    ON Games (ProviderName, ProviderGameId)
                    WHERE ProviderGameId IS NOT NULL AND ProviderGameId > 0;",
                ref backupPath,
                $"Ensured index {GamesProviderGameIdLookupIndexName}.");
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
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("Unable to create migration backup: plugin data directory is unknown.");
            }

            var backupRoot = Path.Combine(root, "migration_backups");
            Directory.CreateDirectory(backupRoot);

            var backupPath = Path.Combine(
                backupRoot,
                $"db-schema-{DateTime.UtcNow:yyyyMMdd_HHmmssfff}");
            Directory.CreateDirectory(backupPath);

            var filesToBackup = new[]
            {
                _databasePath,
                _databasePath + "-wal",
                _databasePath + "-shm"
            };

            for (var i = 0; i < filesToBackup.Length; i++)
            {
                var sourcePath = filesToBackup[i];
                if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(backupPath, Path.GetFileName(sourcePath));
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }

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

            if (IndexExists(db, LegacyGamesProviderGameIdIndexName))
            {
                missing.Add($"Unexpected legacy index: {LegacyGamesProviderGameIdIndexName}");
            }

            EnsureRequiredIndex(db, GamesProviderGameIdNonRaIndexName, missing);
            EnsureRequiredIndex(db, GamesProviderGameIdLookupIndexName, missing);

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

        private void EnsureRequiredIndex(
            SQLiteDatabase db,
            string indexName,
            List<string> missing)
        {
            if (!IndexExists(db, indexName))
            {
                missing.Add($"Missing index: {indexName}");
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

