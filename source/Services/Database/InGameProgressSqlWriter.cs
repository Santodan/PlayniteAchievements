using PlayniteAchievements.Common;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using SqlNado;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>
    /// Atomic, progress-only SQL writer for live game sessions. It intentionally knows nothing
    /// about schema discovery, images, localization, rarity, or enrichment.
    /// </summary>
    internal static class InGameProgressSqlWriter
    {
        private sealed class TargetRow
        {
            public long UserGameProgressId { get; set; }
            public long GameId { get; set; }
        }

        private sealed class AchievementRow
        {
            public long DefinitionId { get; set; }
            public string ApiName { get; set; }
            public string CategoryType { get; set; }
            public long? UserAchievementId { get; set; }
            public long? Unlocked { get; set; }
            public string UnlockTimeUtc { get; set; }
            public int? ProgressNum { get; set; }
            public int? ProgressDenom { get; set; }
        }

        public static InGameProgressWriteResult Apply(
            SQLiteDatabase db,
            string key,
            string providerKey,
            IReadOnlyList<AchievementProgressObservation> observations)
        {
            if (db == null ||
                string.IsNullOrWhiteSpace(key) ||
                string.IsNullOrWhiteSpace(providerKey))
            {
                return InGameProgressWriteResult.Failed("invalid_target");
            }

            var incoming = (observations ?? Array.Empty<AchievementProgressObservation>())
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Last())
                .ToList();

            InGameProgressWriteResult result = null;
            db.RunTransaction(() =>
            {
                var target = db.Load<TargetRow>(
                    @"SELECT
                        ugp.Id AS UserGameProgressId,
                        ugp.GameId AS GameId
                      FROM UserGameProgress ugp
                      INNER JOIN Users u ON u.Id = ugp.UserId
                      INNER JOIN Games g ON g.Id = ugp.GameId
                      WHERE u.IsCurrentUser = 1
                        AND ugp.CacheKey = ?
                        AND g.ProviderKey = ?
                      ORDER BY ugp.LastUpdatedUtc DESC, ugp.Id DESC
                      LIMIT 1;",
                    key.Trim(),
                    providerKey.Trim()).FirstOrDefault();

                if (target == null)
                {
                    result = InGameProgressWriteResult.Failed("schema_missing");
                    return;
                }

                var rows = db.Load<AchievementRow>(
                    @"SELECT
                        ad.Id AS DefinitionId,
                        ad.ApiName AS ApiName,
                        ad.CategoryType AS CategoryType,
                        ua.Id AS UserAchievementId,
                        ua.Unlocked AS Unlocked,
                        ua.UnlockTimeUtc AS UnlockTimeUtc,
                        ua.ProgressNum AS ProgressNum,
                        ua.ProgressDenom AS ProgressDenom
                      FROM AchievementDefinitions ad
                      LEFT JOIN UserAchievements ua
                        ON ua.AchievementDefinitionId = ad.Id
                       AND ua.UserGameProgressId = ?
                      WHERE ad.GameId = ?;",
                    target.UserGameProgressId,
                    target.GameId)
                    .Where(row => row != null && !string.IsNullOrWhiteSpace(row.ApiName))
                    .ToDictionary(row => row.ApiName.Trim(), StringComparer.OrdinalIgnoreCase);

                if (rows.Count == 0)
                {
                    result = InGameProgressWriteResult.Failed("schema_missing");
                    return;
                }

                var nowIso = ToIso(DateTime.UtcNow);
                var matched = new List<string>();
                var unmatched = new List<string>();
                var newlyUnlocked = new List<string>();
                var changed = false;

                foreach (var observation in incoming)
                {
                    var apiName = observation.ApiName.Trim();
                    if (!rows.TryGetValue(apiName, out var row))
                    {
                        unmatched.Add(apiName);
                        continue;
                    }

                    matched.Add(apiName);

                    var wasUnlocked = row.Unlocked.GetValueOrDefault() != 0;
                    var shouldUnlock = wasUnlocked || observation.Unlocked;
                    var previousUnlockIso = NormalizeStoredIso(row.UnlockTimeUtc);
                    var resolvedUnlockIso = previousUnlockIso;
                    if (shouldUnlock &&
                        string.IsNullOrWhiteSpace(resolvedUnlockIso) &&
                        observation.UnlockTimeUtc.HasValue)
                    {
                        var unlockUtc = DateTimeUtilities.AsUtcKind(
                            observation.UnlockTimeUtc.Value);
                        if (unlockUtc > DateTime.MinValue && unlockUtc < DateTime.MaxValue)
                        {
                            resolvedUnlockIso = ToIso(unlockUtc);
                        }
                    }

                    var progressNum = MaxNullable(row.ProgressNum, observation.ProgressNum);
                    var progressDenom = MaxNullable(row.ProgressDenom, observation.ProgressDenom);
                    var rowChanged =
                        shouldUnlock != wasUnlocked ||
                        !string.Equals(previousUnlockIso, resolvedUnlockIso, StringComparison.Ordinal) ||
                        progressNum != row.ProgressNum ||
                        progressDenom != row.ProgressDenom;

                    if (!row.UserAchievementId.HasValue)
                    {
                        if (shouldUnlock || progressNum.HasValue || progressDenom.HasValue)
                        {
                            db.ExecuteNonQuery(
                                @"INSERT INTO UserAchievements
                                    (UserGameProgressId, AchievementDefinitionId, Unlocked, UnlockTimeUtc,
                                     ProgressNum, ProgressDenom, LastUpdatedUtc, CreatedUtc)
                                  VALUES (?, ?, ?, ?, ?, ?, ?, ?);",
                                target.UserGameProgressId,
                                row.DefinitionId,
                                shouldUnlock ? 1 : 0,
                                DbValue(resolvedUnlockIso),
                                DbParam(progressNum),
                                DbParam(progressDenom),
                                nowIso,
                                nowIso);
                            rowChanged = true;
                        }
                    }
                    else if (rowChanged)
                    {
                        db.ExecuteNonQuery(
                            @"UPDATE UserAchievements
                              SET Unlocked = ?,
                                  UnlockTimeUtc = ?,
                                  ProgressNum = ?,
                                  ProgressDenom = ?,
                                  LastUpdatedUtc = ?
                              WHERE Id = ?;",
                            shouldUnlock ? 1 : 0,
                            DbValue(resolvedUnlockIso),
                            DbParam(progressNum),
                            DbParam(progressDenom),
                            nowIso,
                            row.UserAchievementId.Value);
                    }

                    if (!wasUnlocked && shouldUnlock)
                    {
                        newlyUnlocked.Add(apiName);
                    }

                    if (!string.IsNullOrWhiteSpace(observation.UnlockMode))
                    {
                        var categoryTypes = AchievementCategoryTypeHelper
                            .ParseValues(row.CategoryType)
                            .Where(value =>
                                !string.Equals(
                                    value,
                                    AchievementCategoryTypeHelper.SoftcoreCategoryType,
                                    StringComparison.OrdinalIgnoreCase) &&
                                !string.Equals(
                                    value,
                                    AchievementCategoryTypeHelper.HardcoreCategoryType,
                                    StringComparison.OrdinalIgnoreCase))
                            .Concat(new[] { observation.UnlockMode });
                        var updatedCategoryType = AchievementCategoryTypeHelper.NormalizeOrDefault(
                            AchievementCategoryTypeHelper.Combine(categoryTypes));
                        if (!string.Equals(
                            updatedCategoryType,
                            row.CategoryType,
                            StringComparison.Ordinal))
                        {
                            db.ExecuteNonQuery(
                                @"UPDATE AchievementDefinitions
                                  SET CategoryType = ?, UpdatedUtc = ?
                                  WHERE Id = ?;",
                                updatedCategoryType,
                                nowIso,
                                row.DefinitionId);
                            rowChanged = true;
                        }
                    }

                    changed |= rowChanged;
                }

                if (changed)
                {
                    var unlockedCount = db.ExecuteScalar<long>(
                        @"SELECT COUNT(1)
                          FROM UserAchievements
                          WHERE UserGameProgressId = ? AND Unlocked = 1;",
                        new object[] { target.UserGameProgressId });

                    db.ExecuteNonQuery(
                        @"UPDATE UserGameProgress
                          SET AchievementsUnlocked = ?,
                              LastUpdatedUtc = ?,
                              UpdatedUtc = ?
                          WHERE Id = ?;",
                        unlockedCount,
                        nowIso,
                        nowIso,
                        target.UserGameProgressId);
                    db.ExecuteNonQuery(
                        "UPDATE Games SET LastUpdatedUtc = ? WHERE Id = ?;",
                        nowIso,
                        target.GameId);
                }

                result = new InGameProgressWriteResult
                {
                    Success = true,
                    Changed = changed,
                    MatchedKeys = matched,
                    UnmatchedKeys = unmatched,
                    NewlyUnlockedKeys = newlyUnlocked
                };
            });

            return result ?? InGameProgressWriteResult.Failed("write_failed");
        }

        private static int? MaxNullable(int? current, int? incoming)
        {
            return incoming.HasValue &&
                   (!current.HasValue || incoming.Value > current.Value)
                ? incoming
                : current;
        }

        private static object DbValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? (object)DBNull.Value : value;
        }

        private static object DbParam<T>(T? value) where T : struct
        {
            return value.HasValue ? (object)value.Value : DBNull.Value;
        }

        private static string NormalizeStoredIso(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
            {
                return ToIso(parsed);
            }

            return value.Trim();
        }

        private static string ToIso(DateTime value)
        {
            return DateTimeUtilities
                .AsUtcKind(value)
                .ToString("O", CultureInfo.InvariantCulture);
        }
    }
}
