using System;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Services.Showcase
{
    public static partial class ShowcasePinService
    {
        public static bool TryGetAchievementIdentity(
            object data,
            out Guid gameId,
            out string apiName,
            out string gameName,
            out string achievementName,
            out bool friendOwned)
        {
            if (data is FriendAchievementDisplayItem)
            {
                gameId = Guid.Empty;
                apiName = null;
                gameName = null;
                achievementName = null;
                friendOwned = true;
                return false;
            }

            if (data is AchievementDisplayItem item &&
                item.PlayniteGameId.HasValue &&
                !string.IsNullOrWhiteSpace(item.ApiName))
            {
                gameId = item.PlayniteGameId.Value;
                apiName = item.ApiName;
                gameName = item.GameName;
                achievementName = item.DisplayName;
                friendOwned = !string.IsNullOrWhiteSpace(item.FriendName);
                return !friendOwned;
            }

            if (data is RecentAchievementItem recent &&
                recent.PlayniteGameId.HasValue &&
                !string.IsNullOrWhiteSpace(recent.ApiName))
            {
                gameId = recent.PlayniteGameId.Value;
                apiName = recent.ApiName;
                gameName = recent.GameName;
                achievementName = recent.Name;
                friendOwned = false;
                return true;
            }

            gameId = Guid.Empty;
            apiName = null;
            gameName = null;
            achievementName = null;
            friendOwned = false;
            return false;
        }
    }
}
