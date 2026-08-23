using System;
using System.Collections.Generic;

namespace PlayniteAchievements.Services.InGameMonitoring
{
    /// <summary>
    /// Deterministic per-game scheduling state for the in-game monitor. Keeping timing
    /// transitions here makes file priming, trailing debounce, safety reads, and degraded
    /// retries independent from the scheduler loop's wall-clock timing.
    /// </summary>
    internal sealed class InGameReadSchedule
    {
        public DateTime NextDueUtc { get; private set; }

        public DateTime LastFileEventUtc { get; private set; }

        /// <summary>
        /// Timestamp assigned to the read currently in flight. A file-triggered read carries the
        /// unconsumed watcher event; safety/remote reads use their local start time.
        /// </summary>
        public DateTime ActiveReadObservedUtc { get; private set; }

        private DateTime _activeFileEventUtc;
        private DateTime _consumedFileEventUtc;

        public bool Primed { get; private set; }

        public bool Dirty { get; private set; }

        public bool Degraded { get; private set; }

        public int RetryAttempt { get; private set; }

        public bool ShouldEmitUnlocks()
        {
            // The first successful read of any source establishes the baseline silently; only
            // subsequent reads emit. Remote sources are no longer exempted, so a freshly cleared
            // or never-refreshed game can never surface its earned backlog on the first grab.
            return Primed;
        }

        public void Configure(
            DateTime nowUtc,
            bool hasProgressSource,
            bool isRemote,
            bool equivalent)
        {
            RetryAttempt = 0;
            Degraded = false;
            Dirty = hasProgressSource;

            if (equivalent)
            {
                // Preserve the session baseline. A live source can reconcile immediately against
                // its newly supplied cached-schema snapshot.
                if (hasProgressSource)
                {
                    NextDueUtc = nowUtc;
                }

                return;
            }

            Primed = false;
            LastFileEventUtc = default;
            ActiveReadObservedUtc = default;
            _activeFileEventUtc = default;
            _consumedFileEventUtc = default;
            // A remote source reads immediately; a file source waits for its watcher subscription
            // or first file event. With no fast source this schedule drives nothing at all — the
            // universal provider-refresh prong keeps its own deadline outside this type.
            NextDueUtc = hasProgressSource && isRemote ? nowUtc : DateTime.MaxValue;
        }

        public void SourceAttached(DateTime nowUtc)
        {
            // A file event delivered while subscriptions were being attached wins, ensuring
            // the baseline read happens after that event's trailing debounce.
            if (LastFileEventUtc == default)
            {
                NextDueUtc = nowUtc;
            }
        }

        public void BeginRead(DateTime nowUtc)
        {
            _activeFileEventUtc = LastFileEventUtc > _consumedFileEventUtc
                ? LastFileEventUtc
                : default;
            ActiveReadObservedUtc = _activeFileEventUtc != default
                ? _activeFileEventUtc
                : nowUtc;
            Dirty = false;
        }

        public void SignalFile(DateTime nowUtc, bool watcherError, TimeSpan debounce)
        {
            Dirty = true;
            LastFileEventUtc = nowUtc;
            if (watcherError)
            {
                Degraded = true;
                NextDueUtc = nowUtc;
            }
            else
            {
                // Repeated signals replace the deadline, producing a true trailing debounce.
                NextDueUtc = nowUtc.Add(debounce);
            }
        }

        public void Succeeded(DateTime nowUtc, TimeSpan safetyCadence)
        {
            if (_activeFileEventUtc > _consumedFileEventUtc)
            {
                _consumedFileEventUtc = _activeFileEventUtc;
            }

            ActiveReadObservedUtc = default;
            _activeFileEventUtc = default;
            Primed = true;
            RetryAttempt = 0;
            Degraded = false;
            if (!Dirty)
            {
                NextDueUtc = nowUtc.Add(safetyCadence);
            }
        }

        public void Failed(
            DateTime nowUtc,
            IReadOnlyList<int> retryMilliseconds,
            TimeSpan degradedCadence)
        {
            // Do not consume the active file event. A retry must retain the original source
            // correlation point unless a newer watcher event supersedes it.
            ActiveReadObservedUtc = default;
            _activeFileEventUtc = default;
            var attempt = RetryAttempt++;
            if (retryMilliseconds != null && attempt < retryMilliseconds.Count)
            {
                NextDueUtc = nowUtc.AddMilliseconds(retryMilliseconds[attempt]);
                return;
            }

            RetryAttempt = 0;
            Degraded = true;
            NextDueUtc = nowUtc.Add(degradedCadence);
        }

        /// <summary>
        /// Records that the universal provider-refresh prong established the session baseline.
        /// Baseline state is per game, not per prong, so whichever prong reads first sets it — both
        /// write through the same monotonic cache, so the other prong's first read then legitimately
        /// finds nothing new. Deliberately touches nothing else: <see cref="Dirty"/>,
        /// <see cref="NextDueUtc"/>, <see cref="RetryAttempt"/> and <see cref="Degraded"/> all
        /// describe the fast source, and clearing them here would drop a pending file event,
        /// overwrite the fast prong's deadline, or mask its failures.
        /// </summary>
        public void MarkPrimed()
        {
            Primed = true;
        }

        public void DueAt(DateTime dueUtc)
        {
            NextDueUtc = dueUtc;
        }
    }
}
