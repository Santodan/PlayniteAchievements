using System;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using PlayniteAchievements.Models;

namespace PlayniteAchievements.Services
{
    public class BackgroundUpdater
    {
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly RefreshRuntime _refreshService;
        private readonly ICacheManager _cacheManager;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private readonly NotificationPublisher _notifications;
        private readonly Action _onUpdateCompleted;

        private readonly object _ctsLock = new object();
        private CancellationTokenSource _cts;

        public BackgroundUpdater(
            RefreshEntryPoint refreshEntryPoint,
            RefreshRuntime refreshRuntime,
            ICacheManager cacheManager,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            NotificationPublisher notifications,
            Action onUpdateCompleted)
        {
            _refreshCoordinator = refreshEntryPoint;
            _refreshService = refreshRuntime;
            _cacheManager = cacheManager;
            _settings = settings;
            _logger = logger;
            _notifications = notifications;
            _onUpdateCompleted = onUpdateCompleted;
        }

        public void Start()
        {
            lock (_ctsLock)
            {
                if (_cts != null)
                {
                    return;
                }

                _cts = new CancellationTokenSource();
            }

            var token = _cts.Token;

            var interval = TimeSpan.FromHours(Math.Max(1, _settings.Persisted.PeriodicUpdateHours));

            // Run an initial check immediately on startup, then continue with the normal loop.
            Task.Run(async () =>
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var msg = ResourceProvider.GetString("LOCPlayAch_Error_Periodic_InitialCheckFailed");
                    _logger.Error(ex, msg);
                }

                await PeriodicUpdateLoop(interval, token).ConfigureAwait(false);
            }, token);
        }

        public void Stop()
        {
            CancellationTokenSource ctsToDispose = null;

            lock (_ctsLock)
            {
                if (_cts == null)
                {
                    return;
                }

                ctsToDispose = _cts;
                _cts = null;
            }

            try
            {
                ctsToDispose?.Cancel();
                _refreshService?.CancelCurrentRebuild();
            }
            catch (Exception ex)
            {
                // Log but ignore shutdown errors to ensure cleanup completes
                _logger?.Debug(ex, "[PeriodicUpdate] Error during background update service shutdown.");
            }
            finally
            {
                ctsToDispose?.Dispose();
            }
        }

        private async Task PeriodicUpdateLoop(TimeSpan interval, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await PerformUpdateIfNeeded(interval, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var msg = ResourceProvider.GetString("LOCPlayAch_Error_Periodic_UpdateFailed");
                    _logger.Error(ex, msg);
                }

                await DelayNextUpdate(interval, token).ConfigureAwait(false);
            }
        }

        private async Task PerformUpdateIfNeeded(TimeSpan interval, CancellationToken token)
        {
            if (ShouldPerformUpdate(interval))
            {
                await ExecuteUpdate(token).ConfigureAwait(false);
            }
            else
            {
                _logger.Debug("[PeriodicUpdate] Cache is recent; skipping update.");
            }
        }

        private bool ShouldPerformUpdate(TimeSpan interval)
        {
            if (!_settings.Persisted.EnablePeriodicUpdates)
            {
                return false;
            }

            // Skip if landing page should be shown (user hasn't completed setup)
            var firstTimeCompleted = _settings.Persisted.FirstTimeSetupCompleted;
            var seenThemeMigration = _settings.Persisted.SeenThemeMigration;
            var achievementDataService = PlayniteAchievementsPlugin.Instance?.AchievementDataService;
            var hasCachedData = achievementDataService?.HasCachedGameData() == true;
            bool showLandingPage = !seenThemeMigration || !firstTimeCompleted || !hasCachedData;

            if (showLandingPage)
            {
                _logger.Debug("[PeriodicUpdate] Skipping update - landing page should be shown.");
                return false;
            }

            var cache = _cacheManager;
            if (cache == null || !cache.IsCacheValid())
            {
                _logger.Debug("[PeriodicUpdate] No valid cache; update needed.");
                return true;
            }

            var lastUpdate = cache.GetMostRecentLastUpdatedUtc();
            if (!lastUpdate.HasValue)
            {
                _logger.Debug("[PeriodicUpdate] No last update time found; update needed.");
                return true;
            }

            var age = DateTime.UtcNow - lastUpdate.Value;
            var needsUpdate = age >= interval;
            _logger.Debug($"[PeriodicUpdate] Last refresh was {age.TotalHours:F1}h ago, interval={interval.TotalHours:F1}h, needsUpdate={needsUpdate}");

            return needsUpdate;
        }

        private async Task ExecuteUpdate(CancellationToken token)
        {
            _logger.Debug("[PeriodicUpdate] Triggering cache update...");

            try
            {
                var request = new RefreshRequest { Mode = RefreshModeType.Recent };
                var policy = new RefreshExecutionPolicy
                {
                    ValidateAuthentication = true,
                    UseProgressWindow = false,
                    SwallowExceptions = true,
                    ErrorLogMessage = ResourceProvider.GetString("LOCPlayAch_Error_Periodic_UpdateFailed")
                };

                await _refreshCoordinator.ExecuteAsync(request, policy).ConfigureAwait(false);

                _logger.Debug("[PeriodicUpdate] Cache update completed.");
                HandleUpdateCompletion();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Graceful shutdown
            }
        }

        private void HandleUpdateCompletion()
        {
            var lastStatus = _refreshService.GetLastRebuildStatus() ?? ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
            _notifications?.ShowPeriodicStatus(lastStatus);

            var failedKeys = _refreshService.GetLastFailedAuthProviderKeys();
            if (failedKeys != null && failedKeys.Count > 0)
            {
                _notifications?.ShowProviderAuthFailed(failedKeys);
            }
            else
            {
                _notifications?.ClearAllProviderAuthNotifications();
            }

            _onUpdateCompleted?.Invoke();
        }

        private async Task DelayNextUpdate(TimeSpan interval, CancellationToken token)
        {
            try
            {
                await Task.Delay(interval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Loop will terminate
            }
        }
    }
}




