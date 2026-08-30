namespace PlayniteAchievements.Providers
{
    /// <summary>
    /// Opt-in for providers whose achievements can be earned more than once, so a newer unlock time
    /// on an already-unlocked achievement is a genuine new event.
    ///
    /// The unlock differ ignores a moved timestamp by default, and deliberately: most providers can
    /// be read from more than one source, and those sources disagree about when the original unlock
    /// happened — a local stats file records the moment, a scraped page a coarser rendered time.
    /// Treating that shift as new would re-announce a whole library. A provider may implement this
    /// interface only when it has a single authoritative source whose timestamp moves solely
    /// because the player earned the achievement again.
    ///
    /// League of Legends challenges are the first case: each one is re-earned at successively
    /// higher tiers, and Riot reports the moment the current tier was reached.
    /// </summary>
    internal interface IRepeatableUnlockProvider
    {
        /// <summary>
        /// True when a newer unlock time on an already-unlocked achievement should be announced as
        /// a new unlock. A provider whose repeat behaviour is configurable can return false to opt
        /// back out at runtime.
        /// </summary>
        bool ReportsRepeatUnlocks { get; }
    }
}
