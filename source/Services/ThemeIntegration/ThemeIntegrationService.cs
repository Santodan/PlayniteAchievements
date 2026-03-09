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
            nameof(PlayniteAchievementsSettings.TotalUnlockCount),
            nameof(PlayniteAchievementsSettings.TotalCommonUnlockCount),
            nameof(PlayniteAchievementsSettings.TotalUncommonUnlockCount),
            nameof(PlayniteAchievementsSettings.TotalRareUnlockCount),
            nameof(PlayniteAchievementsSettings.TotalUltraRareUnlockCount),
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
        private readonly Action<Guid?> _requestSingleGameThemeUpdate;

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

        private bool _fullscreenInitialized;

        public ThemeIntegrationService(
            IPlayniteAPI api,
            AchievementService achievementService,
            RefreshCoordinator refreshCoordinator,
            PlayniteAchievementsSettings settings,
            FullscreenWindowService windowService,
            Action<Guid?> requestSingleGameThemeUpdate,
            ILogger logger)
        {
            _api = api ?? throw new ArgumentNullException(nameof(api));
            _achievementService = achievementService ?? throw new ArgumentNullException(nameof(achievementService));
            _refreshCoordinator = refreshCoordinator ?? throw new ArgumentNullException(nameof(refreshCoordinator));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _windowService = windowService ?? throw new ArgumentNullException(nameof(windowService));
            _requestSingleGameThemeUpdate = requestSingleGameThemeUpdate ?? throw new ArgumentNullException(nameof(requestSingleGameThemeUpdate));
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
        }

        public void NotifySelectionChanged(Guid? selectedGameId)
        {
            // Raise PropertyChanged for SelectedGame so themes get notified of selection changes
            _settings.OnPropertyChanged(nameof(PlayniteAchievementsSettings.SelectedGame));

            if (!selectedGameId.HasValue)
            {
                return;
            }

            EnsureFullscreenInitialized();
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
            if (e == null)
            {
                return;
            }

            if (e.PropertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.UltraRareThreshold)}" ||
                e.PropertyName == nameof(PersistedSettings.UltraRareThreshold) ||
                e.PropertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.RareThreshold)}" ||
                e.PropertyName == nameof(PersistedSettings.RareThreshold) ||
                e.PropertyName == $"{nameof(PlayniteAchievementsSettings.Persisted)}.{nameof(PersistedSettings.UncommonThreshold)}" ||
                e.PropertyName == nameof(PersistedSettings.UncommonThreshold))
            {
                try
                {
                    RarityHelper.Configure(
                        _settings.Persisted.UltraRareThreshold,
                        _settings.Persisted.RareThreshold,
                        _settings.Persisted.UncommonThreshold);
                }
                catch
                {
                }

                if (IsFullscreen() && _fullscreenInitialized)
                {
                    RequestRefresh();
                }
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
                    _requestSingleGameThemeUpdate(id);
                }
            }
            catch
            {
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
                RefreshModeType.Single => "Single game achievement refresh failed.",
                RefreshModeType.Recent => "Recent refresh achievement refresh failed.",
                RefreshModeType.Favorites => "Favorites achievement refresh failed.",
                RefreshModeType.Full => "Full achievement refresh failed.",
                RefreshModeType.Installed => "Installed games achievement refresh failed.",
                _ => "Achievement refresh failed."
            };

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
                            return;
                        }
                    }
                    catch { }
                };

                _achievementService.RebuildProgress += progressHandler;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var request = new RefreshRequest
                        {
                            Mode = mode,
                            SingleGameId = mode == RefreshModeType.Single ? gameIdForThemeUpdate : null
                        };

                        await _refreshCoordinator.ExecuteAsync(
                            request,
                            new RefreshExecutionPolicy
                            {
                                ValidateAuthentication = false,
                                UseProgressWindow = false,
                                SwallowExceptions = false,
                                ErrorLogMessage = errorLogMessage
                            }).ConfigureAwait(false);

                        try
                        {
                            progress.CurrentProgressValue = 100;
                            progress.Text = ResourceProvider.GetString("LOCPlayAch_Status_RefreshComplete");
                        }
                        catch { }
                    }
                    catch (OperationCanceledException)
                    {
                        try
                        {
                            progress.Text = ResourceProvider.GetString("LOCPlayAch_Status_Canceled");
                        }
                        catch { }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Error(ex, errorLogMessage);
                        try
                        {
                            progress.Text = ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
                        }
                        catch { }
                    }
                    finally
                    {
                        _achievementService.RebuildProgress -= progressHandler;
                        if (gameIdForThemeUpdate.HasValue)
                        {
                            try { _requestSingleGameThemeUpdate(gameIdForThemeUpdate.Value); } catch { }
                        }
                        try { if (IsFullscreen() && _fullscreenInitialized) RequestRefresh(); } catch { }
                    }
                });
            }, progressOptions);
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

                var snapshot = AllGamesSnapshotService.BuildSnapshot(
                    allData,
                    _api,
                    OpenGameWindow,
                    CancellationToken.None,
                    includeHeavyAchievementLists);

                _logger?.Info($"PopulateAllGamesDataSync: Snapshot created - TotalCount={snapshot.TotalCount}, PlatCount={snapshot.PlatCount}, GoldCount={snapshot.GoldCount}, Rank={snapshot.Rank}");

                ApplySnapshot(snapshot);

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
        /// Synchronously populates single-game achievement data before opening a game window.
        /// Uses existing cached data without triggering a refresh. This prevents stale data by
        /// ensuring the snapshot is applied before the window opens.
        /// </summary>
        private void PopulateSingleGameDataSync(Guid gameId)
        {
            try
            {
                var gameData = _achievementService.GetGameAchievementData(gameId);
                if (gameData == null || !gameData.HasAchievements)
                {
                    ClearSingleGameThemeProperties();
                    return;
                }

                var snapshot = SingleGameSnapshotService.BuildSnapshot(
                    gameId,
                    gameData,
                    _settings.Persisted.UltraRareThreshold,
                    _settings.Persisted.RareThreshold,
                    _settings.Persisted.UncommonThreshold);

                if (snapshot != null)
                {
                    ApplySnapshot(snapshot);
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

                    var snapshot = AllGamesSnapshotService.BuildSnapshot(
                        allData,
                        _api,
                        OpenGameWindow,
                        token,
                        shouldBuildHeavyAchievementLists);

                    var uiDispatcher = _api.MainView?.UIDispatcher ?? Application.Current?.Dispatcher;
                    uiDispatcher?.InvokeIfNeeded(() => ApplySnapshot(snapshot), DispatcherPriority.Background);
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

        private void ApplySnapshot(AllGamesSnapshot snapshot)
        {
            snapshot ??= new AllGamesSnapshot();

            ApplyNativeSurface(snapshot);
            ApplyCompatibilitySurface(snapshot);
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

        private void ApplyCompatibilitySurface(AllGamesSnapshot snapshot)
        {
            _settings.LegacyTheme.AllGamesWithAchievements = snapshot.CreateAllGamesObservable();
            _settings.PlatinumGames = snapshot.CreatePlatinumObservable();
            _settings.LegacyTheme.PlatinumGamesAscending = snapshot.CreatePlatinumAscendingObservable();

            _settings.LegacyTheme.GSTotal = snapshot.TotalCount > 0 ? snapshot.TotalCount.ToString() : "0";
            _settings.LegacyTheme.GSPlat = snapshot.TotalCount > 0 ? snapshot.PlatCount.ToString() : "0";
            _settings.LegacyTheme.GS90 = snapshot.TotalCount > 0 ? snapshot.GoldCount.ToString() : "0";
            _settings.LegacyTheme.GS30 = snapshot.TotalCount > 0 ? snapshot.SilverCount.ToString() : "0";
            _settings.LegacyTheme.GS15 = snapshot.TotalCount > 0 ? snapshot.BronzeCount.ToString() : "0";
            _settings.LegacyTheme.GSScore = snapshot.TotalCount > 0 ? snapshot.Score.ToString("N0") : "0";

            _settings.LegacyTheme.GSLevel = snapshot.TotalCount > 0 ? snapshot.Level.ToString() : "0";
            _settings.LegacyTheme.GSLevelProgress = snapshot.TotalCount > 0 ? snapshot.LevelProgress : 0;
            _settings.LegacyTheme.GSRank = snapshot.TotalCount > 0 && !string.IsNullOrWhiteSpace(snapshot.Rank) ? snapshot.Rank : "Bronze1";

            // Raise PropertyChanged for delegated properties so bindings update
            NotifySettingProperties(CompatibilityAllGamesDelegatedProperties);
        }

        private void ApplyNativeSurface(AllGamesSnapshot snapshot)
        {
            _settings.Theme.CompletedGamesAsc = snapshot.CreateCompletedGamesAscObservable();
            _settings.Theme.CompletedGamesDesc = snapshot.CreateCompletedGamesDescObservable();
            _settings.Theme.GameSummariesAsc = snapshot.CreateGameSummariesAscObservable();
            _settings.Theme.GameSummariesDesc = snapshot.CreateGameSummariesDescObservable();

            _settings.LegacyTheme.HasDataAllGames = snapshot.TotalCount > 0;
            _settings.LegacyTheme.GamesWithAchievements = snapshot.CreateGameSummariesDescObservable();

            _settings.LegacyTheme.TotalTrophies = snapshot.TotalCount;
            _settings.LegacyTheme.PlatinumTrophies = snapshot.PlatCount;
            _settings.LegacyTheme.GoldTrophies = snapshot.GoldCount;
            _settings.LegacyTheme.SilverTrophies = snapshot.SilverCount;
            _settings.LegacyTheme.BronzeTrophies = snapshot.BronzeCount;

            _settings.Theme.TotalUnlockCount = snapshot.TotalUnlockCount;
            _settings.Theme.TotalCommonUnlockCount = snapshot.TotalCommonUnlockCount;
            _settings.Theme.TotalUncommonUnlockCount = snapshot.TotalUncommonUnlockCount;
            _settings.Theme.TotalRareUnlockCount = snapshot.TotalRareUnlockCount;
            _settings.Theme.TotalUltraRareUnlockCount = snapshot.TotalUltraRareUnlockCount;

            // Per-provider game lists
            _settings.Theme.SteamGames = snapshot.CreateSteamGamesObservable();
            _settings.Theme.GOGGames = snapshot.CreateGOGGamesObservable();
            _settings.Theme.EpicGames = snapshot.CreateEpicGamesObservable();
            _settings.Theme.XboxGames = snapshot.CreateXboxGamesObservable();
            _settings.Theme.PSNGames = snapshot.CreatePSNGamesObservable();
            _settings.Theme.RetroAchievementsGames = snapshot.CreateRetroAchievementsGamesObservable();
            _settings.Theme.RPCS3Games = snapshot.CreateRPCS3GamesObservable();
            _settings.Theme.ShadPS4Games = snapshot.CreateShadPS4GamesObservable();
            _settings.Theme.ManualGames = snapshot.CreateManualGamesObservable();

            _settings.LegacyTheme.Level = snapshot.Level;
            _settings.LegacyTheme.LevelProgress = snapshot.LevelProgress;
            _settings.LegacyTheme.Rank = !string.IsNullOrWhiteSpace(snapshot.Rank) ? snapshot.Rank : "Bronze1";

            // Lightweight all-games lists (always updated)
            _settings.Theme.MostRecentUnlocksTop3 = snapshot.MostRecentUnlocksTop3;
            _settings.Theme.MostRecentUnlocksTop5 = snapshot.MostRecentUnlocksTop5;
            _settings.Theme.MostRecentUnlocksTop10 = snapshot.MostRecentUnlocksTop10;
            _settings.Theme.RarestRecentUnlocksTop3 = snapshot.RarestRecentUnlocksTop3;
            _settings.Theme.RarestRecentUnlocksTop5 = snapshot.RarestRecentUnlocksTop5;
            _settings.Theme.RarestRecentUnlocksTop10 = snapshot.RarestRecentUnlocksTop10;

            var shouldUpdateHeavyLists = snapshot.HeavyListsBuilt || snapshot.TotalCount <= 0;
            if (shouldUpdateHeavyLists)
            {
                _settings.Theme.AllAchievementsUnlockAsc = snapshot.AllAchievementsUnlockAsc;
                _settings.Theme.AllAchievementsUnlockDesc = snapshot.AllAchievementsUnlockDesc;
                _settings.Theme.AllAchievementsRarityAsc = snapshot.AllAchievementsRarityAsc;
                _settings.Theme.AllAchievementsRarityDesc = snapshot.AllAchievementsRarityDesc;
                _settings.Theme.MostRecentUnlocks = snapshot.MostRecentUnlocks;
                _settings.Theme.RarestRecentUnlocks = snapshot.RarestRecentUnlocks;
            }

            // Raise PropertyChanged for delegated properties so bindings update.
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
        public void ApplySnapshot(SingleGameSnapshot snapshot)
        {
            if (snapshot == null || snapshot.Total <= 0)
            {
                ClearSingleGameThemeProperties();
                return;
            }

            ApplySingleGameToTheme(snapshot);
            ApplySingleGameToLegacyTheme(snapshot);
        }

        private void ApplySingleGameToTheme(SingleGameSnapshot snapshot)
        {
            _settings.Theme.HasAchievements = true;
            _settings.Theme.IsCompleted = snapshot.IsCompleted;
            _settings.Theme.AchievementCount = snapshot.Total;
            _settings.Theme.UnlockedCount = snapshot.Unlocked;
            _settings.Theme.LockedCount = snapshot.Locked;
            _settings.Theme.ProgressPercentage = snapshot.Percent;

            _settings.Theme.Common = snapshot.Common;
            _settings.Theme.Uncommon = snapshot.Uncommon;
            _settings.Theme.Rare = snapshot.Rare;
            _settings.Theme.UltraRare = snapshot.UltraRare;

            _settings.Theme.AllAchievements = snapshot.AllAchievements;
            _settings.Theme.AchievementsNewestFirst = snapshot.UnlockDateDesc;
            _settings.Theme.AchievementsOldestFirst = snapshot.UnlockDateAsc;
            _settings.Theme.AchievementsRarityAsc = snapshot.RarityAsc;
            _settings.Theme.AchievementsRarityDesc = snapshot.RarityDesc;

            // Raise PropertyChanged for computed properties that delegate to Theme
            NotifySettingProperties(SingleGameThemeDelegatedProperties);
        }

        private void ApplySingleGameToLegacyTheme(SingleGameSnapshot snapshot)
        {
            _settings.LegacyTheme.HasData = true;
            _settings.LegacyTheme.Total = snapshot.Total;
            _settings.LegacyTheme.Unlocked = snapshot.Unlocked;
            _settings.LegacyTheme.Percent = snapshot.Percent;

            _settings.LegacyTheme.Is100Percent = snapshot.Unlocked == snapshot.Total && snapshot.Total > 0;
            _settings.LegacyTheme.Locked = snapshot.Locked;
            _settings.LegacyTheme.TotalGamerScore = 0;
            _settings.LegacyTheme.EstimateTimeToUnlock = string.Empty;

            _settings.LegacyTheme.ListAchievements = snapshot.AllAchievements;
            _settings.LegacyTheme.ListAchUnlockDateAsc = snapshot.UnlockDateAsc;
            _settings.LegacyTheme.ListAchUnlockDateDesc = snapshot.UnlockDateDesc;

            // Raise PropertyChanged for computed properties that delegate to LegacyTheme
            NotifySettingProperties(SingleGameLegacyDelegatedProperties);
        }

        /// <summary>
        /// Clear per-game theme properties when no game is selected or game has no achievements.
        /// </summary>
        public void ClearSingleGameThemeProperties()
        {
            ClearSingleGameTheme();
            ClearSingleGameLegacyTheme();
        }

        private void ClearSingleGameTheme()
        {
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

            // Raise PropertyChanged for computed properties that delegate to Theme
            NotifySettingProperties(SingleGameThemeDelegatedProperties);
        }

        private void ClearSingleGameLegacyTheme()
        {
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

            // Raise PropertyChanged for computed properties that delegate to LegacyTheme
            NotifySettingProperties(SingleGameLegacyDelegatedProperties);
        }

        #endregion
    }
}



