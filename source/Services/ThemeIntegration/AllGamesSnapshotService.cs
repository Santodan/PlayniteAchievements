using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Input;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    /// <summary>
    /// Service for building all-games achievement snapshots.
    /// Creates immutable AllGamesSnapshot instances for trophy overview displays.
    /// </summary>
    public static class AllGamesSnapshotService
    {
        /// <summary>
        /// Build an all-games snapshot from achievement data.
        /// </summary>
        /// <param name="allData">All games achievement data (must be hydrated with Game references).</param>
        /// <param name="api">Playnite API for resolving file paths.</param>
        /// <param name="openGameWindowCallback">Callback to open a game's achievement window.</param>
        /// <param name="token">Cancellation token.</param>
        public static AllGamesSnapshot BuildSnapshot(
            List<GameAchievementData> allData,
            IPlayniteAPI api,
            Action<Guid> openGameWindowCallback,
            CancellationToken token,
            bool includeHeavyAchievementLists = true)
        {
            token.ThrowIfCancellationRequested();

            var snapshot = new AllGamesSnapshot();
            snapshot.HeavyListsBuilt = includeHeavyAchievementLists;

            allData ??= new List<GameAchievementData>();

            var items = new List<GameAchievementSummary>();
            foreach (var data in allData)
            {
                token.ThrowIfCancellationRequested();

                if (data?.PlayniteGameId == null || !data.HasAchievements) continue;
                var total = data.Achievements?.Count ?? 0;
                if (total <= 0) continue;

                var id = data.PlayniteGameId.Value;
                var game = data.Game;

                var unlocked = 0;
                var latestUnlockUtc = DateTime.MinValue;
                for (int i = 0; i < data.Achievements.Count; i++)
                {
                    var a = data.Achievements[i];
                    if (a?.Unlocked != true)
                    {
                        continue;
                    }

                    unlocked++;

                    var t = a.UnlockTimeUtc;
                    if (!t.HasValue)
                    {
                        continue;
                    }

                    var utc = t.Value;
                    if (utc.Kind == DateTimeKind.Unspecified)
                    {
                        utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
                    }

                    if (utc > latestUnlockUtc)
                    {
                        latestUnlockUtc = utc;
                    }
                }

                var progress = unlocked == total ? 100 : (int)Math.Floor(100.0 * unlocked / total);
                var isCompleted = data.IsCompleted;
                var latestUnlockLocal = latestUnlockUtc == DateTime.MinValue ? DateTime.MinValue : latestUnlockUtc.ToLocalTime();

                GetTrophyCounts(
                    data,
                    out var commonUnlockCount,
                    out var uncommonUnlockCount,
                    out var rareUnlockCount,
                    out var ultraRareUnlockCount,
                    out var gold,
                    out var silver,
                    out var bronze);

                var openCmd = new Common.RelayCommand(_ => openGameWindowCallback(id));

                var name = game?.Name ?? data.GameName ?? string.Empty;
                var platform = game?.Source?.Name ?? "Unknown";
                var coverImagePath = ResolveCoverImagePath(game, api);

                items.Add(new GameAchievementSummary(
                    id,
                    name,
                    platform,
                    coverImagePath,
                    progress,
                    gold,
                    silver,
                    bronze,
                    isCompleted,
                    latestUnlockLocal,
                    openCmd,
                    commonUnlockCount,
                    uncommonUnlockCount,
                    rareUnlockCount,
                    ultraRareUnlockCount));
            }

            items = items
                .OrderByDescending(i => i.LastUnlockDate)
                .ThenByDescending(i => i.Progress)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            snapshot.All = items;
            snapshot.GameSummariesDesc = items;
            snapshot.GameSummariesAsc = items
                .OrderBy(i => i.LastUnlockDate)
                .ThenByDescending(i => i.Progress)
                .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            snapshot.CompletedGamesDesc = snapshot.GameSummariesDesc
                .Where(i => i.IsCompleted)
                .ToList();

            snapshot.CompletedGamesAsc = snapshot.GameSummariesAsc
                .Where(i => i.IsCompleted)
                .ToList();

            snapshot.Platinum = items
                .Where(i => i.IsCompleted)
                .Where(i => i.LastUnlockDate != DateTime.MinValue)
                .OrderByDescending(i => i.LastUnlockDate)
                .ToList();

            snapshot.PlatinumAscending = snapshot.Platinum
                .OrderBy(i => i.LastUnlockDate)
                .ToList();

            snapshot.PlatCount = snapshot.Platinum.Count;
            snapshot.GoldCount = items.Sum(i => i.GoldCount);
            snapshot.SilverCount = items.Sum(i => i.SilverCount);
            snapshot.BronzeCount = items.Sum(i => i.BronzeCount);
            snapshot.TotalCommonUnlockCount = items.Sum(i => i.CommonUnlockCount);
            snapshot.TotalUncommonUnlockCount = items.Sum(i => i.UncommonUnlockCount);
            snapshot.TotalRareUnlockCount = items.Sum(i => i.RareUnlockCount);
            snapshot.TotalUltraRareUnlockCount = items.Sum(i => i.UltraRareUnlockCount);

            // Build per-provider game lists
            var steamGames = new List<GameAchievementSummary>();
            var gogGames = new List<GameAchievementSummary>();
            var epicGames = new List<GameAchievementSummary>();
            var xboxGames = new List<GameAchievementSummary>();
            var psnGames = new List<GameAchievementSummary>();
            var retroAchievementsGames = new List<GameAchievementSummary>();
            var rpcs3Games = new List<GameAchievementSummary>();
            var shadPS4Games = new List<GameAchievementSummary>();
            var manualGames = new List<GameAchievementSummary>();

            foreach (var data in allData)
            {
                if (data?.HasAchievements != true || data.PlayniteGameId == null) continue;

                var summary = items.FirstOrDefault(i => i.GameId == data.PlayniteGameId.Value);
                if (summary == null) continue;

                var providerName = data.ProviderName ?? "";
                switch (providerName)
                {
                    case "Steam":
                        steamGames.Add(summary);
                        break;
                    case "GOG":
                        gogGames.Add(summary);
                        break;
                    case "Epic":
                        epicGames.Add(summary);
                        break;
                    case "Xbox":
                        xboxGames.Add(summary);
                        break;
                    case "PSN":
                        psnGames.Add(summary);
                        break;
                    case "RetroAchievements":
                        retroAchievementsGames.Add(summary);
                        break;
                    case "RPCS3":
                        rpcs3Games.Add(summary);
                        break;
                    case "ShadPS4":
                        shadPS4Games.Add(summary);
                        break;
                    case "Manual":
                        manualGames.Add(summary);
                        break;
                }
            }

            snapshot.SteamGames = steamGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.GOGGames = gogGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.EpicGames = epicGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.XboxGames = xboxGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.PSNGames = psnGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.RetroAchievementsGames = retroAchievementsGames.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.RPCS3Games = rpcs3Games.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.ShadPS4Games = shadPS4Games.OrderByDescending(g => g.LastUnlockDate).ToList();
            snapshot.ManualGames = manualGames.OrderByDescending(g => g.LastUnlockDate).ToList();

            if (snapshot.TotalCount > 0)
            {
                snapshot.Score = snapshot.PlatCount * 300 + snapshot.GoldCount * 90 + snapshot.SilverCount * 30 + snapshot.BronzeCount * 15;
                ComputeLevel(snapshot.Score, out var level, out var progressPercent);
                snapshot.Level = level;
                snapshot.LevelProgress = progressPercent;
                snapshot.Rank = RankFromLevel(snapshot.Level);
            }

            if (includeHeavyAchievementLists)
            {
                // Build all-games achievement lists (flattened from all games)
                var allAchievements = new List<AchievementDetail>();
                foreach (var data in allData)
                {
                    token.ThrowIfCancellationRequested();
                    if (data?.Achievements == null) continue;
                    foreach (var achievement in data.Achievements)
                    {
                        if (achievement != null)
                        {
                            achievement.Game = data.Game;
                            allAchievements.Add(achievement);
                        }
                    }
                }

                // Sort by unlock date (ascending = oldest first, descending = newest first)
                snapshot.AllAchievementsUnlockAsc = allAchievements
                    .OrderBy(a => a?.UnlockTimeUtc)
                    .ThenBy(a => a?.DisplayName)
                    .ToList();

                snapshot.AllAchievementsUnlockDesc = allAchievements
                    .OrderByDescending(a => a?.UnlockTimeUtc)
                    .ThenBy(a => a?.DisplayName)
                    .ToList();

                // Sort by rarity (ascending = rarest first, descending = common first)
                snapshot.AllAchievementsRarityAsc = allAchievements
                    .OrderBy(a => a?.GlobalPercentUnlocked ?? 100)
                    .ThenBy(a => a?.DisplayName)
                    .ToList();

                snapshot.AllAchievementsRarityDesc = allAchievements
                    .OrderByDescending(a => a?.GlobalPercentUnlocked ?? 100)
                    .ThenBy(a => a?.DisplayName)
                    .ToList();

                var unlockedAchievements = allAchievements
                    .Where(a => a != null && a.UnlockTimeUtc.HasValue && a.UnlockTimeUtc.Value != DateTime.MinValue)
                    .ToList();

                PopulateRecentLists(snapshot, unlockedAchievements, includeFullLists: true);
            }
            else
            {
                // Lightweight mode: avoid building full all-games sorted list surfaces.
                var unlockedAchievements = new List<AchievementDetail>();
                foreach (var data in allData)
                {
                    token.ThrowIfCancellationRequested();
                    if (data?.Achievements == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < data.Achievements.Count; i++)
                    {
                        var achievement = data.Achievements[i];
                        if (achievement == null ||
                            !achievement.UnlockTimeUtc.HasValue ||
                            achievement.UnlockTimeUtc.Value == DateTime.MinValue)
                        {
                            continue;
                        }

                        achievement.Game = data.Game;
                        unlockedAchievements.Add(achievement);
                    }
                }

                PopulateRecentLists(snapshot, unlockedAchievements, includeFullLists: false);
            }

            return snapshot;
        }

        private static string ResolveCoverImagePath(Playnite.SDK.Models.Game game, IPlayniteAPI api)
        {
            if (game == null || api?.Database == null)
            {
                return string.Empty;
            }

            // Try cover image first
            if (!string.IsNullOrWhiteSpace(game.CoverImage))
            {
                var coverPath = api.Database.GetFullFilePath(game.CoverImage);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    return coverPath;
                }
            }

            // Fallback to icon
            if (!string.IsNullOrWhiteSpace(game.Icon))
            {
                var iconPath = api.Database.GetFullFilePath(game.Icon);
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    return iconPath;
                }
            }

            return string.Empty;
        }

        private static void PopulateRecentLists(AllGamesSnapshot snapshot, List<AchievementDetail> unlockedAchievements, bool includeFullLists)
        {
            unlockedAchievements ??= new List<AchievementDetail>();

            var mostRecent = unlockedAchievements
                .OrderByDescending(a => NormalizeUtc(a.UnlockTimeUtc.Value))
                .ThenBy(a => a.DisplayName)
                .ToList();

            var rareRecentCutoffUtc = DateTime.UtcNow.AddDays(-180);
            var rareRecent = unlockedAchievements
                .Where(a => NormalizeUtc(a.UnlockTimeUtc.Value) >= rareRecentCutoffUtc)
                .OrderBy(a => a.GlobalPercentUnlocked ?? 100)
                .ThenByDescending(a => NormalizeUtc(a.UnlockTimeUtc.Value))
                .ThenBy(a => a.DisplayName)
                .ToList();

            if (includeFullLists)
            {
                snapshot.MostRecentUnlocks = mostRecent;
                snapshot.RarestRecentUnlocks = rareRecent;
            }

            snapshot.MostRecentUnlocksTop3 = mostRecent.Take(3).ToList();
            snapshot.MostRecentUnlocksTop5 = mostRecent.Take(5).ToList();
            snapshot.MostRecentUnlocksTop10 = mostRecent.Take(10).ToList();

            snapshot.RarestRecentUnlocksTop3 = rareRecent.Take(3).ToList();
            snapshot.RarestRecentUnlocksTop5 = rareRecent.Take(5).ToList();
            snapshot.RarestRecentUnlocksTop10 = rareRecent.Take(10).ToList();
        }

        private static DateTime NormalizeUtc(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            }

            return timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
        }

        private static void GetTrophyCounts(
            GameAchievementData data,
            out int commonUnlockCount,
            out int uncommonUnlockCount,
            out int rareUnlockCount,
            out int ultraRareUnlockCount,
            out int gold,
            out int silver,
            out int bronze)
        {
            commonUnlockCount = 0;
            uncommonUnlockCount = 0;
            rareUnlockCount = 0;
            ultraRareUnlockCount = 0;
            gold = 0;
            silver = 0;
            bronze = 0;

            if (data?.Achievements == null || data.Achievements.Count == 0)
            {
                return;
            }

            foreach (var a in data.Achievements)
            {
                if (a?.Unlocked != true)
                {
                    continue;
                }

                // Only count rarity if data is available (null means no rarity info for this provider)
                if (!a.GlobalPercentUnlocked.HasValue)
                {
                    continue;
                }

                var percent = a.GlobalPercentUnlocked.Value;
                var tier = RarityHelper.GetRarityTier(percent);

                switch (tier)
                {
                    case RarityTier.UltraRare:
                        ultraRareUnlockCount++;
                        gold++;
                        break;
                    case RarityTier.Rare:
                        rareUnlockCount++;
                        gold++;
                        break;
                    case RarityTier.Uncommon:
                        uncommonUnlockCount++;
                        silver++;
                        break;
                    case RarityTier.Common:
                    default:
                        commonUnlockCount++;
                        bronze++;
                        break;
                }
            }
        }

        private static void ComputeLevel(int score, out int level, out int levelProgressPercent)
        {
            level = 0;
            levelProgressPercent = 0;

            if (score <= 0)
            {
                return;
            }

            int rangeMin = 0;
            int rangeMax = 100;
            int step = 100;

            while (score > rangeMax)
            {
                level++;
                rangeMin = rangeMax + 1;
                step += 100;
                rangeMax = rangeMin + step - 1;
            }

            int rangeSpan = rangeMax - rangeMin + 1;
            int progress = (int)(((double)(score - rangeMin) / rangeSpan) * 100);
            levelProgressPercent = Math.Max(0, Math.Min(100, progress));
        }

        private static string RankFromLevel(int level)
        {
            if (level <= 0) return "Bronze1";

            if (level <= 3) return "Bronze1";
            if (level <= 7) return "Bronze2";
            if (level <= 12) return "Bronze3";
            if (level <= 21) return "Silver1";
            if (level <= 31) return "Silver2";
            if (level <= 44) return "Silver3";
            if (level <= 59) return "Gold1";
            if (level <= 77) return "Gold2";
            if (level <= 97) return "Gold3";
            if (level <= 119) return "Plat1";
            if (level <= 144) return "Plat2";
            if (level <= 171) return "Plat3";
            return "Plat";
        }
    }
}
