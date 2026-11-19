using System;
using System.Collections.Generic;
using PlayniteAchievements.Services.Database;

namespace PlayniteAchievements.Tests.TestSupport
{
    /// <summary>
    /// An <see cref="IReadScheduler"/> that queues everything until the test runs it, so reads,
    /// applies and windows happen in an order the test chooses, on the test thread.
    /// </summary>
    internal sealed class ManualReadScheduler : IReadScheduler
    {
        private readonly Queue<Action> _background = new Queue<Action>();
        private readonly Queue<Action> _ui = new Queue<Action>();
        private readonly List<Action> _timers = new List<Action>();

        public int PendingReads => _background.Count;

        public int PendingTimers => _timers.Count;

        public void Run(Action work) => _background.Enqueue(work);

        public void Post(Action action) => _ui.Enqueue(action);

        public void PostAfter(TimeSpan delay, Action action) => _timers.Add(action);

        /// <summary>Runs the queued reads, then the queued UI work, until both are empty.</summary>
        public void Drain()
        {
            while (_background.Count > 0 || _ui.Count > 0)
            {
                RunReads();
                RunUi();
            }
        }

        /// <summary>Runs the reads queued now; what they queue waits.</summary>
        public void RunReads()
        {
            for (var count = _background.Count; count > 0; count--)
            {
                _background.Dequeue()();
            }
        }

        /// <summary>Runs the UI work queued now; what it queues waits.</summary>
        public void RunUi()
        {
            for (var count = _ui.Count; count > 0; count--)
            {
                _ui.Dequeue()();
            }
        }

        /// <summary>Ends every pending window: their actions run on the UI queue.</summary>
        public void Elapse()
        {
            var timers = _timers.ToArray();
            _timers.Clear();
            foreach (var timer in timers)
            {
                Post(timer);
            }
        }
    }
}
