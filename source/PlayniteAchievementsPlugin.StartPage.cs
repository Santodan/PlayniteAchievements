using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK.Models;
using PlayniteAchievements.Models;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.StartPage;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;
using PlayniteAchievements.ViewModels.StartPage;
using PlayniteAchievements.Views.Helpers;
using PlayniteAchievements.Views.StartPage;
using PlayniteAchievements.Views.Showcase;
using StartPage.SDK;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin : IStartPageExtension
    {
        private readonly Dictionary<string, IDisposable> _startPageViewModels =
            new Dictionary<string, IDisposable>(StringComparer.Ordinal);
        private StartPageDataCoordinator _startPageDataCoordinator;
        private readonly object _startPageInvalidateSync = new object();
        private System.Threading.Timer _startPageInvalidateTimer;
        private const int StartPageInvalidateDelayMs = 2000;

        private readonly object _retentionDiagnosticsSync = new object();
        private System.Threading.Timer _retentionDiagnosticsTimer;
        private string _pendingRetentionPoint;

        public StartPageExtensionArgs GetAvailableStartPageViews()
        {
            EnsureAchievementResourcesLoaded();

            return new StartPageExtensionArgs
            {
                ExtensionName = L("LOCPlayAch_Title_PluginName"),
                Views = StartPageViewCatalog.Views
                    .Where(view => !view.Hidden)
                    .Select(view => new StartPageViewArgsBase
                    {
                        ViewId = view.ViewId,
                        Name = L(view.NameKey, view.ViewId),
                        Description = string.Empty,
                        HasSettings = view.HasSettings,
                        AllowMultipleInstances = view.AllowMultipleInstances
                    }).ToList()
            };
        }

        public object GetStartPageView(string viewId, Guid instanceId)
        {
            if (!StartPageViewCatalog.TryGetDefinition(viewId, out var definition))
            {
                return null;
            }

            EnsureAchievementResourcesLoaded();

            var sharedSettings = definition.ShowcaseWidgetKind.HasValue
                ? GetOrCreateStartPageWidgetSettings(
                    viewId,
                    instanceId,
                    definition.ShowcaseWidgetKind.Value)
                : null;
            var viewModel = CreateStartPageViewModel(definition, sharedSettings);
            if (viewModel == null)
            {
                return null;
            }

            var view = CreateStartPageView(definition);
            Common.FormattingCulture.Apply(view);
            view.DataContext = viewModel;

            var key = GetStartPageInstanceKey(viewId, instanceId);
            DisposeStartPageViewModel(key);
            _startPageViewModels[key] = viewModel;
            if (viewModel is IStartPageControl startPageControl)
            {
                startPageControl.OnStartPageOpened();
            }

            return view;
        }

        public Control GetStartPageViewSettings(string viewId, Guid instanceId)
        {
            if (!StartPageViewCatalog.TryGetDefinition(viewId, out var definition) ||
                !definition.HasSettings ||
                !definition.ShowcaseWidgetKind.HasValue)
            {
                return null;
            }

            var settings = GetOrCreateStartPageWidgetSettings(
                viewId,
                instanceId,
                definition.ShowcaseWidgetKind.Value);
            if (settings.Kind != ShowcaseWidgetKind.Profile)
            {
                return new ShowcaseWidgetOptionsControl(
                    settings,
                    PersistSettingsForUi);
            }

            // The profile card's identity (name, avatar, links) sits above its display options,
            // as in the Showcase widget dialog; both persist on each edit.
            var profile = settings.Profile ?? (settings.Profile = new ShowcaseProfileSettings());
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(new ShowcaseProfileSettingsEditor(profile, () =>
            {
                PersistSettingsForUi();
                ShowcaseConfigurationEvents.RaiseChanged();
            }));
            panel.Children.Add(new ShowcaseWidgetOptionsControl(
                settings,
                PersistSettingsForUi,
                margin: new Thickness(0),
                loadStyles: false));
            var host = new UserControl { Content = panel };
            host.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/PlayniteAchievements;component/Resources/PlayAchImplicitControlStyles.xaml",
                    UriKind.Absolute)
            });
            Common.FormattingCulture.Apply(host);
            return host;
        }

        public void OnViewRemoved(string viewId, Guid instanceId)
        {
            DisposeStartPageViewModel(GetStartPageInstanceKey(viewId, instanceId));
            var persisted = Settings?.Persisted;
            if (persisted?.Showcase?.StartPageInstances?.Remove(GetStartPageInstanceKey(viewId, instanceId)) == true)
            {
                ShowcaseGridSurfaces.PruneOrphaned(persisted.GridOptions, persisted.Showcase);
                PersistSettingsForUi();
                ShowcaseConfigurationEvents.RaiseChanged();
            }
        }

        internal ContextMenu BuildStartPageRowContextMenu(
            object data,
            FrameworkElement resourceOwner,
            Action onChanged,
            DependencyObject menuSource)
        {
            var menu = BuildStartPageBaseRowContextMenu(data, resourceOwner);
            var appendedAchievementOptions = AchievementRowOptionsMenuBuilder.AppendAchievementOptions(
                menu,
                data,
                resourceOwner,
                onChanged,
                menuSource);
            if (!appendedAchievementOptions)
            {
                // Game rows get no achievement options, which is where display settings are
                // appended for achievement rows; add the entry here so every grid row has it.
                GridDisplaySettingsMenuBuilder.Append(menu, resourceOwner, menuSource);
            }

            return menu.Items.Count > 0 ? menu : null;
        }

        private IDisposable CreateStartPageViewModel(
            StartPageViewDefinition definition,
            ShowcaseWidgetInstanceSettings sharedSettings)
        {
            var widgetKind = definition.WidgetKind;
            if (definition.ShowcaseWidgetKind.HasValue)
            {
                return new StartPageShowcaseWidgetViewModel(
                    sharedSettings,
                    GetStartPageDataCoordinator(),
                    Settings,
                    _logger);
            }

            switch (widgetKind)
            {
                case StartPageWidgetKind.CollectionScoreCard:
                case StartPageWidgetKind.PrestigeScoreCard:
                    return new StartPageScoreCardWidgetViewModel(widgetKind, GetStartPageDataCoordinator(), Settings, _logger);
                default:
                    return null;
            }
        }

        private static Control CreateStartPageView(StartPageViewDefinition definition)
        {
            if (definition.ShowcaseWidgetKind.HasValue)
            {
                return new StartPageShowcaseWidgetView();
            }

            var widgetKind = definition.WidgetKind;
            switch (widgetKind)
            {
                case StartPageWidgetKind.CollectionScoreCard:
                case StartPageWidgetKind.PrestigeScoreCard:
                    return new StartPageScoreCardWidgetView();
                default:
                    return null;
            }
        }

        private StartPageDataCoordinator GetStartPageDataCoordinator()
        {
            if (_startPageDataCoordinator == null)
            {
                _startPageDataCoordinator = new StartPageDataCoordinator(
                    _achievementDataService,
                    _libraryProjectionService,
                    Providers,
                    PlayniteApi,
                    _logger,
                    Settings,
                    () => FriendCacheManager?.LoadCurrentUserIdentities());
            }

            return _startPageDataCoordinator;
        }

        private void InvalidateStartPageData()
        {
            _startPageDataCoordinator?.Invalidate();
        }

        // Reports what the process-lifetime caches still hold once a refresh has settled, so
        // residual memory can be attributed to a specific retainer instead of inferred from
        // the process total. Delayed past the post-refresh delta/projection work and the LOH
        // compaction so it measures the resting state, not the peak.
        private void ScheduleRetentionDiagnostics()
        {
            ScheduleRetentionDiagnostics("refresh.settled", 20);
        }

        /// <summary>
        /// Logs a retention report once the named point has settled. The delay lets deferred
        /// UI work (dispatcher teardown, weak-reference queues) finish, so the forced
        /// collection measures what is genuinely still rooted.
        /// </summary>
        internal void ScheduleRetentionDiagnostics(string point, int delaySeconds)
        {
            // RetentionReportEnabled, not Enabled: this schedules two blocking gen2 collections
            // plus a full cache-and-LeakWatch census, so a timing-only build must not arm it.
            if (!Common.MemoryDiagnostics.RetentionReportEnabled)
            {
                return;
            }

            // Trailing-edge coalescer, the same shape as ScheduleStartPageInvalidate. This is
            // scheduled off every cache invalidation, and a burst of them -- one per custom-data
            // edit -- used to queue one independent delayed task each, so the forced collections
            // arrived as a storm one delay later. Collapsing the burst into a single report is
            // what the report wanted anyway: it measures the resting state after things settle.
            var delayMs = Math.Max(1, delaySeconds) * 1000;
            lock (_retentionDiagnosticsSync)
            {
                // Last writer wins. When a deliberate one-shot point ("manage.closed",
                // "overview.closed") collides with a refresh.settled burst, that point is the
                // later and more interesting event, so naming the report after it is correct.
                _pendingRetentionPoint = point;

                if (_retentionDiagnosticsTimer == null)
                {
                    _retentionDiagnosticsTimer = new System.Threading.Timer(
                        _ => FlushRetentionDiagnostics(),
                        null,
                        delayMs,
                        System.Threading.Timeout.Infinite);
                }
                else
                {
                    _retentionDiagnosticsTimer.Change(delayMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        private void FlushRetentionDiagnostics()
        {
            string point;
            lock (_retentionDiagnosticsSync)
            {
                point = _pendingRetentionPoint;
                _pendingRetentionPoint = null;
            }

            if (point == null)
            {
                return;
            }

            try
            {
                LogRetentionDiagnostics(point);
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Retention diagnostics failed.");
            }
        }

        private void LogRetentionDiagnostics(string point)
        {
            var detail = new System.Text.StringBuilder();

            try
            {
                if (_imageService != null)
                {
                    _imageService.GetCacheStats(out var imageCount, out var imageBytes);
                    detail.Append($"images={imageCount}/{imageBytes / (1024 * 1024)}MB ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read image cache stats.");
            }

            try
            {
                if (_cacheManager is Services.Cache.CacheManager concreteCache)
                {
                    concreteCache.GetMemoryCacheStats(out var cachedGames, out var cachedAchievements);
                    detail.Append($"gameLru={cachedGames}games/{cachedAchievements}ach ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read game cache stats.");
            }

            try
            {
                detail.Append($"projections={_libraryProjectionService?.DescribeCachedProjections() ?? "n/a"} ");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read projection cache stats.");
            }

            try
            {
                if (_achievementDataService != null)
                {
                    _achievementDataService.GetOverviewMemoStats(out var memoEntries, out var memoRows);
                    detail.Append($"summaryMemo={memoEntries}/{memoRows}rows ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read summary memo stats.");
            }

            try
            {
                if (_startPageDataCoordinator != null)
                {
                    _startPageDataCoordinator.GetRetentionStats(out var hasSnapshot, out var achRows, out var gameRows);
                    detail.Append($"sharedSnapshot={(hasSnapshot ? "yes" : "no")}/{achRows}ach/{gameRows}games ");
                }
                else
                {
                    detail.Append("sharedSnapshot=none ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read shared snapshot stats.");
            }

            try
            {
                var modern = _settingsViewModel?.Settings?.ModernTheme;
                if (modern != null)
                {
                    detail.Append(
                        $"themeLists={modern.AllAchievementsUnlockDesc?.Count ?? 0}ach/" +
                        $"{modern.GameSummariesDesc?.Count ?? 0}games ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read theme list stats.");
            }

            try
            {
                detail.Append(
                    "appearanceSubs=" +
                    Models.Achievements.RarityAppearanceHelper.AppearanceChangedSubscriberCount + " ");
                // Broken down by owning type: these handlers are instance methods on visual
                // elements, so a stranded one roots its whole ancestor chain and DataContext. The
                // count alone cannot say which type to go and look at.
                detail.Append(
                    "appearanceSubsBy=" +
                    Models.Achievements.RarityAppearanceHelper.DescribeAppearanceChangedSubscribers() + " ");
                detail.Append(
                    "raySubs=" + Views.Helpers.RayAnimationDriver.SubscriberCount + " ");
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read appearance subscriber count.");
            }

            // What each live surface is holding, reported by the surface itself. The caches above
            // are the ones this file can reach; a reported session grew the managed heap by
            // 790 MB with every one of them flat, so the surfaces that could not be reached were
            // exactly where the retained memory had to be.
            try
            {
                var probes = Common.RetentionProbes.Describe();
                if (!string.IsNullOrWhiteSpace(probes))
                {
                    detail.Append(probes + " ");
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to read retention probes.");
            }

            // Live instance counts come last: they are the discriminator when every cache
            // above reads flat but the heap still grows.
            detail.Append($"live={Common.LeakWatch.DescribeLive()}");

            Common.MemoryDiagnostics.LogRetained(_logger, point, detail.ToString().TrimEnd());
        }

        // Trailing coalescer for high-frequency invalidation sources: per-game refresh saves,
        // playtime ItemUpdated ticks, and library-sync bursts. Each invalidation makes every
        // live start-page widget re-pull (and, with no publisher attached, rebuild) a full
        // library snapshot, so bursts must collapse into one. Invalidate() is thread-safe;
        // no dispatcher marshaling is needed. Low-frequency user-driven changes keep calling
        // InvalidateStartPageData() directly.
        private void ScheduleStartPageInvalidate()
        {
            lock (_startPageInvalidateSync)
            {
                if (_startPageDataCoordinator == null)
                {
                    return;
                }

                if (_startPageInvalidateTimer == null)
                {
                    _startPageInvalidateTimer = new System.Threading.Timer(
                        _ => InvalidateStartPageData(),
                        null,
                        StartPageInvalidateDelayMs,
                        System.Threading.Timeout.Infinite);
                }
                else
                {
                    _startPageInvalidateTimer.Change(StartPageInvalidateDelayMs, System.Threading.Timeout.Infinite);
                }
            }
        }

        internal void InvalidateStartPageDataForUi()
        {
            InvalidateStartPageData();
        }

        private void DisposeStartPageViews()
        {
            foreach (var viewModel in _startPageViewModels.Values.ToList())
            {
                try
                {
                    viewModel?.Dispose();
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to dispose StartPage widget view model.");
                }
            }

            _startPageViewModels.Clear();

            lock (_startPageInvalidateSync)
            {
                _startPageInvalidateTimer?.Dispose();
                _startPageInvalidateTimer = null;
            }

            lock (_retentionDiagnosticsSync)
            {
                _retentionDiagnosticsTimer?.Dispose();
                _retentionDiagnosticsTimer = null;
                _pendingRetentionPoint = null;
            }

            try
            {
                _startPageDataCoordinator?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose StartPage data coordinator.");
            }

            _startPageDataCoordinator = null;
        }

        private void DisposeStartPageViewModel(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            if (!_startPageViewModels.TryGetValue(key, out var viewModel))
            {
                return;
            }

            _startPageViewModels.Remove(key);
            try
            {
                viewModel?.Dispose();
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, "Failed to dispose StartPage widget view model.");
            }
        }

        private ContextMenu BuildStartPageBaseRowContextMenu(object data, FrameworkElement resourceOwner)
        {
            var menu = new ContextMenu();
            if (!TryGetStartPageGameId(data, out var gameId))
            {
                return menu;
            }

            if (data is GameSummaryItem)
            {
                AddStartPageGameRowMenuItems(
                    menu,
                    gameId,
                    resourceOwner,
                    includeShowcasePin: !(data is FriendGameSummaryItem));
                return menu;
            }

            if (data is FriendAchievementDisplayItem)
            {
                if (Settings.Persisted.EnableFriendsFeatures)
                {
                    menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_ViewFriendsAchievements",
                        () => OpenViewFriendsAchievementsWindow(gameId)));
                }
                menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_ViewAchievements",
                    () => OpenViewAchievementsWindow(gameId)));
                menu.Items.Add(CreateStartPageOpenMenu(resourceOwner, gameId));
                return menu;
            }

            if (data is AchievementDisplayItem || data is RecentAchievementItem)
            {
                menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_ViewAchievements",
                    () => OpenViewAchievementsWindow(gameId)));
                menu.Items.Add(CreateStartPageOpenMenu(resourceOwner, gameId));
            }

            return menu;
        }

        private void AddStartPageGameRowMenuItems(
            ContextMenu menu,
            Guid gameId,
            FrameworkElement resourceOwner,
            bool includeShowcasePin)
        {
            menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_ViewAchievements",
                () => OpenViewAchievementsWindow(gameId)));

            if (!IsRefreshInProgress())
            {
                menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_RefreshGame",
                    () => _ = StartMenuRefreshAsync(
                        new RefreshRequest
                        {
                            Mode = RefreshModeType.Single,
                            SingleGameId = gameId,
                            SurfaceUserNotices = true
                        },
                        gameId)));
            }

            menu.Items.Add(CreateStartPageOpenMenu(resourceOwner, gameId));
            menu.Items.Add(CreateStartPageMenuItem(resourceOwner, "LOCPlayAch_Menu_ManageAchievements",
                () => OpenManageAchievementsView(gameId)));

            if (includeShowcasePin)
            {
                ShowcasePinMenuBuilder.AppendGameMenu(menu, resourceOwner, gameId);
            }

            menu.Items.Add(new Separator());

            var game = PlayniteApi?.Database?.Games?.Get(gameId);
            if (game != null)
            {
                // Same Maintenance grouping as the Overview's game rows.
                menu.Items.Add(GameRowContextMenuBuilder.CreateMaintenanceMenu(
                    resourceOwner,
                    IsGameExcludedFromSummaries(gameId),
                    IsGameExcluded(gameId),
                    () => ClearSingleGameData(game),
                    () => ToggleExcludedFromSummaries(new[] { game }),
                    () => ToggleExcludedFromRefreshes(
                        new[] { game },
                        clearDataWhenExcluding: false,
                        confirmWhenClearingData: false),
                    () => ToggleExcludedFromRefreshesAndRefresh(new[] { game })));
            }
        }

        private MenuItem CreateStartPageOpenMenu(FrameworkElement resourceOwner, Guid gameId)
        {
            return GameRowContextMenuBuilder.CreateOpenMenu(
                resourceOwner,
                gameId,
                () => OpenGameInLibrary(gameId),
                PlayniteApi,
                _logger);
        }

        private static MenuItem CreateStartPageMenuItem(
            FrameworkElement resourceOwner,
            string resourceKey,
            Action onClick)
        {
            var item = new MenuItem
            {
                Header = resourceOwner?.TryFindResource(resourceKey) as string
                         ?? ResourceProvider.GetString(resourceKey)
                         ?? resourceKey
            };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        /// <summary>Restores the main window and selects the game in the library view.</summary>
        internal void OpenGameInLibrary(Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            try
            {
                PlayniteUiProvider.RestoreMainView();
                PlayniteApi?.MainView?.SelectGame(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to open game in Playnite library: {gameId}");
            }
        }

        private static bool TryGetStartPageGameId(object data, out Guid gameId)
        {
            switch (data)
            {
                case GameSummaryItem game when game.PlayniteGameId.HasValue:
                    gameId = game.PlayniteGameId.Value;
                    return true;
                case AchievementDisplayItem achievement when achievement.PlayniteGameId.HasValue:
                    gameId = achievement.PlayniteGameId.Value;
                    return true;
                case RecentAchievementItem recent when recent.PlayniteGameId.HasValue:
                    gameId = recent.PlayniteGameId.Value;
                    return true;
                case Guid id when id != Guid.Empty:
                    gameId = id;
                    return true;
                default:
                    gameId = Guid.Empty;
                    return false;
            }
        }

        private static string GetStartPageInstanceKey(string viewId, Guid instanceId)
        {
            return $"{viewId}:{instanceId:N}";
        }

        private ShowcaseWidgetInstanceSettings GetOrCreateStartPageWidgetSettings(
            string viewId,
            Guid instanceId,
            ShowcaseWidgetKind kind)
        {
            var showcase = Settings.Persisted.Showcase;
            var key = GetStartPageInstanceKey(viewId, instanceId);
            if (showcase.StartPageInstances.TryGetValue(key, out var existing) &&
                existing != null &&
                existing.Kind == kind)
            {
                return existing;
            }

            var settings = ShowcaseWidgetSettingsFactory.CreateDefault(
                kind,
                instanceId.ToString("N"));

            if (kind == ShowcaseWidgetKind.IconMosaic ||
                kind == ShowcaseWidgetKind.RecentAchievements)
            {
                ShowcaseWidgetOptions.SetPinCollectionId(
                    settings,
                    showcase.DefaultAchievementPinCollectionId);
            }
            else if (kind == ShowcaseWidgetKind.GameSummaries)
            {
                ShowcaseWidgetOptions.SetPinCollectionId(
                    settings,
                    showcase.DefaultGamePinCollectionId);
            }

            // The four pie views share the showcase Pie kind; each seeds the distribution
            // its view id has always shown.
            if (kind == ShowcaseWidgetKind.Pie)
            {
                ShowcaseWidgetOptions.SetPieMode(settings, ResolvePieModeForView(viewId));
            }

            // Migration: StartPage-hosted grid widgets used to share the fixed StartPage
            // surfaces edited on the Display tab. Seed each new per-instance surface from
            // the matching fixed surface so already-placed widgets keep their configured
            // display options and column layout.
            var catalog = Settings.Persisted.GridOptions;
            var surfaceKey = ShowcaseGridSurfaces.ResolveWidgetSurface(kind, settings.InstanceId);
            if (kind == ShowcaseWidgetKind.RecentAchievements)
            {
                catalog.SeedAchievementFrom(surfaceKey, GridOptionKeys.Achievement.StartPageRecent);
            }
            else if (kind == ShowcaseWidgetKind.GameSummaries)
            {
                catalog.SeedGameSummariesFrom(surfaceKey, GridOptionKeys.GameSummaries.StartPage);
            }

            showcase.StartPageInstances[key] = settings;
            PersistSettingsForUi();
            return settings;
        }

        private static ShowcasePieMode ResolvePieModeForView(string viewId)
        {
            switch (viewId)
            {
                case StartPageViewCatalog.ProviderPieViewId:
                    return ShowcasePieMode.Provider;
                case StartPageViewCatalog.RarityPieViewId:
                    return ShowcasePieMode.Rarity;
                case StartPageViewCatalog.TrophyPieViewId:
                    return ShowcasePieMode.Trophy;
                default:
                    return ShowcasePieMode.CompletedGames;
            }
        }

        private static string L(string key)
        {
            return ResourceProvider.GetString(key);
        }

        private static string L(string key, string fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return fallback;
            }

            var value = ResourceProvider.GetString(key);
            return string.IsNullOrWhiteSpace(value) || string.Equals(value, key, StringComparison.Ordinal)
                ? fallback
                : value;
        }
    }
}
