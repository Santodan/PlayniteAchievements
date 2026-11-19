CREATE VIEW ResolvedAchievementsBefore AS SELECT u.*,
                ROW_NUMBER() OVER (PARTITION BY u.PlayniteGameId ORDER BY u.CustomOrder IS NULL, u.CustomOrder,
                    u.MetadataOrder IS NULL, u.MetadataOrder, u.IsAuthored, u.Id, u.ApiName) - 1 AS DisplayOrder
              FROM (SELECT
                ad.Id AS Id,
                ad.GameId AS GameId,
                ad.ApiName AS ApiName,
                ad.DisplayName AS DisplayName,
                ad.Description AS Description,
                ad.UnlockedIconPath AS UnlockedIconPath,
                ad.LockedIconPath AS LockedIconPath,
                ad.Points AS Points,
                ad.ScaledPoints AS ScaledPoints,
                ad.Category AS Category,
                ad.ProviderCategory AS ProviderCategory,
                ad.Tags AS Tags,
                ad.CategoryKind AS CategoryKind,
                ad.TrophyType AS TrophyType,
                ad.Hidden AS Hidden,
                ad.IsCapstone AS IsCapstone,
                ad.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                ad.Rarity AS Rarity,
                ad.ProgressMax AS ProgressMax,
                ad.MetadataOrder AS MetadataOrder,
                ad.CreatedUtc AS CreatedUtc,
                ad.UpdatedUtc AS UpdatedUtc,
                ad.IsFiltered AS IsFiltered,
                ad.IsSummaryFiltered AS IsSummaryFiltered,
                ad.Note AS Note,
                ad.IsGoal AS IsGoal,
                ad.GoalOrder AS GoalOrder,
                ad.CustomOrder AS CustomOrder,
                ad.CategoryOrder AS CategoryOrder,
                ad.CategoryArtPath AS CategoryArtPath,
                g.PlayniteGameId AS PlayniteGameId,
                g.GameName AS GameName,
                g.ProviderKey AS ProviderKey,
                COALESCE(ua.Unlocked, 0) AS Unlocked,
                CASE WHEN COALESCE(ua.Unlocked, 0) = 1 AND c.UnlockTimeUtc IS NOT NULL THEN c.UnlockTimeUtc
                     WHEN COALESCE(ua.Unlocked, 0) = 1 AND (COALESCE(c.Cleared, 0) & 4096) <> 0 THEN NULL
                     ELSE ua.UnlockTimeUtc END AS UnlockTimeUtc,
                ua.ProgressNum AS ProgressNum,
                ua.ProgressDenom AS ProgressDenom,
                COALESCE(gs.ExcludedFromSummaries, 0) AS ExcludedFromSummaries,
                0 AS IsAuthored,
                cap.CleanPath AS CleanCapturePath, cap.NotificationPath AS NotificationCapturePath, cap.FramedPath AS FramedCapturePath, cap.VideoPath AS VideoCapturePath
              FROM UserGameProgress ugp
              INNER JOIN Users u ON u.Id = ugp.UserId AND u.IsCurrentUser = 1
              INNER JOIN Games g ON g.Id = ugp.GameId
              INNER JOIN AchievementDefinitions ad ON ad.GameId = ugp.GameId
              LEFT JOIN UserAchievements ua ON ua.UserGameProgressId = ugp.Id AND ua.AchievementDefinitionId = ad.Id
              LEFT JOIN CustomLayerAchievements c ON c.PlayniteGameId = g.PlayniteGameId AND c.ApiName = ad.ApiName AND c.IsAuthored = 0
              LEFT JOIN CustomLayerGames gs ON gs.PlayniteGameId = g.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = g.PlayniteGameId AND cap.ApiName = ad.ApiName
            UNION ALL SELECT
                NULL AS Id,
                NULL AS GameId,
                a.ApiName AS ApiName,
                a.DisplayName AS DisplayName,
                a.Description AS Description,
                a.UnlockedIconPath AS UnlockedIconPath,
                a.LockedIconPath AS LockedIconPath,
                a.Points AS Points,
                a.ScaledPoints AS ScaledPoints,
                COALESCE(a.Category, 'Default') AS Category,
                COALESCE(a.Category, 'Default') AS ProviderCategory,
                a.Tags AS Tags,
                a.CategoryKind AS CategoryKind,
                a.TrophyType AS TrophyType,
                COALESCE(a.Hidden, 0) AS Hidden,
                a.IsCapstone AS IsCapstone,
                a.GlobalPercentUnlocked AS GlobalPercentUnlocked,
                COALESCE(a.Rarity, 'Common') AS Rarity,
                a.ProgressDenom AS ProgressMax,
                NULL AS MetadataOrder,
                NULL AS CreatedUtc,
                a.UpdatedUtc AS UpdatedUtc,
                a.FilterOrder IS NOT NULL AS IsFiltered,
                a.SummaryFilterOrder IS NOT NULL AS IsSummaryFiltered,
                a.Note AS Note,
                a.GoalOrder IS NOT NULL AS IsGoal,
                a.GoalOrder AS GoalOrder,
                a.CustomOrder AS CustomOrder,
                (SELECT k.SortIndex FROM CustomLayerCategories k WHERE k.PlayniteGameId = a.PlayniteGameId AND k.Category = COALESCE(a.Category, 'Default')) AS CategoryOrder,
                (SELECT k.ImagePath FROM CustomLayerCategories k WHERE k.PlayniteGameId = a.PlayniteGameId AND k.Category = COALESCE(a.Category, 'Default')) AS CategoryArtPath,
                a.PlayniteGameId AS PlayniteGameId,
                (SELECT gn.GameName FROM Games gn WHERE gn.PlayniteGameId = a.PlayniteGameId AND gn.GameName IS NOT NULL LIMIT 1) AS GameName,
                NULL AS ProviderKey,
                a.Unlocked AS Unlocked,
                a.UnlockTimeUtc AS UnlockTimeUtc,
                a.ProgressNum AS ProgressNum,
                a.ProgressDenom AS ProgressDenom,
                COALESCE(gs.ExcludedFromSummaries, 0) AS ExcludedFromSummaries,
                1 AS IsAuthored,
                cap.CleanPath AS CleanCapturePath, cap.NotificationPath AS NotificationCapturePath, cap.FramedPath AS FramedCapturePath, cap.VideoPath AS VideoCapturePath
              FROM CustomLayerAchievements a
              LEFT JOIN CustomLayerGames gs ON gs.PlayniteGameId = a.PlayniteGameId
              LEFT JOIN AchievementCaptures cap ON cap.PlayniteGameId = a.PlayniteGameId AND cap.ApiName = a.ApiName
              WHERE a.IsAuthored = 1) u;
