using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using PlayniteAchievements.Models;
using PlayniteAchievements.Providers.Local;
using PlayniteAchievements.Services.Refresh;
using Playnite.SDK;
using Playnite.SDK.Models;

namespace PlayniteAchievements
{
    public partial class PlayniteAchievementsPlugin
    {
        private void DownloadExpectedAchievementsJson(Game game)
        {
            var localProvider = Providers?.OfType<LocalSavesProvider>().FirstOrDefault();
            if (game == null || localProvider == null)
            {
                ShowLocalMenuFailure(
                    "LOCPlayAch_Menu_LocalExpectedJson_ProviderUnavailable",
                    MessageBoxImage.Error);
                return;
            }

            if (localProvider.TryResolveAchievementsJsonPath(game, out var jsonPath, out _, out _) &&
                File.Exists(jsonPath))
            {
                var overwrite = PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_OverwriteConfirm"),
                        game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) ?? MessageBoxResult.None;
                if (overwrite != MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var options = new GlobalProgressOptions(
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
            }, options);

            if (failure != null)
            {
                _logger?.Error(failure, $"Failed to generate expected achievements.json for '{game.Name}'.");
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_Failed"),
                        failure.Message),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            PlayniteApi?.Dialogs?.ShowMessage(
                result?.Message ??
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalExpectedJson_UnknownError"),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                result?.Success == true ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void SetLocalSteamAppIdOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            LocalSavesProvider.TryResolveAppId(game, out var currentAppId, out var isOverridden);
            var input = PlayniteApi?.Dialogs?.SelectString(
                currentAppId > 0
                    ? string.Format(
                        ResourceProvider.GetString(isOverridden
                            ? "LOCPlayAch_Menu_LocalAppId_DialogHintOverride"
                            : "LOCPlayAch_Menu_LocalAppId_DialogHintDetected"),
                        currentAppId)
                    : ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_DialogHint"),
                ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_DialogTitle"),
                currentAppId > 0 ? currentAppId.ToString() : string.Empty);
            if (input == null || !input.Result)
            {
                return;
            }

            if (!int.TryParse(input.SelectedString?.Trim(), out var appId) || appId <= 0)
            {
                ShowLocalMenuFailure("LOCPlayAch_Menu_LocalAppId_InvalidId");
                return;
            }

            if (!LocalSavesProvider.TrySetAppIdOverride(
                    game.Id,
                    appId,
                    game.Name,
                    PersistSettingsForUi,
                    _logger))
            {
                ShowLocalMenuFailure("LOCPlayAch_Menu_LocalAppId_SetFailed", MessageBoxImage.Error);
                return;
            }

            RefreshGameAfterLocalOverride(game);
        }

        private void ClearLocalSteamAppIdOverride(Game game)
        {
            if (game == null ||
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalAppId_ClearConfirm"),
                        game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!LocalSavesProvider.TryClearAppIdOverride(
                    game.Id,
                    game.Name,
                    PersistSettingsForUi,
                    _logger))
            {
                ShowLocalMenuFailure("LOCPlayAch_Menu_LocalAppId_ClearFailed");
                return;
            }

            RefreshGameAfterLocalOverride(game);
        }

        private void ChangeLocalSteamUserOverride(Game game, string userId)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var normalized = userId?.Trim() ?? string.Empty;
            var success = string.IsNullOrWhiteSpace(normalized)
                ? LocalSavesProvider.TryClearSteamAppCacheUserOverride(
                    game.Id,
                    game.Name,
                    PersistSettingsForUi,
                    _logger)
                : LocalSavesProvider.TrySetSteamAppCacheUserOverride(
                    game.Id,
                    normalized,
                    game.Name,
                    PersistSettingsForUi,
                    _logger);
            if (!success)
            {
                ShowLocalMenuFailure(string.IsNullOrWhiteSpace(normalized)
                    ? "LOCPlayAch_Menu_LocalSteamUser_ClearFailed"
                    : "LOCPlayAch_Menu_LocalSteamUser_SetFailed");
                return;
            }

            if (!string.IsNullOrWhiteSpace(normalized))
            {
                _achievementOverridesService?.SetPreferredProviderOverride(game.Id, "Local");
            }

            RefreshGameAfterLocalOverride(game);
        }

        private void SetLocalFolderOverride(Game game)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var folder = PlayniteApi?.Dialogs?.SelectFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!Directory.Exists(folder))
            {
                ShowLocalMenuFailure("LOCPlayAch_GameOptions_LocalFolder_NotFound");
                return;
            }

            if (!LocalSavesProvider.TrySetFolderOverride(
                    game.Id,
                    folder,
                    game.Name,
                    PersistSettingsForUi,
                    _logger))
            {
                ShowLocalMenuFailure("LOCPlayAch_Menu_LocalFolder_SetFailed", MessageBoxImage.Error);
                return;
            }

            RefreshGameAfterLocalOverride(game);
        }

        private void ClearLocalFolderOverride(Game game)
        {
            if (game == null ||
                PlayniteApi?.Dialogs?.ShowMessage(
                    string.Format(
                        ResourceProvider.GetString("LOCPlayAch_Menu_LocalFolder_ClearConfirm"),
                        game.Name),
                    ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            if (!LocalSavesProvider.TryClearFolderOverride(
                    game.Id,
                    game.Name,
                    PersistSettingsForUi,
                    _logger))
            {
                ShowLocalMenuFailure("LOCPlayAch_Menu_LocalFolder_ClearFailed");
                return;
            }

            RefreshGameAfterLocalOverride(game);
        }

        private void ChangePreferredProvider(Game game, string providerKey)
        {
            if (game == null || game.Id == Guid.Empty)
            {
                return;
            }

            var result = string.IsNullOrWhiteSpace(providerKey)
                ? _achievementOverridesService?.ClearPreferredProviderOverride(game.Id)
                : _achievementOverridesService?.SetPreferredProviderOverride(game.Id, providerKey);
            if (result?.Success != true)
            {
                ShowLocalMenuFailure(string.IsNullOrWhiteSpace(providerKey)
                    ? "LOCPlayAch_Menu_LocalProvider_ClearFailed"
                    : "LOCPlayAch_Menu_LocalProvider_SetFailed");
                return;
            }

            RefreshGameAfterLocalOverride(game);
        }

        private List<string> GetSelectableProviderKeys()
        {
            return Providers?
                .Where(provider => provider != null && !string.IsNullOrWhiteSpace(provider.ProviderKey))
                .Select(provider => provider.ProviderKey.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(
                    PlayniteAchievements.Providers.ProviderRegistry.GetLocalizedName,
                    StringComparer.CurrentCultureIgnoreCase)
                .ToList() ?? new List<string>();
        }

        private static string FormatLocalProviderMenuLabel(string label, bool isCurrent)
        {
            return isCurrent
                ? string.Format(
                    ResourceProvider.GetString("LOCPlayAch_Menu_LocalProvider_Current"),
                    label)
                : label;
        }

        private void RefreshGameAfterLocalOverride(Game game)
        {
            _achievementOverridesService?.ClearGameData(game.Id, game.Name);
            _ = StartMenuRefreshAsync(
                new RefreshRequest
                {
                    Mode = RefreshModeType.Single,
                    SingleGameId = game.Id
                },
                game.Id);
        }

        private void ShowLocalMenuFailure(
            string resourceKey,
            MessageBoxImage image = MessageBoxImage.Warning)
        {
            PlayniteApi?.Dialogs?.ShowMessage(
                ResourceProvider.GetString(resourceKey),
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                image);
        }
    }
}
