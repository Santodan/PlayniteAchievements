using PlayniteAchievements.Models;
using System;
using System.Threading;

namespace PlayniteAchievements.Services.Refresh
{
    internal sealed class RefreshStateManager
    {
        private readonly object _lock = new object();
        private CancellationTokenSource _activeRunCts;
        private Guid? _activeOperationId;
        private RefreshModeType? _activeRefreshMode;
        private Guid? _activeSingleGameId;
        private bool _reservedExclusive;

        public bool IsRebuilding
        {
            get
            {
                lock (_lock)
                {
                    return _activeRunCts != null;
                }
            }
        }

        /// <summary>True while another operation holds off every refresh.</summary>
        public bool IsReservedExclusive
        {
            get
            {
                lock (_lock)
                {
                    return _reservedExclusive;
                }
            }
        }

        /// <summary>
        /// Holds off every refresh until <see cref="ReleaseExclusive"/>. False while a refresh runs
        /// or another reservation is held.
        /// </summary>
        public bool TryReserveExclusive()
        {
            lock (_lock)
            {
                if (_activeRunCts != null || _reservedExclusive)
                {
                    return false;
                }

                _reservedExclusive = true;
                return true;
            }
        }

        public void ReleaseExclusive()
        {
            lock (_lock)
            {
                _reservedExclusive = false;
            }
        }

        public (Guid? OperationId, RefreshModeType? Mode, Guid? SingleGameId) GetActiveRunContext()
        {
            lock (_lock)
            {
                return (_activeOperationId, _activeRefreshMode, _activeSingleGameId);
            }
        }

        public bool TryBeginRun(
            Guid operationId,
            RefreshModeType mode,
            Guid? singleGameId,
            CancellationToken externalToken,
            out CancellationTokenSource cts,
            out (Guid? OperationId, RefreshModeType? Mode, Guid? SingleGameId) activeContext)
        {
            lock (_lock)
            {
                if (_activeRunCts != null || _reservedExclusive)
                {
                    cts = null;
                    activeContext = (_activeOperationId, _activeRefreshMode, _activeSingleGameId);
                    return false;
                }

                _activeRunCts = externalToken != CancellationToken.None
                    ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                    : new CancellationTokenSource();

                _activeOperationId = operationId;
                _activeRefreshMode = mode;
                _activeSingleGameId = singleGameId;

                cts = _activeRunCts;
                activeContext = (_activeOperationId, _activeRefreshMode, _activeSingleGameId);

                return true;
            }
        }

        public void EndRun()
        {
            lock (_lock)
            {
                _activeRunCts?.Dispose();
                _activeRunCts = null;
                _activeOperationId = null;
                _activeRefreshMode = null;
                _activeSingleGameId = null;
            }
        }

        public void CancelCurrentRebuild()
        {
            lock (_lock)
            {
                _activeRunCts?.Cancel();
            }
        }
    }
}