using PlayniteAchievements.Models;
using System;
using System.Threading;

namespace PlayniteAchievements.Services
{
    internal sealed class RefreshStateManager
    {
        private readonly object _lock = new object();
        private CancellationTokenSource _activeRunCts;
        private Guid? _activeOperationId;
        private RefreshModeType? _activeRefreshMode;
        private Guid? _activeSingleGameId;

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
                if (_activeRunCts != null)
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