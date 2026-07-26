using Playnite.SDK;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;
using PlayniteAchievements.Providers;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;
using ObservableObject = PlayniteAchievements.Common.ObservableObject;

namespace PlayniteAchievements.ViewModels
{
    /// <summary>
    /// Tri-state cell value for one per-provider notification override: Inherit follows the
    /// corresponding global default, On/Off force the feature for that provider.
    /// </summary>
    internal enum OverrideState
    {
        Inherit,
        On,
        Off
    }

    internal static class OverrideStates
    {
        public static OverrideState FromNullable(bool? value) =>
            value == null ? OverrideState.Inherit : value.Value ? OverrideState.On : OverrideState.Off;

        public static bool? ToNullable(OverrideState state) =>
            state == OverrideState.Inherit ? (bool?)null : state == OverrideState.On;
    }

    /// <summary>
    /// Backs the per-provider notification override grid in the Notifications settings section.
    /// Rows are built once from the provider registry (same order as the Providers tab); a
    /// provider without a stored override shows all cells as Inherit, so new providers inherit
    /// the globals automatically. Row edits write the override store immediately and persist via
    /// a debounced timer, mirroring FriendsSettingsViewModel.
    /// </summary>
    internal sealed class ProviderNotificationSettingsViewModel : ObservableObject, IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PlayniteAchievementsPlugin _plugin;
        private readonly ILogger _logger;
        private readonly DispatcherTimer _persistDebounceTimer;
        private bool _hasPendingPersist;

        public ProviderNotificationSettingsViewModel(
            PlayniteAchievementsSettings settings,
            PlayniteAchievementsPlugin plugin,
            ProviderRegistry providerRegistry,
            ILogger logger)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _plugin = plugin;
            _logger = logger;

            _persistDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _persistDebounceTimer.Tick += OnPersistDebounceTimerTick;

            Rows = new ObservableCollection<ProviderNotificationRowItem>(
                (providerRegistry?.GetSettingsViewProviderKeys() ?? Enumerable.Empty<string>())
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .Select(key => new ProviderNotificationRowItem(
                    key,
                    _settings.Persisted?.GetProviderNotificationOverride(key),
                    OnRowChanged)));
        }

        public ObservableCollection<ProviderNotificationRowItem> Rows { get; }

        private void OnRowChanged(ProviderNotificationRowItem row)
        {
            if (row == null)
            {
                return;
            }

            _settings.Persisted?.SetProviderNotificationOverride(row.ProviderKey, row.BuildOverride());
            SchedulePersist();
        }

        private void SchedulePersist()
        {
            _hasPendingPersist = true;
            _persistDebounceTimer.Stop();
            _persistDebounceTimer.Start();
        }

        private void OnPersistDebounceTimerTick(object sender, EventArgs e)
        {
            FlushPendingPersist();
        }

        /// <summary>
        /// Persists a pending debounced change immediately. Called from the debounce timer tick
        /// and from Dispose so closing the settings view never drops an edit.
        /// </summary>
        public void FlushPendingPersist()
        {
            _persistDebounceTimer.Stop();
            if (!_hasPendingPersist)
            {
                return;
            }

            _hasPendingPersist = false;
            try
            {
                _plugin?.PersistSettingsForUi();
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to persist provider notification overrides.");
            }
        }

        public void Dispose()
        {
            FlushPendingPersist();
            _persistDebounceTimer.Tick -= OnPersistDebounceTimerTick;
        }
    }

    internal sealed class ProviderNotificationRowItem : ObservableObject
    {
        private readonly Action<ProviderNotificationRowItem> _onChanged;
        private OverrideState _unlockToasts;
        private OverrideState _friendUnlockToasts;
        private OverrideState _screenshotClean;
        private OverrideState _screenshotWithToast;
        private OverrideState _screenshotFramed;
        private OverrideState _recordings;

        public ProviderNotificationRowItem(
            string providerKey,
            ProviderNotificationOverride stored,
            Action<ProviderNotificationRowItem> onChanged)
        {
            ProviderKey = providerKey;
            DisplayName = ProviderRegistry.GetLocalizedName(providerKey);
            ProviderRegistry.TryResolveProviderVisuals(providerKey, out var iconKey, out _);
            ProviderIconKey = iconKey;
            ProviderColorHex = ProviderRegistry.GetProviderColorHex(providerKey);

            _unlockToasts = OverrideStates.FromNullable(stored?.UnlockToasts);
            _friendUnlockToasts = OverrideStates.FromNullable(stored?.FriendUnlockToasts);
            _screenshotClean = OverrideStates.FromNullable(stored?.ScreenshotClean);
            _screenshotWithToast = OverrideStates.FromNullable(stored?.ScreenshotWithToast);
            _screenshotFramed = OverrideStates.FromNullable(stored?.ScreenshotFramed);
            _recordings = OverrideStates.FromNullable(stored?.Recordings);

            // Assigned last so construction never fires the change/persist callback.
            _onChanged = onChanged;
        }

        public string ProviderKey { get; }

        public string DisplayName { get; }

        public string ProviderIconKey { get; }

        public string ProviderColorHex { get; }

        public bool HasProviderIcon => !string.IsNullOrWhiteSpace(ProviderIconKey);

        public OverrideState UnlockToasts
        {
            get => _unlockToasts;
            set => SetStateValue(ref _unlockToasts, value);
        }

        public OverrideState FriendUnlockToasts
        {
            get => _friendUnlockToasts;
            set => SetStateValue(ref _friendUnlockToasts, value);
        }

        public OverrideState ScreenshotClean
        {
            get => _screenshotClean;
            set => SetStateValue(ref _screenshotClean, value, isScreenshotState: true);
        }

        public OverrideState ScreenshotWithToast
        {
            get => _screenshotWithToast;
            set => SetStateValue(ref _screenshotWithToast, value, isScreenshotState: true);
        }

        public OverrideState ScreenshotFramed
        {
            get => _screenshotFramed;
            set => SetStateValue(ref _screenshotFramed, value, isScreenshotState: true);
        }

        public OverrideState Recordings
        {
            get => _recordings;
            set => SetStateValue(ref _recordings, value);
        }

        /// <summary>
        /// Label for the Screenshots popup toggle: "Default" while all three variant cells
        /// inherit, "Custom" once any deviates.
        /// </summary>
        public string ScreenshotsSummaryText =>
            _screenshotClean == OverrideState.Inherit &&
            _screenshotWithToast == OverrideState.Inherit &&
            _screenshotFramed == OverrideState.Inherit
                ? ResourceProvider.GetString("LOCPlayAch_Common_Default")
                : ResourceProvider.GetString("LOCPlayAch_Common_Custom");

        /// <summary>
        /// The row's current cell states as an override entry; all-inherit rows produce an entry
        /// the settings store prunes on set.
        /// </summary>
        public ProviderNotificationOverride BuildOverride()
        {
            return new ProviderNotificationOverride
            {
                UnlockToasts = OverrideStates.ToNullable(_unlockToasts),
                FriendUnlockToasts = OverrideStates.ToNullable(_friendUnlockToasts),
                ScreenshotClean = OverrideStates.ToNullable(_screenshotClean),
                ScreenshotWithToast = OverrideStates.ToNullable(_screenshotWithToast),
                ScreenshotFramed = OverrideStates.ToNullable(_screenshotFramed),
                Recordings = OverrideStates.ToNullable(_recordings)
            };
        }

        private void SetStateValue(
            ref OverrideState field,
            OverrideState value,
            bool isScreenshotState = false,
            [System.Runtime.CompilerServices.CallerMemberName] string propertyName = null)
        {
            if (!SetValueAndReturn(ref field, value, propertyName))
            {
                return;
            }

            if (isScreenshotState)
            {
                OnPropertyChanged(nameof(ScreenshotsSummaryText));
            }

            _onChanged?.Invoke(this);
        }
    }
}
