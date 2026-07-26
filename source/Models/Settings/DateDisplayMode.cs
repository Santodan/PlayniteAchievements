namespace PlayniteAchievements.Models.Settings
{
    /// <summary>
    /// Controls how a date/time grid column renders its value. Display only; sorting is unaffected.
    /// </summary>
    public enum DateDisplayMode
    {
        // Short date and time (the historical default).
        DateAndTime,

        // Short date only, time component omitted.
        DateOnly,

        // Bucketed relative label (Today, Yesterday, This Week, ...).
        Relative
    }
}
