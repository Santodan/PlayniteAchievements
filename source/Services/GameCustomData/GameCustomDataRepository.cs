using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.GameCustomData
{
    internal sealed class GameCustomDataRepository
    {
        private const string TableName = "GameCustomData";

        private sealed class GameCustomDataRow
        {
            public string PlayniteGameId { get; set; }

            public string PayloadJson { get; set; }

            public string UpdatedUtc { get; set; }
        }

        private readonly object _syncRoot = new object();
        private readonly string _databasePath;
        private readonly ILogger _logger;
        private readonly JsonSerializerSettings _writeSettings;
        private bool _schemaInitialized;
        private bool _migrationBackupAttempted;

        public GameCustomDataRepository(string databasePath, JsonSerializerSettings writeSettings, ILogger logger = null)
        {
            _databasePath = databasePath ?? string.Empty;
            _writeSettings = writeSettings ?? throw new ArgumentNullException(nameof(writeSettings));
            _logger = logger;
        }

        public string DatabasePath => _databasePath;

        /// <summary>
        /// Copies the custom-data database aside once per session, before the first record written
        /// by an older schema is upgraded. Unlike the achievement cache, this database holds
        /// authorship that cannot be re-fetched, so a migration defect must not leave the migrated
        /// copy as the only one.
        /// </summary>
        /// <param name="onDiskSchemaVersion">
        /// The version read from the payload, before normalization rewrites it. The current
        /// version means there is nothing to migrate; zero means a record from before the version
        /// was stamped, which normalization still folds.
        /// </param>
        private void EnsureSchemaMigrationBackup(int onDiskSchemaVersion)
        {
            // Zero means a record written before the version was stamped. Normalization folds
            // those exactly as it folds a numbered one, so they need the backup most, not least.
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
                var root = Path.GetDirectoryName(_databasePath);
                var backupPath = BackupHelper.CreateBackup(
                    root,
                    string.Format(
                        CultureInfo.InvariantCulture,
                        "customdata-schema{0}",
                        GameCustomDataNormalizer.CurrentSchemaVersion),
                    _databasePath,
                    _databasePath + "-wal",
                    _databasePath + "-shm");
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

        public bool TryLoad(Guid playniteGameId, out GameCustomDataFile data)
        {
            data = null;
            if (playniteGameId == Guid.Empty)
            {
                return false;
            }

            var row = WithDb(
                createIfMissing: false,
                db => db.Load<GameCustomDataRow>(
                    $"SELECT PlayniteGameId, PayloadJson, UpdatedUtc FROM {TableName} WHERE PlayniteGameId = ? LIMIT 1;",
                    ToGuidText(playniteGameId)).FirstOrDefault(),
                default(GameCustomDataRow));

            if (row == null)
            {
                return false;
            }

            if (TryDeserialize(playniteGameId, row.PayloadJson, out var normalized, out var shouldDelete))
            {
                data = normalized;
                return true;
            }

            if (shouldDelete)
            {
                Delete(playniteGameId);
            }

            return false;
        }

        public GameCustomDataFile LoadOrDefault(Guid playniteGameId)
        {
            return TryLoad(playniteGameId, out var data)
                ? data
                : GameCustomDataNormalizer.CreateDefault(playniteGameId);
        }

        /// <summary>
        /// Persists the game's custom data and returns the normalized instance that was written,
        /// so the caller can seed its cache without reading the row back. Returns null when the
        /// data normalized to nothing and the row was deleted instead.
        /// </summary>
        public GameCustomDataFile Save(Guid playniteGameId, GameCustomDataFile data)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, playniteGameId);
            if (!GameCustomDataNormalizer.HasInternalData(normalized))
            {
                Delete(playniteGameId);
                return null;
            }

            var row = CreateRow(normalized);
            WithDb(createIfMissing: true, db =>
            {
                db.ExecuteNonQuery(
                    $"INSERT OR REPLACE INTO {TableName} (PlayniteGameId, PayloadJson, UpdatedUtc) VALUES (?, ?, ?);",
                    row.PlayniteGameId,
                    row.PayloadJson,
                    row.UpdatedUtc);
            });
            return normalized;
        }

        public void SaveMany(IEnumerable<GameCustomDataFile> items)
        {
            var rows = new List<GameCustomDataRow>();
            var deleteIds = new List<Guid>();

            foreach (var item in items ?? Enumerable.Empty<GameCustomDataFile>())
            {
                if (item == null || item.PlayniteGameId == Guid.Empty)
                {
                    continue;
                }

                var normalized = GameCustomDataNormalizer.NormalizeInternal(item, item.PlayniteGameId);
                if (!GameCustomDataNormalizer.HasInternalData(normalized))
                {
                    deleteIds.Add(item.PlayniteGameId);
                    continue;
                }

                rows.Add(CreateRow(normalized));
            }

            if (rows.Count == 0)
            {
                DeleteMany(deleteIds);
                return;
            }

            WithDb(createIfMissing: true, db =>
            {
                db.RunTransaction(() =>
                {
                    foreach (var gameId in deleteIds.Distinct())
                    {
                        db.ExecuteNonQuery(
                            $"DELETE FROM {TableName} WHERE PlayniteGameId = ?;",
                            ToGuidText(gameId));
                    }

                    foreach (var row in rows)
                    {
                        db.ExecuteNonQuery(
                            $"INSERT OR REPLACE INTO {TableName} (PlayniteGameId, PayloadJson, UpdatedUtc) VALUES (?, ?, ?);",
                            row.PlayniteGameId,
                            row.PayloadJson,
                            row.UpdatedUtc);
                    }
                });
            });
        }

        public void Delete(Guid playniteGameId)
        {
            if (playniteGameId == Guid.Empty)
            {
                return;
            }

            DeleteMany(new[] { playniteGameId });
        }

        /// <summary>
        /// Rewrites every record still stored under an older schema, and reports how many.
        /// </summary>
        /// <remarks>
        /// Normalization upgrades a record on the way out of every read, but nothing wrote the
        /// upgraded shape back unless that particular game was edited afterwards. A game the user
        /// has not touched since the schema moved therefore stayed at its old version forever, and
        /// because any such record arms the migration backup, a full copy of this database was
        /// taken on every single launch - 184 of them, 348 MB, on the machine this was found on.
        ///
        /// Safe by construction rather than by care: this persists exactly what
        /// <see cref="TryDeserialize"/> already hands every caller, and deletes exactly what that
        /// method already asks callers to delete. Nothing here decides anything a read does not
        /// decide already - it only stops the answer being recomputed and thrown away. The backup
        /// is taken first, by the same guard the read path uses.
        /// </remarks>
        public int UpgradeStoredRecordsToCurrentSchema()
        {
            var rows = WithDb(
                createIfMissing: false,
                db => db.Load<GameCustomDataRow>(
                    $"SELECT PlayniteGameId, PayloadJson, UpdatedUtc FROM {TableName} ORDER BY PlayniteGameId;").ToList(),
                new List<GameCustomDataRow>());

            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            var upgraded = new List<GameCustomDataFile>();
            var deleteIds = new List<Guid>();

            foreach (var row in rows)
            {
                if (!Guid.TryParse(row?.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                // The stored version, read before normalization rewrites it. A record already at
                // the current version is left exactly as it is: untouched bytes cannot be damaged
                // by a defect in this sweep.
                int storedVersion;
                try
                {
                    storedVersion =
                        JsonConvert.DeserializeObject<GameCustomDataFile>(row.PayloadJson)?.SchemaVersion ?? 0;
                }
                catch (Exception ex)
                {
                    // Left alone deliberately. A payload that will not parse is not one to rewrite
                    // from a guess, and the read path already decides what to do with it.
                    _logger?.Warn(ex, $"Skipped schema upgrade for unreadable custom data, gameId={gameId}.");
                    continue;
                }

                if (storedVersion >= GameCustomDataNormalizer.CurrentSchemaVersion)
                {
                    continue;
                }

                if (TryDeserialize(gameId, row.PayloadJson, out var normalized, out var shouldDelete))
                {
                    upgraded.Add(normalized);
                }
                else if (shouldDelete)
                {
                    deleteIds.Add(gameId);
                }
            }

            if (upgraded.Count == 0 && deleteIds.Count == 0)
            {
                return 0;
            }

            SaveMany(upgraded);
            DeleteMany(deleteIds);

            var total = upgraded.Count + deleteIds.Count;
            _logger?.Info(
                $"[GameCustomData] Upgraded {upgraded.Count} record(s) to schema " +
                $"{GameCustomDataNormalizer.CurrentSchemaVersion} and dropped {deleteIds.Count} empty one(s). " +
                "The migration backup will not be taken again unless a record predates the schema.");
            return total;
        }

        public IEnumerable<GameCustomDataFile> EnumerateAllNormalized()
        {
            var rows = WithDb(
                createIfMissing: false,
                db => db.Load<GameCustomDataRow>(
                    $"SELECT PlayniteGameId, PayloadJson, UpdatedUtc FROM {TableName} ORDER BY PlayniteGameId;").ToList(),
                new List<GameCustomDataRow>());

            if (rows == null || rows.Count == 0)
            {
                return Array.Empty<GameCustomDataFile>();
            }

            var results = new List<GameCustomDataFile>(rows.Count);
            var deleteIds = new List<Guid>();

            foreach (var row in rows)
            {
                if (!Guid.TryParse(row?.PlayniteGameId, out var gameId) || gameId == Guid.Empty)
                {
                    continue;
                }

                if (TryDeserialize(gameId, row.PayloadJson, out var normalized, out var shouldDelete))
                {
                    results.Add(normalized);
                    continue;
                }

                if (shouldDelete)
                {
                    deleteIds.Add(gameId);
                }
            }

            DeleteMany(deleteIds);
            return results;
        }

        private void DeleteMany(IEnumerable<Guid> playniteGameIds)
        {
            var ids = (playniteGameIds ?? Enumerable.Empty<Guid>())
                .Where(a => a != Guid.Empty)
                .Distinct()
                .Select(ToGuidText)
                .ToList();

            if (ids.Count == 0)
            {
                return;
            }

            WithDb(createIfMissing: false, db =>
            {
                db.RunTransaction(() =>
                {
                    foreach (var id in ids)
                    {
                        db.ExecuteNonQuery(
                            $"DELETE FROM {TableName} WHERE PlayniteGameId = ?;",
                            id);
                    }
                });
            });
        }

        private bool TryDeserialize(
            Guid playniteGameId,
            string payloadJson,
            out GameCustomDataFile data,
            out bool shouldDelete)
        {
            data = null;
            shouldDelete = false;

            if (string.IsNullOrWhiteSpace(payloadJson))
            {
                shouldDelete = true;
                return false;
            }

            try
            {
                var parsed = JsonConvert.DeserializeObject<GameCustomDataFile>(payloadJson);
                // Normalization upgrades the record in place, so this is the last point where the
                // on-disk schema version is still visible.
                EnsureSchemaMigrationBackup(parsed?.SchemaVersion ?? 0);
                var normalized = GameCustomDataNormalizer.NormalizeInternal(parsed, playniteGameId);
                if (!GameCustomDataNormalizer.HasInternalData(normalized))
                {
                    shouldDelete = true;
                    return false;
                }

                data = normalized;
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed loading per-game custom data for gameId={playniteGameId}.");
                return false;
            }
        }

        private GameCustomDataRow CreateRow(GameCustomDataFile data)
        {
            return new GameCustomDataRow
            {
                PlayniteGameId = ToGuidText(data.PlayniteGameId),
                PayloadJson = JsonConvert.SerializeObject(data, _writeSettings),
                UpdatedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
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

            return new SQLiteDatabase(_databasePath, options);
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
            db.ExecuteNonQuery(
                @"CREATE TABLE IF NOT EXISTS GameCustomData (
                    PlayniteGameId TEXT PRIMARY KEY NOT NULL,
                    PayloadJson TEXT NOT NULL,
                    UpdatedUtc TEXT NOT NULL
                );");
        }

        private static string ToGuidText(Guid playniteGameId)
        {
            return playniteGameId.ToString("D");
        }
    }
}
