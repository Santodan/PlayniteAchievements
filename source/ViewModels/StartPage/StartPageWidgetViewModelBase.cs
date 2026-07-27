using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using PlayniteAchievements.Common;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Services.Overview;
using PlayniteAchievements.Services.StartPage;
using StartPage.SDK;

namespace PlayniteAchievements.ViewModels.StartPage
{
    public abstract class StartPageWidgetViewModelBase : ObservableObject, IStartPageControl, IDisposable
    {
        private readonly object _refreshLock = new object();
        private CancellationTokenSource _refreshCts;
        private bool _isLoading;
        private string _statusText;
        private bool _disposed;
        private PersistedSettings _subscribedPersistedSettings;

        protected StartPageWidgetViewModelBase(
            StartPageDataCoordinator dataCoordinator,
            PlayniteAchievementsSettings settings,
            ILogger logger)
        {
            DataCoordinator = dataCoordinator ?? throw new ArgumentNullException(nameof(dataCoordinator));
            Settings = settings ?? new PlayniteAchievementsSettings();
            Logger = logger;

            DataCoordinator.SnapshotInvalidated += DataCoordinator_SnapshotInvalidated;
            Settings.PropertyChanged += Settings_PropertyChanged;
            AttachPersistedSettings(Settings.Persisted);
        }

        protected StartPageDataCoordinator DataCoordinator { get; }

        protected PlayniteAchievementsSettings Settings { get; }

        protected ILogger Logger { get; }

        protected PersistedSettings PersistedSettings => Settings?.Persisted;

        public bool IsLoading
        {
            get => _isLoading;
            private set => SetValue(ref _isLoading, value);
        }

        public string StatusText
        {
            get => _statusText;
            private set => SetValue(ref _statusText, value);
        }

        public void OnStartPageOpened()
        {
            _ = RefreshAsync(forceRefresh: false);
        }

        public void OnStartPageClosed()
        {
        }

        public void OnDayChanged(DateTime newTime)
        {
            DataCoordinator.Invalidate();
            _ = RefreshAsync(forceRefresh: true);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DataCoordinator.SnapshotInvalidated -= DataCoordinator_SnapshotInvalidated;
            Settings.PropertyChanged -= Settings_PropertyChanged;
            AttachPersistedSettings(null);

            lock (_refreshLock)
            {
                _refreshCts?.Cancel();
                _refreshCts?.Dispose();
                _refreshCts = null;
            }
        }

        protected abstract void ApplySnapshot(OverviewDataSnapshot snapshot);

        protected virtual void OnPersistedSettingsChanged(string propertyName)
        {
        }

        protected virtual bool ShouldRefreshForPersistedSettingsChanged(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(PersistedSettings.StartPageAchievementColumnVisibility):
                case nameof(PersistedSettings.StartPageAchievementColumnWidths):
                case nameof(PersistedSettings.StartPageAchievementColumnOrder):
                case nameof(PersistedSettings.StartPageAchievementColumnAlignments):
                case nameof(PersistedSettings.StartPageAchievementColumnVerticalAlignments):
                case nameof(PersistedSettings.StartPageAchievementColumnHeaderAlignments):
                case nameof(PersistedSettings.StartPageGameSummariesColumnVisibility):
                case nameof(PersistedSettings.StartPageGameSummariesColumnWidths):
                case nameof(PersistedSettings.StartPageGameSummariesColumnOrder):
                case nameof(PersistedSettings.StartPageGameSummariesColumnAlignments):
                case nameof(PersistedSettings.StartPageGameSummariesColumnVerticalAlignments):
                case nameof(PersistedSettings.StartPageGameSummariesColumnHeaderAlignments):
                case nameof(PersistedSettings.ShowOverviewRecentAchievementsGridColumnHeaders):
                case nameof(PersistedSettings.ShowOverviewSelectedGameGridColumnHeaders):
                case nameof(PersistedSettings.StartPageGameSummariesGridRowHeight):
                case nameof(PersistedSettings.StartPageRecentAchievementsGridRowHeight):
                    return false;
                default:
                    return true;
            }
        }

        protected void RefreshFromSettings()
        {
            _ = RefreshAsync(forceRefresh: false);
        }

        private async Task RefreshAsync(bool forceRefresh)
        {
            if (_disposed)
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_refreshLock)
            {
                _refreshCts?.Cancel();
                _refreshCts = new CancellationTokenSource();
                cts = _refreshCts;
            }

            RunOnUiThread(() =>
            {
                IsLoading = true;
                StatusText = ResourceProvider.GetString("LOCPlayAch_Status_LoadingAchievements");
            });

            try
            {
                var snapshot = await DataCoordinator.GetSnapshotAsync(forceRefresh, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested || _disposed)
                {
                    return;
                }

                RunOnUiThread(() =>
                {
                    ApplySnapshot(snapshot);
                    StatusText = string.Empty;
                    IsLoading = false;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger?.Error(ex, "Failed to refresh StartPage achievement widget.");
                RunOnUiThread(() =>
                {
                    StatusText = ResourceProvider.GetString("LOCPlayAch_Error_RebuildFailed");
                    IsLoading = false;
                });
            }
        }

        private void DataCoordinator_SnapshotInvalidated(object sender, EventArgs e)
        {
            RefreshFromSettings();
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e?.PropertyName) ||
                e.PropertyName == nameof(PlayniteAchievementsSettings.Persisted))
            {
                AttachPersistedSettings(Settings.Persisted);
                OnPersistedSettingsChanged(null);
                RefreshFromSettings();
            }
        }

        private void PersistedSettings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            OnPersistedSettingsChanged(e?.PropertyName);
            if (ShouldRefreshForPersistedSettingsChanged(e?.PropertyName))
            {
                RefreshFromSettings();
            }
        }

        private void AttachPersistedSettings(PersistedSettings persistedSettings)
        {
            if (ReferenceEquals(_subscribedPersistedSettings, persistedSettings))
            {
                return;
            }

            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged -= PersistedSettings_PropertyChanged;
            }

            _subscribedPersistedSettings = persistedSettings;
            if (_subscribedPersistedSettings != null)
            {
                _subscribedPersistedSettings.PropertyChanged += PersistedSettings_PropertyChanged;
            }
        }

        private static void RunOnUiThread(Action action)
        {
            if (action == null)
            {
                return;
            }

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(action);
                return;
            }

            action();
        }
    }
}
