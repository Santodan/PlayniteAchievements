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
    }
}
