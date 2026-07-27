using System;
using System.ComponentModel;
using PlayniteAchievements.Models;
using PlayniteAchievements.Models.Settings;

namespace PlayniteAchievements.Views.Settings
{
    /// <summary>
    /// Tracks the current <see cref="PersistedSettings"/> instance on a settings object.
    /// Resubscribes the per-property handler when the whole Persisted instance is swapped
    /// (e.g. by CancelEdit) and invokes a callback so owners can refresh derived state.
    /// </summary>
    internal sealed class PersistedSettingsSubscription : IDisposable
    {
        private readonly PlayniteAchievementsSettings _settings;
        private readonly PropertyChangedEventHandler _onPersistedPropertyChanged;
        private readonly Action _onPersistedInstanceChanged;
        private PersistedSettings _subscribed;

        public PersistedSettingsSubscription(
            PlayniteAchievementsSettings settings,
            PropertyChangedEventHandler onPersistedPropertyChanged,
            Action onPersistedInstanceChanged = null)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _onPersistedPropertyChanged = onPersistedPropertyChanged;
            _onPersistedInstanceChanged = onPersistedInstanceChanged;

            _settings.PropertyChanged += OnSettingsObjectPropertyChanged;
            Subscribe(_settings.Persisted);
        }

        private void OnSettingsObjectPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!string.Equals(e.PropertyName, nameof(PlayniteAchievementsSettings.Persisted), StringComparison.Ordinal))
            {
                return;
            }

            Subscribe(_settings.Persisted);
            _onPersistedInstanceChanged?.Invoke();
        }

        private void Subscribe(PersistedSettings persisted)
        {
            if (ReferenceEquals(_subscribed, persisted))
            {
                return;
            }

            if (_subscribed != null && _onPersistedPropertyChanged != null)
            {
                _subscribed.PropertyChanged -= _onPersistedPropertyChanged;
            }

            _subscribed = persisted;

            if (_subscribed != null && _onPersistedPropertyChanged != null)
            {
                _subscribed.PropertyChanged += _onPersistedPropertyChanged;
            }
        }

        public void Dispose()
        {
            _settings.PropertyChanged -= OnSettingsObjectPropertyChanged;
            Subscribe(null);
        }
    }
}
