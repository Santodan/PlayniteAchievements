using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services.Cache;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    internal sealed class LegacyJsonCacheImporter
    {
        private readonly CacheStorage _storage;
        private readonly SqlNadoCacheStore _store;
        private readonly ILogger _logger;

        public LegacyJsonCacheImporter(CacheStorage storage, SqlNadoCacheStore store, ILogger logger)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _logger = logger;
        }

        public void ImportIfNeeded()
        {
            _store.EnsureInitialized();
            var files = _storage.EnumerateUserCacheFiles()?.ToList() ?? new List<string>();
            var isMarkedDone = string.Equals(_store.GetMetadata("legacy_import_done"), "1", StringComparison.Ordinal);
            if (isMarkedDone && files.Count <= 0)
            {
                return;
            }

            if (isMarkedDone && files.Count > 0)
            {
                _logger?.Warn(
                    $"Legacy import was marked as complete, but found {files.Count} legacy JSON files. " +
                    "Resetting legacy_import_done and re-running import.");
                _store.SetMetadata("legacy_import_done", "0");
            }

            int imported = 0;
            int parseFailed = 0;
            int dbWriteFailed = 0;
            int deleted = 0;
            int deleteFailed = 0;
            int quarantined = 0;

            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                try
                {
                    var key = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(key))
                    {
                        parseFailed++;
                        quarantined += TryQuarantine(file, "invalid-key") ? 1 : 0;
                        continue;
                    }

                    if (!TryParseLegacyFile(file, out var data, out var parseError) || data == null)
                    {
                        parseFailed++;
                        var moved = TryQuarantine(file, "parse-failed");
                        if (moved)
                        {
                            quarantined++;
                        }

                        _logger?.Warn($"Failed importing legacy cache file (parse failed): {file}. Error={parseError ?? "Unknown"} Quarantined={moved}");
                        continue;
                    }

                    try
                    {
                        _store.SaveCurrentUserGameData(key, data);
                        imported++;
                    }
                    catch (Exception ex)
                    {
                        dbWriteFailed++;
                        _logger?.Warn(ex, $"Failed importing legacy cache file (db write failed): {file}");
                        continue;
                    }

                    try
                    {
                        _storage.DeleteFileIfExists(file);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        deleteFailed++;
                        _logger?.Warn(ex, $"Imported legacy cache but failed to delete source file: {file}");
                    }
                }
                catch (Exception ex)
                {
                    parseFailed++;
                    _logger?.Warn(ex, $"Failed importing legacy cache file: {file}");
                }
            }

            var remainingFileCount = _storage.EnumerateUserCacheFiles().Count();

            _store.SetMetadata("legacy_import_last_attempt_utc", DateTime.UtcNow.ToString("O"));
            _store.SetMetadata("legacy_import_imported_count", imported.ToString());
            _store.SetMetadata("legacy_import_parse_failed_count", parseFailed.ToString());
            _store.SetMetadata("legacy_import_db_write_failed_count", dbWriteFailed.ToString());
            _store.SetMetadata("legacy_import_deleted_count", deleted.ToString());
            _store.SetMetadata("legacy_import_delete_failed_count", deleteFailed.ToString());
            _store.SetMetadata("legacy_import_quarantined_count", quarantined.ToString());
            _store.SetMetadata("legacy_import_remaining_file_count", remainingFileCount.ToString());

            try
            {
                if (remainingFileCount == 0)
                {
                    _storage.DeleteDirectoryIfExists(_storage.UserCacheRootDir);
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to delete legacy achievement_cache directory after migration.");
            }

            if (SqlNadoCacheBehavior.ShouldMarkLegacyImportDone(parseFailed, dbWriteFailed, remainingFileCount))
            {
                _store.SetMetadata("legacy_import_done", "1");
                _store.SetMetadata("legacy_import_utc", DateTime.UtcNow.ToString("O"));
            }
            else
            {
                _store.SetMetadata("legacy_import_done", "0");
            }

            _logger?.Info(
                $"Legacy cache import finished. Imported={imported}, ParseFailed={parseFailed}, " +
                $"DbWriteFailed={dbWriteFailed}, Deleted={deleted}, DeleteFailed={deleteFailed}, " +
                $"Quarantined={quarantined}, Remaining={remainingFileCount}");
        }

        private bool TryQuarantine(string file, string reasonSuffix)
        {
            try
            {
                var quarantinePath = _storage.MoveFileToLegacyQuarantine(file, reasonSuffix);
                return !string.IsNullOrWhiteSpace(quarantinePath);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to quarantine malformed legacy file: {file}");
                return false;
            }
        }

        private static bool TryParseLegacyFile(string filePath, out GameAchievementData data, out string parseError)
        {
            data = null;
            parseError = null;

            try
            {
                var json = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(json))
                {
                    parseError = "JSON file is empty.";
                    return false;
                }

                var token = JToken.Parse(json);
                data = token.ToObject<GameAchievementData>();
                if (data == null)
                {
                    parseError = "JSON payload returned null model.";
                    return false;
                }

                if (data.Achievements == null)
                {
                    data.Achievements = new List<Models.Achievements.AchievementDetail>();
                }

                var achievementsArray = token["Achievements"] as JArray;
                if (achievementsArray != null)
                {
                    var count = Math.Min(achievementsArray.Count, data.Achievements.Count);
                    for (var i = 0; i < count; i++)
                    {
                        var achievementObj = achievementsArray[i] as JObject;
                        if (achievementObj == null || data.Achievements[i] == null)
                        {
                            continue;
                        }

                        var mappedUnlocked = FirstNonEmpty(
                            achievementObj.Value<string>("UnlockedIconPath"),
                            achievementObj.Value<string>("IconUnlockedPath"),
                            achievementObj.Value<string>("IconPath"));

                        if (string.IsNullOrWhiteSpace(data.Achievements[i].UnlockedIconPath) &&
                            !string.IsNullOrWhiteSpace(mappedUnlocked))
                        {
                            data.Achievements[i].UnlockedIconPath = mappedUnlocked;
                        }
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                parseError = ex.Message;
                return false;
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return null;
            }

            for (var i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }

            return null;
        }
    }
}
