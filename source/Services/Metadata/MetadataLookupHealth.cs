using System.Threading;

namespace PlayniteAchievements.Services.Metadata
{
    /// <summary>
    /// Whether a lookup could not read something it asked for (a failed load, a challenge page, a
    /// search that did not answer). Pages that could not be read may hold a game or DLC the lookup
    /// is missing, so its answer then counts only when it found something for every sizeable
    /// category; otherwise the lookup is unavailable rather than a no-match or a partial link, and
    /// the game is looked for again later.
    /// </summary>
    internal sealed class MetadataLookupHealth
    {
        private int _unreadable;

        public bool HadUnreadable => _unreadable > 0;

        public void MarkUnreadable() => Interlocked.Increment(ref _unreadable);

        /// <summary>
        /// True when <paramref name="choice"/> cannot be trusted: something went unread and there is
        /// no choice, or the choice leaves a sizeable category with no match at all. A few unmatched
        /// achievements spread over the game's categories are a platform's own differences, which
        /// a page that went unread would not change.
        /// </summary>
        public bool LeavesDoubt(MetadataPageChoice choice) =>
            HadUnreadable && (choice == null || choice.Uncovered.Count > 0);
    }
}
