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
        private readonly RefreshEntryPoint _refreshCoordinator;
        private readonly ICacheManager _cacheManager;
        private readonly ProviderRegistry _providerRegistry;
        private readonly NotificationPublisher _notifications;
        private readonly ILogger _logger;

        private readonly object _sync = new object();
        private CancellationTokenSource _pollingCts;
        private Task _pollingTask;
        private Guid? _activeGameId;

        public ActiveGameAchievementMonitor(
            RefreshEntryPoint refreshCoordinator,
            ICacheManager cacheManager,
            ProviderRegistry providerRegistry,
            NotificationPublisher notifications,
            ILogger logger)
        {
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _providerRegistry = providerRegistry ?? throw new ArgumentNullException(nameof(providerRegistry));
            _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
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

        private async Task RunAsync(Game game, CancellationToken cancellationToken)
        {
            var previousSnapshot = CaptureSnapshot(game.Id);

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
                    await RefreshLocalGameAsync(game.Id, cancellationToken).ConfigureAwait(false);
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

                var currentSnapshot = CaptureSnapshot(game.Id);
                var newlyUnlocked = FindNewlyUnlockedAchievements(previousSnapshot, currentSnapshot);
                if (previousSnapshot != null && newlyUnlocked.Count > 0)
                {
                    var localSettings = ProviderRegistry.Settings<LocalSettings>();
                    var soundPath = localSettings?.UnlockSoundPath;

                    _notifications.ShowLocalAchievementUnlocked(game.Name, newlyUnlocked, soundPath);
                }

                previousSnapshot = currentSnapshot ?? previousSnapshot;
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

        private async Task RefreshLocalGameAsync(Guid gameId, CancellationToken cancellationToken)
        {
            await _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Custom,
                    CustomOptions = new CustomRefreshOptions
                    {
                        Scope = CustomGameScope.Explicit,
                        ProviderKeys = new[] { "Local" },
                        IncludeGameIds = new[] { gameId },
                        RespectUserExclusions = false,
                        RunProvidersInParallelOverride = false
                    }
                },
                new RefreshExecutionPolicy
                {
                    SwallowExceptions = false,
                    ExternalCancellationToken = cancellationToken,
                    ErrorLogMessage = "Active Local achievement refresh failed."
                }).ConfigureAwait(false);
        }

        private AchievementSnapshot CaptureSnapshot(Guid gameId)
        {
            var cacheManager = _cacheManager as CacheManager;
            var data = cacheManager?.LoadGameData(gameId.ToString(), "Local");
            if (data == null)
            {
                return null;
            }

            var unlocked = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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

                unlocked[key] = string.IsNullOrWhiteSpace(achievement.DisplayName)
                    ? achievement.ApiName
                    : achievement.DisplayName;
            }

            return new AchievementSnapshot(data.UnlockedCount, unlocked);
        }

        private static List<string> FindNewlyUnlockedAchievements(AchievementSnapshot previous, AchievementSnapshot current)
        {
            if (previous == null || current == null)
            {
                return new List<string>();
            }

            var results = current.UnlockedAchievements
                .Where(pair => !previous.UnlockedAchievements.ContainsKey(pair.Key))
                .Select(pair => string.IsNullOrWhiteSpace(pair.Value) ? pair.Key : pair.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (results.Count == 0 && current.UnlockedCount > previous.UnlockedCount)
            {
                var fallbackCount = current.UnlockedCount - previous.UnlockedCount;
                for (var index = 0; index < fallbackCount; index++)
                {
                    results.Add(string.Empty);
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
            public AchievementSnapshot(int unlockedCount, IDictionary<string, string> unlockedAchievements)
            {
                UnlockedCount = unlockedCount;
                UnlockedAchievements = new Dictionary<string, string>(
                    unlockedAchievements ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            public int UnlockedCount { get; }

            public IDictionary<string, string> UnlockedAchievements { get; }
        }
    }
}