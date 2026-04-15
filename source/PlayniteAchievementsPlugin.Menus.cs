using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private const string PluginGameMenuSection = "Playnite Achievements";
        private const string PluginLocalGameMenuSection = PluginGameMenuSection + "|Local Saves";
        private const string PluginMainMenuSection = "@Playnite Achievements";

        private bool IsRefreshInProgress()
        {
            return _refreshService?.IsRebuilding == true;
        }

        private IEnumerable<GameMenuItem> GetRefreshInProgressGameMenuHeader(Guid? singleGameRefreshId = null)
        {
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress"),
                MenuSection = PluginGameMenuSection
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewRefreshProgress"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ShowRefreshProgressControl(singleGameRefreshId);
                }
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Button_Cancel"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    _refreshService.CancelCurrentRebuild();
                }
            };

            yield return new GameMenuItem
            {
                Description = "-",
                MenuSection = PluginGameMenuSection
            };
        }

        private IEnumerable<MainMenuItem> GetRefreshInProgressMainMenuHeader()
        {
            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress"),
                MenuSection = PluginMainMenuSection
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewRefreshProgress"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    ShowRefreshProgressControl();
                }
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Button_Cancel"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    _refreshService.CancelCurrentRebuild();
                }
            };
        }

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (args?.Games == null || args.Games.Count == 0)
            {
                yield break;
            }

            var refreshInProgress = IsRefreshInProgress();

            // Multiple games selected.
            if (args.Games.Count > 1)
            {
                var selectedGames = GetDistinctValidGames(args.Games);
                if (selectedGames.Count == 0)
                {
                    yield break;
                }

                if (refreshInProgress)
                {
                    foreach (var item in GetRefreshInProgressGameMenuHeader())
                    {
                        yield return item;
                    }
                }

                if (!refreshInProgress)
                {
                    yield return new GameMenuItem
                    {
                        Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshSelected"),
                        MenuSection = PluginGameMenuSection,
                        Action = (a) =>
                        {
                            var selectedIds = selectedGames.Select(g => g.Id).ToList();
                            _ = _refreshCoordinator.ExecuteAsync(
                                new RefreshRequest { GameIds = selectedIds },
                                RefreshExecutionPolicy.ProgressWindow());
                        }
                    };

                    yield return new GameMenuItem
                    {
                        Description = "-",
                        MenuSection = PluginGameMenuSection,
                    };
                }

                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_ClearData"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ClearSelectedGamesData(selectedGames);
                    }
                };

                var allExcludedFromSummaries = selectedGames.All(g => IsGameExcludedFromSummaries(g.Id));
                yield return new GameMenuItem
                {
                    Description = allExcludedFromSummaries
                        ? ResourceProvider.GetString("LOCPlayAch_Common_Action_IncludeInSummaries")
                        : ResourceProvider.GetString("LOCPlayAch_Common_Action_ExcludeFromSummaries"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ToggleExcludedFromSummaries(selectedGames);
                    }
                };

                var allExcludedFromRefreshes = selectedGames.All(g => IsGameExcluded(g.Id));
                yield return new GameMenuItem
                {
                    Description = allExcludedFromRefreshes
                        ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInRefreshes")
                        : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromRefreshes"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ToggleExcludedFromRefreshes(selectedGames, clearDataWhenExcluding: false, confirmWhenClearingData: false);
                    }
                };

                yield return new GameMenuItem
                {
                    Description = allExcludedFromRefreshes
                        ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInRefreshesAndRefresh")
                        : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromRefreshesAndClearData"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ToggleExcludedFromRefreshesAndRefresh(selectedGames);
                    }
                };

                yield break;
            }

            // Single game selected
            var game = args.Games.FirstOrDefault(g => g != null);
            if (game == null)
            {
                yield break;
            }

            if (refreshInProgress)
            {
                foreach (var item in GetRefreshInProgressGameMenuHeader(game.Id))
                {
                    yield return item;
                }
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ViewAchievements"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenSingleGameAchievementsView(game.Id);
                }
            };

            if (!refreshInProgress)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshGame"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Single,
                                SingleGameId = game.Id
                            },
                            RefreshExecutionPolicy.ProgressWindow(game.Id));
                    }
                };
            }

            yield return new GameMenuItem
            {
                Description = "-",
                MenuSection = PluginGameMenuSection
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_GameOptions"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenGameOptionsView(game.Id);
                }
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Download"),
                MenuSection = PluginLocalGameMenuSection,
                Action = (a) =>
                {
                    DownloadExpectedAchievementsJson(game);
                }
            };

            var hasLocalAppIdOverride = LocalSavesProvider.TryGetAppIdOverride(game.Id, out _);
            yield return new GameMenuItem
            {
                Description = hasLocalAppIdOverride
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_Change")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_Set"),
                MenuSection = PluginLocalGameMenuSection,
                Action = (a) =>
                {
                    SetLocalSteamAppIdOverride(game);
                }
            };

            if (hasLocalAppIdOverride)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_Clear"),
                    MenuSection = PluginLocalGameMenuSection,
                    Action = (a) =>
                    {
                        ClearLocalSteamAppIdOverride(game);
                    }
                };
            }

            var hasLocalFolderOverride = LocalSavesProvider.TryGetFolderOverride(game.Id, out _);
            yield return new GameMenuItem
            {
                Description = hasLocalFolderOverride
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_Change")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_Set"),
                MenuSection = PluginLocalGameMenuSection,
                Action = (a) =>
                {
                    SetLocalFolderOverride(game);
                }
            };

            if (hasLocalFolderOverride)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_Clear"),
                    MenuSection = PluginLocalGameMenuSection,
                    Action = (a) =>
                    {
                        ClearLocalFolderOverride(game);
                    }
                };
            }

            var preferredProviderOverride = _achievementOverridesService?.GetPreferredProviderOverride(game.Id);
            var providerMenuSection = PluginLocalGameMenuSection + "|" + ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Change");
            yield return new GameMenuItem
            {
                Description = FormatProviderMenuLabel(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Automatic"),
                    string.IsNullOrWhiteSpace(preferredProviderOverride)),
                MenuSection = providerMenuSection,
                Action = (a) =>
                {
                    ChangePreferredProvider(game, null);
                }
            };

            foreach (var providerKey in GetSelectableProviderKeys())
            {
                var capturedProviderKey = providerKey;
                var providerLabel = PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(capturedProviderKey);
                yield return new GameMenuItem
                {
                    Description = FormatProviderMenuLabel(
                        providerLabel,
                        string.Equals(preferredProviderOverride, capturedProviderKey, StringComparison.OrdinalIgnoreCase)),
                    MenuSection = providerMenuSection,
                    Action = (a) =>
                    {
                        ChangePreferredProvider(game, capturedProviderKey);
                    }
                };
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_ClearData"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ClearSingleGameData(game);
                }
            };

            var excludedFromSummaries = IsGameExcludedFromSummaries(game.Id);
            yield return new GameMenuItem
            {
                Description = excludedFromSummaries
                    ? ResourceProvider.GetString("LOCPlayAch_Common_Action_IncludeInSummaries")
                    : ResourceProvider.GetString("LOCPlayAch_Common_Action_ExcludeFromSummaries"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ToggleExcludedFromSummaries(new[] { game });
                }
            };

            var excludedFromRefreshes = IsGameExcluded(game.Id);
            yield return new GameMenuItem
            {
                Description = excludedFromRefreshes
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInRefreshes")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromRefreshes"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ToggleExcludedFromRefreshes(new[] { game }, clearDataWhenExcluding: false, confirmWhenClearingData: false);
                }
            };

            yield return new GameMenuItem
            {
                Description = excludedFromRefreshes
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInRefreshesAndRefresh")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromRefreshesAndClearData"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    ToggleExcludedFromRefreshesAndRefresh(new[] { game });
                }
            };
        }

        private static List<Game> GetDistinctValidGames(IEnumerable<Game> games)
        {
            return games?
                .Where(g => g != null && g.Id != Guid.Empty)
                .GroupBy(g => g.Id)
                .Select(g => g.First())
                .ToList() ?? new List<Game>();
        }

        private void ExcludeGamesFromSummaries(IEnumerable<Game> games)
        {
            var targets = GetDistinctValidGames(games);
            if (targets.Count == 0)
            {
                return;
            }

            foreach (var game in targets)
            {
                _achievementOverridesService.SetExcludedFromSummaries(game.Id, true);
            }
        }

        private void ExcludeGamesFromRefreshes(
            IEnumerable<Game> games,
            bool clearDataWhenExcluding,
            bool confirmWhenClearingData)
        {
            var targets = GetDistinctValidGames(games);
            if (targets.Count == 0)
            {
                return;
            }

            if (clearDataWhenExcluding && confirmWhenClearingData)
            {
                var confirmText = targets.Count == 1
                    ? string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), targets[0].Name)
                    : string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSelected"), targets.Count);

                var result = PlayniteApi?.Dialogs?.ShowMessage(
                    confirmText,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            foreach (var game in targets)
            {
                _achievementOverridesService.SetExcludedByUser(
                    game.Id,
                    excluded: true,
                    clearCachedDataWhenExcluding: clearDataWhenExcluding);
            }
        }

        private void ToggleExcludedFromSummaries(IEnumerable<Game> games)
        {
            var targets = GetDistinctValidGames(games);
            foreach (var game in targets)
            {
                var isExcluded = IsGameExcludedFromSummaries(game.Id);
                _achievementOverridesService.SetExcludedFromSummaries(game.Id, !isExcluded);
            }
        }

        private void ToggleExcludedFromRefreshes(IEnumerable<Game> games, bool clearDataWhenExcluding, bool confirmWhenClearingData)
        {
            var targets = GetDistinctValidGames(games);
            foreach (var game in targets)
            {
                var isExcluded = IsGameExcluded(game.Id);
                if (isExcluded)
                {
                    _achievementOverridesService.SetExcludedByUser(game.Id, false, clearCachedDataWhenExcluding: false);
                }
                else
                {
                    if (clearDataWhenExcluding && confirmWhenClearingData)
                    {
                        var confirmText = string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), game.Name);
                        var result = PlayniteApi?.Dialogs?.ShowMessage(
                            confirmText,
                            ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) ?? MessageBoxResult.None;
                        if (result != MessageBoxResult.Yes) continue;
                    }
                    _achievementOverridesService.SetExcludedByUser(game.Id, true, clearCachedDataWhenExcluding: clearDataWhenExcluding);
                }
            }
        }

        private void ToggleExcludedFromRefreshesAndRefresh(IEnumerable<Game> games)
        {
            var targets = GetDistinctValidGames(games);
            var gameIdsToRefresh = new List<Guid>();
            var gamesToExclude = new List<Game>();

            // First pass: categorize games
            foreach (var game in targets)
            {
                var isExcluded = IsGameExcluded(game.Id);
                if (isExcluded)
                {
                    _achievementOverridesService.SetExcludedByUser(game.Id, false, clearCachedDataWhenExcluding: false);
                    gameIdsToRefresh.Add(game.Id);
                }
                else
                {
                    gamesToExclude.Add(game);
                }
            }

            // Single batch confirmation for all exclusions
            if (gamesToExclude.Count > 0)
            {
                var confirmText = gamesToExclude.Count == 1
                    ? string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), gamesToExclude[0].Name)
                    : string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSelected"), gamesToExclude.Count);
                var result = PlayniteApi?.Dialogs?.ShowMessage(
                    confirmText,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result == MessageBoxResult.Yes)
                {
                    foreach (var game in gamesToExclude)
                    {
                        _achievementOverridesService.SetExcludedByUser(game.Id, true, clearCachedDataWhenExcluding: true);
                    }
                }
            }

            // Refresh un-excluded games
            if (gameIdsToRefresh.Count == 1)
            {
                var gameId = gameIdsToRefresh[0];
                _ = _refreshCoordinator.ExecuteAsync(
                    new RefreshRequest
                    {
                        Mode = RefreshModeType.Single,
                        SingleGameId = gameId
                    },
                    RefreshExecutionPolicy.ProgressWindow(gameId));
            }
            else if (gameIdsToRefresh.Count > 1)
            {
                _ = _refreshCoordinator.ExecuteAsync(
                    new RefreshRequest { GameIds = gameIdsToRefresh },
                    RefreshExecutionPolicy.ProgressWindow());
            }
        }

        private void ClearSingleGameData(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSingle"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (_achievementOverridesService != null)
                {
                    _achievementOverridesService.ClearGameData(game.Id, game.Name);
                }
                else
                {
                    _cacheManager.RemoveGameCache(game.Id);
                }

                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, $"Failed to clear cached data for game '{game.Name}' ({game.Id}).");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), ex.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ClearSelectedGamesData(IEnumerable<Game> selectedGames)
        {
            var targets = selectedGames?
                .Where(g => g != null && g.Id != Guid.Empty)
                .GroupBy(g => g.Id)
                .Select(g => g.First())
                .ToList() ?? new List<Game>();

            if (targets.Count == 0)
            {
                return;
            }

            var result = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_ClearData_ConfirmSelected"), targets.Count),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var clearedCount = 0;
            foreach (var game in targets)
            {
                try
                {
                    if (_achievementOverridesService != null)
                    {
                        _achievementOverridesService.ClearGameData(game.Id, game.Name);
                    }
                    else
                    {
                        _cacheManager.RemoveGameCache(game.Id);
                    }

                    clearedCount++;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, $"Failed to clear cached data for game '{game.Name}' ({game.Id}).");
                }
            }

            PlayniteApi?.Dialogs?.ShowMessage(
                ResourceProvider.GetString("LOCPlayAch_Status_Succeeded"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void DownloadExpectedAchievementsJson(Game game)
        {
            if (game == null)
            {
                return;
            }

            var localProvider = Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
            if (localProvider == null)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_ProviderUnavailable"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (localProvider.TryResolveAchievementsJsonPath(game, out var jsonPath, out _, out _) &&
                File.Exists(jsonPath))
            {
                var overwriteResult = PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_OverwriteConfirm"), game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (overwriteResult != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var progressOptions = new GlobalProgressOptions(
                ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Progress"),
                true)
            {
                Cancelable = true,
                IsIndeterminate = true
            };

            LocalSavesProvider.ExpectedAchievementsDownloadResult result = null;
            Exception failure = null;

            PlayniteApi?.Dialogs?.ActivateGlobalProgress(progress =>
            {
                progress.Text = ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Progress");

                try
                {
                    result = localProvider
                        .DownloadExpectedAchievementsFileAsync(game, progress.CancelToken)
                        .GetAwaiter()
                        .GetResult();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            }, progressOptions);

            if (failure != null)
            {
                _logger?.Error(failure, $"Failed to generate expected achievements.json for '{game.Name}'.");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Failed"), failure.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            if (result?.Success == true)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    result.Message,
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            PlayniteApi?.Dialogs?.ShowMessage(
                result?.Message ?? ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_UnknownError"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        private void SetLocalSteamAppIdOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            LocalSavesProvider.TryResolveAppId(game, out var currentAppId, out var isOverridden);
            var defaultValue = currentAppId > 0 ? currentAppId.ToString() : string.Empty;
            var dialogTitle = ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_DialogTitle");
            var dialogHint = currentAppId > 0
                ? string.Format(
                    ResourceProvider.GetString(isOverridden
                        ? "LOCPlayAch_Menu_LocalAppId_DialogHintOverride"
                        : "LOCPlayAch_Menu_LocalAppId_DialogHintDetected"),
                    currentAppId)
                : ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_DialogHint");

            var input = PlayniteApi?.Dialogs?.SelectString(dialogHint, dialogTitle, defaultValue);
            if (input == null || !input.Result)
            {
                return;
            }

            var selected = input.SelectedString?.Trim();
            if (!int.TryParse(selected, out var newAppId) || newAppId <= 0)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_InvalidId"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!LocalSavesProvider.TrySetAppIdOverride(game.Id, newAppId, game.Name, PersistSettingsForUi, _logger))
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_SetFailed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _achievementOverridesService?.ClearGameData(game.Id, game.Name);
            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                RefreshExecutionPolicy.ProgressWindow(game.Id));
        }

        private void ClearLocalSteamAppIdOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var confirmResult = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_ClearConfirm"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            if (!LocalSavesProvider.TryClearAppIdOverride(game.Id, game.Name, PersistSettingsForUi, _logger))
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_ClearFailed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _achievementOverridesService?.ClearGameData(game.Id, game.Name);
            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                RefreshExecutionPolicy.ProgressWindow(game.Id));
        }

        private void SetLocalFolderOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var selectedPath = PlayniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            if (!Directory.Exists(selectedPath))
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_GameOptions_LocalFolder_NotFound"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (!LocalSavesProvider.TrySetFolderOverride(game.Id, selectedPath, game.Name, PersistSettingsForUi, _logger))
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_SetFailed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _achievementOverridesService?.ClearGameData(game.Id, game.Name);
            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                RefreshExecutionPolicy.ProgressWindow(game.Id));
        }

        private void ClearLocalFolderOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var confirmResult = PlayniteApi?.Dialogs?.ShowMessage(
                string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_ClearConfirm"), game.Name),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) ?? MessageBoxResult.None;

            if (confirmResult != MessageBoxResult.Yes)
            {
                return;
            }

            if (!LocalSavesProvider.TryClearFolderOverride(game.Id, game.Name, PersistSettingsForUi, _logger))
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_ClearFailed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _achievementOverridesService?.ClearGameData(game.Id, game.Name);
            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                RefreshExecutionPolicy.ProgressWindow(game.Id));
        }

        private void ChangePreferredProvider(Game game, string providerKey)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            CacheWriteResult result;
            if (string.IsNullOrWhiteSpace(providerKey))
            {
                result = _achievementOverridesService?.ClearPreferredProviderOverride(game.Id);
                if (result?.Success != true)
                {
                    PlayniteApi?.Dialogs?.ShowMessage(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_ClearFailed"),
                        ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                _ = _refreshCoordinator.ExecuteAsync(
                    new RefreshRequest
                    {
                        Mode = RefreshModeType.Single,
                        SingleGameId = game.Id
                    },
                    RefreshExecutionPolicy.ProgressWindow(game.Id));
                return;
            }

            result = _achievementOverridesService?.SetPreferredProviderOverride(game.Id, providerKey);
            if (result?.Success != true)
            {
                PlayniteApi?.Dialogs?.ShowMessage(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_SetFailed"),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            _ = _refreshCoordinator.ExecuteAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Custom,
                    CustomOptions = new CustomRefreshOptions
                    {
                        ProviderKeys = new[] { providerKey },
                        Scope = CustomGameScope.Explicit,
                        IncludeGameIds = new[] { game.Id },
                        RespectUserExclusions = false,
                        ForceBypassExclusionsForExplicitIncludes = true
                    }
                },
                new RefreshExecutionPolicy
                {
                    UseProgressWindow = true,
                    SwallowExceptions = true,
                    ProgressSingleGameId = game.Id
                });
        }

        private List<string> GetSelectableProviderKeys()
        {
            return Providers?
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.ProviderKey))
                .Select(provider => provider.ProviderKey.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    providerKey => PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(providerKey),
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? new List<string>();
        }

        private static string FormatProviderMenuLabel(string label, bool isCurrent)
        {
            if (!isCurrent)
            {
                return label;
            }

            return string.Format(
                ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Current"),
                label);
        }

        public bool IsGameExcluded(Guid gameId)
        {
            return GameCustomDataLookup.IsExcludedFromRefreshes(
                gameId,
                _settingsViewModel?.Settings?.Persisted,
                _gameCustomDataStore);
        }

        public bool IsGameExcludedFromSummaries(Guid gameId)
        {
            return GameCustomDataLookup.IsExcludedFromSummaries(
                gameId,
                _settingsViewModel?.Settings?.Persisted,
                _gameCustomDataStore);
        }

        public void ToggleGameExclusion(Guid gameId)
        {
            var isExcluded = IsGameExcluded(gameId);

            // Only show confirmation when excluding (not when including)
            if (!isExcluded)
            {
                var game = PlayniteApi?.Database?.Games?.Get(gameId);
                var gameName = game?.Name ?? ResourceProvider.GetString("LOCPlayAch_Text_UnknownGame");

                var result = PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Menu_Exclude_ConfirmSingle"), gameName),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;

                if (result != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            _achievementOverridesService.SetExcludedByUser(gameId, !isExcluded, clearCachedDataWhenExcluding: !isExcluded);
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var refreshInProgress = IsRefreshInProgress();
            if (refreshInProgress)
            {
                foreach (var item in GetRefreshInProgressMainMenuHeader())
                {
                    yield return item;
                }
            }

            if (!refreshInProgress)
            {
                // Fullscreen-only: overview window (desktop uses the sidebar panel).
                var isFullscreen = false;
                try
                {
                    isFullscreen = PlayniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
                }
                catch { }

                if (isFullscreen)
                {
                    yield return new MainMenuItem
                    {
                        Description = ResourceProvider.GetString("LOCPlayAch_Menu_OpenOverview"),
                        MenuSection = PluginMainMenuSection,
                        Action = (a) =>
                        {
                            OpenOverviewWindow();
                        }
                    };

                    yield return new MainMenuItem
                    {
                        Description = "-",
                        MenuSection = PluginMainMenuSection
                    };
                }

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Recent"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Recent },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Full"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Full },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Installed"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Installed },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Favorites"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Favorites },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Selected"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.LibrarySelected },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshMode_Missing"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest { Mode = RefreshModeType.Missing },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_CustomRefresh_MenuItem"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        if (!CustomRefreshControl.TryShowDialog(
                            PlayniteApi,
                            _refreshService,
                            PersistSettingsForUi,
                            _settingsViewModel.Settings,
                            _logger,
                            out var customOptions))
                        {
                            return;
                        }

                        _ = _refreshCoordinator.ExecuteAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Custom,
                                CustomOptions = customOptions
                            },
                            RefreshExecutionPolicy.ProgressWindow());
                    }
                };
            }
        }
    }
}
