using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Images;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    /// <summary>
    /// Reads and writes custom data in the custom layer tables (see <see cref="GameCustomLayer"/>),
    /// one game per transaction, on its own connection to the plugin's database.
    /// </summary>
    internal sealed class GameCustomDataRepository
    {
        // Where custom data was stored, one JSON payload per game, before the custom layer tables.
        // Kept untouched as a backup for one release; read only to migrate.
        private const string LegacyTableName = "GameCustomData";
        private const string CapstoneArtTableName = "CapstoneArtRecords";

        // Custom data had its own database file before it moved into the plugin's one database.
        private const string LegacyDatabaseFileName = "game_custom_data.db";
        private const string MovedInMarkerKey = "custom_data_moved_in";
        private const int BusyTimeoutMs = 5000;

        private const string AchievementColumns = "PlayniteGameId, " + CustomAchievementRow.Columns;
        private const string GameColumns = CustomGameStateRow.Columns;
        private const string CategoryColumns = CustomCategoryRow.Columns;

        private sealed class LegacyPayloadRow
        {
            public string PlayniteGameId { get; set; }

            public string PayloadJson { get; set; }

            public string UpdatedUtc { get; set; }
        }

        /// <summary>The outcome of moving the legacy payloads into the tables.</summary>
        internal sealed class MigrationResult
        {
            public int Migrated { get; set; }

            /// <summary>Payloads that would not parse, left in the legacy table only.</summary>
            public int Unreadable { get; set; }

            public string BackupPath { get; set; }
        }

        private readonly object _syncRoot = new object();
        private readonly string _databasePath;
        private readonly string _pluginDataRoot;
        private readonly ILogger _logger;
        private bool _schemaInitialized;
        private bool _migrationBackupAttempted;

        public GameCustomDataRepository(string databasePath, ILogger logger = null)
        {
            _databasePath = databasePath ?? string.Empty;
            _pluginDataRoot = Path.GetDirectoryName(_databasePath);
            _logger = logger;
        }

        public string DatabasePath => _databasePath;

        /// <summary>The last migration of legacy payloads this repository ran, or null.</summary>
        internal MigrationResult LastMigration { get; private set; }

        public bool TryLoad(Guid playniteGameId, out GameCustomDataFile data)
        {
            data = null;
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            var idText = ToGuidText(playniteGameId);
            data = WithDb(
                createIfMissing: false,
                db => Load(
                    Games(db, $"SELECT {GameColumns} FROM {AchievementLayerSql.GamesTable} WHERE PlayniteGameId = ?;", idText),
                    Achievements(db, $"SELECT {AchievementColumns} FROM {AchievementLayerSql.AchievementsTable} WHERE PlayniteGameId = ?;", idText),
                    Categories(db, $"SELECT {CategoryColumns} FROM {AchievementLayerSql.CategoriesTable} WHERE PlayniteGameId = ?;", idText))
                    .FirstOrDefault(),
                default(GameCustomDataFile));
            return data != null;
        }

        /// <summary>Every stored record, normalized.</summary>
        public IEnumerable<GameCustomDataFile> EnumerateAllNormalized()
        {
            return WithDb(
                createIfMissing: false,
                db => Load(
                    Games(db, $"SELECT {GameColumns} FROM {AchievementLayerSql.GamesTable} ORDER BY PlayniteGameId;"),
                    Achievements(db, $"SELECT {AchievementColumns} FROM {AchievementLayerSql.AchievementsTable};"),
                    Categories(db, $"SELECT {CategoryColumns} FROM {AchievementLayerSql.CategoriesTable};")),
                new List<GameCustomDataFile>());
        }

        /// <summary>
        /// Writes the game's rows and returns the record they hold, file paths in the form a load
        /// returns them, so the caller can seed its cache without reading it back, or null when the
        /// record holds nothing and the game's rows were deleted instead.
        /// </summary>
        /// <param name="normalized">A record <see cref="GameCustomDataNormalizer.NormalizeInternal"/> returned.</param>
        /// <param name="changed">True when a row moved.</param>
        public GameCustomDataFile Save(Guid playniteGameId, GameCustomDataFile normalized, out bool changed)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            var keep = GameCustomDataNormalizer.HasInternalData(normalized);
            var rows = keep ? ToRows(normalized) : null;
            var wrote = false;
            WithDb(createIfMissing: keep, db => db.RunTransaction(() => wrote = Write(db, playniteGameId, rows)));
            changed = wrote;
            return keep ? Read(playniteGameId, rows) : null;
        }

        /// <summary>
        /// Writes several games' normalized records in one transaction, and returns for each, in
        /// order, the record its rows hold (null when they were deleted) and whether a row moved.
        /// </summary>
        public IReadOnlyList<(GameCustomDataFile Persisted, bool Changed)> SaveBatch(
            IReadOnlyList<KeyValuePair<Guid, GameCustomDataFile>> normalizedRecords)
        {
            var prepared = (normalizedRecords ?? Array.Empty<KeyValuePair<Guid, GameCustomDataFile>>())
                .Select(pair =>
                {
                    if (pair.Key == Guid.Empty)
                    {
                        throw new ArgumentException("Game ID is required.", nameof(normalizedRecords));
                    }

                    var keep = GameCustomDataNormalizer.HasInternalData(pair.Value);
                    return (GameId: pair.Key, Keep: keep, Rows: keep ? ToRows(pair.Value) : null);
                })
                .ToList();
            var changed = new bool[prepared.Count];
            if (prepared.Count > 0)
            {
                WithDb(createIfMissing: prepared.Any(item => item.Keep), db => db.RunTransaction(() =>
                {
                    for (var index = 0; index < prepared.Count; index++)
                    {
                        changed[index] = Write(db, prepared[index].GameId, prepared[index].Rows);
                    }
                }));
            }

            return prepared
                .Select((item, index) => (item.Keep ? Read(item.GameId, item.Rows) : null, changed[index]))
                .ToList();
        }

        /// <summary>Normalizes and writes every record in one transaction; returns the games whose rows moved.</summary>
        public IReadOnlyList<Guid> SaveMany(IEnumerable<GameCustomDataFile> items)
        {
            var pending = (items ?? Enumerable.Empty<GameCustomDataFile>())
                .Where(item => item != null && item.PlayniteGameId != Guid.Empty)
                .Select(item => GameCustomDataNormalizer.NormalizeInternal(item, item.PlayniteGameId))
                .Select(normalized => new KeyValuePair<Guid, GameCustomLayerRows>(
                    normalized.PlayniteGameId,
                    GameCustomDataNormalizer.HasInternalData(normalized) ? ToRows(normalized) : null))
                .ToList();
            var changed = new List<Guid>();
            if (pending.Count == 0)
            {
                return changed;
            }

            WithDb(createIfMissing: pending.Any(pair => pair.Value != null), db => db.RunTransaction(() =>
            {
                foreach (var pair in pending.Where(pair => Write(db, pair.Key, pair.Value)))
                {
                    changed.Add(pair.Key);
                }
            }));
            return changed;
        }

        /// <summary>Deletes the game's rows; true when it had any.</summary>
        public bool Delete(Guid playniteGameId)
        {
            var deleted = false;
            if (playniteGameId != Guid.Empty)
            {
                WithDb(createIfMissing: false, db => db.RunTransaction(() => deleted = Write(db, playniteGameId, null)));
            }

            return deleted;
        }

        /// <summary>
        /// Rewrites every record stored under an older schema, and reports how many.
        /// </summary>
        /// <remarks>
        /// Normalization upgrades a record on the way out of every read, but nothing wrote the
        /// upgraded shape back unless that particular game was edited afterwards. A game the user
        /// has not touched since the schema moved therefore stayed at its old version, and any such
        /// record arms the migration backup on every launch. This persists exactly what a read
        /// already hands every caller, after the backup the read path takes.
        /// </remarks>
        public int UpgradeStoredRecordsToCurrentSchema()
        {
            var stale = WithDb(
                createIfMissing: false,
                db => Games(db, $"SELECT {GameColumns} FROM {AchievementLayerSql.GamesTable};")
                    .Where(row => GameCustomLayer.SchemaVersionOf(row.SettingsJson) < GameCustomDataNormalizer.CurrentSchemaVersion)
                    .Select(row => Guid.TryParse(row.PlayniteGameId, out var id) ? id : Guid.Empty)
                    .Where(id => id != Guid.Empty)
                    .ToList(),
                new List<Guid>());
            var upgraded = stale
                .Select(id => TryLoad(id, out var data) ? data : GameCustomDataNormalizer.CreateDefault(id))
                .ToList();
            if (upgraded.Count == 0)
            {
                return 0;
            }

            SaveMany(upgraded);
            _logger?.Info(
                $"[GameCustomData] Upgraded {upgraded.Count} record(s) to schema {GameCustomDataNormalizer.CurrentSchemaVersion}. " +
                "The migration backup will not be taken again unless a record predates the schema.");
            return upgraded.Count;
        }

        /// <summary>
        /// Points every stored path under the old icon cache folder at the same file under the new
        /// one, in the custom data and the capstone art records, in one transaction. Returns how
        /// many records changed.
        /// </summary>
        public int RewriteIconCacheRoot(string oldRoot, string newRoot)
        {
            var rebased = new List<GameCustomDataFile>();
            foreach (var data in EnumerateAllNormalized())
            {
                if (IconCachePathRebaser.TryRebaseJson(JsonConvert.SerializeObject(data, GameCustomLayer.JsonSettings), oldRoot, newRoot, out var json))
                {
                    rebased.Add(JsonConvert.DeserializeObject<GameCustomDataFile>(json, GameCustomLayer.JsonSettings));
                }
            }

            var changedArt = LoadAllCapstoneArtRecords()
                .Where(record => IconCacheLocationRebase(record, oldRoot, newRoot))
                .ToList();

            if (rebased.Count == 0 && changedArt.Count == 0)
            {
                return 0;
            }

            SaveMany(rebased);
            WithDb(createIfMissing: false, db => db.RunTransaction(() =>
            {
                foreach (var record in changedArt)
                {
                    db.ExecuteNonQuery(
                        $"UPDATE {CapstoneArtTableName} SET Path = ? WHERE PlayniteGameId = ? AND ApiName = ?;",
                        record.Path,
                        record.PlayniteGameId,
                        record.ApiName);
                }
            }));

            _logger?.Info(
                $"[GameCustomData] Moved stored icon paths to the new icon cache folder in " +
                $"{rebased.Count} record(s) and {changedArt.Count} capstone art record(s).");
            return rebased.Count + changedArt.Count;
        }

        private static bool IconCacheLocationRebase(CapstoneArtRecordRow record, string oldRoot, string newRoot)
        {
            var rebased = Images.IconCachePathRebaser.RebasePath(record?.Path, oldRoot, newRoot);
            if (record == null || string.Equals(rebased, record.Path, StringComparison.Ordinal))
            {
                return false;
            }

            record.Path = rebased;
            return true;
        }

        private GameCustomLayerRows ToRows(GameCustomDataFile normalized)
        {
            var iconCacheRoot = IconCacheLocation.CurrentRootOrDefault(_pluginDataRoot);
            return GameCustomLayer.ToRows(normalized, path => StoredPathMapping.ToStored(path, _pluginDataRoot, iconCacheRoot));
        }

        private GameCustomDataFile Read(Guid gameId, GameCustomLayerRows rows)
        {
            var iconCacheRoot = IconCacheLocation.CurrentRootOrDefault(_pluginDataRoot);
            return GameCustomDataNormalizer.NormalizeInternal(
                GameCustomLayer.FromRows(gameId, rows.State, rows.Achievements, rows.Categories, path => StoredPathMapping.FromStored(path, _pluginDataRoot, iconCacheRoot)),
                gameId);
        }

        /// <summary>The records the rows hold, normalized; a game whose settings will not parse is skipped.</summary>
        private List<GameCustomDataFile> Load(
            IEnumerable<CustomGameStateRow> games,
            IEnumerable<CustomAchievementRow> achievements,
            IEnumerable<CustomCategoryRow> categories)
        {
            var achievementsByGame = achievements.ToLookup(row => row.PlayniteGameId, StringComparer.OrdinalIgnoreCase);
            var categoriesByGame = categories.ToLookup(row => row.PlayniteGameId, StringComparer.OrdinalIgnoreCase);
            var results = new List<GameCustomDataFile>();
            foreach (var game in games)
            {
                if (!Guid.TryParse(game?.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                try
                {
                    // Normalization upgrades the record in place, so this is the last point where
                    // the stored schema version is still visible.
                    EnsureSchemaMigrationBackup(GameCustomLayer.SchemaVersionOf(game.SettingsJson));
                    var data = Read(gameId, new GameCustomLayerRows
                    {
                        State = game,
                        Achievements = achievementsByGame[game.PlayniteGameId].ToList(),
                        Categories = categoriesByGame[game.PlayniteGameId].ToList()
                    });
                    if (GameCustomDataNormalizer.HasInternalData(data))
                    {
                        results.Add(data);
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed loading per-game custom data for gameId={gameId}.");
                }
            }

            return results;
        }

        /// <summary>
        /// Replaces one game's rows with <paramref name="rows"/>, or deletes them when null,
        /// writing only the rows that differ. Runs inside the caller's transaction. True when a
        /// row moved.
        /// </summary>
        private static bool Write(SQLiteDatabase db, Guid playniteGameId, GameCustomLayerRows rows)
        {
            var idText = ToGuidText(playniteGameId);
            var storedGame = Games(db, $"SELECT {GameColumns} FROM {AchievementLayerSql.GamesTable} WHERE PlayniteGameId = ?;", idText)
                .FirstOrDefault();
            var storedAchievements = Achievements(db, $"SELECT {AchievementColumns} FROM {AchievementLayerSql.AchievementsTable} WHERE PlayniteGameId = ?;", idText)
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var storedCategories = CategorySignature(
                Categories(db, $"SELECT {CategoryColumns} FROM {AchievementLayerSql.CategoriesTable} WHERE PlayniteGameId = ?;", idText));

            var desiredAchievements = (rows?.Achievements ?? new List<CustomAchievementRow>())
                .GroupBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            var changed = false;

            if (!string.Equals(storedGame?.Signature(), rows?.State?.Signature(), StringComparison.Ordinal))
            {
                db.ExecuteNonQuery($"DELETE FROM {AchievementLayerSql.GamesTable} WHERE PlayniteGameId = ?;", idText);
                if (rows != null)
                {
                    var state = rows.State;
                    db.ExecuteNonQuery(
                        $"INSERT INTO {AchievementLayerSql.GamesTable} ({GameColumns}) VALUES (?, ?, ?, ?, ?, ?, ?);",
                        idText,
                        state.CapstonesMaterialized,
                        state.HasManualLink,
                        state.ExcludedFromSummaries,
                        state.ExcludedFromRefreshes,
                        state.UseSeparateLockedIcons,
                        state.SettingsJson);
                }

                changed = true;
            }

            // Only the rows that moved: an edit to one achievement of a heavily customized game
            // writes that achievement's row, not every row the game has.
            var nowIso = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            var insertSql = $"INSERT INTO {AchievementLayerSql.AchievementsTable} ({AchievementColumns}, UpdatedUtc) VALUES (" +
                string.Join(", ", Enumerable.Repeat("?", AchievementColumns.Split(',').Length + 1)) + ");";
            var deleteSql = $"DELETE FROM {AchievementLayerSql.AchievementsTable} WHERE PlayniteGameId = ? AND ApiName = ? AND IsAuthored = ?;";
            foreach (var row in storedAchievements.Where(pair => !desiredAchievements.ContainsKey(pair.Key)).Select(pair => pair.Value).ToList())
            {
                db.ExecuteNonQuery(deleteSql, idText, row.ApiName, row.IsAuthored);
                changed = true;
            }

            foreach (var row in desiredAchievements.Values)
            {
                if (storedAchievements.TryGetValue(row.Key, out var stored) &&
                    string.Equals(stored.Signature(), row.Signature(), StringComparison.Ordinal))
                {
                    continue;
                }

                db.ExecuteNonQuery(deleteSql, idText, row.ApiName, row.IsAuthored);
                db.ExecuteNonQuery(insertSql, new object[] { idText }.Concat(row.Values()).Concat(new object[] { nowIso }).ToArray());
                changed = true;
            }

            if (!string.Equals(storedCategories, CategorySignature(rows?.Categories), StringComparison.Ordinal))
            {
                db.ExecuteNonQuery($"DELETE FROM {AchievementLayerSql.CategoriesTable} WHERE PlayniteGameId = ?;", idText);
                foreach (var row in rows?.Categories ?? new List<CustomCategoryRow>())
                {
                    db.ExecuteNonQuery(
                        $"INSERT OR REPLACE INTO {AchievementLayerSql.CategoriesTable} ({CategoryColumns}) VALUES (?, ?, ?, ?, ?);",
                        idText,
                        row.Category,
                        row.Kind,
                        row.SortIndex,
                        row.ImagePath);
                }

                changed = true;
            }

            return changed;
        }

        private static List<CustomGameStateRow> Games(SQLiteDatabase db, string sql, params object[] args) =>
            db.LoadObjects(sql, args).Select(CustomGameStateRow.From).ToList();

        private static List<CustomAchievementRow> Achievements(SQLiteDatabase db, string sql, params object[] args) =>
            db.LoadObjects(sql, args).Select(CustomAchievementRow.From).ToList();

        private static List<CustomCategoryRow> Categories(SQLiteDatabase db, string sql, params object[] args) =>
            db.LoadObjects(sql, args).Select(CustomCategoryRow.From).ToList();

        private static string CategorySignature(IEnumerable<CustomCategoryRow> rows) =>
            string.Join("\n\n", (rows ?? Enumerable.Empty<CustomCategoryRow>())
                .Select(row => row.Signature())
                .OrderBy(signature => signature, StringComparer.Ordinal));

        /// <summary>
        /// Copies the custom-data database aside once per session, before the first record written
        /// by an older schema is upgraded. The database holds authorship that cannot be re-fetched,
        /// so a migration defect must not leave the migrated copy as the only one.
        /// </summary>
        /// <param name="onDiskSchemaVersion">
        /// The stored version, before normalization rewrites it. Zero means a record from before
        /// the version was stamped, which normalization still folds.
        /// </param>
        private void EnsureSchemaMigrationBackup(int onDiskSchemaVersion)
        {
            if (onDiskSchemaVersion >= GameCustomDataNormalizer.CurrentSchemaVersion)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_migrationBackupAttempted)
                {
                    return;
                }

                // Set before the attempt so a failing backup is not retried on every record.
                _migrationBackupAttempted = true;
            }

            try
            {
                var backupPath = CreateBackup(string.Format(
                    CultureInfo.InvariantCulture,
                    "customdata-schema{0}",
                    GameCustomDataNormalizer.CurrentSchemaVersion));
                _logger?.Info(
                    $"[GameCustomData] Schema {onDiskSchemaVersion} -> " +
                    $"{GameCustomDataNormalizer.CurrentSchemaVersion} migration backup created: {backupPath}");
            }
            catch (Exception ex)
            {
                // Reads must keep working: refusing to load would strand the user's data rather
                // than protect it. The failure is logged so a lost backup is diagnosable.
                _logger?.Error(ex, "[GameCustomData] Failed to create schema migration backup.");
            }
        }

        private string CreateBackup(string prefix) =>
            BackupHelper.CreateBackup(_pluginDataRoot, prefix, _databasePath, _databasePath + "-wal", _databasePath + "-shm");

        /// <summary>
        /// Moves the legacy <c>GameCustomData</c> payloads into the custom layer tables, once, in
        /// one transaction with its marker, after a backup of the database. The legacy table is left
        /// as it was. A payload written after the marker, by an earlier version run on this
        /// database, is moved again.
        /// </summary>
        private void MigrateLegacyPayloads(SQLiteDatabase db)
        {
            if (db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = ?;", LegacyTableName) == 0)
            {
                return;
            }

            var marker = db.ExecuteScalar<string>(
                "SELECT Value FROM CacheMetadata WHERE Key = ?;", new object[] { AchievementLayerSql.CustomLayerMigratedKey });
            var pending = db.Load<LegacyPayloadRow>(
                    $"SELECT p.PlayniteGameId AS PlayniteGameId, p.PayloadJson AS PayloadJson, p.UpdatedUtc AS UpdatedUtc FROM {LegacyTableName} p " +
                    $"WHERE ? IS NULL OR p.UpdatedUtc > ?;",
                    marker,
                    marker)
                .ToList();
            if (pending.Count == 0 && marker != null)
            {
                return;
            }

            var result = new MigrationResult();
            if (pending.Count > 0)
            {
                result.BackupPath = CreateBackup("customdata-layer");
            }

            var parsed = new List<KeyValuePair<Guid, GameCustomLayerRows>>();
            foreach (var row in pending)
            {
                if (!Guid.TryParse(row.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                GameCustomDataFile normalized;
                try
                {
                    normalized = GameCustomDataNormalizer.NormalizeInternal(
                        JsonConvert.DeserializeObject<GameCustomDataFile>(row.PayloadJson ?? string.Empty),
                        gameId);
                }
                catch (Exception ex)
                {
                    result.Unreadable++;
                    _logger?.Warn(ex, $"[GameCustomData] Custom data of game {gameId} would not parse; left in {LegacyTableName} only.");
                    continue;
                }

                var rows = GameCustomDataNormalizer.HasInternalData(normalized) ? ToRows(normalized) : null;
                parsed.Add(new KeyValuePair<Guid, GameCustomLayerRows>(gameId, rows));
            }

            db.RunTransaction(() =>
            {
                foreach (var pair in parsed)
                {
                    Write(db, pair.Key, pair.Value);
                    result.Migrated++;
                }

                db.ExecuteNonQuery(
                    "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                    AchievementLayerSql.CustomLayerMigratedKey,
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            });

            LastMigration = result;
            if (pending.Count > 0)
            {
                _logger?.Info(
                    $"[GameCustomData] Moved {result.Migrated} game(s) of custom data into the custom layer tables " +
                    $"({result.Unreadable} unreadable); backup: {result.BackupPath}.");
            }
        }

        private TResult WithDb<TResult>(bool createIfMissing, Func<SQLiteDatabase, TResult> action, TResult defaultValue)
        {
            if (action == null)
            {
                throw new ArgumentNullException(nameof(action));
            }

            lock (_syncRoot)
            {
                var databaseExists = File.Exists(_databasePath);
                if (!databaseExists && !createIfMissing)
                {
                    return defaultValue;
                }

                if (createIfMissing)
                {
                    EnsureParentDirectory();
                }

                using (var db = OpenDatabase(createIfMissing))
                {
                    if (!_schemaInitialized || !databaseExists)
                    {
                        EnsureSchema(db);
                        MoveInLegacyDatabase(db);
                        MigrateLegacyPayloads(db);
                        _schemaInitialized = true;
                    }

                    return action(db);
                }
            }
        }

        private void WithDb(bool createIfMissing, Action<SQLiteDatabase> action)
        {
            WithDb(
                createIfMissing,
                db =>
                {
                    action(db);
                    return true;
                },
                defaultValue: false);
        }

        private SQLiteDatabase OpenDatabase(bool createIfMissing)
        {
            var options = SQLiteOpenOptions.SQLITE_OPEN_READWRITE |
                          SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX;

            if (createIfMissing)
            {
                options |= SQLiteOpenOptions.SQLITE_OPEN_CREATE;
            }

            // The achievement cache reads and writes this database on its own connections.
            return new SQLiteDatabase(_databasePath, options) { BusyTimeout = BusyTimeoutMs };
        }

        private void EnsureParentDirectory()
        {
            var parent = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
            }
        }

        private static void EnsureSchema(SQLiteDatabase db)
        {
            db.ExecuteNonQuery("PRAGMA journal_mode = WAL;");
            db.ExecuteNonQuery("PRAGMA synchronous = NORMAL;");
            db.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS CacheMetadata (Key TEXT PRIMARY KEY NOT NULL, Value TEXT NOT NULL);");
            AchievementLayerSql.EnsureCustomLayerTables(db);
            CreateCapstoneArtTable(db);
        }

        // Not custom data: portable packages and Workshop exports never carry these rows, so they
        // stay local to this database.
        private static void CreateCapstoneArtTable(SQLiteDatabase db)
        {
            db.ExecuteNonQuery(
                $@"CREATE TABLE IF NOT EXISTS {CapstoneArtTableName} (
                    PlayniteGameId TEXT NOT NULL,
                    ApiName TEXT NOT NULL COLLATE NOCASE,
                    Title TEXT NULL,
                    Description TEXT NULL,
                    Source TEXT NULL,
                    Path TEXT NULL,
                    Length INTEGER NULL,
                    WriteUtc TEXT NULL,
                    PRIMARY KEY (PlayniteGameId, ApiName)
                );");
        }

        /// <summary>
        /// Loads every CapstoneArtRecords row. Returns an empty list when the database file does
        /// not exist.
        /// </summary>
        public IReadOnlyList<CapstoneArtRecordRow> LoadAllCapstoneArtRecords()
        {
            return WithDb(
                createIfMissing: false,
                db => db.Load<CapstoneArtRecordRow>(
                        $@"SELECT PlayniteGameId, ApiName, Title, Description, Source, Path, Length, WriteUtc
                           FROM {CapstoneArtTableName};")
                    .Where(row => row != null)
                    .ToList(),
                new List<CapstoneArtRecordRow>());
        }

        /// <summary>
        /// Upserts one game's capstone art record for <paramref name="apiName"/>, or deletes it
        /// when <paramref name="record"/> is null. The row is keyed by the given game and api name,
        /// not by the record's own PlayniteGameId and ApiName.
        /// </summary>
        public void SetCapstoneArtRecord(Guid playniteGameId, string apiName, CapstoneArtRecordRow record)
        {
            if (playniteGameId == Guid.Empty || string.IsNullOrWhiteSpace(apiName))
            {
                return;
            }

            var gameIdText = ToGuidText(playniteGameId);
            if (record == null)
            {
                WithDb(createIfMissing: false, db =>
                {
                    db.ExecuteNonQuery(
                        $"DELETE FROM {CapstoneArtTableName} WHERE PlayniteGameId = ? AND ApiName = ?;",
                        gameIdText,
                        apiName);
                });
                return;
            }

            WithDb(createIfMissing: true, db =>
            {
                UpsertCapstoneArtRecord(db, gameIdText, apiName, record);
            });
        }

        /// <summary>
        /// Upserts every row in one transaction. Rows without a parseable non-empty PlayniteGameId
        /// or with a blank ApiName are skipped.
        /// </summary>
        public void ImportCapstoneArtRecords(IReadOnlyList<CapstoneArtRecordRow> rows)
        {
            var valid = new List<KeyValuePair<string, CapstoneArtRecordRow>>();
            foreach (var row in rows ?? Array.Empty<CapstoneArtRecordRow>())
            {
                if (row == null ||
                    string.IsNullOrWhiteSpace(row.ApiName) ||
                    !Guid.TryParse(row.PlayniteGameId, out var gameId) ||
                    gameId == Guid.Empty)
                {
                    continue;
                }

                valid.Add(new KeyValuePair<string, CapstoneArtRecordRow>(ToGuidText(gameId), row));
            }

            if (valid.Count == 0)
            {
                return;
            }

            WithDb(createIfMissing: true, db =>
            {
                db.RunTransaction(() =>
                {
                    foreach (var pair in valid)
                    {
                        UpsertCapstoneArtRecord(db, pair.Key, pair.Value.ApiName, pair.Value);
                    }
                });
            });
        }

        /// <summary>
        /// Deletes every CapstoneArtRecords row for one game.
        /// </summary>
        public void DeleteCapstoneArtRecords(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            WithDb(createIfMissing: false, db =>
            {
                db.ExecuteNonQuery(
                    $"DELETE FROM {CapstoneArtTableName} WHERE PlayniteGameId = ?;",
                    ToGuidText(playniteGameId));
            });
        }

        private static void UpsertCapstoneArtRecord(
            SQLiteDatabase db,
            string gameIdText,
            string apiName,
            CapstoneArtRecordRow record)
        {
            db.ExecuteNonQuery(
                $@"INSERT OR REPLACE INTO {CapstoneArtTableName}
                       (PlayniteGameId, ApiName, Title, Description, Source, Path, Length, WriteUtc)
                   VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                gameIdText,
                apiName,
                record.Title,
                record.Description,
                record.Source,
                record.Path,
                record.Length.HasValue ? (object)record.Length.Value : null,
                record.WriteUtc);
        }

        /// <summary>
        /// Copies the separate custom data file's tables into this database once, in one
        /// transaction with a marker, then moves the file into the migration backups. A
        /// <c>GameCustomData</c> table an older version left in this database is stale and is
        /// replaced, not merged.
        /// </summary>
        private void MoveInLegacyDatabase(SQLiteDatabase db)
        {
            var root = Path.GetDirectoryName(_databasePath) ?? string.Empty;
            var legacyPath = Path.Combine(root, LegacyDatabaseFileName);
            if (!File.Exists(legacyPath))
            {
                return;
            }

            db.ExecuteNonQuery("CREATE TABLE IF NOT EXISTS CacheMetadata (Key TEXT PRIMARY KEY NOT NULL, Value TEXT NOT NULL);");
            if (db.ExecuteScalar<long>("SELECT COUNT(1) FROM CacheMetadata WHERE Key = ?;", MovedInMarkerKey) == 0)
            {
                db.ExecuteNonQuery("ATTACH DATABASE ? AS legacy;", legacyPath);
                try
                {
                    bool LegacyHas(string table) => db.ExecuteScalar<long>(
                        "SELECT COUNT(1) FROM legacy.sqlite_master WHERE type = 'table' AND name = ?;", table) > 0;

                    db.RunTransaction(() =>
                    {
                        db.ExecuteNonQuery("DROP TABLE IF EXISTS main.GameCustomDataAchievementFilter;");
                        db.ExecuteNonQuery("DROP TABLE IF EXISTS main." + LegacyTableName + ";");
                        db.ExecuteNonQuery("DROP TABLE IF EXISTS main." + CapstoneArtTableName + ";");
                        db.ExecuteNonQuery("CREATE TABLE " + LegacyTableName + " (PlayniteGameId TEXT PRIMARY KEY NOT NULL, PayloadJson TEXT NOT NULL, UpdatedUtc TEXT NOT NULL);");
                        CreateCapstoneArtTable(db);
                        if (LegacyHas(LegacyTableName))
                        {
                            db.ExecuteNonQuery(
                                "INSERT INTO main." + LegacyTableName + " (PlayniteGameId, PayloadJson, UpdatedUtc) " +
                                "SELECT PlayniteGameId, PayloadJson, UpdatedUtc FROM legacy." + LegacyTableName + ";");
                        }

                        if (LegacyHas(CapstoneArtTableName))
                        {
                            db.ExecuteNonQuery(
                                "INSERT INTO main." + CapstoneArtTableName +
                                " (PlayniteGameId, ApiName, Title, Description, Source, Path, Length, WriteUtc) " +
                                "SELECT PlayniteGameId, ApiName, Title, Description, Source, Path, Length, WriteUtc FROM legacy." +
                                CapstoneArtTableName + ";");
                        }

                        db.ExecuteNonQuery(
                            "INSERT OR REPLACE INTO CacheMetadata (Key, Value) VALUES (?, ?);",
                            MovedInMarkerKey,
                            DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                    });
                }
                finally
                {
                    db.ExecuteNonQuery("DETACH DATABASE legacy;");
                }

                _logger?.Info(
                    $"[GameCustomData] Moved {db.ExecuteScalar<long>("SELECT COUNT(1) FROM " + LegacyTableName + ";")} " +
                    $"games of custom data from {LegacyDatabaseFileName} into {Path.GetFileName(_databasePath)}.");
            }

            // The marker keeps a file that cannot be moved from being copied in again.
            try
            {
                var files = new[] { legacyPath, legacyPath + "-wal", legacyPath + "-shm" };
                var backupPath = BackupHelper.CreateBackup(root, "customdata-file", files);
                foreach (var file in files.Where(File.Exists))
                {
                    File.Delete(file);
                }

                _logger?.Info($"[GameCustomData] Moved {LegacyDatabaseFileName} to {backupPath}.");
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[GameCustomData] Failed to move {LegacyDatabaseFileName} into the backups.");
            }
        }

        private static string ToGuidText(Guid playniteGameId)
        {
            return playniteGameId.ToString("D");
        }
    }
}
