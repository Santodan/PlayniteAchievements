using PlayniteAchievements.Common;
using System;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace PlayniteAchievements.Services.Database
{
    /// <summary>Where <see cref="LiveQuery{T}"/> and <see cref="PagedRows{T}"/> run reads and apply results.</summary>
    internal interface IReadScheduler
    {
        /// <summary>Runs <paramref name="work"/> off the UI thread.</summary>
        void Run(Action work);

        /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
        void Post(Action action);

        /// <summary>Runs <paramref name="action"/> on the UI thread after <paramref name="delay"/>.</summary>
        void PostAfter(TimeSpan delay, Action action);

        /// <summary>Runs <paramref name="action"/> on the UI thread once input and rendering are done.</summary>
        void PostIdle(Action action);
    }

    /// <summary>Reads on the thread pool, applies on a dispatcher at Normal priority.</summary>
    internal sealed class DispatcherReadScheduler : IReadScheduler
    {
        private readonly Dispatcher _dispatcher;

        public DispatcherReadScheduler(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
        }

        public void Run(Action work) => Task.Run(work);

        public void Post(Action action) => _dispatcher.InvokeIfNeeded(action, DispatcherPriority.Normal);

        public void PostAfter(TimeSpan delay, Action action) =>
            Task.Delay(delay).ContinueWith(_ => Post(action), TaskScheduler.Default);

        public void PostIdle(Action action) => _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }
}
