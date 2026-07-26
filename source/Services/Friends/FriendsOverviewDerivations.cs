using PlayniteAchievements.Models.Achievements.Scoring;
using PlayniteAchievements.Services.Summaries;
using PlayniteAchievements.ViewModels.Items;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// Derivations shared by the full friends-overview load and the incremental patch path:
    /// the unlocked/recent sublists computed from AllAchievements and the per-friend summary
    /// scores. Running the identical code over full and patched data keeps the two build paths
    /// consistent by construction.
    /// </summary>
    internal static class FriendsOverviewDerivations
    {
        /// <summary>
        /// Recomputes <see cref="FriendsOverviewData.AllUnlockedAchievements"/>,
        /// <see cref="FriendsOverviewData.RecentUnlocks"/> (limit 0 = all), and the per-friend
        /// summary scores from the current <see cref="FriendsOverviewData.AllAchievements"/>.
        /// </summary>
        public static void Apply(FriendsOverviewData data, int recentLimit)
        {
            if (data == null)
            {
                return;
            }

            data.AllUnlockedAchievements = (data.AllAchievements ?? new List<FriendAchievementDisplayItem>())
                .Where(item => item?.Unlocked == true)
                .ToList();

            // Recent unlocks are the time-stamped subset of all unlocked achievements - derive
            // them in memory (with an explicit unlock-time sort, since the full load is in
            // definition order) rather than re-running the identical friend/achievement join.
            var recentUnlocked = data.AllUnlockedAchievements
                .Where(item => item.UnlockTimeUtc.HasValue)
                .OrderByDescending(item => item.UnlockTimeUtc ?? DateTime.MinValue);
            data.RecentUnlocks = (recentLimit > 0 ? recentUnlocked.Take(recentLimit) : recentUnlocked).ToList();

            ApplyFriendSummaryScores(data.Friends, data.AllUnlockedAchievements);
        }

        public static void ApplyFriendSummaryScores(
            IEnumerable<FriendSummaryItem> friends,
            IEnumerable<FriendAchievementDisplayItem> unlockedAchievements)
        {
            var friendList = friends?.Where(friend => friend != null).ToList();
            if (friendList == null || friendList.Count == 0)
            {
                return;
            }

            var achievementsByFriend = new Dictionary<string, List<FriendAchievementDisplayItem>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in unlockedAchievements ?? Enumerable.Empty<FriendAchievementDisplayItem>())
            {
                var key = BuildFriendScoreKey(achievement?.ProviderKey, achievement?.FriendExternalUserId);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                if (!achievementsByFriend.TryGetValue(key, out var list))
                {
                    list = new List<FriendAchievementDisplayItem>();
                    achievementsByFriend[key] = list;
                }

                list.Add(achievement);
            }

            foreach (var friend in friendList)
            {
                if (achievementsByFriend.TryGetValue(
                        BuildFriendScoreKey(friend.ProviderKey, friend.ExternalUserId),
                        out var friendAchievements))
                {
                    // Reuse the shared accumulator so per-friend scores, rarity, and trophy counts
                    // stay consistent with the per-game friend path in
                    // FriendOverviewProjection.BuildSelectedFriendGameSummary. Every row counts
                    // (no cross-game dedup) to preserve UnlockedAchievementsCount semantics.
                    var stats = AchievementStatsAccumulator.FromDisplayItems(
                        friendAchievements,
                        treatItemsAsUnlocked: true);
                    friend.CollectionScore = stats.CollectionScore;
                    friend.PrestigeScore = stats.PrestigeScore;
                    friend.CommonCount = stats.CommonCount;
                    friend.UncommonCount = stats.UncommonCount;
                    friend.RareCount = stats.RareCount;
                    friend.UltraRareCount = stats.UltraRareCount;
                    friend.TrophyPlatinumCount = stats.TrophyPlatinumCount;
                    friend.TrophyGoldCount = stats.TrophyGoldCount;
                    friend.TrophySilverCount = stats.TrophySilverCount;
                    friend.TrophyBronzeCount = stats.TrophyBronzeCount;
                }

                friend.CollectionLevel = GetDisplayLevel(AchievementLevelCalculator.CalculateModern(friend.CollectionScore));
                friend.PrestigeLevel = GetDisplayLevel(AchievementLevelCalculator.CalculateModern(friend.PrestigeScore));
            }
        }

        private static int GetDisplayLevel(AchievementLevelSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 0;
            }

            return snapshot.DisplayLevel > 0 ? snapshot.DisplayLevel : snapshot.Level;
        }

        internal static string BuildFriendScoreKey(string providerKey, string externalUserId)
        {
            if (string.IsNullOrWhiteSpace(providerKey) || string.IsNullOrWhiteSpace(externalUserId))
            {
                return null;
            }

            return providerKey.Trim() + "\u001f" + externalUserId.Trim();
        }
    }
}
