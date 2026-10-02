namespace PlayniteAchievements.Models
{
    /// <summary>
    /// User override for the bar width of an unlocks-over-time chart. <see cref="Auto"/> lets
    /// the chart choose from the window span; the others force a unit and only escalate when the
    /// bar count would exceed the chart's cap.
    /// </summary>
    public enum TimelineGranularity
    {
        Auto = 0,
        Day = 1,
        Week = 2,
        Month = 3
    }
}
