using System;
using System.Windows;
using System.Windows.Threading;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Raises <see cref="DayChanged"/> on the UI thread just after local midnight so views whose
    /// window ends at "today" re-evaluate. One dispatcher timer serves every subscriber; each
    /// subscriber owns its lifetime and unsubscribes when disposed.
    /// </summary>
    public static class LocalDayRollover
    {
        private static readonly object Sync = new object();
        private static DispatcherTimer _timer;
        private static DateTime _armedForDay;
        private static EventHandler<DateTime> _handlers;

        /// <summary>Fires with the new local date once the calendar day changes.</summary>
        public static event EventHandler<DateTime> DayChanged
        {
            add => Subscribe(value);
            remove => Unsubscribe(value);
        }

        public static void Subscribe(EventHandler<DateTime> handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (Sync)
            {
                _handlers += handler;
            }

            EnsureTimer();
        }

        public static void Unsubscribe(EventHandler<DateTime> handler)
        {
            if (handler == null)
            {
                return;
            }

            lock (Sync)
            {
                _handlers -= handler;
            }
        }

        private static void EnsureTimer()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(EnsureTimer));
                return;
            }

            if (_timer != null)
            {
                return;
            }

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Tick += OnTick;
            Arm();
        }

        private static void Arm()
        {
            var now = DateTime.Now;
            _armedForDay = now.Date;
            var untilNextDay = now.Date.AddDays(1).AddSeconds(1) - now;
            if (untilNextDay < TimeSpan.FromSeconds(1))
            {
                untilNextDay = TimeSpan.FromSeconds(1);
            }

            _timer.Interval = untilNextDay;
            _timer.Start();
        }

        private static void OnTick(object sender, EventArgs e)
        {
            _timer.Stop();
            var today = DateTime.Now.Date;
            if (today != _armedForDay)
            {
                EventHandler<DateTime> handlers;
                lock (Sync)
                {
                    handlers = _handlers;
                }

                handlers?.Invoke(null, today);
            }

            // Re-arm either way: a clock moved backwards leaves the date unchanged and the timer
            // simply waits for the next midnight.
            Arm();
        }
    }
}
