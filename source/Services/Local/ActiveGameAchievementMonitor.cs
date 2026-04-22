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
using PlayniteAchievements.Providers.Local;

namespace PlayniteAchievements.Services.Local
{
    internal sealed class ActiveGameAchievementMonitor : IDisposable
    {
        private readonly ICacheManager _cacheManager;
        private readonly ProviderRegistry _providerRegistry;
        private readonly NotificationPublisher _notifications;
        private readonly LocalAchievementScreenshotService _screenshotService;
        private readonly Func<Guid, bool> _isRealtimeNotificationDisabled;
        private readonly ILogger _logger;

        private readonly object _sync = new object();
        private CancellationTokenSource _pollingCts;
        private Task _pollingTask;
        private Guid? _activeGameId;
        private AchievementSnapshot _lastKnownSnapshot;
        private Guid? _lastKnownGameId;
        private string _lastKnownGameName;

        public ActiveGameAchievementMonitor(
            ICacheManager cacheManager,
            ProviderRegistry providerRegistry,
            NotificationPublisher notifications,
            LocalAchievementScreenshotService screenshotService,
            Func<Guid, bool> isRealtimeNotificationDisabled,
            ILogger logger)
        {
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
            _screenshotService = screenshotService ?? throw new ArgumentNullException(nameof(screenshotService));
            _isRealtimeNotificationDisabled = isRealtimeNotificationDisabled;
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
                _lastKnownGameName = game.Name;
            }

            _logger?.Info($"Started active Local achievement monitor for '{game.Name}'.");
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

            try
            {
                cts?.Cancel();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to cancel active Local achievement monitor.");
            }

            cts?.Dispose();

            if (stoppedGameId.HasValue)
            {
                _logger?.Info($"Stopped active Local achievement monitor for game id '{stoppedGameId.Value}'.");
            }
        }

        public void Dispose()
        {
            Stop();
        }

        public async Task TryDetectMissedUnlocksAfterStopAsync(Game game, CancellationToken cancellationToken = default)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            AchievementSnapshot previousSnapshot;
            lock (_sync)
            {
                if (_lastKnownGameId != game.Id)
                {
                    return;
                }

                previousSnapshot = _lastKnownSnapshot;
            }

            if (previousSnapshot == null || !ShouldMonitor(game))
            {
                return;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                var currentSnapshot = await RefreshLocalGameAsync(game, cancellationToken).ConfigureAwait(false);
                var newlyUnlocked = FindNewlyUnlockedAchievements(previousSnapshot, currentSnapshot);
                if (newlyUnlocked.Count == 0)
                {
                    return;
                }

                var localSettings = ProviderRegistry.Settings<LocalSettings>();
                var soundPath = localSettings?.UnlockSoundPath;
                var unlockNames = newlyUnlocked.Select(item => item.DisplayName).ToList();
                var firstIconPath = newlyUnlocked
                    .Select(item => item.UnlockedIconPath)
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

                _logger?.Info($"[LocalMonitor] Detected {newlyUnlocked.Count} late Local unlock(s) for '{game.Name}' after game stop.");

                if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
                {
                    _logger?.Info($"[LocalMonitor] Skipped late Local unlock notification for '{game.Name}' because real-time notifications are disabled for this game.");
                }
                else
                {
                    _notifications.ShowLocalAchievementUnlocked(game.Name, unlockNames, soundPath, firstIconPath);
                }

                _ = _screenshotService.TryCaptureUnlockScreenshotsAsync(game, unlockNames, cancellationToken);

                lock (_sync)
                {
                    _lastKnownSnapshot = currentSnapshot;
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"[LocalMonitor] Final post-stop Local achievement recheck failed for '{game.Name}'.");
            }
        }

        private async Task RunAsync(Game game, CancellationToken cancellationToken)
        {
            AchievementSnapshot previousSnapshot = null;

            try
            {
                previousSnapshot = await RefreshLocalGameAsync(game, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _lastKnownSnapshot = previousSnapshot;
                    _lastKnownGameId = game.Id;
                    _lastKnownGameName = game.Name;
                }
                _logger?.Info(previousSnapshot != null
                    ? $"Initialized active Local achievement monitor baseline for '{game.Name}' with {previousSnapshot.UnlockedCount} unlocked achievements."
                    : $"Initialized active Local achievement monitor baseline for '{game.Name}' without cached Local achievements yet.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed to initialize active Local achievement monitor baseline for '{game.Name}'.");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(GetPollInterval(), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                if (!ShouldMonitor(game))
                {
                    _logger?.Info($"Stopping active Local achievement monitor for '{game.Name}' because the feature is disabled.");
                    break;
                }

                try
                {
                    var currentSnapshot = await RefreshLocalGameAsync(game, cancellationToken).ConfigureAwait(false);
                    if (previousSnapshot != null && currentSnapshot != null)
                    {
                        _logger?.Debug($"[LocalMonitor] Snapshot delta for '{game.Name}': previous={previousSnapshot.UnlockedCount}, current={currentSnapshot.UnlockedCount}, previousKeys={previousSnapshot.UnlockedAchievements.Count}, currentKeys={currentSnapshot.UnlockedAchievements.Count}");
                    }

                    var newlyUnlocked = FindNewlyUnlockedAchievements(previousSnapshot, currentSnapshot);
                    if (previousSnapshot != null && newlyUnlocked.Count > 0)
                    {
                        var localSettings = ProviderRegistry.Settings<LocalSettings>();
                        var soundPath = localSettings?.UnlockSoundPath;
                        var unlockNames = newlyUnlocked
                            .Select(item => item.DisplayName)
                            .ToList();
                        var firstIconPath = newlyUnlocked
                            .Select(item => item.UnlockedIconPath)
                            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

                        _logger?.Info($"Detected {newlyUnlocked.Count} newly unlocked Local achievement(s) for '{game.Name}'.");

                        _ = _screenshotService.TryCaptureUnlockScreenshotsAsync(game, unlockNames, cancellationToken);
                        if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
                        {
                            _logger?.Info($"Skipped Local unlock notification for '{game.Name}' because real-time notifications are disabled for this game.");
                        }
                        else
                        {
                            _notifications.ShowLocalAchievementUnlocked(game.Name, unlockNames, soundPath, firstIconPath);
                        }
                    }
                    else if (previousSnapshot == null && currentSnapshot != null)
                    {
                        _logger?.Info($"Active Local achievement monitor established a delayed baseline for '{game.Name}' with {currentSnapshot.UnlockedCount} unlocked achievements.");
                    }

                    previousSnapshot = currentSnapshot ?? previousSnapshot;
                    lock (_sync)
                    {
                        _lastKnownSnapshot = previousSnapshot;
                        _lastKnownGameId = game.Id;
                        _lastKnownGameName = game.Name;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Active Local achievement refresh failed for '{game.Name}'.");
                    continue;
                }
            }
        }

        private bool ShouldMonitor(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return false;
            }

            var localSettings = ProviderRegistry.Settings<LocalSettings>();
            return localSettings?.IsEnabled == true &&
                   localSettings.EnableActiveGameMonitoring &&
                   _providerRegistry.IsProviderEnabled("Local");
        }

        private TimeSpan GetPollInterval()
        {
            var localSettings = ProviderRegistry.Settings<LocalSettings>();
            var seconds = localSettings?.ActiveGameMonitoringIntervalSeconds ?? 5;
            seconds = Math.Max(LocalSettings.MinActiveGameMonitoringIntervalSeconds, Math.Min(LocalSettings.MaxActiveGameMonitoringIntervalSeconds, seconds));
            return TimeSpan.FromSeconds(seconds);
        }

        private async Task<AchievementSnapshot> RefreshLocalGameAsync(Game game, CancellationToken cancellationToken)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return null;
            }

            var cachedBefore = CaptureSnapshot(game.Id);
            var localProvider = _providerRegistry.GetProvider("Local") as LocalSavesProvider;
            if (localProvider == null)
            {
                _logger?.Warn("Active Local achievement monitor could not resolve the Local provider.");
                return cachedBefore;
            }

            var data = await localProvider.GetAchievementsAsync(game, null).ConfigureAwait(false);
            if (data == null)
            {
                return cachedBefore;
            }

            if (string.IsNullOrWhiteSpace(data.ProviderKey))
            {
                data.ProviderKey = "Local";
            }

            var writeResult = _cacheManager.SaveGameData(game.Id.ToString(), data);
            if (writeResult?.Success != true)
            {
                var errorMessage = writeResult?.ErrorMessage ?? "Unknown cache persistence failure.";
                throw new InvalidOperationException($"Active Local achievement monitor failed to persist cache for '{game.Name}': {errorMessage}");
            }

            var currentSnapshot = BuildSnapshot(data);
            if (!SnapshotsEqual(cachedBefore, currentSnapshot))
            {
                _cacheManager.NotifyCacheInvalidated();
            }

            return currentSnapshot;
        }

        private AchievementSnapshot CaptureSnapshot(Guid gameId)
        {
            var cacheManager = _cacheManager as CacheManager;
            var data = cacheManager?.LoadGameData(gameId.ToString(), "Local");
            return BuildSnapshot(data);
        }

        private AchievementSnapshot BuildSnapshot(GameAchievementData data)
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

                var key = BuildAchievementKey(achievement);
                if (string.IsNullOrWhiteSpace(key) || unlocked.ContainsKey(key))
                {
                    continue;
                }

                unlocked[key] = new UnlockedAchievementInfo(
                    string.IsNullOrWhiteSpace(achievement.DisplayName) ? achievement.ApiName : achievement.DisplayName,
                    achievement.UnlockedIconPath);
            }

            return new AchievementSnapshot(data.UnlockedCount, unlocked);
        }

        private static bool SnapshotsEqual(AchievementSnapshot left, AchievementSnapshot right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null)
            {
                return false;
            }

            if (left.UnlockedCount != right.UnlockedCount || left.UnlockedAchievements.Count != right.UnlockedAchievements.Count)
            {
                return false;
            }

            foreach (var achievementKey in left.UnlockedAchievements.Keys)
            {
                if (!right.UnlockedAchievements.ContainsKey(achievementKey))
                {
                    return false;
                }
            }

            return true;
        }

        private static List<UnlockedAchievementInfo> FindNewlyUnlockedAchievements(AchievementSnapshot previous, AchievementSnapshot current)
        {
            if (previous == null || current == null)
            {
                return new List<UnlockedAchievementInfo>();
            }

            var results = current.UnlockedAchievements
                .Where(pair => !previous.UnlockedAchievements.ContainsKey(pair.Key))
                .Select(pair => pair.Value ?? new UnlockedAchievementInfo(pair.Key, null))
                .GroupBy(item => item.DisplayName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            if (results.Count == 0 && current.UnlockedCount > previous.UnlockedCount)
            {
                var fallbackCount = current.UnlockedCount - previous.UnlockedCount;
                for (var index = 0; index < fallbackCount; index++)
                {
                    results.Add(new UnlockedAchievementInfo(string.Empty, null));
                }
            }

            return results;
        }

        private static string BuildAchievementKey(AchievementDetail achievement)
        {
            if (!string.IsNullOrWhiteSpace(achievement?.ApiName))
            {
                return achievement.ApiName.Trim();
            }

            if (!string.IsNullOrWhiteSpace(achievement?.DisplayName))
            {
                return achievement.DisplayName.Trim();
            }

            return null;
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
            public UnlockedAchievementInfo(string displayName, string unlockedIconPath)
            {
                DisplayName = displayName ?? string.Empty;
                UnlockedIconPath = unlockedIconPath ?? string.Empty;
            }

            public string DisplayName { get; }

            public string UnlockedIconPath { get; }
        }
    }
}