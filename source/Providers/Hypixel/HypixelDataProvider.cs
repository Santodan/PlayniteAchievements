using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Overrides;
using PlayniteAchievements.Providers.Settings;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PlayniteAchievements.Providers.Hypixel
{
    /// <summary>
    /// Hypixel achievement provider backed by the public hypixel.net profile page. Hypixel
    /// achievements are account-wide across every Hypixel game, so the whole set is attached to
    /// the matched Hypixel entry in the Playnite library, one category per Hypixel game.
    /// Exempt from in-game polling: each refresh downloads the full profile page, and repeating
    /// that on a timer while the player is online would be a steady load on the site.
    /// </summary>
    internal sealed class HypixelDataProvider : DataProviderBase<HypixelSettings>, IDataProvider, IProviderOverride, IInGamePollingExempt, IDisposable
    {
        // Presence-only binding: forces a game to be treated as Hypixel (account-wide data).
        public ProviderOverrideDescriptor OverrideDescriptor { get; } = ProviderOverrideDescriptor.None();

        // Stable so repeated refreshes replace the entry rather than stacking.
        private const string PlayerNotFoundNotificationId = "PA_Hypixel_PlayerNotFound";

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _playniteApi;
        private readonly string _pluginUserDataPath;

        private readonly object _initLock = new object();
        private HypixelApiClient _apiClient;
        private HypixelCatalogCache _catalogCache;

        public HypixelDataProvider(ILogger logger, IPlayniteAPI playniteApi, string pluginUserDataPath)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _playniteApi = playniteApi;
            _pluginUserDataPath = pluginUserDataPath ?? string.Empty;
        }

        public string ProviderName => ResourceProvider.GetString("LOCPlayAch_Provider_Hypixel");
        public string ProviderKey => "Hypixel";
        public string ProviderIconKey => "ProviderIconHypixel";
        public string ProviderColorHex => "#F6B31E";
        public ISessionManager AuthSession => null;

        public PlayniteAchievements.Models.Friends.IFriendsProvider Friends => null;

        /// <summary>
        /// Hypixel is authenticated once a username is configured.
        /// </summary>
        public bool IsAuthenticated =>
            !string.IsNullOrWhiteSpace(ProviderRegistry.Settings<HypixelSettings>().Username);

        public bool IsCapable(Game game)
        {
            if (game == null || !IsAuthenticated)
            {
                return false;
            }

            // Manual override binding takes precedence.
            if (GameCustomDataLookup.TryGetProviderOverride(game.Id, out var providerOverride) &&
                string.Equals(providerOverride.ProviderKey, ProviderKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return HypixelParsing.IsHypixelTitle(game.Name);
        }

        public async Task<RebuildPayload> RefreshAsync(
            IReadOnlyList<Game> gamesToRefresh,
            Action<Game> onGameStarting,
            Func<Game, GameAchievementData, Task> onGameCompleted,
            CancellationToken cancel)
        {
            var username = ProviderRegistry.Settings<HypixelSettings>().Username?.Trim();
            if (string.IsNullOrEmpty(username))
            {
                _logger?.Warn("[Hypixel] No username is configured - cannot fetch achievements.");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }

            if (gamesToRefresh == null || gamesToRefresh.Count == 0)
            {
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            EnsureInitialized();

            HypixelProfile profile;
            try
            {
                profile = await _apiClient.FetchProfileAsync(username, cancel).ConfigureAwait(false);
            }
            catch (HypixelPlayerNotFoundException)
            {
                _logger?.Warn($"[Hypixel] hypixel.net has no player named '{username}'.");
                ShowNotification(PlayerNotFoundNotificationId, "LOCPlayAch_Settings_Hypixel_PlayerNotFound");
                return new RebuildPayload { Summary = new RebuildSummary(), AuthRequired = true };
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The site could not be asked, so nothing is known about the player. Reporting
                // this as a configuration problem would be wrong.
                _logger?.Warn(ex, $"[Hypixel] Profile page for '{username}' could not be fetched; skipping this run.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            ClearNotification(PlayerNotFoundNotificationId);

            // A page with no achievement rows is a changed layout or an interstitial, not a player
            // with no achievements; importing it would write every stored unlock as gone.
            if (profile.Panels.Sum(p => p.Entries.Count) == 0)
            {
                _logger?.Warn($"[Hypixel] Profile page for '{username}' listed no achievements; skipping this run.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            // Without the definitions every row would fall back to a name-based identity, and the
            // next run with them would re-key the whole set, orphaning overrides and notes.
            var catalog = await _catalogCache.GetCatalogAsync(_apiClient, cancel).ConfigureAwait(false);
            if (catalog == null)
            {
                _logger?.Warn("[Hypixel] Achievement definitions are unavailable; skipping this run.");
                return new RebuildPayload { Summary = new RebuildSummary() };
            }

            return await ProviderRefreshExecutor.RunProviderGamesAsync(
                gamesToRefresh,
                onGameStarting,
                (game, token) =>
                {
                    if (!IsCapable(game))
                    {
                        return Task.FromResult(ProviderRefreshExecutor.ProviderGameResult.Skipped());
                    }

                    var data = BuildGameData(game, profile, catalog);
                    return Task.FromResult(new ProviderRefreshExecutor.ProviderGameResult { Data = data });
                },
                onGameCompleted,
                isAuthRequiredException: _ => false,
                onGameError: (game, ex, consecutiveErrors) =>
                {
                    _logger?.Warn(ex, $"[Hypixel] Failed to build achievements for '{game?.Name}' after {consecutiveErrors} consecutive errors.");
                },
                delayBetweenGamesAsync: null,
                delayAfterErrorAsync: null,
                cancel).ConfigureAwait(false);
        }

        private GameAchievementData BuildGameData(Game game, HypixelProfile profile, HypixelAchievementsResponse catalog)
        {
            // Built per game so each gets its own row instances; several Playnite entries may be
            // bound to Hypixel.
            var achievements = HypixelAchievementMapper.BuildAchievements(profile, catalog);

            return new GameAchievementData
            {
                ProviderKey = ProviderKey,
                GameName = game?.Name,
                LibrarySourceName = game?.Source?.Name,
                LastUpdatedUtc = DateTime.UtcNow,
                HasAchievements = achievements.Count > 0,
                PlayniteGameId = game?.Id,
                Achievements = achievements
            };
        }

        /// <summary>
        /// Raises a Playnite notification under a stable id, so repeated refreshes of the same
        /// unresolved problem leave one entry rather than a stack of them.
        /// </summary>
        private void ShowNotification(string notificationId, string messageKey)
        {
            if (_playniteApi?.Notifications == null)
            {
                return;
            }

            try
            {
                _playniteApi.Notifications.Add(new NotificationMessage(
                    notificationId,
                    ResourceProvider.GetString(messageKey),
                    NotificationType.Error));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Hypixel] Failed to show notification {notificationId}.");
            }
        }

        private void ClearNotification(string notificationId)
        {
            if (_playniteApi?.Notifications == null)
            {
                return;
            }

            try
            {
                _playniteApi.Notifications.Remove(notificationId);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"[Hypixel] Failed to clear notification {notificationId}.");
            }
        }

        private void EnsureInitialized()
        {
            lock (_initLock)
            {
                if (_apiClient == null)
                {
                    _apiClient = new HypixelApiClient(_logger);
                }

                if (_catalogCache == null)
                {
                    _catalogCache = new HypixelCatalogCache(_logger, _pluginUserDataPath);
                }
            }
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
        }

        /// <inheritdoc />
        public ProviderSettingsViewBase CreateSettingsView() => new HypixelSettingsView();
    }
}
