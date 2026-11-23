using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Database;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.ManageAchievements;
using PlayniteAchievements.Views.Dialogs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace PlayniteAchievements.Views.ManageAchievements
{
    /// <summary>
    /// The Achievement Editor window's content: the Manage Achievements editor over every game,
    /// with a Game filter. Carries the editor's writes to the cache and the theme for each game
    /// they touched, which the Manage window does for its one game.
    /// </summary>
    internal sealed class LibraryAchievementEditorControl : UserControl
    {
        private readonly RefreshRuntime _refreshService;
        private readonly PlayniteAchievementsSettings _settings;
        private readonly ILogger _logger;
        private ManageAchievementsEditorViewModel _viewModel;
        private ManageAchievementsEditorTab _tab;
        private Task _iconApplyChain;
        private int _iconAppliesInFlight;
        private bool _selfWriteMarkerClearQueued;

        public LibraryAchievementEditorControl(
            RefreshRuntime refreshService,
            AchievementOverridesService achievementOverridesService,
            AchievementDataService achievementDataService,
            PlayniteAchievementsSettings settings,
            ILogger logger,
            IEnumerable<Guid> lockedGameIds = null)
        {
            _refreshService = refreshService ?? throw new ArgumentNullException(nameof(refreshService));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger;
            if (achievementDataService?.CacheReads == null)
            {
                throw new InvalidOperationException("The achievement database is not available.");
            }

            var plugin = PlayniteAchievementsPlugin.Instance;
            var sources = new EditorLibrarySources(
                achievementDataService.CacheReads,
                achievementDataService,
                new DispatcherReadScheduler(Dispatcher));
            _viewModel = ManageAchievementsEditorViewModel.ForLibrary(
                sources,
                achievementOverridesService,
                plugin?.GameCustomDataStore,
                plugin?.ManagedCustomIconService,
                settings,
                logger,
                plugin?.CustomProviderStore,
                currentValue => plugin?.PickColor(Window.GetWindow(this), currentValue),
                editor => CustomProviderEditorDialog.Show(Window.GetWindow(this), editor),
                lockedGameIds);
            _viewModel.AssignmentsChanged += ViewModel_Persisted;
            _viewModel.CustomAchievementsSaved += ViewModel_Persisted;
            _viewModel.CapstoneChanged += ViewModel_CapstoneChanged;
            _viewModel.IconOverridesSaved += ViewModel_IconOverridesSaved;

            _tab = new ManageAchievementsEditorTab(_viewModel);
            // The window's edge is the tab's edge here; the Manage window sets it inside a card.
            _tab.SetResourceReference(MarginProperty, "PlayAch.Thickness.CardPadding");
            Content = _tab;

            _refreshService.GameCacheUpdated += RefreshService_GameCacheUpdated;
            OpenEditorRegistry.BackgroundWritten += OpenEditorRegistry_BackgroundWritten;
            Loaded += Control_Loaded;

            Common.LeakWatch.Track("LibraryAchievementEditorControl", this);
            Common.LeakWatch.Track("LibraryAchievementEditorViewModel", _viewModel);
        }

        public string WindowTitle => ResourceProvider.GetString("LOCPlayAch_Menu_AchievementEditor");

        /// <summary>Locks the open editor to these games; see <see cref="ManageAchievementsEditorViewModel.LockToGames"/>.</summary>
        public void LockToGames(IEnumerable<Guid> gameIds) => _viewModel?.LockToGames(gameIds);

        private void Control_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= Control_Loaded;
            _viewModel?.SetVisible(true);
        }

        /// <summary>Releases the editor and its subscriptions; called when the window closes.</summary>
        public void Cleanup()
        {
            _refreshService.GameCacheUpdated -= RefreshService_GameCacheUpdated;
            OpenEditorRegistry.BackgroundWritten -= OpenEditorRegistry_BackgroundWritten;
            _tab?.Cleanup();
            if (_viewModel != null)
            {
                _viewModel.AssignmentsChanged -= ViewModel_Persisted;
                _viewModel.CustomAchievementsSaved -= ViewModel_Persisted;
                _viewModel.CapstoneChanged -= ViewModel_CapstoneChanged;
                _viewModel.IconOverridesSaved -= ViewModel_IconOverridesSaved;
                _viewModel.Detach();
            }

            Content = null;
            _tab = null;
            _viewModel = null;
        }

        // The editor already shows its own edit, so the refresh its cascade brings back is skipped
        // until the cascade has run; see ScheduleSelfWriteMarkerClear.
        private void ViewModel_Persisted(object sender, EventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.SuppressExternalRefresh = true;
            Propagate(_viewModel.ConsumeWrittenGames());
            ScheduleSelfWriteMarkerClear();
        }

        private void ViewModel_CapstoneChanged(object sender, CapstoneChangedEventArgs e) => ViewModel_Persisted(sender, e);

        /// <summary>
        /// Tells the cache and the theme that these games' custom data changed, as the Manage
        /// window does for its game: a scoped invalidation routes each through the overview's
        /// per-game path, and the theme rebuilds its lists that carry them.
        /// </summary>
        private void Propagate(IReadOnlyCollection<Guid> gameIds)
        {
            var games = gameIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? new List<Guid>();
            if (games.Count == 0)
            {
                return;
            }

            _refreshService.Cache?.NotifyCacheInvalidated(games);
            var plugin = PlayniteAchievementsPlugin.Instance;
            var selected = _settings.SelectedGame?.Id;
            if (selected.HasValue && games.Contains(selected.Value))
            {
                plugin?.ThemeUpdateService?.RequestUpdate(selected.Value, forceRefresh: true);
            }

            // The library lists are rebuilt once, with the last game: each call would request it.
            for (var index = 0; index < games.Count; index++)
            {
                var gameId = games[index];
                try
                {
                    plugin?.ThemeIntegrationService?.NotifyCustomDataChanged(
                        gameId,
                        refreshLibraryState: index == games.Count - 1);
                }
                catch (Exception ex)
                {
                    _logger?.Debug(ex, "Failed to refresh theme state after achievement editor edits.");
                }
            }
        }

        /// <summary>
        /// Carries changed icon overrides into the cached rows, one apply at a time, as the Manage
        /// window does: of the game the event names, or of the one game the editor lists.
        /// </summary>
        private void ViewModel_IconOverridesSaved(object sender, IconOverridesSavedEventArgs e)
        {
            var gameId = e?.GameId is Guid named && named != Guid.Empty
                ? named
                : _viewModel?.GameId ?? Guid.Empty;
            var apiNames = e?.ChangedApiNames?.ToList();
            if (gameId == Guid.Empty || apiNames == null || apiNames.Count == 0)
            {
                return;
            }

            _iconApplyChain = ApplyIconOverridesAsync(_iconApplyChain, gameId, apiNames);
        }

        private async Task ApplyIconOverridesAsync(Task previous, Guid gameId, IReadOnlyCollection<string> apiNames)
        {
            _iconAppliesInFlight++;
            try
            {
                try
                {
                    await (previous ?? Task.CompletedTask);
                }
                catch
                {
                    // Already logged by the apply that faulted.
                }

                try
                {
                    // Off the dispatcher: the apply ends in a SQLite write that would stall input.
                    await Task.Run(() => _refreshService.ApplyAchievementIconOverridesAsync(gameId, apiNames));
                }
                catch (Exception ex)
                {
                    _logger?.Error(ex, $"Failed applying icon overrides for gameId={gameId}");
                }

                Propagate(new[] { gameId });
            }
            finally
            {
                _iconAppliesInFlight--;
            }
        }

        // A cache write for the game the editor lists whole is reloaded into its rows, unless it
        // is the editor's own; across games the paged rows follow the change feed themselves.
        private void RefreshService_GameCacheUpdated(object sender, GameCacheUpdatedEventArgs e)
        {
            if (Guid.TryParse(e?.GameId, out var gameId))
            {
                DispatchExternalChange(gameId);
            }
        }

        private void OpenEditorRegistry_BackgroundWritten(Guid gameId) => DispatchExternalChange(gameId);

        private void DispatchExternalChange(Guid gameId)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => DispatchExternalChange(gameId)));
                return;
            }

            var viewModel = _viewModel;
            if (viewModel == null || viewModel.IsPagedMode || gameId != viewModel.GameId)
            {
                return;
            }

            if (_iconAppliesInFlight > 0 || viewModel.SuppressExternalRefresh)
            {
                ScheduleSelfWriteMarkerClear();
                return;
            }

            _tab?.RefreshData();
        }

        /// <summary>
        /// Clears the self-write marker once the cascade of the write that set it has run: every
        /// leg of it is synchronous, so a Background callback lands after all of them.
        /// </summary>
        private void ScheduleSelfWriteMarkerClear()
        {
            if (_selfWriteMarkerClearQueued)
            {
                return;
            }

            _selfWriteMarkerClearQueued = true;
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                _selfWriteMarkerClearQueued = false;
                if (_viewModel != null)
                {
                    _viewModel.SuppressExternalRefresh = false;
                }
            }), DispatcherPriority.Background);
        }
    }
}
