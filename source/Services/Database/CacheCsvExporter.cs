using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class CacheCsvExporter
    {
        private readonly SqlNadoCacheStore _store;

        internal CacheCsvExporter(SqlNadoCacheStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        /// <summary>
        /// Exports all database tables to CSV files in the specified directory.
        /// Returns the path to the directory containing the CSV files.
        /// </summary>
        public string ExportToCsv(string exportDirectory)
        {
            lock (_store._sync)
            {
                if (_store._db == null)
                {
                    throw new InvalidOperationException("Database not initialized.");
                }

                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                var dir = Path.Combine(exportDirectory, $"achievement_export_{timestamp}");
                Directory.CreateDirectory(dir);

                _store._logger.Info($"Exporting database to CSV: {dir}");

                // Export each table using typed row classes
                ExportAchievementDefinitions(dir);
                ExportUserGameProgress(dir);
                ExportUserAchievements(dir);
                ExportGames(dir);
                ExportUsers(dir);
                ExportAchievementSummary(dir);

                _store._logger.Info($"Database export completed: {dir}");
                return dir;
            }
        }

        private void ExportAchievementDefinitions(string dir)
        {
            var filePath = Path.Combine(dir, "AchievementDefinitions.csv");
            var rows = _store._db.Load<AchievementDefinitionExportRow>(
                "SELECT Id, GameId, ApiName, DisplayName, Description, " +
                "UnlockedIconPath, LockedIconPath, Points, Category, CategoryType, TrophyType, Hidden, IsCapstone, " +
                "GlobalPercentUnlocked, Rarity, ProgressMax, CreatedUtc, UpdatedUtc " +
                "FROM AchievementDefinitions").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "Id", "GameId", "ApiName", "DisplayName", "Description",
                "UnlockedIconPath", "LockedIconPath", "Points", "Category", "CategoryType", "TrophyType", "Hidden", "IsCapstone",
                "GlobalPercentUnlocked", "Rarity", "ProgressMax", "CreatedUtc", "UpdatedUtc"
            }, r => new[] {
                r.Id?.ToString(), r.GameId?.ToString(), r.ApiName, r.DisplayName, r.Description,
                r.UnlockedIconPath, r.LockedIconPath, r.Points?.ToString(), r.Category, r.CategoryType, r.TrophyType, r.Hidden?.ToString(), r.IsCapstone?.ToString(),
                r.GlobalPercentUnlocked?.ToString(), r.Rarity, r.ProgressMax?.ToString(), r.CreatedUtc, r.UpdatedUtc
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private void ExportUserGameProgress(string dir)
        {
            var filePath = Path.Combine(dir, "UserGameProgress.csv");
            var rows = _store._db.Load<UserGameProgressExportRow>(
                "SELECT Id, UserId, GameId, CacheKey, " +
                "HasAchievements, AchievementsUnlocked, TotalAchievements, " +
                "LastUpdatedUtc, CreatedUtc, UpdatedUtc " +
                "FROM UserGameProgress").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "Id", "UserId", "GameId", "CacheKey",
                "HasAchievements", "AchievementsUnlocked", "TotalAchievements",
                "LastUpdatedUtc", "CreatedUtc", "UpdatedUtc"
            }, r => new[] {
                r.Id.ToString(), r.UserId.ToString(), r.GameId.ToString(), r.CacheKey,
                r.HasAchievements.ToString(), r.AchievementsUnlocked.ToString(), r.TotalAchievements.ToString(),
                r.LastUpdatedUtc, r.CreatedUtc, r.UpdatedUtc
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private void ExportUserAchievements(string dir)
        {
            var filePath = Path.Combine(dir, "UserAchievements.csv");
            var rows = _store._db.Load<UserAchievementExportRow>(
                "SELECT Id, UserGameProgressId, AchievementDefinitionId, " +
                "Unlocked, UnlockTimeUtc, ProgressNum, ProgressDenom, " +
                "LastUpdatedUtc, CreatedUtc " +
                "FROM UserAchievements").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "Id", "UserGameProgressId", "AchievementDefinitionId",
                "Unlocked", "UnlockTimeUtc", "ProgressNum", "ProgressDenom",
                "LastUpdatedUtc", "CreatedUtc"
            }, r => new[] {
                r.Id.ToString(), r.UserGameProgressId.ToString(), r.AchievementDefinitionId.ToString(),
                r.Unlocked.ToString(), r.UnlockTimeUtc, r.ProgressNum?.ToString(), r.ProgressDenom?.ToString(),
                r.LastUpdatedUtc, r.CreatedUtc
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private void ExportGames(string dir)
        {
            var filePath = Path.Combine(dir, "Games.csv");
            var rows = _store._db.Load<GameExportRow>(
                "SELECT Id, ProviderKey, ProviderPlatformKey, ProviderGameId, PlayniteGameId, GameName, " +
                "LibrarySourceName, FirstSeenUtc, LastUpdatedUtc " +
                "FROM Games").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "Id", "ProviderKey", "ProviderPlatformKey", "ProviderGameId", "PlayniteGameId", "GameName",
                "LibrarySourceName", "FirstSeenUtc", "LastUpdatedUtc"
            }, r => new[] {
                r.Id.ToString(), r.ProviderKey, r.ProviderPlatformKey, r.ProviderGameId?.ToString(), r.PlayniteGameId, r.GameName,
                r.LibrarySourceName, r.FirstSeenUtc, r.LastUpdatedUtc
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private void ExportUsers(string dir)
        {
            var filePath = Path.Combine(dir, "Users.csv");
            var rows = _store._db.Load<UserExportRow>(
                "SELECT Id, ProviderKey, ExternalUserId, DisplayName, IsCurrentUser, " +
                "FriendSource, CreatedUtc, UpdatedUtc " +
                "FROM Users").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "Id", "ProviderKey", "ExternalUserId", "DisplayName", "IsCurrentUser",
                "FriendSource", "CreatedUtc", "UpdatedUtc"
            }, r => new[] {
                r.Id.ToString(), r.ProviderKey, r.ExternalUserId, r.DisplayName, r.IsCurrentUser.ToString(),
                r.FriendSource, r.CreatedUtc, r.UpdatedUtc
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private void ExportAchievementSummary(string dir)
        {
            var filePath = Path.Combine(dir, "AchievementSummary.csv");
            var rows = _store._db.Load<AchievementSummaryExportRow>(
                "SELECT g.GameName, g.ProviderKey, g.PlayniteGameId, " +
                "ad.ApiName, ad.DisplayName AS AchievementName, ad.Description, ad.Points, ad.Category, ad.CategoryType, ad.TrophyType, " +
                "ad.GlobalPercentUnlocked, ad.Rarity, ad.Hidden, " +
                "ua.Unlocked, ua.UnlockTimeUtc, u.DisplayName AS UserName " +
                "FROM AchievementDefinitions ad " +
                "JOIN Games g ON ad.GameId = g.Id " +
                "LEFT JOIN UserAchievements ua ON ua.AchievementDefinitionId = ad.Id " +
                "LEFT JOIN UserGameProgress ugp ON ua.UserGameProgressId = ugp.Id " +
                "LEFT JOIN Users u ON ugp.UserId = u.Id " +
                "ORDER BY g.GameName, ad.DisplayName").ToList();
            WriteCsv(filePath, rows, new[]
            {
                "GameName", "ProviderKey", "PlayniteGameId",
                "ApiName", "AchievementName", "Description", "Points", "Category", "CategoryType", "TrophyType",
                "GlobalPercentUnlocked", "Rarity", "Hidden",
                "Unlocked", "UnlockTimeUtc", "UserName"
            }, r => new[] {
                r.GameName, r.ProviderKey, r.PlayniteGameId,
                r.ApiName, r.AchievementName, r.Description, r.Points?.ToString(), r.Category, r.CategoryType, r.TrophyType,
                r.GlobalPercentUnlocked?.ToString(), r.Rarity, r.Hidden?.ToString(),
                r.Unlocked?.ToString(), r.UnlockTimeUtc, r.UserName
            });
            _store._logger.Info($"Exported {rows.Count} rows to {filePath}");
        }

        private static void WriteCsv<T>(string filePath, List<T> rows, string[] headers, Func<T, string[]> getValues)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine(string.Join(",", headers.Select(EscapeCsvField)));
                foreach (var row in rows)
                {
                    var values = getValues(row);
                    writer.WriteLine(string.Join(",", values.Select(v => EscapeCsvField(v ?? ""))));
                }
            }
        }

        private static string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field))
            {
                return "";
            }

            if (field.Contains(",") || field.Contains("\n") || field.Contains("\r") || field.Contains("\""))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }

            return field;
        }

        // Export row DTOs
        private sealed class AchievementDefinitionExportRow
        {
            public long? Id { get; set; }
            public long? GameId { get; set; }
            public string ApiName { get; set; }
            public string DisplayName { get; set; }
            public string Description { get; set; }
            public string UnlockedIconPath { get; set; }
            public string LockedIconPath { get; set; }
            public int? Points { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public bool? Hidden { get; set; }
            public bool? IsCapstone { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public int? ProgressMax { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class UserGameProgressExportRow
        {
            public long Id { get; set; }
            public long UserId { get; set; }
            public long GameId { get; set; }
            public string CacheKey { get; set; }
            public long HasAchievements { get; set; }
            public long AchievementsUnlocked { get; set; }
            public long TotalAchievements { get; set; }
            public string LastUpdatedUtc { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class UserAchievementExportRow
        {
            public long Id { get; set; }
            public long UserGameProgressId { get; set; }
            public long AchievementDefinitionId { get; set; }
            public long Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
            public string LastUpdatedUtc { get; set; }
            public string CreatedUtc { get; set; }
        }

        private sealed class GameExportRow
        {
            public long Id { get; set; }
            public string ProviderKey { get; set; }
            public string ProviderPlatformKey { get; set; }
            public long? ProviderGameId { get; set; }
            public string PlayniteGameId { get; set; }
            public string GameName { get; set; }
            public string LibrarySourceName { get; set; }
            public string FirstSeenUtc { get; set; }
            public string LastUpdatedUtc { get; set; }
        }

        private sealed class UserExportRow
        {
            public long Id { get; set; }
            public string ProviderKey { get; set; }
            public string ExternalUserId { get; set; }
            public string DisplayName { get; set; }
            public long IsCurrentUser { get; set; }
            public string FriendSource { get; set; }
            public string CreatedUtc { get; set; }
            public string UpdatedUtc { get; set; }
        }

        private sealed class AchievementSummaryExportRow
        {
            public string GameName { get; set; }
            public string ProviderKey { get; set; }
            public string PlayniteGameId { get; set; }
            public string ApiName { get; set; }
            public string AchievementName { get; set; }
            public string Description { get; set; }
            public int? Points { get; set; }
            public string Category { get; set; }
            public string CategoryType { get; set; }
            public string TrophyType { get; set; }
            public double? GlobalPercentUnlocked { get; set; }
            public string Rarity { get; set; }
            public bool? Hidden { get; set; }
            public bool? Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public string UserName { get; set; }
        }
    }
}
