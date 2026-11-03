using ForkLog = PlayniteAchievements.Services.Logging.ForkLogger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Media;
using System.Reflection;
using System.Security;
using System.Text;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json.Linq;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Logging;
using PlayniteAchievements.Services.UI;

namespace PlayniteAchievements.Services
{
    public partial class NotificationPublisher
    {
        private static void PopulateSantodanNotificationScores(NotificationScoreContext context, Game game,
            string gameName, string achievementName, string achievementRarity)
        {
            var plugin = PlayniteAchievementsPlugin.Instance;
            if (Providers.ProviderRegistry.Settings<Providers.Local.LocalSettings>()?.UseForkNotificationSummaryCache == true)
            {
                var dataService = plugin?.AchievementDataService;
                var gameData = game != null && game.Id != Guid.Empty
                    ? dataService?.GetVisibleGameAchievementData(game.Id)
                    : null;
                // Reuse library totals and patch only changed games after polling writes.
                // Only the notified game needs full achievement objects.
                var summaries = dataService?.GetNotificationGameSummaries();
                var namedGame = summaries?.FirstOrDefault(data =>
                    !string.IsNullOrWhiteSpace(gameName) &&
                    string.Equals(data?.GameName, gameName, StringComparison.OrdinalIgnoreCase));
                if (gameData == null && namedGame?.PlayniteGameId.HasValue == true)
                {
                    gameData = dataService?.GetVisibleGameAchievementData(namedGame.PlayniteGameId.Value);
                }
                var achievement = gameData?.Achievements?.FirstOrDefault(item =>
                    string.Equals(item?.DisplayName, achievementName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item?.ApiName, achievementName, StringComparison.OrdinalIgnoreCase));

                if (achievement != null)
                {
                    context.AchievementCollectionScore = achievement.CollectionScore;
                    context.AchievementPrestigeScore = achievement.PrestigeScore;
                }
                else
                {
                    var fallbackTier = ResolveNotificationRarityTier(achievementRarity);
                    context.AchievementCollectionScore = AchievementScoreCalculator.GetCollectionValue(fallbackTier);
                    context.AchievementPrestigeScore = AchievementScoreCalculator.GetPrestigeValue(
                        TryParseRarityPercent(achievementRarity),
                        fallbackTier);
                }
                context.AchievementType = achievement?.CategoryType ?? string.Empty;
                context.AchievementCategory = achievement?.Category ?? string.Empty;
                PopulateGameNotificationScores(context, gameData);

                var collectionScore = SumNotificationValues(summaries?.Where(item => item != null).Select(item => item.CollectionScore));
                var prestigeScore = SumNotificationValues(summaries?.Where(item => item != null).Select(item => item.PrestigeScore));
                if (context.TotalCollectionScore <= 0)
                {
                    context.TotalCollectionScore = collectionScore;
                }
                if (context.TotalPrestigeScore <= 0)
                {
                    context.TotalPrestigeScore = prestigeScore;
                }
                context.TotalPoints = SumNotificationValues(summaries?.Where(item => item != null).Select(item => item.Points));
            }
            else
            {
                var dataService = plugin?.AchievementDataService;
                var gameData = game != null && game.Id != Guid.Empty
                    ? dataService?.GetVisibleGameAchievementData(game.Id)
                    : null;
                var allData = dataService?.GetAllVisibleGameAchievementDataForTheme() ?? new List<GameAchievementData>();
                gameData = gameData ?? allData.FirstOrDefault(data =>
                    !string.IsNullOrWhiteSpace(gameName) &&
                    string.Equals(data?.GameName, gameName, StringComparison.OrdinalIgnoreCase));
                var achievement = gameData?.Achievements?.FirstOrDefault(item =>
                    string.Equals(item?.DisplayName, achievementName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(item?.ApiName, achievementName, StringComparison.OrdinalIgnoreCase));

                if (achievement != null)
                {
                    context.AchievementCollectionScore = achievement.CollectionScore;
                    context.AchievementPrestigeScore = achievement.PrestigeScore;
                }
                else
                {
                    var fallbackTier = ResolveNotificationRarityTier(achievementRarity);
                    context.AchievementCollectionScore = AchievementScoreCalculator.GetCollectionValue(fallbackTier);
                    context.AchievementPrestigeScore = AchievementScoreCalculator.GetPrestigeValue(
                        TryParseRarityPercent(achievementRarity),
                        fallbackTier);
                }
                context.AchievementType = achievement?.CategoryType ?? string.Empty;
                context.AchievementCategory = achievement?.Category ?? string.Empty;
                PopulateGameNotificationScores(context, gameData);

                var libraryScores = AchievementScoreCalculator.CalculateModernScores(allData);
                if (context.TotalCollectionScore <= 0 && libraryScores.CollectorScore > 0)
                {
                    context.TotalCollectionScore = libraryScores.CollectorScore;
                    context.CollectionLevel = libraryScores.CollectorLevel?.DisplayLevel ?? 0;
                    context.CollectionTier = AchievementRankPresentation.FormatRank(libraryScores.CollectorLevel?.RankValue ?? AchievementRank.Bronze5);
                }
                if (context.TotalPrestigeScore <= 0 && libraryScores.PrestigeScore > 0)
                {
                    context.TotalPrestigeScore = libraryScores.PrestigeScore;
                    context.PrestigeLevel = libraryScores.PrestigeLevel?.DisplayLevel ?? 0;
                    context.PrestigeTier = AchievementRankPresentation.FormatRank(libraryScores.PrestigeLevel?.RankValue ?? AchievementRank.Bronze5);
                }
                context.TotalPoints = SumNotificationPoints(
                    allData.SelectMany(data => data?.Achievements ?? Enumerable.Empty<AchievementDetail>())
                        .Where(item => item?.Unlocked == true));
            }
        }
    }
}
