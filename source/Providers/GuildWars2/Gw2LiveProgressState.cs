using System.Collections.Generic;

namespace PlayniteAchievements.Providers.GuildWars2
{
    /// <summary>
    /// Shared between the live in-game reader and the full refresh, so the two halves of the
    /// provider can tell what the other has already done.
    ///
    /// The current-user refresh path has no notion of a partial payload: a provider returns a whole
    /// game and the cache upserts a whole game. The live path does have one, and while a session is
    /// running it applies every change as it happens. This records how far it has got, so the full
    /// refresh can recognize that a rebuild of every row would tell the cache nothing it was not
    /// already told.
    /// </summary>
    internal sealed class Gw2LiveProgressState
    {
        private readonly object _lock = new object();

        private Dictionary<int, Gw2ProgressSignature> _applied;

        /// <summary>
        /// Records account progress the live reader has finished pushing into the cache. Called
        /// only after observations for the whole diff have been handed to the monitor, never for
        /// the silent baseline read, which emits nothing.
        /// </summary>
        public void MarkApplied(Dictionary<int, Gw2ProgressSignature> snapshot)
        {
            lock (_lock)
            {
                _applied = snapshot;
            }
        }

        /// <summary>
        /// True when the live reader has already applied exactly this progress. A false answer is
        /// always safe: it only means a full rebuild runs.
        /// </summary>
        public bool HasApplied(Dictionary<int, Gw2ProgressSignature> snapshot)
        {
            lock (_lock)
            {
                return Gw2ProgressSnapshot.AreEquivalent(_applied, snapshot);
            }
        }

        /// <summary>
        /// Forgets the applied point. The session is over, so the next refresh rebuilds and
        /// reconciles whatever the live reader may have missed.
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _applied = null;
            }
        }
    }
}
