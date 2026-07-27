using System.IO;
using PlayniteAchievements.Services.Images;

namespace PlayniteAchievements.Services.Friends
{
    /// <summary>
    /// Builds stable, icon_cache-rooted relative paths for friend imagery, keyed by provider, game,
    /// and achievement rather than by source URL. A single cached copy is therefore shared across
    /// every friend that references the same game/achievement, and is never re-downloaded once
    /// present. Reuses the achievement-icon sanitizer/stem logic from
    /// <see cref="AchievementIconCachePathBuilder"/>.
    /// </summary>
    internal static class FriendImageCachePathBuilder
    {
        private const string FallbackStem = "achievement";
        public const string GameCoverFileName = "cover.png";
        public const string GameIconFileName = "icon.png";

        // icon_cache/friendavatars/{provider}_{externalUserId}.png
        public static string BuildAvatarRelativePath(string providerKey, string externalUserId)
        {
            var provider = AchievementIconCachePathBuilder.SanitizeSegment(providerKey);
            var user = AchievementIconCachePathBuilder.SanitizeSegment(externalUserId);
            return Path.Combine(
                "icon_cache",
                FriendImageCacheFolders.Avatars,
                $"{provider}_{user}.png");
        }

        // icon_cache/friendgames/{provider}/{gameKey}
        public static string BuildGameFolderRelativePath(string providerKey, string gameKey)
        {
            var provider = AchievementIconCachePathBuilder.SanitizeSegment(providerKey);
            var game = AchievementIconCachePathBuilder.SanitizeSegment(gameKey);
            return Path.Combine(
                "icon_cache",
                FriendImageCacheFolders.Games,
                provider,
                game);
        }

        // icon_cache/friendgames/{provider}/{gameKey}/{fileName}
        public static string BuildGameImageRelativePath(string providerKey, string gameKey, string fileName)
        {
            return Path.Combine(BuildGameFolderRelativePath(providerKey, gameKey), fileName);
        }

        // The achievement icon filename within a game folder: "{stem}.png" or "{stem}.locked.png".
        public static string GetAchievementFileName(string fileStem, AchievementIconVariant variant)
        {
            var stem = string.IsNullOrWhiteSpace(fileStem) ? FallbackStem : fileStem.Trim();
            return variant == AchievementIconVariant.Locked
                ? stem + ".locked.png"
                : stem + ".png";
        }
    }
}
