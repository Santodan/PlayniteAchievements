using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Services.Images
{
    /// <summary>
    /// Counts writes into the icon cache, so a move can stop new writes and wait for running ones
    /// to finish before it touches the files. A write that finds the gate closed is skipped.
    /// </summary>
    public sealed class IconCacheWriteGate
    {
        private readonly object _sync = new object();
        private int _active;
        private bool _closed;
        private TaskCompletionSource<bool> _drained;

        public bool IsOpen
        {
            get
            {
                lock (_sync)
                {
                    return !_closed;
                }
            }
        }

        /// <summary>A lease to dispose when the write ends, or null when writes are stopped.</summary>
        public IDisposable TryEnter()
        {
            lock (_sync)
            {
                if (_closed)
                {
                    return null;
                }

                _active++;
                return new Lease(this);
            }
        }

        /// <summary>Stops new writes and completes once every running write has ended.</summary>
        public Task CloseAndDrainAsync(CancellationToken cancel)
        {
            Task drained;
            lock (_sync)
            {
                _closed = true;
                if (_active == 0)
                {
                    return Task.CompletedTask;
                }

                _drained = _drained ?? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drained = _drained.Task;
            }

            if (!cancel.CanBeCanceled)
            {
                return drained;
            }

            return WaitWithCancelAsync(drained, cancel);
        }

        public void Close()
        {
            lock (_sync)
            {
                _closed = true;
            }
        }

        public void Open()
        {
            lock (_sync)
            {
                _closed = false;
            }
        }

        private static async Task WaitWithCancelAsync(Task drained, CancellationToken cancel)
        {
            var cancelled = new TaskCompletionSource<bool>();
            using (cancel.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(drained, cancelled.Task).ConfigureAwait(false) != drained)
                {
                    throw new OperationCanceledException(cancel);
                }
            }
        }

        private void Exit()
        {
            TaskCompletionSource<bool> drained = null;
            lock (_sync)
            {
                _active--;
                if (_active == 0 && _drained != null)
                {
                    drained = _drained;
                    _drained = null;
                }
            }

            drained?.TrySetResult(true);
        }

        private sealed class Lease : IDisposable
        {
            private IconCacheWriteGate _owner;

            public Lease(IconCacheWriteGate owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.Exit();
            }
        }
    }
}
