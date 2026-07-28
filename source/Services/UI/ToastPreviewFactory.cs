using System;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services.UI
{
    /// <summary>
    /// Builds sample <see cref="AchievementUnlockedEventArgs"/> for settings previews and the
    /// inline appearance mockups. Setting <c>ProviderKey</c> on the returned args makes the
    /// notification pipeline resolve the same per-platform style a real unlock from that
    /// provider would use.
    /// </summary>
    internal static class ToastPreviewFactory
    {
        /// <summary>
        /// Returns preview args for the given sample kind: common / uncommon / rare /
        /// ultrarare / capstone / complete / friend / mockup.
        /// </summary>
        public static AchievementUnlockedEventArgs BuildPreviewArgs(string kind, string providerKey = null)
        {
            var sampleGame = L("LOCPlayAch_Settings_ToastPreviewSampleGame");
            var sampleCategory = L("LOCPlayAch_Settings_ToastPreviewSampleCategory");
            var sampleTitle = L("LOCPlayAch_Settings_ToastPreviewSampleTitle");
            var sampleDescription = L("LOCPlayAch_Settings_ToastPreviewSampleDescription");

            switch (kind)
            {
                case "common":
                    return SampleUnlock("Common", 61.4, false);
                case "uncommon":
                    return SampleUnlock("Uncommon", 28.7, false);
                case "rare":
                    return SampleUnlock("Rare", 9.3, false);
                case "ultrarare":
                    return SampleUnlock("UltraRare", 1.8, false);
                case "capstone":
                    var capstone = SampleUnlock("UltraRare", 1.2, true);
                    capstone.IsCompletionAchievement = true;
                    return capstone;
                case "complete":
                    // The standalone completion notification (own wave after unlock toasts).
                    return new AchievementUnlockedEventArgs
                    {
                        IsPreview = true,
                        ProviderKey = providerKey,
                        GameName = sampleGame,
                        UnlockedCount = 40,
                        TotalCount = 40,
                        UnlockTimeUtc = DateTime.UtcNow,
                        IsGameCompleted = true
                    };
                case "friend":
                    var friend = SampleUnlock("Rare", 7.5, false);
                    friend.IsFriendUnlock = true;
                    friend.FriendDisplayName = L("LOCPlayAch_Settings_ToastPreviewSampleFriend");
                    friend.FriendAvatarUrl =
                        "pack://application:,,,/PlayniteAchievements;component/Resources/UnlockedAchIcon.png";
                    return friend;
                case "mockup":
                default:
                    return SampleUnlock("Rare", 9.3, false);
            }

            AchievementUnlockedEventArgs SampleUnlock(string rarity, double percent, bool capstone)
            {
                return new AchievementUnlockedEventArgs
                {
                    IsPreview = true,
                    ProviderKey = providerKey,
                    GameName = sampleGame,
                    Category = sampleCategory,
                    DisplayName = sampleTitle,
                    Description = sampleDescription,
                    RarityTier = rarity,
                    GlobalPercent = percent,
                    IsCapstone = capstone,
                    UnlockedCount = 27,
                    TotalCount = 40,
                    UnlockTimeUtc = DateTime.UtcNow.AddMinutes(-3)
                };
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
