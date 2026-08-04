using System;
using System.Collections.Generic;
using System.Linq;
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
        // The plugin branding crest stands in for the achievement icon in previews so the mockup
        // is recognizable and independent of any game's art.
        private const string PreviewIcon =
            "pack://application:,,,/PlayniteAchievements;component/Resources/BrandingIcon.png";

        // The repeated sample strings are ~5 KB each and rebuilt for every preview; cache them
        // keyed by the localized source string (UI-thread only, so no synchronization).
        private static string _repeatedTitleSource;
        private static string _repeatedTitle;
        private static string _repeatedDescriptionSource;
        private static string _repeatedDescription;

        /// <summary>
        /// Returns preview args for the given sample kind: common / uncommon / rare /
        /// ultrarare / capstone / complete / friend / mockup.
        /// </summary>
        public static AchievementUnlockedEventArgs BuildPreviewArgs(
            string kind,
            string providerKey = null,
            NotificationTemplatePreviewSource? previewSource = null)
        {
            var sampleGame = L("LOCPlayAch_Settings_ToastPreviewSampleGame");
            var sampleCategory = L("LOCPlayAch_Settings_ToastPreviewSampleCategory");

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
                    // The standalone completion notification (own wave after the unlock wave).
                    return new AchievementUnlockedEventArgs
                    {
                        IsPreview = true,
                        PreviewTemplateSource = previewSource,
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
                // Repeat the sample name and description so previews always demonstrate the
                // trimming / cutoff behavior for long text.
                return new AchievementUnlockedEventArgs
                {
                    IsPreview = true,
                    PreviewTemplateSource = previewSource,
                    ProviderKey = providerKey,
                    GameName = sampleGame,
                    Category = sampleCategory,
                    DisplayName = RepeatCached(
                        L("LOCPlayAch_Settings_ToastPreviewSampleTitle"),
                        ref _repeatedTitleSource,
                        ref _repeatedTitle),
                    Description = RepeatCached(
                        L("LOCPlayAch_Settings_ToastPreviewSampleDescription"),
                        ref _repeatedDescriptionSource,
                        ref _repeatedDescription),
                    IconPath = PreviewIcon,
                    RarityTier = rarity,
                    GlobalPercent = percent,
                    IsCapstone = capstone,
                    UnlockedCount = 27,
                    TotalCount = 40,
                    UnlockTimeUtc = DateTime.UtcNow.AddMinutes(-3)
                };
            }
        }

        private static string RepeatCached(string sample, ref string cachedSource, ref string cachedValue)
        {
            if (!string.Equals(sample, cachedSource, StringComparison.Ordinal))
            {
                cachedValue = string.IsNullOrEmpty(sample)
                    ? sample
                    : string.Join(" ", Enumerable.Repeat(sample, 100));
                cachedSource = sample;
            }

            return cachedValue;
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }
    }
}
