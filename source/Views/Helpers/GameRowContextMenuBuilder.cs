using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.Showcase;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.ViewModels.Items;

namespace PlayniteAchievements.Views.Helpers
{
    /// <summary>
    /// Builds the per-game right-click context menu shared by the Overview window and the
    /// View Achievements window. Parameterized by the host's commands and services so neither
    /// control depends on the other.
    /// </summary>
    internal static class GameRowContextMenuBuilder
    {
        /// <summary>
        /// Builds the game-level context menu (Refresh, Open, optional Manage Achievements,
        /// Clear Data, Exclude from Summaries/Refreshes). The Manage Achievements item is omitted when
        /// <paramref name="openManageAchievements"/> is null (e.g. when already inside that window).
        /// </summary>
        public static ContextMenu BuildGameMenu(
            object data,
            FrameworkElement resourceOwner,
            ICommand refreshGameCommand,
            ICommand openGameInLibraryCommand,
            Action<Guid> openManageAchievements,
            IPlayniteAPI playniteApi,
            AchievementOverridesService overridesService,
            ICacheManager cacheManager,
            ILogger logger,
            DependencyObject menuSource,
            bool includeViewCaptures = false)
        {
            var menu = new ContextMenu();
            var hasPlayniteGameId = TryGetGameId(data, out var menuGameId);
            menu.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_RefreshGame",
                () => ExecuteCommand(refreshGameCommand, data)));

            if (hasPlayniteGameId)
            {
                menu.Items.Add(CreateOpenMenu(
                    resourceOwner,
                    menuGameId,
                    () => ExecuteCommand(openGameInLibraryCommand, data),
                    playniteApi,
                    logger));

                if (openManageAchievements != null)
                {
                    menu.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_ManageAchievements", () =>
                    {
                        if (TryGetGameId(data, out var gameId))
                        {
                            openManageAchievements(gameId);
                        }
                    }));
                }

                // User-earned scopes only (opted in by the caller); disabled when the game has no
                // saved captures. Friend game rows never opt in.
                if (includeViewCaptures && data is GameSummaryItem gameSummary && !(data is FriendGameSummaryItem))
                {
                    var captureItem = CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_ViewCaptures",
                        () => PlayniteAchievementsPlugin.Instance?.OpenCapturesViewer(gameSummary));
                    captureItem.IsEnabled = PlayniteAchievementsPlugin.Instance?.CaptureLibraryService?
                        .GameHasCaptures(gameSummary.GameName) == true;
                    menu.Items.Add(captureItem);
                }

                if (!(data is FriendGameSummaryItem) &&
                    TryGetGameId(data, out var showcaseGameId))
                {
                    ShowcasePinMenuBuilder.AppendGameMenu(
                        menu,
                        resourceOwner,
                        showcaseGameId);
                }

                menu.Items.Add(new Separator());

                var excludedFromSummaries = overridesService?.IsExcludedFromSummaries(menuGameId) == true;
                var excludedFromRefreshes = overridesService?.IsExcludedFromRefreshes(menuGameId) == true;

                menu.Items.Add(CreateMaintenanceMenu(
                    resourceOwner,
                    excludedFromSummaries,
                    excludedFromRefreshes,
                    () => ClearGameData(data, playniteApi, overridesService, cacheManager, logger),
                    () => SetExcludedFromSummaries(data, overridesService, excluded: !excludedFromSummaries),
                    () => SetExcludedFromRefreshes(data, playniteApi, overridesService,
                        excluded: !excludedFromRefreshes, clearDataWhenExcluding: false, refreshGameCommand: null),
                    () => SetExcludedFromRefreshes(data, playniteApi, overridesService,
                        excluded: !excludedFromRefreshes, clearDataWhenExcluding: true,
                        refreshGameCommand: refreshGameCommand)));
            }

            // Required rather than optional: a call site that forgets the row would lose the
            // display settings entry silently, so the compiler asks for it.
            GridDisplaySettingsMenuBuilder.Append(menu, resourceOwner, menuSource);
            return menu;
        }

        /// <summary>
        /// The Maintenance submenu shared by every game row menu, grouping the destructive and
        /// rarely-used data actions. Each host supplies its own actions; the grouping, order and
        /// toggle labels live only here.
        /// </summary>
        public static MenuItem CreateMaintenanceMenu(
            FrameworkElement resourceOwner,
            bool excludedFromSummaries,
            bool excludedFromRefreshes,
            Action clearData,
            Action toggleExcludedFromSummaries,
            Action toggleExcludedFromRefreshes,
            Action toggleExcludedFromRefreshesWithData)
        {
            var maintenance = new MenuItem
            {
                Header = ResolveHeader(resourceOwner, "LOCPlayAch_Settings_Maintenance_Title")
            };
            maintenance.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_ClearData", clearData));
            maintenance.Items.Add(CreateMenuItem(resourceOwner,
                excludedFromSummaries
                    ? "LOCPlayAch_Common_Action_IncludeInSummaries"
                    : "LOCPlayAch_Common_Action_ExcludeFromSummaries",
                toggleExcludedFromSummaries));
            maintenance.Items.Add(CreateMenuItem(resourceOwner,
                excludedFromRefreshes
                    ? "LOCPlayAch_Menu_IncludeInRefreshes"
                    : "LOCPlayAch_Menu_ExcludeFromRefreshes",
                toggleExcludedFromRefreshes));
            maintenance.Items.Add(CreateMenuItem(resourceOwner,
                excludedFromRefreshes
                    ? "LOCPlayAch_Menu_IncludeInRefreshesAndRefresh"
                    : "LOCPlayAch_Menu_ExcludeFromRefreshesAndClearData",
                toggleExcludedFromRefreshesWithData));
            return maintenance;
        }

        /// <summary>
        /// Builds the "Open" submenu shared by every game row menu: "Game" launches the game through
        /// Playnite, "Library" runs the caller's existing select-in-library action. "Game" is disabled
        /// when the game is not installed, where IPlayniteAPI.StartGame starts the install flow instead
        /// of launching.
        /// </summary>
        public static MenuItem CreateOpenMenu(
            FrameworkElement resourceOwner,
            Guid gameId,
            Action openInLibrary,
            IPlayniteAPI playniteApi,
            ILogger logger)
        {
            var openMenu = new MenuItem { Header = ResolveHeader(resourceOwner, "LOCOpen") };

            var gameItem = CreateMenuItem(resourceOwner, "LOCPlayAch_Column_Game",
                () => StartGame(playniteApi, gameId, logger));
            gameItem.IsEnabled = IsGameInstalled(playniteApi, gameId);
            openMenu.Items.Add(gameItem);

            openMenu.Items.Add(CreateMenuItem(resourceOwner, "LOCLibrary", () => openInLibrary?.Invoke()));
            return openMenu;
        }

        public static MenuItem CreateMenuItem(FrameworkElement resourceOwner, string resourceKey, Action onClick)
        {
            var item = new MenuItem { Header = ResolveHeader(resourceOwner, resourceKey) };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
        }

        private static string ResolveHeader(FrameworkElement resourceOwner, string resourceKey)
        {
            return resourceOwner?.TryFindResource(resourceKey) as string
                ?? ResourceProvider.GetString(resourceKey)
                ?? resourceKey;
        }

        private static bool IsGameInstalled(IPlayniteAPI playniteApi, Guid gameId)
        {
            if (gameId == Guid.Empty)
            {
                return false;
            }

            return (playniteApi ?? API.Instance)?.Database?.Games?.Get(gameId)?.IsInstalled == true;
        }

        private static void StartGame(IPlayniteAPI playniteApi, Guid gameId, ILogger logger)
        {
            if (gameId == Guid.Empty)
            {
                return;
            }

            try
            {
                (playniteApi ?? API.Instance)?.StartGame(gameId);
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"Failed to start game: {gameId}");
            }
        }

        public static void ExecuteCommand(ICommand command, object parameter)
        {
            if (command != null && command.CanExecute(parameter))
            {
                command.Execute(parameter);
            }
        }

        public static bool TryGetGameId(object data, out Guid gameId)
        {
            switch (data)
            {
                case GameSummaryItem game when game.PlayniteGameId.HasValue:
                    gameId = game.PlayniteGameId.Value; return true;
                case AchievementDisplayItem ach when ach.PlayniteGameId.HasValue:
                    gameId = ach.PlayniteGameId.Value; return true;
                case RecentAchievementItem recent when recent.PlayniteGameId.HasValue:
                    gameId = recent.PlayniteGameId.Value; return true;
                case Guid id when id != Guid.Empty:
                    gameId = id; return true;
                default:
                    gameId = Guid.Empty; return false;
            }
        }

        private static void ClearGameData(
            object data,
            IPlayniteAPI playniteApi,
            AchievementOverridesService overridesService,
            ICacheManager cacheManager,
            ILogger logger)
        {
            if (!TryGetGameId(data, out var gameId))
            {
                return;
            }

            var game = playniteApi?.Database?.Games?.Get(gameId);
            if (game == null)
            {
                return;
            }

            var result = playniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSingle"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (overridesService != null)
                {
                    overridesService.ClearGameData(game.Id, game.Name);
                }
                else
                {
                    cacheManager?.RemoveGameCache(game.Id);
                }

                playniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                logger?.Error(ex, $"Failed to clear data for game '{game.Name}' ({game.Id}).");
                playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void SetExcludedFromSummaries(object data, AchievementOverridesService overridesService, bool excluded)
        {
            if (!TryGetGameId(data, out var gameId))
            {
                return;
            }

            overridesService?.SetExcludedFromSummaries(gameId, excluded);
        }

        private static void SetExcludedFromRefreshes(
            object data,
            IPlayniteAPI playniteApi,
            AchievementOverridesService overridesService,
            bool excluded,
            bool clearDataWhenExcluding,
            ICommand refreshGameCommand)
        {
            if (!TryGetGameId(data, out var gameId))
            {
                return;
            }

            var game = playniteApi?.Database?.Games?.Get(gameId);
            if (game == null)
            {
                return;
            }

            if (!excluded)
            {
                overridesService?.SetExcludedByUser(gameId, excluded: false, clearCachedDataWhenExcluding: false);

                // "Include in Refreshes and Refresh" re-includes then refreshes the game.
                if (refreshGameCommand != null)
                {
                    ExecuteCommand(refreshGameCommand, data);
                }

                return;
            }

            if (clearDataWhenExcluding)
            {
                var result = playniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            overridesService?.SetExcludedByUser(
                gameId,
                excluded: true,
                clearCachedDataWhenExcluding: clearDataWhenExcluding);
        }
    }
}
