using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Services.Cache;
using PlayniteAchievements.Services.GameCustomData;
using Playnite.SDK;

namespace PlayniteAchievements
{
    /// <summary>What a maintenance clear removes from a game.</summary>
    internal enum DataClear
    {
        /// <summary>The cached achievements and icons, with the metadata stored against them.</summary>
        Platform,

        /// <summary>The values taken from metadata sources, fetched again by a refresh.</summary>
        Metadata,

        /// <summary>The user's custom data.</summary>
        Custom,

        /// <summary>Platform data, metadata and custom data.</summary>
        All
    }

    /// <summary>
    /// Per-layer maintenance: clearing a game's platform data, metadata, custom data or all of it,
    /// shared by the game menu, the plugin's game row menus, Settings and Manage Achievements.
    /// </summary>
    public partial class PlayniteAchievementsPlugin
    {
        private const string ManualProviderKey = "Manual";

        /// <summary>Every clear with its menu label, in layer order, for each Maintenance menu.</summary>
        internal static readonly IReadOnlyList<(DataClear Kind, string LabelKey)> DataClears = new[]
        {
            (DataClear.Platform, "LOCPlayAch_Menu_ClearPlatformData"),
            (DataClear.Metadata, "LOCPlayAch_Menu_ClearMetadata"),
            (DataClear.Custom, "LOCPlayAch_ManageAchievements_Overrides_ClearCustomDataButton"),
            (DataClear.All, "LOCPlayAch_Menu_ClearAllData")
        };

        /// <summary>
        /// Asks to confirm <paramref name="kind"/> for <paramref name="games"/> and runs it.
        /// Returns whether it ran.
        /// </summary>
        internal bool ClearData(DataClear kind, IReadOnlyCollection<(Guid Id, string Name)> games)
        {
            var targets = (games ?? Array.Empty<(Guid Id, string Name)>())
                .Where(game => game.Id != Guid.Empty)
                .GroupBy(game => game.Id)
                .Select(group => group.First())
                .ToList();
            if (kind == DataClear.Custom)
            {
                return ClearCustomData(targets);
            }

            if (targets.Count == 0)
            {
                return false;
            }

            var keys = kind == DataClear.Platform ? "LOCPlayAch_Menu_ClearPlatformData"
                : kind == DataClear.Metadata ? "LOCPlayAch_Menu_ClearMetadata"
                : "LOCPlayAch_Menu_ClearAllData";
            if (!ConfirmMaintenance(targets.Count == 1
                    ? string.Format(ResourceProvider.GetString(keys + "_ConfirmSingle"), targets[0].Name)
                    : string.Format(ResourceProvider.GetString(keys + "_ConfirmSelected"), targets.Count)))
            {
                return false;
            }

            var ids = targets.Select(game => game.Id).ToList();
            return RunMaintenance(() =>
            {
                switch (kind)
                {
                    case DataClear.Platform:
                        ids.ForEach(id => _cacheManager?.RemoveGameCache(id));
                        break;
                    case DataClear.Metadata:
                        ClearMetadataAndRefetch(ids);
                        break;
                    default:
                        foreach (var id in ids)
                        {
                            _cacheManager?.RemoveGameCache(id);
                            _refreshService?.MetadataOverlay?.Links?.ForgetApplied(id);
                            DeleteGameCustomData(id);
                        }

                        break;
                }
            }, "clear " + kind.ToString().ToLowerInvariant() + " data");
        }

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
            if (GameCustomDataStore == null || current == null)
            {
                return false;
            }

            if (WouldStrandManualAchievements(gameId, current))
            {
                _achievementOverridesService?.ClearGameData(gameId, gameName);
            }

            DeleteGameCustomData(gameId);
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

        private bool ClearCustomData(IReadOnlyList<(Guid Id, string Name)> games)
        {
            var store = GameCustomDataStore;
            var targets = games
                .Select(game => (Game: game, Data: store != null && store.TryLoad(game.Id, out var data) ? data : null))
                .Where(target => target.Data != null)
                .ToList();
            if (targets.Count == 0)
            {
                return false;
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
                return false;
            }

            return RunMaintenance(() =>
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

        private void DeleteGameCustomData(Guid gameId)
        {
            GameCustomDataStore?.Delete(gameId);
            try
            {
                GameDataLinks?.Unlink(gameId);
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed ending the Workshop record of gameId={gameId}.");
            }
        }

        internal bool ConfirmMaintenance(string message)
        {
            return (PlayniteApi?.Dialogs?.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None) == MessageBoxResult.Yes;
        }

        internal bool RunMaintenance(Action action, string what)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to {what}.");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return false;
            }
        }
    }
}
