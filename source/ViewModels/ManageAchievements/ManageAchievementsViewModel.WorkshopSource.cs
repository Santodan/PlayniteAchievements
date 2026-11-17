using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Library;
using PlayniteAchievements.Services.Workshop;
using AsyncCommand = PlayniteAchievements.Common.AsyncCommand;
using RelayCommand = PlayniteAchievements.Common.RelayCommand;

namespace PlayniteAchievements.ViewModels.ManageAchievements
{
    /// <summary>
    /// The Workshop source of this game's custom data, on the Overview's custom data card: the
    /// item applied, its version, whether the data was edited since, and whether the Workshop
    /// has a newer version; with Update, Reset and Unlink.
    /// </summary>
    /// <remarks>
    /// "Update available" reads the index the plugin last fetched (the hourly update check,
    /// Browse or the Library page), so opening this window never fetches it. Whether the data was
    /// edited reads the baseline and the icon folder, off the UI thread.
    /// </remarks>
    public sealed partial class ManageAchievementsViewModel
    {
        private LibraryLink _workshopSource;
        private WorkshopItem _workshopSourceIndexItem;
        private bool _workshopSourceIsEdited;
        private bool _isWorkshopSourceBusy;
        private int _workshopSourceGeneration;
        private GameLinkStore _workshopSourceLinks;

        public AsyncCommand UpdateWorkshopDataCommand { get; private set; }
        public AsyncCommand ResetWorkshopDataCommand { get; private set; }
        public RelayCommand UnlinkWorkshopDataCommand { get; private set; }

        public bool HasWorkshopSource => _workshopSource != null;

        public string WorkshopSourceName => _workshopSource == null
            ? null
            : !string.IsNullOrWhiteSpace(_workshopSource.Name)
                ? _workshopSource.Name
                : _workshopSourceIndexItem?.Name ?? GameDataLinkService.WorkshopItemIdOf(_workshopSource);

        public string WorkshopSourceVersionText => string.IsNullOrWhiteSpace(_workshopSource?.AppliedVersion)
            ? null
            : "v" + _workshopSource.AppliedVersion;

        public bool HasWorkshopSourceVersion => WorkshopSourceVersionText != null;

        public bool WorkshopSourceIsEdited
        {
            get => _workshopSourceIsEdited;
            private set => SetValue(ref _workshopSourceIsEdited, value);
        }

        public bool WorkshopSourceHasUpdate => GameDataLinkService.HasUpdate(_workshopSource, _workshopSourceIndexItem);

        public bool IsWorkshopSourceBusy
        {
            get => _isWorkshopSourceBusy;
            private set
            {
                if (SetValueAndReturn(ref _isWorkshopSourceBusy, value))
                {
                    RaiseWorkshopSourceCommandStates();
                }
            }
        }

        private void InitializeWorkshopSource()
        {
            UpdateWorkshopDataCommand = new AsyncCommand(
                _ => UpdateWorkshopDataAsync(),
                _ => HasGame && WorkshopSourceHasUpdate && !IsWorkshopSourceBusy);
            ResetWorkshopDataCommand = new AsyncCommand(
                _ => ResetWorkshopDataAsync(),
                _ => HasGame && HasWorkshopSource && !IsWorkshopSourceBusy);
            UnlinkWorkshopDataCommand = new RelayCommand(
                _ => UnlinkWorkshopData(),
                _ => HasGame && HasWorkshopSource && !IsWorkshopSourceBusy);

            _workshopSourceLinks = _plugin?.GameLinkStore;
            if (_workshopSourceLinks != null)
            {
                _workshopSourceLinks.Changed += WorkshopSourceLinks_Changed;
            }
        }

        /// <summary>Stops following the per-game links; called when the window closes.</summary>
        internal void DetachWorkshopSource()
        {
            if (_workshopSourceLinks != null)
            {
                _workshopSourceLinks.Changed -= WorkshopSourceLinks_Changed;
                _workshopSourceLinks = null;
            }
        }

        /// <summary>An install from Browse or an unlink elsewhere changed a record: re-read this game's on the UI thread.</summary>
        private void WorkshopSourceLinks_Changed(object sender, EventArgs e)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                return;
            }

            dispatcher.BeginInvoke(new Action(RefreshWorkshopSource), DispatcherPriority.Background);
        }

        /// <summary>
        /// Re-reads the game's record and the last fetched index entry of its item, then works out
        /// off the UI thread whether the data was edited; a newer refresh discards an older result.
        /// </summary>
        private void RefreshWorkshopSource()
        {
            var service = _plugin?.GameDataLinks;
            LibraryLink link = null;
            try
            {
                link = HasGame ? service?.Get(_gameId) : null;
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed reading the Workshop record of gameId={_gameId}.");
            }

            _workshopSource = link;
            _workshopSourceIndexItem = link == null ? null : GameDataLinkService.IndexItemOf(_plugin?.WorkshopClient?.LastIndex, link);
            OnPropertyChanged(nameof(HasWorkshopSource));
            OnPropertyChanged(nameof(WorkshopSourceName));
            OnPropertyChanged(nameof(WorkshopSourceVersionText));
            OnPropertyChanged(nameof(HasWorkshopSourceVersion));
            OnPropertyChanged(nameof(WorkshopSourceHasUpdate));
            RaiseWorkshopSourceCommandStates();

            var generation = ++_workshopSourceGeneration;
            if (link == null)
            {
                WorkshopSourceIsEdited = false;
                return;
            }

            _ = ReadWorkshopSourceEditedAsync(service, link, generation);
        }

        private async Task ReadWorkshopSourceEditedAsync(GameDataLinkService service, LibraryLink link, int generation)
        {
            var gameId = _gameId;
            var edited = false;
            try
            {
                edited = await Task.Run(() => service.IsEdited(gameId, link));
            }
            catch (Exception ex)
            {
                _logger?.Debug(ex, $"Failed telling whether the Workshop data of gameId={gameId} was edited.");
            }

            if (generation == _workshopSourceGeneration)
            {
                WorkshopSourceIsEdited = edited;
            }
        }

        private void RaiseWorkshopSourceCommandStates()
        {
            UpdateWorkshopDataCommand?.RaiseCanExecuteChanged();
            ResetWorkshopDataCommand?.RaiseCanExecuteChanged();
            UnlinkWorkshopDataCommand?.RaiseCanExecuteChanged();
        }

        /// <summary>
        /// Ends the game's Workshop record, deleting its package copy and baseline; the game keeps
        /// its data. For actions that replace or clear that data from somewhere else.
        /// </summary>
        private void RemoveWorkshopSource()
        {
            try
            {
                if (_plugin?.GameDataLinks?.Unlink(_gameId) == true)
                {
                    RefreshWorkshopSource();
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, $"Failed ending the Workshop record of gameId={_gameId}.");
            }
        }

        private void UnlinkWorkshopData()
        {
            if (!HasWorkshopSource)
            {
                return;
            }

            RemoveWorkshopSource();
        }

        /// <summary>Downloads the newer version and merges it in, keeping the edits made since the last apply.</summary>
        private async Task UpdateWorkshopDataAsync()
        {
            var item = _workshopSourceIndexItem;
            if (!WorkshopSourceHasUpdate || item == null)
            {
                return;
            }

            var gameId = _gameId;
            await ApplyWorkshopPackageAsync(applier => applier.ApplyAsync(gameId, item, WorkshopGameDataInstallMode.KeepEditsSinceInstall, CancellationToken.None));
        }

        /// <summary>
        /// After a Playnite confirmation, applies the kept package copy again as published (see
        /// <see cref="GameDataPackageApplier.ResetAsync"/>).
        /// </summary>
        private async Task ResetWorkshopDataAsync()
        {
            var link = _workshopSource;
            if (link == null)
            {
                return;
            }

            var confirmed = _playniteApi?.Dialogs?.ShowMessage(
                string.Format(L("LOCPlayAch_Workshop_ResetConfirmGameData"), WorkshopSourceName, GameName),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                return;
            }

            var gameId = _gameId;
            var name = WorkshopSourceName;
            await ApplyWorkshopPackageAsync(applier => applier.ResetAsync(gameId, link, name, CancellationToken.None));
        }

        /// <summary>
        /// Runs an apply onto this game through the shared game data applier, then refreshes the
        /// window and shows any warnings.
        /// </summary>
        private async Task ApplyWorkshopPackageAsync(Func<GameDataPackageApplier, Task<WorkshopInstallResult>> apply)
        {
            if (_plugin == null || IsWorkshopSourceBusy)
            {
                return;
            }

            IsWorkshopSourceBusy = true;
            try
            {
                var applier = new GameDataPackageApplier(_plugin.WorkshopClient, _plugin.WorkshopInstaller, _plugin.GameDataLinks);
                var result = await apply(applier);

                NotifyCustomDataChanged(requiresRefresh: false);
                if (result.Warnings.Count > 0)
                {
                    _playniteApi?.Dialogs?.ShowMessage(
                        string.Join("\n", result.Warnings),
                        L("LOCPlayAch_Title_PluginName"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                PromptAttachLinkedFileIfMappingOnly();
            }
            catch (Exception ex)
            {
                ShowWorkshopSourceFailure(ex);
            }
            finally
            {
                IsWorkshopSourceBusy = false;
                RefreshWorkshopSource();
            }
        }

        private void ShowWorkshopSourceFailure(Exception ex)
        {
            _logger?.Error(ex, $"Failed applying Workshop game data to gameId={_gameId}.");
            _playniteApi?.Dialogs?.ShowMessage(
                string.Format(L("LOCPlayAch_Status_Failed"), ex.Message),
                L("LOCPlayAch_Title_PluginName"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
