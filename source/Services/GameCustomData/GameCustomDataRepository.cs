using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteAchievements.Models.Settings;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services
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

        public GameCustomDataRepository(string databasePath, JsonSerializerSettings writeSettings, ILogger logger = null)
        {
            _databasePath = databasePath ?? string.Empty;
            _writeSettings = writeSettings ?? throw new ArgumentNullException(nameof(writeSettings));
            _logger = logger;
        }

        public string DatabasePath => _databasePath;

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

        public void Save(Guid playniteGameId, GameCustomDataFile data)
        {
            if (playniteGameId == Guid.Empty)
            {
                throw new ArgumentException("Game ID is required.", nameof(playniteGameId));
            }

            var normalized = GameCustomDataNormalizer.NormalizeInternal(data, playniteGameId);
            if (!GameCustomDataNormalizer.HasInternalData(normalized))
            {
                Delete(playniteGameId);
                return;
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
