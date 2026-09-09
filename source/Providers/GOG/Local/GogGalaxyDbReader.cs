using Playnite.SDK;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Providers.GOG.Local
{
    /// <summary>
    /// Reads per-user achievement unlock rows from the GOG Galaxy client's local SQLite database
    /// (UserAchievements: gameReleaseKey, userId, apikey, unlockTime, isUnlocked). Galaxy writes the
    /// database in WAL mode while it runs, so reads open a fresh read-only connection per call and
    /// report failure (busy, mid-checkpoint, Galaxy absent) instead of throwing; the in-game
    /// monitor's read schedule retries and the provider refresh prong remains the backstop.
    /// </summary>
    internal sealed class GogGalaxyDbReader
    {
        private const int BusyTimeoutMs = 250;

        private readonly ILogger _logger;

        public GogGalaxyDbReader(ILogger logger)
        {
            _logger = logger;
        }

        public static string GetDefaultDatabasePath()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "GOG.com",
                "Galaxy",
                "storage",
                "galaxy-2.0.db");
        }

        /// <summary>
        /// Reads the unlocked rows for one game release and user. Only positive observations are
        /// returned: the progress cache writer is monotonic, so a row Galaxy has not written yet can
        /// never retract an unlock reported by an earlier read.
        /// </summary>
        public bool TryRead(
            string databasePath,
            string releaseKey,
            long userId,
            out List<AchievementProgressObservation> observations)
        {
            observations = null;
            if (string.IsNullOrWhiteSpace(databasePath) ||
                string.IsNullOrWhiteSpace(releaseKey) ||
                !File.Exists(databasePath))
            {
                return false;
            }

            try
            {
                using (var db = new SQLiteDatabase(
                    databasePath,
                    SQLiteOpenOptions.SQLITE_OPEN_READONLY |
                    SQLiteOpenOptions.SQLITE_OPEN_FULLMUTEX))
                {
                    db.BusyTimeout = BusyTimeoutMs;
                    var rows = db.Load<UserAchievementRow>(
                        @"SELECT apikey AS ApiKey,
                                 unlockTime AS UnlockTime,
                                 isUnlocked AS IsUnlocked
                          FROM UserAchievements
                          WHERE gameReleaseKey = ? AND userId = ?;",
                        releaseKey,
                        userId).ToList();

                    observations = rows
                        .Where(row => row != null &&
                                      row.IsUnlocked != 0 &&
                                      !string.IsNullOrWhiteSpace(row.ApiKey))
                        .Select(row => new AchievementProgressObservation
                        {
                            ApiName = row.ApiKey,
                            Unlocked = true,
                            UnlockTimeUtc = ParseUnlockTime(row.UnlockTime)
                        })
                        .ToList();
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[GogAch] Galaxy database read failed for {releaseKey} at '{databasePath}'.");
                observations = null;
                return false;
            }
        }

        internal static DateTime? ParseUnlockTime(string unlockTime)
        {
            if (string.IsNullOrWhiteSpace(unlockTime))
            {
                return null;
            }

            var trimmed = unlockTime.Trim();
            if (DateTime.TryParseExact(
                trimmed,
                "yyyy-MM-dd HH:mm:ss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            {
                return parsed;
            }

            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epochSeconds) &&
                epochSeconds > 0)
            {
                return DateTimeOffset.FromUnixTimeSeconds(epochSeconds).UtcDateTime;
            }

            return null;
        }

        private sealed class UserAchievementRow
        {
            public string ApiKey { get; set; }
            public string UnlockTime { get; set; }
            public long IsUnlocked { get; set; }
        }
    }
}
