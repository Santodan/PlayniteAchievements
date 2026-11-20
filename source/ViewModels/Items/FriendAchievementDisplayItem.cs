using System;
using System.Runtime.Serialization;
using System.Windows.Input;
using Playnite.SDK.Data;
using PlayniteAchievements.Common;
using PlayniteAchievements.Services.Friends;

namespace PlayniteAchievements.ViewModels.Items
{
    public sealed class FriendAchievementDisplayItem : AchievementDisplayItem
    {
        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeProviderCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeUserCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public ICommand SetDynamicFriendScopeGameCommand { get; set; }

        [DontSerialize]
        [IgnoreDataMember]
        public string FriendScopeKey => FriendOverviewProjection.GetFriendScopeKey(this)
            ?? FriendOverviewProjection.AllScopeKey;

        [DontSerialize]
        [IgnoreDataMember]
        public string FriendGameScopeKey => FriendOverviewProjection.BuildGameUnlockKey(ProviderKey, ProviderGameKey, AppId, PlayniteGameId)
            ?? FriendOverviewProjection.AllScopeKey;

        public int AppId { get; set; }

        public string ProviderGameKey { get; set; }

        public string FriendGroupId { get; set; }

        public bool UnlockedBySelf { get; set; }

        public DateTime? SelfUnlockTimeUtc { get; set; }

        /// <summary>
        /// True when the current user has cached progress for this row's Playnite game,
        /// so a self comparison shows real data instead of an all-locked column.
        /// </summary>
        public bool SelfHasGameData { get; set; }

        // Whether the friend is known to have this row's game: it is in their game list or has
        // playtime. False for a row made only by scraping their achievements for the game, which
        // lists every achievement locked whether or not they own it.
        public bool FriendOwnsGame { get; set; }

        /// <summary>
        /// Unless spoilers are shown, visibility decisions use the current user's
        /// unlock state instead of the friend's, so achievements the user has not
        /// unlocked are obscured per the achievement visibility settings.
        /// </summary>
        public override bool UnlockedForVisibility =>
            ShowFriendSpoilers ? base.UnlockedForVisibility : UnlockedBySelf;

        public DateTime? FriendUnlockTimeUtc
        {
            get => UnlockTimeUtc;
            set => UnlockTimeUtc = value;
        }

        public DateTime? FriendUnlockTimeLocal => DateTimeUtilities.AsLocalFromUtc(FriendUnlockTimeUtc);

        public override bool ShowUnlockDate => FriendUnlockTimeUtc.HasValue;

        public override bool ShowLockedProgress => !ShowUnlockDate;

        /// <summary>A friend row's read carries its friend, self and comparison columns too.</summary>
        public override void RefreshFrom(AchievementDisplayItem fresh)
        {
            if (fresh is FriendAchievementDisplayItem row)
            {
                // Set before the base update, whose notifications cover what these change.
                AppId = row.AppId;
                ProviderGameKey = row.ProviderGameKey;
                FriendGroupId = row.FriendGroupId;
                UnlockedBySelf = row.UnlockedBySelf;
                SelfUnlockTimeUtc = row.SelfUnlockTimeUtc;
                SelfHasGameData = row.SelfHasGameData;
                FriendOwnsGame = row.FriendOwnsGame;
                FriendName = row.FriendName;
                FriendExternalUserId = row.FriendExternalUserId;
                FriendAvatarPath = row.FriendAvatarPath;
                FriendIsFavorite = row.FriendIsFavorite;
                if (row.HasComparison)
                {
                    ApplyComparison(row.ComparisonFriendName, row.ComparisonFriendAvatarPath, row.ComparisonUnlockTimeUtc,
                        row.ComparisonUnlocked, row.ComparisonOwnerName, row.ComparisonOwnerAvatarPath, row.ComparisonApplicable);
                }
                else
                {
                    ClearComparison();
                }
            }

            base.RefreshFrom(fresh);
        }
    }
}
