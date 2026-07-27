using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Cache;

namespace PlayniteAchievements.Services.Local
{
    internal sealed class ActiveGameAchievementMonitor : IDisposable
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
        private string _lastKnownGameName;
        private string _lastKnownSourceFingerprint;

        public ActiveGameAchievementMonitor(
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
                var unlockNotifications = newlyUnlocked
                    .Select(item => new AchievementUnlockNotificationItem(
                        item.DisplayName,
                        item.UnlockedIconPath,
                        item.Description,
                        item.Points,
                        item.Rarity,
                        item.Trophy))
                    .ToList();

                _logger?.Info($"[LocalMonitor] Detected {newlyUnlocked.Count} late Local unlock(s) for '{game.Name}' after game stop.");

                if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
                {
                    _logger?.Info($"[LocalMonitor] Skipped late Local unlock notification for '{game.Name}' because real-time notifications are disabled for this game.");
                }
                else
                {
                    _notifications.ShowLocalAchievementUnlocked(
                        game.Name,
                        unlockNotifications,
                        soundPath,
                        game: game);
                    _ = _screenshotService.TryCaptureUnlockScreenshotsAsync(game, unlockNames, cancellationToken);
                }
                QueueRefreshGameInExtensionAfterUnlock(game);

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
                    _lastKnownSourceFingerprint = TryGetLocalSourceFingerprint(game, out var baselineFingerprint)
                        ? baselineFingerprint
                        : null;
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
                    if (TryGetLocalSourceFingerprint(game, out var sourceFingerprint) &&
                        string.Equals(sourceFingerprint, _lastKnownSourceFingerprint, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var currentSnapshot = await RefreshLocalGameAsync(game, cancellationToken).ConfigureAwait(false);
                    if (TryGetLocalSourceFingerprint(game, out sourceFingerprint))
                    {
                        _lastKnownSourceFingerprint = sourceFingerprint;
                    }

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
                        var unlockNotifications = newlyUnlocked
                            .Select(item => new AchievementUnlockNotificationItem(
                                item.DisplayName,
                                item.UnlockedIconPath,
                                item.Description,
                                item.Points,
                                item.Rarity,
                                item.Trophy))
                            .ToList();

                        _logger?.Info($"Detected {newlyUnlocked.Count} newly unlocked Local achievement(s) for '{game.Name}'.");

                        if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
                        {
                            _logger?.Info($"Skipped Local unlock notification for '{game.Name}' because real-time notifications are disabled for this game.");
                        }
                        else
                        {
                            _notifications.ShowLocalAchievementUnlocked(
                                game.Name,
                                unlockNotifications,
                                soundPath,
                                game: game);
                            _ = _screenshotService.TryCaptureUnlockScreenshotsAsync(game, unlockNames, cancellationToken);
                        }

                        QueueRefreshGameInExtensionAfterUnlock(game);
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
                        _lastKnownSourceFingerprint = sourceFingerprint;
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
            if (localSettings?.IsEnabled != true ||
                !localSettings.EnableActiveGameMonitoring ||
                !_providerRegistry.IsProviderEnabled("Local"))
            {
                return false;
            }

            if (_isExcludedFromRefreshes?.Invoke(game.Id) == true)
            {
                _logger?.Info($"Skipping active Local achievement monitor for '{game.Name}' because the game is excluded from refreshes.");
                return false;
            }

            if (_isRealtimeNotificationDisabled?.Invoke(game.Id) == true)
            {
                _logger?.Info($"Skipping active Local achievement monitor for '{game.Name}' because real-time notifications are disabled for this game.");
                return false;
            }

            return true;
        }

        private TimeSpan GetPollInterval()
        {
            var localSettings = ProviderRegistry.Settings<LocalSettings>();
            var seconds = localSettings?.ActiveGameMonitoringIntervalSeconds ?? 5;
            seconds = Math.Max(LocalSettings.MinActiveGameMonitoringIntervalSeconds, Math.Min(LocalSettings.MaxActiveGameMonitoringIntervalSeconds, seconds));
            return TimeSpan.FromSeconds(seconds);
        }

        private bool TryGetLocalSourceFingerprint(Game game, out string fingerprint)
        {
            fingerprint = null;
            try
            {
                var localProvider = _providerRegistry.GetProvider("Local") as LocalSavesProvider;
                return localProvider?.TryGetActiveMonitorSourceFingerprint(game, out fingerprint) == true &&
                       !string.IsNullOrWhiteSpace(fingerprint);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed to get active Local monitor source fingerprint for '{game?.Name}'.");
                fingerprint = null;
                return false;
            }
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

            GameAchievementData data;
            using (localProvider.BeginRealtimeLogThrottle())
            {
                data = await localProvider.GetAchievementsAsync(game, null).ConfigureAwait(false);
            }

            if (data == null)
            {
                return cachedBefore;
            }

            if (string.IsNullOrWhiteSpace(data.ProviderKey))
            {
                data.ProviderKey = "Local";
            }

            RestoreCachedAchievementIconPaths(data, cachedBefore);

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

        private void QueueRefreshGameInExtensionAfterUnlock(Game game)
        {
            if (_refreshGameInExtensionAsync == null ||
                game == null ||
                game.Id == Guid.Empty ||
                ProviderRegistry.Settings<LocalSettings>()?.RefreshAchievementsOnRealtimeUnlock != true)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    _logger?.Info($"[LocalMonitor] Refreshing extension data for '{game.Name}' after showing real-time unlock notification.");
                    await _refreshGameInExtensionAsync(game, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"[LocalMonitor] Extension refresh after real-time unlock failed for '{game.Name}'.");
                }
            });
        }

        private AchievementSnapshot CaptureSnapshot(Guid gameId)
        {
            var cacheManager = _cacheManager as CacheManager;
            var data = cacheManager?.LoadGameData(gameId.ToString());
            return BuildSnapshot(data);
        }

        private static void RestoreCachedAchievementIconPaths(
            GameAchievementData currentData,
            AchievementSnapshot cachedSnapshot)
        {
            if (currentData?.Achievements == null || cachedSnapshot?.Achievements == null)
            {
                return;
            }

            foreach (var achievement in currentData.Achievements)
            {
                var key = BuildAchievementKey(achievement);
                if (string.IsNullOrWhiteSpace(key) ||
                    !cachedSnapshot.Achievements.TryGetValue(key, out var cachedAchievement))
                {
                    continue;
                }

                if (IsExistingLocalFile(cachedAchievement.UnlockedIconPath))
                {
                    achievement.UnlockedIconPath = cachedAchievement.UnlockedIconPath;
                }

                if (IsExistingLocalFile(cachedAchievement.LockedIconPath))
                {
                    achievement.LockedIconPath = cachedAchievement.LockedIconPath;
                }
            }
        }

        private static bool IsExistingLocalFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var normalized = AchievementIconResolver.NormalizeIconPath(path);
            if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri) &&
                uri.IsFile)
            {
                normalized = uri.LocalPath;
            }

            return Path.IsPathRooted(normalized) && File.Exists(normalized);
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
                    achievement.UnlockedIconPath,
                    achievement.Description,
                    achievement.Points,
                    achievement.RarityDetailText,
                    achievement.TrophyType);
            }

            var achievements = (data.Achievements ?? Enumerable.Empty<AchievementDetail>())
                .Where(achievement => !string.IsNullOrWhiteSpace(BuildAchievementKey(achievement)))
                .GroupBy(BuildAchievementKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new CachedAchievementIconInfo(
                        group.First().UnlockedIconPath,
                        group.First().LockedIconPath),
                    StringComparer.OrdinalIgnoreCase);

            return new AchievementSnapshot(data.UnlockedCount, unlocked, achievements);
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
            public AchievementSnapshot(
                int unlockedCount,
                IDictionary<string, UnlockedAchievementInfo> unlockedAchievements,
                IDictionary<string, CachedAchievementIconInfo> achievements)
            {
                UnlockedCount = unlockedCount;
                UnlockedAchievements = new Dictionary<string, UnlockedAchievementInfo>(
                    unlockedAchievements ?? new Dictionary<string, UnlockedAchievementInfo>(),
                    StringComparer.OrdinalIgnoreCase);
                Achievements = new Dictionary<string, CachedAchievementIconInfo>(
                    achievements ?? new Dictionary<string, CachedAchievementIconInfo>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            public int UnlockedCount { get; }

            public IDictionary<string, UnlockedAchievementInfo> UnlockedAchievements { get; }

            public IDictionary<string, CachedAchievementIconInfo> Achievements { get; }
        }

        private sealed class CachedAchievementIconInfo
        {
            public CachedAchievementIconInfo(string unlockedIconPath, string lockedIconPath)
            {
                UnlockedIconPath = unlockedIconPath;
                LockedIconPath = lockedIconPath;
            }

            public string UnlockedIconPath { get; }

            public string LockedIconPath { get; }
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
