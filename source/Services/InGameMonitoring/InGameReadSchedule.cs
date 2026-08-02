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
            DateTime firstFallbackDueUtc,
            bool hasProgressSource,
            bool isRemote,
            bool equivalent)
        {
            RetryAttempt = 0;
            Degraded = false;
            Dirty = hasProgressSource;

            if (equivalent)
            {
                // Preserve the session baseline and the fallback deadline. A live source can
                // reconcile immediately against its newly supplied cached-schema snapshot.
                if (hasProgressSource)
                {
                    NextDueUtc = nowUtc;
                }

                return;
            }

            Primed = false;
            LastFileEventUtc = default;
            NextDueUtc = hasProgressSource
                ? isRemote ? nowUtc : DateTime.MaxValue
                : firstFallbackDueUtc > nowUtc ? firstFallbackDueUtc : nowUtc;
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

        public void BeginRead()
        {
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

        public void MarkFallbackSuccess(DateTime nowUtc, TimeSpan cadence)
        {
            Primed = true;
            Dirty = false;
            RetryAttempt = 0;
            Degraded = false;
            NextDueUtc = nowUtc.Add(cadence);
        }

        public void DueAt(DateTime dueUtc)
        {
            NextDueUtc = dueUtc;
        }
    }
}
