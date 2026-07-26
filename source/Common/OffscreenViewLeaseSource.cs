using Playnite.SDK;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Common
{
    /// <summary>
    /// Provides offscreen CEF views with an optional lease that shares one view across
    /// many calls. While at least one lease is active, acquisition returns a single
    /// shared view (created lazily); otherwise each acquisition returns a fresh
    /// per-call view the caller owns. A faulted shared view is discarded so the next
    /// acquisition recreates it. Offscreen views have no UI-thread affinity (CEF runs
    /// its own threads), so all members may be called from any thread; concurrent
    /// navigations on the shared view are serialized through an internal gate.
    /// </summary>
    internal sealed class OffscreenViewLeaseSource
    {
        private readonly IPlayniteAPI _api;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly SemaphoreSlim _navGate = new SemaphoreSlim(1, 1);
        private IWebView _leasedView;
        private int _leaseCount;

        public OffscreenViewLeaseSource(IPlayniteAPI api, ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _logger = logger;
        }

        /// <summary>
        /// Holds the shared view open until the returned lease is disposed. Leases nest;
        /// the view is disposed when the last lease ends.
        /// </summary>
        public IDisposable BeginLease()
        {
            lock (_lock)
            {
                _leaseCount++;
            }

            return new Lease(this);
        }

        private sealed class Lease : IDisposable
        {
            private OffscreenViewLeaseSource _owner;

            public Lease(OffscreenViewLeaseSource owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _owner, null)?.EndLease();
            }
        }

        private void EndLease()
        {
            IWebView toDispose;
            lock (_lock)
            {
                _leaseCount = Math.Max(0, _leaseCount - 1);
                if (_leaseCount > 0)
                {
                    return;
                }

                toDispose = _leasedView;
                _leasedView = null;
            }

            if (toDispose == null)
            {
                return;
            }

            // Wait briefly for any in-flight navigation to release the gate so the view is
            // not disposed under an active operation (e.g. a cancelled refresh still unwinding);
            // dispose regardless after the timeout so a wedged navigation cannot leak the view.
            var gateHeld = false;
            try
            {
                gateHeld = _navGate.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to acquire nav gate before disposing offscreen view.");
            }

            try
            {
                toDispose.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose offscreen view.");
            }
            finally
            {
                if (gateHeld)
                {
                    _navGate.Release();
                }
            }
        }

        /// <summary>
        /// Returns the shared leased view (created on first use) when a lease is active,
        /// otherwise a fresh per-call view the caller owns.
        /// </summary>
        public (IWebView View, bool Owned) AcquireView()
        {
            lock (_lock)
            {
                if (_leaseCount > 0)
                {
                    if (_leasedView == null)
                    {
                        _leasedView = _api.WebViews.CreateOffscreenView();
                    }

                    return (_leasedView, false);
                }
            }

            return (_api.WebViews.CreateOffscreenView(), true);
        }

        /// <summary>
        /// Releases a view returned by AcquireView. Per-call views are disposed;
        /// a faulted shared view is discarded so the next acquisition recreates it.
        /// </summary>
        public void ReleaseView(IWebView view, bool owned, bool faulted)
        {
            if (view == null)
            {
                return;
            }

            if (!owned)
            {
                if (!faulted)
                {
                    return;
                }

                lock (_lock)
                {
                    if (ReferenceEquals(_leasedView, view))
                    {
                        _leasedView = null;
                    }
                }
            }

            try
            {
                view.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose offscreen view.");
            }
        }

        /// <summary>
        /// Navigates an offscreen view to a URL and returns the page's text content (e.g. a
        /// JSON API response rendered as a plain-text document). Useful when a site's WAF
        /// tarpits the .NET HTTP stack's TLS fingerprint but accepts the browser's. Returns
        /// null on failure; a real caller cancellation still propagates.
        /// </summary>
        public async Task<string> GetPageTextAsync(string url, CancellationToken ct, int timeoutMs = 15000)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                return await WithNavigableViewAsync(async view =>
                {
                    await view.NavigateAndWaitAsync(url, timeoutMs).ConfigureAwait(false);
                    return await view.GetPageTextAsync().ConfigureAwait(false);
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Offscreen page-text fetch failed for {url}");
                return null;
            }
        }

        /// <summary>
        /// Runs navigation work against an offscreen view; callable from any thread.
        /// Navigations on the shared leased view are serialized through a gate; per-call
        /// views run unguarded. An exception from the work marks the view faulted.
        /// </summary>
        public async Task<T> WithNavigableViewAsync<T>(Func<IWebView, Task<T>> work, CancellationToken ct)
        {
            var (view, owned) = AcquireView();
            if (!owned)
            {
                await _navGate.WaitAsync(ct).ConfigureAwait(false);
            }

            var faulted = false;
            try
            {
                return await work(view).ConfigureAwait(false);
            }
            catch
            {
                faulted = true;
                throw;
            }
            finally
            {
                // Discard/dispose a faulted view before releasing the gate so the next
                // waiter never acquires a view that is about to be disposed under it.
                ReleaseView(view, owned, faulted);

                if (!owned)
                {
                    _navGate.Release();
                }
            }
        }

    }
}
