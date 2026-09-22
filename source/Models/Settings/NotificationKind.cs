using PlayniteAchievements.Models;

namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// The notification kinds that can carry their own appearance. <see cref="Base"/> is the
    /// style every notification uses unless its kind has been given a separate one; the other
    /// members map onto the discriminator flags on <see cref="AchievementUnlockedEventArgs"/>.
    /// </summary>
    public enum NotificationKind
    {
        Base,
        Capstone,
        Completion,
        Friend,
        Progress
    }

    /// <summary>
    /// Maps an unlock's discriminator flags onto the one kind whose style it uses.
    /// </summary>
    public static class NotificationKindResolver
    {
        /// <summary>
        /// The kind slots a user can style separately, in the order the settings UI lists them.
        /// </summary>
        public static readonly NotificationKind[] StyleableKinds =
        {
            NotificationKind.Capstone,
            NotificationKind.Completion,
            NotificationKind.Friend,
            NotificationKind.Progress
        };

        /// <summary>
        /// Resolves the kind for one notification. The flags are not mutually exclusive, so the
        /// order here is the rule: the standalone 100% notification wins over everything, then a
        /// capstone unlock, then a progress advance, then a friend's unlock. A friend's 100%
        /// therefore uses the completion style, matching how the header text already branches
        /// friend vs. non-friend inside completion.
        /// </summary>
        public static NotificationKind Resolve(AchievementUnlockedEventArgs args)
        {
            if (args == null)
            {
                return NotificationKind.Base;
            }

            if (args.IsGameCompleted)
            {
                return NotificationKind.Completion;
            }

            if (args.IsCapstone)
            {
                return NotificationKind.Capstone;
            }

            if (args.IsProgressUpdate)
            {
                return NotificationKind.Progress;
            }

            if (args.IsFriendUnlock)
            {
                return NotificationKind.Friend;
            }

            return NotificationKind.Base;
        }
    }
}
