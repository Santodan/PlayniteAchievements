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
        private readonly Func<string> _iconKeyResolver;
        private readonly Func<string> _iconColorResolver;

        public ProviderNavigationItem(
            string providerKey,
            string displayName,
            string groupName,
            string providerIconKey,
            string providerColorHex,
            IProviderSettings settings,
            Func<ProviderSettingsViewBase> settingsViewFactory,
            string redirectProviderKey = null,
            string subtitle = null,
            Func<string> iconKeyResolver = null,
            Func<string> iconColorResolver = null)
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
            _iconKeyResolver = iconKeyResolver;
            _iconColorResolver = iconColorResolver;

            if (_settings != null)
            {
                _settings.PropertyChanged += Settings_PropertyChanged;
            }
        }

        public string ProviderKey => Key;
        public override string ProviderIconKey => _iconKeyResolver?.Invoke() ?? base.ProviderIconKey;
        public override string ProviderColorHex => _iconColorResolver?.Invoke() ?? base.ProviderColorHex;
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
            if (_iconKeyResolver != null && (string.IsNullOrEmpty(e.PropertyName) ||
                e.PropertyName == nameof(Providers.Local.LocalSettings.NativeSourceIcons)))
            {
                OnPropertyChanged(nameof(ProviderIconKey));
                OnPropertyChanged(nameof(ProviderColorHex));
                OnPropertyChanged(nameof(HasProviderIcon));
            }
            if (string.IsNullOrEmpty(e.PropertyName) ||
                string.Equals(e.PropertyName, nameof(IProviderSettings.IsEnabled), StringComparison.Ordinal))
            {
                OnPropertyChanged(nameof(IsEnabled));
            }
        }
    }
}
