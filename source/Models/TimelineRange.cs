namespace PlayniteAchievements.Models
{
    /// <summary>
    /// Represents the time range for timeline graphs.
    /// </summary>
    public enum TimelineRange
    {
        SevenDays,
        FourteenDays,
        OneMonth,
        ThreeMonths,
        OneYear,
        All,
        // Appended after All: legacy settings store this enum as its integer ordinal.
        SixMonths
    }

    /// <summary>
    /// The single mapping from a timeline range to its localized label, shared by the range
    /// pickers, the timeline widget, and the score history caption.
    /// </summary>
    public static class TimelineRangeText
    {
        public static string Describe(TimelineRange range)
        {
            switch (range)
            {
                case TimelineRange.SevenDays:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_7D");
                case TimelineRange.FourteenDays:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_14D");
                case TimelineRange.OneMonth:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_1M");
                case TimelineRange.SixMonths:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_6M");
                case TimelineRange.OneYear:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_1Y");
                case TimelineRange.All:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_Common_All");
                default:
                    return Playnite.SDK.ResourceProvider.GetString("LOCPlayAch_TimeRange_3M");
            }
        }
    }
}