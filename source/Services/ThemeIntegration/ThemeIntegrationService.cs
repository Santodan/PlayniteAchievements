using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Models.ThemeIntegration;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Services;
using Playnite.SDK;
using Playnite.SDK.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.Services.ThemeIntegration
{
    /// <summary>
    /// Service for theme integration, handling both per-game and all-games
    /// achievement data for desktop and fullscreen themes. Manages snapshot
    /// building, refresh commands, and theme surface updates.
    /// </summary>
    public sealed class ThemeIntegrationService : IDisposable
    {
        private static readonly List<AchievementDetail> EmptyAchievementList = new List<AchievementDetail>();
        private static readonly AchievementRarityStats EmptyRarityStats = new AchievementRarityStats();
        private static readonly string[] CompatibilityAllGamesDelegatedProperties =
        {
            nameof(PlayniteAchievementsSettings.AllGamesWithAchievements),
            nameof(PlayniteAchievementsSettings.PlatinumGames),
            nameof(PlayniteAchievementsSettings.PlatinumGamesAscending),
            nameof(PlayniteAchievementsSettings.GSTotal),
            nameof(PlayniteAchievementsSettings.GSPlat),
            nameof(PlayniteAchievementsSettings.GS90),
            nameof(PlayniteAchievementsSettings.GS30),
            nameof(PlayniteAchievementsSettings.GS15),
            nameof(PlayniteAchievementsSettings.GSScore),
            nameof(PlayniteAchievementsSettings.GSLevel),
            nameof(PlayniteAchievementsSettings.GSLevelProgress),
            nameof(PlayniteAchievementsSettings.GSRank)
        };

        // NOTE: PlatinumGames is notified via compatibility surface only to avoid duplicate notifications.
        private static readonly string[] NativeAllGamesCoreDelegatedProperties =
        {
            nameof(PlayniteAchievementsSettings.HasDataAllGames),
            nameof(PlayniteAchievementsSettings.CompletedGamesAsc),
            nameof(PlayniteAchievementsSettings.CompletedGamesDesc),
            nameof(PlayniteAchievementsSettings.GameSummariesAsc),
            nameof(PlayniteAchievementsSettings.GameSummariesDesc),
            nameof(PlayniteAchievementsSettings.GamesWithAchievements),
            nameof(PlayniteAchievementsSettings.TotalTrophies),
            nameof(PlayniteAchievementsSettings.PlatinumTrophies),
            nameof(PlayniteAchievementsSettings.GoldTrophies),
            nameof(PlayniteAchievementsSettings.SilverTrophies),
            nameof(PlayniteAchievementsSettings.BronzeTrophies),
            nameof(PlayniteAchievementsSettings.TotalCommon),
            nameof(PlayniteAchievementsSettings.TotalUncommon),
            nameof(PlayniteAchievementsSettings.TotalRare),
            nameof(PlayniteAchievementsSettings.TotalUltraRare),
            nameof(PlayniteAchievementsSettings.TotalRareAndUltraRare),
            nameof(PlayniteAchievementsSettings.TotalOverall),
            nameof(PlayniteAchievementsSettings.Level),
            nameof(PlayniteAchievementsSettings.LevelProgress),
            nameof(PlayniteAchievementsSettings.Rank),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop3),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop5),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocksTop10),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop3),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop5),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocksTop10),
            nameof(PlayniteAchievementsSettings.SteamGames),
            nameof(PlayniteAchievementsSettings.GOGGames),
            nameof(PlayniteAchievementsSettings.EpicGames),
            nameof(PlayniteAchievementsSettings.XboxGames),
            nameof(PlayniteAchievementsSettings.PSNGames),
            nameof(PlayniteAchievementsSettings.RetroAchievementsGames),
            nameof(PlayniteAchievementsSettings.RPCS3Games),
            nameof(PlayniteAchievementsSettings.ShadPS4Games),
            nameof(PlayniteAchievementsSettings.ManualGames)
        };

        private static readonly string[] NativeAllGamesHeavyDelegatedProperties =
        {
            nameof(PlayniteAchievementsSettings.AllAchievementsUnlockAsc),
            nameof(PlayniteAchievementsSettings.AllAchievementsUnlockDesc),
            nameof(PlayniteAchievementsSettings.AllAchievementsRarityAsc),
            nameof(PlayniteAchievementsSettings.AllAchievementsRarityDesc),
            nameof(PlayniteAchievementsSettings.MostRecentUnlocks),
            nameof(PlayniteAchievementsSettings.RarestRecentUnlocks)
        };

        private static readonly string[] SingleGameThemeDelegatedProperties =
        {
            nameof(PlayniteAchievementsSettings.HasData),
            nameof(PlayniteAchievementsSettings.HasAchievements),
            nameof(PlayniteAchievementsSettings.AchievementCount),
            nameof(PlayniteAchievementsSettings.UnlockedCount),
            nameof(PlayniteAchievementsSettings.LockedCount),
            nameof(PlayniteAchievementsSettings.ProgressPercentage),
            nameof(PlayniteAchievementsSettings.IsCompleted)
        };

        private static readonly string[] SingleGameLegacyDelegatedProperties =
        {
            nameof(PlayniteAchievementsSettings.HasDataLegacy),
            nameof(PlayniteAchievementsSettings.Total),
            nameof(PlayniteAchievementsSettings.Unlocked),
            nameof(PlayniteAchievementsSettings.Percent),
            nameof(PlayniteAchievementsSettings.Is100Percent),
            nameof(PlayniteAchievementsSettings.Locked),
            nameof(PlayniteAchievementsSettings.Common),
            nameof(PlayniteAchievementsSettings.Uncommon),
            nameof(PlayniteAchievementsSettings.Rare),
            nameof(PlayniteAchievementsSettings.UltraRare),
            nameof(PlayniteAchievementsSettings.RareAndUltraRare),
            nameof(PlayniteAchievementsSettings.ListAchievements),
            nameof(PlayniteAchievementsSettings.ListAchUnlockDateAsc),
            nameof(PlayniteAchievementsSettings.ListAchUnlockDateDesc)
        };

        private readonly ILogger _logger;
        private readonly IPlayniteAPI _api;
        private readonly AchievementService _achievementService;
        private readonly RefreshCoordinator _refreshCoordinator;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly FullscreenWindowService _windowService;
        private readonly ThemeRuntimeState _runtimeState = new ThemeRuntimeState();

        private readonly ICommand _openOverviewCmd;
        private readonly ICommand _openSelectedGameCmd;
        private readonly ICommand _singleGameRefreshCmd;
        private readonly ICommand _recentRefreshCmd;
        private readonly ICommand _favoritesRefreshCmd;
        private readonly ICommand _fullRefreshCmd;
        private readonly ICommand _installedRefreshCmd;

        private readonly object _refreshLock = new object();
        private CancellationTokenSource _refreshCts;
        private DateTime _lastRefreshRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan StartupRefreshCoalesceWindow = TimeSpan.FromMilliseconds(350);

        private readonly object _updateGate = new object();
        private Task _updateRunner;
        private int _requestVersion;
        private int _processedVersion;
        private Guid? _requestedGameId;
        private CancellationTokenSource _activeUpdateCts;
        private Guid? _appliedGameId;
        private DateTime _appliedLastUpdatedUtc;
        private int _appliedSettingsRevision;
        private double _ultraRareThreshold;
        private double _rareThreshold;
        private double _uncommonThreshold;
        private int _settingsRevision;

        private bool _fullscreenInitialized;

        public ThemeIntegrationService(
            IPlayniteAPI api,
            AchievementService achievementService,
            RefreshCoordinator refreshCoordinator,
            PlayniteAchievementsSettings settings,
            FullscreenWindowService windowService,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _logger = logger;

            _openOverviewCmd = new RelayCommand(_ => OpenOverviewWindow());
            _openSelectedGameCmd = new RelayCommand(_ => OpenSelectedGameWindow());
            _singleGameRefreshCmd = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Single));
            _recentRefreshCmd = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Recent));
            _favoritesRefreshCmd = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Favorites));
            _fullRefreshCmd = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Full));
            _installedRefreshCmd = new RelayCommand(_ => RefreshWithMode(RefreshModeType.Installed));

            // Command surfaces referenced by themes.
            _settings.OpenFullscreenAchievementWindow = _openSelectedGameCmd;
            _settings.OpenAchievementWindow = _openOverviewCmd;
            _settings.OpenGameAchievementWindow = _openSelectedGameCmd;
            _settings.SingleGameRefreshCommand = _singleGameRefreshCmd;
            _settings.RecentRefreshCommand = _recentRefreshCmd;
            _settings.FavoritesRefreshCommand = _favoritesRefreshCmd;
            _settings.FullRefreshCommand = _fullRefreshCmd;
            _settings.InstalledRefreshCommand = _installedRefreshCmd;

            _settings.PropertyChanged += Settings_PropertyChanged;
            _achievementService.CacheInvalidated += AchievementService_CacheInvalidated;
            RefreshCachedThresholds();
        }

        public void Dispose()
        {
            try { _settings.PropertyChanged -= Settings_PropertyChanged; } catch { }
            try { _achievementService.CacheInvalidated -= AchievementService_CacheInvalidated; } catch { }

            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = null;
            }

            lock (_updateGate)
            {
                try { _activeUpdateCts?.Cancel(); } catch { }
                try { _activeUpdateCts?.Dispose(); } catch { }
                _activeUpdateCts = null;
            }
        }

        public void NotifySelectionChanged(Guid? selectedGameId)
        {
            if (!selectedGameId.HasValue)
            {
                return;
            }

            EnsureFullscreenInitialized();
        }

        public void RequestUpdate(Guid? gameId)
        {
            lock (_updateGate)
            {
                _requestVersion++;
                _requestedGameId = gameId;
                if (_updateRunner == null || _updateRunner.IsCompleted)
                {
                    _updateRunner = RunUpdateLoopAsync();
                }
            }

            if (gameId.HasValue && (!_appliedGameId.HasValue || _appliedGameId.Value != gameId.Value))
            {
                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                _ = dispatcher?.BeginInvoke(new Action(() =>
                {
                    try { ClearSingleGameThemeProperties(); } catch { }
                }), DispatcherPriority.Background);
            }
        }

        private bool IsFullscreen()
        {
            try
            {
                return _api?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private void EnsureFullscreenInitialized()
        {
            if (!IsFullscreen() || _fullscreenInitialized)
            {
                return;
            }

            _fullscreenInitialized = true;
            RequestRefresh();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!IsThresholdProperty(e?.PropertyName))
            {
                return;
            }

            RefreshCachedThresholds();
            Interlocked.Increment(ref _settingsRevision);

            if (_appliedGameId.HasValue)
            {
                RequestUpdate(_appliedGameId);
            }

            if (IsFullscreen() && _fullscreenInitialized)
            {
                RequestRefresh();
            }
        }

        private void AchievementService_CacheInvalidated(object sender, EventArgs e)
        {
            if (IsFullscreen() && _fullscreenInitialized)
            {
                RequestRefresh();
            }

            try
            {
                var id = ResolveSelectedGameIdForThemeUpdate();
                if (id.HasValue)
                {
                    RequestUpdate(id);
                }
            }
            catch
            {
            }
        }

        private bool IsThresholdProperty(string propertyName)
        {
            return propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.UltraRareThreshold)}" ||
                   propertyName == nameof(PersistedSettings.UltraRareThreshold) ||
                   propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.RareThreshold)}" ||
                   propertyName == nameof(PersistedSettings.RareThreshold) ||
                   propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.UncommonThreshold)}" ||
                   propertyName == nameof(PersistedSettings.UncommonThreshold) ||
                   propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.XboxUltraRarePointsThreshold)}" ||
                   propertyName == nameof(PersistedSettings.XboxUltraRarePointsThreshold) ||
                   propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.XboxRarePointsThreshold)}" ||
                   propertyName == nameof(PersistedSettings.XboxRarePointsThreshold) ||
                   propertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.XboxUncommonPointsThreshold)}" ||
                   propertyName == nameof(PersistedSettings.XboxUncommonPointsThreshold);
        }

        private void RefreshCachedThresholds()
        {
            try
            {
                _ultraRareThreshold = _settings.Persisted.UltraRareThreshold;
                _rareThreshold = _settings.Persisted.RareThreshold;
                _uncommonThreshold = _settings.Persisted.UncommonThreshold;
            }
            catch
            {
                _ultraRareThreshold = 5;
                _rareThreshold = 10;
                _uncommonThreshold = 50;
            }

        }

        private Guid? ResolveSelectedGameIdForThemeUpdate()
        {
            try
            {
                var selectedGame = _settings?.SelectedGame;
                if (selectedGame != null && selectedGame.Id != Guid.Empty)
                {
                    return selectedGame.Id;
                }
            }
            catch
            {
            }

            return GetSingleSelectedGameId();
        }

        private Guid? GetSingleSelectedGameId()
        {
            try
            {
                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher != null && !dispatcher.CheckAccess())
                {
                    return dispatcher.Invoke(
                        new Func<Guid?>(GetSingleSelectedGameIdFromMainView),
                        DispatcherPriority.Background);
                }

                return GetSingleSelectedGameIdFromMainView();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to resolve selected game for fullscreen achievement window.");
                return null;
            }
        }

        private Guid? GetSingleSelectedGameIdFromMainView()
        {
            var selected = _api?.MainView?.SelectedGames?
                    .Where(g => g != null)
                    .Take(2)
                    .ToList();

            if (selected == null || selected.Count != 1)
            {
                return null;
            }

            return selected[0].Id;
        }

        private async Task RunUpdateLoopAsync()
        {
            while (true)
            {
                int version;
                Guid? gameId;
                int settingsRevision;
                CancellationToken token;

                lock (_updateGate)
                {
                    if (_processedVersion == _requestVersion)
                    {
                        return;
                    }

                    version = _requestVersion;
                    gameId = _requestedGameId;
                    settingsRevision = Volatile.Read(ref _settingsRevision);
                    _processedVersion = version;

                    try { _activeUpdateCts?.Cancel(); } catch { }
                    try { _activeUpdateCts?.Dispose(); } catch { }
                    _activeUpdateCts = new CancellationTokenSource();
                    token = _activeUpdateCts.Token;
                }

                if (!gameId.HasValue)
                {
                    await ApplyClearAsync(version).ConfigureAwait(false);
                    continue;
                }

                GameAchievementData gameData = null;
                try
                {
                    gameData = await Task.Run(() => _achievementService.GetGameAchievementData(gameId.Value), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Theme integration update failed to fetch game data.");
                }

                if (token.IsCancellationRequested)
                {
                    continue;
                }

                if (gameData == null || !gameData.HasAchievements)
                {
                    await ApplyClearAsync(version).ConfigureAwait(false);
                    continue;
                }

                if (_appliedGameId.HasValue &&
                    _appliedGameId.Value == gameId.Value &&
                    _appliedLastUpdatedUtc == gameData.LastUpdatedUtc &&
                    _appliedSettingsRevision == settingsRevision)
                {
                    continue;
                }

                SelectedGameRuntimeState state = null;
                try
                {
                    var ultra = _ultraRareThreshold;
                    var rare = _rareThreshold;
                    var uncommon = _uncommonThreshold;

                    state = await Task.Run(
                        () => SelectedGameRuntimeStateBuilder.Build(gameId.Value, gameData, ultra, rare, uncommon),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    continue;
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Theme integration update failed while building selected-game state.");
                }

                if (token.IsCancellationRequested)
                {
                    continue;
                }

                var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    continue;
                }

                await dispatcher.InvokeAsync(() =>
                {
                    if (!IsLatest(version))
                    {
                        return;
                    }

                    if (state == null || !state.HasAchievements)
                    {
                        ClearSingleGameThemeProperties();
                        return;
                    }

                    ApplySelectedGameState(state);
                    _appliedGameId = gameId;
                    _appliedLastUpdatedUtc = gameData.LastUpdatedUtc;
                    _appliedSettingsRevision = settingsRevision;
                }, DispatcherPriority.Background).Task.ConfigureAwait(false);
            }
        }

        private Task ApplyClearAsync(int version)
        {
            var dispatcher = _api?.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                ClearSingleGameThemeProperties();
                return Task.CompletedTask;
            }

            return dispatcher.InvokeAsync(() =>
            {
                if (!IsLatest(version))
                {
                    return;
                }

                ClearSingleGameThemeProperties();
            }, DispatcherPriority.Background).Task;
        }

        private bool IsLatest(int version)
        {
            lock (_updateGate)
            {
                return version == _requestVersion;
            }
        }

        #region Window Operations (delegated to FullscreenWindowService)

        private void OpenOverviewWindow()
        {
            // Cancel any pending async refresh to prevent it from overwriting our data
            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = null;
            }

            // Mark as initialized to prevent other code paths from triggering refresh
            if (!_fullscreenInitialized)
            {
                _fullscreenInitialized = true;
            }

            // Immediately populate all-games data on UI thread before opening window
            PopulateAllGamesDataSync(includeHeavyAchievementLists: true);

            _windowService.OpenOverviewWindow();
        }

        private void OpenSelectedGameWindow()
        {
            var id = GetSingleSelectedGameId();
            if (!id.HasValue)
            {
                return;
            }

            OpenGameWindow(id.Value);
        }

        public void OpenGameWindow(Guid gameId)
        {
            EnsureFullscreenInitialized();

            // Synchronously populate single-game data before opening the window.
            // This prevents the race condition where the window opens before
            // async theme updates complete, showing stale data from the previous selection.
            PopulateSingleGameDataSync(gameId);

            _windowService.OpenGameWindow(gameId);
        }

        #endregion

        #region Refresh Operations

        private void RefreshWithMode(RefreshModeType mode)
        {
            Guid? gameIdForThemeUpdate = null;

            if (mode == RefreshModeType.Single)
            {
                var id = GetSingleSelectedGameId();
                if (!id.HasValue)
                {
                    return;
                }
                gameIdForThemeUpdate = id;
            }

            EnsureFullscreenInitialized();
            RunAchievementRefresh(mode, gameIdForThemeUpdate);
        }

        private void RunAchievementRefresh(RefreshModeType mode, Guid? gameIdForThemeUpdate)
        {
            var errorLogMessage = mode switch
            {
                RefreshModeType.Full => "Full achievement refresh failed.",
                RefreshModeType.Installed => "Installed games achievement refresh failed.",
                RefreshModeType.Single => "Single game achievement refresh failed.",
                RefreshModeType.Recent => "Recent refresh achievement refresh failed.",
                RefreshModeType.Favorites => "Favorites achievement refresh failed.",
                _ => "Achievement refresh failed."
            };

            var isFullscreen = IsFullscreen();
            _logger?.Info($"RunAchievementRefresh: mode={mode}, isFullscreen={isFullscreen}, gameId={gameIdForThemeUpdate}");

            if (isFullscreen)
            {
                RunAchievementRefreshWithGlobalProgress(mode, gameIdForThemeUpdate, errorLogMessage);
            }
            else
            {
                RunAchievementRefreshWithProgressWindow(mode, gameIdForThemeUpdate, errorLogMessage);
            }
        }

        private void RunAchievementRefreshWithGlobalProgress(
            RefreshModeType mode,
            Guid? gameIdForThemeUpdate,
            string errorLogMessage)
        {
            var progressOptions = new GlobalProgressOptions(ResourceProvider.GetString("LOCPlayAch_Status_Starting"), true)
            {
                Cancelable = true,
                IsIndeterminate = false
            };

            _api.Dialogs.ActivateGlobalProgress((progress) =>
            {
                progress.ProgressMaxValue = 100;
                progress.CurrentProgressValue = 0;

                EventHandler<ProgressReport> progressHandler = null;
                progressHandler = (sender, report) =>
                {
                    if (report == null) return;

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(report.Message))
                        {
                            progress.Text = report.Message;
                        }

                        var percent = report.PercentComplete;
                        if (percent <= 0 || double.IsNaN(percent))
                        {
                            if (report.TotalSteps > 0)
                            {
                                percent = (report.CurrentStep * 100.0) / report.TotalSteps;
                            }
                            else
                            {
                                percent = 0;
                            }
                        }
                        progress.CurrentProgressValue = Math.Max(0, Math.Min(100, percent));

                        if (report.IsCanceled || progress.CancelToken.IsCancellationRequested)
                        {
                            _logger?.Info("Progress handler detected cancellation request.");
                            return;
                        }
                    }
                    catch { }
                };

                _achievementService.RebuildProgress += progressHandler;

                try
                {
                    var request = new RefreshRequest
                    {
                        Mode = mode,
                        SingleGameId = mode == RefreshModeType.Single ? gameIdForThemeUpdate : null
                    };

                    var refreshTask = _refreshCoordinator.ExecuteAsync(
                        request,
                        new RefreshExecutionPolicy
                        {
                            ValidateAuthentication = false,
                            SwallowExceptions = false,
                            ErrorLogMessage = errorLogMessage,
                            ExternalCancellationToken = progress.CancelToken
                        });

                    refreshTask.Wait(progress.CancelToken);
                    progress.CurrentProgressValue = 100;
                    progress.Text = ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
                }
                catch (OperationCanceledException)
                {
                    _achievementService.CancelCurrentRebuild();
                    progress.Text = ResourceProvider.GetString("LOCPlayAch_Status_Canceled");
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, errorLogMessage);
                    progress.Text = ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
                }
                finally
                {
                    _achievementService.RebuildProgress -= progressHandler;
                    if (gameIdForThemeUpdate.HasValue)
                    {
                        try { RequestUpdate(gameIdForThemeUpdate.Value); } catch { }
                    }
                    try { if (IsFullscreen() && _fullscreenInitialized) RequestRefresh(); } catch { }
                }
            }, progressOptions);
        }

        private void RunAchievementRefreshWithProgressWindow(
            RefreshModeType mode,
            Guid? gameIdForThemeUpdate,
            string errorLogMessage)
        {
            _logger?.Info($"RunAchievementRefreshWithProgressWindow: Starting fullscreen refresh, mode={mode}, gameId={gameIdForThemeUpdate}");

            var request = new RefreshRequest
            {
                Mode = mode,
                SingleGameId = mode == RefreshModeType.Single ? gameIdForThemeUpdate : null
            };

            _ = _refreshCoordinator.ExecuteAsync(
                request,
                new RefreshExecutionPolicy
                {
                    ValidateAuthentication = false,
                    UseProgressWindow = true,
                    SwallowExceptions = true,
                    ProgressSingleGameId = gameIdForThemeUpdate,
                    ErrorLogMessage = errorLogMessage,
                    OnRefreshCompleted = (success) =>
                    {
                        _logger?.Info($"RunAchievementRefreshWithProgressWindow: Completed, success={success}, gameId={gameIdForThemeUpdate}");
                        if (success)
                        {
                            if (gameIdForThemeUpdate.HasValue)
                            {
                                try { RequestUpdate(gameIdForThemeUpdate.Value); } catch { }
                            }
                            try { if (IsFullscreen()) RequestRefresh(); } catch { }
                        }
                    }
                });
        }

        #endregion

        #region Snapshot Building and Refresh

        private void PopulateAllGamesDataSync(bool includeHeavyAchievementLists)
        {
            try
            {
                _logger?.Info("PopulateAllGamesDataSync: Starting to populate all-games achievement data.");

                var allData = _achievementService.GetAllGameAchievementData() ?? new List<GameAchievementData>();
                allData = FilterExcludedFromSummaries(allData);
                _logger?.Info($"PopulateAllGamesDataSync: Found {allData.Count} total game data entries.");

                var state = LibraryRuntimeStateBuilder.Build(
                    allData,
                    _api,
                    CancellationToken.None,
                    includeHeavyAchievementLists);

                _logger?.Info($"PopulateAllGamesDataSync: State created - TotalTrophies={state.TotalTrophies}, PlatinumTrophies={state.PlatinumTrophies}, GoldTrophies={state.GoldTrophies}, Rank={state.Rank}");

                ApplyLibraryState(state);

                _logger?.Info($"PopulateAllGamesDataSync: Applied snapshot. AllGamesWithAchievements count={_settings.LegacyTheme.AllGamesWithAchievements?.Count ?? 0}");
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to populate all-games data synchronously.");
            }
        }

        private List<GameAchievementData> FilterExcludedFromSummaries(List<GameAchievementData> allData)
        {
            allData ??= new List<GameAchievementData>();

            var excludedIds = _settings?.Persisted?.ExcludedFromSummariesGameIds;
            if (excludedIds == null || excludedIds.Count == 0)
            {
                return allData;
            }

            return allData
                .Where(data => data?.PlayniteGameId == null || !excludedIds.Contains(data.PlayniteGameId.Value))
                .ToList();
        }

        /// <summary>
        /// Synchronously populates single-game achievement data for the specified game.
        /// Uses existing cached data without triggering a refresh.
        /// Called on desktop game selection changes to populate native theme bindings for theme controls.
        /// </summary>
        /// <param name="gameId">The ID of the game to populate data for.</param>
        internal void PopulateSingleGameDataSync(Guid gameId)
        {
            try
            {
                var gameData = _achievementService.GetGameAchievementData(gameId);
                var state = SelectedGameRuntimeStateBuilder.Build(
                    gameId,
                    gameData,
                    _ultraRareThreshold,
                    _rareThreshold,
                    _uncommonThreshold);

                if (state != null && state.HasAchievements)
                {
                    ApplySelectedGameState(state);
                    _appliedGameId = gameId;
                    _appliedLastUpdatedUtc = gameData?.LastUpdatedUtc ?? default;
                    _appliedSettingsRevision = Volatile.Read(ref _settingsRevision);
                }
                else
                {
                    ClearSingleGameThemeProperties();
                }
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to populate single-game data synchronously for game {gameId}.");
                ClearSingleGameThemeProperties();
            }
        }

        private void RequestRefresh()
        {
            if (!IsFullscreen())
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastRefreshRequestUtc) < StartupRefreshCoalesceWindow)
            {
                return;
            }
            _lastRefreshRequestUtc = nowUtc;

            const bool shouldBuildHeavyAchievementLists = false;

            CancellationToken token;
            lock (_refreshLock)
            {
                try { _refreshCts?.Cancel(); } catch { }
                try { _refreshCts?.Dispose(); } catch { }
                _refreshCts = new CancellationTokenSource();
                token = _refreshCts.Token;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(500, token).ConfigureAwait(false);

                    var allData = _achievementService.GetAllGameAchievementData() ?? new List<GameAchievementData>();
                    allData = FilterExcludedFromSummaries(allData);

                    token.ThrowIfCancellationRequested();

                    var state = LibraryRuntimeStateBuilder.Build(
                        allData,
                        _api,
                        token,
                        shouldBuildHeavyAchievementLists);

                    var uiDispatcher = _api.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                    uiDispatcher?.InvokeIfNeeded(() => ApplyLibraryState(state), DispatcherPriority.Background);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, "Failed to refresh theme integration snapshot.");
                }
            }, token);
        }

        private void ApplyLibraryState(LibraryRuntimeState state)
        {
            _runtimeState.Library = state ?? new LibraryRuntimeState();
            var library = _runtimeState.Library;

            _settings.Theme.CompletedGamesAsc = ProjectGameSummaries(library.CompletedGamesAsc);
            _settings.Theme.CompletedGamesDesc = ProjectGameSummaries(library.CompletedGamesDesc);
            _settings.Theme.GameSummariesAsc = ProjectGameSummaries(library.GameSummariesAsc);
            _settings.Theme.GameSummariesDesc = ProjectGameSummaries(library.GameSummariesDesc);
            _settings.Theme.TotalCommon = library.TotalCommon;
            _settings.Theme.TotalUncommon = library.TotalUncommon;
            _settings.Theme.TotalRare = library.TotalRare;
            _settings.Theme.TotalUltraRare = library.TotalUltraRare;
            _settings.Theme.TotalRareAndUltraRare = library.TotalRareAndUltraRare;
            _settings.Theme.TotalOverall = library.TotalOverall;
            _settings.Theme.SteamGames = ProjectGameSummaries(library.SteamGames);
            _settings.Theme.GOGGames = ProjectGameSummaries(library.GOGGames);
            _settings.Theme.EpicGames = ProjectGameSummaries(library.EpicGames);
            _settings.Theme.XboxGames = ProjectGameSummaries(library.XboxGames);
            _settings.Theme.PSNGames = ProjectGameSummaries(library.PSNGames);
            _settings.Theme.RetroAchievementsGames = ProjectGameSummaries(library.RetroAchievementsGames);
            _settings.Theme.RPCS3Games = ProjectGameSummaries(library.RPCS3Games);
            _settings.Theme.ShadPS4Games = ProjectGameSummaries(library.ShadPS4Games);
            _settings.Theme.ManualGames = ProjectGameSummaries(library.ManualGames);
            _settings.Theme.MostRecentUnlocksTop3 = library.MostRecentUnlocksTop3;
            _settings.Theme.MostRecentUnlocksTop5 = library.MostRecentUnlocksTop5;
            _settings.Theme.MostRecentUnlocksTop10 = library.MostRecentUnlocksTop10;
            _settings.Theme.RarestRecentUnlocksTop3 = library.RarestRecentUnlocksTop3;
            _settings.Theme.RarestRecentUnlocksTop5 = library.RarestRecentUnlocksTop5;
            _settings.Theme.RarestRecentUnlocksTop10 = library.RarestRecentUnlocksTop10;

            _settings.LegacyTheme.HasDataAllGames = library.HasData;
            _settings.LegacyTheme.GamesWithAchievements = ProjectGameSummaries(library.GameSummariesDesc);
            _settings.LegacyTheme.TotalTrophies = library.TotalTrophies;
            _settings.LegacyTheme.PlatinumTrophies = library.PlatinumTrophies;
            _settings.LegacyTheme.GoldTrophies = library.GoldTrophies;
            _settings.LegacyTheme.SilverTrophies = library.SilverTrophies;
            _settings.LegacyTheme.BronzeTrophies = library.BronzeTrophies;
            _settings.LegacyTheme.Level = library.Level;
            _settings.LegacyTheme.LevelProgress = library.LevelProgress;
            _settings.LegacyTheme.Rank = !string.IsNullOrWhiteSpace(library.Rank) ? library.Rank : "Bronze1";

            _settings.LegacyTheme.AllGamesWithAchievements = ProjectGameSummaries(library.AllGamesWithAchievements);
            _settings.PlatinumGames = ProjectGameSummaries(library.PlatinumGames);
            _settings.LegacyTheme.PlatinumGamesAscending = ProjectGameSummaries(library.PlatinumGamesAscending);
            _settings.LegacyTheme.GSTotal = library.TotalTrophies > 0 ? library.TotalTrophies.ToString() : "0";
            _settings.LegacyTheme.GSPlat = library.TotalTrophies > 0 ? library.PlatinumTrophies.ToString() : "0";
            _settings.LegacyTheme.GS90 = library.TotalTrophies > 0 ? library.GoldTrophies.ToString() : "0";
            _settings.LegacyTheme.GS30 = library.TotalTrophies > 0 ? library.SilverTrophies.ToString() : "0";
            _settings.LegacyTheme.GS15 = library.TotalTrophies > 0 ? library.BronzeTrophies.ToString() : "0";
            _settings.LegacyTheme.GSScore = library.TotalTrophies > 0 ? library.Score.ToString("N0") : "0";
            _settings.LegacyTheme.GSLevel = library.TotalTrophies > 0 ? library.Level.ToString() : "0";
            _settings.LegacyTheme.GSLevelProgress = library.TotalTrophies > 0 ? library.LevelProgress : 0;
            _settings.LegacyTheme.GSRank = library.TotalTrophies > 0 && !string.IsNullOrWhiteSpace(library.Rank) ? library.Rank : "Bronze1";

            var shouldUpdateHeavyLists = library.HeavyListsBuilt || !library.HasData;
            if (shouldUpdateHeavyLists)
            {
                _settings.Theme.AllAchievementsUnlockAsc = library.AllAchievementsUnlockAsc;
                _settings.Theme.AllAchievementsUnlockDesc = library.AllAchievementsUnlockDesc;
                _settings.Theme.AllAchievementsRarityAsc = library.AllAchievementsRarityAsc;
                _settings.Theme.AllAchievementsRarityDesc = library.AllAchievementsRarityDesc;
                _settings.Theme.MostRecentUnlocks = library.MostRecentUnlocks;
                _settings.Theme.RarestRecentUnlocks = library.RarestRecentUnlocks;
            }

            NotifySettingProperties(CompatibilityAllGamesDelegatedProperties);
            NotifySettingProperties(NativeAllGamesCoreDelegatedProperties);
            if (shouldUpdateHeavyLists)
            {
                NotifySettingProperties(NativeAllGamesHeavyDelegatedProperties);
            }
        }

        #endregion

        #region Per-Game Theme Integration

        /// <summary>
        /// Apply a single-game snapshot to theme-exposed settings properties.
        /// Intended to be executed on the UI thread.
        /// </summary>
        private void ApplySelectedGameState(SelectedGameRuntimeState state)
        {
            if (state == null || !state.HasAchievements)
            {
                ClearSingleGameThemeProperties();
                return;
            }

            _runtimeState.SelectedGame = state;
            _settings.Theme.HasAchievements = true;
            _settings.Theme.IsCompleted = state.IsCompleted;
            _settings.Theme.AchievementCount = state.AchievementCount;
            _settings.Theme.UnlockedCount = state.UnlockedCount;
            _settings.Theme.LockedCount = state.LockedCount;
            _settings.Theme.ProgressPercentage = state.ProgressPercentage;
            _settings.Theme.Common = state.Common;
            _settings.Theme.Uncommon = state.Uncommon;
            _settings.Theme.Rare = state.Rare;
            _settings.Theme.UltraRare = state.UltraRare;
            _settings.Theme.RareAndUltraRare = state.RareAndUltraRare;
            _settings.Theme.AllAchievements = state.AllAchievements;
            _settings.Theme.AchievementsNewestFirst = state.AchievementsNewestFirst;
            _settings.Theme.AchievementsOldestFirst = state.AchievementsOldestFirst;
            _settings.Theme.AchievementsRarityAsc = state.AchievementsRarityAsc;
            _settings.Theme.AchievementsRarityDesc = state.AchievementsRarityDesc;

            _settings.LegacyTheme.HasData = true;
            _settings.LegacyTheme.Total = state.AchievementCount;
            _settings.LegacyTheme.Unlocked = state.UnlockedCount;
            _settings.LegacyTheme.Percent = state.ProgressPercentage;
            _settings.LegacyTheme.Is100Percent = state.UnlockedCount == state.AchievementCount && state.AchievementCount > 0;
            _settings.LegacyTheme.Locked = state.LockedCount;
            _settings.LegacyTheme.TotalGamerScore = 0;
            _settings.LegacyTheme.EstimateTimeToUnlock = string.Empty;
            _settings.LegacyTheme.ListAchievements = state.AllAchievements;
            _settings.LegacyTheme.ListAchUnlockDateAsc = state.AchievementsOldestFirst;
            _settings.LegacyTheme.ListAchUnlockDateDesc = state.AchievementsNewestFirst;

            NotifySettingProperties(SingleGameThemeDelegatedProperties);
            NotifySettingProperties(SingleGameLegacyDelegatedProperties);
        }

        /// <summary>
        /// Clear per-game theme properties when no game is selected or game has no achievements.
        /// </summary>
        public void ClearSingleGameThemeProperties()
        {
            _runtimeState.SelectedGame = SelectedGameRuntimeState.Empty;
            _settings.Theme.HasAchievements = false;
            _settings.Theme.IsCompleted = false;
            _settings.Theme.AchievementCount = 0;
            _settings.Theme.UnlockedCount = 0;
            _settings.Theme.LockedCount = 0;
            _settings.Theme.ProgressPercentage = 0;

            _settings.Theme.AllAchievements = EmptyAchievementList;
            _settings.Theme.AchievementsNewestFirst = EmptyAchievementList;
            _settings.Theme.AchievementsOldestFirst = EmptyAchievementList;
            _settings.Theme.AchievementsRarityAsc = EmptyAchievementList;
            _settings.Theme.AchievementsRarityDesc = EmptyAchievementList;

            _settings.Theme.Common = EmptyRarityStats;
            _settings.Theme.Uncommon = EmptyRarityStats;
            _settings.Theme.Rare = EmptyRarityStats;
            _settings.Theme.UltraRare = EmptyRarityStats;
            _settings.Theme.RareAndUltraRare = EmptyRarityStats;
            _settings.LegacyTheme.HasData = false;
            _settings.LegacyTheme.Total = 0;
            _settings.LegacyTheme.Unlocked = 0;
            _settings.LegacyTheme.Percent = 0;

            _settings.LegacyTheme.Is100Percent = false;
            _settings.LegacyTheme.Locked = 0;
            _settings.LegacyTheme.TotalGamerScore = 0;
            _settings.LegacyTheme.EstimateTimeToUnlock = string.Empty;

            _settings.LegacyTheme.ListAchievements = EmptyAchievementList;
            _settings.LegacyTheme.ListAchUnlockDateAsc = EmptyAchievementList;
            _settings.LegacyTheme.ListAchUnlockDateDesc = EmptyAchievementList;

            _appliedGameId = null;
            _appliedLastUpdatedUtc = default;
            _appliedSettingsRevision = Volatile.Read(ref _settingsRevision);

            NotifySettingProperties(SingleGameThemeDelegatedProperties);
            NotifySettingProperties(SingleGameLegacyDelegatedProperties);
        }

        #endregion

        private ObservableCollection<GameAchievementSummary> ProjectGameSummaries(IEnumerable<GameSummaryRuntimeItem> items)
        {
            var projected = (items ?? Enumerable.Empty<GameSummaryRuntimeItem>())
                .Select(item => new GameAchievementSummary(
                    item.GameId,
                    item.Name,
                    item.Platform,
                    item.CoverImagePath,
                    item.Progress,
                    item.GoldCount,
                    item.SilverCount,
                    item.BronzeCount,
                    item.IsCompleted,
                    item.LastUnlockDate,
                    new RelayCommand(_ => OpenGameWindow(item.GameId)),
                    item.Common,
                    item.Uncommon,
                    item.Rare,
                    item.UltraRare,
                    item.RareAndUltraRare,
                    item.Overall))
                .ToList();

            return new ObservableCollection<GameAchievementSummary>(projected);
        }

        private void NotifySettingProperties(params string[] propertyNames)
        {
            if (propertyNames == null || propertyNames.Length == 0)
            {
                return;
            }

            for (int i = 0; i < propertyNames.Length; i++)
            {
                var name = propertyNames[i];
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _settings.OnPropertyChanged(name);
                }
            }
        }
    }
}



