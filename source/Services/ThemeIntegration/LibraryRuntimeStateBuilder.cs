using Playnite.SDK;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.ThemeIntegration;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using PlayniteAchievements.ViewModels;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    internal static class LibraryRuntimeStateBuilder
    {
        public static LibraryRuntimeState Build(
            List<GameAchievementData> allData,
            IPlayniteAPI api,
            CancellationToken token,
            bool includeHeavyAchievementLists = true)
        {
            token.ThrowIfCancellationRequested();

            allData ??= new List<GameAchievementData>();
            var state = new LibraryRuntimeState
            {
                HeavyListsBuilt = includeHeavyAchievementLists
            };

            var summariesById = new Dictionary<Guid, GameAchievementSummary>();
            var allGames = new List<GameAchievementSummary>();

            foreach (var data in allData)
            {
                token.ThrowIfCancellationRequested();

                if (data?.PlayniteGameId == null || !data.HasAchievements)
                {
                    continue;
                }

                var total = data.Achievements?.Count ?? 0;
                if (total <= 0)
                {
                    continue;
                }

                var unlocked = 0;
                var latestUnlockUtc = DateTime.MinValue;
                for (int i = 0; i < data.Achievements.Count; i++)
                {
                    var achievement = data.Achievements[i];
                    if (achievement?.Unlocked != true)
                    {
                        continue;
                    }

                    unlocked++;
                    if (!achievement.UnlockTimeUtc.HasValue)
                    {
                        continue;
                    }

                    var utc = NormalizeUtc(achievement.UnlockTimeUtc.Value);
                    if (utc > latestUnlockUtc)
                    {
                        latestUnlockUtc = utc;
                    }
                }

                var common = new AchievementRarityStats();
                var uncommon = new AchievementRarityStats();
                var rare = new AchievementRarityStats();
                var ultraRare = new AchievementRarityStats();
                AccumulateGameRarityStats(
                    data,
                    common,
                    uncommon,
                    rare,
                    ultraRare);
                AddRarityStats(state.TotalCommon, common);
                AddRarityStats(state.TotalUncommon, uncommon);
                AddRarityStats(state.TotalRare, rare);
                AddRarityStats(state.TotalUltraRare, ultraRare);

                var rareAndUltraRare = AchievementRarityStatsCombiner.Combine(rare, ultraRare);
                var overall = AchievementRarityStatsCombiner.Combine(common, uncommon, rare, ultraRare);
                var gold = rare.Unlocked + ultraRare.Unlocked;
                var silver = uncommon.Unlocked;
                var bronze = common.Unlocked;

                var summary = new GameAchievementSummary(
                    data.PlayniteGameId.Value,
                    data.Game?.Name ?? data.GameName ?? string.Empty,
                    data.Game?.Source?.Name ?? "Unknown",
                    ResolveCoverImagePath(data.Game, api),
                    AchievementCompletionPercentCalculator.ComputeRoundedPercent(unlocked, total),
                    gold,
                    silver,
                    bronze,
                    data.IsCompleted,
                    latestUnlockUtc == DateTime.MinValue ? DateTime.MinValue : latestUnlockUtc.ToLocalTime(),
                    null,
                    common,
                    uncommon,
                    rare,
                    ultraRare,
                    rareAndUltraRare,
                    overall);

                allGames.Add(summary);
                summariesById[summary.GameId] = summary;
            }

            allGames = allGames
                .OrderByDescending(item => item.LastUnlockDate)
                .ThenByDescending(item => item.Progress)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            state.AllGamesWithAchievements = allGames;
            state.GameSummariesDesc = allGames;
            state.GameSummariesAsc = allGames
                .OrderBy(item => item.LastUnlockDate)
                .ThenByDescending(item => item.Progress)
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            state.CompletedGamesDesc = state.GameSummariesDesc.Where(item => item.IsCompleted).ToList();
            state.CompletedGamesAsc = state.GameSummariesAsc.Where(item => item.IsCompleted).ToList();
            state.PlatinumGames = allGames
                .Where(item => item.IsCompleted && item.LastUnlockDate != DateTime.MinValue)
                .OrderByDescending(item => item.LastUnlockDate)
                .ToList();
            state.PlatinumGamesAscending = state.PlatinumGames.OrderBy(item => item.LastUnlockDate).ToList();

            state.PlatinumTrophies = state.PlatinumGames.Count;
            state.GoldTrophies = allGames.Sum(item => item.GoldCount);
            state.SilverTrophies = allGames.Sum(item => item.SilverCount);
            state.BronzeTrophies = allGames.Sum(item => item.BronzeCount);
            state.TotalRareAndUltraRare = AchievementRarityStatsCombiner.Combine(state.TotalRare, state.TotalUltraRare);
            state.TotalOverall = AchievementRarityStatsCombiner.Combine(
                state.TotalCommon,
                state.TotalUncommon,
                state.TotalRare,
                state.TotalUltraRare);
            state.TotalTrophies =
                state.PlatinumTrophies +
                state.GoldTrophies +
                state.SilverTrophies +
                state.BronzeTrophies;

            PopulateProviderLists(state, allData, summariesById);

            if (state.TotalTrophies > 0)
            {
                state.Score = state.PlatinumTrophies * 300 +
                              state.GoldTrophies * 90 +
                              state.SilverTrophies * 30 +
                              state.BronzeTrophies * 15;
                ComputeLevel(state.Score, out var level, out var levelProgress);
                state.Level = level;
                state.LevelProgress = levelProgress;
                state.Rank = RankFromLevel(level);
            }

            PopulateAchievementLists(state, allData, token, includeHeavyAchievementLists);
            return state;
        }

        private static void PopulateProviderLists(
            LibraryRuntimeState state,
            IEnumerable<GameAchievementData> allData,
            IReadOnlyDictionary<Guid, GameAchievementSummary> summariesById)
        {
            var steamGames = new List<GameAchievementSummary>();
            var gogGames = new List<GameAchievementSummary>();
            var epicGames = new List<GameAchievementSummary>();
            var battleNetGames = new List<GameAchievementSummary>();
            var eaGames = new List<GameAchievementSummary>();
            var xboxGames = new List<GameAchievementSummary>();
            var psnGames = new List<GameAchievementSummary>();
            var retroAchievementsGames = new List<GameAchievementSummary>();
            var rpcs3Games = new List<GameAchievementSummary>();
            var xeniaGames = new List<GameAchievementSummary>();
            var shadPS4Games = new List<GameAchievementSummary>();
            var manualGames = new List<GameAchievementSummary>();

            foreach (var data in allData)
            {
                if (data?.PlayniteGameId == null || !summariesById.TryGetValue(data.PlayniteGameId.Value, out var summary))
                {
                    continue;
                }

                var providerKey = data.EffectiveProviderKey;
                if (string.IsNullOrWhiteSpace(providerKey))
                {
                    providerKey = data.ProviderKey;
                }

                switch (providerKey ?? string.Empty)
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
                    case "BattleNet":
                        battleNetGames.Add(summary);
                        break;
                    case "EA":
                        eaGames.Add(summary);
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
                    case "Xenia":
                        xeniaGames.Add(summary);
                        break;
                    case "ShadPS4":
                        shadPS4Games.Add(summary);
                        break;
                    case "Manual":
                        manualGames.Add(summary);
                        break;
                }
            }

            state.SteamGames = steamGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.GOGGames = gogGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.EpicGames = epicGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.BattleNetGames = battleNetGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.EAGames = eaGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.XboxGames = xboxGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.PSNGames = psnGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.RetroAchievementsGames = retroAchievementsGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.RPCS3Games = rpcs3Games.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.XeniaGames = xeniaGames.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.ShadPS4Games = shadPS4Games.OrderByDescending(item => item.LastUnlockDate).ToList();
            state.ManualGames = manualGames.OrderByDescending(item => item.LastUnlockDate).ToList();
        }

        private static void PopulateAchievementLists(
            LibraryRuntimeState state,
            List<GameAchievementData> allData,
            CancellationToken token,
            bool includeHeavyAchievementLists)
        {
            if (includeHeavyAchievementLists)
            {
                var allAchievements = new List<AchievementDetail>();
                foreach (var data in allData)
                {
                    token.ThrowIfCancellationRequested();
                    if (data?.Achievements == null)
                    {
                        continue;
                    }

                    foreach (var achievement in data.Achievements)
                    {
                        if (achievement == null)
                        {
                            continue;
                        }

                        achievement.Game = data.Game;
                        allAchievements.Add(achievement);
                    }
                }

                state.AllAchievementsUnlockAsc = AchievementGridSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.UnlockTime),
                    ListSortDirection.Ascending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsUnlockDesc = AchievementGridSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.UnlockTime),
                    ListSortDirection.Descending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsRarityAsc = AchievementGridSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.RaritySortValue),
                    ListSortDirection.Ascending,
                    includeGameNameTieBreak: true);
                state.AllAchievementsRarityDesc = AchievementGridSortHelper.CreateSortedDetailList(
                    allAchievements,
                    nameof(AchievementDisplayItem.RaritySortValue),
                    ListSortDirection.Descending,
                    includeGameNameTieBreak: true);

                var unlockedAchievements = allAchievements
                    .Where(a => a != null && a.UnlockTimeUtc.HasValue && a.UnlockTimeUtc.Value != DateTime.MinValue)
                    .ToList();
                PopulateRecentLists(state, unlockedAchievements, includeFullLists: true);
                return;
            }

            var unlockedRecent = new List<AchievementDetail>();
            foreach (var data in allData)
            {
                token.ThrowIfCancellationRequested();
                if (data?.Achievements == null)
                {
                    continue;
                }

                foreach (var achievement in data.Achievements)
                {
                    if (achievement == null ||
                        !achievement.UnlockTimeUtc.HasValue ||
                        achievement.UnlockTimeUtc.Value == DateTime.MinValue)
                    {
                        continue;
                    }

                    achievement.Game = data.Game;
                    unlockedRecent.Add(achievement);
                }
            }

            PopulateRecentLists(state, unlockedRecent, includeFullLists: false);
        }

        private static void PopulateRecentLists(
            LibraryRuntimeState state,
            List<AchievementDetail> unlockedAchievements,
            bool includeFullLists)
        {
            unlockedAchievements ??= new List<AchievementDetail>();

            var mostRecent = AchievementGridSortHelper.CreateSortedDetailList(
                unlockedAchievements,
                nameof(AchievementDisplayItem.UnlockTime),
                ListSortDirection.Descending,
                includeGameNameTieBreak: true);
            var rareRecentCutoffUtc = DateTime.UtcNow.AddDays(-180);
            var rareRecent = AchievementGridSortHelper.CreateSortedDetailList(
                unlockedAchievements.Where(a => NormalizeUtc(a.UnlockTimeUtc.Value) >= rareRecentCutoffUtc),
                nameof(AchievementDisplayItem.RaritySortValue),
                ListSortDirection.Ascending,
                includeGameNameTieBreak: true);

            if (includeFullLists)
            {
                state.MostRecentUnlocks = mostRecent;
                state.RarestRecentUnlocks = rareRecent;
            }

            state.MostRecentUnlocksTop3 = mostRecent.Take(3).ToList();
            state.MostRecentUnlocksTop5 = mostRecent.Take(5).ToList();
            state.MostRecentUnlocksTop10 = mostRecent.Take(10).ToList();
            state.RarestRecentUnlocksTop3 = rareRecent.Take(3).ToList();
            state.RarestRecentUnlocksTop5 = rareRecent.Take(5).ToList();
            state.RarestRecentUnlocksTop10 = rareRecent.Take(10).ToList();
        }

        private static string ResolveCoverImagePath(Playnite.SDK.Models.Game game, IPlayniteAPI api)
        {
            if (game == null || api?.Database == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(game.CoverImage))
            {
                var coverPath = api.Database.GetFullFilePath(game.CoverImage);
                if (!string.IsNullOrWhiteSpace(coverPath))
                {
                    return coverPath;
                }
            }

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

        private static void AccumulateGameRarityStats(
            GameAchievementData data,
            AchievementRarityStats common,
            AchievementRarityStats uncommon,
            AchievementRarityStats rare,
            AchievementRarityStats ultraRare)
        {
            if (data?.Achievements == null)
            {
                return;
            }

            foreach (var achievement in data.Achievements)
            {
                if (!TryGetEffectiveRarityTier(achievement, out var tier))
                {
                    continue;
                }

                var target = GetStatsForTier(tier, common, uncommon, rare, ultraRare);
                if (target == null)
                {
                    continue;
                }

                target.Total++;
                if (achievement.Unlocked)
                {
                    target.Unlocked++;
                }
                else
                {
                    target.Locked++;
                }
            }
        }

        private static void AddRarityStats(AchievementRarityStats target, AchievementRarityStats source)
        {
            if (target == null || source == null)
            {
                return;
            }

            target.Total += source.Total;
            target.Unlocked += source.Unlocked;
            target.Locked += source.Locked;
        }

        private static bool TryGetEffectiveRarityTier(AchievementDetail achievement, out RarityTier tier)
        {
            tier = RarityTier.Common;
            if (achievement == null)
            {
                return false;
            }

            tier = achievement.Rarity;
            return true;
        }

        private static AchievementRarityStats GetStatsForTier(
            RarityTier tier,
            AchievementRarityStats common,
            AchievementRarityStats uncommon,
            AchievementRarityStats rare,
            AchievementRarityStats ultraRare)
        {
            switch (tier)
            {
                case RarityTier.UltraRare:
                    return ultraRare;
                case RarityTier.Rare:
                    return rare;
                case RarityTier.Uncommon:
                    return uncommon;
                default:
                    return common;
            }
        }

        private static DateTime NormalizeUtc(DateTime timestamp)
        {
            if (timestamp.Kind == DateTimeKind.Unspecified)
            {
                return DateTime.SpecifyKind(timestamp, DateTimeKind.Utc);
            }

            return timestamp.Kind == DateTimeKind.Local ? timestamp.ToUniversalTime() : timestamp;
        }

        private static void ComputeLevel(int score, out int level, out int levelProgressPercent)
        {
            level = 0;
            levelProgressPercent = 0;

            if (score <= 0)
            {
                return;
            }

            var rangeMin = 0;
            var rangeMax = 100;
            var step = 100;

            while (score > rangeMax)
            {
                level++;
                rangeMin = rangeMax + 1;
                step += 100;
                rangeMax = rangeMin + step - 1;
            }

            var rangeSpan = rangeMax - rangeMin + 1;
            var progress = (int)(((double)(score - rangeMin) / rangeSpan) * 100);
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
