using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// A process-wide gate marking the notification slide's span, so periodic work that shares the
    /// UI thread or invalidates visuals every frame can stand down while the slide animates.
    /// Producers engage a ref-counted scope around each slide; consumers either poll
    /// <see cref="IsEngaged"/> from their per-frame callback or await <see cref="WhenClearAsync"/>
    /// with their own bound before marshaling work to the UI thread.
    /// </summary>
    internal static class RenderQuietGate
    {
        /// <summary>
        /// Readers treat an engagement older than this as cleared. A scope leaked by a producer
        /// (an exotic theme storyboard that never completes, an exception path that skips
        /// disposal) can therefore never starve consumers indefinitely; the built-in slides are
        /// an order of magnitude shorter than this cap.
        /// </summary>
        internal const int HardCapMs = 1500;

        private const int PollIntervalMs = 25;

        private static int _engagements;
        private static int _engagedAtTick;

        /// <summary>
        /// Whether a quiet span is currently active. Readable from any thread; false once every
        /// scope is disposed or the newest engagement is older than <see cref="HardCapMs"/>.
        /// </summary>
        public static bool IsEngaged
        {
            get
            {
                if (Volatile.Read(ref _engagements) <= 0)
                {
                    return false;
                }

                // Tick subtraction is wrap-safe in unchecked int arithmetic for spans far below
                // the counter's ~24.9-day period.
                return unchecked(Environment.TickCount - Volatile.Read(ref _engagedAtTick)) <= HardCapMs;
            }
        }

        /// <summary>
        /// Opens one quiet scope. Scopes nest by ref count, and each engage refreshes the
        /// hard-cap stamp. Disposal is idempotent.
        /// </summary>
        public static IDisposable Engage()
        {
            Volatile.Write(ref _engagedAtTick, Environment.TickCount);
            Interlocked.Increment(ref _engagements);
            return new EngageScope();
        }

        /// <summary>
        /// Completes when the gate is clear, or after <paramref name="maxDeferMs"/> at the
        /// latest — the caller's own bound, independent of <see cref="HardCapMs"/>. Returns
        /// synchronously when the gate is already clear.
        /// </summary>
        public static async Task WhenClearAsync(int maxDeferMs)
        {
            var start = Environment.TickCount;
            while (IsEngaged && unchecked(Environment.TickCount - start) < maxDeferMs)
            {
                await Task.Delay(PollIntervalMs).ConfigureAwait(false);
            }
        }

        private sealed class EngageScope : IDisposable
        {
            private int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Interlocked.Decrement(ref _engagements);
            }
        }
    }
}
