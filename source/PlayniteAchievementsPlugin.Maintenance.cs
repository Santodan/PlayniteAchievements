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
    /// <summary>The layers a maintenance clear removes from a game.</summary>
    internal enum DataClear
    {
        /// <summary>Cached achievements, unlocks and icons, with the metadata stored against them.</summary>
        Platform,

        /// <summary>The values taken from metadata sources, fetched again by a refresh.</summary>
        Metadata,

        /// <summary>The user's custom data.</summary>
        Custom,

        /// <summary>Platform data, metadata and custom data.</summary>
        All
    }

    /// <summary>
    /// Per-layer maintenance: each clear removes one layer of the given games, or of every game,
    /// and nothing else. Shared by the game menu, the plugin's game row menus, Settings and
    /// Manage Achievements.
    /// </summary>
    public partial class PlayniteAchievementsPlugin
    {
        /// <summary>Every clear, in layer order, for each Maintenance menu.</summary>
        internal static readonly IReadOnlyList<DataClear> DataClears = new[]
        {
            DataClear.Platform,
            DataClear.Metadata,
            DataClear.Custom,
            DataClear.All
        };

        /// <summary>The menu label key of a clear.</summary>
        internal static string ClearLabelKey(DataClear kind)
        {
            switch (kind)
            {
                case DataClear.Platform: return "LOCPlayAch_Menu_ClearPlatformData";
                case DataClear.Metadata: return "LOCPlayAch_Menu_ClearMetadata";
                case DataClear.Custom: return "LOCPlayAch_Menu_ClearCustomData";
                default: return "LOCPlayAch_Menu_ClearAllData";
            }
        }

        /// <summary>The confirmation key of a clear, formatted with the game name, selection or every game.</summary>
        private static string ClearConfirmKey(DataClear kind)
        {
            switch (kind)
            {
                case DataClear.Platform: return "LOCPlayAch_Menu_ClearPlatformData_Confirm";
                case DataClear.Metadata: return "LOCPlayAch_Menu_ClearMetadata_Confirm";
                case DataClear.Custom: return "LOCPlayAch_Menu_ClearCustomData_Confirm";
                default: return "LOCPlayAch_Menu_ClearAllData_Confirm";
            }
        }

        /// <summary>
        /// Asks to confirm <paramref name="kind"/> for <paramref name="games"/>, or for every game
        /// when it is null, and runs it. Returns whether it ran.
        /// </summary>
        internal bool ClearData(DataClear kind, IReadOnlyCollection<(Guid Id, string Name)> games)
        {
            var targets = games?
                .Where(game => game.Id != Guid.Empty)
                .GroupBy(game => game.Id)
                .Select(group => group.First())
                .ToList();
            if (targets != null && targets.Count == 0)
            {
                return false;
            }

            var target = targets == null ? ResourceProvider.GetString("LOCPlayAch_Common_EveryGame")
                : targets.Count == 1 ? targets[0].Name
                : string.Format(ResourceProvider.GetString("LOCPlayAch_Common_SelectedGames"), targets.Count);
            if (!ConfirmMaintenance(string.Format(ResourceProvider.GetString(ClearConfirmKey(kind)), target)))
            {
                return false;
            }

            var ids = targets?.Select(game => game.Id).ToList();
            return RunMaintenance(() =>
            {
                if (kind == DataClear.Platform || kind == DataClear.All)
                {
                    ClearPlatformData(ids);
                }

                if (kind == DataClear.Metadata)
                {
                    ClearMetadataAndRefetch(ids);
                }

                if (kind == DataClear.Custom || kind == DataClear.All)
                {
                    ClearCustomData(ids);
                }
            }, "clear " + kind.ToString().ToLowerInvariant() + " data");
        }

        /// <summary>
        /// Deletes the platform data of <paramref name="gameIds"/>, or of every game when null.
        /// The metadata is stored against the platform's achievements, so it goes with them.
        /// </summary>
        private void ClearPlatformData(IReadOnlyCollection<Guid> gameIds)
        {
            if (gameIds == null)
            {
                _cacheManager?.ClearCache();
                return;
            }

            foreach (var gameId in gameIds)
            {
                _cacheManager?.RemoveGameCache(gameId);
                _refreshService?.MetadataOverlay?.Links?.ForgetApplied(gameId);
            }
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
        /// Deletes the custom data of <paramref name="gameIds"/>, or of every game when null, and
        /// refreshes the games whose custom data chose where their achievements come from.
        /// </summary>
        private void ClearCustomData(IReadOnlyCollection<Guid> gameIds)
        {
            var store = GameCustomDataStore;
            if (store == null)
            {
                return;
            }

            var refresh = new List<Guid>();
            var ids = gameIds ?? store.LoadAll().Select(data => data.PlayniteGameId).ToList();
            foreach (var gameId in ids)
            {
                if (!store.TryLoad(gameId, out var current) || current == null)
                {
                    continue;
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

                if (CustomDataTransition.Analyze(current, null).RequiresRefresh)
                {
                    refresh.Add(gameId);
                }
            }

            if (refresh.Count > 0)
            {
                _ = StartMenuRefreshAsync(new RefreshRequest { GameIds = refresh });
            }
        }

        /// <summary>Whether no metadata source supplies the game.</summary>
        internal bool IsGameExcludedFromMetadata(Guid gameId) => _metadataLinkStore?.IsExcludedFromMetadata(gameId) == true;

        /// <summary>
        /// Excludes games from metadata, or includes them again, keeping their own metadata
        /// choices, then clears and refreshes their metadata so the change shows.
        /// </summary>
        internal void SetExcludedFromMetadata(IReadOnlyCollection<Guid> gameIds, bool excluded)
        {
            var changed = (gameIds ?? Array.Empty<Guid>())
                .Where(gameId => _metadataLinkStore?.SetExcludedFromMetadata(gameId, excluded) == true)
                .ToList();
            if (changed.Count > 0)
            {
                ClearMetadataAndRefetch(changed);
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
