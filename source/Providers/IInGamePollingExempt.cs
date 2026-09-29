namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Marks a provider whose data cannot change while the game is running, so the in-game
    /// monitor does not track its games. Its periodic refreshes would only repeat requests to the
    /// provider's service and return the same data. For example, FFXIV Collect reads characters from
    /// the Lodestone, which updates every few hours.
    /// </summary>
    internal interface IInGamePollingExempt
    {
    }
}
