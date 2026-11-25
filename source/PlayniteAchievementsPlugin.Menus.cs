using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Achievements;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services;
using PlayniteAchievements.Services.GameCustomData;
using PlayniteAchievements.Services.Refresh;
using PlayniteAchievements.ViewModels;
using PlayniteAchievements.Views;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        /// <summary>
        /// Shows Log Memory Diagnostics in the extensions menu while memory tracing is armed.
        /// Off: tracing still logs without the menu item.
        /// </summary>
        private static readonly bool MemoryDiagnosticsMenuEnabled = false;

        private static string PluginGameMenuSection => ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
        // Nested "Maintenance" submenu under the plugin's game-menu section (Playnite uses | to nest).
        private static string PluginGameMaintenanceSection =>
            PluginGameMenuSection + "|" + ResourceProvider.GetString("LOCPlayAch_Settings_Maintenance_Title");
        private static string PluginLocalGameMenuSection => PluginGameMenuSection + "|Local Saves";
        private static string PluginMainMenuSection => "@" + ResourceProvider.GetString("LOCPlayAch_Title_PluginName");
        // Nested "Refresh" submenu under the plugin's main-menu section, holding every refresh mode.
        private static string PluginMainRefreshSection =>
            PluginMainMenuSection + "|" + ResourceProvider.GetString("LOCPlayAch_Button_Refresh");
        private int _fullscreenMenuGlobalProgressActive;

        private bool IsRefreshInProgress()
        {
            return _refreshService?.IsRebuilding == true;
        }

        private bool IsFullscreenMode()
        {
            try
            {
                return PlayniteApi?.ApplicationInfo?.Mode == ApplicationMode.Fullscreen;
            }
            catch
            {
                return false;
            }
        }

        private bool IsFullscreenMenuGlobalProgressActive()
        {
            return Volatile.Read(ref _fullscreenMenuGlobalProgressActive) == 1;
        }

        private void SetFullscreenMenuGlobalProgressActive(bool active)
        {
            Interlocked.Exchange(ref _fullscreenMenuGlobalProgressActive, active ? 1 : 0);
        }

        private string GetMenuRefreshErrorLogMessage(RefreshRequest request)
        {
            var refreshName = GetMenuRefreshDisplayName(request);
            var format = ResourceProvider.GetString("LOCPlayAch_Error_RefreshFailed");
            if (!string.IsNullOrWhiteSpace(format) && !string.IsNullOrWhiteSpace(refreshName))
            {
                return string.Format(format, refreshName);
            }

            return ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
        }

        private string GetMenuRefreshDisplayName(RefreshRequest request)
        {
            if (request?.SingleGameId.HasValue == true ||
                (request?.GameIds?.Count ?? 0) == 1 ||
                request?.Mode == RefreshModeType.Single)
            {
                return ResourceProvider.GetString(RefreshModeType.Single.GetResourceKey());
            }

            if ((request?.GameIds?.Count ?? 0) > 1)
            {
                return ResourceProvider.GetString(RefreshModeType.LibrarySelected.GetResourceKey());
            }

            if (request?.Mode.HasValue == true)
            {
                return ResourceProvider.GetString(request.Mode.Value.GetResourceKey());
            }

            if (!string.IsNullOrWhiteSpace(request?.ModeKey) &&
                Enum.TryParse(request.ModeKey.Trim(), ignoreCase: true, out RefreshModeType parsedMode))
            {
                return ResourceProvider.GetString(parsedMode.GetResourceKey());
            }

            return null;
        }

        private Task StartMenuRefreshAsync(RefreshRequest request, Guid? singleGameId = null)
        {
            var progressSingleGameId = singleGameId ?? request?.SingleGameId;
            if (!IsFullscreenMode())
            {
                return _refreshCoordinator.ExecuteAsync(
                    request,
                    RefreshExecutionPolicy.ProgressWindow(progressSingleGameId));
            }

            SetFullscreenMenuGlobalProgressActive(true);
            if (_themeIntegrationService != null)
            {
                return _themeIntegrationService.RunFullscreenRefreshRequestAsync(
                    request,
                    GetMenuRefreshErrorLogMessage(request),
                    validateAuthentication: true,
                    onCompleted: success => SetFullscreenMenuGlobalProgressActive(false));
            }

            return _windowService.RunRefreshWithGlobalProgressAsync(
                request,
                GetMenuRefreshErrorLogMessage(request),
                validateAuthentication: true,
                onCompleted: success => SetFullscreenMenuGlobalProgressActive(false));
        }

        private IEnumerable<GameMenuItem> GetRefreshInProgressGameMenuHeader(Guid? singleGameRefreshId = null)
        {
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress"),
                MenuSection = PluginGameMenuSection
            };

            if (!IsFullscreenMenuGlobalProgressActive())
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Common_View"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        ShowRefreshProgressControl(singleGameRefreshId);
                    }
                };
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Common_Cancel"),
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

            if (!IsFullscreenMenuGlobalProgressActive())
            {
                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Common_View"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        ShowRefreshProgressControl();
                    }
                };
            }

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Common_Cancel"),
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
                            _ = StartMenuRefreshAsync(new RefreshRequest { GameIds = selectedIds });
                        }
                    };
                }

                // The Achievement Editor locked to the selection; shown mid-refresh, as in the main menu.
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_AchievementEditor"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        OpenLibraryEditorWindow(selectedGames.Select(g => g.Id).ToList());
                    }
                };

                yield return new GameMenuItem
                {
                    Description = "-",
                    MenuSection = PluginGameMenuSection,
                };

                foreach (var item in MaintenanceMenuItems(selectedGames))
                {
                    yield return item;
                }

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
                Description = ResourceProvider.GetString("LOCPlayAch_Common_View"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenViewAchievementsWindow(game.Id);
                }
            };

            if (_settingsViewModel?.Settings?.Persisted?.EnableFriendsFeatures == true)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Settings_Friends"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        OpenViewFriendsAchievementsWindow(game.Id);
                    }
                };
            }

            // Only shown when the game actually has saved captures (Playnite menu items can't be
            // greyed out, so absence is the "inactive" state here).
            if (_captureLibraryService?.GameHasCaptures(game.Name) == true)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Column_Captures"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        OpenCapturesViewerForGame(game.Name);
                    }
                };
            }

            if (!refreshInProgress)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Button_Refresh"),
                    MenuSection = PluginGameMenuSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Single,
                                SingleGameId = game.Id,
                                SurfaceUserNotices = true
                            },
                            game.Id);
                    }
                };
            }

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Common_Manage"),
                MenuSection = PluginGameMenuSection,
                Action = (a) =>
                {
                    OpenManageAchievementsView(game.Id);
                }
            };

            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Download"),
                MenuSection = PluginLocalGameMenuSection,
                Action = _ => DownloadExpectedAchievementsJson(game)
            };

            var hasLocalAppIdOverride = LocalSavesProvider.TryGetAppIdOverride(game.Id, out _);
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString(hasLocalAppIdOverride
                    ? "LOCPlayAch_Menu_LocalAppId_Change"
                    : "LOCPlayAch_Menu_LocalAppId_Set"),
                MenuSection = PluginLocalGameMenuSection,
                Action = _ => SetLocalSteamAppIdOverride(game)
            };
            if (hasLocalAppIdOverride)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_Clear"),
                    MenuSection = PluginLocalGameMenuSection,
                    Action = _ => ClearLocalSteamAppIdOverride(game)
                };
            }

            var selectedLocalUser = LocalSavesProvider.TryGetSteamAppCacheUserOverride(
                game.Id,
                out var localUserOverride)
                ? localUserOverride
                : string.Empty;
            var localUserSection = PluginLocalGameMenuSection + "|" +
                ResourceProvider.GetString("LOCPlayAch_Menu_LocalSteamUser_Change");
            yield return new GameMenuItem
            {
                Description = FormatLocalProviderMenuLabel(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalSteamUser_Automatic"),
                    string.IsNullOrWhiteSpace(selectedLocalUser)),
                MenuSection = localUserSection,
                Action = _ => ChangeLocalSteamUserOverride(game, null)
            };
            var localProvider = Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
            foreach (var user in localProvider?.GetAvailableSteamAppCacheUsersForMenu() ??
                     Enumerable.Empty<LocalSteamAppCacheUserOption>())
            {
                var userId = user.UserId;
                var userLabel = string.IsNullOrWhiteSpace(user.DisplayName) ? userId : user.DisplayName;
                yield return new GameMenuItem
                {
                    Description = FormatLocalProviderMenuLabel(
                        userLabel,
                        string.Equals(selectedLocalUser, userId, StringComparison.OrdinalIgnoreCase)),
                    MenuSection = localUserSection,
                    Action = _ => ChangeLocalSteamUserOverride(game, userId)
                };
            }

            var hasLocalFolderOverride = LocalSavesProvider.TryGetFolderOverride(game.Id, out _);
            yield return new GameMenuItem
            {
                Description = ResourceProvider.GetString(hasLocalFolderOverride
                    ? "LOCPlayAch_Menu_LocalFolder_Change"
                    : "LOCPlayAch_Menu_LocalFolder_Set"),
                MenuSection = PluginLocalGameMenuSection,
                Action = _ => SetLocalFolderOverride(game)
            };
            if (hasLocalFolderOverride)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_Clear"),
                    MenuSection = PluginLocalGameMenuSection,
                    Action = _ => ClearLocalFolderOverride(game)
                };
            }

            var preferredProvider = GameCustomDataLookup.TryGetProviderOverride(
                game.Id,
                out var providerOverride)
                    ? providerOverride?.ProviderKey
                    : null;
            var providerSection = PluginLocalGameMenuSection + "|" +
                ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Change");
            yield return new GameMenuItem
            {
                Description = FormatLocalProviderMenuLabel(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Automatic"),
                    string.IsNullOrWhiteSpace(preferredProvider)),
                MenuSection = providerSection,
                Action = _ => ChangePreferredProvider(game, null)
            };
            foreach (var providerKey in GetSelectableProviderKeys())
            {
                var capturedProviderKey = providerKey;
                var providerLabel = PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName(
                    capturedProviderKey);
                yield return new GameMenuItem
                {
                    Description = FormatLocalProviderMenuLabel(
                        providerLabel,
                        string.Equals(
                            preferredProvider,
                            capturedProviderKey,
                            StringComparison.OrdinalIgnoreCase)),
                    MenuSection = providerSection,
                    Action = _ => ChangePreferredProvider(game, capturedProviderKey)
                };
            }


            foreach (var item in MaintenanceMenuItems(new List<Game> { game }))
            {
                yield return item;
            }
        }

        /// <summary>The game menu's Maintenance submenu: the per-layer clears, then the exclusions.</summary>
        private IEnumerable<GameMenuItem> MaintenanceMenuItems(List<Game> games)
        {
            var targets = games.Select(game => (Id: game.Id, Name: game.Name)).ToList();
            foreach (var kind in DataClears)
            {
                yield return new GameMenuItem
                {
                    Description = ResourceProvider.GetString(ClearLabelKey(kind)),
                    MenuSection = PluginGameMaintenanceSection,
                    Action = (a) => ClearData(kind, targets)
                };
            }

            yield return new GameMenuItem
            {
                Description = "-",
                MenuSection = PluginGameMaintenanceSection
            };

            var excludedFromSummaries = games.All(g => IsGameExcludedFromSummaries(g.Id));
            yield return new GameMenuItem
            {
                Description = excludedFromSummaries
                    ? ResourceProvider.GetString("LOCPlayAch_Common_Action_IncludeInSummaries")
                    : ResourceProvider.GetString("LOCPlayAch_Common_Action_ExcludeFromSummaries"),
                MenuSection = PluginGameMaintenanceSection,
                Action = (a) => ToggleExcludedFromSummaries(games)
            };

            var excludedFromRefreshes = games.All(g => IsGameExcluded(g.Id));
            yield return new GameMenuItem
            {
                Description = excludedFromRefreshes
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInRefreshes")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromRefreshes"),
                MenuSection = PluginGameMaintenanceSection,
                Action = (a) => ToggleExcludedFromRefreshes(games)
            };

            var excludedFromMetadata = games.All(g => IsGameExcludedFromMetadata(g.Id));
            yield return new GameMenuItem
            {
                Description = excludedFromMetadata
                    ? ResourceProvider.GetString("LOCPlayAch_Menu_IncludeInMetadata")
                    : ResourceProvider.GetString("LOCPlayAch_Menu_ExcludeFromMetadata"),
                MenuSection = PluginGameMaintenanceSection,
                Action = (a) => SetExcludedFromMetadata(games.Select(g => g.Id).ToList(), !excludedFromMetadata)
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

        private void ToggleExcludedFromSummaries(IEnumerable<Game> games)
        {
            var targets = GetDistinctValidGames(games);
            foreach (var game in targets)
            {
                var isExcluded = IsGameExcludedFromSummaries(game.Id);
                _achievementOverridesService.SetExcludedFromSummaries(game.Id, !isExcluded);
            }
        }

        private void ToggleExcludedFromRefreshes(IEnumerable<Game> games)
        {
            foreach (var game in GetDistinctValidGames(games))
            {
                _achievementOverridesService.SetExcludedByUser(game.Id, !IsGameExcluded(game.Id), clearCachedDataWhenExcluding: false);
            }
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

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            var refreshInProgress = IsRefreshInProgress();
            var isFullscreen = IsFullscreenMode();
            if (refreshInProgress)
            {
                foreach (var item in GetRefreshInProgressMainMenuHeader())
                {
                    yield return item;
                }

                yield return new MainMenuItem
                {
                    Description = "-",
                    MenuSection = PluginMainMenuSection
                };
            }

            // The Overview, settings and the Workshop show in every mode, mid-refresh included.
            // Overview and Settings use Playnite's own labels, which are already translated.
            // Fullscreen can't open Playnite's native plugin-settings dialog (OpenSettingsView is
            // a no-op there), so it hosts the plugin's settings UI in a managed popout that saves
            // on close.
            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Common_Overview"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    OpenOverviewWindow();
                }
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Menu_AchievementEditor"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    OpenLibraryEditorWindow();
                }
            };

            if (Services.YearReview.YearReviewAvailability.IsReleased(DateTime.Today))
            {
                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_YearReview_Title"),
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        OpenYearReviewWindow();
                    }
                };
            }

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Common_Settings"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    if (isFullscreen)
                    {
                        OpenSettingsWindow();
                    }
                    else
                    {
                        OpenSettingsView();
                    }
                }
            };

            yield return new MainMenuItem
            {
                Description = ResourceProvider.GetString("LOCPlayAch_Workshop_Title"),
                MenuSection = PluginMainMenuSection,
                Action = (a) =>
                {
                    ToggleWorkshopTabWindow();
                }
            };

            if (!refreshInProgress)
            {
                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Recent"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.Recent });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Full"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.Full });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Installed"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.Installed });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Favorites"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.Favorites });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Selected"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.LibrarySelected });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_RefreshModeShort_Missing"),
                    MenuSection = PluginMainRefreshSection,
                    Action = (a) =>
                    {
                        _ = StartMenuRefreshAsync(new RefreshRequest { Mode = RefreshModeType.Missing });
                    }
                };

                yield return new MainMenuItem
                {
                    Description = ResourceProvider.GetString("LOCPlayAch_Common_Custom") + "...",
                    MenuSection = PluginMainRefreshSection,
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

                        _ = StartMenuRefreshAsync(
                            new RefreshRequest
                            {
                                Mode = RefreshModeType.Custom,
                                Options = RefreshOptions.FromCustom(customOptions)
                            });
                    }
                };
            }

            // Developer-only diagnostic, hidden unless the hardcoded MemoryDiagnosticsMenuEnabled
            // flag is on and memory tracing is armed. Deliberately not localized.
            // Available mid-refresh: capturing memory state during and after large scans is the
            // point. The compacting collect logs a managed-vs-native breakdown of residual memory.
            if (MemoryDiagnosticsMenuEnabled && Common.MemoryDiagnostics.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Log Memory Diagnostics",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) => _ = Task.Run(() =>
                    {
                        try
                        {
                            var before = Common.MemoryDiagnostics.Log(_logger, "manual", "trigger=menu");
                            System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                                System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                            GC.Collect();
                            GC.WaitForPendingFinalizers();
                            GC.Collect();
                            Common.MemoryDiagnostics.Log(_logger, "manual.afterGC", before, "trigger=menu");

                            // Process totals say memory grew; the retention report says which
                            // graph is still rooted. It forces its own blocking collect, so the
                            // per-cache and live= counts mean "still reachable".
                            LogRetentionDiagnostics("manual.retention");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Debug(ex, "Manual memory diagnostics failed.");
                        }
                    })
                };
            }

            // Developer-only, hidden unless the hardcoded LinkedFileTestKit.Enabled flag is on.
            // Deliberately not localized.
            if (Services.LinkedFiles.LinkedFileTestKit.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Linked files: set up test game",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        try
                        {
                            var reportPath = Services.LinkedFiles.LinkedFileTestKit.Run(
                                PlayniteApi,
                                _gameCustomDataStore,
                                _achievementOverridesService,
                                _linkedFileSyncService,
                                _linkedFileSchemaStore,
                                System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "linked-files"),
                                _logger);
                            PlayniteApi.Dialogs.ShowMessage(
                                "The test game \"" + Services.LinkedFiles.LinkedFileTestKit.GameName + "\" is ready, with its CSV linked and applied.\n\n" +
                                "Report: " + reportPath + "\n\n" +
                                "Next: open Manage Achievements on the test game. Use Link File to load the other saved mappings (Test JSON, Test TOML, Test XML, Test INI, Test INI flat, Test Excel). " +
                                "Press Play to watch the file live: edit and save a file, then click the test game's window, because notifications wait until the game is in front. Click OK in that window to stop the game.",
                                "Linked file test");
                            System.Diagnostics.Process.Start("explorer.exe", "\"" + System.IO.Path.GetDirectoryName(reportPath) + "\"");
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn(ex, "Linked file test kit failed.");
                            PlayniteApi.Dialogs.ShowErrorMessage(ex.ToString(), "Linked file test");
                        }
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded TrueGamingProbe.Enabled flag is on.
            // Deliberately not localized.
            if (global::PlayniteAchievements.Providers.Metadata.TrueGaming.TrueGamingProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe TrueGaming",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) => _ = Task.Run(async () =>
                    {
                        try
                        {
                            await global::PlayniteAchievements.Providers.Metadata.TrueGaming.TrueGamingProbe.RunAsync(
                                PlayniteApi,
                                _logger,
                                System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "truegaming"),
                                CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            _logger?.Warn(ex, "TrueGaming probe failed.");
                        }
                    })
                };
            }

            // Developer-only, hidden unless the hardcoded MetadataSourceProbe.Enabled flag is on.
            // Deliberately not localized. Probes a fixed list of games against TrueGaming and SteamHunters.
            if (global::PlayniteAchievements.Providers.Metadata.MetadataSourceProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe collections (TrueGaming, SteamHunters)",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "sources");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.Metadata.MetadataSourceProbe.RunAsync(
                                    PlayniteApi,
                                    Settings,
                                    _providerRegistry?.GetMetadataProviders(),
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_SourceProbe",
                                    $"Source probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Source probe failed.");
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded TagsCategoriesProbe.Enabled flag is on.
            // Deliberately not localized. Compares tag and category sources on a fixed list of Steam games.
            if (global::PlayniteAchievements.Providers.Metadata.TagsCategoriesProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe tags and categories",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "tags-categories");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.Metadata.TagsCategoriesProbe.RunAsync(
                                    PlayniteApi,
                                    Settings,
                                    _providerRegistry?.GetMetadataProviders(),
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_TagsCategoriesProbe",
                                    $"Tags and categories probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Tags and categories probe failed.");
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded ExophaseMissingPageProbe.Enabled flag is on.
            // Deliberately not localized. Loads a fixed list of Exophase award pages, localized and English.
            if (global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseMissingPageProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe missing pages (Exophase)",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "exophase-missing-2");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseMissingPageProbe.RunAsync(
                                    PlayniteApi,
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_MissingPageProbe",
                                    $"Missing-page probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Missing-page probe failed.");
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded DataForAzerothProbe.Enabled flag is on.
            // Deliberately not localized. Opens the site for its human check, then compares hosts.
            if (global::PlayniteAchievements.Providers.BattleNet.DataForAzerothProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe Data for Azeroth hosts",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "dfa-probe");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.BattleNet.DataForAzerothProbe.RunAsync(
                                    PlayniteApi,
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_DfaProbe",
                                    $"Data for Azeroth probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Data for Azeroth probe failed.");
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded BattleNetGamesProbe.Enabled flag is on.
            // Deliberately not localized. Runs on a tester's machine and zips its report to send back.
            if (global::PlayniteAchievements.Providers.BattleNet.BattleNetGamesProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe Battle.net games",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "battlenet-probe");
                        PlayniteApi.Notifications.Add(
                            "PlayAch_BattleNetGamesProbe",
                            "Battle.net probe running. A folder opens when it finishes.",
                            NotificationType.Info);
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var zipPath = await global::PlayniteAchievements.Providers.BattleNet.BattleNetGamesProbe.RunAsync(
                                    PlayniteApi,
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_BattleNetGamesProbe",
                                    $"Battle.net probe finished. Send back {System.IO.Path.GetFileName(zipPath)} from {System.IO.Path.GetDirectoryName(zipPath)}",
                                    NotificationType.Info);
                                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + zipPath + "\"");
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Battle.net probe failed.");
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_BattleNetGamesProbe",
                                    $"Battle.net probe failed: {ex.Message}",
                                    NotificationType.Error);
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded ExophaseCollectionProbe.Enabled flag is on.
            // Deliberately not localized. Probes a fixed list of Steam collections, plus the games
            // selected in the library.
            if (global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseCollectionProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe collections (Exophase PlayStation)",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var games = PlayniteApi.MainView.SelectedGames?.ToList() ?? new List<Game>();
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "collections");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseCollectionProbe.RunAsync(
                                    PlayniteApi,
                                    Settings,
                                    _providerRegistry?.GetMetadataProviders(),
                                    _achievementDataService,
                                    games,
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_CollectionProbe",
                                    $"Collection probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Collection probe failed.");
                            }
                        });
                    }
                };
            }

            // Developer-only, hidden unless the hardcoded ExophaseIconProbe.Enabled flag is on.
            // Deliberately not localized. Saves a fixed list of Exophase pages' icons.
            if (global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseIconProbe.Enabled)
            {
                yield return new MainMenuItem
                {
                    Description = "Probe borrowed icons (Exophase PlayStation)",
                    MenuSection = PluginMainMenuSection,
                    Action = (a) =>
                    {
                        var folder = System.IO.Path.Combine(GetPluginUserDataPath(), "diagnostics", "icon-probe");
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await global::PlayniteAchievements.Providers.Metadata.Exophase.ExophaseIconProbe.RunAsync(
                                    PlayniteApi,
                                    Settings,
                                    _refreshService?.IconService,
                                    _diskImageService,
                                    _logger,
                                    folder,
                                    CancellationToken.None).ConfigureAwait(false);
                                PlayniteApi.Notifications.Add(
                                    "PlayAch_IconProbe",
                                    $"Icon probe finished: {folder}",
                                    NotificationType.Info);
                            }
                            catch (Exception ex)
                            {
                                _logger?.Warn(ex, "Icon probe failed.");
                            }
                        });
                    }
                };
            }
        }
    }
}
