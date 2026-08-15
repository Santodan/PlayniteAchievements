namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Calendar window a friend's game must have been played within for Full/Recent friend
    /// refreshes (and unowned discovery) to fetch its data. Buckets are calendar-based to match
    /// the relative-date labels: ThisWeek starts at the current local week, ThisMonth at the 1st,
    /// ThisYear at January 1. Games with no last-played date are always fetched.
    /// </summary>
    public enum FriendLastPlayedThreshold
    {
        ThisWeek,

        ThisMonth,

        ThisYear,

        AllTime
    }
}
