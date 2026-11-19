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
        private readonly object _sync = new object();
        private readonly Queue<Action> _background = new Queue<Action>();
        private readonly Queue<Action> _ui = new Queue<Action>();
        private readonly Queue<Action> _idle = new Queue<Action>();
        private readonly List<Action> _timers = new List<Action>();

        public int PendingReads { get { lock (_sync) { return _background.Count; } } }

        public int PendingTimers { get { lock (_sync) { return _timers.Count; } } }

        public int PendingIdle { get { lock (_sync) { return _idle.Count; } } }

        /// <summary>Runs reads and UI work as they are queued, for tests that read synchronously.</summary>
        public bool Immediate { get; set; }

        public void Run(Action work)
        {
            if (Immediate)
            {
                work();
                return;
            }

            lock (_sync) { _background.Enqueue(work); }
        }

        // Code under test may post from another thread; the queued work still runs on the test's.
        public void Post(Action action)
        {
            if (Immediate)
            {
                action();
                return;
            }

            lock (_sync) { _ui.Enqueue(action); }
        }

        /// <summary>The delay of every <see cref="PostAfter"/>, in call order.</summary>
        public List<TimeSpan> Delays { get; } = new List<TimeSpan>();

        public void PostAfter(TimeSpan delay, Action action) { lock (_sync) { Delays.Add(delay); _timers.Add(action); } }

        public void PostIdle(Action action)
        {
            if (Immediate)
            {
                action();
                return;
            }

            lock (_sync) { _idle.Enqueue(action); }
        }

        /// <summary>
        /// Runs the queued reads, then the queued UI work, until both are empty, and idle work
        /// whenever they are, until all three are.
        /// </summary>
        public void Drain()
        {
            while (PendingReads > 0 || PendingUi > 0 || PendingIdle > 0)
            {
                RunReads();
                RunUi();
                if (PendingReads == 0 && PendingUi == 0)
                {
                    RunIdle();
                }
            }
        }

        /// <summary>Runs the idle work queued now; what it queues waits.</summary>
        public void RunIdle()
        {
            foreach (var action in Take(_idle))
            {
                action();
            }
        }

        /// <summary>Runs the reads queued now; what they queue waits.</summary>
        public void RunReads()
        {
            foreach (var work in Take(_background))
            {
                work();
            }
        }

        /// <summary>Runs the UI work queued now; what it queues waits.</summary>
        public void RunUi()
        {
            foreach (var action in Take(_ui))
            {
                action();
            }
        }

        private int PendingUi { get { lock (_sync) { return _ui.Count; } } }

        // The work queued now; what it queues waits.
        private Action[] Take(Queue<Action> queue)
        {
            lock (_sync)
            {
                var items = queue.ToArray();
                queue.Clear();
                return items;
            }
        }

        /// <summary>Ends every pending window: their actions run on the UI queue.</summary>
        public void Elapse()
        {
            Action[] timers;
            lock (_sync)
            {
                timers = _timers.ToArray();
                _timers.Clear();
            }

            foreach (var timer in timers)
            {
                Post(timer);
            }
        }
    }
}
