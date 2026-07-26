using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.Achievements;
using PlayniteAchievements.Services.Cache;
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
        /// Builds the game-level context menu (Refresh, Open in Library, optional Manage Achievements,
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
            ILogger logger)
        {
            var menu = new ContextMenu();
            var hasPlayniteGameId = TryGetGameId(data, out _);
            menu.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_RefreshGame",
                () => ExecuteCommand(refreshGameCommand, data)));

            if (hasPlayniteGameId)
            {
                menu.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_OpenGameInLibrary",
                    () => ExecuteCommand(openGameInLibraryCommand, data)));

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

                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem(resourceOwner, "LOCPlayAch_Menu_ClearData",
                    () => ClearGameData(data, playniteApi, overridesService, cacheManager, logger)));

                TryGetGameId(data, out var menuGameId);
                var excludedFromSummaries = overridesService?.IsExcludedFromSummaries(menuGameId) == true;
                var excludedFromRefreshes = overridesService?.IsExcludedFromRefreshes(menuGameId) == true;

                menu.Items.Add(CreateMenuItem(resourceOwner,
                    excludedFromSummaries
                        ? "LOCPlayAch_Common_Action_IncludeInSummaries"
                        : "LOCPlayAch_Common_Action_ExcludeFromSummaries",
                    () => SetExcludedFromSummaries(data, overridesService, excluded: !excludedFromSummaries)));
                menu.Items.Add(CreateMenuItem(resourceOwner,
                    excludedFromRefreshes
                        ? "LOCPlayAch_Menu_IncludeInRefreshes"
                        : "LOCPlayAch_Menu_ExcludeFromRefreshes",
                    () => SetExcludedFromRefreshes(data, playniteApi, overridesService,
                        excluded: !excludedFromRefreshes, clearDataWhenExcluding: false, refreshGameCommand: null)));
                menu.Items.Add(CreateMenuItem(resourceOwner,
                    excludedFromRefreshes
                        ? "LOCPlayAch_Menu_IncludeInRefreshesAndRefresh"
                        : "LOCPlayAch_Menu_ExcludeFromRefreshesAndClearData",
                    () => SetExcludedFromRefreshes(data, playniteApi, overridesService,
                        excluded: !excludedFromRefreshes, clearDataWhenExcluding: true,
                        refreshGameCommand: refreshGameCommand)));
            }

            return menu;
        }

        public static MenuItem CreateMenuItem(FrameworkElement resourceOwner, string resourceKey, Action onClick)
        {
            var text = resourceOwner?.TryFindResource(resourceKey) as string
                ?? ResourceProvider.GetString(resourceKey)
                ?? resourceKey;
            var item = new MenuItem { Header = text };
            item.Click += (_, __) => onClick?.Invoke();
            return item;
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
