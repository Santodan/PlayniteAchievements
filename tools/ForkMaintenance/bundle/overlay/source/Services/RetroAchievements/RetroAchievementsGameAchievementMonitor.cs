using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.RetroAchievements;
using PlayniteAchievements.Services.Local;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Services.RetroAchievements
{
    /// <summary>
    /// Polls RetroAchievements while a game is running and emits unlock notifications
    /// when the user's API progress changes.
    /// </summary>
    internal sealed class RetroAchievementsGameAchievementMonitor : IDisposable
    {
        private readonly ICacheManager _cacheManager;
        private readonly ProviderRegistry _providerRegistry;
        private readonly NotificationPublisher _notifications;
        private readonly LocalAchievementScreenshotService _screenshotService;
        private readonly Func<Guid, bool> _isRealtimeNotificationDisabled;
        private readonly Func<Guid, bool> _isExcludedFromRefreshes;
        private readonly Func<Game, CancellationToken, Task> _refreshGameInExtensionAsync;
        private readonly ILogger _logger;

        private readonly object _sync = new object();
        private CancellationTokenSource _pollingCts;
        private Task _pollingTask;
        private Guid? _activeGameId;
        private AchievementSnapshot _lastKnownSnapshot;
        private Guid? _lastKnownGameId;

        public RetroAchievementsGameAchievementMonitor(
            ICacheManager cacheManager,
            ProviderRegistry providerRegistry,
            NotificationPublisher notifications,
            LocalAchievementScreenshotService screenshotService,
            Func<Guid, bool> isRealtimeNotificationDisabled,
            Func<Guid, bool> isExcludedFromRefreshes,
            Func<Game, CancellationToken, Task> refreshGameInExtensionAsync,
            ILogger logger)
        {
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _screenshotService = screenshotService ?? throw new ArgumentNullException(nameof(screenshotService));
            _isRealtimeNotificationDisabled = isRealtimeNotificationDisabled;
            _isExcludedFromRefreshes = isExcludedFromRefreshes;
            _refreshGameInExtensionAsync = refreshGameInExtensionAsync;
            _logger = logger;
        }

        public void Start(Game game)
        {
            Stop();

            if (!ShouldMonitor(game))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            var task = RunAsync(game, cts.Token);

            lock (_sync)
            {
                _activeGameId = game.Id;
                _pollingCts = cts;
                _pollingTask = task;
                _lastKnownGameId = game.Id;
            }

            _logger?.Info($"[RAMonitor] Started active RetroAchievements monitor for '{game.Name}'.");
        }

        public void Stop()
        {
            CancellationTokenSource cts = null;
            Guid? stoppedGameId = null;

            lock (_sync)
            {
                cts = _pollingCts;
                stoppedGameId = _activeGameId;
                _pollingCts = null;
                _pollingTask = null;
                _activeGameId = null;
            }

            try { cts?.Cancel(); }
            catch (Exception ex) { _logger?.Debug(ex, "[RAMonitor] Cancel error."); }

            cts?.Dispose();

            if (stoppedGameId.HasValue)
            {
                _logger?.Info($"[RAMonitor] Stopped RetroAchievements monitor for game id '{stoppedGameId.Value}'.");
            }
        }

        public void Dispose() => Stop();

        private async Task RunAsync(Game game, CancellationToken cancellationToken)
        {
            AchievementSnapshot previousSnapshot = null;

            try
            {
                previousSnapshot = await RefreshRetroAchievementsGameAsync(game, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _lastKnownSnapshot = previousSnapshot;
                    _lastKnownGameId = game.Id;
                }

                _logger?.Info(previousSnapshot != null
                    ? $"[RAMonitor] Baseline for '{game.Name}': {previousSnapshot.UnlockedCount} unlocked."
                    : $"[RAMonitor] No RetroAchievements data baseline for '{game.Name}'.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAMonitor] Failed to establish baseline for '{game.Name}'.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(GetPollInterval(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }

                if (!ShouldMonitor(game))
                {
                    _logger?.Info($"[RAMonitor] Stopping monitor for '{game.Name}' because the feature is disabled.");
                    break;
                }

                try
                {
                    var currentSnapshot = await RefreshRetroAchievementsGameAsync(game, cancellationToken).ConfigureAwait(false);
                    var newlyUnlocked = FindNewlyUnlocked(previousSnapshot, currentSnapshot);

                    if (previousSnapshot != null && newlyUnlocked.Count > 0)
                    {
                        if (ProviderRegistry.Settings<Providers.Local.LocalSettings>()?.RefreshAchievementsOnRealtimeUnlock == true)
                        {
                            currentSnapshot = await TryRefreshGameInExtensionAsync(game, currentSnapshot, cancellationToken)
                                .ConfigureAwait(false);
                            newlyUnlocked = FindNewlyUnlocked(previousSnapshot, currentSnapshot);
                            if (newlyUnlocked.Count == 0)
                            {
                                previousSnapshot = currentSnapshot ?? previousSnapshot;
                                lock (_sync)
                                {
                                    _lastKnownSnapshot = previousSnapshot;
                                    _lastKnownGameId = game.Id;
                                }

                                continue;
                            }
                        }

                        var unlockNotifications = newlyUnlocked
                            .Select(i => new AchievementUnlockNotificationItem(
                                i.DisplayName,
                                i.UnlockedIconPath,
                                i.Description,
                                i.Points,
                                i.Rarity,
                                i.Trophy))
                            .ToList();

                        _logger?.Info($"[RAMonitor] {newlyUnlocked.Count} new RetroAchievements unlock(s) for '{game.Name}'.");

                        if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
                        {
                            _logger?.Info($"[RAMonitor] Skipped notification for '{game.Name}' because real-time notifications are disabled for this game.");
                        }
                        else
                        {
                            var localSettings = ProviderRegistry.Settings<Providers.Local.LocalSettings>();
                            _notifications.ShowLocalAchievementUnlocked(
                                game.Name,
                                unlockNotifications,
                                localSettings?.UnlockSoundPath,
                                game: game,
                                notificationProviderKey: "RetroAchievements");
                            var unlockNames = newlyUnlocked.Select(i => i.DisplayName).ToList();
                            _ = _screenshotService.TryCaptureUnlockScreenshotsAsync(game, unlockNames, cancellationToken);
                        }
                    }
                    else if (previousSnapshot == null && currentSnapshot != null)
                    {
                        _logger?.Info($"[RAMonitor] Late baseline established for '{game.Name}': {currentSnapshot.UnlockedCount} unlocked.");
                    }

                    previousSnapshot = currentSnapshot ?? previousSnapshot;
                    lock (_sync)
                    {
                        _lastKnownSnapshot = previousSnapshot;
                        _lastKnownGameId = game.Id;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[RAMonitor] Refresh failed for '{game.Name}'.");
                }
            }
        }

        private bool ShouldMonitor(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            var settings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            if (settings?.IsEnabled != true || !settings.EnableActiveMonitoring)
            {
                return false;
            }

            if (!_providerRegistry.IsProviderEnabled("RetroAchievements"))
            {
                return false;
            }

            if (_isExcludedFromRefreshes?.Invoke(game.Id) == true)
            {
                _logger?.Info($"[RAMonitor] Skipping monitor for '{game.Name}' because the game is excluded from refreshes.");
                return false;
            }

            if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
            {
                _logger?.Info($"[RAMonitor] Skipping monitor for '{game.Name}' because real-time notifications are disabled for this game.");
                return false;
            }

            var provider = _providerRegistry.GetProvider("RetroAchievements");
            return provider?.IsAuthenticated == true && provider.IsCapable(game);
        }

        private TimeSpan GetPollInterval()
        {
            var settings = ProviderRegistry.Settings<RetroAchievementsSettings>();
            var seconds = settings?.MonitoringIntervalSeconds ?? 300;
            seconds = Math.Max(30, Math.Min(3600, seconds));
            return TimeSpan.FromSeconds(seconds);
        }

        private async Task<AchievementSnapshot> RefreshRetroAchievementsGameAsync(Game game, CancellationToken cancellationToken)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return null;
            }

            var provider = _providerRegistry.GetProvider("RetroAchievements");
            if (provider == null)
            {
                _logger?.Warn("[RAMonitor] Could not resolve RetroAchievements provider.");
                return CaptureSnapshot(game.Id);
            }

            GameAchievementData fetchedData = null;

            await provider.RefreshAsync(
                new[] { game },
                _ => { },
                (g, d) => { fetchedData = d; return Task.CompletedTask; },
                cancellationToken).ConfigureAwait(false);

            if (fetchedData == null)
            {
                return CaptureSnapshot(game.Id);
            }

            if (string.IsNullOrWhiteSpace(fetchedData.ProviderKey))
            {
                fetchedData.ProviderKey = "RetroAchievements";
            }

            var writeResult = _cacheManager.SaveGameData(game.Id.ToString(), fetchedData);
            if (writeResult?.Success != true)
            {
                _logger?.Warn($"[RAMonitor] Cache write failed for '{game.Name}': {writeResult?.ErrorMessage ?? "unknown error"}");
                return CaptureSnapshot(game.Id);
            }

            _cacheManager.NotifyCacheInvalidated();
            return BuildSnapshot(fetchedData);
        }

        private async Task<AchievementSnapshot> TryRefreshGameInExtensionAsync(
            Game game,
            AchievementSnapshot fallbackSnapshot,
            CancellationToken cancellationToken)
        {
            if (_refreshGameInExtensionAsync == null || game == null || game.Id == Guid.Empty)
            {
                return fallbackSnapshot;
            }

            try
            {
                _logger?.Info($"[RAMonitor] Refreshing extension data for '{game.Name}' before showing real-time unlock notification.");
                await _refreshGameInExtensionAsync(game, cancellationToken).ConfigureAwait(false);
                var refreshedSnapshot = CaptureSnapshot(game.Id);
                return refreshedSnapshot ?? fallbackSnapshot;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[RAMonitor] Extension refresh after real-time unlock failed for '{game.Name}'. Showing notification with monitor data.");
                return fallbackSnapshot;
            }
        }
        private AchievementSnapshot CaptureSnapshot(Guid gameId)
        {
            var cacheManager = _cacheManager as CacheManager;
            var data = cacheManager?.LoadGameData(gameId.ToString());
            return BuildSnapshot(data);
        }

        private static AchievementSnapshot BuildSnapshot(GameAchievementData data)
        {
            if (data == null)
            {
                return null;
            }

            var unlocked = new Dictionary<string, UnlockedAchievementInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var achievement in data.Achievements ?? Enumerable.Empty<AchievementDetail>())
            {
                if (!achievement.Unlocked)
                {
                    continue;
                }

                var key = !string.IsNullOrWhiteSpace(achievement.ApiName) ? achievement.ApiName.Trim()
                    : !string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.DisplayName.Trim()
                    : null;
                if (string.IsNullOrWhiteSpace(key) || unlocked.ContainsKey(key))
                {
                    continue;
                }

                unlocked[key] = new UnlockedAchievementInfo(
                    string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName,
                    achievement.UnlockedIconPath,
                    achievement.Description,
                    achievement.Points,
                    achievement.RarityDetailText,
                    achievement.TrophyType);
            }

            return new AchievementSnapshot(data.UnlockedCount, unlocked);
        }

        private static List<UnlockedAchievementInfo> FindNewlyUnlocked(
            AchievementSnapshot previous, AchievementSnapshot current)
        {
            if (previous == null || current == null)
            {
                return new List<UnlockedAchievementInfo>();
            }

            return current.UnlockedAchievements
                .Where(kvp => !previous.UnlockedAchievements.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();
        }

        private sealed class AchievementSnapshot
        {
            public AchievementSnapshot(int unlockedCount, IDictionary<string, UnlockedAchievementInfo> unlockedAchievements)
            {
                UnlockedCount = unlockedCount;
                UnlockedAchievements = new Dictionary<string, UnlockedAchievementInfo>(
                    unlockedAchievements ?? new Dictionary<string, UnlockedAchievementInfo>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            public int UnlockedCount { get; }
            public IDictionary<string, UnlockedAchievementInfo> UnlockedAchievements { get; }
        }

        private sealed class UnlockedAchievementInfo
        {
            public UnlockedAchievementInfo(string displayName, string unlockedIconPath, string description = null, int? points = null, string rarity = null, string trophy = null)
            {
                DisplayName = displayName ?? string.Empty;
                UnlockedIconPath = unlockedIconPath ?? string.Empty;
                Description = description ?? string.Empty;
                Points = points;
                Rarity = rarity ?? string.Empty;
                Trophy = trophy ?? string.Empty;
            }

            public string DisplayName { get; }
            public string UnlockedIconPath { get; }
            public string Description { get; }
            public int? Points { get; }
            public string Rarity { get; }
            public string Trophy { get; }
        }
    }
}
