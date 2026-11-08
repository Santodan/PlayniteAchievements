using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements
{
    /// <summary>
    /// Per-layer maintenance: clearing a game's metadata or its custom data, shared by the game
    /// menu, Settings and Manage Achievements.
    /// </summary>
    public partial class PlayniteAchievementsPlugin
    {
        private const string ManualProviderKey = "Manual";

        /// <summary>
        /// Clears the metadata layer of <paramref name="gameIds"/>, or of every game when null,
        /// forgets their automatic links and applied state, and refreshes the games that had
        /// metadata so their sources are searched again.
        /// </summary>
        internal void ClearMetadataAndRefetch(IReadOnlyCollection<Guid> gameIds)
        {
            var cleared = (_cacheManager as CacheManager)?.ClearMetadataLayers(gameIds) ?? Array.Empty<Guid>();
            var links = _refreshService?.MetadataOverlay?.Links;
            foreach (var gameId in (IEnumerable<Guid>)gameIds ?? cleared)
            {
                links?.ForgetApplied(gameId);
            }

            var toRefresh = ((IEnumerable<Guid>)gameIds ?? cleared).Distinct().ToList();
            if (toRefresh.Count > 0)
            {
                _ = StartMenuRefreshAsync(new RefreshRequest { GameIds = toRefresh });
            }
        }

        /// <summary>
        /// Deletes a game's custom data. A manual link is the game's source of achievements, so
        /// when the cache holds achievements it produced, those go with it. Returns whether the
        /// change needs a refresh.
        /// </summary>
        internal bool ClearGameCustomData(Guid gameId, string gameName, Models.Settings.GameCustomDataFile current)
        {
            var store = GameCustomDataStore;
            if (store == null || current == null)
            {
                return false;
            }

            if (WouldStrandManualAchievements(gameId, current))
            {
                _achievementOverridesService?.ClearGameData(gameId, gameName);
            }

            store.Delete(gameId);
            try
            {
                GameDataLinks?.Unlink(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed ending the Workshop record of gameId={gameId}.");
            }

            return CustomDataTransition.Analyze(current, null).RequiresRefresh;
        }

        /// <summary>Whether clearing the game's custom data also removes achievements its manual link produced.</summary>
        internal bool WouldStrandManualAchievements(Guid gameId, Models.Settings.GameCustomDataFile current)
        {
            if (current?.ManualLink == null)
            {
                return false;
            }

            var raw = _cacheManager?.LoadGameData(gameId.ToString());
            return raw?.Achievements?.Count > 0 &&
                   string.Equals(raw.ProviderKey, ManualProviderKey, StringComparison.OrdinalIgnoreCase);
        }

        private void ClearMetadataFromMenu(IReadOnlyList<Game> games)
        {
            if (games.Count == 0 || !ConfirmMaintenance(games.Count == 1
                    ? string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearMetadata_ConfirmSingle"), games[0].Name)
                    : string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearMetadata_ConfirmSelected"), games.Count)))
            {
                return;
            }

            RunMaintenance(() => ClearMetadataAndRefetch(games.Select(game => game.Id).ToList()), "clear metadata");
        }

        private void ClearCustomDataFromMenu(IReadOnlyList<Game> games)
        {
            var store = GameCustomDataStore;
            var targets = games
                .Select(game => (Game: game, Data: store != null && store.TryLoad(game.Id, out var data) ? data : null))
                .Where(target => target.Data != null)
                .ToList();
            if (targets.Count == 0)
            {
                return;
            }

            var strands = targets.Any(target => WouldStrandManualAchievements(target.Game.Id, target.Data));
            var message = targets.Count == 1
                ? string.Format(
                    ResourceProvider.GetString(strands
                        ? "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataConfirmManual"
                        : "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataConfirm"),
                    targets[0].Game.Name)
                : string.Format(
                    ResourceProvider.GetString(strands
                        ? "LOCPlayAch_Menu_ClearCustomData_ConfirmSelectedManual"
                        : "LOCPlayAch_Menu_ClearCustomData_ConfirmSelected"),
                    targets.Count);
            if (!ConfirmMaintenance(message))
            {
                return;
            }

            RunMaintenance(() =>
            {
                var refresh = targets
                    .Where(target => ClearGameCustomData(target.Game.Id, target.Game.Name, target.Data))
                    .Select(target => target.Game.Id)
                    .ToList();
                if (refresh.Count > 0)
                {
                    _ = StartMenuRefreshAsync(new RefreshRequest { GameIds = refresh });
                }
            }, "clear custom data");
        }

        internal bool ConfirmMaintenance(string message)
        {
            return (PlayniteApi?.Dialogs?.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None) == MessageBoxResult.Yes;
        }

        internal void RunMaintenance(Action action, string what)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to {what}.");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
    }
}
