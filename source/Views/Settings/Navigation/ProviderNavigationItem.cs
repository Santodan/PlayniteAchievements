using System;
using System.ComponentModel;
using System.Windows.Controls;
using PlayniteAchievements.Providers.Settings;

namespace PlayniteAchievements.Views.Settings.Navigation
{
    /// <summary>
    /// Represents a provider item in the settings Providers overview navigation.
    /// Tracks the provider's IsEnabled state and initializes its settings view on creation.
    /// </summary>
    public sealed class ProviderNavigationItem : SettingsNavigationItem
    {
        private readonly IProviderSettings _settings;

        public ProviderNavigationItem(
            string providerKey,
            string displayName,
            string groupName,
            string providerIconKey,
            string providerColorHex,
            IProviderSettings settings,
            Func<ProviderSettingsViewBase> settingsViewFactory,
            string redirectProviderKey = null,
            string subtitle = null)
            : base(
                providerKey,
                displayName,
                groupName,
                CreateViewFactory(settingsViewFactory, settings),
                redirectProviderKey,
                subtitle,
                providerIconKey: providerIconKey,
                providerColorHex: providerColorHex)
        {
            _settings = settings;

            if (_settings != null)
            {
                _settings.PropertyChanged += Settings_PropertyChanged;
            }
        }

        public string ProviderKey => Key;
        public override bool IsEnabled => _settings?.IsEnabled ?? true;

        private static Func<UserControl> CreateViewFactory(
            Func<ProviderSettingsViewBase> settingsViewFactory,
            IProviderSettings settings)
        {
            if (settingsViewFactory == null)
            {
                return null;
            }

            return () =>
            {
                var view = settingsViewFactory();
                view?.Initialize(settings);
                return view;
            };
        }

        private void Settings_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(IProviderSettings.IsEnabled), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }
}
