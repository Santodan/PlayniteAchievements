using System;
using System.Collections.Generic;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Services.Images;
using PlayniteAchievements.Services.Images.Relocation;

namespace PlayniteAchievements
{
    /// <summary>
    /// The icon cache folder: where it is at startup, moving it, and pointing every surface at the
    /// new folder once it moved.
    /// </summary>
    public partial class PlayniteAchievementsPlugin
    {
        private const string IconCacheMissingNotificationId = "PlayAch_IconCacheFolderMissing";

        private IconCacheLocation _iconCacheLocation;
        private IconCacheRelocationService _iconCacheRelocation;
        private IconCacheRelocationJournal _pendingIconCacheJournal;

        internal IconCacheLocation IconCacheFolder => _iconCacheLocation;

        internal IconCacheRelocationService IconCacheRelocation => _iconCacheRelocation;

        // Before anything resolves a cached path: the folder comes from the saved setting, or from
        // a move the last session left unfinished.
        private void InitializeIconCacheLocation(string pluginUserDataPath)
        {
            _iconCacheLocation = IconCacheRelocationService.CreateStartupLocation(
                pluginUserDataPath,
                _settingsViewModel?.Settings?.Persisted?.IconCacheDirectory,
                out _pendingIconCacheJournal);
            IconCacheLocation.SetCurrent(_iconCacheLocation);
            _logger?.Info($"Icon cache folder: {_iconCacheLocation.Root}");
        }

        private void InitializeIconCacheRelocation()
        {
            _iconCacheRelocation = new IconCacheRelocationService(
                _iconCacheLocation,
                _diskImageService.WriteGate,
                new IconCacheRelocationHost(this),
                _logger);
            _iconCacheLocation.RootChanged += IconCacheLocation_RootChanged;
            SettingsSaved += OnSettingsSavedForIconCacheJournal;
        }

        // Finishes a move the last session started, then reports a custom folder that is missing.
        private void ResumeIconCacheMoveOnStartup()
        {
            var journal = _pendingIconCacheJournal;
            _pendingIconCacheJournal = null;
            if (journal != null)
            {
                var result = journal.Phase == IconCacheRelocationPhase.Moving
                    ? RunWithProgress(args => _iconCacheRelocation.Resume(journal, ProgressReporter(args)), cancelable: false)
                    : _iconCacheRelocation.Resume(journal, null);
                if (result?.Outcome == IconCacheMoveOutcome.Failed)
                {
                    ShowIconCacheMoveError(result);
                }
            }

            if (!_iconCacheLocation.IsAvailable)
            {
                PlayniteApi.Notifications.Add(new NotificationMessage(
                    IconCacheMissingNotificationId,
                    string.Format(ResourceProvider.GetString("LOCPlayAch_Settings_IconCache_FolderMissing"), _iconCacheLocation.Root),
                    NotificationType.Error));
            }
        }

        /// <summary>
        /// Moves the icon cache to <paramref name="target"/> under a progress dialog, telling the
        /// user when it cannot. Returns true when the cache now lives there.
        /// </summary>
        internal bool MoveIconCacheInteractive(string target)
        {
            var plan = _iconCacheRelocation.Plan(target);
            if (plan.Problem == IconCacheMoveProblem.SameFolder)
            {
                return true;
            }

            if (!plan.IsValid)
            {
                ShowIconCacheMoveError(new IconCacheMoveResult { Outcome = IconCacheMoveOutcome.Invalid, Problem = plan.Problem });
                return false;
            }

            var result = RunWithProgress(
                args => _iconCacheRelocation
                    .MoveAsync(plan, ProgressReporter(args), args.CancelToken)
                    .GetAwaiter()
                    .GetResult(),
                cancelable: true);
            if (result?.Outcome == IconCacheMoveOutcome.Moved)
            {
                PlayniteApi.Notifications.Remove(IconCacheMissingNotificationId);
                return true;
            }

            if (result != null && result.Outcome != IconCacheMoveOutcome.Cancelled)
            {
                ShowIconCacheMoveError(result);
            }

            return false;
        }

        private IconCacheMoveResult RunWithProgress(Func<GlobalProgressActionArgs, IconCacheMoveResult> move, bool cancelable)
        {
            IconCacheMoveResult result = null;
            PlayniteApi.Dialogs.ActivateGlobalProgress(
                args => result = move(args),
                new GlobalProgressOptions(ResourceProvider.GetString(IconCacheRelocationService.MovingTextKey), cancelable)
                {
                    IsIndeterminate = false
                });
            return result;
        }

        private static Action<string, int, int> ProgressReporter(GlobalProgressActionArgs args)
        {
            return (text, done, total) =>
            {
                args.Text = text;
                args.ProgressMaxValue = Math.Max(total, 1);
                args.CurrentProgressValue = done;
            };
        }

        private void ShowIconCacheMoveError(IconCacheMoveResult result)
        {
            string message;
            switch (result.Outcome)
            {
                case IconCacheMoveOutcome.RefreshRunning:
                case IconCacheMoveOutcome.AlreadyMoving:
                    message = ResourceProvider.GetString("LOCPlayAch_Menu_RefreshInProgress");
                    break;
                case IconCacheMoveOutcome.Failed:
                    message = string.Format(ResourceProvider.GetString("LOCPlayAch_Status_Failed"), result.Error?.Message);
                    break;
                default:
                    message = result.Problem == IconCacheMoveProblem.TargetUnreachable
                        ? ResourceProvider.GetString("LOCPlayAch_InvalidPath")
                        : ResourceProvider.GetString("LOCPlayAch_Settings_IconCache_MoveError");
                    break;
            }

            PlayniteApi.Dialogs.ShowMessage(
                message,
                ResourceProvider.GetString("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        // Every image, game and friend held in memory names files under the old folder.
        private void IconCacheLocation_RootChanged(object sender, IconCacheRootChangedEventArgs e)
        {
            void Reload()
            {
                try
                {
                    _imageService?.Clear();
                    _metadataState?.AttachCustomData(_gameCustomDataStore);
                    (_cacheManager as Services.Cache.CacheManager)?.ResetMemoryForIconCacheMove();
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to reload after the icon cache moved.");
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                Reload();
            }
            else
            {
                dispatcher.Invoke(Reload);
            }
        }

        private void OnSettingsSavedForIconCacheJournal(object sender, EventArgs e)
        {
            _iconCacheRelocation?.CompleteCommittedJournal(_settingsViewModel?.Settings?.Persisted?.IconCacheDirectory);
        }

        private bool CommitIconCacheStoredPaths(IReadOnlyList<string> oldRoots, string newRoot, string settingValue)
        {
            foreach (var oldRoot in oldRoots)
            {
                _gameCustomDataStore?.RewriteIconCacheRoot(oldRoot, newRoot);
            }

            var saved = false;
            void UpdateSettings()
            {
                _settingsViewModel?.UpdatePersistedIncludingEditSnapshot(settings =>
                {
                    foreach (var friend in settings.Friends)
                    {
                        foreach (var oldRoot in oldRoots)
                        {
                            friend.AvatarPath = IconCachePathRebaser.RebasePath(friend.AvatarPath, oldRoot, newRoot);
                        }
                    }

                    settings.IconCacheDirectory = settingValue;
                });

                // An open settings window saves on OK; until then the journal keeps the folder.
                if (_settingsViewModel?.IsEditSessionActive != true)
                {
                    PersistSettingsForUi();
                    saved = true;
                }
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                UpdateSettings();
            }
            else
            {
                dispatcher.Invoke(UpdateSettings);
            }

            return saved;
        }

        private sealed class IconCacheRelocationHost : IIconCacheRelocationHost
        {
            private readonly PlayniteAchievementsPlugin _plugin;

            public IconCacheRelocationHost(PlayniteAchievementsPlugin plugin)
            {
                _plugin = plugin;
            }

            public bool TryReserveRefreshes()
            {
                return _plugin._refreshService?.TryReserveExclusive() ?? true;
            }

            public void ReleaseRefreshes()
            {
                _plugin._refreshService?.ReleaseExclusive();
            }

            public bool CommitStoredPaths(IReadOnlyList<string> oldRoots, string newRoot, string settingValue)
            {
                return _plugin.CommitIconCacheStoredPaths(oldRoots, newRoot, settingValue);
            }
        }
    }
}
